using System.Buffers.Binary;

namespace PcHost.Protocol;

/// <summary>
/// Disconnect (type 3, either direction). See shared-protocol/PROTOCOL.md.
///
/// NOTE on a protocol-doc discrepancy: PROTOCOL.md's offset table lists
///   offset 4  size 4  sessionId (uint32)
///   offset 5  size 1  reason (uint8)
/// Offset 5 falls *inside* the 4-byte sessionId field (which spans bytes 4-7), so it cannot
/// be taken literally -- no implementation could write both fields at those offsets without
/// clobbering sessionId's second byte. Every other packet in the doc places fields back-to-back
/// with no overlap, so this is almost certainly a typo for offset 8 (immediately after the
/// 4-byte sessionId, matching the pattern used everywhere else in the spec). This
/// implementation uses offset 8. Flagged to the coordinator/protocol owner for a doc fix --
/// if the iOS client instead follows the literal (broken) offset 5, Disconnect parsing will
/// disagree between the two sides. Disconnect is not on the hot path (best-effort teardown
/// notice; the 5s heartbeat timeout is the real backstop), so this is lower risk than a
/// mismatch would be on Handshake/VideoFrameFragment/InputEvent.
///
/// Total wire size used here: 9 bytes (4-byte common header + 4-byte sessionId + 1-byte reason).
/// </summary>
public readonly struct Disconnect
{
    public const int WireSize = 9;
    public const int ReasonOffset = 8;

    public required uint SessionId { get; init; }
    public required byte Reason { get; init; } // 0=user, 1=error, 2=shutdown

    public static bool TryParse(ReadOnlySpan<byte> datagram, out Disconnect disconnect)
    {
        disconnect = default;

        if (!WireHeader.TryRead(datagram, out _, out var type) || type != PacketType.Disconnect)
        {
            return false;
        }

        if (datagram.Length < WireSize)
        {
            return false;
        }

        disconnect = new Disconnect
        {
            SessionId = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(4, 4)),
            Reason = datagram[ReasonOffset],
        };
        return true;
    }

    public int WriteTo(Span<byte> buffer)
    {
        WireHeader.Write(buffer, PacketType.Disconnect);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4, 4), SessionId);
        buffer[ReasonOffset] = Reason;
        return WireSize;
    }
}
