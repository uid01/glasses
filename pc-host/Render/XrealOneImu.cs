using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PcHost.Render;

/// <summary>
/// Parses the XREAL One glasses' IMU stream frame format. Reached over a plain TCP socket to
/// 169.254.2.1:52998 -- the glasses present as a USB Ethernet-class network interface once
/// connected over USB-C, not a raw HID accessory, per the open-source reverse-engineering
/// community (github.com/rohitsangwan01/xreal_one_driver, github.com/Skarian/one-xr). XREAL
/// themselves haven't documented this anywhere.
///
/// UNVERIFIED AGAINST REAL HARDWARE: this PC has no physical connection to the glasses yet
/// (blocked on a USB-C adapter cable), so the byte offsets, validation thresholds, and axis
/// sign-flips below are ported faithfully from rohitsangwan01/xreal_one_driver's Rust
/// implementation, not independently confirmed here. Deliberately NOT "improved" against the
/// reference (e.g. no bound on how much unparseable data can accumulate before a header shows
/// up) so behavior stays faithful to what's actually been shown to work against a real device,
/// pending the same test here once the cable is in hand. Unit tests lock down parsing against
/// synthetic frames matching that reference -- the only verification possible without hardware.
/// </summary>
public sealed class XrealOneImuFrameParser
{
    private static readonly byte[] Header = { 0x28, 0x36, 0x00, 0x00, 0x00, 0x80 };
    private static readonly byte[] SensorMarker = { 0x00, 0x40, 0x1f, 0x00, 0x00, 0x40 };
    private const int MinMessageSize = 84;

    private readonly List<byte> _buf = new();

    public readonly record struct Sample(Vector3 Gyroscope, Vector3 Accelerometer, ulong TimestampMicros);

    /// <summary>
    /// Feeds a newly-read chunk of bytes from the TCP stream and returns every complete,
    /// validated IMU sample found in it (usually zero or one per call, but a chunk can contain
    /// several if reads lag behind the glasses' actual sample rate).
    /// </summary>
    public List<Sample> Feed(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length > 0)
        {
            _buf.AddRange(chunk.ToArray());
        }

        var samples = new List<Sample>();
        while (true)
        {
            var bufSpan = CollectionsMarshal.AsSpan(_buf);
            int headerIndex = bufSpan.IndexOf(Header);
            if (headerIndex < 0)
            {
                return samples;
            }
            if (headerIndex > 0)
            {
                _buf.RemoveRange(0, headerIndex);
                bufSpan = CollectionsMarshal.AsSpan(_buf);
            }

            if (_buf.Count < MinMessageSize)
            {
                return samples;
            }

            var candidate = bufSpan[..MinMessageSize];
            if (candidate.IndexOf(SensorMarker) < 0)
            {
                _buf.RemoveRange(0, Header.Length);
                continue;
            }

            if (TryDecode(candidate, out var sample))
            {
                samples.Add(sample);
                _buf.RemoveRange(0, MinMessageSize);
            }
            else
            {
                _buf.RemoveRange(0, Header.Length);
            }
        }
    }

    private static bool TryDecode(ReadOnlySpan<byte> data, out Sample sample)
    {
        sample = default;

        ulong timestampMicros = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(14, 8)) / 1000;

        float gx = BitConverter.ToSingle(data.Slice(34, 4));
        float gy = BitConverter.ToSingle(data.Slice(38, 4));
        float gz = BitConverter.ToSingle(data.Slice(42, 4));
        float ax = BitConverter.ToSingle(data.Slice(46, 4));
        float ay = BitConverter.ToSingle(data.Slice(50, 4));
        float az = BitConverter.ToSingle(data.Slice(54, 4));

        if (!IsFinite(gx) || !IsFinite(gy) || !IsFinite(gz) || !IsFinite(ax) || !IsFinite(ay) || !IsFinite(az))
        {
            return false;
        }

        const float maxGyro = 1000f;
        const float maxAccel = 100f;
        if (MathF.Abs(gx) > maxGyro || MathF.Abs(gy) > maxGyro || MathF.Abs(gz) > maxGyro)
        {
            return false;
        }
        if (MathF.Abs(ax) > maxAccel || MathF.Abs(ay) > maxAccel || MathF.Abs(az) > maxAccel)
        {
            return false;
        }

        // Axis remap + sign flip matches the reference implementation exactly; the resulting
        // mapping to yaw/pitch/roll is NOT independently confirmed (see class doc) and may need
        // adjustment once tested against the real device.
        var gyro = new Vector3(-gx, -gz, -gy);
        var accel = new Vector3(-ax, -az, -ay);

        if (gyro.Length() < 1e-6f && accel.Length() < 1e-6f)
        {
            return false; // suspicious all-zero reading
        }

        float accelMagnitude = accel.Length();
        if (accelMagnitude < 5f || accelMagnitude > 15f)
        {
            return false; // should read close to 9.81 m/s^2 (gravity) when roughly stationary
        }

        sample = new Sample(gyro, accel, timestampMicros);
        return true;
    }

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}
