import CFFmpeg
import Foundation

/// Decodes a video file to flat tightly-packed BGRA frames (the portable
/// interchange format for the Vulkan staging→image upload). Software decode
/// via libavcodec; YUV→BGRA conversion via libswscale. Hardware decode
/// (DXVA2/D3D11VA/NVDEC) is a later optimization — see
/// docs/windows-media-engine-design.md.
///
/// One decoder owns one AVFormatContext + AVCodecContext + SwsContext and
/// yields frames sequentially. Seeking flushes the codec per FFmpeg contract.
/// Not Sendable — owned by one decode worker; callers snapshot the returned
/// BGRA bytes before crossing isolation boundaries.
public final class FFmpegDecoder {
    public enum DecodeError: Error, Sendable {
        case openFailed(Int32)
        case noVideoStream
        case codecNotFound(Int32)
        case openCodecFailed(Int32)
        case decodeFailed(Int32)
    }

    public struct VideoInfo: Sendable, Equatable {
        public let width: Int
        public let height: Int
        public let codecName: String
        public init(width: Int, height: Int, codecName: String) {
            self.width = width; self.height = height; self.codecName = codecName
        }
    }

    public let info: VideoInfo

    /// Presentation timestamp, in the video stream's time base, of the frame
    /// `nextBGRAFrame` last returned. `Int64.min` before the first decode.
    /// A seek lands on a keyframe, so this is the only way a caller can tell
    /// which frame it actually got.
    public private(set) var lastTimestamp: Int64 = .min

    private var fmt: UnsafeMutablePointer<AVFormatContext>?
    private var codec: UnsafeMutablePointer<AVCodecContext>?
    private var sws: UnsafeMutablePointer<SwsContext>?
    private var swsDst: UnsafeMutablePointer<AVFrame>?  // reusable BGRA output frame
    private var frame: UnsafeMutablePointer<AVFrame>?
    /// Reference to the most recently decoded frame. `avcodec_receive_frame`
    /// unrefs its output frame whenever it fails, so at end of stream `frame`
    /// is empty — converting it asserts inside swscale. This holds a
    /// refcounted reference (no pixel copy) so the last good picture survives.
    private var keep: UnsafeMutablePointer<AVFrame>?
    private var packet: UnsafeMutablePointer<AVPacket>?
    private var streamIndex: Int32 = -1

    /// The first playable (non-attached-pic) video stream in an opened,
    /// stream-info'd format context — the ONE stream-selection rule the
    /// probe, the decoder, and stream probes all share.
    public static func firstPlayableVideoStream(
        in fmt: UnsafeMutablePointer<AVFormatContext>
    ) -> (index: Int, stream: UnsafeMutablePointer<AVStream>)? {
        guard let streamsBase = fmt.pointee.streams else { return nil }
        for i in 0..<Int(fmt.pointee.nb_streams) {
            guard let s = streamsBase[i], let p = s.pointee.codecpar else { continue }
            if p.pointee.codec_type == AVMEDIA_TYPE_VIDEO,
               (s.pointee.disposition & AV_DISPOSITION_ATTACHED_PIC) == 0 {
                return (i, s)
            }
        }
        return nil
    }

