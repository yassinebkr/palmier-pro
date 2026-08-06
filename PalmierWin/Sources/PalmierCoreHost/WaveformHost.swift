import Foundation
import PalmierWin

/// Decodes `path`'s audio and writes `columns` min/max pairs (mono, -1…1)
/// into buf as [min0, max0, min1, max1, …] (2*columns floats). Decoded chunks
/// are folded straight into per-sample column spans sized from the container
/// duration, so memory stays O(columns). Returns 1 on success, 0 when the
/// file has no decodable audio.
@_cdecl("palmier_waveform")
public func palmierWaveform(_ path: UnsafePointer<CChar>?,
                            _ buf: UnsafeMutablePointer<Float>?, _ columns: Int32) -> Int32 {
    guard let path, let buf, columns > 0 else { return 0 }
    let swiftPath = String(cString: path)
    guard !swiftPath.isEmpty, let decoder = try? FFmpegAudioDecoder(path: swiftPath) else { return 0 }

    let channels = FFmpegAudioDecoder.channels
    let cols = Int(columns)
    let samplesPerCol = max(1, (decoder.estimatedSampleFrames ?? 240 * cols) / cols)
    let chunkFrames = 4096
    var chunk = [Float](repeating: 0, count: chunkFrames * channels)

    var g = 0          // global sample index
    var col = 0        // column currently accumulating
    var lo: Float = 1, hi: Float = -1
    while true {
        let got = chunk.withUnsafeMutableBufferPointer {
            decoder.read(into: $0.baseAddress!, sampleFrames: chunkFrames)
        }
        guard got > 0 else { break }
        for i in 0..<got {
            var sum: Float = 0
            for c in 0..<channels { sum += chunk[i * channels + c] }
            let mono = sum / Float(channels)
            // Past the estimate (VBR/priming drift), samples fold into the
            // last column; sub-percent, invisible at display scale.
            let target = min(g / samplesPerCol, cols - 1)
            if target != col {
                buf[col * 2] = min(lo, hi)
                buf[col * 2 + 1] = max(lo, hi)
                col = target
                lo = 1; hi = -1
            }
            lo = min(lo, mono); hi = max(hi, mono)
            g += 1
        }
        if got < chunkFrames { break }
    }
    guard g > 0 else { return 0 }

    buf[col * 2] = min(lo, hi)
    buf[col * 2 + 1] = max(lo, hi)
    for c in (col + 1)..<cols {  // short of the estimate: silent tail
        buf[c * 2] = 0
        buf[c * 2 + 1] = 0
    }
    return 1
}
