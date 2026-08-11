using System.Diagnostics;
using System.Text;

namespace PcHost.Capture;

/// <summary>
/// Launches ffmpeg to capture one or more monitors (ddagrab / DXGI Desktop Duplication),
/// tiles them side by side into one wide canvas per <see cref="Layout"/>, and encodes the result
/// to raw Annex-B H264 on stdout, then feeds the byte stream through
/// <see cref="AnnexBFrameSplitter"/> and invokes a callback per completed frame.
///
/// Multi-monitor capture graph verified empirically on this machine (2 real outputs, 3440x1440
/// primary + 1920x1080 secondary, tiled to a 3840x1080 canvas): ffprobe confirmed a valid H264
/// stream with the expected dimensions end to end. See <see cref="MonitorLayout"/> for why tiling
/// real capture into a wide canvas is the entire multi-monitor feature -- the glasses' own
/// onboard 3DoF chip does the head-tracking-driven panning across it, not this code.
///
/// See <see cref="EncoderProbe"/> for why the encoder itself (h264_nvenc vs libx264) is chosen
/// dynamically rather than hardcoded to NVENC.
/// </summary>
public sealed class FfmpegCaptureSource
{
    public required MonitorLayout Layout { get; init; }
    public required int Fps { get; init; }
    public required string LogFilePath { get; init; }

    private Process? _process;

    public Process Start()
    {
        bool useNvenc = EncoderProbe.IsNvencAvailable();

        // Access Unit Delimiters (NAL type 9) are requested from the encoder explicitly so
        // AnnexBFrameSplitter has an unambiguous per-picture boundary marker. This matters
        // because both encoders use slice-based threading under a zero-latency tune (verified
        // empirically on this machine: libx264 -tune zerolatency auto-splits every picture,
        // including IDRs, into ~17 slice NALs to parallelize across CPU cores without adding
        // frame-pipeline latency) -- so "one VCL NAL = one picture" does not hold, and without
        // AUDs the splitter would fragment a single picture into a dozen-plus bogus "frames".
        string encoderArgs = useNvenc
            ? "-c:v h264_nvenc -preset p1 -tune ll -zerolatency 1 -aud 1"
            : "-c:v libx264 -preset ultrafast -tune zerolatency -x264-params \"aud=1\"";

        int gop = Math.Max(1, Fps * 2);

        // One bitrate/maxrate budget scaled per monitor tile (same 8M-per-tile ratio the
        // original single-monitor default used) rather than a fixed total, so a wider canvas
        // (more monitors) doesn't get starved of bits relative to how much more picture content
        // it actually contains.
        int bitrateMbps = 8 * Layout.MonitorCount;
        int bufsizeMbps = Math.Max(1, bitrateMbps / 2);

        // Input stream ordering, which BuildFilterComplex assumes exactly: monitor inputs
        // (row-major, indices 0..MonitorCount-1), then horizontal gap spacers grouped per row
        // (Rows*(Columns-1) of them, if GapX>0), then vertical gap spacers (Rows-1 of them, if
        // GapY>0) -- see BuildFilterComplex's doc comment for the precise index math.
        string monitorInputArgs = string.Join(' ', Layout.OutputIndices.Select(idx =>
            $"-f lavfi -i \"ddagrab=output_idx={idx}:framerate={Fps}\""));

        bool hasGapX = Layout.GapX > 0 && Layout.Columns > 1;
        string hGapInputArgs = hasGapX
            ? string.Join(' ', Enumerable.Repeat(
                $"-f lavfi -i \"color=c=black:s={Layout.GapX}x{Layout.TileHeight}:r={Fps}\"",
                Layout.Rows * (Layout.Columns - 1)))
            : string.Empty;

        bool hasGapY = Layout.GapY > 0 && Layout.Rows > 1;
        string vGapInputArgs = hasGapY
            ? string.Join(' ', Enumerable.Repeat(
                // Full canvas width, since a vertical gap spans the entire row (which itself
                // already includes any horizontal gaps) -- vstack requires all inputs share the
                // same width.
                $"-f lavfi -i \"color=c=black:s={Layout.CanvasWidth}x{Layout.GapY}:r={Fps}\"",
                Layout.Rows - 1))
            : string.Empty;

        string inputArgs = string.Join(' ', new[] { monitorInputArgs, hGapInputArgs, vGapInputArgs }
            .Where(s => !string.IsNullOrEmpty(s)));

        string filterComplex = BuildFilterComplex(Layout);

        string args = "-hide_banner -loglevel warning " +
                      $"{inputArgs} " +
                      $"-filter_complex \"{filterComplex}\" -map \"[vout]\" " +
                      $"{encoderArgs} -profile:v baseline -g {gop} " +
                      $"-b:v {bitrateMbps}M -maxrate {bitrateMbps}M -bufsize {bufsizeMbps}M " +
                      "-f h264 -";

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Console.WriteLine($"[ffmpeg] launching ({(useNvenc ? "NVENC" : "libx264 software fallback")}): ffmpeg {args}");

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg process.");
        _process = process;

        // Drain stderr on a background task so ffmpeg (which is chatty on stderr) never blocks
        // on a full pipe. Write everything to a per-session log file.
        _ = Task.Run(() => DrainStderrAsync(process));

        return process;
    }

