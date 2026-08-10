using System.Diagnostics;

namespace PcHost.Capture;

/// <summary>
/// One-shot probe for whether ffmpeg's h264_nvenc encoder actually works on this machine.
///
/// Having an NVENC-capable GPU is not sufficient -- ffmpeg's nvenc wrapper also requires a
/// minimum NVIDIA driver version tied to the NVENC API version it was built against. On the
/// machine this was developed/tested on (RTX 5070 Ti, driver 595.95), ffmpeg 8.1.2's nvenc
/// requires driver >= 610.00 and fails to open the encoder ("Driver does not support the
/// required nvenc API version. Required: 13.1 Found: 13.0"). Desktop capture via ddagrab
/// (DXGI Desktop Duplication) works fine independently of this -- only the encode stage needs
/// the fallback. We probe once at startup and cache the result, falling back to libx264
/// (software encode) when NVENC isn't usable, rather than silently downgrading capture too.
/// </summary>
public static class EncoderProbe
{
    private static bool? _nvencAvailable;
    private static readonly object Lock = new();

    public static bool IsNvencAvailable()
    {
        lock (Lock)
        {
            if (_nvencAvailable.HasValue)
            {
                return _nvencAvailable.Value;
            }

            _nvencAvailable = Probe();
            return _nvencAvailable.Value;
        }
    }

    private static bool Probe()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-hide_banner -loglevel error -f lavfi -i color=size=64x64:rate=1 -frames:v 1 -c:v h264_nvenc -f null -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            // Drain both streams so the short-lived probe process can't block on a full pipe.
            var stdoutDrain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderrDrain = process.StandardError.ReadToEndAsync();

            bool exited = process.WaitForExit(5000);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }

            try { stdoutDrain.Wait(1000); } catch { /* ignore */ }
            try { stderrDrain.Wait(1000); } catch { /* ignore */ }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
