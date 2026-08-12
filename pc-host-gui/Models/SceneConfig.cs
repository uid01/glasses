using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PcHostGui.Models;

/// <summary>
/// An arbitrary set of monitors placed freely in 3D (position, yaw/pitch/roll, curvature) --
/// what the drag-drop scene builder edits, as opposed to <see cref="GridConfig"/>'s fixed
/// rows x columns grid. Mirrors pc-host's <c>RenderSceneSpec</c>/<c>RenderObjectSpec</c>
/// (pc-host/Render/RenderSceneSpec.cs) but keeps <see cref="SceneObjectConfig.OutputIndex"/>
/// nullable, same reasoning as <see cref="GridConfig"/>'s nullable cells: this side needs to
/// represent "not yet assigned a monitor" mid-edit, which pc-host's model has no reason to.
/// Serialized to the JSON shape pc-host's <c>--scene-file</c> flag reads
/// (pc-host/Render/SceneFileFormat.cs); rotation stays in degrees end-to-end on this side for
/// human-friendliness, matching that file format (pc-host converts to radians on load).
/// </summary>
public sealed class SceneConfig
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public List<SceneObjectConfig> Objects { get; set; } = new();

    public bool IsComplete => Objects.Count > 0 && Objects.All(o => o.OutputIndex.HasValue);

    /// <summary>
    /// A new panel placed a bit further back and to the side of whatever's already there, so
    /// repeated "+ Add Monitor" clicks fan panels out left-to-right instead of stacking them on
    /// top of each other. Purely a starting point -- the user drags/edits from there.
    /// </summary>
    public SceneObjectConfig AddObject()
    {
        float x = Objects.Count == 0 ? 0f : (Objects.Count % 2 == 0 ? -1f : 1f) * (0.9f * ((Objects.Count + 1) / 2));
        var obj = new SceneObjectConfig
        {
            PosX = x,
            PosZ = 1.8f,
        };
        Objects.Add(obj);
        return obj;
    }

    /// <summary>
    /// Builds pc-host's <c>--scene-file</c> JSON. Throws if any object still lacks an assigned
    /// monitor -- callers should check <see cref="IsComplete"/> first and surface that to the
    /// user rather than relying on this exception for UI flow (same pattern as
    /// <see cref="GridConfig.ToGridSpec"/>).
    /// </summary>
    public string ToSceneFileJson()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException("Every monitor in the scene needs a source assigned before the bridge can start.");
        }

        var dto = new
        {
            width = Width,
            height = Height,
            objects = Objects.Select(o => new
            {
                outputIndex = o.OutputIndex!.Value,
                panelWidth = o.PanelWidth,
                panelHeight = o.PanelHeight,
                curvatureDegrees = o.CurvatureDegrees,
                posX = o.PosX,
                posY = o.PosY,
                posZ = o.PosZ,
                yawDegrees = o.YawDegrees,
                pitchDegrees = o.PitchDegrees,
                rollDegrees = o.RollDegrees,
            }),
        };

        return JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>One placed panel in the scene builder. See <see cref="SceneConfig"/>.</summary>
public sealed class SceneObjectConfig
{
    public int? OutputIndex { get; set; }
    public float PanelWidth { get; set; } = 1.78f;
    public float PanelHeight { get; set; } = 1.0f;

    /// <summary>0 = flat. Arc angle in degrees the panel's width sweeps around a cylinder.</summary>
    public float CurvatureDegrees { get; set; }

    /// <summary>Meters. X = left/right, Y = up/down, Z = distance in front of the viewer.</summary>
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; } = 1.6f;

    /// <summary>Degrees. Yaw turns left/right, pitch tilts up/down, roll tilts sideways.</summary>
    public float YawDegrees { get; set; }
    public float PitchDegrees { get; set; }
    public float RollDegrees { get; set; }

    public string Label => OutputIndex.HasValue ? $"Monitor #{OutputIndex}" : "(unassigned)";
}
