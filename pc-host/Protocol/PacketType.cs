namespace PcHost.Protocol;

/// <summary>
/// PacketType enum values, per shared-protocol/PROTOCOL.md "PacketType" table.
/// </summary>
public enum PacketType : byte
{
    Handshake = 0,
    HandshakeAck = 1,
    Heartbeat = 2,
    Disconnect = 3,
    VideoFrameFragment = 10,
    InputEvent = 20,
}
