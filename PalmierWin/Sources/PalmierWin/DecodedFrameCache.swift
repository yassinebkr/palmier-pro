import Foundation

/// One media source's decoder plus its most recently decoded frame, uploaded
/// to a GPU texture.
///
/// Playback and export both walk a timeline pulling source frames, and both
/// used to carry their own copy of the rule for getting one. The rule has a
/// trap in it — a seek lands on the nearest *keyframe before* the target, so
/// the caller must decode forward until the decoder confirms it arrived — and
/// the copies drifted: fixing playback left export still taking the keyframe
/// and calling it the requested frame. It lives here once now.
public final class DecodedFrameCache {
    /// Frames a walk may decode through before giving up. Must exceed the
    /// longest keyframe interval in real media or frames deep in a GOP become
    /// unreachable — x264's default is 250, so 240 silently truncated walks.
    /// Decode-only steps are cheap; the cap only guards timestamp-less streams.
    public static let maxDecodeAhead = 600

    /// Forward gap past which a seek beats decoding straight through.
    public static let seekAheadThreshold = 240

    private let decoder: FFmpegDecoder
    private let device: VulkanDevice
    private let fps: Int
    private var texture: VulkanTexture?
    /// Frame index the texture currently holds; -1 when unknown.
    private var lastFrame = -1

    public init?(path: String, device: VulkanDevice, fps: Int) {
        guard let decoder = try? FFmpegDecoder(path: path) else {
            engineLog("[decode] could not open \(path)")
            return nil
        }
        self.decoder = decoder
        self.device = device
        self.fps = max(1, fps)
    }

    /// The texture for `frame`, decoding or seeking as needed. Returns the
    /// last good frame at end of stream, nil if nothing has decoded yet.
    public func texture(at frame: Int) -> VulkanTexture? {
        if frame == lastFrame, let texture { return texture }
        let started = Date()
        var seeks = 0

        if frame < lastFrame || frame > lastFrame + Self.seekAheadThreshold {
            try? decoder.seek(toFrame: max(0, frame), fps: fps)
            lastFrame = -1          // unknown until the first decode lands
            seeks = 1
        }

        var decoded = 0
        while lastFrame < frame {
            // Decode only; the intervening frames of a walk are never shown, so
            // converting and uploading them is the cost with nothing to show
            // for it.
            guard (try? decoder.decodeNextFrame()) == true else { break }
            decoded += 1
            lastFrame = decoder.lastFrameIndex(fps: fps) ?? (lastFrame + 1)
            if lastFrame >= frame {
                if let bgra = decoder.currentBGRAFrame() { upload(bgra) }
                break
            }
            // A stream without timestamps, or a target past the end, must not
            // spin the caller's thread.
            if decoded > Self.maxDecodeAhead { break }
        }
        PreviewStats.shared.recordDecode(
            seconds: -started.timeIntervalSinceNow, frames: decoded, seeks: seeks)
        return texture
    }

    private func upload(_ bgra: Data) {
        let width = UInt32(decoder.info.width), height = UInt32(decoder.info.height)
        if texture == nil {
            texture = VulkanTexture(device: device, width: width, height: height)
        }
        guard texture?.width == width, texture?.height == height else {
            engineLog("[decode] no texture for \(width)x\(height)")
            return
        }
        _ = texture?.upload(bgra: bgra)
    }
}
