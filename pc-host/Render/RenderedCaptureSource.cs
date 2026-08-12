using System.Diagnostics;
using PcHost.Capture;

namespace PcHost.Render;

/// <summary>
/// The curved/arbitrary-3D-placement alternative to <see cref="FfmpegCaptureSource"/>: instead
/// of ffmpeg doing its own capture+compositing (ddagrab + filter_complex), <see cref="SceneRenderer"/>
/// does the capture (via <see cref="MonitorCapture"/>) and compositing (curved meshes, arbitrary
/// placement, camera-driven reprojection) on the GPU directly, and ffmpeg is reduced to an
/// encode-only sink fed raw BGRA frames over stdin. Everything downstream of "raw H264 bytes on
/// stdout" (AUD-based frame splitting, the encoder-selection/bitrate logic, stderr draining) is
/// unchanged and shared with the original pipeline via <see cref="FfmpegProcessUtil"/> --
/// deliberately not reinventing what's already proven, only replacing the capture/compositing
/// stage that ffmpeg's filter graphs can't do (curved surfaces, non-grid placement, live
/// reprojection from a moving camera).
/// </summary>
public sealed class RenderedCaptureSource
{
    public required SceneRenderer Renderer { get; init; }
    public required IReadOnlyList<SceneObject> Objects { get; init; }
    public required Camera Camera { get; init; }
    public required int Fps { get; init; }
    public required string LogFilePath { get; init; }

    private Process? _process;
    private CancellationTokenSource? _feedCts;

    public Process Start()
    {
        bool useNvenc = EncoderProbe.IsNvencAvailable();

        // Same reasoning as FfmpegCaptureSource: AUDs give AnnexBFrameSplitter an unambiguous
        // per-picture boundary, since slice-based threading under a zero-latency tune otherwise
        // splits one picture into many NALs.
        string encoderArgs = useNvenc
            ? "-c:v h264_nvenc -preset p1 -tune ll -zerolatency 1 -aud 1"
            : "-c:v libx264 -preset ultrafast -tune zerolatency -x264-params \"aud=1\"";

        int gop = Math.Max(1, Fps * 2);
        int bitrateMbps = Math.Max(8, 8 * Objects.Count);
        int bufsizeMbps = Math.Max(1, bitrateMbps / 2);

        string args = "-hide_banner -loglevel warning " +
                      $"-f rawvideo -pix_fmt bgra -s {Renderer.Width}x{Renderer.Height} -r {Fps} -i pipe:0 " +
                      "-vf format=yuv420p " +
                      $"{encoderArgs} -profile:v baseline -g {gop} " +
                      $"-b:v {bitrateMbps}M -maxrate {bitrateMbps}M -bufsize {bufsizeMbps}M " +
                      "-f h264 -";

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Console.WriteLine($"[render] launching encode-only ffmpeg ({(useNvenc ? "NVENC" : "libx264 software fallback")}): ffmpeg {args}");

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg process.");
        _process = process;

        _ = Task.Run(() => FfmpegProcessUtil.DrainStderrAsync(process, LogFilePath));

        _feedCts = new CancellationTokenSource();
        _ = Task.Run(() => FeedFramesAsync(process, _feedCts.Token));

        return process;
    }

    /// <summary>
    /// Renders and writes one frame to ffmpeg's stdin at (approximately) the target frame rate.
    /// Runs until the process exits or is cancelled. Uses whatever the objects' captures last
    /// had (<see cref="MonitorCapture.TryUpdate"/> is non-blocking) rather than waiting for a
    /// new frame each tick -- an idle monitor just keeps re-rendering its last known content,
    /// same as a real display would show no visible change.
    /// </summary>
    private async Task FeedFramesAsync(Process process, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(1.0 / Fps);
        var stdin = process.StandardInput.BaseStream;
        var controller = new ConsoleCameraController();

        try
        {
            while (!ct.IsCancellationRequested && !process.HasExited)
            {
                var frameStart = DateTime.UtcNow;

                controller.PollAndApply(Camera);
                foreach (var obj in Objects)
                {
                    obj.Capture.TryUpdate();
                }

                Renderer.Render(Objects, Camera);
                Renderer.WriteFrameBgra(stdin);
                await stdin.FlushAsync(ct).ConfigureAwait(false);

                var elapsed = DateTime.UtcNow - frameStart;
                var delay = interval - elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        catch (IOException)
        {
            // ffmpeg's stdin pipe closed (process exited) -- nothing more to write.
        }
        finally
        {
            try { stdin.Close(); } catch { /* best effort */ }
        }
    }

    public Task PumpFramesAsync(Process process, Action<AnnexBFrame> onFrame, CancellationToken ct)
        => FfmpegProcessUtil.PumpFramesAsync(process, onFrame, ct);

    public void Stop()
    {
        _feedCts?.Cancel();
    }
}