    public init(path: String) throws {
        var fmtCtx: UnsafeMutablePointer<AVFormatContext>? = nil
        let openResult = path.withCString { p in avformat_open_input(&fmtCtx, p, nil, nil) }
        guard openResult == 0, let fmtCtx else { throw DecodeError.openFailed(openResult) }
        self.fmt = fmtCtx

        if avformat_find_stream_info(fmtCtx, nil) < 0 {
            var f: UnsafeMutablePointer<AVFormatContext>? = fmtCtx; avformat_close_input(&f); self.fmt = nil
            throw DecodeError.openFailed(-1)
        }

        guard let (vidx, stream) = Self.firstPlayableVideoStream(in: fmtCtx),
              let par = stream.pointee.codecpar else {
            var f: UnsafeMutablePointer<AVFormatContext>? = fmtCtx; avformat_close_input(&f); self.fmt = nil
            throw DecodeError.noVideoStream
        }
        self.streamIndex = Int32(vidx)
        let codecId = par.pointee.codec_id
        guard let decoder = avcodec_find_decoder(codecId) else {
            var f: UnsafeMutablePointer<AVFormatContext>? = fmtCtx; avformat_close_input(&f); self.fmt = nil
            throw DecodeError.codecNotFound(Int32(codecId.rawValue))
        }

        guard let cc = avcodec_alloc_context3(decoder) else {
            var f: UnsafeMutablePointer<AVFormatContext>? = fmtCtx; avformat_close_input(&f); self.fmt = nil
            throw DecodeError.openCodecFailed(-1)
        }
        if avcodec_parameters_to_context(cc, par) < 0 {
            var c: UnsafeMutablePointer<AVCodecContext>? = cc; avcodec_free_context(&c)
            var f: UnsafeMutablePointer<AVFormatContext>? = fmtCtx; avformat_close_input(&f); self.fmt = nil
            throw DecodeError.openCodecFailed(-1)
        }
        // libavcodec defaults thread_count to 1; frame threading parallelizes
        // the walk after a seek (it was why timeline clicks waited on one
        // core). FF_THREAD_SLICE is deliberately off: with this file's usage
        // (continuous decode+upload, avcodec_flush_buffers on seeks, long
        // GOPs) it corrupts memory and kills the host — reproduced with a 5
        // min 1080p clip crashing at ~25 s; frame-only is stable and keeps
        // most of the speed win.
        cc.pointee.thread_count = 0  // auto: one per core
        cc.pointee.thread_type = FF_THREAD_FRAME
        let openCodec = avcodec_open2(cc, decoder, nil)
        guard openCodec == 0 else {
            var c: UnsafeMutablePointer<AVCodecContext>? = cc; avcodec_free_context(&c)
            var f: UnsafeMutablePointer<AVFormatContext>? = fmtCtx; avformat_close_input(&f); self.fmt = nil
            throw DecodeError.openCodecFailed(openCodec)
        }
        self.codec = cc

        guard let frame = av_frame_alloc(),
              let keep = av_frame_alloc(),
              let packet = av_packet_alloc() else {
            var c: UnsafeMutablePointer<AVCodecContext>? = cc; avcodec_free_context(&c)
            var f: UnsafeMutablePointer<AVFormatContext>? = fmtCtx; avformat_close_input(&f); self.fmt = nil
            throw DecodeError.openCodecFailed(-1)
        }
        self.frame = frame
        self.keep = keep
        self.packet = packet

        self.info = VideoInfo(
            width: Int(cc.pointee.width),
            height: Int(cc.pointee.height),
            codecName: String(cString: avcodec_get_name(codecId))
        )
    }

    deinit {
        if let swsDst { var f: UnsafeMutablePointer<AVFrame>? = swsDst; av_frame_free(&f) }
        if let sws { sws_freeContext(sws) }
        if let packet { var p: UnsafeMutablePointer<AVPacket>? = packet; av_packet_free(&p) }
        if let frame { var f: UnsafeMutablePointer<AVFrame>? = frame; av_frame_free(&f) }
        if let keep { var f: UnsafeMutablePointer<AVFrame>? = keep; av_frame_free(&f) }
        if let codec { var c: UnsafeMutablePointer<AVCodecContext>? = codec; avcodec_free_context(&c) }
        if let fmt { var f: UnsafeMutablePointer<AVFormatContext>? = fmt; avformat_close_input(&f) }
    }

    /// Lazily create (or reuse) the swscale context. The modern API
    /// (sws_scale_frame) takes AVFrames directly; the context's source
    /// format/dims are inferred from the input frame on each call.
    private func ensureSws() -> UnsafeMutablePointer<SwsContext>? {
        if sws != nil { return sws }
        // sws_alloc_context returns a context with default fields; the actual
        // format/dims are negotiated by sws_scale_frame per call.
        guard let ctx = sws_alloc_context() else { return nil }
        sws = ctx
        return ctx
    }

    /// Decodes the next frame and converts it to tightly-packed BGRA in a
    /// freshly-allocated Data (row stride = width * 4). Returns nil at EOF.
    public func nextBGRAFrame() throws -> Data? {
        guard try decodeNextFrame() else { return nil }
        guard let bgra = currentBGRAFrame() else { throw DecodeError.decodeFailed(-1) }
        return bgra
    }

