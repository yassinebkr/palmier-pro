import CVulkan
import Foundation
import PalmierCore
import PalmierWin

// Composited-frame capture: renders one timeline frame exactly as the preview
// composites it and hands back the BGRA pixels.
//
// Transition and shot stills used to be re-derived per clip — trim plus
// speed-scaled offset, decoded from the source file. That is a second
// implementation of "what is on screen at frame N", and it drifted from the
// real one: wrong fps domain once, wrong frame after edits, and it could never
// account for transforms or layered tracks at all. Upstream captures the
// composite (`captureFrameToMedia(source: .timeline(frame:))`), so the stills
// are pixel-identical to the viewer by construction. This is that, behind the
// ABI.

/// Everything a capture needs, kept alive between calls. Building a Vulkan
/// instance, device and fresh decoders per call made every frame nudge take
/// seconds; with the session persistent, a one-frame step is an incremental
/// decode. One session per project, calls serialized by its own lock.
final class CaptureSession {
    private var instance: VkInstance?
    private var device: VulkanDevice?
    private var renderer: WinFrameRenderer?
    private var offscreen: VulkanTexture?
    private var scratch: VulkanTexture?
    private var caches: DecodeCachePool? = DecodeCachePool()
    var natCache: [String: Size2D] = [:]
    let width: Int
    let height: Int
    private let lock = NSLock()

    init?(width: Int, height: Int) {
        guard let instance = Vulkan.createInstance(appName: "palmier-capture", extensions: []) else {
            return nil
        }
        guard let device = VulkanDevice.create(instance: instance),
              let renderer = WinFrameRenderer(device: device),
              let offscreen = VulkanTexture(device: device,
                                            width: UInt32(width), height: UInt32(height)),
              let scratch = VulkanTexture(device: device,
                                          width: UInt32(width), height: UInt32(height)) else {
            Vulkan.destroyInstance(instance)
            return nil
        }
        self.instance = instance
        self.device = device
        self.renderer = renderer
        self.offscreen = offscreen
        self.scratch = scratch
        self.width = width
        self.height = height
    }

    /// Releases GPU objects in dependency order, then the instance. Explicit
    /// because deinit cannot order member teardown against destroyInstance.
    func shutdown() {
        lock.lock()
        defer { lock.unlock() }
        caches = nil
        offscreen = nil
        scratch = nil
        renderer = nil
        device = nil
        if let instance {
            Vulkan.destroyInstance(instance)
            self.instance = nil
        }
    }

    /// One frame through the same planner + compositor the preview and export
    /// use. A frame over a gap renders as black — that is what the preview
    /// shows there too.
    func render(timeline: Timeline, frame: Int) -> Data? {
        lock.lock()
        defer { lock.unlock() }
        guard let device, let renderer, let offscreen, let scratch, let caches else { return nil }

        let renderSize = Size2D(width: Double(width), height: Double(height))
        let (trackSlots, mediaPaths, clipIds) = buildVideoSlots(timeline: timeline,
                                                                natCache: &natCache)
        let fps = max(1, timeline.fps)
        let instructions = RenderPlanner.plan(
            timeline: timeline, renderSize: renderSize,
            totalFrames: timeline.totalFrames, trackSlots: trackSlots,
            resolveTimeline: { _ in nil })

        guard let instruction = TimelineLookup.segment(instructions, frame: frame) else {
            engineLog("[capture] frame \(frame): no segment — renders black")
            renderer.renderEmpty(size: renderSize, fps: fps, into: offscreen)
            return offscreen.download()
        }

        // What the still will actually show, topmost layer last — the answer
        // to "why is this frame not the clip I right-clicked".
        let described = instruction.layers.map { layer in
            let name = layer.trackID.flatMap { mediaPaths[$0] }.map {
                URL(fileURLWithPath: $0).lastPathComponent
            } ?? "?"
            return "\(layer.clip.id.prefix(8)):\(name)@\(layer.clip.startFrame)"
        }.joined(separator: ", ")
        engineLog("[capture] frame \(frame) layers=[\(described)]")

        var sources: [TrackID: VulkanTexture] = [:]
        for layer in instruction.layers {
            guard let trackID = layer.trackID, sources[trackID] == nil,
                  let path = mediaPaths[trackID] else { continue }
            let clipId = clipIds[trackID] ?? "capture-\(trackID.rawValue)"
            let sourceFrame = TimelineLookup.sourceFrame(for: layer, timelineFrame: frame)
            if let tex = caches.cache(clipId: clipId, path: path, device: device, fps: fps)?
                .texture(at: sourceFrame) {
                sources[trackID] = tex
            } else {
                engineLog("[capture] frame \(frame): no texture for \(path) source \(sourceFrame)")
            }
        }
        renderer.render(instruction: instruction, frame: frame,
                        sourceFrame: { id in sources[id] }, into: offscreen)
        // Graded like the preview and the export: a transition built from an
        // ungraded still would cut against the graded footage around it.
        let effects = instruction.layers.flatMap { $0.clip.effects ?? [] }
        if !effects.isEmpty {
            let firstLayerStart = instruction.layers.first?.clip.startFrame ?? 0
            if let graded = renderer.applyEffectsOneShot(
                effects, frame: frame, clipStartFrame: firstLayerStart,
                source: offscreen, scratch: scratch) {
                return graded.download()
            }
        }
        return offscreen.download()
    }
}

/// Renders timeline `frame` composited at the timeline's render size and
/// writes tightly-packed BGRA into `buf`. `outWidth`/`outHeight` receive the
/// dimensions. Returns 1 on success, a negative required byte count when the
/// buffer is too small, and 0 on failure. Blocking GPU + decode work — call
/// off the UI thread.
@_cdecl("palmier_project_capture_frame")
public func palmierProjectCaptureFrame(_ handle: UnsafeMutableRawPointer?, _ frame: Int32,
                                       _ buf: UnsafeMutablePointer<UInt8>?, _ bufSize: Int32,
                                       _ outWidth: UnsafeMutablePointer<Int32>?,
                                       _ outHeight: UnsafeMutablePointer<Int32>?) -> Int32 {
    guard let handle, frame >= 0 else { return 0 }
    let project = Unmanaged<ProjectContext>.fromOpaque(handle).takeUnretainedValue()
    let timeline = project.snapshot()
    let width = timeline.width, height = timeline.height
    guard width > 0, height > 0 else { return 0 }
    outWidth?.pointee = Int32(width)
    outHeight?.pointee = Int32(height)
    let needed = width * height * 4
    guard let buf, Int(bufSize) >= needed else { return -Int32(needed) }

    guard let session = project.captureSession(width: width, height: height),
          let rendered = session.render(timeline: timeline, frame: Int(frame)),
          rendered.count >= needed else { return 0 }
    rendered.withUnsafeBytes { raw in
        buf.update(from: raw.baseAddress!.assumingMemoryBound(to: UInt8.self), count: needed)
    }
    return 1
}
