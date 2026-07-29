import CFFmpeg
import Foundation

/// Encodes a sequence of BGRA frames into a video file (H.264 MP4 by default).
/// The inverse of `FFmpegDecoder`: takes tightly-packed BGRA frames via
/// `writeFrame(_:)`, scales to yuv420p via libswscale, encodes via libavcodec,
/// and muxes via libavformat. `close()` drains the encoder and writes the
/// trailer; the file is incomplete until then.
///
/// Not Sendable — owned by one export worker. The macOS export path delegates
/// the whole encode loop to `AVAssetExportSession`; Windows drives its own
/// per-frame decode→composite→encode loop (see WinExporter) and hands each
/// rendered BGRA frame here.
public final class FFmpegEncoder {
    public enum EncodeError: Error, Sendable {
        case noEncoder(String)
        case openCodecFailed(Int32)
        case muxerSetupFailed(Int32)
        case ioOpenFailed(Int32)
        case writeHeaderFailed(Int32)
        case encodeFailed(Int32)
        case writePacketFailed(Int32)
        case writeTrailerFailed(Int32)
    }

    public struct Config: Sendable {
        public let width: Int
        public let height: Int
        public let fps: Int
        public let codec: String       // "libx264" (avcodec_find_encoder_by_name)
        public let container: String   // "mp4"
        public let bitRate: Int64
        public let gopSize: Int

        public init(width: Int, height: Int, fps: Int = 30,
                    codec: String = "libx264", container: String = "mp4",
                    bitRate: Int64 = 4_000_000, gopSize: Int = 30) {
            self.width = width; self.height = height; self.fps = fps
            self.codec = codec; self.container = container
            self.bitRate = bitRate; self.gopSize = gopSize
        }
    }

    public let config: Config

    private var fmtCtx: UnsafeMutablePointer<AVFormatContext>?
    private var codec: UnsafeMutablePointer<AVCodecContext>?
    private var sws: UnsafeMutablePointer<SwsContext>?
    private var swsSrc: UnsafeMutablePointer<AVFrame>?  // reusable BGRA input frame
    private var swsDst: UnsafeMutablePointer<AVFrame>?  // reusable yuv420p output frame
    private var packet: UnsafeMutablePointer<AVPacket>?
    private var streamIndex: Int32 = -1
    private var pts: Int64 = 0
    private var headerWritten = false
    private var closed = false

