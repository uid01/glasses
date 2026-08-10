using System.Net;
using System.Net.Sockets;
using PcHost.Capture;
using PcHost.Protocol;
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
    private long _nextSessionIdCounter = Random.Shared.Next(1, int.MaxValue);

    public ControlServer(int controlPort, int videoPort, SessionManager sessions, VideoSender videoSender, bool mock, string logDirectory)
    {
        _listener = new UdpClient(new IPEndPoint(IPAddress.Any, controlPort));
        _videoPort = videoPort;
        _sessions = sessions;
        _videoSender = videoSender;
        _mock = mock;
        _logDirectory = logDirectory;
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

        var (width, height, fps) = ChooseMode(handshake.DesiredWidth, handshake.DesiredHeight, handshake.DesiredFps);
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
        };

        _sessions.Add(session);
        session.PipelineTask = CapturePipeline.RunAsync(session, _videoSender, _mock, _logDirectory, session.PipelineCts.Token);

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

        Console.WriteLine($"[control] session {sessionId} accepted from {remoteEndPoint} -> {width}x{height}@{fps} codec={chosenCodec} ({(_mock ? "mock" : "ffmpeg")})");
    }

    /// <summary>
    /// Resolution/fps negotiation. Per the PC-Host-Agent task spec: accept the client's request
    /// if it's exactly 1920x1080@60, 1920x1200@60, or a 16:18-ish vertical mode at 60fps;
    /// otherwise fall back to 1920x1080@60. Note this always sends an ack (even on fallback)
    /// rather than PROTOCOL.md's general "silently don't respond on rejection" rule -- the task
    /// spec explicitly calls for always accepting/falling back rather than dropping, so every
    /// handshake gets a usable session.
    /// </summary>
    private static (ushort width, ushort height, byte fps) ChooseMode(ushort width, ushort height, byte fps)
    {
        if (width == 1920 && height == 1080 && fps == 60)
        {
            return (1920, 1080, 60);
        }

        if (width == 1920 && height == 1200 && fps == 60)
        {
            return (1920, 1200, 60);
        }

        if (fps == 60 && width > 0)
        {
            double aspect = (double)height / width;
            const double targetAspect = 18.0 / 16.0; // 1.125
            if (Math.Abs(aspect - targetAspect) < 0.05)
            {
                return (width, height, 60);
            }
        }

        return (1920, 1080, 60);
    }

    public void Dispose() => _listener.Dispose();
}
