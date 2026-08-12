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
    /// Half the render camera's horizontal field of view, derived from pc-host's Camera default
    /// vertical FOV (pc-host/Render/Camera.cs -- FieldOfViewRadians = MathF.PI / 2.2f, duplicated
    /// here since this project doesn't reference pc-host's assembly for one constant) and this
    /// scene's canvas aspect ratio, matching how Matrix4x4.CreatePerspectiveFieldOfView derives
    /// horizontal FOV from vertical FOV + aspect. Used to warn when a placed panel falls outside
    /// what the camera will actually render -- see <see cref="SceneObjectConfig.IsOutOfView"/>.
    /// </summary>
    public double HalfHorizontalFovRadians
    {
        get
        {
            const double verticalFov = Math.PI / 2.2;
            double aspect = Height > 0 ? (double)Width / Height : 16.0 / 9.0;
            return Math.Atan(Math.Tan(verticalFov / 2.0) * aspect);
        }
    }

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
    /// Repositions every object into a comfortable, evenly-spaced arc directly in front of the
    /// viewer, each one angled to face back toward the camera -- the fix for "everything looks
    /// far away and small": panels placed too far back, and/or left parallel to each other
    /// (rather than angled inward) so ones off to the side present themselves increasingly
    /// edge-on -- both compound as more panels get spread out. Overwrites Position and Yaw for
    /// every object in the scene; doesn't touch size, curvature, elevation, or source
    /// assignment. Order preserved from <see cref="Objects"/> (index 0 goes leftmost).
    /// </summary>
    public void AutoArrangeInView(float distanceMeters = 1.2f, float usedFovFraction = 0.7f)
    {
        if (Objects.Count == 0)
        {
            return;
        }

        double halfFov = HalfHorizontalFovRadians * usedFovFraction;
        double slice = (halfFov * 2.0) / Objects.Count;

        for (int i = 0; i < Objects.Count; i++)
        {
            double angle = -halfFov + slice * (i + 0.5);
            var obj = Objects[i];
            obj.PosX = (float)(distanceMeters * Math.Sin(angle));
            obj.PosZ = (float)(distanceMeters * Math.Cos(angle));
            // Facing back toward the origin (camera): the same angle used to place it also
            // happens to be the yaw that turns its face inward, rather than leaving every panel
            // parallel (which is correct for the one directly ahead, but presents everything
            // off to the side increasingly edge-on as it gets further from center).
            obj.YawDegrees = (float)(angle * 180.0 / Math.PI);
        }
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

    /// <summary>
    /// True if any part of this panel's (unrotated) width falls outside the camera's horizontal
    /// field of view at its own depth -- i.e. it would be partially or fully missing from the
    /// rendered/streamed output even though it places fine in this 2D top-down editor. See
    /// <see cref="SceneConfig.HalfHorizontalFovRadians"/> for how the angle is derived. Doesn't
    /// account for yaw (a rotated panel's true screen-space extent is narrower than its unrotated
    /// width, so this can over-warn slightly for a steeply-rotated panel) or vertical FOV/PosY
    /// (not representable in a top-down view) -- a conservative, good-enough check for the
    /// common case of panels spread out left-to-right.
    /// </summary>
    public bool IsOutOfView(double halfHorizontalFovRadians)
    {
        if (PosZ <= 0)
        {
            return true; // behind or at the camera -- never visible regardless of X.
        }

        double visibleHalfWidthAtZ = PosZ * Math.Tan(halfHorizontalFovRadians);
        double left = PosX - PanelWidth / 2.0;
        double right = PosX + PanelWidth / 2.0;
        return left < -visibleHalfWidthAtZ || right > visibleHalfWidthAtZ;
    }
}
