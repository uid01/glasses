import Foundation

// Wire protocol types mirroring `shared-protocol/PROTOCOL.md` exactly.
// This file is intentionally free of UIKit / Network / VideoToolbox imports
// so its logic (encode/decode round trips) is cheap and safe to unit test
// without touching real sockets or hardware decode.
//
// All multi-byte integer/float fields are little-endian EXCEPT the 2-byte
// magic, which is the literal byte sequence 0xAF, 0x51 (in that order) on
// the wire -- NOT a little-endian encoding of the UInt16 value 0xAF51
// (that would serialize as bytes 0x51, 0xAF). We therefore always read/
// write the magic as two raw bytes, never via a UInt16 helper.

enum ProtocolConstants {
    static let magicByte0: UInt8 = 0xAF
    static let magicByte1: UInt8 = 0x51
    static let protocolVersion: UInt8 = 1
}

enum PacketType: UInt8 {
    case handshake = 0
    case handshakeAck = 1
    case heartbeat = 2
    case disconnect = 3
    case videoFrameFragment = 10
    case inputEvent = 20
}

enum DisconnectReason: UInt8 {
    case user = 0
    case error = 1
    case shutdown = 2
}

enum InputEventType: UInt8 {
    case mouseMove = 0
    case scroll = 1
    case leftClick = 2
    case rightClick = 3
    case leftDown = 4
    case leftUp = 5
    case keyDown = 6
    case keyUp = 7
}

/// `codecMask` bit flags advertised by the client in `Handshake.codecMask`.
/// bit0 = H264, bit1 = HEVC, per PROTOCOL.md.
struct CodecMask: OptionSet {
    let rawValue: UInt8
    static let h264 = CodecMask(rawValue: 1 << 0)
    static let hevc = CodecMask(rawValue: 1 << 1)
}

/// `chosenCodec` value in HandshakeAck -- a single choice, NOT a bitmask.
/// 0 = H264, 1 = HEVC, per PROTOCOL.md.
enum ChosenCodec: UInt8 {
    case h264 = 0
    case hevc = 1
}

// MARK: - Byte-level helpers

/// Small append-only little-endian byte writer used by every packet's
/// `encode()`.
struct ByteWriter {
    private(set) var bytes: [UInt8] = []

    mutating func writeRaw(_ b: UInt8) {
        bytes.append(b)
    }

    mutating func writeRawBytes(_ raw: [UInt8]) {
        bytes.append(contentsOf: raw)
    }

    mutating func writeUInt8(_ v: UInt8) {
        bytes.append(v)
    }

    mutating func writeUInt16LE(_ v: UInt16) {
        bytes.append(UInt8(v & 0xFF))
        bytes.append(UInt8((v >> 8) & 0xFF))
    }

    mutating func writeUInt32LE(_ v: UInt32) {
        bytes.append(UInt8(v & 0xFF))
        bytes.append(UInt8((v >> 8) & 0xFF))
        bytes.append(UInt8((v >> 16) & 0xFF))
        bytes.append(UInt8((v >> 24) & 0xFF))
    }

    mutating func writeUInt64LE(_ v: UInt64) {
        var value = v
        for _ in 0..<8 {
            bytes.append(UInt8(value & 0xFF))
            value >>= 8
        }
    }

    mutating func writeFloat32LE(_ v: Float) {
        writeUInt32LE(v.bitPattern)
    }

    var data: Data { Data(bytes) }
}

/// Bounds-checked little-endian byte reader used by every packet's
/// `decode(_:)`. Operates on a plain `[UInt8]` (never a `Data` slice) to
/// avoid `Data`'s non-zero-based-index footgun after `subdata`/slicing.
struct ByteReader {
    let bytes: [UInt8]
    private(set) var offset: Int = 0

    init(_ bytes: [UInt8]) {
        self.bytes = bytes
    }

    var remaining: Int { bytes.count - offset }

    mutating func readUInt8() -> UInt8? {
        guard offset + 1 <= bytes.count else { return nil }
        let v = bytes[offset]
        offset += 1
        return v
    }

    mutating func readUInt16LE() -> UInt16? {
        guard offset + 2 <= bytes.count else { return nil }
        let v = UInt16(bytes[offset]) | (UInt16(bytes[offset + 1]) << 8)
        offset += 2
        return v
    }

    mutating func readUInt32LE() -> UInt32? {
        guard offset + 4 <= bytes.count else { return nil }
        let v = UInt32(bytes[offset])
            | (UInt32(bytes[offset + 1]) << 8)
            | (UInt32(bytes[offset + 2]) << 16)
            | (UInt32(bytes[offset + 3]) << 24)
        offset += 4
        return v
    }

