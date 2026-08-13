namespace RenderProbe;

/// <summary>
/// Generates a curved-monitor mesh: a cylindrical-section panel where every point is equidistant
/// (radius <c>Distance</c>) from the panel's own local origin -- the standard curved-display
/// model (think: viewer sits at a cylinder's central axis, screen is an arc of that cylinder, so
/// every pixel is the same distance from the viewer). Deliberately a LOCAL-space mesh with no
/// knowledge of world placement: "how curved this monitor is" and "where it sits in the scene"
/// are independent, composed later via a separate world transform -- required for the upcoming
/// arbitrary-3D-placement milestone, where curvature must stay a per-monitor property
/// independent of drag-drop position.
///
/// Curvature is expressed as the arc angle (degrees) the panel's full width subtends, matching
/// how a curvature slider should feel to a user (0 = flat, larger = wraps more tightly), rather
/// than as a radius directly. Radius is derived: r = width / angleRadians. Flat (curvature ~ 0)
/// is special-cased to avoid a division blow-up as angle -> 0.
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
            // Flat special case: a plane centered at local origin, facing -Z (toward a camera
            // looking down +Z at it from behind), spanning -width/2..width/2 in X.
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                float x = (t - 0.5f) * width;
                float u = t;
                vertices[i * 2 + 0] = new Vertex { X = x, Y = halfHeight, Z = 0f, U = u, V = 0f };
                vertices[i * 2 + 1] = new Vertex { X = x, Y = -halfHeight, Z = 0f, U = u, V = 1f };
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
                // Centered so the panel's middle (angle=0) sits at local Z=0 -- matching the
                // flat case's convention exactly, which matters because a world transform is
                // just a translation applied on top of this local mesh: if flat's center is at
                // local Z=0 but curved's center were offset by -radius (a bug an earlier version
                // of this had), the same placement translation would put the two meshes at very
                // different depths for "the same" position. Edges curve toward NEGATIVE local Z
                // (i.e. closer to a viewer positioned along -Z from the panel, once translated
                // into world space with the panel ahead of the camera) -- edges pulled toward
                // the viewer, matching how a real curved monitor wraps partway around you rather
                // than bowing away.
                float x = radius * MathF.Sin(angle);
                float z = radius * (MathF.Cos(angle) - 1f);
                float u = t;
                vertices[i * 2 + 0] = new Vertex { X = x, Y = halfHeight, Z = z, U = u, V = 0f };
                vertices[i * 2 + 1] = new Vertex { X = x, Y = -halfHeight, Z = z, U = u, V = 1f };
            }
        }

        var indices = new ushort[segments * 6];
        for (int i = 0; i < segments; i++)
        {
            ushort topLeft = (ushort)(i * 2 + 0);
            ushort bottomLeft = (ushort)(i * 2 + 1);
            ushort topRight = (ushort)((i + 1) * 2 + 0);
            ushort bottomRight = (ushort)((i + 1) * 2 + 1);

            // Two triangles per quad segment, wound so the visible face points toward -Z (the
            // side the "flat" case above faces by construction) for a viewer at local origin
            // looking down -Z.
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
