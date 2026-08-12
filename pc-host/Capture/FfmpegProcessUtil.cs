using System.Diagnostics;

namespace PcHost.Capture;

/// <summary>
/// The parts of driving an ffmpeg child process that are identical whether ffmpeg is doing its
/// own capture (<see cref="FfmpegCaptureSource"/>, ddagrab) or acting as an encode-only sink fed
/// externally-rendered frames (<see cref="PcHost.Render.RenderedCaptureSource"/>): draining
/// stderr so a full pipe can never deadlock it, and pumping/parsing its Annex-B H264 stdout.
/// Factored out here once both needed the exact same logic rather than duplicating it.
/// </summary>
public static class FfmpegProcessUtil
{
    public static async Task DrainStderrAsync(Process process, string logFilePath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
            await using var log = new StreamWriter(logFilePath, append: false) { AutoFlush = true };
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
    public static async Task PumpFramesAsync(Process process, Action<AnnexBFrame> onFrame, CancellationToken ct)
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
