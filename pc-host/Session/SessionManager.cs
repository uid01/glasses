using System.Collections.Concurrent;

namespace PcHost.Session;

/// <summary>
/// Tracks all active sessions and enforces the 5-second traffic timeout described in
/// shared-protocol/PROTOCOL.md's Heartbeat section: "Either side that hasn't seen a Heartbeat,
/// VideoFrameFragment, or InputEvent in 5s tears the session down locally." From the PC host's
/// point of view, "seen" traffic is Heartbeat or InputEvent received from the client (the PC
/// itself generates VideoFrameFragment, so receiving one isn't a liveness signal for the host).
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private static readonly TimeSpan TimeoutWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<uint, SessionInfo> _sessions = new();
    private readonly CancellationTokenSource _sweeperCts = new();
    private readonly Task _sweeperTask;

    public event Action<SessionInfo>? SessionExpired;

    public SessionManager()
    {
        _sweeperTask = Task.Run(SweepLoopAsync);
    }

    public void Add(SessionInfo session) => _sessions[session.SessionId] = session;

    public bool TryGet(uint sessionId, out SessionInfo? session) => _sessions.TryGetValue(sessionId, out session);

    public IReadOnlyCollection<SessionInfo> All => (IReadOnlyCollection<SessionInfo>)_sessions.Values;

    public void Touch(uint sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.Touch();
        }
    }

    public async Task<bool> RemoveAsync(uint sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return false;
        }

        await TeardownAsync(session);
        return true;
    }

    private static async Task TeardownAsync(SessionInfo session)
    {
        session.PipelineCts.Cancel();

        try
        {
            if (session.FfmpegProcess is { HasExited: false } proc)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process may have already exited between the check and Kill(); nothing to do.
        }

        if (session.PipelineTask is not null)
        {
            try
            {
                await session.PipelineTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Best-effort: don't let a stuck pipeline task block teardown indefinitely.
            }
        }
    }

    private async Task SweepLoopAsync()
    {
        while (!_sweeperCts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, _sweeperCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = DateTime.UtcNow;
            foreach (var session in _sessions.Values)
            {
                if (!session.ExemptFromTimeout && now - session.LastSeenUtc > TimeoutWindow)
                {
                    if (_sessions.TryRemove(session.SessionId, out _))
                    {
                        await TeardownAsync(session);
                        SessionExpired?.Invoke(session);
                    }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _sweeperCts.Cancel();
        try
        {
            await _sweeperTask;
        }
        catch
        {
            // ignore
        }

        foreach (var session in _sessions.Values)
        {
            await TeardownAsync(session);
        }
        _sessions.Clear();
    }
}
