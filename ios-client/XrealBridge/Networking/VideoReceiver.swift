import Foundation
import Network

// MARK: - FragmentReassembler

/// Pure reassembly logic for `VideoFrameFragment` datagrams, kept free of
/// any networking/UIKit/VideoToolbox dependency so it's cheap and
/// deterministic to unit test (see XrealBridgeTests/ProtocolTests.swift).
///
/// Rules, per PROTOCOL.md:
/// - Buffer fragments by frameId; once `fragmentCount` fragments are
///   present, concatenate in `fragmentIndex` order and hand off the
///   completed access unit.
/// - If a frame's fragments aren't fully received before a newer frame
///   completes, drop the stale partial frame rather than ever feeding a
///   partially-assembled access unit onward.
/// - Separately, `purgeStaleFrames(now:)` drops any partial frame that has
///   been sitting incomplete for longer than `staleFrameTimeout` (handles
///   the case where a keyframe never fully arrives and no newer frame
///   completes to naturally evict it).
final class FragmentReassembler {
    struct CompletedFrame {
        let frameId: UInt32
        let bytes: [UInt8]
        let ptsMicros: UInt64
        let isKeyframe: Bool
    }

    private struct FragmentBuffer {
        let fragmentCount: Int
        var fragments: [Int: [UInt8]] = [:]
        var ptsMicros: UInt64 = 0
        var isKeyframe: Bool = false
        var firstSeenAt: Date = Date()
    }

    private var pending: [UInt32: FragmentBuffer] = [:]
    private var newestCompletedFrameId: UInt32 = 0
    private var hasCompletedAnyFrame = false
    private let staleFrameTimeout: TimeInterval

    init(staleFrameTimeout: TimeInterval = 0.25) {
        self.staleFrameTimeout = staleFrameTimeout
    }

    /// Number of frames currently buffered but incomplete. Exposed mainly
    /// for tests.
    var pendingFrameCount: Int { pending.count }

    /// Feeds one fragment. Returns the completed access unit if this
    /// fragment was the one that completed its frame, else `nil`.
    @discardableResult
    func ingest(_ fragment: VideoFrameFragment) -> CompletedFrame? {
        guard fragment.fragmentCount > 0, fragment.fragmentIndex < fragment.fragmentCount else {
            return nil
        }
        if hasCompletedAnyFrame && fragment.frameId < newestCompletedFrameId {
            // Stale fragment for a frame we've already superseded -- drop.
            return nil
        }

        var buffer = pending[fragment.frameId] ?? FragmentBuffer(fragmentCount: Int(fragment.fragmentCount))
        buffer.fragments[Int(fragment.fragmentIndex)] = fragment.payload
        buffer.ptsMicros = fragment.ptsMicros
        if fragment.isKeyframe {
            buffer.isKeyframe = true
        }
        pending[fragment.frameId] = buffer

        guard buffer.fragments.count == buffer.fragmentCount else { return nil }

        var accessUnit: [UInt8] = []
        accessUnit.reserveCapacity(buffer.fragments.values.reduce(0) { $0 + $1.count })
        for i in 0..<buffer.fragmentCount {
            guard let piece = buffer.fragments[i] else {
                // Shouldn't happen given fragments.count == fragmentCount
                // and indices are guaranteed < fragmentCount above, but
                // bail safely rather than assembling a corrupt frame.
                pending.removeValue(forKey: fragment.frameId)
                return nil
            }
            accessUnit.append(contentsOf: piece)
        }

        pending.removeValue(forKey: fragment.frameId)
        newestCompletedFrameId = max(newestCompletedFrameId, fragment.frameId)
        hasCompletedAnyFrame = true
        // Drop any older still-incomplete buffers now that a newer frame
        // has completed -- never feed a stale partial frame later.
        pending = pending.filter { $0.key >= newestCompletedFrameId }

        return CompletedFrame(
            frameId: fragment.frameId,
            bytes: accessUnit,
            ptsMicros: buffer.ptsMicros,
            isKeyframe: buffer.isKeyframe
        )
    }

    /// Drops partial frame buffers older than `staleFrameTimeout`. Call
    /// periodically (VideoReceiver does this on a timer); pure logic here
    /// takes `now` as a parameter so tests don't need to sleep in real
    /// time.
    func purgeStaleFrames(now: Date = Date()) {
        pending = pending.filter { now.timeIntervalSince($0.value.firstSeenAt) < staleFrameTimeout }
    }

