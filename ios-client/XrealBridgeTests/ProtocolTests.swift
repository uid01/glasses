import Foundation
import XCTest
@testable import XrealBridge

/// Pure-logic tests: packet encode/decode round trips for every packet
/// type, plus fragment reassembly with out-of-order/missing fragments.
/// Deliberately exercises only `ProtocolTypes.swift` and the
/// `FragmentReassembler` type in `VideoReceiver.swift`, neither of which
/// import UIKit/VideoToolbox/Network -- so these tests don't open real
/// sockets, touch hardware decode, or need a UI, and should be the most
/// reliable thing in this project to "just work" in CI.
final class ProtocolTests: XCTestCase {

    // MARK: - Handshake

    func testHandshakeRoundTrip() {
        let original = Handshake(
            clientProtocolVersion: 1,
            desiredWidth: 1920,
            desiredHeight: 1080,
            desiredFps: 60,
            codecMask: [.h264, .hevc],
            sessionNonce: (0..<16).map { UInt8($0) }
        )
        let encoded = original.encode()
        // 4-byte common header + 2+2+2+1+1+16 = 28 bytes total.
        XCTAssertEqual(encoded.count, 28)

        guard let decoded = Handshake.decode(encoded) else {
            return XCTFail("Failed to decode Handshake")
        }
        XCTAssertEqual(decoded, original)
    }

    func testHandshakeNonceIsPaddedOrTruncatedTo16Bytes() {
        let short = Handshake(desiredWidth: 100, desiredHeight: 100, desiredFps: 30, codecMask: [.h264], sessionNonce: [1, 2, 3])
        let encoded = short.encode()
        XCTAssertEqual(encoded.count, 28)
        guard let decoded = Handshake.decode(encoded) else {
            return XCTFail("Failed to decode Handshake")
        }
        XCTAssertEqual(decoded.sessionNonce.count, 16)
        XCTAssertEqual(Array(decoded.sessionNonce.prefix(3)), [1, 2, 3])
    }

    // MARK: - HandshakeAck

    func testHandshakeAckRoundTrip() {
        let original = HandshakeAck(
            serverProtocolVersion: 1,
            chosenWidth: 1920,
            chosenHeight: 1080,
            chosenFps: 60,
            chosenCodec: .h264,
            sessionNonce: (0..<16).map { UInt8(15 - $0) },
            sessionId: 0xDEADBEEF
        )
        let encoded = original.encode()
        // 4-byte common header + 2+2+2+1+1+16+4 = 32 bytes total.
        XCTAssertEqual(encoded.count, 32)

        guard let decoded = HandshakeAck.decode(encoded) else {
            return XCTFail("Failed to decode HandshakeAck")
        }
        XCTAssertEqual(decoded, original)
    }

    // MARK: - Heartbeat

    func testHeartbeatRoundTrip() {
        let original = Heartbeat(sessionId: 42)
        let encoded = original.encode()
        XCTAssertEqual(encoded.count, 8) // 4-byte header + 4-byte sessionId
        guard let decoded = Heartbeat.decode(encoded) else {
            return XCTFail("Failed to decode Heartbeat")
        }
        XCTAssertEqual(decoded, original)
    }

    // MARK: - Disconnect

    func testDisconnectRoundTrip() {
        for reason: DisconnectReason in [.user, .error, .shutdown] {
            let original = Disconnect(sessionId: 7, reason: reason)
            let encoded = original.encode()
            guard let decoded = Disconnect.decode(encoded) else {
                return XCTFail("Failed to decode Disconnect for reason \(reason)")
            }
            XCTAssertEqual(decoded, original)
        }
    }

    // MARK: - VideoFrameFragment

    func testVideoFrameFragmentRoundTrip() {
        let payload: [UInt8] = Array(repeating: 0xAB, count: 100)
        let original = VideoFrameFragment(
            sessionId: 123,
            frameId: 456,
            fragmentIndex: 2,
            fragmentCount: 5,
            ptsMicros: 1_000_000,
            flags: VideoFrameFragment.makeFlags(isKeyframe: true, isLastFragment: false),
            payload: payload
        )
        let encoded = original.encode()
        // 27-byte header (4-byte common header + 23 bytes of fragment
        // fields: sessionId4+frameId4+fragIdx2+fragCount2+pts8+flags1+
        // payloadLength2) + payload.
        XCTAssertEqual(encoded.count, 27 + payload.count)

        guard let decoded = VideoFrameFragment.decode(encoded) else {
            return XCTFail("Failed to decode VideoFrameFragment")
        }
        XCTAssertEqual(decoded, original)
        XCTAssertTrue(decoded.isKeyframe)
        XCTAssertFalse(decoded.isLastFragment)
    }

