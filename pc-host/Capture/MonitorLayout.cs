namespace PcHost.Capture;

/// <summary>
/// Describes a 2D grid (rows x columns) of DXGI outputs (ddagrab's <c>output_idx</c>) to capture
/// and tile into one wide/tall canvas.
///
/// Why this is the whole multi-monitor feature, not a partial one: the XREAL 1S's onboard X1 chip
/// already does its own 3DoF head-tracking-driven panning (yaw AND pitch -- all 3 rotational
/// axes) across whatever video signal it's fed, entirely on its own hardware, regardless of
/// source (this is how XREAL's own Nebula achieves its ultrawide/multi-monitor "follow mode"
/// experience too). So the host side of this doesn't need to do any head-tracking or
/// reprojection itself -- it only needs to hand the glasses a canvas big enough, in both
/// dimensions for a real grid. Verified empirically on this machine: capturing 2 real outputs
/// (3440x1440 primary, 1920x1080 secondary) and hstacking them into a 3840x1080 canvas (and,
/// separately, with a 60px gap into 3900x1080) produces a valid H264 stream end to end
/// (ffprobe-confirmed) -- see pc-host/README.md's "Multi-monitor capture" section. 2D
/// (rows-and-columns) grid support extends the same idea with an added vstack step across rows.
/// </summary>
public sealed class MonitorLayout
{
    /// <summary>
    /// DXGI output indices arranged as a grid, <c>Grid[row][col]</c>. Every row must have the
    /// same number of columns (a ragged grid isn't supported -- use a placeholder/duplicate
    /// index and crop visually if you need an irregular shape, or file it as a real feature
    /// request if that's not good enough in practice).
    /// </summary>
    public required int[][] Grid { get; init; }

    /// <summary>
    /// Each captured output is scaled to this size before tiling, regardless of its native
    /// resolution/aspect ratio -- keeps the canvas math simple (uniform tiles) and matches the
    /// mental model of "N virtual monitors" better than trying to preserve each real monitor's
    /// native aspect ratio. A real monitor with a different aspect ratio than the tile will look
    /// slightly stretched (confirmed visually in testing with the 3440x1440 ultrawide primary
    /// scaled to a 1920x1080 tile) -- acceptable for now, revisit with letterboxing if it bothers
    /// you in practice.
    /// </summary>
    public required int TileWidth { get; init; }
    public required int TileHeight { get; init; }

    /// <summary>Solid black gutter (pixels) between adjacent columns. No effect with 1 column.</summary>
    public int GapX { get; init; }

    /// <summary>Solid black gutter (pixels) between adjacent rows. No effect with 1 row.</summary>
    public int GapY { get; init; }

    public int Rows => Grid.Length;
    public int Columns => Grid.Length > 0 ? Grid[0].Length : 0;
    public int MonitorCount => Rows * Columns;

    /// <summary>Flattened row-major order -- also the ffmpeg input stream order <see cref="Capture.FfmpegCaptureSource"/> uses.</summary>
    public int[] OutputIndices => Grid.SelectMany(row => row).ToArray();

    public int CanvasWidth => TileWidth * Columns + GapX * Math.Max(0, Columns - 1);
    public int CanvasHeight => TileHeight * Rows + GapY * Math.Max(0, Rows - 1);

    /// <summary>Single primary-monitor capture -- the original, pre-multi-monitor behavior.</summary>
    public static MonitorLayout SinglePrimary { get; } = new()
    {
        Grid = new[] { new[] { 0 } },
        TileWidth = 1920,
        TileHeight = 1080,
    };

    /// <summary>
    /// Parses a grid spec like <c>"0,1;2,3"</c> (rows separated by <c>;</c>, columns within a
    /// row separated by <c>,</c>) -- e.g. that example is a 2x2 grid: output_idx 0 top-left,
    /// 1 top-right, 2 bottom-left, 3 bottom-right. A single row with no <c>;</c> (e.g.
    /// <c>"0,1,2"</c>) is a 1xN horizontal strip, matching the original pre-grid behavior.
    /// </summary>
    public static MonitorLayout Parse(string gridSpec, int tileWidth, int tileHeight, int gapX = 0, int gapY = 0)
    {
        var rows = gridSpec
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(rowStr => rowStr
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse)
                .ToArray())
            .ToArray();

        if (rows.Length == 0 || rows.Any(r => r.Length == 0))
        {
            throw new ArgumentException("At least one monitor output index is required.", nameof(gridSpec));
        }

        int columns = rows[0].Length;
        if (rows.Any(r => r.Length != columns))
        {
            throw new ArgumentException(
                $"All rows must have the same number of columns (row 0 has {columns}).", nameof(gridSpec));
        }

        if (gapX < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gapX), gapX, "Gap cannot be negative.");
        }

        if (gapY < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gapY), gapY, "Gap cannot be negative.");
        }

        return new MonitorLayout
        {
            Grid = rows,
            TileWidth = tileWidth,
            TileHeight = tileHeight,
            GapX = gapX,
            GapY = gapY,
        };
    }
}
