namespace PcHost.Capture;

/// <summary>
/// Describes which DXGI outputs (ddagrab's <c>output_idx</c>) to capture and how to tile them
/// into one wide canvas.
///
/// Why this is the whole feature, not a partial one: the XREAL 1S's onboard X1 chip already does
/// its own 3DoF head-tracking-driven panning across whatever video signal it's fed, entirely on
/// its own hardware, regardless of source (this is how XREAL's own Nebula achieves its ultrawide/
/// multi-monitor "follow mode" experience too). So the host side of this doesn't need to do any
/// head-tracking or reprojection itself -- it only needs to hand the glasses a wide enough canvas.
/// Verified empirically on this machine: capturing both real outputs (3440x1440 primary,
/// 1920x1080 secondary) and hstacking them into a 3840x1080 canvas produces a valid H264 stream
/// end to end (ffprobe-confirmed) -- see pc-host/README.md's "Multi-monitor capture" section.
/// </summary>
public sealed class MonitorLayout
{
    /// <summary>DXGI output indices (ddagrab <c>output_idx</c>) to capture, left to right.</summary>
    public required int[] OutputIndices { get; init; }

    /// <summary>
    /// Each captured output is scaled to this size before tiling, regardless of its native
    /// resolution/aspect ratio -- keeps the canvas math simple (N uniform tiles) and matches the
    /// mental model of "N virtual monitors" better than trying to preserve each real monitor's
    /// native aspect ratio. A real monitor with a different aspect ratio than the tile will look
    /// slightly stretched (confirmed visually in testing with the 3440x1440 ultrawide primary
    /// scaled to a 1920x1080 tile) -- acceptable for now, revisit with letterboxing if it bothers
    /// you in practice.
    /// </summary>
    public required int TileWidth { get; init; }
    public required int TileHeight { get; init; }

    /// <summary>
    /// Solid black gutter (pixels) inserted between adjacent tiles, so the glasses' panning
    /// shows a visible break between monitors instead of one seamless edge-to-edge image --
    /// closer to how physical monitor bezels read as separate screens. Zero (the default) means
    /// no gap, i.e. the original edge-to-edge behavior. Has no effect with a single monitor.
    /// </summary>
    public int GapWidth { get; init; }

    public int MonitorCount => OutputIndices.Length;
    public int CanvasWidth => TileWidth * OutputIndices.Length + GapWidth * Math.Max(0, OutputIndices.Length - 1);
    public int CanvasHeight => TileHeight;

    /// <summary>Single primary-monitor capture -- the original, pre-multi-monitor behavior.</summary>
    public static MonitorLayout SinglePrimary { get; } = new()
    {
        OutputIndices = new[] { 0 },
        TileWidth = 1920,
        TileHeight = 1080,
    };

    public static MonitorLayout Parse(string outputIndicesCsv, int tileWidth, int tileHeight, int gapWidth = 0)
    {
        var indices = outputIndicesCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToArray();

        if (indices.Length == 0)
        {
            throw new ArgumentException("At least one monitor output index is required.", nameof(outputIndicesCsv));
        }

        if (gapWidth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gapWidth), gapWidth, "Gap width cannot be negative.");
        }

        return new MonitorLayout
        {
            OutputIndices = indices,
            TileWidth = tileWidth,
            TileHeight = tileHeight,
            GapWidth = gapWidth,
        };
    }
}