    public init(path: String, config: Config) throws {
        self.config = config

        // 1) Muxer + output context.
        var oc: UnsafeMutablePointer<AVFormatContext>? = nil
        let allocResult: Int32 = config.container.withCString { c -> Int32 in
            path.withCString { p in
                avformat_alloc_output_context2(&oc, nil, c, p)
            }
        }
        guard allocResult == 0, let ocUnwrapped = oc else { throw EncodeError.muxerSetupFailed(allocResult) }
        self.fmtCtx = ocUnwrapped

        // 2) Encoder (prefer the named codec; fall back to AV_CODEC_ID_H264).
        let encoder = config.codec.withCString { name in avcodec_find_encoder_by_name(name) }
        let resolvedEncoder = encoder ?? avcodec_find_encoder(AV_CODEC_ID_H264)
        guard let resolvedEncoder else {
            avformat_free_context(ocUnwrapped); self.fmtCtx = nil
            throw EncodeError.noEncoder(config.codec)
        }

        // 3) Codec context.
        guard let cc = avcodec_alloc_context3(resolvedEncoder) else {
            avformat_free_context(ocUnwrapped); self.fmtCtx = nil
            throw EncodeError.openCodecFailed(-1)
        }
        cc.pointee.bit_rate = config.bitRate
        cc.pointee.width = Int32(config.width)
        cc.pointee.height = Int32(config.height)
        cc.pointee.time_base = AVRational(num: 1, den: Int32(config.fps))
        cc.pointee.framerate = AVRational(num: Int32(config.fps), den: 1)
        cc.pointee.gop_size = Int32(config.gopSize)
        cc.pointee.pix_fmt = AV_PIX_FMT_YUV420P
        let openResult = avcodec_open2(cc, resolvedEncoder, nil)
        guard openResult == 0 else {
            var c: UnsafeMutablePointer<AVCodecContext>? = cc; avcodec_free_context(&c)
            avformat_free_context(ocUnwrapped); self.fmtCtx = nil
            throw EncodeError.openCodecFailed(openResult)
        }
        self.codec = cc

        // 4) Muxer video stream + parameters.
        guard let stream = avformat_new_stream(ocUnwrapped, nil) else {
            var c: UnsafeMutablePointer<AVCodecContext>? = cc; avcodec_free_context(&c)
            avformat_free_context(ocUnwrapped); self.fmtCtx = nil
            throw EncodeError.muxerSetupFailed(-1)
        }
        stream.pointee.time_base = cc.pointee.time_base
        self.streamIndex = stream.pointee.index
        _ = avcodec_parameters_from_context(stream.pointee.codecpar, cc)

        // 5) Open the output file + write header.
        let oformatFlags = ocUnwrapped.pointee.oformat?.pointee.flags ?? 0
        if oformatFlags & Int32(AVFMT_NOFILE) == 0 {
            var pb: UnsafeMutablePointer<AVIOContext>? = nil
            let ioResult = path.withCString { p in
                avio_open2(&pb, p, Int32(AVIO_FLAG_WRITE), nil, nil)
            }
            guard ioResult == 0, let pb else {
                var c: UnsafeMutablePointer<AVCodecContext>? = cc; avcodec_free_context(&c)
                avformat_free_context(ocUnwrapped); self.fmtCtx = nil
                throw EncodeError.ioOpenFailed(ioResult)
            }
            ocUnwrapped.pointee.pb = pb
        }
        var opts: OpaquePointer? = nil
        let headerResult = withUnsafeMutablePointer(to: &opts) { optsPtr in
            avformat_write_header(ocUnwrapped, optsPtr)
        }
        guard headerResult == 0 else {
            var c: UnsafeMutablePointer<AVCodecContext>? = cc; avcodec_free_context(&c)
            avformat_free_context(ocUnwrapped); self.fmtCtx = nil
            throw EncodeError.writeHeaderFailed(headerResult)
        }
        self.headerWritten = true

        // 6) Sws (BGRA → yuv420p) + reusable frames + packet.
        guard let sws = sws_alloc_context(),
              let src = av_frame_alloc(),
              let dst = av_frame_alloc(),
              let pkt = av_packet_alloc() else {
            throw EncodeError.openCodecFailed(-1)
        }
        src.pointee.format = Int32(AV_PIX_FMT_BGRA.rawValue)
        src.pointee.width = Int32(config.width)
        src.pointee.height = Int32(config.height)
        dst.pointee.format = Int32(AV_PIX_FMT_YUV420P.rawValue)
        dst.pointee.width = Int32(config.width)
        dst.pointee.height = Int32(config.height)
        self.sws = sws
        self.swsSrc = src
        self.swsDst = dst
        self.packet = pkt
    }

    deinit {
        if !closed { try? close() }
    }

    /// Encodes one tightly-packed BGRA frame (width*height*4 bytes) into the
    /// output. The frame's pts advances by 1 per call (in codec time_base).
    /// Returns false on any encode/mux error.
    @discardableResult
    public func writeFrame(_ bytes: Data) -> Bool {
        guard let fmtCtx, let codec, let sws, let src = swsSrc, let dst = swsDst, let pkt = packet
        else { return false }
        let expected = config.width * config.height * 4
        guard bytes.count >= expected else { return false }

        // Copy BGRA bytes into the reusable source frame's plane 0.
        // av_frame_get_buffer allocates on first use; av_frame_make_writable
        // guarantees we own it before writing.
        if av_frame_get_buffer(src, 0) < 0 { return false }
        if av_frame_make_writable(src) < 0 { return false }
        let srcStride = Int(src.pointee.linesize.0)
        let tightStride = config.width * 4
        bytes.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            guard let base = raw.baseAddress?.assumingMemoryBound(to: UInt8.self) else { return }
            if let plane = src.pointee.data.0 {
                if srcStride == tightStride {
                    memcpy(plane, base, expected)
                } else {
                    // Frame linesize may include padding; copy row-by-row.
                    for row in 0..<config.height {
                        memcpy(plane.advanced(by: row * srcStride),
                               base.advanced(by: row * tightStride), tightStride)
                    }
                }
            }
        }

