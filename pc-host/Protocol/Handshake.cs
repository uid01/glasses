using System.Buffers.Binary;

namespace PcHost.Protocol;

/// <summary>
/// Handshake (type 0, iOS -> PC, Control port). See shared-protocol/PROTOCOL.md.
/// Total wire size: 28 bytes (4-byte common header + 24-byte payload).
/// </summary>
public readonly struct Handshake
{
    public const int WireSize = 28;
    public const int SessionNonceLength = 16;

    public required ushort ClientProtocolVersion { get; init; }
    public required ushort DesiredWidth { get; init; }
    public required ushort DesiredHeight { get; init; }
    public required byte DesiredFps { get; init; }
    public required byte CodecMask { get; init; }
    public required byte[] SessionNonce { get; init; } // 16 bytes

    public static bool TryParse(ReadOnlySpan<byte> datagram, out Handshake handshake)
    {
        handshake = default;

        if (!WireHeader.TryRead(datagram, out _, out var type) || type != PacketType.Handshake)
        {
            return false;
        }

        if (datagram.Length < WireSize)
        {
            return false;
        }

        var nonce = new byte[SessionNonceLength];
        datagram.Slice(12, SessionNonceLength).CopyTo(nonce);

        handshake = new Handshake
        {
            ClientProtocolVersion = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(4, 2)),
            DesiredWidth = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(6, 2)),
            DesiredHeight = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(8, 2)),
            DesiredFps = datagram[10],
            CodecMask = datagram[11],
            SessionNonce = nonce,
        };
        return true;
    }

    public int WriteTo(Span<byte> buffer)
    {
        WireHeader.Write(buffer, PacketType.Handshake);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(4, 2), ClientProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(6, 2), DesiredWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(8, 2), DesiredHeight);
        buffer[10] = DesiredFps;
        buffer[11] = CodecMask;
        (SessionNonce ?? new byte[SessionNonceLength]).AsSpan(0, SessionNonceLength).CopyTo(buffer.Slice(12, SessionNonceLength));
        return WireSize;
    }
}
