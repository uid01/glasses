using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace PcHost.Render;

/// <summary>
/// JSON on-disk shape for a <see cref="RenderSceneSpec"/>, loaded via <c>--scene-file</c>.
/// Exists because the CLI-flag surface (<c>--render-monitor</c>/<c>--render-width</c>/etc, see
/// Program.cs) only ever describes a single hardcoded panel -- pc-host-gui's drag-drop 3D
/// placement builder needs to hand over an arbitrary number of objects, each with its own
/// position/rotation/curvature/size, which doesn't fit on a command line. Rotation is stored in
/// degrees here (human/GUI-friendly) and converted to the radians <see cref="RenderObjectSpec"/>
/// actually uses at load time.
/// </summary>
public sealed class SceneFileDto
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public List<SceneObjectFileDto> Objects { get; set; } = new();
}

public sealed class SceneObjectFileDto
{
    public int OutputIndex { get; set; }
    public float PanelWidth { get; set; } = 1.78f;
    public float PanelHeight { get; set; } = 1.0f;
    public float CurvatureDegrees { get; set; }
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; } = 1.6f;
    public float YawDegrees { get; set; }
    public float PitchDegrees { get; set; }
    public float RollDegrees { get; set; }
}

public static class SceneFileLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RenderSceneSpec Load(string path)
    {
        string json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<SceneFileDto>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Scene file '{path}' parsed to null.");

        if (dto.Objects.Count == 0)
        {
            throw new InvalidOperationException($"Scene file '{path}' has no objects -- nothing to render.");
        }

        return new RenderSceneSpec
        {
            Width = dto.Width,
            Height = dto.Height,
            Objects = dto.Objects.Select(o => new RenderObjectSpec
            {
                OutputIndex = o.OutputIndex,
                PanelWidth = o.PanelWidth,
                PanelHeight = o.PanelHeight,
                CurvatureDegrees = o.CurvatureDegrees,
                Position = new Vector3(o.PosX, o.PosY, o.PosZ),
                RotationEuler = new Vector3(
                    o.PitchDegrees * MathF.PI / 180f,
                    o.YawDegrees * MathF.PI / 180f,
                    o.RollDegrees * MathF.PI / 180f),
            }).ToList(),
        };
    }
}
