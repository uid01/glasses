namespace PcHost.Capture;

/// <summary>
/// Generates synthetic "frames" at a fixed rate with zero external dependencies (no ffmpeg, no
/// GPU, no real client needed). Used by `--mock` mode so the network/protocol layer
/// (fragmentation, UDP send, session handling) is fully testable standalone. The bytes are not
/// valid H264 -- just a counter pattern -- since only the transport path is under test here.
/// </summary>
public sealed class MockFrameSource
{
    public required int Fps { get; init; }
    public required int FrameSizeBytes { get; init; }
    public required int KeyframeInterval { get; init; } // every Nth frame is flagged as a keyframe

    public async Task PumpFramesAsync(Action<AnnexBFrame> onFrame, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, Fps));
        uint counter = 0;

        using var timer = new PeriodicTimer(interval);
        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var data = new byte[FrameSizeBytes];
            // Fill with a recognizable counter pattern so a test receiver can sanity-check
            // reassembled byte count / content without needing a real decoder.
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)((counter + i) & 0xFF);
            }

            bool isKeyframe = KeyframeInterval > 0 && counter % (uint)KeyframeInterval == 0;
            onFrame(new AnnexBFrame(data, isKeyframe));
            counter++;
        }
    }
}
