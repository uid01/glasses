import Foundation
import UIKit

/// Process-wide singleton holding the one long-lived `BridgeSession`. Scene
/// delegates read this rather than owning their own networking objects, so
/// unplugging/replugging the XREAL glasses (which disconnects/reconnects
/// only the external-display scene) doesn't tear down the Control channel
/// or force a re-handshake with the PC.
final class AppEnvironment {
    static let shared = AppEnvironment()
    let bridgeSession = BridgeSession()
    private init() {}
}

protocol BridgeSessionStatusDelegate: AnyObject {
    func bridgeSession(_ session: BridgeSession, statusDidChange status: BridgeSession.Status)
}

/// Coordinates the control channel, video receive/decode pipeline, and
/// input sender for one PC connection. Owned for the lifetime of the app
/// process via `AppEnvironment.shared`; individual UI scenes/view
/// controllers attach/detach as they connect/disconnect rather than owning
/// their own copies of the networking stack.
final class BridgeSession {
    enum Status: Equatable {
        case disconnected
        case connecting
        case connected(width: UInt16, height: UInt16, fps: UInt8)
    }

    weak var statusDelegate: BridgeSessionStatusDelegate?

    private(set) var status: Status = .disconnected {
        didSet { statusDelegate?.bridgeSession(self, statusDidChange: status) }
    }

    private var controlChannel: ControlChannel?
    private var videoReceiver: VideoReceiver?
    private var inputSender: InputSender?
    private let decoder = H264Decoder()

    private weak var externalDisplayView: ExternalDisplayView?

    /// PC IP the user entered, persisted across launches. Ports are the
    /// PROTOCOL.md defaults (9000/9001/9002); the protocol doc notes ports
    /// are configurable at runtime, but this app doesn't currently expose
    /// that -- only the host/IP is user-editable.
    var pcHost: String {
        get { UserDefaults.standard.string(forKey: "pcHost") ?? "" }
        set { UserDefaults.standard.set(newValue, forKey: "pcHost") }
    }

    init() {}

    func connect(host: String) {
        guard !host.isEmpty else { return }
        pcHost = host
        tearDownNetworking()
        status = .connecting

        let control = ControlChannel(host: host, port: 9000)
        control.delegate = self
        controlChannel = control

        let input = InputSender(host: host, port: 9002)
        inputSender = input

        control.connect()
    }

    func disconnect() {
        controlChannel?.disconnect()
        tearDownNetworking()
        status = .disconnected
    }

    private func tearDownNetworking() {
        videoReceiver?.stop()
        videoReceiver = nil
        inputSender?.stop()
        inputSender = nil
        decoder.invalidate()
    }

    // MARK: - External display attach/detach

    func attachExternalDisplay(_ view: ExternalDisplayView) {
        externalDisplayView = view
        decoder.delegate = view
    }

    func detachExternalDisplay() {
        decoder.delegate = nil
        externalDisplayView = nil
    }

    // MARK: - Input passthrough used by TrackpadViewController

    func sendMouseMove(dx: Float, dy: Float) { inputSender?.addMouseMoveDelta(dx: dx, dy: dy) }
    func sendScroll(dx: Float, dy: Float) { inputSender?.addScrollDelta(dx: dx, dy: dy) }
    func sendLeftClick() { inputSender?.sendLeftClick() }
    func sendRightClick() { inputSender?.sendRightClick() }
    func sendLeftDown() { inputSender?.sendLeftDown() }
    func sendLeftUp() { inputSender?.sendLeftUp() }
    func sendKeyDown(keyCode: UInt16) { inputSender?.sendKeyDown(keyCode: keyCode) }
    func sendKeyUp(keyCode: UInt16) { inputSender?.sendKeyUp(keyCode: keyCode) }
}

extension BridgeSession: ControlChannelDelegate {
    func controlChannel(
        _ channel: ControlChannel,
        didEstablishSession sessionId: UInt32,
        width: UInt16,
        height: UInt16,
        fps: UInt8,
        codec: ChosenCodec
    ) {
        inputSender?.updateSession(sessionId)

        let receiver = VideoReceiver(port: 9001)
        receiver.delegate = self
        receiver.start(sessionId: sessionId)
        videoReceiver = receiver

        status = .connected(width: width, height: height, fps: fps)
    }

    func controlChannelDidDisconnect(_ channel: ControlChannel, reason: String) {
        tearDownNetworking()
        status = .disconnected
    }
}

extension BridgeSession: VideoReceiverDelegate {
    func videoReceiver(
        _ receiver: VideoReceiver,
        didReassembleAccessUnit bytes: [UInt8],
        ptsMicros: UInt64,
        isKeyframe: Bool
    ) {
        decoder.decode(accessUnit: bytes, presentationTimeMicros: ptsMicros, isKeyframe: isKeyframe)
    }
}
