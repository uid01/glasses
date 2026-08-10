using System.Buffers.Binary;

namespace PcHost.Protocol;

/// <summary>
/// InputEvent (type 20, iOS -> PC, Input port). See shared-protocol/PROTOCOL.md.
/// Total wire size: 19 bytes (4-byte common header + 15-byte fixed payload), regardless of
/// eventType -- fields not used by a given event type are still present on the wire and
/// simply ignored by the receiver.
/// </summary>
public readonly struct InputEvent
{
    public const int WireSize = 19;

    public required uint SessionId { get; init; }
    public required InputEventType EventType { get; init; }
    public required float Dx { get; init; }
    public required float Dy { get; init; }
    public required ushort KeyCode { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> datagram, out InputEvent inputEvent)
    {
        inputEvent = default;

        if (!WireHeader.TryRead(datagram, out _, out var type) || type != PacketType.InputEvent)
        {
            return false;
        }

        if (datagram.Length < WireSize)
        {
            return false;
        }

        inputEvent = new InputEvent
        {
            SessionId = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(4, 4)),
            EventType = (InputEventType)datagram[8],
            Dx = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(datagram.Slice(9, 4))),
            Dy = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(datagram.Slice(13, 4))),
            KeyCode = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(17, 2)),
        };
        return true;
    }

    public int WriteTo(Span<byte> buffer)
    {
        WireHeader.Write(buffer, PacketType.InputEvent);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(4, 4), SessionId);
        buffer[8] = (byte)EventType;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(9, 4), BitConverter.SingleToInt32Bits(Dx));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(13, 4), BitConverter.SingleToInt32Bits(Dy));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(17, 2), KeyCode);
        return WireSize;
    }
}
