using PcHost.Protocol;
using Xunit;

namespace PcHost.Tests;

public class PacketSerializationTests
{
    [Fact]
    public void WireHeader_RoundTrips_MagicVersionType()
    {
        Span<byte> buffer = stackalloc byte[4];
        int written = WireHeader.Write(buffer, PacketType.Heartbeat);

        Assert.Equal(4, written);
        Assert.Equal(0xAF, buffer[0]);
        Assert.Equal(0x51, buffer[1]);
        Assert.Equal(1, buffer[2]);
        Assert.Equal((byte)PacketType.Heartbeat, buffer[3]);

        Assert.True(WireHeader.TryRead(buffer, out byte version, out var type));
        Assert.Equal(1, version);
        Assert.Equal(PacketType.Heartbeat, type);
    }

    [Fact]
    public void WireHeader_RejectsBadMagic()
    {
        byte[] buffer = { 0x00, 0x00, 1, (byte)PacketType.Heartbeat };
        Assert.False(WireHeader.TryRead(buffer, out _, out _));
    }

    [Fact]
    public void WireHeader_RejectsUnknownVersion()
    {
        byte[] buffer = { 0xAF, 0x51, 2, (byte)PacketType.Heartbeat };
        Assert.False(WireHeader.TryRead(buffer, out _, out _));
    }

    [Fact]
    public void WireHeader_RejectsTooShortDatagram()
    {
        byte[] buffer = { 0xAF, 0x51 };
        Assert.False(WireHeader.TryRead(buffer, out _, out _));
    }

    [Fact]
    public void Handshake_RoundTrips()
    {
        var nonce = new byte[16];
        Random.Shared.NextBytes(nonce);

        var original = new Handshake
        {
            ClientProtocolVersion = 1,
            DesiredWidth = 1920,
            DesiredHeight = 1080,
            DesiredFps = 60,
            CodecMask = 0b11,
            SessionNonce = nonce,
        };

        Span<byte> buffer = stackalloc byte[Handshake.WireSize];
        int written = original.WriteTo(buffer);
        Assert.Equal(Handshake.WireSize, written);
        Assert.Equal(28, Handshake.WireSize);

        Assert.True(Handshake.TryParse(buffer, out var parsed));
        Assert.Equal(original.ClientProtocolVersion, parsed.ClientProtocolVersion);
        Assert.Equal(original.DesiredWidth, parsed.DesiredWidth);
        Assert.Equal(original.DesiredHeight, parsed.DesiredHeight);
        Assert.Equal(original.DesiredFps, parsed.DesiredFps);
        Assert.Equal(original.CodecMask, parsed.CodecMask);
        Assert.Equal(original.SessionNonce, parsed.SessionNonce);
    }

    [Fact]
    public void Handshake_RejectsWrongPacketType()
    {
        Span<byte> buffer = stackalloc byte[Handshake.WireSize];
        WireHeader.Write(buffer, PacketType.Heartbeat);
        Assert.False(Handshake.TryParse(buffer, out _));
    }

    [Fact]
    public void HandshakeAck_RoundTrips()
    {
        var nonce = new byte[16];
        Random.Shared.NextBytes(nonce);

        var original = new HandshakeAck
        {
            ServerProtocolVersion = 1,
            ChosenWidth = 1920,
            ChosenHeight = 1200,
            ChosenFps = 60,
            ChosenCodec = 0,
            SessionNonce = nonce,
            SessionId = 0xDEADBEEF,
        };

        Span<byte> buffer = stackalloc byte[HandshakeAck.WireSize];
        int written = original.WriteTo(buffer);
        Assert.Equal(HandshakeAck.WireSize, written);
        Assert.Equal(32, HandshakeAck.WireSize);

        Assert.True(HandshakeAck.TryParse(buffer, out var parsed));
        Assert.Equal(original.ServerProtocolVersion, parsed.ServerProtocolVersion);
        Assert.Equal(original.ChosenWidth, parsed.ChosenWidth);
        Assert.Equal(original.ChosenHeight, parsed.ChosenHeight);
        Assert.Equal(original.ChosenFps, parsed.ChosenFps);
        Assert.Equal(original.ChosenCodec, parsed.ChosenCodec);
        Assert.Equal(original.SessionNonce, parsed.SessionNonce);
        Assert.Equal(original.SessionId, parsed.SessionId);
    }

    [Fact]
    public void Heartbeat_RoundTrips()
    {
        var original = new Heartbeat { SessionId = 42 };

        Span<byte> buffer = stackalloc byte[Heartbeat.WireSize];
        int written = original.WriteTo(buffer);
        Assert.Equal(8, written);

        Assert.True(Heartbeat.TryParse(buffer, out var parsed));
        Assert.Equal(42u, parsed.SessionId);
    }

