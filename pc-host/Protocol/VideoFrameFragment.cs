using System.Buffers.Binary;

namespace PcHost.Protocol;

/// <summary>
/// VideoFrameFragment (type 10, PC -> iOS, Video port). See shared-protocol/PROTOCOL.md.
/// Fixed header portion is 27 bytes (4-byte common header + 23-byte fragment header);
/// total wire size = 27 + PayloadLength.
/// </summary>
public readonly struct VideoFrameFragment
{
    public const int FixedHeaderSize = 27;
    public const int MaxPayloadLength = 1173; // per PROTOCOL.md: keep total datagram <= ~1200 bytes

    [Flags]
    public enum FrameFlags : byte
    {
        None = 0,
        Keyframe = 1 << 0,
        LastFragmentOfFrame = 1 << 1,
    }

    public required uint SessionId { get; init; }
    public required uint FrameId { get; init; }
    public required ushort FragmentIndex { get; init; }
    public required ushort FragmentCount { get; init; }
    public required ulong PtsMicros { get; init; }
    public required FrameFlags Flags { get; init; }
    public required ReadOnlyMemory<byte> Payload { get; init; }

    public int WireSize => FixedHeaderSize + Payload.Length;

    public static bool TryParse(ReadOnlySpan<byte> datagram, out VideoFrameFragment fragment)
    {
        fragment = default;

        if (!WireHeader.TryRead(datagram, out _, out var type) || type != PacketType.VideoFrameFragment)
        {
            return false;
        }

        if (datagram.Length < FixedHeaderSize)
        {
            return false;
        }

        ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(25, 2));
        if (datagram.Length < FixedHeaderSize + payloadLength)
        {
            return false;
        }

        var payload = new byte[payloadLength];
        datagram.Slice(FixedHeaderSize, payloadLength).CopyTo(payload);

        fragment = new VideoFrameFragment
        {
            SessionId = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(4, 4)),
            FrameId = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(8, 4)),
            FragmentIndex = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(12, 2)),
            FragmentCount = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(14, 2)),
            PtsMicros = BinaryPrimitives.ReadUInt64LittleEndian(datagram.Slice(16, 8)),
            Flags = (FrameFlags)datagram[24],
            Payload = payload,
        };
        return true;
    }

    /// <summary>
    /// Writes the fragment into buffer, which must be at least WireSize bytes. Returns the
    /// number of bytes written.
    /// </summary>
    public int WriteTo(Span<byte> buffer)
    {
        WireHeader.Write(buffer, PacketType.VideoFrameFragment);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4, 4), SessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(8, 4), FrameId);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(12, 2), FragmentIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(14, 2), FragmentCount);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(16, 8), PtsMicros);
        buffer[24] = (byte)Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(25, 2), (ushort)Payload.Length);
        Payload.Span.CopyTo(buffer.Slice(FixedHeaderSize, Payload.Length));
        return FixedHeaderSize + Payload.Length;
    }
}
