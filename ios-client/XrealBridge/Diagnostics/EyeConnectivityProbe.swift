import Foundation
import Network

/// One-shot diagnostic, not part of the bridge protocol: tests whether 169.254.2.1:52998 (IMU
/// stream) and :52999 (control) -- the link-local endpoints documented by the open-source XREAL
/// One reverse-engineering community (github.com/Skarian/one-xr,
/// github.com/rohitsangwan01/xreal_one_driver) -- are reachable over whatever network path
/// exists right now with the glasses connected over USB-C.
///
/// This answers one question empirically instead of by more reading: does iOS route to that
/// address at all (the way it would for a standard USB Ethernet/RNDIS adapter, no special
/// entitlement needed), or is it invisible the way a proprietary MFi-gated accessory would be.
/// Standalone and not wired into BridgeSession/ControlChannel -- this has nothing to do with the
/// PC connection, it's purely local (phone <-> glasses) reachability. Its result is surfaced
/// directly in TrackpadViewController's UI because there's no Mac/Xcode available in this build
/// environment to attach a debugger and read console output.
enum EyeConnectivityProbe {
    struct Result {
        let port: UInt16
        let reachable: Bool
        let detail: String
    }

    /// Attempts a TCP connection to 169.254.2.1 on the given port, calling back on the main
    /// queue with the outcome once the connection either succeeds or `timeout` seconds elapse.
    static func probe(port: UInt16, timeout: TimeInterval = 3.0, completion: @escaping (Result) -> Void) {
        guard let nwPort = NWEndpoint.Port(rawValue: port) else {
            DispatchQueue.main.async { completion(Result(port: port, reachable: false, detail: "invalid port")) }
            return
        }

        let connection = NWConnection(host: "169.254.2.1", port: nwPort, using: .tcp)
        var finished = false
        let finish: (Result) -> Void = { result in
            guard !finished else { return }
            finished = true
            connection.cancel()
            DispatchQueue.main.async { completion(result) }
        }

        connection.stateUpdateHandler = { state in
            switch state {
            case .ready:
                finish(Result(port: port, reachable: true, detail: "connected"))
            case .failed(let error):
                finish(Result(port: port, reachable: false, detail: "\(error)"))
            case .waiting:
                // Not a definite failure yet -- the network stack is still trying (e.g. no
                // route to that address at all is reported this way, not .failed, on iOS).
                // Let the timeout below be the actual failure signal.
                break
            default:
                break
            }
        }

        connection.start(queue: .global(qos: .utility))

        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + timeout) {
            finish(Result(port: port, reachable: false, detail: "no route / timed out after \(Int(timeout))s"))
        }
    }

    /// Probes both documented ports, calling back once with both results.
    static func probeBoth(completion: @escaping (_ stream: Result, _ control: Result) -> Void) {
        probe(port: 52998) { streamResult in
            probe(port: 52999) { controlResult in
                completion(streamResult, controlResult)
            }
        }
    }
}