    mutating func readUInt64LE() -> UInt64? {
        guard offset + 8 <= bytes.count else { return nil }
        var v: UInt64 = 0
        for i in 0..<8 {
            v |= UInt64(bytes[offset + i]) << (8 * i)
        }
        offset += 8
        return v
    }

    mutating func readFloat32LE() -> Float? {
        guard let bits = readUInt32LE() else { return nil }
        return Float(bitPattern: bits)
    }

    mutating func readBytes(_ count: Int) -> [UInt8]? {
        guard count >= 0, offset + count <= bytes.count else { return nil }
        let slice = Array(bytes[offset..<(offset + count)])
        offset += count
        return slice
    }
}

// MARK: - Common header

struct PacketHeader {
    let version: UInt8
    let type: PacketType

    /// Writes the 4-byte common header (magic + version + type) into
    /// `writer`. Every packet's `encode()` must call this first.
    static func writeHeader(into writer: inout ByteWriter, type: PacketType) {
        writer.writeRaw(ProtocolConstants.magicByte0)
        writer.writeRaw(ProtocolConstants.magicByte1)
        writer.writeUInt8(ProtocolConstants.protocolVersion)
        writer.writeUInt8(type.rawValue)
    }

    /// Validates magic + version and returns the parsed header plus a
    /// `ByteReader` already positioned right after the 4-byte common header
    /// (i.e. at offset 4, ready to read type-specific fields).
    ///
    /// Any datagram that doesn't start with the magic bytes, has an
    /// unknown version, or has an unrecognized type byte is rejected by
    /// returning `nil` -- per PROTOCOL.md, such datagrams must be dropped
    /// silently.
    static func parse(_ bytes: [UInt8]) -> (header: PacketHeader, reader: ByteReader)? {
        var reader = ByteReader(bytes)
        guard let m0 = reader.readUInt8(), let m1 = reader.readUInt8() else { return nil }
        guard m0 == ProtocolConstants.magicByte0, m1 == ProtocolConstants.magicByte1 else { return nil }
        guard let version = reader.readUInt8(), version == ProtocolConstants.protocolVersion else { return nil }
        guard let typeRaw = reader.readUInt8(), let type = PacketType(rawValue: typeRaw) else { return nil }
        return (PacketHeader(version: version, type: type), reader)
    }
}

// MARK: - Handshake (type 0, iOS -> PC, Control port)

struct Handshake: Equatable {
    var clientProtocolVersion: UInt16
    var desiredWidth: UInt16
    var desiredHeight: UInt16
    var desiredFps: UInt8
    var codecMask: CodecMask
    /// Exactly 16 bytes.
    var sessionNonce: [UInt8]

    init(
        clientProtocolVersion: UInt16 = 1,
        desiredWidth: UInt16,
        desiredHeight: UInt16,
        desiredFps: UInt8,
        codecMask: CodecMask,
        sessionNonce: [UInt8]
    ) {
        self.clientProtocolVersion = clientProtocolVersion
        self.desiredWidth = desiredWidth
        self.desiredHeight = desiredHeight
        self.desiredFps = desiredFps
        self.codecMask = codecMask
        self.sessionNonce = sessionNonce
    }

    static func == (lhs: Handshake, rhs: Handshake) -> Bool {
        lhs.clientProtocolVersion == rhs.clientProtocolVersion
            && lhs.desiredWidth == rhs.desiredWidth
            && lhs.desiredHeight == rhs.desiredHeight
            && lhs.desiredFps == rhs.desiredFps
            && lhs.codecMask == rhs.codecMask
            && lhs.sessionNonce == rhs.sessionNonce
    }

    static func randomNonce() -> [UInt8] {
        (0..<16).map { _ in UInt8.random(in: 0...255) }
    }

    func encode() -> Data {
        var writer = ByteWriter()
        PacketHeader.writeHeader(into: &writer, type: .handshake)
        writer.writeUInt16LE(clientProtocolVersion)
        writer.writeUInt16LE(desiredWidth)
        writer.writeUInt16LE(desiredHeight)
        writer.writeUInt8(desiredFps)
        writer.writeUInt8(codecMask.rawValue)
        var nonce = sessionNonce
        if nonce.count < 16 {
            nonce.append(contentsOf: [UInt8](repeating: 0, count: 16 - nonce.count))
        } else if nonce.count > 16 {
            nonce = Array(nonce.prefix(16))
        }
        writer.writeRawBytes(nonce)
        return writer.data
    }

