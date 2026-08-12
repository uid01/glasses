using System.Net.Sockets;

namespace PcHost.Render;

/// <summary>
/// Connects to the XREAL One glasses' link-local IMU stream and pumps parsed samples to a
/// callback. See <see cref="XrealOneImuFrameParser"/> for the frame format and why this is a
/// plain TCP socket rather than raw USB HID -- and for why this whole thing is UNVERIFIED
/// AGAINST REAL HARDWARE pending a USB-C adapter cable to actually connect the glasses to this
/// PC directly.
/// </summary>
public sealed class XrealOneImuClient : IDisposable
{
    public const string DefaultHost = "169.254.2.1";
    public const int StreamPort = 52998;

    private readonly TcpClient _client = new();
    private readonly XrealOneImuFrameParser _parser = new();
    private NetworkStream? _stream;

    public async Task ConnectAsync(string host = DefaultHost, int port = StreamPort, CancellationToken ct = default)
    {
        await _client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        _stream = _client.GetStream();
    }

    /// <summary>
    /// Reads from the stream until cancelled or the connection closes, invoking
    /// <paramref name="onSample"/> for each parsed IMU sample. Run this in its own background
    /// task for the lifetime of a session.
    /// </summary>
    public async Task PumpAsync(Action<XrealOneImuFrameParser.Sample> onSample, CancellationToken ct)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Call ConnectAsync before PumpAsync.");
        }

        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break; // glasses closed the connection (unplugged, mode change, etc.)
                }

                foreach (var sample in _parser.Feed(buffer.AsSpan(0, read)))
                {
                    onSample(sample);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _client.Dispose();
    }
}
