using System.Net;
using System.Numerics;
using PcHost.Capture;
using PcHost.Network;
using PcHost.Render;
using PcHost.Session;

namespace PcHost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);

        Console.WriteLine("PC Host -- low-latency desktop bridge");
        Console.WriteLine($"  mode         : {(options.Mock ? "MOCK (no ffmpeg, synthetic frames)" : options.RenderScene is not null ? "REAL (GPU 3D compositor)" : "REAL (ffmpeg capture)")}");
        if (options.RenderScene is not null)
        {
            Console.WriteLine($"  render scene : {options.RenderScene.Objects.Count} object(s), canvas {options.RenderScene.Width}x{options.RenderScene.Height}");
        }
        else
        {
            Console.WriteLine($"  monitors     : {options.MonitorLayout.Rows}x{options.MonitorLayout.Columns} grid, output_idx [{string.Join(',', options.MonitorLayout.OutputIndices)}], tile {options.MonitorLayout.TileWidth}x{options.MonitorLayout.TileHeight}, gap {options.MonitorLayout.GapX}x{options.MonitorLayout.GapY}px -> canvas {options.MonitorLayout.CanvasWidth}x{options.MonitorLayout.CanvasHeight}");
        }
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
        using var controlServer = new ControlServer(options.ControlPort, options.VideoPort, sessions, videoSender, options.Mock, options.LogDirectory, options.MonitorLayout, options.RenderScene);
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
    /// Which monitor(s) to capture and tile into one canvas -- see Capture/MonitorLayout.cs.
    /// Defaults to just the primary monitor (output_idx 0), matching the original single-monitor
    /// behavior, so existing single-screen usage is unaffected unless --monitors is explicitly
    /// passed.
    /// </summary>
    public MonitorLayout MonitorLayout { get; private set; } = MonitorLayout.SinglePrimary;

    /// <summary>
    /// Set (non-null) when --render is passed: switches this host from the ddagrab+filter_complex
    /// pipeline (<see cref="MonitorLayout"/>) to the GPU 3D compositor (curved surfaces, arbitrary
    /// placement -- see PcHost.Render). Today this is built from a handful of flags describing a
    /// single flat panel (the "parity milestone": prove the rendered path produces an equivalent
    /// stream to the original pipeline) -- pc-host-gui's forthcoming drag-drop 3D placement
    /// builder is the real way this will get populated with richer scenes later.
    /// </summary>
    public RenderSceneSpec? RenderScene { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        string monitorsGridSpec = "0";
        int tileWidth = 1920;
        int tileHeight = 1080;
        int gapX = 0;
        int gapY = 0;
        bool render = false;
        int renderMonitorIndex = 0;
        int renderWidth = 1920;
        int renderHeight = 1080;
        float renderCurvatureDegrees = 0f;
        string? sceneFilePath = null;

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
                // ddagrab output_idx values arranged as a grid: rows separated by ";", columns
                // within a row separated by ",". E.g. "0,1" is a 1x2 horizontal strip; "0,1;2,3"
                // is a 2x2 grid (0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right). Run
                // once with a single index (the default) first if you just want to confirm which
                // output_idx corresponds to which physical monitor before combining them -- see
                // pc-host/README.md's "Multi-monitor capture" section.
                case "--monitors" when i + 1 < args.Length:
                    monitorsGridSpec = args[++i];
                    break;
                case "--tile-width" when i + 1 < args.Length:
                    tileWidth = int.Parse(args[++i]);
                    break;
                case "--tile-height" when i + 1 < args.Length:
                    tileHeight = int.Parse(args[++i]);
                    break;
                // Solid black gutters (pixels) between adjacent monitor tiles, so the glasses
                // show a visible break between screens instead of one seamless edge-to-edge
                // image -- closer to how physical monitor bezels read as separate screens.
                // --gap-x is between columns, --gap-y is between rows; each is a no-op if the
                // grid only has 1 column / 1 row respectively.
                case "--gap-x" when i + 1 < args.Length:
                    gapX = int.Parse(args[++i]);
                    break;
                case "--gap-y" when i + 1 < args.Length:
                    gapY = int.Parse(args[++i]);
                    break;
                // Switches to the GPU 3D compositor path (PcHost.Render) instead of the
                // ddagrab+filter_complex pipeline. See RenderScene's doc comment.
                case "--render":
                    render = true;
                    break;
                case "--render-monitor" when i + 1 < args.Length:
                    renderMonitorIndex = int.Parse(args[++i]);
                    break;
                case "--render-width" when i + 1 < args.Length:
                    renderWidth = int.Parse(args[++i]);
                    break;
                case "--render-height" when i + 1 < args.Length:
                    renderHeight = int.Parse(args[++i]);
                    break;
                case "--render-curvature" when i + 1 < args.Length:
                    renderCurvatureDegrees = float.Parse(args[++i]);
                    break;
                // Full multi-object scene (arbitrary count, per-object position/rotation/
                // curvature) as JSON -- see Render/SceneFileFormat.cs. This is what
                // pc-host-gui's drag-drop 3D placement builder emits; it supersedes --render's
                // single-hardcoded-panel flags when both are given.
                case "--scene-file" when i + 1 < args.Length:
                    sceneFilePath = args[++i];
                    break;
            }
        }

        options.MonitorLayout = MonitorLayout.Parse(monitorsGridSpec, tileWidth, tileHeight, gapX, gapY);

        if (sceneFilePath is not null)
        {
            options.RenderScene = SceneFileLoader.Load(sceneFilePath);
        }
        else if (render)
        {
            options.RenderScene = new RenderSceneSpec
            {
                Width = renderWidth,
                Height = renderHeight,
                Objects = new[]
                {
                    new RenderObjectSpec
                    {
                        OutputIndex = renderMonitorIndex,
                        // 16:9 panel sized/placed so it fills most of the frame at a comfortable
                        // "sitting distance" from the camera at the origin -- matches roughly
                        // what the client would have seen as a flat single monitor before.
                        PanelWidth = 1.78f,
                        PanelHeight = 1.0f,
                        CurvatureDegrees = renderCurvatureDegrees,
                        Position = new Vector3(0f, 0f, 1.6f),
                        RotationEuler = Vector3.Zero,
                    },
                },
            };
        }

        return options;
    }
}