    [Fact]
    public void Disconnect_RoundTrips()
    {
        var original = new Disconnect { SessionId = 123, Reason = 2 };

        Span<byte> buffer = stackalloc byte[Disconnect.WireSize];
        int written = original.WriteTo(buffer);
        Assert.Equal(9, written);

        Assert.True(Disconnect.TryParse(buffer, out var parsed));
        Assert.Equal(123u, parsed.SessionId);
        Assert.Equal((byte)2, parsed.Reason);
    }

    [Fact]
    public void VideoFrameFragment_RoundTrips_WithPayload()
    {
        byte[] payload = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();

        var original = new VideoFrameFragment
        {
            SessionId = 7,
            FrameId = 99,
            FragmentIndex = 1,
            FragmentCount = 3,
            PtsMicros = 123_456_789UL,
            Flags = VideoFrameFragment.FrameFlags.Keyframe | VideoFrameFragment.FrameFlags.LastFragmentOfFrame,
            Payload = payload,
        };

        Assert.Equal(27 + 500, original.WireSize);

        var buffer = new byte[original.WireSize];
        int written = original.WriteTo(buffer);
        Assert.Equal(original.WireSize, written);

        Assert.True(VideoFrameFragment.TryParse(buffer, out var parsed));
        Assert.Equal(original.SessionId, parsed.SessionId);
        Assert.Equal(original.FrameId, parsed.FrameId);
        Assert.Equal(original.FragmentIndex, parsed.FragmentIndex);
        Assert.Equal(original.FragmentCount, parsed.FragmentCount);
        Assert.Equal(original.PtsMicros, parsed.PtsMicros);
        Assert.Equal(original.Flags, parsed.Flags);
        Assert.Equal(payload, parsed.Payload.ToArray());
    }

    [Fact]
    public void VideoFrameFragment_RoundTrips_EmptyPayload()
    {
        var original = new VideoFrameFragment
        {
            SessionId = 1,
            FrameId = 1,
            FragmentIndex = 0,
            FragmentCount = 1,
            PtsMicros = 0,
            Flags = VideoFrameFragment.FrameFlags.None,
            Payload = Array.Empty<byte>(),
        };

        var buffer = new byte[original.WireSize];
        original.WriteTo(buffer);

        Assert.True(VideoFrameFragment.TryParse(buffer, out var parsed));
        Assert.Empty(parsed.Payload.ToArray());
    }

    [Fact]
    public void VideoFrameFragment_MaxPayloadKeepsDatagramUnderTarget()
    {
        // PROTOCOL.md: fragment payload <= ~1173 bytes so total datagram stays <= ~1200 bytes.
        int totalDatagram = VideoFrameFragment.FixedHeaderSize + VideoFrameFragment.MaxPayloadLength;
        Assert.True(totalDatagram <= 1200, $"datagram size {totalDatagram} exceeds 1200-byte target");
    }

    [Theory]
    [InlineData(PcHost.Protocol.InputEventType.MouseMove, 12.5f, -3.25f, 0)]
    [InlineData(PcHost.Protocol.InputEventType.Scroll, 0f, 120f, 0)]
    [InlineData(PcHost.Protocol.InputEventType.KeyDown, 0f, 0f, 65)]
    [InlineData(PcHost.Protocol.InputEventType.KeyUp, 0f, 0f, 13)]
    public void InputEvent_RoundTrips(InputEventType eventType, float dx, float dy, ushort keyCode)
    {
        var original = new InputEvent
        {
            SessionId = 55,
            EventType = eventType,
            Dx = dx,
            Dy = dy,
            KeyCode = keyCode,
        };

        Span<byte> buffer = stackalloc byte[InputEvent.WireSize];
        int written = original.WriteTo(buffer);
        Assert.Equal(19, written);

        Assert.True(InputEvent.TryParse(buffer, out var parsed));
        Assert.Equal(original.SessionId, parsed.SessionId);
        Assert.Equal(original.EventType, parsed.EventType);
        Assert.Equal(original.Dx, parsed.Dx);
        Assert.Equal(original.Dy, parsed.Dy);
        Assert.Equal(original.KeyCode, parsed.KeyCode);
    }

    [Fact]
    public void InputEvent_RejectsTruncatedDatagram()
    {
        Span<byte> buffer = stackalloc byte[InputEvent.WireSize];
        new InputEvent { SessionId = 1, EventType = InputEventType.LeftClick, Dx = 0, Dy = 0, KeyCode = 0 }.WriteTo(buffer);

        Assert.False(InputEvent.TryParse(buffer[..10], out _));
    }
}
