import Foundation
import Network

/// Thin wrapper over `NWConnection` configured for UDP.
///
/// NOTE (unverified locally -- no macOS/Xcode available in this build
/// environment): creating an `NWConnection` to a fixed UDP host/port and
/// calling `send`/`receiveMessage` does not perform a real handshake (UDP
/// is connectionless), but Network.framework still models it as a
/// "connection" bound to that remote host/port and a stable local
/// ephemeral port, so replies the peer sends back to that same local port
/// are delivered through this same `NWConnection`'s receive callbacks.
/// This is the standard, documented way to do "connected UDP" with
/// Network.framework (used here for the Control channel and Input
/// sending), but it has not been exercised against a live peer in this
/// environment -- worth confirming early against a real PC host.
final class UDPConnection {
    private let connection: NWConnection
    private let queue: DispatchQueue
    private(set) var isReady: Bool = false
    private var stopped = false

    var onReceive: ((Data) -> Void)?
    var onStateChange: ((NWConnection.State) -> Void)?

    init(host: String, port: UInt16, queue: DispatchQueue) {
        self.queue = queue
        let nwHost = NWEndpoint.Host(host)
        // NWEndpoint.Port(rawValue:) only fails for port 0; our callers
        // always pass a real port number (9000/9001/9002 by default).
        let nwPort = NWEndpoint.Port(rawValue: port) ?? NWEndpoint.Port.any
        let params = NWParameters.udp
        self.connection = NWConnection(host: nwHost, port: nwPort, using: params)
        self.connection.stateUpdateHandler = { [weak self] state in
            guard let self = self else { return }
            // Switching rather than `state == .ready` deliberately --
            // NWConnection.State's Equatable conformance couldn't be
            // confirmed against the real SDK in this environment, and a
            // switch works regardless.
            switch state {
            case .ready:
                self.isReady = true
            default:
                self.isReady = false
            }
            self.onStateChange?(state)
        }
    }

    func start() {
        stopped = false
        connection.start(queue: queue)
        receiveNext()
    }

    func cancel() {
        stopped = true
        connection.cancel()
        isReady = false
    }

    func send(_ data: Data) {
        connection.send(content: data, completion: .contentProcessed { _ in
            // Fire-and-forget: UDP has no delivery guarantee, and the
            // higher-level protocol (heartbeats / retries) is what detects
            // loss, not this per-datagram completion handler.
        })
    }

    private func receiveNext() {
        connection.receiveMessage { [weak self] data, _, _, error in
            guard let self = self, !self.stopped else { return }
            if let data = data, !data.isEmpty {
                self.onReceive?(data)
            }
            if error == nil {
                self.receiveNext()
            }
            // On error, the receive loop stops; the caller's onStateChange
            // handler (or a heartbeat-timeout watchdog) is responsible for
            // detecting the dead connection and restarting it.
        }
    }
}
