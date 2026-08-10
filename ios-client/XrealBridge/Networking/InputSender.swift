import Foundation

/// Encodes and sends `InputEvent` packets to the PC's Input port (9002 by
/// default). Pointer motion and scroll deltas are coalesced (summed) and
/// flushed on a fixed-rate timer capped at ~120Hz, so a fast finger drag on
/// a ProMotion display -- which can generate touch callbacks faster than
/// that -- doesn't flood the network with one datagram per touch update. A
/// plain repeating `DispatchSourceTimer` is used rather than `CADisplayLink`
/// since this class has no UI/run-loop attachment and a GCD timer is the
/// simpler correct tool for a fixed-rate background flush.
final class InputSender {
    private let connection: UDPConnection
    private let queue = DispatchQueue(label: "com.xrealbridge.inputsender")
    private var sessionId: UInt32?

    private var pendingMoveDx: Float = 0
    private var pendingMoveDy: Float = 0
    private var hasPendingMove = false

    private var pendingScrollDx: Float = 0
    private var pendingScrollDy: Float = 0
    private var hasPendingScroll = false

    private var coalesceTimer: DispatchSourceTimer?

    init(host: String, port: UInt16 = 9002) {
        connection = UDPConnection(host: host, port: port, queue: DispatchQueue(label: "com.xrealbridge.inputsender.socket"))
        connection.start()
        startCoalesceTimer()
    }

    func updateSession(_ sessionId: UInt32?) {
        queue.async { [weak self] in
            self?.sessionId = sessionId
        }
    }

    func stop() {
        queue.async { [weak self] in
            self?.coalesceTimer?.cancel()
            self?.coalesceTimer = nil
        }
        connection.cancel()
    }

    private func startCoalesceTimer() {
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now(), repeating: 1.0 / 120.0)
        timer.setEventHandler { [weak self] in
            self?.flush()
        }
        timer.resume()
        coalesceTimer = timer
    }

    private func flush() {
        guard let sid = sessionId else { return }

        if hasPendingMove {
            let evt = InputEvent(sessionId: sid, eventType: .mouseMove, dx: pendingMoveDx, dy: pendingMoveDy)
            connection.send(evt.encode())
            pendingMoveDx = 0
            pendingMoveDy = 0
            hasPendingMove = false
        }

        if hasPendingScroll {
            let evt = InputEvent(sessionId: sid, eventType: .scroll, dx: pendingScrollDx, dy: pendingScrollDy)
            connection.send(evt.encode())
            pendingScrollDx = 0
            pendingScrollDy = 0
            hasPendingScroll = false
        }
    }

    // MARK: - Public gesture -> event API (called from TrackpadViewController)

    func addMouseMoveDelta(dx: Float, dy: Float) {
        queue.async { [weak self] in
            guard let self = self else { return }
            self.pendingMoveDx += dx
            self.pendingMoveDy += dy
            self.hasPendingMove = true
        }
    }

    func addScrollDelta(dx: Float, dy: Float) {
        queue.async { [weak self] in
            guard let self = self else { return }
            self.pendingScrollDx += dx
            self.pendingScrollDy += dy
            self.hasPendingScroll = true
        }
    }

    private func sendImmediate(_ type: InputEventType, keyCode: UInt16 = 0) {
        queue.async { [weak self] in
            guard let self = self, let sid = self.sessionId else { return }
            let evt = InputEvent(sessionId: sid, eventType: type, keyCode: keyCode)
            self.connection.send(evt.encode())
        }
    }

    func sendLeftClick() { sendImmediate(.leftClick) }
    func sendRightClick() { sendImmediate(.rightClick) }
    func sendLeftDown() { sendImmediate(.leftDown) }
    func sendLeftUp() { sendImmediate(.leftUp) }
    func sendKeyDown(keyCode: UInt16) { sendImmediate(.keyDown, keyCode: keyCode) }
    func sendKeyUp(keyCode: UInt16) { sendImmediate(.keyUp, keyCode: keyCode) }
}
