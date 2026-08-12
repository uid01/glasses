using System.Numerics;
using PcHost.Render;
using Xunit;

namespace PcHost.Tests;

public class XrealOneImuFrameParserTests
{
    private static readonly byte[] Header = { 0x28, 0x36, 0x00, 0x00, 0x00, 0x80 };
    private static readonly byte[] SensorMarker = { 0x00, 0x40, 0x1f, 0x00, 0x00, 0x40 };
    private const int MessageSize = 84;

    /// <summary>
    /// Builds a synthetic 84-byte frame matching the reference implementation's layout
    /// (github.com/rohitsangwan01/xreal_one_driver): header at 0, sensor marker placed right
    /// after it (its real offset isn't documented -- the reference just searches the whole
    /// message for it), u64 LE timestamp-in-micros*1000 at 14, six f32 LE values (gyro xyz then
    /// accel xyz) at 34.
    /// </summary>
    private static byte[] BuildFrame(ulong timestampMicros, float gx, float gy, float gz, float ax, float ay, float az)
    {
        var frame = new byte[MessageSize];
        Header.CopyTo(frame, 0);
        SensorMarker.CopyTo(frame, 6);
        BitConverter.GetBytes(timestampMicros * 1000).CopyTo(frame, 14);
        BitConverter.GetBytes(gx).CopyTo(frame, 34);
        BitConverter.GetBytes(gy).CopyTo(frame, 38);
        BitConverter.GetBytes(gz).CopyTo(frame, 42);
        BitConverter.GetBytes(ax).CopyTo(frame, 46);
        BitConverter.GetBytes(ay).CopyTo(frame, 50);
        BitConverter.GetBytes(az).CopyTo(frame, 54);
        return frame;
    }

    [Fact]
    public void Feed_ParsesSingleValidFrame_WithAxisRemapAndTimestampScaling()
    {
        // ay carries ~gravity raw so accelerometer magnitude passes the plausibility check.
        var frame = BuildFrame(timestampMicros: 123_456, gx: 0.1f, gy: 0.2f, gz: 0.3f, ax: 0f, ay: 9.81f, az: 0f);

        var parser = new XrealOneImuFrameParser();
        var samples = parser.Feed(frame);

        Assert.Single(samples);
        var sample = samples[0];
        Assert.Equal(123_456ul, sample.TimestampMicros);
        // Reference axis remap: gyro = (-gx, -gz, -gy), accel = (-ax, -az, -ay).
        Assert.Equal(new Vector3(-0.1f, -0.3f, -0.2f), sample.Gyroscope);
        Assert.Equal(new Vector3(0f, 0f, -9.81f), sample.Accelerometer);
    }

    [Fact]
    public void Feed_ReturnsNothing_WhenLessThanOneFullFrameBuffered()
    {
        var frame = BuildFrame(1, 0, 0, 0, 0, 9.81f, 0);
        var parser = new XrealOneImuFrameParser();

        var samples = parser.Feed(frame.AsSpan(0, MessageSize - 10).ToArray());

        Assert.Empty(samples);
    }

    [Fact]
    public void Feed_ParsesFrameSplitAcrossMultipleCalls()
    {
        var frame = BuildFrame(999, 0.5f, -0.5f, 0f, 0f, 9.81f, 0f);
        var parser = new XrealOneImuFrameParser();

        var first = parser.Feed(frame.AsSpan(0, 40).ToArray());
        Assert.Empty(first);
        var second = parser.Feed(frame.AsSpan(40, MessageSize - 40).ToArray());

        Assert.Single(second);
        Assert.Equal(999ul, second[0].TimestampMicros);
    }

    [Fact]
    public void Feed_SkipsGarbageBytesBeforeHeader()
    {
        var junk = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11, 0x22 };
        var frame = BuildFrame(42, 0, 0, 0, 0, 9.81f, 0);
        var parser = new XrealOneImuFrameParser();

        var samples = parser.Feed(junk.Concat(frame).ToArray());

        Assert.Single(samples);
        Assert.Equal(42ul, samples[0].TimestampMicros);
    }

    [Fact]
    public void Feed_ParsesTwoConsecutiveFramesInOneChunk()
    {
        var frame1 = BuildFrame(1, 0, 0, 0, 0, 9.81f, 0);
        var frame2 = BuildFrame(2, 0, 0, 0, 0, 9.81f, 0);
        var parser = new XrealOneImuFrameParser();

        var samples = parser.Feed(frame1.Concat(frame2).ToArray());

        Assert.Equal(2, samples.Count);
        Assert.Equal(1ul, samples[0].TimestampMicros);
        Assert.Equal(2ul, samples[1].TimestampMicros);
    }

    [Fact]
    public void Feed_RejectsFrameWithHeaderButNoSensorMarker_AndRecoversOnNextValidFrame()
    {
        var bogus = new byte[MessageSize];
        Header.CopyTo(bogus, 0);
        // No sensor marker anywhere in this one -- some other, unrelated message type that
        // happens to share the same 6-byte header prefix.
        var validFrame = BuildFrame(7, 0, 0, 0, 0, 9.81f, 0);

        var parser = new XrealOneImuFrameParser();
        var samples = parser.Feed(bogus.Concat(validFrame).ToArray());

        Assert.Single(samples);
        Assert.Equal(7ul, samples[0].TimestampMicros);
    }

    [Fact]
    public void Feed_RejectsImplausibleAccelerometerMagnitude()
    {
        // ay=0 here means accelerometer magnitude is ~0 -- nowhere near gravity, so this frame
        // should be discarded as implausible rather than reported as a real sample.
        var frame = BuildFrame(1, 0, 0, 0, 0, 0, 0);
        var parser = new XrealOneImuFrameParser();

        var samples = parser.Feed(frame);

        Assert.Empty(samples);
    }

    [Fact]
    public void Feed_EmptyChunkYieldsNoSamples()
    {
        var parser = new XrealOneImuFrameParser();
        var samples = parser.Feed(ReadOnlySpan<byte>.Empty);
        Assert.Empty(samples);
    }
}
