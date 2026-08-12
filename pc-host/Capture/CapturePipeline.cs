using System.Net;
using System.Numerics;
using PcHost.Network;
using PcHost.Render;
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

        if (session.RenderScene is not null)
        {
            return RunRenderedAsync(session, session.RenderScene, logPath, OnFrame, ct);
        }

        var layout = session.Layout
            ?? throw new InvalidOperationException($"Session {session.SessionId} has no MonitorLayout or RenderScene -- one is required for real (non-mock) capture.");
        var capture = new FfmpegCaptureSource
        {
            Layout = layout,
            Fps = session.Fps,
            LogFilePath = logPath,
        };

        var process = capture.Start();
        session.FfmpegProcess = process;

        return capture.PumpFramesAsync(process, OnFrame, ct);
    }

    /// <summary>
    /// Builds the live D3D scene (device, per-monitor captures, meshes) from
    /// <paramref name="spec"/> for this session's lifetime, disposing everything on teardown --
    /// mirrors how each session already gets its own ffmpeg process today (see
    /// RenderedCaptureSource's class doc for why that per-session cost is accepted for now).
    /// </summary>
    private static async Task RunRenderedAsync(SessionInfo session, RenderSceneSpec spec, string logPath, Action<AnnexBFrame> onFrame, CancellationToken ct)
    {
        var renderer = new SceneRenderer(spec.Width, spec.Height);
        var camera = new Camera { AspectRatio = (float)spec.Width / spec.Height };
        var objects = new List<SceneObject>();

        try
        {
            foreach (var objSpec in spec.Objects)
            {
                var monitorCapture = new MonitorCapture(renderer.Device, renderer.Context, objSpec.OutputIndex);
                monitorCapture.WarmUp();

                objects.Add(new SceneObject
                {
                    Capture = monitorCapture,
                    Width = objSpec.PanelWidth,
                    Height = objSpec.PanelHeight,
                    CurvatureDegrees = objSpec.CurvatureDegrees,
                    Position = objSpec.Position,
                    RotationEuler = objSpec.RotationEuler,
                });
            }

            var renderedSource = new RenderedCaptureSource
            {
                Renderer = renderer,
                Objects = objects,
                Camera = camera,
                Fps = session.Fps,
                LogFilePath = logPath,
            };

            var process = renderedSource.Start();
            session.FfmpegProcess = process;

            await renderedSource.PumpFramesAsync(process, onFrame, ct);
        }
        finally
        {
            foreach (var obj in objects)
            {
                obj.Dispose();
            }
            renderer.Dispose();
        }
    }
}
