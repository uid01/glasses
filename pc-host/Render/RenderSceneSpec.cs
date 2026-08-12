using System.Numerics;

namespace PcHost.Render;

/// <summary>
/// Describes a scene to build (which real/virtual monitors, how curved, where placed) without
/// holding any live D3D/capture resources -- those get constructed per-session in
/// <see cref="CapturePipelineExtensions"/> when a session's pipeline actually starts, and
/// disposed on teardown, mirroring how each session already gets its own ffmpeg process today.
/// Deliberately minimal for now (see pc-host-gui's forthcoming drag-drop 3D placement builder
/// for how a real user-facing config format will eventually populate this) -- today it's built
/// directly from a couple of CLI flags, just enough to verify the rendered path produces a
/// correct single-monitor stream (the "parity milestone").
/// </summary>
public sealed class RenderSceneSpec
{
    public required IReadOnlyList<RenderObjectSpec> Objects { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
}

public sealed class RenderObjectSpec
{
    public required int OutputIndex { get; init; }
    public required float PanelWidth { get; init; }
    public required float PanelHeight { get; init; }
    public float CurvatureDegrees { get; init; }
    public Vector3 Position { get; init; }
    public Vector3 RotationEuler { get; init; }
}
