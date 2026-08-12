using System.Diagnostics;
using System.Net;
using PcHost.Capture;
using PcHost.Render;

namespace PcHost.Session;

/// <summary>
/// State for one accepted client session. A session is created when a Handshake is accepted
/// and torn down on Disconnect or a 5s traffic timeout (per shared-protocol/PROTOCOL.md).
/// </summary>
public sealed class SessionInfo
{
    public required uint SessionId { get; init; }
    public required IPAddress ClientAddress { get; init; }
    public required int VideoPort { get; init; }

    /// <summary>
    /// The client's source address+port for the Handshake that created this session --
    /// i.e. where its Control-channel <see cref="UDPConnection"/>-equivalent is actually
    /// listening. Needed to proactively send Heartbeat packets back to the client (per
    /// PROTOCOL.md, Heartbeat is bidirectional); ClientAddress + a fixed control port isn't
    /// enough because the client's UDP socket for Handshake/Heartbeat traffic uses an
    /// ephemeral source port, not the well-known control port -- that ephemeral port is what
    /// a reply must be addressed to.
    /// </summary>
    public required IPEndPoint ControlEndpoint { get; init; }
    public required ushort Width { get; init; }
    public required ushort Height { get; init; }
    public required byte Fps { get; init; }
    public required byte Codec { get; init; } // 0=H264, 1=HEVC

    /// <summary>
    /// Which real monitor(s) to capture and how to tile them -- see <see cref="Capture.MonitorLayout"/>.
    /// Null only for the synthetic `--mock` session (which never launches ffmpeg, so there's
    /// nothing to capture). Whoever constructs a real session must keep Width/Height in sync
    /// with Layout.CanvasWidth/CanvasHeight -- they're kept as separate fields rather than
    /// computed from Layout because Width/Height are also what gets echoed to the client in
    /// HandshakeAck, independent of how the host produces the pixels.
    /// </summary>
    public MonitorLayout? Layout { get; init; }

    /// <summary>
    /// If set, this session uses the GPU 3D compositor (<see cref="RenderedCaptureSource"/> --
    /// curved surfaces, arbitrary placement) instead of the ddagrab+filter_complex pipeline
    /// (<see cref="Layout"/>). Mutually exclusive with <see cref="Layout"/> in practice, though
    /// nothing enforces that at this type's level -- whichever CapturePipeline checks first wins.
    /// </summary>
    public RenderSceneSpec? RenderScene { get; init; }

    /// <summary>
    /// True only for the synthetic session `--mock` mode auto-starts without a real Handshake
    /// (see Program.cs). It has no client sending Heartbeat/InputEvent traffic to keep it alive,
    /// so it is exempt from <see cref="SessionManager"/>'s 5s idle-timeout sweep -- otherwise it
    /// would always tear itself down ~5s after startup. Sessions created via a real Handshake
    /// leave this false and are timed out normally.
    /// </summary>
    public bool ExemptFromTimeout { get; init; }

    /// <summary>Wall-clock reference for computing ptsMicros (microseconds since session start).</summary>
    public Stopwatch Clock { get; } = Stopwatch.StartNew();

    private long _frameIdCounter = -1;
    public uint NextFrameId() => (uint)Interlocked.Increment(ref _frameIdCounter);

    private long _lastSeenTicks = DateTime.UtcNow.Ticks;
    public DateTime LastSeenUtc
    {
        get => new(Interlocked.Read(ref _lastSeenTicks), DateTimeKind.Utc);
        set => Interlocked.Exchange(ref _lastSeenTicks, value.Ticks);
    }

    public void Touch() => LastSeenUtc = DateTime.UtcNow;

    /// <summary>Cancellation source for this session's capture pipeline (ffmpeg or mock).</summary>
    public CancellationTokenSource PipelineCts { get; } = new();

    /// <summary>The running capture pipeline task, so shutdown can await it.</summary>
    public Task? PipelineTask { get; set; }

    /// <summary>Set when the session's capture backend is a real ffmpeg process, so it can be killed on teardown.</summary>
    public Process? FfmpegProcess { get; set; }

    public ulong ElapsedMicros() => (ulong)(Clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency);
}
