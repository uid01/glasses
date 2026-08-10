using PcHost.Protocol;

namespace PcHost.Capture;

/// <summary>
/// Splits a single frame's bytes into VideoFrameFragment packets per shared-protocol/PROTOCOL.md
/// (max ~1173 bytes of payload per fragment, so the whole datagram stays <= ~1200 bytes).
/// </summary>
public static class FrameFragmenter
{
    public static List<VideoFrameFragment> Fragment(
        uint sessionId,
        uint frameId,
        ReadOnlyMemory<byte> frameData,
        ulong ptsMicros,
        bool isKeyframe)
    {
        int maxPayload = VideoFrameFragment.MaxPayloadLength;
        int fragmentCount = frameData.Length == 0
            ? 1
            : (frameData.Length + maxPayload - 1) / maxPayload;

        var fragments = new List<VideoFrameFragment>(fragmentCount);

        for (int index = 0; index < fragmentCount; index++)
        {
            int offset = index * maxPayload;
            int length = Math.Min(maxPayload, frameData.Length - offset);
            bool isLast = index == fragmentCount - 1;

            var flags = VideoFrameFragment.FrameFlags.None;
            if (isKeyframe)
            {
                flags |= VideoFrameFragment.FrameFlags.Keyframe;
            }
            if (isLast)
            {
                flags |= VideoFrameFragment.FrameFlags.LastFragmentOfFrame;
            }

            fragments.Add(new VideoFrameFragment
            {
                SessionId = sessionId,
                FrameId = frameId,
                FragmentIndex = (ushort)index,
                FragmentCount = (ushort)fragmentCount,
                PtsMicros = ptsMicros,
                Flags = flags,
                Payload = frameData.Slice(offset, length),
            });
        }

        return fragments;
    }
}
