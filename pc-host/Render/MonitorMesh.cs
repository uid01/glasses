namespace PcHost.Render;

/// <summary>
/// Generates a curved-monitor mesh: a cylindrical-section panel where every point is equidistant
/// (radius derived from width/curvature) from the panel's own local center -- the standard
/// curved-display model (viewer at a cylinder's central axis, screen is an arc of that cylinder).
/// Deliberately a LOCAL-space mesh with no knowledge of world placement: curvature and world
/// position are independent, composed via a separate world transform in <see cref="SceneObject"/>
/// -- required so arbitrary drag-drop 3D placement doesn't disturb how curved an individual
/// monitor looks.
///
/// Curvature is the arc angle (degrees) the panel's full width subtends (0 = flat, larger =
/// wraps more tightly) rather than a radius directly, since that's what a curvature slider
/// should feel like to a user. Radius is derived: r = width / angleRadians.
///
/// Validated first in tools/render-probe against real captured desktop content before being
/// ported here -- notably, an earlier version of this class centered the curved mesh's arc at
/// local Z=-radius while the flat case centers at Z=0, which meant "the same" world placement
/// put a curved and a flat panel at very different depths. Fixed by centering both at local Z=0
/// (the panel's midpoint, angle=0, is always local Z=0; edges curve toward negative local Z --
/// closer to a viewer positioned along -Z from the panel -- which also reads as "wrapping
/// partway around you" rather than bowing away, matching how real curved monitors read).
/// </summary>
public static class MonitorMesh
{
    public const float FlatCurvatureEpsilonDegrees = 0.01f;

    public struct Vertex
    {
        public float X, Y, Z;
        public float U, V;
    }

    /// <param name="width">Panel width in world units.</param>
    /// <param name="height">Panel height in world units.</param>
    /// <param name="curvatureDegrees">Arc angle the full width subtends. 0 = flat.</param>
    /// <param name="segments">Horizontal tessellation -- more segments = smoother curve.</param>
    public static (Vertex[] Vertices, ushort[] Indices) Generate(float width, float height, float curvatureDegrees, int segments = 32)
    {
        if (segments < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(segments), segments, "Need at least 1 segment.");
        }

        var vertices = new Vertex[(segments + 1) * 2];
        float halfHeight = height / 2f;

        if (MathF.Abs(curvatureDegrees) < FlatCurvatureEpsilonDegrees)
        {
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                float x = (t - 0.5f) * width;
                vertices[i * 2 + 0] = new Vertex { X = x, Y = halfHeight, Z = 0f, U = t, V = 0f };
                vertices[i * 2 + 1] = new Vertex { X = x, Y = -halfHeight, Z = 0f, U = t, V = 1f };
            }
        }
        else
        {
            float angleRadians = curvatureDegrees * MathF.PI / 180f;
            float radius = width / angleRadians;

            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                float angle = (t - 0.5f) * angleRadians;
                float x = radius * MathF.Sin(angle);
                float z = radius * (MathF.Cos(angle) - 1f);
                vertices[i * 2 + 0] = new Vertex { X = x, Y = halfHeight, Z = z, U = t, V = 0f };
                vertices[i * 2 + 1] = new Vertex { X = x, Y = -halfHeight, Z = z, U = t, V = 1f };
            }
        }

        var indices = new ushort[segments * 6];
        for (int i = 0; i < segments; i++)
        {
            ushort topLeft = (ushort)(i * 2 + 0);
            ushort bottomLeft = (ushort)(i * 2 + 1);
            ushort topRight = (ushort)((i + 1) * 2 + 0);
            ushort bottomRight = (ushort)((i + 1) * 2 + 1);

            indices[i * 6 + 0] = topLeft;
            indices[i * 6 + 1] = topRight;
            indices[i * 6 + 2] = bottomLeft;
            indices[i * 6 + 3] = bottomLeft;
            indices[i * 6 + 4] = topRight;
            indices[i * 6 + 5] = bottomRight;
        }

        return (vertices, indices);
    }
}
