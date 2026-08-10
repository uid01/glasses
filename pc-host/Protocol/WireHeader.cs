namespace PcHost.Protocol;

/// <summary>
/// Common datagram header shared by every packet type, per shared-protocol/PROTOCOL.md
/// "Common datagram header" section.
///
/// offset 0  size 2  magic   = 0xAF51, described in the protocol doc as "big-endian on the
///                              wire": byte 0 = 0xAF, byte 1 = 0x51. Every other multi-byte
///                              field in the protocol is little-endian; only these two magic
///                              bytes are fixed/big-endian.
/// offset 2  size 1  version = 1
/// offset 3  size 1  type    PacketType enum
/// offset 4  ...     payload type-specific
/// </summary>
public static class WireHeader
{
    public const byte MagicByte0 = 0xAF;
    public const byte MagicByte1 = 0x51;
    public const byte CurrentVersion = 1;
    public const int Size = 4;

    /// <summary>
    /// Attempts to read and validate the common header from the start of a datagram.
    /// Per the protocol doc: "Any datagram that doesn't start with the magic bytes or has an
    /// unknown version is dropped silently." This method returns false in exactly that case;
    /// callers should silently discard the datagram rather than logging noisily.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> datagram, out byte version, out PacketType type)
    {
        version = 0;
        type = default;

        if (datagram.Length < Size)
        {
            return false;
        }

        if (datagram[0] != MagicByte0 || datagram[1] != MagicByte1)
        {
            return false;
        }

        byte readVersion = datagram[2];
        if (readVersion != CurrentVersion)
        {
            return false;
        }

        version = readVersion;
        type = (PacketType)datagram[3];
        return true;
    }

    /// <summary>
    /// Writes the 4-byte common header into the start of the buffer. Returns the number of
    /// bytes written (always 4).
    /// </summary>
    public static int Write(Span<byte> buffer, PacketType type)
    {
        buffer[0] = MagicByte0;
        buffer[1] = MagicByte1;
        buffer[2] = CurrentVersion;
        buffer[3] = (byte)type;
        return Size;
    }
}
