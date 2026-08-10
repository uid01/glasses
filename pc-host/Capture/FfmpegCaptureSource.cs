using System.Diagnostics;

namespace PcHost.Capture;

/// <summary>
/// Launches ffmpeg to capture the desktop (ddagrab / DXGI Desktop Duplication) and encode it to
/// raw Annex-B H264 on stdout, then feeds the byte stream through <see cref="AnnexBFrameSplitter"/>
/// and invokes a callback per completed frame.
///
/// Capture filter graph (ddagrab) was verified empirically on this machine to work well: it
/// reports the real desktop (3440x1440 in testing) as a d3d11 hardware frame. Encoding then
/// downloads that frame to system memory, scales to the client's chosen resolution, and converts
/// to yuv420p before handing to the H264 encoder. See <see cref="EncoderProbe"/> for why the
/// encoder itself (h264_nvenc vs libx264) is chosen dynamically rather than hardcoded to NVENC.
/// </summary>
public sealed class FfmpegCaptureSource
{
    public required int Width { get; init; }
    public required int Height { get; init; }
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
        string filter = $"hwdownload,format=bgra,scale={Width}:{Height},format=yuv420p";

        string args = "-hide_banner -loglevel warning " +
                      $"-f lavfi -i ddagrab=framerate={Fps} " +
                      $"-vf \"{filter}\" " +
                      $"{encoderArgs} -profile:v baseline -g {gop} " +
                      "-b:v 8M -maxrate 8M -bufsize 4M " +
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
