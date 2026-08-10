import Foundation
import Network

protocol ControlChannelDelegate: AnyObject {
    func controlChannel(
        _ channel: ControlChannel,
        didEstablishSession sessionId: UInt32,
        width: UInt16,
        height: UInt16,
        fps: UInt8,
        codec: ChosenCodec
    )
    func controlChannelDidDisconnect(_ channel: ControlChannel, reason: String)
}

/// Handshake / heartbeat state machine for the Control channel (iOS -> PC,
/// port 9000 by default). Owns the UDP connection used both to send
/// Handshake/Heartbeat/Disconnect and to receive HandshakeAck/Heartbeat
/// back from the PC.
final class ControlChannel {
    weak var delegate: ControlChannelDelegate?

    private let queue = DispatchQueue(label: "com.xrealbridge.controlchannel")
    private var connection: UDPConnection?

    private let host: String
    private let port: UInt16

    private var sessionId: UInt32?
    private var sessionNonce: [UInt8] = []
    private var handshakeAttempt: Int = 0
    private var heartbeatTimer: DispatchSourceTimer?
    private var watchdogTimer: DispatchSourceTimer?
    private var lastReceivedAt: Date = .distantPast
    private var handshakeRetryWorkItem: DispatchWorkItem?

    static let defaultWidth: UInt16 = 1920
    static let defaultHeight: UInt16 = 1080
    static let defaultFps: UInt8 = 60

    /// Per PROTOCOL.md: "the client retries with backoff and eventually
    /// falls back to a default 1920x1080@60 H264 request." After this many
    /// failed attempts at the caller's requested resolution, we switch to
    /// the default and keep retrying.
    private static let attemptsBeforeFallback = 4
    private static let maxBackoffSeconds: TimeInterval = 8.0
    private static let watchdogTimeoutSeconds: TimeInterval = 5.0
    private static let heartbeatIntervalSeconds: TimeInterval = 1.0

    private var desiredWidth: UInt16
    private var desiredHeight: UInt16
    private var desiredFps: UInt8

    private(set) var currentSessionId: UInt32?

    init(
        host: String,
        port: UInt16 = 9000,
        desiredWidth: UInt16 = ControlChannel.defaultWidth,
        desiredHeight: UInt16 = ControlChannel.defaultHeight,
        desiredFps: UInt8 = ControlChannel.defaultFps
    ) {
        self.host = host
        self.port = port
        self.desiredWidth = desiredWidth
        self.desiredHeight = desiredHeight
        self.desiredFps = desiredFps
    }

    func connect() {
        queue.async { [weak self] in
            guard let self = self else { return }
            self.teardown()
            self.handshakeAttempt = 0
            self.desiredWidth = self.desiredWidth == 0 ? ControlChannel.defaultWidth : self.desiredWidth
            self.openSocketAndHandshake()
        }
    }

    func disconnect() {
        queue.async { [weak self] in
            guard let self = self else { return }
            if let sid = self.sessionId {
                let disc = Disconnect(sessionId: sid, reason: .user)
                self.connection?.send(disc.encode())
            }
            self.teardown()
        }
    }

    private func openSocketAndHandshake() {
        let conn = UDPConnection(host: host, port: port, queue: queue)
        conn.onReceive = { [weak self] data in
            self?.handleIncoming(data)
        }
        conn.onStateChange = { [weak self] state in
            if case .failed = state {
                self?.scheduleHandshakeRetry()
            }
        }
        connection = conn
        conn.start()
        sendHandshake()
    }

    private func sendHandshake() {
        sessionNonce = Handshake.randomNonce()
        let hs = Handshake(
            clientProtocolVersion: 1,
            desiredWidth: desiredWidth,
            desiredHeight: desiredHeight,
            desiredFps: desiredFps,
            codecMask: [.h264],
            sessionNonce: sessionNonce
        )
        connection?.send(hs.encode())
        scheduleHandshakeRetry()
    }