    func reset() {
        pending.removeAll()
        newestCompletedFrameId = 0
        hasCompletedAnyFrame = false
    }
}

// MARK: - VideoReceiverDelegate

protocol VideoReceiverDelegate: AnyObject {
    func videoReceiver(
        _ receiver: VideoReceiver,
        didReassembleAccessUnit bytes: [UInt8],
        ptsMicros: UInt64,
        isKeyframe: Bool
    )
}

// MARK: - VideoReceiver

/// Listens on the Video UDP port for `VideoFrameFragment` datagrams sent
/// by the PC host and reassembles them into complete Annex-B access units
/// via `FragmentReassembler`.
///
/// NOTE (unverified locally -- no macOS/Xcode available in this build
/// environment): PC->iOS UDP delivery is received here via an `NWListener`
/// bound to the video port rather than an `NWConnection`, because the PC
/// initiates traffic to us (we never "dial out" on this channel) and we
/// don't know the PC's video-port 4-tuple ahead of time -- it only learns
/// our reachable address from the source of our Control-channel Handshake.
/// `NWListener`'s `newConnectionHandler` fires with a fresh `NWConnection`
/// representing the first inbound flow; we keep only the most recent one
/// (this app only ever talks to one PC at a time) and cancel any prior
/// one. This is the standard pattern for "receive UDP from anyone" with
/// Network.framework, but it has not been exercised end-to-end against a
/// real peer in this environment -- worth confirming on-device early,
/// since if `newConnectionHandler` doesn't fire the way expected for
/// repeated datagrams from the same PC source port, this is the first
/// place to look.
final class VideoReceiver {
    weak var delegate: VideoReceiverDelegate?

    private let port: UInt16
    private let queue = DispatchQueue(label: "com.xrealbridge.videoreceiver")
    private var listener: NWListener?
    private var activeConnection: NWConnection?
    private var expectedSessionId: UInt32?
    private let reassembler = FragmentReassembler()
    private var purgeTimer: DispatchSourceTimer?

    init(port: UInt16 = 9001) {
        self.port = port
    }

    /// `sessionId` is the one negotiated by ControlChannel; fragments for
    /// any other session are ignored (stale/foreign traffic, e.g. from a
    /// PC process that restarted mid-session).
    func start(sessionId: UInt32) {
        stop()
        expectedSessionId = sessionId
        reassembler.reset()

        guard let nwPort = NWEndpoint.Port(rawValue: port) else { return }
        do {
            let params = NWParameters.udp
            let l = try NWListener(using: params, on: nwPort)
            l.newConnectionHandler = { [weak self] connection in
                self?.adopt(connection)
            }
            l.stateUpdateHandler = { _ in }
            l.start(queue: queue)
            listener = l
        } catch {
            // Listener construction failed (e.g. port already in use).
            // There's no automatic retry here; BridgeSession's status
            // reporting is the caller's signal to surface this to the
            // user.
            return
        }

        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 0.1, repeating: 0.1)
        timer.setEventHandler { [weak self] in
            self?.reassembler.purgeStaleFrames()
        }
        timer.resume()
        purgeTimer = timer
    }

    func stop() {
        purgeTimer?.cancel()
        purgeTimer = nil
        listener?.cancel()
        listener = nil
        activeConnection?.cancel()
        activeConnection = nil
        expectedSessionId = nil
        reassembler.reset()
    }

    private func adopt(_ connection: NWConnection) {
        activeConnection?.cancel()
        activeConnection = connection
        connection.stateUpdateHandler = { _ in }
        connection.start(queue: queue)
        receiveNext(on: connection)
    }

    private func receiveNext(on connection: NWConnection) {
        connection.receiveMessage { [weak self] data, _, _, error in
            guard let self = self else { return }
            if let data = data, !data.isEmpty {
                self.handleDatagram(data)
            }
            if error == nil, connection === self.activeConnection {
                self.receiveNext(on: connection)
            }
        }
    }

    private func handleDatagram(_ data: Data) {
        guard let fragment = VideoFrameFragment.decode(data) else { return }
        guard fragment.sessionId == expectedSessionId else { return }
        guard let completed = reassembler.ingest(fragment) else { return }
        delegate?.videoReceiver(
            self,
            didReassembleAccessUnit: completed.bytes,
            ptsMicros: completed.ptsMicros,
            isKeyframe: completed.isKeyframe
        )
    }
}
