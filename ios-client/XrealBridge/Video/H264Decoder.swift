import CoreMedia
import CoreVideo
import Foundation
import VideoToolbox

protocol H264DecoderDelegate: AnyObject {
    /// Called (on VideoToolbox's own callback thread, NOT necessarily main)
    /// with each successfully decoded frame.
    func h264Decoder(_ decoder: H264Decoder, didDecode imageBuffer: CVImageBuffer, presentationTimeMicros: UInt64)
}

/// Wraps a `VTDecompressionSession` that turns reassembled Annex-B H264
/// access units (from `VideoReceiver`) into decoded `CVImageBuffer`s
/// (pixel buffers).
///
/// NOTE (unverified locally -- no VideoToolbox-capable machine available
/// in this build environment): this class follows the standard,
/// well-documented VideoToolbox decode pattern --
///   1. extract SPS/PPS (NAL types 7/8) from the Annex-B stream,
///   2. `CMVideoFormatDescriptionCreateFromH264ParameterSets` to build a
///      format description,
///   3. `VTDecompressionSessionCreate`,
///   4. convert each Annex-B access unit's non-parameter-set NAL units to
///      AVCC (4-byte big-endian length prefixes, no start codes) in a
///      `CMBlockBuffer` -- VideoToolbox decode requires length-prefixed
///      NALs matching the format description, not raw Annex-B start
///      codes, which is the detail most worth double-checking first on a
///      real device,
///   5. wrap that in a `CMSampleBuffer` and call
///      `VTDecompressionSessionDecodeFrame`.
/// None of this has been compiled or run; it has not been possible to
/// validate exact argument-label spelling against the real SDK headers in
/// this environment.
final class H264Decoder {
    weak var delegate: H264DecoderDelegate?

    private var formatDescription: CMVideoFormatDescription?
    private var session: VTDecompressionSession?
    private var lastSPS: [UInt8]?
    private var lastPPS: [UInt8]?

    init() {}

    deinit {
        invalidate()
    }

    /// Tears down the current decompression session cleanly. Must be
    /// called on teardown/reconnect rather than just dropping the
    /// reference, so we don't leak a VTDecompressionSession every time the
    /// PC connection resets.
    func invalidate() {
        if let s = session {
            VTDecompressionSessionInvalidate(s)
        }
        session = nil
        formatDescription = nil
        lastSPS = nil
        lastPPS = nil
    }

    /// Feed one reassembled Annex-B access unit (may contain in-band
    /// SPS/PPS NALs, typically alongside a keyframe).
    func decode(accessUnit: [UInt8], presentationTimeMicros: UInt64, isKeyframe: Bool) {
        let nalUnits = Self.splitAnnexBNALUnits(accessUnit)
        guard !nalUnits.isEmpty else { return }

        var spsData: [UInt8]?
        var ppsData: [UInt8]?
        var frameNALUnits: [[UInt8]] = []
        frameNALUnits.reserveCapacity(nalUnits.count)

        for nal in nalUnits {
            guard let first = nal.first else { continue }
            let nalType = first & 0x1F
            switch nalType {
            case 7: spsData = nal
            case 8: ppsData = nal
            default: frameNALUnits.append(nal)
            }
        }

        if let sps = spsData, let pps = ppsData, sps != lastSPS || pps != lastPPS {
            rebuildSession(sps: sps, pps: pps)
        }

        guard let formatDescription = formatDescription, session != nil else {
            // No parameter sets seen yet -- can't decode until a keyframe
            // carrying SPS/PPS arrives.
            return
        }
        guard !frameNALUnits.isEmpty else { return }

        guard let blockBuffer = Self.makeAVCCBlockBuffer(from: frameNALUnits) else { return }
        guard let sampleBuffer = Self.makeSampleBuffer(
            blockBuffer: blockBuffer,
            formatDescription: formatDescription,
            presentationTimeMicros: presentationTimeMicros
        ) else { return }

        decodeSampleBuffer(sampleBuffer)
    }

