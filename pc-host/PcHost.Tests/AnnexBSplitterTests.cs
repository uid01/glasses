using PcHost.Capture;
using Xunit;

namespace PcHost.Tests;

public class AnnexBSplitterTests
{
    // Builds a minimal Annex-B NAL: start code + a 1-byte header (nal_ref_idc/type) + payload.
    private static byte[] Nal(int scLen, byte nalType, byte refIdc, params byte[] payload)
    {
        byte[] sc = scLen == 4 ? new byte[] { 0, 0, 0, 1 } : new byte[] { 0, 0, 1 };
        byte header = (byte)(((refIdc & 0x3) << 5) | (nalType & 0x1F));
        var result = new byte[sc.Length + 1 + payload.Length];
        sc.CopyTo(result, 0);
        result[sc.Length] = header;
        payload.CopyTo(result, sc.Length + 1);
        return result;
    }

    private static byte[] Concat(params byte[][] chunks) => chunks.SelectMany(c => c).ToArray();

    [Fact]
    public void GroupsLeadingSpsPpsSeiWithFirstIdrIntoOneKeyframe()
    {
        var sps = Nal(4, 7, 3, 1, 2, 3);
        var pps = Nal(4, 8, 3, 4, 5);
        var sei = Nal(3, 6, 0, 9, 9, 9, 9);
        var idr = Nal(3, 5, 3, 10, 11, 12, 13);
        var pSlice = Nal(3, 1, 2, 20, 21);

        var stream = Concat(sps, pps, sei, idr, pSlice);

        var splitter = new AnnexBFrameSplitter();
        var frames = splitter.Feed(stream);

        // Only frame 0 is complete once pSlice's start code arrives (its own bytes belong to frame 1,
        // which stays open until the next boundary or Flush()).
        Assert.Single(frames);
        var frame0 = frames[0];
        Assert.True(frame0.IsKeyframe);

        int expectedLen = sps.Length + pps.Length + sei.Length + idr.Length;
        Assert.Equal(expectedLen, frame0.Data.Length);

        // Sanity: frame bytes start with the SPS start code and end right before pSlice's start code.
        Assert.Equal(0, frame0.Data[0]);
        Assert.Equal(1, frame0.Data[3]);
    }

    [Fact]
    public void SplitsConsecutivePSlicesIntoSeparateNonKeyframes()
    {
        var idr = Nal(4, 5, 3, 1);
        var p1 = Nal(3, 1, 2, 2);
        var p2 = Nal(3, 1, 2, 3);
        var p3 = Nal(3, 1, 2, 4);

        var stream = Concat(idr, p1, p2, p3);

        var splitter = new AnnexBFrameSplitter();
        var frames = splitter.Feed(stream);

        // All of idr, p1, and p2 close out (each one's frame boundary is closed by the next
        // NAL's start code, and all four NALs arrive in this single Feed call); only p3 -- the
        // last NAL in the buffer, with no following start code yet -- stays open until Flush().
        Assert.Equal(3, frames.Count);
        Assert.True(frames[0].IsKeyframe);
        Assert.Equal(idr.Length, frames[0].Data.Length);
        Assert.False(frames[1].IsKeyframe);
        Assert.Equal(p1.Length, frames[1].Data.Length);
        Assert.False(frames[2].IsKeyframe);
        Assert.Equal(p2.Length, frames[2].Data.Length);

        var trailing = splitter.Flush();
        Assert.NotNull(trailing);
        Assert.False(trailing!.IsKeyframe);
        Assert.Equal(p3.Length, trailing!.Data.Length);
    }

    [Fact]
    public void HandlesStartCodesSplitAcrossFeedCalls()
    {
        var idr = Nal(4, 5, 3, 1, 2, 3);
        var p1 = Nal(3, 1, 2, 4, 5);

        var stream = Concat(idr, p1);

        var splitter = new AnnexBFrameSplitter();
        var allFrames = new List<AnnexBFrame>();

        // Feed one byte at a time to force every possible start-code split across calls.
        foreach (var b in stream)
        {
            allFrames.AddRange(splitter.Feed(new[] { b }));
        }

        var trailing = splitter.Flush();
        if (trailing is not null)
        {
            allFrames.Add(trailing);
        }

        Assert.Equal(2, allFrames.Count);
        Assert.True(allFrames[0].IsKeyframe);
        Assert.Equal(idr.Length, allFrames[0].Data.Length);
        Assert.False(allFrames[1].IsKeyframe);
        Assert.Equal(p1.Length, allFrames[1].Data.Length);
    }