    /// <summary>
    /// Builds the filter_complex graph for a 2D grid: each monitor input gets hwdownload'd off
    /// the GPU and scaled to the layout's uniform tile size; each row is hstack'd left-to-right
    /// (with a solid black spacer interleaved between columns if <see cref="MonitorLayout.GapX"/>
    /// is set); then all rows are vstack'd top-to-bottom (with a full-width spacer between rows
    /// if <see cref="MonitorLayout.GapY"/> is set), converting to yuv420p only at this final step
    /// for the encoder. hstack/vstack (which require 2+ inputs) are skipped when there's only one
    /// column/row respectively -- a 1x1 grid short-circuits to just the single tile.
    ///
    /// Input stream index assumptions (must match <see cref="Start"/>'s input ordering exactly):
    /// monitor tiles occupy 0..MonitorCount-1 (row-major); horizontal gap spacers, if any, occupy
    /// MonitorCount..MonitorCount+Rows*(Columns-1)-1, grouped per row; vertical gap spacers, if
    /// any, occupy whatever's left, Rows-1 of them.
    /// </summary>
    public static string BuildFilterComplex(MonitorLayout layout)
    {
        var sb = new StringBuilder();
        int rows = layout.Rows;
        int cols = layout.Columns;
        int monitorCount = layout.MonitorCount;

        // Monitor tiles, row-major, input streams 0..monitorCount-1.
        var tileLabels = new string[rows, cols];
        int inputIndex = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                string label = $"v{r}_{c}";
                sb.Append($"[{inputIndex}:v]hwdownload,format=bgra,scale={layout.TileWidth}:{layout.TileHeight}[{label}];");
                tileLabels[r, c] = label;
                inputIndex++;
            }
        }

        if (rows == 1 && cols == 1)
        {
            sb.Append($"[{tileLabels[0, 0]}]format=yuv420p[vout]");
            return sb.ToString();
        }

        bool hasGapX = layout.GapX > 0 && cols > 1;
        bool hasGapY = layout.GapY > 0 && rows > 1;
        int hGapsPerRow = hasGapX ? cols - 1 : 0;
        int hGapInputBase = monitorCount;

        // Build each row: either the single tile (1 column) or an hstack of that row's tiles
        // (with gap spacers interleaved). Rows stay in bgra format -- final yuv420p conversion
        // happens once, at the very end, whether there's 1 row or many.
        var rowLabels = new string[rows];
        for (int r = 0; r < rows; r++)
        {
            if (cols == 1)
            {
                rowLabels[r] = tileLabels[r, 0];
                continue;
            }

            var stackLabels = new List<string> { tileLabels[r, 0] };
            for (int c = 1; c < cols; c++)
            {
                if (hasGapX)
                {
                    int gapInputIndex = hGapInputBase + r * hGapsPerRow + (c - 1);
                    string gapLabel = $"hgap{r}_{c - 1}";
                    // `color=` lavfi sources default to rgb24; converted to bgra here so the gap
                    // tile matches the monitor tiles' format going into hstack (which requires
                    // all inputs share the same pixel format).
                    sb.Append($"[{gapInputIndex}:v]format=bgra[{gapLabel}];");
                    stackLabels.Add(gapLabel);
                }

                stackLabels.Add(tileLabels[r, c]);
            }

            string rowLabel = $"row{r}";
            string joinedRow = string.Concat(stackLabels.Select(l => $"[{l}]"));
            sb.Append($"{joinedRow}hstack=inputs={stackLabels.Count}[{rowLabel}];");
            rowLabels[r] = rowLabel;
        }

        if (rows == 1)
        {
            sb.Append($"[{rowLabels[0]}]format=yuv420p[vout]");
            return sb.ToString();
        }

        int vGapInputBase = hGapInputBase + rows * hGapsPerRow;
        var vStackLabels = new List<string> { rowLabels[0] };
        for (int r = 1; r < rows; r++)
        {
            if (hasGapY)
            {
                int gapInputIndex = vGapInputBase + (r - 1);
                string gapLabel = $"vgap{r - 1}";
                sb.Append($"[{gapInputIndex}:v]format=bgra[{gapLabel}];");
                vStackLabels.Add(gapLabel);
            }

            vStackLabels.Add(rowLabels[r]);
        }

        string joinedRows = string.Concat(vStackLabels.Select(l => $"[{l}]"));
        sb.Append($"{joinedRows}vstack=inputs={vStackLabels.Count},format=yuv420p[vout]");

        return sb.ToString();
    }

    private async Task DrainStderrAsync(Process process)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
            await using var log = new StreamWriter(LogFilePath, append: false) { AutoFlush = true };
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) is not null)
            {
                await log.WriteLineAsync(line);
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[ffmpeg:stderr] {line}");
                }
            }
        }
        catch
        {
            // Process likely exited/killed while draining; nothing more to do.
        }
    }

    /// <summary>
    /// Reads ffmpeg's stdout until EOF (process exit) or cancellation, invoking
    /// <paramref name="onFrame"/> for each completed Annex-B access unit.
    /// </summary>
    public async Task PumpFramesAsync(Process process, Action<AnnexBFrame> onFrame, CancellationToken ct)
    {
        var splitter = new AnnexBFrameSplitter();
        var buffer = new byte[65536];
        var stdout = process.StandardOutput.BaseStream;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break; // EOF: ffmpeg exited or closed stdout.
                }

                foreach (var frame in splitter.Feed(buffer.AsSpan(0, read)))
                {
                    onFrame(frame);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        finally
        {
            var trailing = splitter.Flush();
            if (trailing is not null)
            {
                onFrame(trailing);
            }
        }
    }
}
