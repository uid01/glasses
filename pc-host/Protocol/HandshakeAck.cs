using System.Buffers.Binary;

namespace PcHost.Protocol;

/// <summary>
/// HandshakeAck (type 1, PC -> iOS, sent to the source port of the Handshake).
/// See shared-protocol/PROTOCOL.md. Total wire size: 32 bytes (4-byte common header
/// + 28-byte payload).
/// </summary>
public readonly struct HandshakeAck
{
    public const int WireSize = 32;
    public const int SessionNonceLength = 16;

    public required ushort ServerProtocolVersion { get; init; }
    public required ushort ChosenWidth { get; init; }
    public required ushort ChosenHeight { get; init; }
    public required byte ChosenFps { get; init; }
    public required byte ChosenCodec { get; init; } // 0=H264, 1=HEVC
    public required byte[] SessionNonce { get; init; } // 16 bytes, echoed from Handshake
    public required uint SessionId { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> datagram, out HandshakeAck ack)
    {
        ack = default;

        if (!WireHeader.TryRead(datagram, out _, out var type) || type != PacketType.HandshakeAck)
        {
            return false;
        }

        if (datagram.Length < WireSize)
        {
            return false;
        }

        var nonce = new byte[SessionNonceLength];
        datagram.Slice(12, SessionNonceLength).CopyTo(nonce);

        ack = new HandshakeAck
        {
            ServerProtocolVersion = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(4, 2)),
            ChosenWidth = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(6, 2)),
            ChosenHeight = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(8, 2)),
            ChosenFps = datagram[10],
            ChosenCodec = datagram[11],
            SessionNonce = nonce,
            SessionId = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(28, 4)),
        };
        return true;
    }

    public int WriteTo(Span<byte> buffer)
    {
        WireHeader.Write(buffer, PacketType.HandshakeAck);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(4, 2), ServerProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(6, 2), ChosenWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(8, 2), ChosenHeight);
        buffer[10] = ChosenFps;
        buffer[11] = ChosenCodec;
        (SessionNonce ?? new byte[SessionNonceLength]).AsSpan(0, SessionNonceLength).CopyTo(buffer.Slice(12, SessionNonceLength));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(28, 4), SessionId);
        return WireSize;
    }
}
