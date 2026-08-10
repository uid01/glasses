using PcHost.Capture;
using PcHost.Protocol;
using Xunit;

namespace PcHost.Tests;

public class FragmentationTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(1173, 1)]     // exactly one fragment's worth
    [InlineData(1174, 2)]     // one byte over -> needs a second fragment
    [InlineData(1173 * 3, 3)] // exact multiple
    [InlineData(1173 * 3 + 1, 4)]
    [InlineData(50_000, 43)]  // a realistic-ish IDR frame size
    public void FragmentCountMatchesCeilingDivision(int frameSize, int expectedFragmentCount)
    {
        var data = new byte[frameSize];
        var fragments = FrameFragmenter.Fragment(sessionId: 1, frameId: 1, data, ptsMicros: 0, isKeyframe: false);

        Assert.Equal(expectedFragmentCount, fragments.Count);
        Assert.All(fragments, f => Assert.Equal((ushort)expectedFragmentCount, f.FragmentCount));
    }

    [Fact]
    public void FragmentPayloadsReassembleToOriginalBytes()
    {
        var data = new byte[5000];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 256);
        }

        var fragments = FrameFragmenter.Fragment(sessionId: 9, frameId: 3, data, ptsMicros: 1000, isKeyframe: true);

        // Every fragment payload must be <= MaxPayloadLength.
        Assert.All(fragments, f => Assert.True(f.Payload.Length <= VideoFrameFragment.MaxPayloadLength));

        // Fragment indices must be contiguous 0..count-1.
        Assert.Equal(Enumerable.Range(0, fragments.Count).Select(i => (ushort)i), fragments.Select(f => f.FragmentIndex));

        // Reassembling payloads in fragmentIndex order must reproduce the original bytes exactly.
        var reassembled = fragments.OrderBy(f => f.FragmentIndex).SelectMany(f => f.Payload.ToArray()).ToArray();
        Assert.Equal(data, reassembled);
    }

    [Fact]
    public void KeyframeFlagSetOnEveryFragmentOfAKeyframe()
    {
        var data = new byte[3000];
        var fragments = FrameFragmenter.Fragment(1, 1, data, 0, isKeyframe: true);

        Assert.True(fragments.Count > 1);
        Assert.All(fragments, f => Assert.True((f.Flags & VideoFrameFragment.FrameFlags.Keyframe) != 0));
    }

    [Fact]
    public void NonKeyframeHasNoKeyframeFlagOnAnyFragment()
    {
        var data = new byte[3000];
        var fragments = FrameFragmenter.Fragment(1, 1, data, 0, isKeyframe: false);

        Assert.All(fragments, f => Assert.True((f.Flags & VideoFrameFragment.FrameFlags.Keyframe) == 0));
    }

    [Fact]
    public void OnlyLastFragmentHasLastFragmentFlag()
    {
        var data = new byte[3000];
        var fragments = FrameFragmenter.Fragment(1, 1, data, 0, isKeyframe: false);

        for (int i = 0; i < fragments.Count; i++)
        {
            bool hasLastFlag = (fragments[i].Flags & VideoFrameFragment.FrameFlags.LastFragmentOfFrame) != 0;
            Assert.Equal(i == fragments.Count - 1, hasLastFlag);
        }
    }

    [Fact]
    public void SerializedFragmentSizeStaysUnderUdpSafetyTarget()
    {
        var data = new byte[10_000];
        var fragments = FrameFragmenter.Fragment(1, 1, data, 0, isKeyframe: false);

        foreach (var fragment in fragments)
        {
            // PROTOCOL.md targets <= ~1200 bytes total datagram size.
            Assert.True(fragment.WireSize <= 1200, $"fragment wire size {fragment.WireSize} exceeds 1200-byte target");
        }
    }
}