    func testVideoFrameFragmentEmptyPayload() {
        let original = VideoFrameFragment(
            sessionId: 1,
            frameId: 1,
            fragmentIndex: 0,
            fragmentCount: 1,
            ptsMicros: 0,
            flags: VideoFrameFragment.makeFlags(isKeyframe: false, isLastFragment: true),
            payload: []
        )
        let encoded = original.encode()
        guard let decoded = VideoFrameFragment.decode(encoded) else {
            return XCTFail("Failed to decode empty-payload VideoFrameFragment")
        }
        XCTAssertEqual(decoded.payload, [])
        XCTAssertTrue(decoded.isLastFragment)
    }

    // MARK: - InputEvent

    func testInputEventRoundTripAllTypes() {
        let allTypes: [InputEventType] = [.mouseMove, .scroll, .leftClick, .rightClick, .leftDown, .leftUp, .keyDown, .keyUp]
        for type in allTypes {
            let original = InputEvent(sessionId: 99, eventType: type, dx: 1.5, dy: -2.25, keyCode: 0x41)
            let encoded = original.encode()
            // Fixed 19-byte total datagram per PROTOCOL.md.
            XCTAssertEqual(encoded.count, 19, "InputEvent for \(type) should be 19 bytes")

            guard let decoded = InputEvent.decode(encoded) else {
                return XCTFail("Failed to decode InputEvent for \(type)")
            }
            XCTAssertEqual(decoded, original)
        }
    }

    func testInputEventNegativeAndFractionalDeltas() {
        let original = InputEvent(sessionId: 1, eventType: .mouseMove, dx: -123.456, dy: 0.001)
        let encoded = original.encode()
        guard let decoded = InputEvent.decode(encoded) else {
            return XCTFail("Failed to decode InputEvent")
        }
        XCTAssertEqual(decoded.dx, original.dx, accuracy: 0.0001)
        XCTAssertEqual(decoded.dy, original.dy, accuracy: 0.0001)
    }

    // MARK: - Header validation / defensive parsing

    func testRejectsWrongMagic() {
        var bytes = Handshake(desiredWidth: 1, desiredHeight: 1, desiredFps: 1, codecMask: [.h264], sessionNonce: []).encode()
        bytes[0] = 0x00
        XCTAssertNil(Handshake.decode(bytes))
    }

    func testRejectsWrongVersion() {
        var bytes = Handshake(desiredWidth: 1, desiredHeight: 1, desiredFps: 1, codecMask: [.h264], sessionNonce: []).encode()
        bytes[2] = 99
        XCTAssertNil(Handshake.decode(bytes))
    }

    func testRejectsMismatchedPacketType() {
        // A validly-formed Heartbeat should not decode as a Handshake.
        let heartbeatBytes = Heartbeat(sessionId: 1).encode()
        XCTAssertNil(Handshake.decode(heartbeatBytes))
    }

    func testRejectsTruncatedPacket() {
        let full = Heartbeat(sessionId: 1).encode()
        let truncated = full.prefix(full.count - 1)
        XCTAssertNil(Heartbeat.decode(Data(truncated)))
    }

    func testRejectsEmptyData() {
        XCTAssertNil(Handshake.decode(Data()))
    }

    // MARK: - FragmentReassembler

    private func makeFragments(
        sessionId: UInt32,
        frameId: UInt32,
        payloads: [[UInt8]],
        isKeyframe: Bool = false,
        pts: UInt64 = 0
    ) -> [VideoFrameFragment] {
        let count = UInt16(payloads.count)
        return payloads.enumerated().map { index, payload in
            VideoFrameFragment(
                sessionId: sessionId,
                frameId: frameId,
                fragmentIndex: UInt16(index),
                fragmentCount: count,
                ptsMicros: pts,
                flags: VideoFrameFragment.makeFlags(isKeyframe: isKeyframe, isLastFragment: index == payloads.count - 1),
                payload: payload
            )
        }
    }

    func testFragmentReassemblerInOrderCompletion() {
        let reassembler = FragmentReassembler()
        let fragments = makeFragments(sessionId: 1, frameId: 1, payloads: [[1, 2], [3, 4], [5, 6]], isKeyframe: true, pts: 1000)

        XCTAssertNil(reassembler.ingest(fragments[0]))
        XCTAssertNil(reassembler.ingest(fragments[1]))
        guard let completed = reassembler.ingest(fragments[2]) else {
            return XCTFail("Expected frame to complete on the final fragment")
        }
        XCTAssertEqual(completed.bytes, [1, 2, 3, 4, 5, 6])
        XCTAssertEqual(completed.frameId, 1)
        XCTAssertEqual(completed.ptsMicros, 1000)
        XCTAssertTrue(completed.isKeyframe)
    }