    /// Advances to the next frame without converting it. Returns false at EOF.
    ///
    /// Separate from the conversion on purpose: a seek lands on the keyframe
    /// before the target, so reaching a requested frame usually means stepping
    /// through most of a GOP. Converting each of those to BGRA — a full
    /// scale plus a frame-sized allocation and copy — is work thrown away, and
    /// it is most of why clicking in the timeline used to stall the preview.
    @discardableResult
    public func decodeNextFrame() throws -> Bool {
        guard let fmt, let codec, let frame, let packet else { throw DecodeError.decodeFailed(-1) }
        let cc = codec

        while true {
            let recv = avcodec_receive_frame(cc, frame)
            if recv == 0 {
                let pts = frame.pointee.best_effort_timestamp
                lastTimestamp = pts == noPTSValue ? frame.pointee.pts : pts
                if let keep {
                    av_frame_unref(keep)
                    av_frame_ref(keep, frame)
                }
                hasDecodedFrame = true
                return true
            }
            if recv == AVERROR_EAGAIN {
                av_packet_unref(packet)
                let read = av_read_frame(fmt, packet)
                if read < 0 {
                    if read == AVERROR_EOF {
                        avcodec_send_packet(cc, nil)  // flush decoder
                        continue
                    }
                    throw DecodeError.decodeFailed(read)
                }
                if packet.pointee.stream_index != streamIndex { continue }
                let send = avcodec_send_packet(cc, packet)
                if send < 0 && send != AVERROR_EAGAIN { throw DecodeError.decodeFailed(send) }
                continue
            }
            if recv == AVERROR_EOF { return false }
            throw DecodeError.decodeFailed(recv)
        }
    }

    /// BGRA bytes for the frame `decodeNextFrame` last produced, or nil when
    /// nothing has decoded yet or the conversion failed.
    public func currentBGRAFrame() -> Data? {
        guard hasDecodedFrame else { return nil }
        let bgra = convertToBGRA()
        return bgra.isEmpty ? nil : bgra
    }

    /// Whether any frame has been decoded since the last seek.
    private var hasDecodedFrame = false

    /// Scale the current decoded frame into flat BGRA via sws_scale_frame.
    /// The dst AVFrame is reused across calls; sws_scale_frame allocates its
    /// buffers. We then copy the tightly-packed BGRA plane into a fresh Data
    /// so the caller owns the bytes before the next decode.
    private func convertToBGRA() -> Data {
        let cc = codec!
        guard let frm = keep, frm.pointee.format >= 0, frm.pointee.width > 0 else { return Data() }
        let w = cc.pointee.width
        let h = cc.pointee.height
        let sws = ensureSws()!

        // Lazily allocate the reusable BGRA output frame.
        if swsDst == nil { swsDst = av_frame_alloc() }
        let dst = swsDst!

        // sws_scale_frame reads dst.format/width/height to negotiate the output.
        // bg_alloc_on_first_call uses these. Clear any prior buffer refs.
        av_frame_unref(dst)
        dst.pointee.format = Int32(AV_PIX_FMT_BGRA.rawValue)
        dst.pointee.width = w
        dst.pointee.height = h

        let scaleResult = sws_scale_frame(sws, dst, frm)
        guard scaleResult == 0 else {
            // On failure, return an empty buffer; the caller treats it as a
            // decode error via decodeFailed in the public path.
            return Data()
        }

        // dst.data[0] is the tightly-packed BGRA plane; dst.linesize[0] may
        // include padding, so copy row-by-row into a flat width*4 buffer.
        let rowStride = Int(dst.pointee.linesize.0)
        let tightStride = Int(w) * 4
        var data = Data(count: Int(w) * Int(h) * 4)
        guard rowStride > 0 else { return data }
        if let plane = dst.pointee.data.0 {
            data.withUnsafeMutableBytes { (dstRaw: UnsafeMutableRawBufferPointer) in
                guard let dstBase = dstRaw.baseAddress?.assumingMemoryBound(to: UInt8.self) else { return }
                for row in 0..<Int(h) {
                    let srcRow = plane.advanced(by: row * rowStride)
                    let dstRow = dstBase.advanced(by: row * tightStride)
                    memcpy(dstRow, srcRow, tightStride)
                }
            }
        }
        return data
    }