    private func rebuildSession(sps: [UInt8], pps: [UInt8]) {
        lastSPS = sps
        lastPPS = pps

        var newFormatDescription: CMVideoFormatDescription?
        let fdStatus = sps.withUnsafeBufferPointer { spsPtr -> OSStatus in
            return pps.withUnsafeBufferPointer { ppsPtr -> OSStatus in
                guard let spsBase = spsPtr.baseAddress, let ppsBase = ppsPtr.baseAddress else {
                    return kCMBlockBufferBadPointerParameterErr
                }
                let pointers: [UnsafePointer<UInt8>] = [spsBase, ppsBase]
                let sizes: [Int] = [spsPtr.count, ppsPtr.count]
                return CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    allocator: kCFAllocatorDefault,
                    parameterSetCount: 2,
                    parameterSetPointers: pointers,
                    parameterSetSizes: sizes,
                    nalUnitHeaderLength: 4,
                    formatDescriptionOut: &newFormatDescription
                )
            }
        }
        guard fdStatus == noErr, let newFormatDescription = newFormatDescription else { return }
        formatDescription = newFormatDescription

        if let existing = session {
            VTDecompressionSessionInvalidate(existing)
            session = nil
        }

        var outputCallback = VTDecompressionOutputCallbackRecord(
            decompressionOutputCallback: decompressionOutputCallback,
            decompressionOutputRefCon: Unmanaged.passUnretained(self).toOpaque()
        )

