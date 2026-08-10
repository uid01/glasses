using System.Buffers.Binary;

namespace PcHost.Protocol;

/// <summary>
/// Heartbeat (type 2, either direction). See shared-protocol/PROTOCOL.md.
/// Total wire size: 8 bytes (4-byte common header + 4-byte sessionId).
/// </summary>
public readonly struct Heartbeat
{
    public const int WireSize = 8;

    public required uint SessionId { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> datagram, out Heartbeat heartbeat)
    {
        heartbeat = default;

        if (!WireHeader.TryRead(datagram, out _, out var type) || type != PacketType.Heartbeat)
        {
            return false;
        }

        if (datagram.Length < WireSize)
        {
            return false;
        }

        heartbeat = new Heartbeat
        {
            SessionId = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(4, 4)),
        };
        return true;
    }

    public int WriteTo(Span<byte> buffer)
    {
        WireHeader.Write(buffer, PacketType.Heartbeat);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4, 4), SessionId);
        return WireSize;
    }
}
