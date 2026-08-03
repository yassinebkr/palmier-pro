import Foundation
import PalmierWin

// Silence detection: windowed RMS over the decoded audio (10 ms windows, mono
// mix of the engine's 48 kHz stereo output). A window under `thresholdDb` is
// silent; runs of silent windows at least `minSilenceMs` long are reported,
// each shrunk by `paddingMs` on the sides that border audible audio (a silence
// touching the file's head or tail is removed whole, matching upstream).

struct SilenceRange: Codable {
    var startMs: Int
    var endMs: Int
}

/// Window length in sample frames at the decoder's fixed 48 kHz rate (10 ms).
private let silenceWindowFrames = 480

/// Detects silent spans in `path`'s audio and writes them as JSON
/// ([{"startMs":…,"endMs":…}], source-media time) into buf. Returns bytes
/// written, -(required size) when buf is null or too small, or 0 when the
/// file has no decodable audio or the parameters are invalid. Blocking decode
/// of the whole stream — call off the UI thread.
@_cdecl("palmier_detect_silence")
public func palmierDetectSilence(_ path: UnsafePointer<CChar>?,
                                 _ thresholdDb: Double,
                                 _ minSilenceMs: Int32,
                                 _ paddingMs: Int32,
                                 _ buf: UnsafeMutablePointer<CChar>?,
                                 _ bufSize: Int32) -> Int32 {
    guard let path, thresholdDb.isFinite, thresholdDb >= -96, thresholdDb <= 0,
          minSilenceMs >= 0, paddingMs >= 0 else { return 0 }
    guard let decoder = try? FFmpegAudioDecoder(path: String(cString: path)) else { return 0 }

    let threshold = Float(pow(10.0, thresholdDb / 20.0))
    var silentWindows: [Bool] = []
    var squareSum: Float = 0
    var windowFill = 0

    let channels = FFmpegAudioDecoder.channels
    let chunkFrames = 4096
    var chunk = [Float](repeating: 0, count: chunkFrames * channels)
    while true {
        let got = chunk.withUnsafeMutableBufferPointer {
            decoder.read(into: $0.baseAddress!, sampleFrames: chunkFrames)
        }
        guard got > 0 else { break }
        for i in 0..<got {
            var mono: Float = 0
            for c in 0..<channels { mono += chunk[i * channels + c] }
            mono /= Float(channels)
            squareSum += mono * mono
            windowFill += 1
            if windowFill == silenceWindowFrames {
                silentWindows.append(sqrt(squareSum / Float(silenceWindowFrames)) < threshold)
                squareSum = 0
                windowFill = 0
            }
        }
        if got < chunkFrames { break }
    }
    guard !silentWindows.isEmpty else { return 0 }

    let ranges = silenceRanges(from: silentWindows, windowMs: 10,
                               minSilenceMs: Int(minSilenceMs), paddingMs: Int(paddingMs))
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    guard let data = try? encoder.encode(ranges) else { return 0 }
    if buf == nil || Int32(data.count) >= bufSize { return -Int32(data.count + 1) }
    data.withUnsafeBytes { raw in
        memcpy(buf!, raw.baseAddress!, data.count)
    }
    buf![data.count] = 0
    return Int32(data.count)
}

/// Merges silent windows into ms ranges, keeps runs of at least
/// `minSilenceMs`, then contracts each by `paddingMs` on the sides that border
/// audible audio. A trailing partial window is ignored, so `windowMs` times
/// the window count is the analyzed length.
func silenceRanges(from windows: [Bool], windowMs: Int,
                   minSilenceMs: Int, paddingMs: Int) -> [SilenceRange] {
    let totalMs = windows.count * windowMs
    var ranges: [SilenceRange] = []
    var i = 0
    while i < windows.count {
        guard windows[i] else { i += 1; continue }
        var j = i + 1
        while j < windows.count, windows[j] { j += 1 }
        var startMs = i * windowMs
        var endMs = j * windowMs
        if endMs - startMs >= minSilenceMs {
            if startMs > 0 { startMs += paddingMs }
            if endMs < totalMs { endMs -= paddingMs }
            if endMs > startMs { ranges.append(SilenceRange(startMs: startMs, endMs: endMs)) }
        }
        i = j
    }
    return ranges
}