        let destinationAttributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarFullRange,
            kCVPixelBufferIOSurfacePropertiesKey as String: [String: Any]()
        ]

        var newSession: VTDecompressionSession?
        let createStatus = VTDecompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            formatDescription: newFormatDescription,
            decoderSpecification: nil,
            imageBufferAttributes: destinationAttributes as CFDictionary,
            outputCallback: &outputCallback,
            decompressionSessionOut: &newSession
        )
        if createStatus == noErr {
            session = newSession
        }
    }

    private func decodeSampleBuffer(_ sampleBuffer: CMSampleBuffer) {
        guard let session = session else { return }
        var flagsOut = VTDecodeInfoFlags()
        // Empty decode flags: synchronous, non-realtime-hinted decode.
        // Kept deliberately minimal to avoid relying on the exact spelling
        // of optional hint flags (e.g. "1x realtime playback") that
        // couldn't be checked against the real SDK headers here.
        VTDecompressionSessionDecodeFrame(
            session,
            sampleBuffer: sampleBuffer,
            flags: [],
            frameRefcon: nil,
            infoFlagsOut: &flagsOut
        )
    }

    fileprivate func handleDecodedFrame(imageBuffer: CVImageBuffer?, presentationTimeStamp: CMTime) {
        guard let imageBuffer = imageBuffer else { return }
        let seconds = CMTimeGetSeconds(presentationTimeStamp)
        let micros = seconds.isFinite ? UInt64(max(0, seconds * 1_000_000)) : 0
        delegate?.h264Decoder(self, didDecode: imageBuffer, presentationTimeMicros: micros)
    }

    // MARK: - Annex-B parsing / AVCC conversion

    /// Splits an Annex-B byte stream (0x000001 or 0x00000001 start codes)
    /// into individual NAL units with the start code stripped.
    static func splitAnnexBNALUnits(_ data: [UInt8]) -> [[UInt8]] {
        let count = data.count
        var starts: [Int] = []
        var i = 0
        while i + 2 < count {
            if data[i] == 0, data[i + 1] == 0, data[i + 2] == 1 {
                starts.append(i + 3)
                i += 3
            } else if i + 3 < count, data[i] == 0, data[i + 1] == 0, data[i + 2] == 0, data[i + 3] == 1 {
                starts.append(i + 4)
                i += 4
            } else {
                i += 1
            }
        }
        guard !starts.isEmpty else { return [] }

        var result: [[UInt8]] = []
        result.reserveCapacity(starts.count)
        for (idx, start) in starts.enumerated() {
            let end: Int
            if idx + 1 < starts.count {
                end = startCodeBegin(before: starts[idx + 1], in: data)
            } else {
                end = count
            }
            guard end > start else { continue }
            result.append(Array(data[start..<end]))
        }
        return result
    }

    /// Given the index just past a start code, walks back to find where
    /// that start code (3 or 4 bytes: 00 00 01 or 00 00 00 01) began, so
    /// the previous NAL unit's true end (exclusive of the next start code)
    /// can be computed.
    private static func startCodeBegin(before nextNALStart: Int, in data: [UInt8]) -> Int {
        if nextNALStart >= 4,
           data[nextNALStart - 4] == 0, data[nextNALStart - 3] == 0,
           data[nextNALStart - 2] == 0, data[nextNALStart - 1] == 1 {
            return nextNALStart - 4
        }
        if nextNALStart >= 3,
           data[nextNALStart - 3] == 0, data[nextNALStart - 2] == 0, data[nextNALStart - 1] == 1 {
            return nextNALStart - 3
        }
        return nextNALStart
    }

    /// Builds a single `CMBlockBuffer` containing all given NAL units
    /// back-to-back, each prefixed with its 4-byte big-endian length
    /// (AVCC format), which is what `VTDecompressionSession` expects (as
    /// opposed to Annex-B start codes).
    static func makeAVCCBlockBuffer(from nalUnits: [[UInt8]]) -> CMBlockBuffer? {
        var avcc: [UInt8] = []
        avcc.reserveCapacity(nalUnits.reduce(0) { $0 + 4 + $1.count })
        for nal in nalUnits {
            let length = UInt32(nal.count)
            avcc.append(UInt8((length >> 24) & 0xFF))
            avcc.append(UInt8((length >> 16) & 0xFF))
            avcc.append(UInt8((length >> 8) & 0xFF))
            avcc.append(UInt8(length & 0xFF))
            avcc.append(contentsOf: nal)
        }
        guard !avcc.isEmpty else { return nil }

        var blockBuffer: CMBlockBuffer?
        let createStatus = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,
            blockLength: avcc.count,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: avcc.count,
            flags: CMBlockBufferFlags(rawValue: 0),
            blockBufferOut: &blockBuffer
        )
        guard createStatus == kCMBlockBufferNoErr, let buffer = blockBuffer else { return nil }

        let copyStatus = avcc.withUnsafeBytes { rawBuffer -> OSStatus in
            guard let base = rawBuffer.baseAddress else { return kCMBlockBufferBadPointerParameterErr }
            return CMBlockBufferReplaceDataBytes(
                with: base,
                blockBuffer: buffer,
                offsetIntoDestination: 0,
                dataLength: avcc.count
            )
        }
        guard copyStatus == kCMBlockBufferNoErr else { return nil }
        return buffer
    }

    static func makeSampleBuffer(
        blockBuffer: CMBlockBuffer,
        formatDescription: CMVideoFormatDescription,
        presentationTimeMicros: UInt64
    ) -> CMSampleBuffer? {
        var sampleBuffer: CMSampleBuffer?
        let dataLength = CMBlockBufferGetDataLength(blockBuffer)
        var timing = CMSampleTimingInfo(
            duration: .invalid,
            presentationTimeStamp: CMTime(value: Int64(presentationTimeMicros), timescale: 1_000_000),
            decodeTimeStamp: .invalid
        )
        var sampleSize = dataLength
        let status = CMSampleBufferCreate(
            allocator: kCFAllocatorDefault,
            dataBuffer: blockBuffer,
            dataReady: true,
            makeDataReadyCallback: nil,
            refcon: nil,
            formatDescription: formatDescription,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleSizeEntryCount: 1,
            sampleSizeArray: &sampleSize,
            sampleBufferOut: &sampleBuffer
        )
        guard status == noErr else { return nil }
        return sampleBuffer
    }
}

/// C-style VideoToolbox decompression output callback. Must be a
/// non-capturing top-level function so it can be used as a raw C function
/// pointer; the owning `H264Decoder` instance is threaded through via
/// `decompressionOutputRefCon` (set to `Unmanaged.passUnretained(self)` in
/// `rebuildSession`).
private func decompressionOutputCallback(
    _ decompressionOutputRefCon: UnsafeMutableRawPointer?,
    _ sourceFrameRefCon: UnsafeMutableRawPointer?,
    _ status: OSStatus,
    _ infoFlags: VTDecodeInfoFlags,
    _ imageBuffer: CVImageBuffer?,
    _ presentationTimeStamp: CMTime,
    _ presentationDuration: CMTime
) {
    guard status == noErr, let refCon = decompressionOutputRefCon else { return }
    let decoder = Unmanaged<H264Decoder>.fromOpaque(refCon).takeUnretainedValue()
    decoder.handleDecodedFrame(imageBuffer: imageBuffer, presentationTimeStamp: presentationTimeStamp)
}
