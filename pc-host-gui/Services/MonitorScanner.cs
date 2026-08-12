using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using PcHostGui.Models;

namespace PcHostGui.Services;

/// <summary>
/// Discovers available DXGI outputs (ddagrab <c>output_idx</c> values) by actually capturing one
/// preview frame from each, rather than trying to correlate them against
/// <c>Screen.AllScreens</c>. See <see cref="Models.MonitorSource"/> for why.
/// </summary>
public static class MonitorScanner
{
    private const int MaxIndexToTry = 16;

    /// <summary>
    /// Tries output_idx 0, 1, 2, ... in order, stopping at the first one that fails to open
    /// (ddagrab returns an error for an output_idx that doesn't exist) or at
    /// <see cref="MaxIndexToTry"/>, whichever comes first. Each successful capture becomes one
    /// <see cref="MonitorSource"/> with a real preview thumbnail.
    /// </summary>
    public static async Task<IReadOnlyList<MonitorSource>> ScanAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var results = new List<MonitorSource>();

        for (int idx = 0; idx < MaxIndexToTry; idx++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Probing output_idx={idx}...");

            var frame = await CaptureOneFrameAsync(idx, ct).ConfigureAwait(false);
            if (frame is null)
            {
                progress?.Report($"output_idx={idx} not available -- stopping scan ({results.Count} monitor(s) found).");
                break;
            }

            results.Add(frame);
            progress?.Report($"Found output_idx={idx}: {frame.Width}x{frame.Height}");
        }

        return results;
    }

    /// <summary>
    /// Captures a single frame from the given output_idx as a PNG via ffmpeg's stdout, returning
    /// null if ffmpeg fails to open that output (the normal, expected signal that no such output
    /// exists) rather than throwing -- a failed probe is an ordinary result here, not an error.
    /// </summary>
    private static async Task<MonitorSource?> CaptureOneFrameAsync(int outputIndex, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            ArgumentList =
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", $"ddagrab=output_idx={outputIndex}:framerate=5",
                // ddagrab's output is a GPU-resident d3d11 hardware frame -- it has to be
                // downloaded to system memory before any software encoder (png here) can touch
                // it. Omitting this produces "Impossible to convert between the formats
                // supported by the filter" (src: d3d11) and a silently empty/zero-byte output,
                // which is exactly the bug this comment is here so nobody reintroduces: caught
                // by actually running this against real hardware, not just from reading the
                // ffmpeg docs. FfmpegCaptureSource.cs's real capture path already does this
                // correctly (hwdownload,format=bgra,scale=...) -- this scanner just skips the
                // scale since it wants the native resolution for display purposes.
                "-vf", "hwdownload,format=bgra",
                "-frames:v", "1",
                "-f", "image2pipe", "-vcodec", "png",
                "-",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return null;
        }

        using var pngBytes = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(pngBytes, ct);
        string stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);

        try
        {
            await stdoutTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        bool exited = process.WaitForExit(5000);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return null;
        }

        if (process.ExitCode != 0 || pngBytes.Length == 0)
        {
            // Expected outcome once outputIndex exceeds the number of real DXGI outputs -- not
            // logged as an error, just the scan's natural stopping condition. stderr is
            // available in the debugger/log if diagnosing an unexpectedly early stop.
            _ = stderr;
            return null;
        }

        pngBytes.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = pngBytes;
        bitmap.EndInit();
        bitmap.Freeze(); // makes it safely usable from the UI thread regardless of which thread decoded it

        return new MonitorSource
        {
            OutputIndex = outputIndex,
            Width = bitmap.PixelWidth,
            Height = bitmap.PixelHeight,
            Thumbnail = bitmap,
        };
    }
}
