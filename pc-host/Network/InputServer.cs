using System.Net;
using System.Net.Sockets;
using PcHost.Input;
using PcHost.Protocol;
using PcHost.Session;

namespace PcHost.Network;

/// <summary>
/// Listens on the Input channel (default UDP 9002) for InputEvent packets from the iOS client
/// and translates them to real Windows input via <see cref="InputInjector"/>.
/// </summary>
public sealed class InputServer : IDisposable
{
    private readonly UdpClient _listener;
    private readonly SessionManager _sessions;

    private long _eventCount;
    private long _unknownSessionCount;
    private DateTime _windowStart = DateTime.UtcNow;

    public InputServer(int inputPort, SessionManager sessions)
    {
        _listener = new UdpClient(new IPEndPoint(IPAddress.Any, inputPort));
        _sessions = sessions;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"[input] listening on UDP {((IPEndPoint)_listener.Client.LocalEndPoint!).Port}");
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

            if (!InputEvent.TryParse(result.Buffer, out var evt))
            {
                continue; // malformed / wrong magic-version: dropped silently
            }

            if (!_sessions.TryGet(evt.SessionId, out var session) || session is null)
            {
                Interlocked.Increment(ref _unknownSessionCount);
                continue; // events for an unknown/expired session are ignored
            }

            session.Touch();
            Interlocked.Increment(ref _eventCount);
            LogThroughputIfDue();

            InputInjector.Inject(evt);
        }
    }

    /// <summary>
    /// The wire format has no sequence number field (see PROTOCOL.md's InputEvent layout), so
    /// true per-packet gap detection isn't possible from the payload alone. As a simple,
    /// honest substitute we log periodic throughput and count events attributed to unknown
    /// sessions (a proxy for "traffic arriving after we've torn the session down", which is the
    /// most common loss/ordering symptom in practice).
    /// </summary>
    private void LogThroughputIfDue()
    {
        var now = DateTime.UtcNow;
        if (now - _windowStart < TimeSpan.FromSeconds(10))
        {
            return;
        }

        long count = Interlocked.Exchange(ref _eventCount, 0);
        long unknown = Interlocked.Exchange(ref _unknownSessionCount, 0);
        _windowStart = now;
        Console.WriteLine($"[input] {count} events in last 10s ({unknown} for unknown/expired sessions)");
    }

    public void Dispose() => _listener.Dispose();
}
