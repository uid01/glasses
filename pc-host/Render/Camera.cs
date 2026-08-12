using System.Numerics;

namespace PcHost.Render;

/// <summary>
/// The scene's viewpoint. This is the clean interface point real 6DoF pose data (from the
/// XREAL Eye, once its USB HID protocol is worked out -- blocked for now on a physical adapter
/// cable) plugs into later: whatever drives the camera (manual keyboard control today, live pose
/// data eventually) just sets <see cref="Position"/>/<see cref="Yaw"/>/<see cref="Pitch"/> each
/// frame and the renderer doesn't need to know or care which. <see cref="ConsoleCameraController"/>
/// is today's manual stand-in.
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
