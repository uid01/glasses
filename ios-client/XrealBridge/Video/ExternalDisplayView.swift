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
