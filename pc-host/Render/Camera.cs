using System.Numerics;

namespace PcHost.Render;

/// <summary>
/// The scene's viewpoint. This is the clean interface point live pose data plugs into: whatever
/// drives the camera just sets <see cref="Position"/>/<see cref="Yaw"/>/<see cref="Pitch"/> each
/// frame and the renderer doesn't need to know or care which. Two controllers exist today:
/// <see cref="ConsoleCameraController"/> (manual WASD/arrow-key stand-in) and
/// <see cref="XrealOneOrientationController"/> (real 3DoF orientation from the glasses' own IMU,
/// via <see cref="XrealOneImuClient"/> -- UNVERIFIED against real hardware, see that class's doc
/// comment). Full positional (6DoF) tracking would need the XREAL Eye's SLAM camera instead,
/// which is a separate, harder problem (proprietary, Android-only SDK) not attempted here.
/// </summary>
public sealed class Camera
{
    public Vector3 Position { get; set; } = Vector3.Zero;
    /// <summary>Radians. 0 = looking down +Z.</summary>
    public float Yaw { get; set; }
    /// <summary>Radians. 0 = level, positive = looking up.</summary>
    public float Pitch { get; set; }

    public float FieldOfViewRadians { get; set; } = MathF.PI / 2.2f;
    public float AspectRatio { get; set; } = 16f / 9f;
    public float NearPlane { get; set; } = 0.05f;
    public float FarPlane { get; set; } = 100f;

    public Vector3 Forward
    {
        get
        {
            float cosPitch = MathF.Cos(Pitch);
            return new Vector3(MathF.Sin(Yaw) * cosPitch, MathF.Sin(Pitch), MathF.Cos(Yaw) * cosPitch);
        }
    }

    public Matrix4x4 ViewMatrix => Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitY);

    public Matrix4x4 ProjectionMatrix => Matrix4x4.CreatePerspectiveFieldOfView(FieldOfViewRadians, AspectRatio, NearPlane, FarPlane);
}

/// <summary>
/// Manual keyboard control (WASD to move, arrow keys to look) via non-blocking console key
/// polling -- today's stand-in for live 6DoF pose. Call <see cref="PollAndApply"/> once per
/// render tick; it's a no-op (not blocking) when no key is currently available, so it's safe to
/// call at full frame rate.
/// </summary>
public sealed class ConsoleCameraController
{
    private const float MoveSpeed = 0.05f;
    private const float LookSpeed = 0.03f;

    // Console.KeyAvailable throws InvalidOperationException when there's no interactive
    // console attached to stdin (redirected/absent, e.g. when pc-host is launched as a
    // subprocess by pc-host-gui, or run under a background job). That's the expected
    // deployment shape, not an error, so probe once and permanently stop polling rather
    // than throwing on every render tick -- manual camera control is only available when
    // pc-host is run directly in an interactive terminal.
    private bool _consoleAvailable = true;

    public void PollAndApply(Camera camera)
    {
        if (!_consoleAvailable)
        {
            return;
        }

        bool keyAvailable;
        try
        {
            keyAvailable = Console.KeyAvailable;
        }
        catch (InvalidOperationException)
        {
            _consoleAvailable = false;
            return;
        }

        while (keyAvailable)
        {
            var key = Console.ReadKey(intercept: true).Key;
            Vector3 forward = camera.Forward;
            Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));

            switch (key)
            {
                case ConsoleKey.W: camera.Position += forward * MoveSpeed; break;
                case ConsoleKey.S: camera.Position -= forward * MoveSpeed; break;
                case ConsoleKey.A: camera.Position += right * MoveSpeed; break;
                case ConsoleKey.D: camera.Position -= right * MoveSpeed; break;
                case ConsoleKey.LeftArrow: camera.Yaw -= LookSpeed; break;
                case ConsoleKey.RightArrow: camera.Yaw += LookSpeed; break;
                case ConsoleKey.UpArrow: camera.Pitch = MathF.Min(camera.Pitch + LookSpeed, MathF.PI / 2.1f); break;
                case ConsoleKey.DownArrow: camera.Pitch = MathF.Max(camera.Pitch - LookSpeed, -MathF.PI / 2.1f); break;
            }

            keyAvailable = Console.KeyAvailable;
        }
    }
}

/// <summary>
/// Integrates live gyroscope samples from <see cref="XrealOneImuClient"/> into
/// <see cref="Camera.Yaw"/>/<see cref="Camera.Pitch"/> -- the real-hardware replacement for
/// <see cref="ConsoleCameraController"/>'s manual stand-in, once the glasses are actually
/// connected. Call <see cref="OnSample"/> from the client's per-sample callback.
///
/// Two things flagged clearly because they can't be verified without real hardware:
/// - Pure gyro integration, no accelerometer-based drift correction (no complementary/Madgwick
///   filter pulling orientation back toward a gravity reference). Will drift over time; whether
///   that's noticeable in practice, and whether it's worth the extra complexity to fix, can only
///   be judged once tested for real.
/// - The gyro axis -> yaw/pitch mapping below (X -> pitch, Y -> yaw) is a reasonable guess, not
///   a confirmed fact -- XrealOneImuFrameParser's axis remap is ported from the reference
///   implementation, but nothing here confirms which output axis is actually "turn your head
///   left/right" versus "nod up/down" on this specific device. Expect to flip/swap axes once
///   this is actually running against the glasses.
/// </summary>
public sealed class XrealOneOrientationController
{
    private float _yaw;
    private float _pitch;
    private ulong? _lastTimestampMicros;

    public void OnSample(XrealOneImuFrameParser.Sample sample, Camera camera)
    {
        if (_lastTimestampMicros is ulong lastMicros && sample.TimestampMicros > lastMicros)
        {
            float dt = (sample.TimestampMicros - lastMicros) / 1_000_000f;
            if (dt < 0.5f) // guard against a bogus/huge gap (reconnect, clock discontinuity)
            {
                _yaw += sample.Gyroscope.Y * dt;
                _pitch += sample.Gyroscope.X * dt;
            }
        }
        _lastTimestampMicros = sample.TimestampMicros;

        camera.Yaw = _yaw;
        camera.Pitch = Math.Clamp(_pitch, -MathF.PI / 2.1f, MathF.PI / 2.1f);
    }
}
