using System.Net;
using System.Net.Sockets;
using PcHost.Protocol;

namespace PcHost.Network;

/// <summary>
/// Sends VideoFrameFragment packets to a client's Video channel (default port 9001), per
/// shared-protocol/PROTOCOL.md.
/// </summary>
public sealed class VideoSender : IDisposable
{
    private readonly UdpClient _client = new(AddressFamily.InterNetwork);

    public void Send(IPEndPoint destination, in VideoFrameFragment fragment)
    {
        Span<byte> buffer = stackalloc byte[fragment.WireSize];
        fragment.WriteTo(buffer);
        _client.Send(buffer, destination);
    }

    public void Dispose() => _client.Dispose();
}
