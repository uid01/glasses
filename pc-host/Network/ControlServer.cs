using System.Net;
using System.Net.Sockets;
using PcHost.Capture;
using PcHost.Protocol;
using PcHost.Render;
using PcHost.Session;

namespace PcHost.Network;

/// <summary>
/// Listens on the Control channel (default UDP 9000) for Handshake / Heartbeat / Disconnect
/// from the iOS client, per shared-protocol/PROTOCOL.md.
/// </summary>
public sealed class ControlServer : IDisposable
{
    private readonly UdpClient _listener;
    private readonly SessionManager _sessions;
    private readonly VideoSender _videoSender;
    private readonly int _videoPort;
    private readonly bool _mock;
    private readonly string _logDirectory;
    private readonly MonitorLayout _monitorLayout;
    private readonly RenderSceneSpec? _renderScene;
    private long _nextSessionIdCounter = Random.Shared.Next(1, int.MaxValue);

    public ControlServer(int controlPort, int videoPort, SessionManager sessions, VideoSender videoSender, bool mock, string logDirectory, MonitorLayout monitorLayout, RenderSceneSpec? renderScene = null)
    {
        _listener = new UdpClient(new IPEndPoint(IPAddress.Any, controlPort));

        // Windows-specific UDP gotcha: if a send on this socket reaches a port nobody's
        // listening on anymore (e.g. a client whose Handshake socket it already closed, but
        // whose session hasn't hit the 5s sweep yet -- see SendHeartbeatTo below), the OS
        // delivers an ICMP Port Unreachable back to *this* socket, and by default Winsock
        // surfaces that as a SocketException (WSAECONNRESET) on the *next* ReceiveAsync call --
        // for an unrelated client, killing the whole receive loop (see the catch clause in
        // ReceiveLoopAsync, added as defense-in-depth for the same reason). SIO_UDP_CONNRESET
        // disables that behavior so a gone client can never take every other client down with it.
        const int SioUdpConnReset = -1744830452; // IOC_IN | IOC_VENDOR | 12
        try
        {
            _listener.Client.IOControl(unchecked((int)SioUdpConnReset), new byte[] { 0 }, null);
        }
        catch (PlatformNotSupportedException)
        {
            // Non-Windows: this ioctl doesn't exist there, and the ICMP-triggers-WSAECONNRESET
            // behavior it suppresses is Windows-specific anyway -- nothing to do.
        }

        _videoPort = videoPort;
        _sessions = sessions;
        _videoSender = videoSender;
        _mock = mock;
        _logDirectory = logDirectory;
        _monitorLayout = monitorLayout;
        _renderScene = renderScene;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"[control] listening on UDP {((IPEndPoint)_listener.Client.LocalEndPoint!).Port}");
        await Task.WhenAll(ReceiveLoopAsync(ct), SendHeartbeatsLoopAsync(ct)).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _listener.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // Belt-and-suspenders alongside the SIO_UDP_CONNRESET ioctl set in the
                // constructor: a single client's send/receive hiccup (e.g. a gone client
                // triggering the ICMP-port-unreachable behavior that ioctl is meant to suppress)
                // must never take down the receive loop for every other connected client.
                continue;
            }

            HandleDatagram(result.Buffer, result.RemoteEndPoint);
        }
    }

    /// <summary>
    /// Per PROTOCOL.md, Heartbeat is bidirectional ("sent every 1s on an idle connection" by
    /// either side). This was originally missed -- the host only ever *received* Heartbeats
    /// and never sent any back, which starves the client's own watchdog (it tracks liveness
    /// from Control-channel traffic it receives FROM the host) of anything to see after the
    /// initial HandshakeAck, causing the client to self-disconnect and reconnect roughly every
    /// 5-6s even though the client's own outbound Heartbeats were reaching the host just fine.
    /// </summary>
    private async Task SendHeartbeatsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            foreach (var session in _sessions.All)
            {
                SendHeartbeatTo(session);
            }
        }
    }

    private void SendHeartbeatTo(SessionInfo session)
    {
        var heartbeat = new Protocol.Heartbeat { SessionId = session.SessionId };
        Span<byte> buffer = stackalloc byte[Protocol.Heartbeat.WireSize];
        heartbeat.WriteTo(buffer);
        try
        {
            _listener.Send(buffer, session.ControlEndpoint);
        }
        catch (SocketException)
        {
            // Best-effort; a send failure here just means this one heartbeat tick is lost --
            // the next tick (1s later) will retry, and the 5s timeout window tolerates that.
        }
    }

    private void HandleDatagram(byte[] datagram, IPEndPoint remoteEndPoint)
    {
        if (!WireHeader.TryRead(datagram, out _, out var type))
        {
            return; // magic/version mismatch: dropped silently per PROTOCOL.md
        }

        switch (type)
        {
            case PacketType.Handshake:
                HandleHandshake(datagram, remoteEndPoint);
                break;

            case PacketType.Heartbeat:
                if (Protocol.Heartbeat.TryParse(datagram, out var heartbeat))
                {
                    _sessions.Touch(heartbeat.SessionId);
                }
                break;

            case PacketType.Disconnect:
                if (Protocol.Disconnect.TryParse(datagram, out var disconnect))
                {
                    Console.WriteLine($"[control] session {disconnect.SessionId} disconnected (reason={disconnect.Reason})");
                    _ = _sessions.RemoveAsync(disconnect.SessionId);
                }
                break;
        }
    }

    private void HandleHandshake(byte[] datagram, IPEndPoint remoteEndPoint)
    {
        if (!Handshake.TryParse(datagram, out var handshake))
        {
            return;
        }

        // The host is authoritative on resolution, not the client: the canvas is however many
        // real monitors this PC is configured to capture (see MonitorLayout), tiled together --
        // the client's requested width/height/fps is accepted but not honored, since "how many
        // screens and what layout" is a host-side decision by design (the whole point is the PC
        // drives monitor creation/layout; the client is just a display+input gateway). fps stays
        // fixed at 60 for now -- no client has ever needed anything else in practice.
        ushort width = (ushort)(_renderScene?.Width ?? _monitorLayout.CanvasWidth);
        ushort height = (ushort)(_renderScene?.Height ?? _monitorLayout.CanvasHeight);
        const byte fps = 60;
        const byte chosenCodec = 0; // H264 only; HEVC encode path is not implemented in this MVP.

        uint sessionId = unchecked((uint)Interlocked.Increment(ref _nextSessionIdCounter));

        var session = new SessionInfo
        {
            SessionId = sessionId,
            ClientAddress = remoteEndPoint.Address,
            ControlEndpoint = remoteEndPoint,
            VideoPort = _videoPort,
            Width = width,
            Height = height,
            Fps = fps,
            Codec = chosenCodec,
            Layout = _mock || _renderScene is not null ? null : _monitorLayout,
            RenderScene = _mock ? null : _renderScene,
        };

        _sessions.Add(session);
        // Task.Run, not a direct call: CapturePipeline.RunAsync's render-mode branch does real
        // synchronous work before its first await (D3D device creation, shader compilation,
        // MonitorCapture.WarmUp's blocking frame-acquisition loop -- can take over a second).
        // Since ReceiveLoopAsync calls HandleDatagram/HandleHandshake synchronously, that work
        // would otherwise run inline on the control server's single receive-loop thread, both
        // delaying the HandshakeAck below and blocking the loop from processing anything else in
        // the meantime. Confirmed by hitting exactly this: a real handshake test received a
        // stray Heartbeat (from the concurrently-running SendHeartbeatsLoopAsync, which had no
        // trouble reaching the newly-added session mid-blocking-window) before ever receiving
        // its HandshakeAck. The ffmpeg path never surfaced this because Process.Start doesn't
        // block waiting for output, so this pre-existing risk was latent until render mode made
        // session startup genuinely slow.
        var pipelineTask = Task.Run(() => CapturePipeline.RunAsync(session, _videoSender, _mock, _logDirectory, session.PipelineCts.Token));
        // Nothing else awaits this task promptly (SessionManager only does on teardown), so a
        // synchronous failure early in the pipeline -- e.g. MonitorCapture.WarmUp failing for a
        // bad output_idx -- would otherwise fault silently: no console output, no log entry, the
        // client just never receives any video and has no way to know why. Surface it.
        pipelineTask.ContinueWith(
            t => Console.WriteLine($"[pipeline] session {sessionId} capture pipeline failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
        session.PipelineTask = pipelineTask;

        var ack = new HandshakeAck
        {
            ServerProtocolVersion = 1,
            ChosenWidth = width,
            ChosenHeight = height,
            ChosenFps = fps,
            ChosenCodec = chosenCodec,
            SessionNonce = handshake.SessionNonce,
            SessionId = sessionId,
        };

        Span<byte> buffer = stackalloc byte[HandshakeAck.WireSize];
        ack.WriteTo(buffer);
        _listener.Send(buffer, remoteEndPoint);

        string modeDescription = _mock ? "mock" : _renderScene is not null ? $"rendered, {_renderScene.Objects.Count} object(s)" : $"ffmpeg, {_monitorLayout.MonitorCount} monitor(s)";
        Console.WriteLine($"[control] session {sessionId} accepted from {remoteEndPoint} -> {width}x{height}@{fps} codec={chosenCodec} ({modeDescription})");
    }

    public void Dispose() => _listener.Dispose();
}
