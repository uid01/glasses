using System.Net;
using PcHost.Network;
using PcHost.Session;

namespace PcHost.Capture;

/// <summary>
/// Wires a session's frame source (real ffmpeg capture, or the synthetic mock generator) to
/// the fragmenter and <see cref="VideoSender"/>. One instance of the pump loop runs per session.
/// </summary>
public static class CapturePipeline
{
    public static Task RunAsync(SessionInfo session, VideoSender sender, bool mock, string logDirectory, CancellationToken ct)
    {
        var destination = new IPEndPoint(session.ClientAddress, session.VideoPort);

        void OnFrame(AnnexBFrame frame)
        {
            uint frameId = session.NextFrameId();
            ulong pts = session.ElapsedMicros();
            var fragments = FrameFragmenter.Fragment(session.SessionId, frameId, frame.Data, pts, frame.IsKeyframe);
            foreach (var fragment in fragments)
            {
                sender.Send(destination, fragment);
            }
        }

        if (mock)
        {
            var mockSource = new MockFrameSource
            {
                Fps = session.Fps,
                FrameSizeBytes = 32 * 1024,
                KeyframeInterval = session.Fps * 2, // roughly every 2 seconds, mirrors real GOP size
            };
            return mockSource.PumpFramesAsync(OnFrame, ct);
        }

        var logPath = Path.Combine(logDirectory, $"ffmpeg-session-{session.SessionId}.log");
        var capture = new FfmpegCaptureSource
        {
            Width = session.Width,
            Height = session.Height,
            Fps = session.Fps,
            LogFilePath = logPath,
        };

        var process = capture.Start();
        session.FfmpegProcess = process;

        return capture.PumpFramesAsync(process, OnFrame, ct);
    }
}