        // BGRA → yuv420p via the modern sws_scale_frame API. sws_scale_frame
        // allocates dst's buffers internally (the frame's format/width/height
        // are set in init to negotiate the conversion) — do NOT call
        // av_frame_make_writable or av_frame_get_buffer on dst first; that
        // pre-allocates incompatible buffers and sws rejects them (EINVAL).
        let scaleResult = sws_scale_frame(sws, dst, src)
        guard scaleResult == 0 else { return false }

        // Encode + mux any packets the encoder emits for this frame.
        dst.pointee.pts = pts
        pts += 1
        if !sendAndReceive(packet: pkt, frame: dst, fmt: fmtCtx, codec: codec) {
            return false
        }
        return true
    }

    /// Sends one frame and drains all packets the encoder produces for it,
    /// rescaling timestamps from codec time_base to stream time_base and muxing.
    private func sendAndReceive(packet: UnsafeMutablePointer<AVPacket>, frame: UnsafeMutablePointer<AVFrame>?, fmt: UnsafeMutablePointer<AVFormatContext>, codec: UnsafeMutablePointer<AVCodecContext>) -> Bool {
        let send = avcodec_send_frame(codec, frame)
        if send < 0 && send != AVERROR_EAGAIN { return false }
        while true {
            let recv = avcodec_receive_packet(codec, packet)
            if recv == AVERROR_EAGAIN || recv == AVERROR_EOF { return true }
            if recv < 0 { return false }
            // Rescale pts/duration from codec time_base to stream time_base.
            let stTimeBase = fmt.pointee.streams[Int(streamIndex)]!.pointee.time_base
            av_packet_rescale_ts(packet, codec.pointee.time_base, stTimeBase)
            packet.pointee.stream_index = streamIndex
            // av_interleaved_write_frame takes ownership of the packet's buffer;
            // unref resets it for the next iteration.
            let writeResult = withUnsafePointer(to: fmt) { fmtPtr in
                av_interleaved_write_frame(fmtPtr.pointee, packet)
            }
            av_packet_unref(packet)
            if writeResult < 0 { return false }
        }
    }

    /// Drains the encoder (sends a NULL frame), writes the trailer, and closes
    /// the file. Idempotent. The output file is incomplete until this returns.
    public func close() throws {
        guard !closed else { return }
        closed = true
        defer {
            if let packet { var p: UnsafeMutablePointer<AVPacket>? = packet; av_packet_free(&p); self.packet = nil }
            if let swsDst { var f: UnsafeMutablePointer<AVFrame>? = swsDst; av_frame_free(&f); self.swsDst = nil }
            if let swsSrc { var f: UnsafeMutablePointer<AVFrame>? = swsSrc; av_frame_free(&f); self.swsSrc = nil }
            if let sws { sws_freeContext(sws); self.sws = nil }
            if let codec { var c: UnsafeMutablePointer<AVCodecContext>? = codec; avcodec_free_context(&c); self.codec = nil }
            if let fmtCtx {
                if let pb = fmtCtx.pointee.pb { var p: UnsafeMutablePointer<AVIOContext>? = pb; avio_closep(&p) }
                avformat_free_context(fmtCtx); self.fmtCtx = nil
            }
        }
        guard headerWritten, let fmtCtx, let codec, let pkt = packet else { return }

        // Drain: send NULL frame, receive remaining packets.
        if !sendAndReceive(packet: pkt, frame: nil, fmt: fmtCtx, codec: codec) {
            throw EncodeError.encodeFailed(-1)
        }

        let trailerResult = av_write_trailer(fmtCtx)
        if trailerResult < 0 { throw EncodeError.writeTrailerFailed(trailerResult) }
    }
}

// FFmpeg error-code macros don't import cleanly under Swift (FFERRTAG bitwise
// tricks); define the two we use as Swift constants. Mirrors FFmpegDecoder.
private let AVERROR_EAGAIN: Int32 = -Int32(EAGAIN)
private let AVERROR_EOF: Int32 = {
    let tag = UInt32(0x45) | (UInt32(0x4F) << 8) | (UInt32(0x46) << 16) | (UInt32(0x20) << 24)
    return -Int32(bitPattern: tag)
}()
