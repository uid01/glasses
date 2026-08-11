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

        string inputArgs = string.Join(' ', Layout.OutputIndices.Select(idx =>
            $"-f lavfi -i \"ddagrab=output_idx={idx}:framerate={Fps}\""));

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
    /// Builds the filter_complex graph: each input gets hwdownload'd off the GPU and scaled to
    /// the layout's uniform tile size, then all tiles are hstack'd left-to-right (matching
    /// <see cref="MonitorLayout.OutputIndices"/> order) and converted to yuv420p for the encoder.
    /// For a single-monitor layout, hstack (which requires 2+ inputs) is skipped entirely.
    /// </summary>
    public static string BuildFilterComplex(MonitorLayout layout)
    {
        var sb = new StringBuilder();
        var tileLabels = new List<string>();

        for (int i = 0; i < layout.OutputIndices.Length; i++)
        {
            string label = $"v{i}";
            sb.Append($"[{i}:v]hwdownload,format=bgra,scale={layout.TileWidth}:{layout.TileHeight}[{label}];");
            tileLabels.Add(label);
        }

        if (tileLabels.Count == 1)
        {
            sb.Append($"[{tileLabels[0]}]format=yuv420p[vout]");
        }
        else
        {
            string joinedInputs = string.Concat(tileLabels.Select(l => $"[{l}]"));
            sb.Append($"{joinedInputs}hstack=inputs={tileLabels.Count},format=yuv420p[vout]");
        }

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
