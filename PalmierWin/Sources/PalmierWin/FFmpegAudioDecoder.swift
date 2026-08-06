import CFFmpeg
import Foundation

/// Decodes a file's best audio stream to interleaved f32 stereo at 48 kHz
/// (the engine's fixed output format) via libavcodec + libswresample.
///
/// One decoder owns one format/codec/swr context and yields samples
/// sequentially through `read`. Seeking flushes the codec per FFmpeg
/// contract. Not Sendable — owned by one audio worker.
public final class FFmpegAudioDecoder {
    public static let sampleRate = 48000
    public static let channels = 2

    public enum AudioError: Error, Sendable {
        case openFailed(Int32)
        case noAudioStream
        case codecFailed(Int32)
        case resampleFailed(Int32)
    }

    private var fmt: UnsafeMutablePointer<AVFormatContext>?
    private var codec: UnsafeMutablePointer<AVCodecContext>?
    private var swr: OpaquePointer?
    private var frame: UnsafeMutablePointer<AVFrame>?
    private var packet: UnsafeMutablePointer<AVPacket>?
    private var streamIndex: Int32 = -1
    private var timeBase = AVRational(num: 1, den: 1)

    /// Leftover resampled samples not yet consumed by `read`.
    private var pending: [Float] = []
    private var pendingOffset = 0
    private var atEOF = false

    /// True when `path`'s container has an audio stream (cheap probe).
    public static func hasAudioStream(path: String) -> Bool {
        var fmtCtx: UnsafeMutablePointer<AVFormatContext>? = nil
        guard path.withCString({ avformat_open_input(&fmtCtx, $0, nil, nil) }) == 0, let fmt = fmtCtx else { return false }
        defer { var f: UnsafeMutablePointer<AVFormatContext>? = fmt; avformat_close_input(&f) }
        guard avformat_find_stream_info(fmt, nil) >= 0 else { return false }
        return av_find_best_stream(fmt, AVMEDIA_TYPE_AUDIO, -1, -1, nil, 0) >= 0
    }

    /// Container probe: true when the file has a playable (non-attached-pic) video stream.
    public static func hasVideoStream(path: String) -> Bool {
        var fmtCtx: UnsafeMutablePointer<AVFormatContext>? = nil
        guard path.withCString({ avformat_open_input(&fmtCtx, $0, nil, nil) }) == 0, let fmt = fmtCtx else { return false }
        defer { var f: UnsafeMutablePointer<AVFormatContext>? = fmt; avformat_close_input(&f) }
        guard avformat_find_stream_info(fmt, nil) >= 0, let streamsBase = fmt.pointee.streams else { return false }
        for i in 0..<Int(fmt.pointee.nb_streams) {
            guard let s = streamsBase[i], let p = s.pointee.codecpar else { continue }
            if p.pointee.codec_type == AVMEDIA_TYPE_VIDEO,
               (s.pointee.disposition & AV_DISPOSITION_ATTACHED_PIC) == 0 { return true }
        }
        return false
    }

    public init(path: String) throws {
        var fmtCtx: UnsafeMutablePointer<AVFormatContext>? = nil
        let openResult = path.withCString { avformat_open_input(&fmtCtx, $0, nil, nil) }
        guard openResult == 0, let fmtCtx else { throw AudioError.openFailed(openResult) }
        fmt = fmtCtx

        func fail(_ error: AudioError) throws -> Never {
            if let codec { var c: UnsafeMutablePointer<AVCodecContext>? = codec; avcodec_free_context(&c); self.codec = nil }
            var f: UnsafeMutablePointer<AVFormatContext>? = fmtCtx
            avformat_close_input(&f)
            fmt = nil
            throw error
        }

        guard avformat_find_stream_info(fmtCtx, nil) >= 0 else { try fail(.openFailed(-1)) }
        let aidx = av_find_best_stream(fmtCtx, AVMEDIA_TYPE_AUDIO, -1, -1, nil, 0)
        guard aidx >= 0 else { try fail(.noAudioStream) }
        streamIndex = aidx

        guard let streamsBase = fmtCtx.pointee.streams,
              let stream = streamsBase[Int(aidx)],
              let par = stream.pointee.codecpar,
              let decoder = avcodec_find_decoder(par.pointee.codec_id),
              let cc = avcodec_alloc_context3(decoder) else { try fail(.codecFailed(-1)) }
        timeBase = stream.pointee.time_base
        codec = cc
        guard avcodec_parameters_to_context(cc, par) >= 0,
              avcodec_open2(cc, decoder, nil) == 0 else { try fail(.codecFailed(-1)) }

        // swr: source layout from the codec, destination fixed stereo/f32/48k.
        var outLayout = AVChannelLayout()
        av_channel_layout_default(&outLayout, Int32(Self.channels))
        var swrCtx: OpaquePointer? = nil
        let allocResult = withUnsafePointer(to: cc.pointee.ch_layout) { inLayout in
            swr_alloc_set_opts2(&swrCtx, &outLayout, AV_SAMPLE_FMT_FLT, Int32(Self.sampleRate),
                                inLayout, cc.pointee.sample_fmt, cc.pointee.sample_rate, 0, nil)
        }
        guard allocResult == 0, let swrCtx, swr_init(swrCtx) >= 0 else { try fail(.resampleFailed(-1)) }
        swr = swrCtx

        guard let f = av_frame_alloc(), let p = av_packet_alloc() else { try fail(.codecFailed(-1)) }
        frame = f
        packet = p
    }