    static func decode(_ data: Data) -> Handshake? {
        let bytes = Array(data)
        guard let (header, reader0) = PacketHeader.parse(bytes), header.type == .handshake else { return nil }
        var reader = reader0
        guard let cpv = reader.readUInt16LE(),
              let w = reader.readUInt16LE(),
              let h = reader.readUInt16LE(),
              let fps = reader.readUInt8(),
              let maskRaw = reader.readUInt8(),
              let nonce = reader.readBytes(16)
        else { return nil }
        return Handshake(
            clientProtocolVersion: cpv,
            desiredWidth: w,
            desiredHeight: h,
            desiredFps: fps,
            codecMask: CodecMask(rawValue: maskRaw),
            sessionNonce: nonce
        )
    }
}

// MARK: - HandshakeAck (type 1, PC -> iOS, Control port)

struct HandshakeAck: Equatable {
    var serverProtocolVersion: UInt16
    var chosenWidth: UInt16
    var chosenHeight: UInt16
    var chosenFps: UInt8
    var chosenCodec: ChosenCodec
    /// Exactly 16 bytes, echoed from the Handshake that triggered this ack.
    var sessionNonce: [UInt8]
    var sessionId: UInt32

    func encode() -> Data {
        var writer = ByteWriter()
        PacketHeader.writeHeader(into: &writer, type: .handshakeAck)
        writer.writeUInt16LE(serverProtocolVersion)
        writer.writeUInt16LE(chosenWidth)
        writer.writeUInt16LE(chosenHeight)
        writer.writeUInt8(chosenFps)
        writer.writeUInt8(chosenCodec.rawValue)
        var nonce = sessionNonce
        if nonce.count < 16 {
            nonce.append(contentsOf: [UInt8](repeating: 0, count: 16 - nonce.count))
        } else if nonce.count > 16 {
            nonce = Array(nonce.prefix(16))
        }
        writer.writeRawBytes(nonce)
        writer.writeUInt32LE(sessionId)
        return writer.data
    }

    static func decode(_ data: Data) -> HandshakeAck? {
        let bytes = Array(data)
        guard let (header, reader0) = PacketHeader.parse(bytes), header.type == .handshakeAck else { return nil }
        var reader = reader0
        guard let spv = reader.readUInt16LE(),
              let w = reader.readUInt16LE(),
              let h = reader.readUInt16LE(),
              let fps = reader.readUInt8(),
              let codecRaw = reader.readUInt8(),
              let codec = ChosenCodec(rawValue: codecRaw),
              let nonce = reader.readBytes(16),
              let sessionId = reader.readUInt32LE()
        else { return nil }
        return HandshakeAck(
            serverProtocolVersion: spv,
            chosenWidth: w,
            chosenHeight: h,
            chosenFps: fps,
            chosenCodec: codec,
            sessionNonce: nonce,
            sessionId: sessionId
        )
    }
}

// MARK: - Heartbeat (type 2, either direction)

struct Heartbeat: Equatable {
    var sessionId: UInt32

    func encode() -> Data {
        var writer = ByteWriter()
        PacketHeader.writeHeader(into: &writer, type: .heartbeat)
        writer.writeUInt32LE(sessionId)
        return writer.data
    }

    static func decode(_ data: Data) -> Heartbeat? {
        let bytes = Array(data)
        guard let (header, reader0) = PacketHeader.parse(bytes), header.type == .heartbeat else { return nil }
        var reader = reader0
        guard let sessionId = reader.readUInt32LE() else { return nil }
        return Heartbeat(sessionId: sessionId)
    }
}

// MARK: - Disconnect (type 3, either direction)

/// NOTE on a PROTOCOL.md inconsistency (flagged for the coordinator/PC
/// side): the doc's byte table for Disconnect reads:
///   offset 4  size 4  sessionId (uint32)
///   offset 5  size 1  reason (uint8)
/// Offset 5 is inside the 4-byte sessionId field (which occupies bytes
/// 4-7), so the two fields would overlap if taken literally -- that can't
/// be what's intended. Every other packet type in the doc lays fields out
/// back-to-back with no overlap/padding, so we implement `reason` at
/// offset 8 (immediately following the 4-byte sessionId), which is the
/// only reading consistent with the rest of the document and with the
/// struct as anyone would naturally write it in C. This should be
/// double-checked against whatever the PC host actually implements.
struct Disconnect: Equatable {
    var sessionId: UInt32
    var reason: DisconnectReason