    func testFragmentReassemblerOutOfOrderCompletion() {
        let reassembler = FragmentReassembler()
        let fragments = makeFragments(sessionId: 1, frameId: 5, payloads: [[10], [20], [30], [40]])

        // Feed in a scrambled order: 2, 0, 3, 1
        XCTAssertNil(reassembler.ingest(fragments[2]))
        XCTAssertNil(reassembler.ingest(fragments[0]))
        XCTAssertNil(reassembler.ingest(fragments[3]))
        guard let completed = reassembler.ingest(fragments[1]) else {
            return XCTFail("Expected frame to complete once all 4 out-of-order fragments arrived")
        }
        XCTAssertEqual(completed.bytes, [10, 20, 30, 40])
    }

    func testFragmentReassemblerMissingFragmentNeverCompletes() {
        let reassembler = FragmentReassembler()
        let fragments = makeFragments(sessionId: 1, frameId: 9, payloads: [[1], [2], [3]])

        // Only deliver fragments 0 and 2; fragment 1 is "lost".
        XCTAssertNil(reassembler.ingest(fragments[0]))
        XCTAssertNil(reassembler.ingest(fragments[2]))
        XCTAssertEqual(reassembler.pendingFrameCount, 1)

        // Re-delivering fragment 2 again shouldn't spuriously complete it.
        XCTAssertNil(reassembler.ingest(fragments[2]))
        XCTAssertEqual(reassembler.pendingFrameCount, 1)
    }

    func testFragmentReassemblerDropsStaleOlderFrameOnNewerCompletion() {
        let reassembler = FragmentReassembler()
        let frame1 = makeFragments(sessionId: 1, frameId: 1, payloads: [[1], [2]])
        let frame2 = makeFragments(sessionId: 1, frameId: 2, payloads: [[9], [8]])

        // Frame 1 arrives partially (1 of 2 fragments)...
        XCTAssertNil(reassembler.ingest(frame1[0]))
        XCTAssertEqual(reassembler.pendingFrameCount, 1)

        // ...then frame 2 arrives completely before frame 1 finishes.
        XCTAssertNil(reassembler.ingest(frame2[0]))
        guard let completed = reassembler.ingest(frame2[1]) else {
            return XCTFail("Expected frame 2 to complete")
        }
        XCTAssertEqual(completed.frameId, 2)

        // The now-superseded partial frame 1 should have been dropped, and
        // its remaining fragment should not be able to resurrect it.
        XCTAssertEqual(reassembler.pendingFrameCount, 0)
        XCTAssertNil(reassembler.ingest(frame1[1]))
        XCTAssertEqual(reassembler.pendingFrameCount, 0)
    }

    func testFragmentReassemblerMultipleConcurrentFrames() {
        let reassembler = FragmentReassembler()
        let frameA = makeFragments(sessionId: 1, frameId: 10, payloads: [[1], [2]])
        let frameB = makeFragments(sessionId: 1, frameId: 11, payloads: [[3], [4]])

        // Interleave fragments from two frames arriving concurrently.
        XCTAssertNil(reassembler.ingest(frameA[0]))
        XCTAssertNil(reassembler.ingest(frameB[0]))
        XCTAssertEqual(reassembler.pendingFrameCount, 2)

        guard let completedA = reassembler.ingest(frameA[1]) else {
            return XCTFail("Expected frame A to complete")
        }
        XCTAssertEqual(completedA.frameId, 10)

        guard let completedB = reassembler.ingest(frameB[1]) else {
            return XCTFail("Expected frame B to complete")
        }
        XCTAssertEqual(completedB.frameId, 11)
    }

    func testFragmentReassemblerPurgeStaleFrames() {
        let reassembler = FragmentReassembler(staleFrameTimeout: 0.2)
        let fragments = makeFragments(sessionId: 1, frameId: 1, payloads: [[1], [2]])

        XCTAssertNil(reassembler.ingest(fragments[0]))
        XCTAssertEqual(reassembler.pendingFrameCount, 1)

        // Not stale yet.
        reassembler.purgeStaleFrames(now: Date().addingTimeInterval(0.1))
        XCTAssertEqual(reassembler.pendingFrameCount, 1)

        // Past the timeout -- should be purged.
        reassembler.purgeStaleFrames(now: Date().addingTimeInterval(0.3))
        XCTAssertEqual(reassembler.pendingFrameCount, 0)

        // The purged frame's remaining fragment should not complete it.
        XCTAssertNil(reassembler.ingest(fragments[1]))
        XCTAssertEqual(reassembler.pendingFrameCount, 1) // starts a fresh buffer
    }

    func testFragmentReassemblerIgnoresZeroFragmentCount() {
        let reassembler = FragmentReassembler()
        let bogus = VideoFrameFragment(sessionId: 1, frameId: 1, fragmentIndex: 0, fragmentCount: 0, ptsMicros: 0, flags: 0, payload: [1, 2, 3])
        XCTAssertNil(reassembler.ingest(bogus))
        XCTAssertEqual(reassembler.pendingFrameCount, 0)
    }
}
