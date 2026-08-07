import CFFmpeg
import Foundation
import PalmierWin

// Filmstrip thumbnails across the ABI. Tiles are BGRA 96x54, packed
// sequentially in the caller's buffer.

public let palmierThumbTileWidth = 96
public let palmierThumbTileHeight = 54

/// Decodes one full-resolution frame of `path` into `buf` as BGRA
/// (width*height*4 bytes, sizes from palmier_probe_media). Feeds
/// capture-frame-to-media and the first/last frames of a generated transition.
///
/// `timelineFrame` is in the timeline's frame domain at `timelineFps`, *not*
/// the file's own rate. Clip trims and durations are stored that way
/// (`sourceDurationFrames` converts every source into it) and the compositor
/// resolves them with the timeline's rate. This probed the file's rate instead
/// and used that, so on any clip that was not the timeline's fps every still
/// came from the wrong time — a 24 fps clip cut at 6.5 s handed back the frame
/// at 8.1 s, and a generated transition was built from two shots that were
/// nowhere near the cut.
///
/// Returns 1 on success, 0 on failure, or a negative required size.
@_cdecl("palmier_extract_frame")
public func palmierExtractFrame(_ path: UnsafePointer<CChar>?, _ timelineFrame: Int32,
                                _ timelineFps: Int32,
                                _ buf: UnsafeMutablePointer<UInt8>?, _ bufSize: Int32) -> Int32 {
    guard let path else { return 0 }
    let swiftPath = String(cString: path)
    guard !swiftPath.isEmpty, let decoder = try? FFmpegDecoder(path: swiftPath) else { return 0 }
    let width = decoder.info.width, height = decoder.info.height
    guard width > 0, height > 0 else { return 0 }
    let needed = width * height * 4
    guard let buf, Int(bufSize) >= needed else { return -Int32(needed) }

    // Walks forward from the keyframe the seek lands on — a plain seek would
    // return that keyframe, so both sides of a cut came back as frame 0. The
    // walk is allowed to run long here: this is a one-off decode off the
    // render thread, and landing on the right frame matters more than latency.
    guard let bgra = try? decoder.frame(at: Int(timelineFrame),
                                        fps: Int(max(1, timelineFps)),
                                        maxDecodeAhead: 1200),
          bgra.count >= needed else { return 0 }
    bgra.withUnsafeBytes { raw in
        buf.update(from: raw.baseAddress!.assumingMemoryBound(to: UInt8.self), count: needed)
    }
    return 1
}

/// Decodes `count` evenly-spaced frames of `path` into consecutive BGRA tiles
/// (96x54 each) written to buf (caller-sized: 96*54*4*count bytes). Sampling
/// seeks to evenly spaced timestamps and takes the nearest keyframe's frame —
/// exact-frame accuracy is not needed for filmstrips. Returns the number of
/// tiles written, or 0 on failure.
@_cdecl("palmier_thumbnails")
public func palmierThumbnails(_ path: UnsafePointer<CChar>?,
                              _ buf: UnsafeMutablePointer<UInt8>?, _ bufSize: Int32,
                              _ count: Int32) -> Int32 {
    guard let path, let buf, count > 0 else { return 0 }
    let tileW = palmierThumbTileWidth, tileH = palmierThumbTileHeight
    let tileBytes = tileW * tileH * 4
    guard Int64(bufSize) >= Int64(tileBytes) * Int64(count) else { return 0 }
    let swiftPath = String(cString: path)
    guard !swiftPath.isEmpty, let decoder = try? FFmpegDecoder(path: swiftPath) else { return 0 }
    let srcW = decoder.info.width, srcH = decoder.info.height
    guard srcW > 0, srcH > 0 else { return 0 }

    // Probe duration once to compute evenly spaced sample frames. Falls back
    // to sequential frames when the container reports no duration.
    var probeBuf = [CChar](repeating: 0, count: 128)
    var totalFrames = -1
    var fps = 30.0
    if palmierProbeMedia(path, &probeBuf, 128) == 1,
       let probe = parseProbeLine(String(cString: probeBuf)) {
        fps = Double(probe.fpsX100) / 100.0
        totalFrames = probe.totalFrames
    }

    var written: Int32 = 0
    for i in 0..<Int(count) {
        if totalFrames > 0 {
            let target = totalFrames * i / Int(count)
            try? decoder.seek(toFrame: target, fps: Int(fps.rounded()))
        }
        guard let bgra = try? decoder.nextBGRAFrame(), bgra.count >= srcW * srcH * 4 else { break }
        bgra.withUnsafeBytes { raw in
            let src = raw.baseAddress!.assumingMemoryBound(to: UInt8.self)
            let dst = buf.advanced(by: i * tileBytes)
            for y in 0..<tileH {
                let sy = y * srcH / tileH
                for x in 0..<tileW {
                    let sx = x * srcW / tileW
                    let sOff = (sy * srcW + sx) * 4
                    let dOff = (y * tileW + x) * 4
                    dst.advanced(by: dOff).update(from: src.advanced(by: sOff), count: 4)
                }
            }
        }
        written += 1
    }
    return written
}
