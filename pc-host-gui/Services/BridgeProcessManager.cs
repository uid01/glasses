using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PcHostGui.Services;

/// <summary>Starts/stops PcHost.exe as a subprocess and streams its console output line by line.</summary>
public sealed class BridgeProcessManager : IDisposable
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public event Action<string>? OutputReceived;
    public event Action? Exited;

    public void Start(string exePath, IReadOnlyList<string> args)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("Bridge is already running.");
        }

        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException($"PcHost.exe not found at '{exePath}'. Set the correct path below.", exePath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OutputReceived?.Invoke(e.Data); };
        process.Exited += (_, _) => Exited?.Invoke();

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
    }

    public void Stop()
    {
        if (_process is not { HasExited: false } p)
        {
            return;
        }

        try
        {
            // pc-host handles Ctrl+C for a clean shutdown (cancels loops, kills its own ffmpeg
            // children, closes sockets), but .NET has no built-in way to deliver that to a
            // *different*, non-console-sharing process on Windows short of native
            // GenerateConsoleCtrlEvent P/Invoke plumbing. Kill(entireProcessTree: true) (which
            // also kills any ffmpeg child, since it's a real child process of PcHost.exe) is the
            // pragmatic fallback -- pc-host holds no state that needs graceful persistence on
            // exit, so a hard stop here is fine.
            p.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited between the check and Kill(); nothing to do.
        }
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
    }
}
