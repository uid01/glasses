using System.Net;
using PcHost.Capture;
using PcHost.Network;
using PcHost.Session;

namespace PcHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);

        Console.WriteLine("PC Host -- low-latency desktop bridge");
        Console.WriteLine($"  mode         : {(options.Mock ? "MOCK (no ffmpeg, synthetic frames)" : "REAL (ffmpeg capture)")}");
        Console.WriteLine($"  monitors     : output_idx [{string.Join(',', options.MonitorLayout.OutputIndices)}], tile {options.MonitorLayout.TileWidth}x{options.MonitorLayout.TileHeight}, gap {options.MonitorLayout.GapWidth}px -> canvas {options.MonitorLayout.CanvasWidth}x{options.MonitorLayout.CanvasHeight}");
        Console.WriteLine($"  control port : {options.ControlPort}");
        Console.WriteLine($"  video port   : {options.VideoPort}");
        Console.WriteLine($"  input port   : {options.InputPort}");
        Console.WriteLine($"  log dir      : {options.LogDirectory}");

        Directory.CreateDirectory(options.LogDirectory);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Console.WriteLine("\n[main] Ctrl+C received, shutting down...");
            cts.Cancel();
        };

        await using var sessions = new SessionManager();
        sessions.SessionExpired += session =>
            Console.WriteLine($"[main] session {session.SessionId} timed out (no traffic for 5s), tore down");

        using var videoSender = new VideoSender();
        using var controlServer = new ControlServer(options.ControlPort, options.VideoPort, sessions, videoSender, options.Mock, options.LogDirectory, options.MonitorLayout);
        using var inputServer = new InputServer(options.InputPort, sessions);

        var tasks = new List<Task>
        {
            controlServer.RunAsync(cts.Token),
            inputServer.RunAsync(cts.Token),
        };

        if (options.Mock)
        {
            // Auto-start a default session immediately so `dotnet run -- --mock` is a
            // zero-dependency, zero-client smoke test of the fragmentation/UDP path: no
            // Handshake needs to be sent for frames to start flowing. The Control/Input
            // listeners above are still fully functional in this mode too, so a real Handshake
            // can additionally be exercised against the same process if desired.
            var mockSession = new SessionInfo
            {
                SessionId = 1,
                ClientAddress = options.MockTargetAddress,
                // No real client is listening for this session's proactive Heartbeats (there
                // was never a real Handshake), so this just needs to be a harmless, clearly
                // inert placeholder -- ExemptFromTimeout above already means nothing depends
                // on traffic actually reaching it.
                ControlEndpoint = new IPEndPoint(IPAddress.Loopback, 1),
                VideoPort = options.MockTargetPort,
                Width = (ushort)options.MonitorLayout.CanvasWidth,
                Height = (ushort)options.MonitorLayout.CanvasHeight,
                Fps = 60,
                Codec = 0,
                ExemptFromTimeout = true,
            };
            sessions.Add(mockSession);
            Console.WriteLine($"[main] mock mode: auto-started session 1 -> {options.MockTargetAddress}:{options.MockTargetPort} @ {mockSession.Width}x{mockSession.Height}@{mockSession.Fps}");

            mockSession.PipelineTask = Capture.CapturePipeline.RunAsync(mockSession, videoSender, mock: true, options.LogDirectory, mockSession.PipelineCts.Token);
            tasks.Add(mockSession.PipelineTask);
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }

        Console.WriteLine("[main] shutdown complete");
        return 0;
    }
}

internal sealed class CliOptions
{
    public bool Mock { get; private set; }
    public int ControlPort { get; private set; } = 9000;
    public int VideoPort { get; private set; } = 9001;
    public int InputPort { get; private set; } = 9002;
    public IPAddress MockTargetAddress { get; private set; } = IPAddress.Loopback;
    public int MockTargetPort { get; private set; } = 9001;
    public string LogDirectory { get; private set; } = Path.Combine(AppContext.BaseDirectory, "logs");

    /// <summary>
    /// Which monitor(s) to capture and tile into one wide canvas -- see
    /// Capture/MonitorLayout.cs. Defaults to just the primary monitor (output_idx 0), matching
    /// the original single-monitor behavior, so existing single-screen usage is unaffected
    /// unless --monitors is explicitly passed.
    /// </summary>
    public MonitorLayout MonitorLayout { get; private set; } = MonitorLayout.SinglePrimary;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        string monitorsCsv = "0";
        int tileWidth = 1920;
        int tileHeight = 1080;
        int gapWidth = 0;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--mock":
                    options.Mock = true;
                    break;
                case "--control-port" when i + 1 < args.Length:
                    options.ControlPort = int.Parse(args[++i]);
                    break;
                case "--video-port" when i + 1 < args.Length:
                    options.VideoPort = int.Parse(args[++i]);
                    options.MockTargetPort = options.VideoPort;
                    break;
                case "--input-port" when i + 1 < args.Length:
                    options.InputPort = int.Parse(args[++i]);
                    break;
                case "--mock-target-ip" when i + 1 < args.Length:
                    options.MockTargetAddress = IPAddress.Parse(args[++i]);
                    break;
                case "--mock-target-port" when i + 1 < args.Length:
                    options.MockTargetPort = int.Parse(args[++i]);
                    break;
                case "--log-dir" when i + 1 < args.Length:
                    options.LogDirectory = args[++i];
                    break;
                // Comma-separated ddagrab output_idx values, left to right, e.g. "0,1" to tile
                // this machine's two real monitors (primary, secondary) into one wide canvas
                // for the glasses' own onboard 3DoF panning to pan across. Run once with a
                // single index (the default) if you just want to confirm which output_idx
                // corresponds to which physical monitor before combining them -- see
                // pc-host/README.md's "Multi-monitor capture" section.
                case "--monitors" when i + 1 < args.Length:
                    monitorsCsv = args[++i];
                    break;
                case "--tile-width" when i + 1 < args.Length:
                    tileWidth = int.Parse(args[++i]);
                    break;
                case "--tile-height" when i + 1 < args.Length:
                    tileHeight = int.Parse(args[++i]);
                    break;
                // Solid black gutter (pixels) between adjacent monitor tiles, so the glasses
                // show a visible break between screens instead of one seamless edge-to-edge
                // image -- closer to how physical monitor bezels read as separate screens. No
                // effect with a single monitor.
                case "--gap" when i + 1 < args.Length:
                    gapWidth = int.Parse(args[++i]);
                    break;
            }
        }

        options.MonitorLayout = MonitorLayout.Parse(monitorsCsv, tileWidth, tileHeight, gapWidth);
        return options;
    }
}
