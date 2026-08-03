import Foundation

/// The decode caches for a timeline's clips, kept alive across presenter
/// rebuilds.
///
/// The presenter is rebuilt whenever the project revision changes — which is
/// every edit, including a volume nudge or a track rename. Rebuilding used to
/// take the decoders with it, so the next frame reopened and re-seeked every
/// clip on the render thread. On a long timeline that is a visible freeze
/// after every edit, and it looks exactly like the preview having died.
///
/// Keyed by clip identity *and* media path, so a clip that is repointed at a
/// different file gets a new decoder rather than the old file's.
public final class DecodeCachePool {
    private var caches: [String: DecodedFrameCache] = [:]

    public init() {}

    /// The cache for one clip, created on first use. Nil when the media cannot
    /// be opened.
    public func cache(clipId: String, path: String, device: VulkanDevice, fps: Int) -> DecodedFrameCache? {
        let key = "\(clipId)\u{1}\(path)"
        if let existing = caches[key] { return existing }
        guard let made = DecodedFrameCache(path: path, device: device, fps: fps) else { return nil }
        caches[key] = made
        return made
    }

    /// Drops the caches for clips no longer in the plan, releasing their
    /// decoders and textures. Called once per presenter rebuild.
    public func keepOnly(clipIds: Set<String>) {
        caches = caches.filter { key, _ in
            guard let separator = key.firstIndex(of: "\u{1}") else { return false }
            return clipIds.contains(String(key[key.startIndex..<separator]))
        }
    }
}