    deinit {
        if let swr { var s: OpaquePointer? = swr; swr_free(&s) }
        if let packet { var p: UnsafeMutablePointer<AVPacket>? = packet; av_packet_free(&p) }
        if let frame { var f: UnsafeMutablePointer<AVFrame>? = frame; av_frame_free(&f) }
        if let codec { var c: UnsafeMutablePointer<AVCodecContext>? = codec; avcodec_free_context(&c) }
        if let fmt { var f: UnsafeMutablePointer<AVFormatContext>? = fmt; avformat_close_input(&f) }
    }

    /// Fills `buffer` with up to `sampleFrames` interleaved stereo frames.
    /// Returns frames written; short counts only at EOF.
    public func read(into buffer: UnsafeMutablePointer<Float>, sampleFrames: Int) -> Int {
        var written = 0
        while written < sampleFrames {
            if pendingOffset < pending.count {
                let framesAvailable = (pending.count - pendingOffset) / Self.channels
                let take = min(framesAvailable, sampleFrames - written)
                pending.withUnsafeBufferPointer { src in
                    buffer.advanced(by: written * Self.channels)
                        .update(from: src.baseAddress!.advanced(by: pendingOffset), count: take * Self.channels)
                }
                pendingOffset += take * Self.channels
                written += take
                continue
            }
            guard decodeNextChunk() else { break }
        }
        return written
    }

    /// Decodes and resamples the next audio frame into `pending`. Returns
    /// false at EOF or on error.
    private func decodeNextChunk() -> Bool {
        guard !atEOF, let fmt, let codec, let frame, let packet, let swr else { return false }
        while true {
            let recv = avcodec_receive_frame(codec, frame)
            if recv == 0 {
                appendResampled(frame: frame, swr: swr)
                return pendingOffset < pending.count
            }
            if recv == swrAVERROR_EOF {
                // Drain swr's internal buffer once the codec is exhausted.
                atEOF = true
                return drainResampler(swr: swr)
            }
            guard recv == swrAVERROR_EAGAIN else { return false }
            av_packet_unref(packet)
            let read = av_read_frame(fmt, packet)
            if read < 0 {
                avcodec_send_packet(codec, nil)
                if read != swrAVERROR_EOF { return false }
                continue
            }
            if packet.pointee.stream_index != streamIndex { continue }
            let send = avcodec_send_packet(codec, packet)
            if send < 0 && send != swrAVERROR_EAGAIN { return false }
        }
    }

    private func appendResampled(frame: UnsafeMutablePointer<AVFrame>, swr: OpaquePointer) {
        let maxOut = Int(swr_get_out_samples(swr, frame.pointee.nb_samples))
        guard maxOut > 0 else { return }
        var out = [Float](repeating: 0, count: maxOut * Self.channels)
        let produced = out.withUnsafeMutableBufferPointer { buf -> Int32 in
            var outPtr: UnsafeMutablePointer<UInt8>? = UnsafeMutableRawPointer(buf.baseAddress!)
                .assumingMemoryBound(to: UInt8.self)
            return withUnsafePointer(to: frame.pointee.data) { dataPtr in
                dataPtr.withMemoryRebound(to: UnsafePointer<UInt8>?.self, capacity: 8) { inPtrs in
                    swr_convert(swr, &outPtr, Int32(maxOut), inPtrs, frame.pointee.nb_samples)
                }
            }
        }
        guard produced > 0 else { return }
        compactPending()
        pending.append(contentsOf: out.prefix(Int(produced) * Self.channels))
    }

    private func drainResampler(swr: OpaquePointer) -> Bool {
        let maxOut = Int(swr_get_out_samples(swr, 0))
        guard maxOut > 0 else { return false }
        var out = [Float](repeating: 0, count: maxOut * Self.channels)
        let produced = out.withUnsafeMutableBufferPointer { buf -> Int32 in
            var outPtr: UnsafeMutablePointer<UInt8>? = UnsafeMutableRawPointer(buf.baseAddress!)
                .assumingMemoryBound(to: UInt8.self)
            return swr_convert(swr, &outPtr, Int32(maxOut), nil, 0)
        }
        guard produced > 0 else { return false }
        compactPending()
        pending.append(contentsOf: out.prefix(Int(produced) * Self.channels))
        return true
    }

    private func compactPending() {
        if pendingOffset > 0 {
            pending.removeFirst(pendingOffset)
            pendingOffset = 0
        }
    }

    /// Seeks to `seconds` (nearest keyframe before) and flushes decode state.
    public func seek(toSeconds seconds: Double) {
        guard let fmt else { return }
        let ts = Int64(seconds * Double(timeBase.den) / Double(max(1, timeBase.num)))
        _ = avformat_seek_file(fmt, streamIndex, Int64.min, ts, Int64.max, AVSEEK_FLAG_BACKWARD)
        if let codec { avcodec_flush_buffers(codec) }
        pending.removeAll()
        pendingOffset = 0
        atEOF = false
    }
}

// AVERROR macros don't import (see FFmpegDecoder.swift).
@inline(__always)
private func swrFferrtag(_ a: UInt8, _ b: UInt8, _ c: UInt8, _ d: UInt8) -> Int32 {
    let tag = UInt32(a) | (UInt32(b) << 8) | (UInt32(c) << 16) | (UInt32(d) << 24)
    return -Int32(bitPattern: tag)
}
private let swrAVERROR_EOF: Int32 = swrFferrtag(0x45, 0x4F, 0x46, 0x20)
private let swrAVERROR_EAGAIN: Int32 = -Int32(EAGAIN)