    [Fact]
    public void FlushReturnsNullWhenNothingBuffered()
    {
        var splitter = new AnnexBFrameSplitter();
        Assert.Null(splitter.Flush());
    }

    [Fact]
    public void EmptyFeedYieldsNoFrames()
    {
        var splitter = new AnnexBFrameSplitter();
        var frames = splitter.Feed(ReadOnlySpan<byte>.Empty);
        Assert.Empty(frames);
    }

    /// <summary>
    /// Regression test for a real bug found while smoke-testing against actual ffmpeg output on
    /// this machine: libx264's zero-latency tune uses slice-based threading, so a single picture
    /// can be encoded as many VCL NAL units (all sharing the same nal_unit_type) instead of one.
    /// Without AUD-aware framing, the old "new VCL NAL after one already seen" heuristic split
    /// one real picture into 17 bogus "frames" (this exact multi-slice-IDR shape, reproduced
    /// here at a smaller scale, was observed via ffprobe/manual NAL analysis of real capture
    /// output). AUD NALs (type 9) must be treated as the authoritative boundary once present.
    /// </summary>
    [Fact]
    public void GroupsMultiSliceIdrPictureIntoOneFrameWhenAudsArePresent()
    {
        var aud1 = Nal(4, 9, 0, 0xF0);
        var sps = Nal(3, 7, 3, 1, 2, 3);
        var pps = Nal(3, 8, 3, 4, 5);
        var idrSlice1 = Nal(3, 5, 3, 10, 11);
        var idrSlice2 = Nal(3, 5, 3, 12, 13); // same picture, second slice -- still type 5
        var idrSlice3 = Nal(3, 5, 3, 14, 15); // third slice
        var aud2 = Nal(3, 9, 0, 0xF0);
        var pSlice1 = Nal(3, 1, 2, 20);
        var pSlice2 = Nal(3, 1, 2, 21); // same P picture, second slice

        var stream = Concat(aud1, sps, pps, idrSlice1, idrSlice2, idrSlice3, aud2, pSlice1, pSlice2);

        var splitter = new AnnexBFrameSplitter();
        var frames = splitter.Feed(stream);

        // Only the first picture (everything up to aud2) is closed by Feed; the second picture
        // stays open until Flush() since nothing follows it in this buffer.
        Assert.Single(frames);
        var idrFrame = frames[0];
        Assert.True(idrFrame.IsKeyframe);
        int expectedIdrFrameLen = aud1.Length + sps.Length + pps.Length + idrSlice1.Length + idrSlice2.Length + idrSlice3.Length;
        Assert.Equal(expectedIdrFrameLen, idrFrame.Data.Length);

        var trailing = splitter.Flush();
        Assert.NotNull(trailing);
        Assert.False(trailing!.IsKeyframe);
        Assert.Equal(aud2.Length + pSlice1.Length + pSlice2.Length, trailing!.Data.Length);
    }

    [Fact]
    public void FallsBackToVclHeuristicWhenNoAudEverAppears()
    {
        // No AUDs anywhere in this stream -- must behave exactly like the pre-AUD heuristic
        // (each VCL NAL is its own frame), matching SplitsConsecutivePSlicesIntoSeparateNonKeyframes.
        var idr = Nal(4, 5, 3, 1);
        var p1 = Nal(3, 1, 2, 2);

        var stream = Concat(idr, p1);
        var splitter = new AnnexBFrameSplitter();
        var frames = splitter.Feed(stream);

        Assert.Single(frames);
        Assert.True(frames[0].IsKeyframe);
        Assert.Equal(idr.Length, frames[0].Data.Length);
    }
}