    func encode() -> Data {
        var writer = ByteWriter()
        PacketHeader.writeHeader(into: &writer, type: .disconnect)
        writer.writeUInt32LE(sessionId)
        writer.writeUInt8(reason.rawValue)
        return writer.data
    }

    static func decode(_ data: Data) -> Disconnect? {
        let bytes = Array(data)
        guard let (header, reader0) = PacketHeader.parse(bytes), header.type == .disconnect else { return nil }
        var reader = reader0
        guard let sessionId = reader.readUInt32LE(),
              let reasonRaw = reader.readUInt8(),
              let reason = DisconnectReason(rawValue: reasonRaw)
        else { return nil }
        return Disconnect(sessionId: sessionId, reason: reason)
    }
}

// MARK: - VideoFrameFragment (type 10, PC -> iOS, Video port)

struct VideoFrameFragment: Equatable {
    var sessionId: UInt32
    var frameId: UInt32
    var fragmentIndex: UInt16
    var fragmentCount: UInt16
    var ptsMicros: UInt64
    /// bit0 = keyframe, bit1 = lastFragmentOfFrame.
    var flags: UInt8
    var payload: [UInt8]

    var isKeyframe: Bool { flags & 0x1 != 0 }
    var isLastFragment: Bool { flags & 0x2 != 0 }

    static func makeFlags(isKeyframe: Bool, isLastFragment: Bool) -> UInt8 {
        var f: UInt8 = 0
        if isKeyframe { f |= 0x1 }
        if isLastFragment { f |= 0x2 }
        return f
    }

    func encode() -> Data {
        var writer = ByteWriter()
        PacketHeader.writeHeader(into: &writer, type: .videoFrameFragment)
        writer.writeUInt32LE(sessionId)
        writer.writeUInt32LE(frameId)
        writer.writeUInt16LE(fragmentIndex)
        writer.writeUInt16LE(fragmentCount)
        writer.writeUInt64LE(ptsMicros)
        writer.writeUInt8(flags)
        writer.writeUInt16LE(UInt16(clamping: payload.count))
        writer.writeRawBytes(payload)
        return writer.data
    }

    static func decode(_ data: Data) -> VideoFrameFragment? {
        let bytes = Array(data)
        guard let (header, reader0) = PacketHeader.parse(bytes), header.type == .videoFrameFragment else { return nil }
        var reader = reader0
        guard let sessionId = reader.readUInt32LE(),
              let frameId = reader.readUInt32LE(),
              let fragIdx = reader.readUInt16LE(),
              let fragCount = reader.readUInt16LE(),
              let pts = reader.readUInt64LE(),
              let flags = reader.readUInt8(),
              let payloadLen = reader.readUInt16LE(),
              let payload = reader.readBytes(Int(payloadLen))
        else { return nil }
        return VideoFrameFragment(
            sessionId: sessionId,
            frameId: frameId,
            fragmentIndex: fragIdx,
            fragmentCount: fragCount,
            ptsMicros: pts,
            flags: flags,
            payload: payload
        )
    }
}

// MARK: - InputEvent (type 20, iOS -> PC, Input port)

struct InputEvent: Equatable {
    var sessionId: UInt32
    var eventType: InputEventType
    var dx: Float
    var dy: Float
    var keyCode: UInt16

    init(
        sessionId: UInt32,
        eventType: InputEventType,
        dx: Float = 0,
        dy: Float = 0,
        keyCode: UInt16 = 0
    ) {
        self.sessionId = sessionId
        self.eventType = eventType
        self.dx = dx
        self.dy = dy
        self.keyCode = keyCode
    }

    func encode() -> Data {
        var writer = ByteWriter()
        PacketHeader.writeHeader(into: &writer, type: .inputEvent)
        writer.writeUInt32LE(sessionId)
        writer.writeUInt8(eventType.rawValue)
        writer.writeFloat32LE(dx)
        writer.writeFloat32LE(dy)
        writer.writeUInt16LE(keyCode)
        return writer.data
    }

    static func decode(_ data: Data) -> InputEvent? {
        let bytes = Array(data)
        guard let (header, reader0) = PacketHeader.parse(bytes), header.type == .inputEvent else { return nil }
        var reader = reader0
        guard let sessionId = reader.readUInt32LE(),
              let typeRaw = reader.readUInt8(),
              let type = InputEventType(rawValue: typeRaw),
              let dx = reader.readFloat32LE(),
              let dy = reader.readFloat32LE(),
              let keyCode = reader.readUInt16LE()
        else { return nil }
        return InputEvent(sessionId: sessionId, eventType: type, dx: dx, dy: dy, keyCode: keyCode)
    }
}