    /// Seek to a stream time_base timestamp. Pass AVSEEK_FLAG_BACKWARD for
    /// nearest-keyframe-before behavior. Codec is flushed per FFmpeg contract.
    public func seek(timestamp: Int64, flags: Int32 = AVSEEK_FLAG_BACKWARD) throws {
        guard let fmt else { throw DecodeError.decodeFailed(-1) }
        let r = avformat_seek_file(fmt, streamIndex, Int64.min, timestamp, Int64.max, flags)
        if r < 0 { throw DecodeError.decodeFailed(r) }
        if let codec { avcodec_flush_buffers(codec) }
        if let frame { av_frame_unref(frame) }
        if let keep { av_frame_unref(keep) }
        lastTimestamp = .min
        hasDecodedFrame = false
    }

    /// Decodes the frame at `index` (in `fps` units), seeking first and then
    /// walking forward from wherever the seek landed.
    ///
    /// The walk is the point: `seek` lands on the nearest keyframe *before* the
    /// target, so taking the next decoded frame returns the keyframe — often
    /// frame 0 — rather than the frame that was asked for. Callers that need a
    /// specific frame must use this rather than seek-then-decode.
    ///
    /// Returns nil at end of stream. `maxDecodeAhead` bounds the walk so a
    /// stream without timestamps cannot spin.
    public func frame(at index: Int, fps: Int, maxDecodeAhead: Int = 600) throws -> Data? {
        try seek(toFrame: max(0, index), fps: fps)
        var decoded = 0
        while decoded <= maxDecodeAhead {
            // Only the frame that is actually wanted gets converted.
            guard try decodeNextFrame() else { return currentBGRAFrame() }
            decoded += 1
            guard let landed = lastFrameIndex(fps: fps) else { return currentBGRAFrame() }
            if landed >= index { return currentBGRAFrame() }
        }
        return currentBGRAFrame()
    }

    /// Frame index of the last decoded frame at `fps`, or nil before the first
    /// decode or when the stream carries no timestamps.
    public func lastFrameIndex(fps: Int) -> Int? {
        guard lastTimestamp != .min, fps > 0, let fmt, let streamsBase = fmt.pointee.streams,
              Int(streamIndex) < Int(fmt.pointee.nb_streams),
              let stream = streamsBase[Int(streamIndex)] else { return nil }
        let tb = stream.pointee.time_base
        guard tb.den > 0 else { return nil }
        // pts * time_base * fps, rounded to the nearest frame.
        let numerator = lastTimestamp * Int64(tb.num) * Int64(fps)
        return Int((Double(numerator) / Double(tb.den)).rounded())
    }

    /// Seek to a frame index at the given fps, converting through the video
    /// stream's time_base. Lands on the nearest keyframe before the frame;
    /// callers decode forward from there.
    public func seek(toFrame frame: Int, fps: Int) throws {
        guard let fmt, let streamsBase = fmt.pointee.streams,
              Int(streamIndex) < Int(fmt.pointee.nb_streams),
              let stream = streamsBase[Int(streamIndex)] else { throw DecodeError.decodeFailed(-1) }
        let tb = stream.pointee.time_base
        let timestamp = Int64(frame) * Int64(tb.den) / max(1, Int64(tb.num) * Int64(fps))
        try seek(timestamp: timestamp)
    }
}

// FFmpeg's AVERROR_EOF and AVERROR(e) macros don't import (FFERRTAG uses
// bitwise tricks the Swift importer can't expand); define Swift constants
// matching the on-the-wire values. FFERRTAG(a,b,c,d) = -(int)MKTAG(a,b,c,d),
// MKTAG = a | b<<8 | c<<16 | d<<24, all UInt32.
@inline(__always)
private func fferrtag(_ a: UInt8, _ b: UInt8, _ c: UInt8, _ d: UInt8) -> Int32 {
    let tag = UInt32(a) | (UInt32(b) << 8) | (UInt32(c) << 16) | (UInt32(d) << 24)
    return -Int32(bitPattern: tag)
}
/// AV_NOPTS_VALUE — the importer cannot expand the macro (it casts through
/// UINT64_C), so the same value is spelled out here.
private let noPTSValue = Int64.min

private let AVERROR_EOF: Int32 = fferrtag(0x45, 0x4F, 0x46, 0x20)    // 'E','O','F',' '
private let AVERROR_EAGAIN: Int32 = -Int32(EAGAIN)                  // AVERROR(e) = -(e)
