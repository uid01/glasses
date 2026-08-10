namespace PcHost.Capture;

/// <summary>
/// A completed H264 Annex-B access unit (frame) ready for fragmentation and transmission.
/// </summary>
public sealed record AnnexBFrame(byte[] Data, bool IsKeyframe);

/// <summary>
/// Incrementally splits a raw Annex-B H264 byte stream (as produced by ffmpeg's "-f h264 -"
/// stdout) into access units (frames), suitable for fragmentation per
/// shared-protocol/PROTOCOL.md's VideoFrameFragment packet.
///
/// This is intentionally not a full H264 parser -- it only needs to correctly group NAL units
/// into per-frame byte ranges. It is NOT safe to assume "one VCL NAL = one picture": verified
/// empirically on this machine that libx264/nvenc under a zero-latency tune use slice-based
/// threading, which splits every picture (including IDRs) into multiple slice NAL units (all
/// sharing the same nal_unit_type) to parallelize across CPU cores without adding frame-pipeline
/// latency -- e.g. 17 slice NALs per picture was observed here, all tagged type 5 for an IDR.
/// Naively treating each VCL NAL as a new frame would fragment a single picture into a dozen-plus
/// bogus "frames".
///
/// The fix: <see cref="Capture.FfmpegCaptureSource"/> asks the encoder for Access Unit
/// Delimiters (`-x264-params aud=1` / `-aud 1`), NAL type 9, exactly one per picture, and this
/// splitter treats AUD as the authoritative frame boundary once any AUD has been observed on the
/// stream. Until the first AUD arrives (e.g. a differently-configured encoder that doesn't emit
/// them), it falls back to the simpler heuristic of "a new VCL slice NAL, or any non-VCL NAL,
/// following a VCL NAL already seen in the open frame closes it" -- correct only for
/// single-slice-per-picture streams, but a reasonable safety net.
/// </summary>
public sealed class AnnexBFrameSplitter
{
    private const int NalTypeAud = 9;

    // Compact the internal buffer once the already-consumed prefix grows past this size, to
    // bound memory without paying the RemoveRange cost on every single frame.
    private const int CompactThreshold = 64 * 1024;

    private readonly List<byte> _buf = new();
    private int _scanPos;
    private int _frameStartOffset = -1;
    private bool _frameHasVcl;
    private bool _frameHasKeyframeVcl;
    private bool _sawAud;

    /// <summary>
    /// Feeds a new chunk of bytes read from ffmpeg's stdout. Returns zero or more frames that
    /// became complete as a result (a single call can yield multiple frames if the chunk was
    /// large, or zero if the chunk didn't complete a frame boundary yet).
    /// </summary>
    public List<AnnexBFrame> Feed(ReadOnlySpan<byte> chunk)
    {
        var completed = new List<AnnexBFrame>();

        foreach (var b in chunk)
        {
            _buf.Add(b);
        }

        int i = _scanPos;
        while (true)
        {
            int start;
            int scLen;

            if (i + 3 < _buf.Count && _buf[i] == 0 && _buf[i + 1] == 0 && _buf[i + 2] == 0 && _buf[i + 3] == 1)
            {
                start = i;
                scLen = 4;
            }
            else if (i + 2 < _buf.Count && _buf[i] == 0 && _buf[i + 1] == 0 && _buf[i + 2] == 1)
            {
                start = i;
                scLen = 3;
            }
            else
            {
                if (i >= _buf.Count)
                {
                    break;
                }
                i++;
                continue;
            }

            int nalTypeIndex = start + scLen;
            if (nalTypeIndex >= _buf.Count)
            {
                // Start code found but we don't have the NAL type byte yet; wait for more data.
                break;
            }

            byte nalHeader = _buf[nalTypeIndex];
            int nalType = nalHeader & 0x1F;
            bool isVcl = nalType is 1 or 5;
            bool isAud = nalType == NalTypeAud;

            if (_frameStartOffset < 0)
            {
                // First start code ever seen; open the first frame here. If this very first NAL
                // is itself an AUD, _sawAud must be set right away -- otherwise the fallback
                // heuristic would stay armed and fire on this frame's later slice NALs before
                // the "already saw an AUD" gate had a chance to engage.
                _frameStartOffset = start;
                _frameHasVcl = false;
                _frameHasKeyframeVcl = false;
                if (isAud)
                {
                    _sawAud = true;
                }
            }
            else if (isAud)
            {
                // AUD is an explicit, authoritative "new picture starts here" marker (see class
                // doc). Once we've seen one, this is the only signal trusted for boundaries.
                _sawAud = true;
                completed.Add(EmitFrame(start));
                _frameStartOffset = start;
                _frameHasVcl = false;
                _frameHasKeyframeVcl = false;
            }
            else if (!_sawAud && isVcl && _frameHasVcl)
            {
                // Fallback heuristic (no AUD seen yet on this stream): a new slice NAL while the
                // open frame already has one implies a new picture started.
                completed.Add(EmitFrame(start));
                _frameStartOffset = start;
                _frameHasVcl = false;
                _frameHasKeyframeVcl = false;
            }
            else if (!_sawAud && !isVcl && _frameHasVcl)
            {
                // Fallback heuristic: non-VCL NAL after a slice NAL, with no AUD framing
                // available, implies it belongs to the next access unit.
                completed.Add(EmitFrame(start));
                _frameStartOffset = start;
                _frameHasVcl = false;
                _frameHasKeyframeVcl = false;
            }

            if (isVcl)
            {
                _frameHasVcl = true;
                if (nalType == 5)
                {
                    _frameHasKeyframeVcl = true;
                }
            }

            i = nalTypeIndex + 1;
        }

        _scanPos = Math.Max(0, i - 3);

        CompactIfNeeded();

        return completed;
    }

    /// <summary>
    /// Call when the underlying stream ends (ffmpeg process exited) to flush any trailing
    /// buffered frame. Returns null if there's nothing pending.
    /// </summary>
    public AnnexBFrame? Flush()
    {
        if (_frameStartOffset < 0 || _frameStartOffset >= _buf.Count || !_frameHasVcl)
        {
            return null;
        }

        return EmitFrame(_buf.Count);
    }

    private AnnexBFrame EmitFrame(int endExclusive)
    {
        int start = _frameStartOffset;
        int length = endExclusive - start;
        var data = new byte[length];
        _buf.CopyTo(start, data, 0, length);
        return new AnnexBFrame(data, _frameHasKeyframeVcl);
    }

    private void CompactIfNeeded()
    {
        int consumable = _frameStartOffset < 0 ? 0 : _frameStartOffset;
        consumable = Math.Min(consumable, _scanPos);
        if (consumable < CompactThreshold)
        {
            return;
        }

        _buf.RemoveRange(0, consumable);
        _scanPos -= consumable;
        if (_frameStartOffset >= 0)
        {
            _frameStartOffset -= consumable;
        }
    }
}
