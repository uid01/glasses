import AVFoundation
import CoreMedia
import CoreVideo
import UIKit

/// Full-bleed view shown on the external `UIWindowScene` (the XREAL
/// glasses, connected as a native external display over USB-C). Backed by
/// an `AVSampleBufferDisplayLayer`, the standard low-latency path for
/// displaying already-decoded video frames (as opposed to `AVPlayerLayer`,
/// which expects to own its own decode pipeline via `AVPlayer` -- we
/// already decode ourselves via `H264Decoder`/VideoToolbox).
///
/// NOTE (unverified locally -- no macOS/Xcode available in this build
/// environment): the `CMVideoFormatDescriptionCreateForImageBuffer` /
/// `CMSampleBufferCreateForImageBuffer` pair used here to re-wrap a
/// decoded `CVImageBuffer` for display is the standard documented
/// approach, but has not been compiled or run.
final class ExternalDisplayView: UIView, H264DecoderDelegate {
    /// CONFIRMED LIVE ON REAL HARDWARE (glasses + phone bridge): the image on the glasses reads
    /// mirrored left-right (vertically correct -- taskbar stays put -- but horizontally
    /// backwards), and a device screenshot captures the same mirrored content, so this isn't a
    /// screenshot-capture artifact or a glasses-optics illusion -- it's really in the pixel data
    /// reaching the display layer. Nothing in this file (or H264Decoder.swift, or
    /// SceneDelegate.swift) sets any transform anywhere, and the PC-side render pipeline has been
    /// extensively verified as correct (non-mirrored) throughout this project, so the cause isn't
    /// identifiable by static reading alone -- it needs an actual on-device test to narrow down.
    ///
    /// This flag exists to make that test a single toggle rather than a guess-and-recompile loop.
    /// Currently ON to test the hypothesis on real hardware via the next sideload build -- if the
    /// glasses now read correctly, this comment (and the flag) should collapse down to just "yes,
    /// needed" with the mystery-diagnosis paragraph above trimmed out; if it makes things worse
    /// (or does nothing), flip back to `false` and this stays an open question.
    static let mirrorHorizontallyForExternalDisplay = true

    override class var layerClass: AnyClass { AVSampleBufferDisplayLayer.self }

    var displayLayer: AVSampleBufferDisplayLayer {
        // Safe force-cast: `layerClass` above guarantees `self.layer` is
        // always an AVSampleBufferDisplayLayer for instances of this class.
        return layer as! AVSampleBufferDisplayLayer // swiftlint:disable:this force_cast
    }

    override init(frame: CGRect) {
        super.init(frame: frame)
        commonInit()
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        commonInit()
    }

    private func commonInit() {
        backgroundColor = .black
        // No letterboxing chrome -- fill the glasses' full frame. If the
        // negotiated resolution's aspect ratio doesn't exactly match the
        // display, .resizeAspect letterboxes with black bars rather than
        // distorting the image; switch to .resize if the coordinator later
        // decides distortion-free full fill is preferred over accurate
        // aspect ratio.
        displayLayer.videoGravity = .resizeAspect

        if Self.mirrorHorizontallyForExternalDisplay {
            // A mirror-of-a-mirror is not a mirror: if the source is genuinely arriving
            // horizontally flipped somewhere upstream of this layer, undoing it here (rather than
            // hunting for the actual root cause blind, with no way to compile/test locally) is the
            // pragmatic fix. CALayer.anchorPoint defaults to (0.5, 0.5) -- the layer's own center --
            // so this scale flips the content in place rather than shifting it off-frame.
            displayLayer.transform = CATransform3DMakeScale(-1, 1, 1)
        }
    }

    func flush() {
        displayLayer.flush()
    }

    func flushAndRemoveImage() {
        displayLayer.flushAndRemoveImage()
    }

    // MARK: - H264DecoderDelegate

    func h264Decoder(_ decoder: H264Decoder, didDecode imageBuffer: CVImageBuffer, presentationTimeMicros: UInt64) {
        var formatDescription: CMVideoFormatDescription?
        let fdStatus = CMVideoFormatDescriptionCreateForImageBuffer(
            allocator: kCFAllocatorDefault,
            imageBuffer: imageBuffer,
            formatDescriptionOut: &formatDescription
        )
        guard fdStatus == noErr, let formatDescription = formatDescription else { return }

        var timing = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: CMTime(value: Int64(presentationTimeMicros), timescale: 1_000_000),
            decodeTimeStamp: .invalid
        )

        var sampleBuffer: CMSampleBuffer?
        let sbStatus = CMSampleBufferCreateForImageBuffer(
            allocator: kCFAllocatorDefault,
            imageBuffer: imageBuffer,
            dataReady: true,
            makeDataReadyCallback: nil,
            refcon: nil,
            formatDescription: formatDescription,
            sampleTiming: &timing,
            sampleBufferOut: &sampleBuffer
        )
        guard sbStatus == noErr, let sampleBuffer = sampleBuffer else { return }

        // AVSampleBufferDisplayLayer must be driven from a single
        // serialized queue; main is the simplest choice and there's no way
        // to profile actual latency impact in this environment.
        DispatchQueue.main.async { [weak self] in
            guard let self = self else { return }
            if self.displayLayer.status == .failed {
                self.displayLayer.flush()
            }
            self.displayLayer.enqueue(sampleBuffer)
        }
    }
}