    private func scheduleHandshakeRetry() {
        handshakeRetryWorkItem?.cancel()
        guard sessionId == nil else { return }

        handshakeAttempt += 1
        if handshakeAttempt == ControlChannel.attemptsBeforeFallback {
            desiredWidth = ControlChannel.defaultWidth
            desiredHeight = ControlChannel.defaultHeight
            desiredFps = ControlChannel.defaultFps
        }

        let delay = min(0.5 * pow(2.0, Double(handshakeAttempt - 1)), ControlChannel.maxBackoffSeconds)
        let work = DispatchWorkItem { [weak self] in
            guard let self = self, self.sessionId == nil else { return }
            self.sendHandshake()
        }
        handshakeRetryWorkItem = work
        queue.asyncAfter(deadline: .now() + delay, execute: work)
    }

    private func handleIncoming(_ data: Data) {
        lastReceivedAt = Date()
        let bytes = Array(data)
        guard let (header, _) = PacketHeader.parse(bytes) else { return }

        switch header.type {
        case .handshakeAck:
            guard sessionId == nil, let ack = HandshakeAck.decode(data), ack.sessionNonce == sessionNonce else {
                return
            }
            sessionId = ack.sessionId
            currentSessionId = ack.sessionId
            handshakeRetryWorkItem?.cancel()
            handshakeRetryWorkItem = nil
            startHeartbeatAndWatchdog()
            delegate?.controlChannel(
                self,
                didEstablishSession: ack.sessionId,
                width: ack.chosenWidth,
                height: ack.chosenHeight,
                fps: ack.chosenFps,
                codec: ack.chosenCodec
            )

        case .heartbeat:
            // Just refreshing lastReceivedAt (done above) is enough --
            // no reply required for an inbound heartbeat.
            break

        case .disconnect:
            let reasonText: String
            if let d = Disconnect.decode(data) {
                reasonText = "peer disconnected (reason=\(d.reason.rawValue))"
            } else {
                reasonText = "peer disconnected"
            }
            teardown()
            delegate?.controlChannelDidDisconnect(self, reason: reasonText)

        default:
            break
        }
    }

    private func startHeartbeatAndWatchdog() {
        heartbeatTimer?.cancel()
        let hb = DispatchSource.makeTimerSource(queue: queue)
        hb.schedule(deadline: .now() + ControlChannel.heartbeatIntervalSeconds, repeating: ControlChannel.heartbeatIntervalSeconds)
        hb.setEventHandler { [weak self] in
            guard let self = self, let sid = self.sessionId else { return }
            self.connection?.send(Heartbeat(sessionId: sid).encode())
        }
        hb.resume()
        heartbeatTimer = hb

        watchdogTimer?.cancel()
        let wd = DispatchSource.makeTimerSource(queue: queue)
        wd.schedule(deadline: .now() + ControlChannel.heartbeatIntervalSeconds, repeating: ControlChannel.heartbeatIntervalSeconds)
        wd.setEventHandler { [weak self] in
            guard let self = self else { return }
            // Per PROTOCOL.md: either side that hasn't seen a Heartbeat,
            // VideoFrameFragment, or InputEvent in 5s tears the session
            // down locally. This channel only observes Control-port
            // traffic (Heartbeat/HandshakeAck/Disconnect); video/input
            // flowing on their own ports is a separate liveness signal the
            // higher-level BridgeSession could additionally take into
            // account, but Control-channel heartbeats alone are a
            // reasonable, simple liveness proxy here.
            if Date().timeIntervalSince(self.lastReceivedAt) > ControlChannel.watchdogTimeoutSeconds {
                self.teardown()
                self.delegate?.controlChannelDidDisconnect(self, reason: "heartbeat timeout")
            }
        }
        wd.resume()
        watchdogTimer = wd
    }

    private func teardown() {
        heartbeatTimer?.cancel()
        heartbeatTimer = nil
        watchdogTimer?.cancel()
        watchdogTimer = nil
        handshakeRetryWorkItem?.cancel()
        handshakeRetryWorkItem = nil
        connection?.cancel()
        connection = nil
        sessionId = nil
        currentSessionId = nil
    }
}
