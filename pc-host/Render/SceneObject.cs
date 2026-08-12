using System.Numerics;
using Vortice.Direct3D11;

namespace PcHost.Render;

/// <summary>
/// One placed monitor panel in the scene: its own curved/flat mesh (see
/// <see cref="MonitorMesh"/>), a world transform (position + yaw/pitch/roll), and the
/// <see cref="MonitorCapture"/> supplying its live texture. Deliberately NOT tied to a grid --
/// position/rotation are arbitrary, independent floats, matching the "arbitrary 3D placement"
/// requirement rather than the earlier rows x columns model (pc-host/Capture/MonitorLayout.cs)
/// this supersedes for the rendered path.
/// </summary>
public sealed class SceneObject : IDisposable
{
    public required MonitorCapture Capture { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
    public required float CurvatureDegrees { get; init; }

    public Vector3 Position { get; set; }
    /// <summary>Yaw/pitch/roll in radians.</summary>
    public Vector3 RotationEuler { get; set; }

    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private int _indexCount;

    public void EnsureMeshBuilt(ID3D11Device device)
    {
        if (_vertexBuffer is not null)
        {
            return;
        }

        var (vertices, indices) = MonitorMesh.Generate(Width, Height, CurvatureDegrees);
        _vertexBuffer = device.CreateBuffer(vertices.AsSpan(), new BufferDescription
        {
            Usage = ResourceUsage.Immutable,
            ByteWidth = (uint)(vertices.Length * 5 * sizeof(float)),
            BindFlags = BindFlags.VertexBuffer,
        });
        _indexBuffer = device.CreateBuffer(indices.AsSpan(), new BufferDescription
        {
            Usage = ResourceUsage.Immutable,
            ByteWidth = (uint)(indices.Length * sizeof(ushort)),
            BindFlags = BindFlags.IndexBuffer,
        });
        _indexCount = indices.Length;
    }

    public Matrix4x4 WorldMatrix =>
        Matrix4x4.CreateRotationX(RotationEuler.X) *
        Matrix4x4.CreateRotationY(RotationEuler.Y) *
        Matrix4x4.CreateRotationZ(RotationEuler.Z) *
        Matrix4x4.CreateTranslation(Position);

    public ID3D11Buffer VertexBuffer => _vertexBuffer ?? throw new InvalidOperationException("Call EnsureMeshBuilt first.");
    public ID3D11Buffer IndexBuffer => _indexBuffer ?? throw new InvalidOperationException("Call EnsureMeshBuilt first.");
    public int IndexCount => _indexCount;

    public void Dispose()
    {
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        Capture.Dispose();
    }
}
