using System.Diagnostics;
using System.Net;

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
