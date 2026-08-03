import Foundation
import PalmierWin

/// Decodes `path`'s audio and writes `columns` min/max pairs (mono, -1…1)
/// into buf as [min0, max0, min1, max1, …] (2*columns floats). The whole
/// stream is decoded once — waveforms are requested per media and cached by
/// the shell. Returns 1 on success, 0 when the file has no decodable audio.
@_cdecl("palmier_waveform")
public func palmierWaveform(_ path: UnsafePointer<CChar>?,
                            _ buf: UnsafeMutablePointer<Float>?, _ columns: Int32) -> Int32 {
    guard let path, let buf, columns > 0 else { return 0 }
    let swiftPath = String(cString: path)
    guard let decoder = try? FFmpegAudioDecoder(path: swiftPath) else { return 0 }

    let channels = FFmpegAudioDecoder.channels
    let chunkFrames = 4096
    var chunk = [Float](repeating: 0, count: chunkFrames * channels)
    var samples: [Float] = []  // mono
    while true {
        let got = chunk.withUnsafeMutableBufferPointer {
            decoder.read(into: $0.baseAddress!, sampleFrames: chunkFrames)
        }
        guard got > 0 else { break }
        samples.reserveCapacity(samples.count + got)
        for i in 0..<got {
            var sum: Float = 0
            for c in 0..<channels { sum += chunk[i * channels + c] }
            samples.append(sum / Float(channels))
        }
        if got < chunkFrames { break }
    }
    guard !samples.isEmpty else { return 0 }

    let cols = Int(columns)
    for col in 0..<cols {
        let start = samples.count * col / cols
        let end = max(start + 1, samples.count * (col + 1) / cols)
        var lo: Float = 1, hi: Float = -1
        for i in start..<min(end, samples.count) {
            lo = min(lo, samples[i])
            hi = max(hi, samples[i])
        }
        buf[col * 2] = min(lo, hi)
        buf[col * 2 + 1] = max(lo, hi)
    }
    return 1
}
