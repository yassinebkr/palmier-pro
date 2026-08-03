import CFFmpeg
import Foundation
import PalmierCore
import PalmierWin

// Timeline state ABI: one open project behind an opaque handle, holding one or
// more timelines with an active one. Clip mediaRefs are file paths. Every
// intent call targets the active timeline; the shell mutates via intents and
// reads state back as JSON snapshots.

/// Retained project state. `lock` guards the timelines and the active index:
/// intent calls come from the shell's UI thread while the render loop, audio
/// mixer, and exporter read on their own workers.
final class ProjectContext {
    private let lock = NSLock()
    private var timelines: [Timeline]
    private var active: Int = 0
    private var revision: Int = 0
    private var capture: CaptureSession?

    /// The canvas the preview, capture, and export composite at. Project
    /// state, not timeline state: changing it is not an undoable edit.
    private var renderWidth = 1920
    private var renderHeight = 1080

    static func isValidRenderSize(width: Int, height: Int) -> Bool {
        // H.264 wants even dimensions; the bounds keep textures allocatable.
        width % 2 == 0 && height % 2 == 0
            && width >= 16 && width <= 7680 && height >= 16 && height <= 7680
    }

    var renderSize: (width: Int, height: Int) {
        lock.lock()
        defer { lock.unlock() }
        return (renderWidth, renderHeight)
    }

    /// Returns false for an invalid size. A valid set bumps the generation so
    /// the playback presenter rebuilds at the new canvas on the next frame.
    func setRenderSize(width: Int, height: Int) -> Bool {
        guard ProjectContext.isValidRenderSize(width: width, height: height) else { return false }
        lock.lock()
        defer { lock.unlock() }
        guard width != renderWidth || height != renderHeight else { return true }
        renderWidth = width
        renderHeight = height
        revision += 1
        return true
    }

    /// The project's persistent capture session, rebuilt when the render size
    /// changes. Kept alive so repeated captures pay decode steps, not Vulkan
    /// and decoder setup.
    func captureSession(width: Int, height: Int) -> CaptureSession? {
        lock.lock()
        defer { lock.unlock() }
        if let capture, capture.width == width, capture.height == height { return capture }
        capture?.shutdown()
        capture = CaptureSession(width: width, height: height)
        return capture
    }

    func shutdownCapture() {
        lock.lock()
        let session = capture
        capture = nil
        lock.unlock()
        session?.shutdown()
    }

    init() {
        timelines = [ProjectContext.newTimeline(named: "Timeline 1")]
    }

    static func newTimeline(named name: String) -> Timeline {
        var t = Timeline(name: name)
        t.tracks = [Track(type: .video), Track(type: .audio)]
        return t
    }

    /// Mutates the active timeline under the lock and bumps the generation.
    func withTimeline<T>(_ body: (inout Timeline) -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        let result = body(&timelines[active])
        revision += 1
        return result
    }

    func snapshot() -> Timeline {
        lock.lock()
        defer { lock.unlock() }
        return timelines[active]
    }

    /// Active timeline and generation read together — consumers that cache by
    /// generation must not see a timeline from a different revision.
    func snapshotWithGeneration() -> (timeline: Timeline, generation: Int) {
        lock.lock()
        defer { lock.unlock() }
        return (timelines[active], revision)
    }

    /// What the preview and audio mixer should play: the source-monitor
    /// override when a viewer tab holds one, otherwise the active timeline.
    /// Edits, snapshots, and export always use the real timeline.
    func renderSnapshotWithGeneration() -> (timeline: Timeline, generation: Int) {
        lock.lock()
        defer { lock.unlock() }
        return (previewSource ?? timelines[active], revision)
    }

    private var previewSource: Timeline?

    func setPreviewSource(_ timeline: Timeline?) {
        lock.lock()
        defer { lock.unlock() }
        previewSource = timeline
        revision += 1
    }

    var generation: Int {
        lock.lock()
        defer { lock.unlock() }
        return revision
    }

    // MARK: Timeline list

    var timelineCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return timelines.count
    }

    var activeIndex: Int {
        lock.lock()
        defer { lock.unlock() }
        return active
    }

    func setActive(_ index: Int) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard timelines.indices.contains(index) else { return false }
        guard index != active else { return true }
        active = index
        revision += 1
        return true
    }

    /// Appends a timeline and makes it active. Returns its index.
    func addTimeline(named name: String) -> Int {
        lock.lock()
        defer { lock.unlock() }
        timelines.append(ProjectContext.newTimeline(named: name))
        active = timelines.count - 1
        revision += 1
        return active
    }

    /// Removes a timeline; refuses to remove the last one so the project
    /// always has something to render.
    func removeTimeline(_ index: Int) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard timelines.count > 1, timelines.indices.contains(index) else { return false }
        timelines.remove(at: index)
        active = min(active >= index ? active - 1 : active, timelines.count - 1)
        active = max(0, active)
        revision += 1
        return true
    }

    func renameTimeline(_ index: Int, to name: String) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard timelines.indices.contains(index), !name.isEmpty else { return false }
        timelines[index].name = name
        revision += 1
        return true
    }

    func timelineName(_ index: Int) -> String? {
        lock.lock()
        defer { lock.unlock() }
        return timelines.indices.contains(index) ? timelines[index].name : nil
    }

    // MARK: Whole-project save/load

    func projectSnapshot() -> ProjectSnapshot {
        lock.lock()
        defer { lock.unlock() }
        return ProjectSnapshot(timelines: timelines, activeIndex: active,
                               renderWidth: renderWidth, renderHeight: renderHeight)
    }

    /// Replaces every timeline. The preview override is dropped: it points at
    /// media the restored project may not contain. A missing or invalid render
    /// size (projects saved before sizes existed) falls back to 1920×1080.
    func restore(_ snapshot: ProjectSnapshot) {
        lock.lock()
        defer { lock.unlock() }
        timelines = snapshot.timelines
        active = min(max(0, snapshot.activeIndex), timelines.count - 1)
        if let width = snapshot.renderWidth, let height = snapshot.renderHeight,
           ProjectContext.isValidRenderSize(width: width, height: height) {
            renderWidth = width
            renderHeight = height
        } else {
            renderWidth = 1920
            renderHeight = 1080
        }
        previewSource = nil
        revision += 1
    }
}

/// On-disk shape of a project's editable state.
struct ProjectSnapshot: Codable {
    var timelines: [Timeline]
    var activeIndex: Int
    var renderWidth: Int?
    var renderHeight: Int?
}

/// Creates an empty project (one video + one audio track). Never NULL.
@_cdecl("palmier_project_create")
public func palmierProjectCreate() -> UnsafeMutableRawPointer {
    Unmanaged.passRetained(ProjectContext()).toOpaque()
}

@_cdecl("palmier_project_destroy")
public func palmierProjectDestroy(_ handle: UnsafeMutableRawPointer?) {
    guard let handle else { return }
    let project = Unmanaged<ProjectContext>.fromOpaque(handle).takeUnretainedValue()
    project.shutdownCapture()
    Unmanaged<ProjectContext>.fromOpaque(handle).release()
}

private func projectContext(_ handle: UnsafeMutableRawPointer?) -> ProjectContext? {
    guard let handle else { return nil }
    return Unmanaged<ProjectContext>.fromOpaque(handle).takeUnretainedValue()
}

/// Writes a NUL-terminated ASCII string into a caller buffer. Returns 1 on
/// success, 0 if the buffer is too small.
func writeCString(_ s: String, into buf: UnsafeMutablePointer<CChar>, size: Int32) -> Int32 {
    let utf8 = Array(s.utf8)
    guard utf8.count < Int(size) else { return 0 }
    utf8.withUnsafeBufferPointer { src in
        buf.withMemoryRebound(to: UInt8.self, capacity: utf8.count + 1) { dst in
            dst.update(from: src.baseAddress!, count: utf8.count)
            dst[utf8.count] = 0
        }
    }
    return 1
}

// MARK: - Timeline tabs
//
// Every other intent in this file targets the active timeline, so switching
// tabs is the only call needed to retarget edits, playback, audio, and export.

@_cdecl("palmier_project_timeline_count")
public func palmierProjectTimelineCount(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    guard let ctx = projectContext(handle) else { return 0 }
    return Int32(ctx.timelineCount)
}

@_cdecl("palmier_project_active_timeline")
public func palmierProjectActiveTimeline(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    guard let ctx = projectContext(handle) else { return -1 }
    return Int32(ctx.activeIndex)
}

/// Returns 1 when `index` is now active, 0 when it is out of range.
@_cdecl("palmier_project_set_active_timeline")
public func palmierProjectSetActiveTimeline(_ handle: UnsafeMutableRawPointer?,
                                            _ index: Int32) -> Int32 {
    guard let ctx = projectContext(handle) else { return 0 }
    return ctx.setActive(Int(index)) ? 1 : 0
}

/// Appends an empty timeline (one video + one audio track), makes it active,
/// and returns its index; -1 on failure.
@_cdecl("palmier_project_add_timeline")
public func palmierProjectAddTimeline(_ handle: UnsafeMutableRawPointer?,
                                      _ name: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = projectContext(handle) else { return -1 }
    let requested = name.map { String(cString: $0) } ?? ""
    let title = requested.isEmpty ? "Timeline \(ctx.timelineCount + 1)" : requested
    return Int32(ctx.addTimeline(named: title))
}

/// Removes a timeline. Returns 0 when the index is unknown or it is the last
/// remaining timeline — a project always keeps one.
@_cdecl("palmier_project_remove_timeline")
public func palmierProjectRemoveTimeline(_ handle: UnsafeMutableRawPointer?,
                                         _ index: Int32) -> Int32 {
    guard let ctx = projectContext(handle) else { return 0 }
    return ctx.removeTimeline(Int(index)) ? 1 : 0
}

@_cdecl("palmier_project_rename_timeline")
public func palmierProjectRenameTimeline(_ handle: UnsafeMutableRawPointer?,
                                         _ index: Int32,
                                         _ name: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = projectContext(handle), let name else { return 0 }
    return ctx.renameTimeline(Int(index), to: String(cString: name)) ? 1 : 0
}

/// Sets the project's render size — the canvas the preview, frame capture,
/// and export composite at (clips keep their transforms; the canvas changes,
/// so sources letterbox/pillarbox into it). A project setting, not a timeline
/// edit: it pushes no undo entry. Dimensions must be even and within
/// 16…7680. Returns 1 on success, 0 on an invalid handle or size.
@_cdecl("palmier_project_set_render_size")
public func palmierProjectSetRenderSize(_ handle: UnsafeMutableRawPointer?,
                                        _ width: Int32, _ height: Int32) -> Int32 {
    guard let ctx = projectContext(handle) else { return 0 }
    return ctx.setRenderSize(width: Int(width), height: Int(height)) ? 1 : 0
}

/// Writes the project's current render size into outWidth/outHeight.
/// Returns 1 on success, 0 on an invalid handle.
@_cdecl("palmier_project_render_size")
public func palmierProjectRenderSize(_ handle: UnsafeMutableRawPointer?,
                                     _ outWidth: UnsafeMutablePointer<Int32>?,
                                     _ outHeight: UnsafeMutablePointer<Int32>?) -> Int32 {
    guard let ctx = projectContext(handle) else { return 0 }
    let size = ctx.renderSize
    outWidth?.pointee = Int32(size.width)
    outHeight?.pointee = Int32(size.height)
    return 1
}

/// Serializes the whole project — every timeline plus which one is active —
/// for saving to disk. Returns bytes written, or a negative required size.
@_cdecl("palmier_project_json")
public func palmierProjectJson(_ handle: UnsafeMutableRawPointer?,
                               _ buf: UnsafeMutablePointer<CChar>?, _ bufSize: Int32) -> Int32 {
    guard let ctx = projectContext(handle) else { return 0 }
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    guard let data = try? encoder.encode(ctx.projectSnapshot()) else { return 0 }
    if buf == nil || Int32(data.count) >= bufSize { return -Int32(data.count + 1) }
    data.withUnsafeBytes { raw in
        memcpy(buf!, raw.baseAddress!, data.count)
    }
    buf![data.count] = 0
    return Int32(data.count)
}

/// Replaces the whole project from JSON produced by palmier_project_json.
/// Returns 1 on success, 0 when the JSON does not decode.
@_cdecl("palmier_project_load_json")
public func palmierProjectLoadJson(_ handle: UnsafeMutableRawPointer?,
                                   _ json: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = projectContext(handle), let json else { return 0 }
    let data = Data(bytes: json, count: strlen(json))
    guard let decoded = try? JSONDecoder().decode(ProjectSnapshot.self, from: data),
          !decoded.timelines.isEmpty else { return 0 }
    ctx.restore(decoded)
    return 1
}

/// Points the preview and audio mixer at a single media file (the viewer's
/// source monitor) instead of the active timeline. Edits, JSON snapshots, and
/// export keep targeting the real timeline. Returns the source's length in
/// timeline frames, or 0 when the file cannot be probed.
@_cdecl("palmier_project_set_preview_source")
public func palmierProjectSetPreviewSource(_ handle: UnsafeMutableRawPointer?,
                                           _ path: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = projectContext(handle), let path else { return 0 }
    let mediaPath = String(cString: path)
    guard let frames = sourceDurationFrames(path: mediaPath), frames > 0 else { return 0 }

    var source = Timeline(name: "Source")
    var video = Track(type: .video)
    var clip = Clip(mediaRef: mediaPath, startFrame: 0, durationFrames: frames)
    video.clips = [clip]
    source.tracks = [video]
    if FFmpegAudioDecoder.hasAudioStream(path: mediaPath) {
        var audio = Track(type: .audio)
        clip.id = UUID().uuidString
        clip.mediaType = .audio
        clip.sourceClipType = .audio
        audio.clips = [clip]
        source.tracks.append(audio)
    }
    ctx.setPreviewSource(source)
    return Int32(frames)
}

@_cdecl("palmier_project_clear_preview_source")
public func palmierProjectClearPreviewSource(_ handle: UnsafeMutableRawPointer?) {
    projectContext(handle)?.setPreviewSource(nil)
}

@_cdecl("palmier_project_timeline_name")
public func palmierProjectTimelineName(_ handle: UnsafeMutableRawPointer?,
                                       _ index: Int32,
                                       _ buf: UnsafeMutablePointer<CChar>?,
                                       _ bufSize: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let buf,
          let name = ctx.timelineName(Int(index)) else { return 0 }
    return writeCString(name, into: buf, size: bufSize)
}

/// Probes a media file via libavformat without decoding. Writes
/// "width,height,fpsX100,totalFrames" (ASCII) into buf. totalFrames is -1
/// when the container reports no duration. Returns 1 on success, 0 on failure.
@_cdecl("palmier_probe_media")
public func palmierProbeMedia(_ path: UnsafePointer<CChar>?,
                              _ buf: UnsafeMutablePointer<CChar>?, _ bufSize: Int32) -> Int32 {
    guard let path, let buf, bufSize > 0 else { return 0 }
    let swiftPath = String(cString: path)

    var fmtCtx: UnsafeMutablePointer<AVFormatContext>? = nil
    guard swiftPath.withCString({ avformat_open_input(&fmtCtx, $0, nil, nil) }) == 0, let fmt = fmtCtx else { return 0 }
    defer { var f: UnsafeMutablePointer<AVFormatContext>? = fmt; avformat_close_input(&f) }
    guard avformat_find_stream_info(fmt, nil) >= 0 else { return 0 }

    let vidx = av_find_best_stream(fmt, AVMEDIA_TYPE_VIDEO, -1, -1, nil, 0)
    guard vidx >= 0,
          let streamsBase = fmt.pointee.streams,
          Int(vidx) < Int(fmt.pointee.nb_streams),
          let stream = streamsBase[Int(vidx)],
          let par = stream.pointee.codecpar else { return 0 }

    let width = Int(par.pointee.width)
    let height = Int(par.pointee.height)

    let rate = stream.pointee.avg_frame_rate
    let fps: Double = (rate.num > 0 && rate.den > 0) ? Double(rate.num) / Double(rate.den) : 30
    let fpsX100 = Int((fps * 100).rounded())

    // Container duration is in AV_TIME_BASE (microseconds) units.
    let totalFrames: Int
    let duration = fmt.pointee.duration
    if duration > 0 {
        totalFrames = Int((Double(duration) / 1_000_000 * fps).rounded())
    } else {
        totalFrames = -1
    }

    return writeCString("\(width),\(height),\(fpsX100),\(totalFrames)", into: buf, size: bufSize)
}

/// Adds a clip for `mediaPath` at the end of the video track. When the
/// source has an audio stream, a linked audio clip (shared linkGroupId, same
/// placement) lands on the first audio track. Writes the new video clip's
/// stable id into `idBuf` (NUL-terminated). Returns the clip's frame
/// duration, or 0 on failure (invalid arguments, no video track, id buffer
/// too small).
@_cdecl("palmier_timeline_add_clip")
public func palmierTimelineAddClip(_ handle: UnsafeMutableRawPointer?,
                                   _ mediaPath: UnsafePointer<CChar>?, _ durationFrames: Int32,
                                   _ idBuf: UnsafeMutablePointer<CChar>?, _ idBufSize: Int32) -> Int32 {
    addClip(handle, mediaPath, durationFrames, startFrame: nil, idBuf, idBufSize)
}

/// Adds a clip at an explicit timeline frame (drag-and-drop placement)
/// instead of appending at the track's end. Same contract as
/// palmier_timeline_add_clip otherwise.
@_cdecl("palmier_timeline_add_clip_at")
public func palmierTimelineAddClipAt(_ handle: UnsafeMutableRawPointer?,
                                     _ mediaPath: UnsafePointer<CChar>?, _ durationFrames: Int32,
                                     _ startFrame: Int32,
                                     _ idBuf: UnsafeMutablePointer<CChar>?, _ idBufSize: Int32) -> Int32 {
    guard startFrame >= 0 else { return 0 }
    return addClip(handle, mediaPath, durationFrames, startFrame: Int(startFrame), idBuf, idBufSize)
}

private func addClip(_ handle: UnsafeMutableRawPointer?,
                     _ mediaPath: UnsafePointer<CChar>?, _ durationFrames: Int32,
                     startFrame: Int?,
                     _ idBuf: UnsafeMutablePointer<CChar>?, _ idBufSize: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let mediaPath, let idBuf, durationFrames > 0 else { return 0 }
    let path = String(cString: mediaPath)
    let hasAudio = FFmpegAudioDecoder.hasAudioStream(path: path)
    var clip = Clip(mediaRef: path, startFrame: 0, durationFrames: Int(durationFrames))
    guard writeCString(clip.id, into: idBuf, size: idBufSize) == 1 else { return 0 }
    let added: Bool = ctx.withTimeline { timeline in
        guard let trackIndex = timeline.tracks.firstIndex(where: { $0.type == .video }) else { return false }
        clip.startFrame = startFrame ?? timeline.tracks[trackIndex].endFrame
        if hasAudio, let audioIndex = timeline.tracks.firstIndex(where: { $0.type == .audio }) {
            var audio = clip
            audio.id = UUID().uuidString
            audio.mediaType = .audio
            audio.sourceClipType = .audio
            let link = UUID().uuidString
            audio.linkGroupId = link
            clip.linkGroupId = link
            insertOverwriting(audio, into: &timeline.tracks[audioIndex].clips)
        }
        insertOverwriting(clip, into: &timeline.tracks[trackIndex].clips)
        return true
    }
    return added ? durationFrames : 0
}

/// Clears [clip.startFrame, clip.endFrame) via OverwriteEngine, then inserts
/// the clip in start order. Clips never overlap on a track.
private func insertOverwriting(_ clip: Clip, into clips: inout [Clip], excluding excluded: Set<String> = []) {
    let others = clips.filter { !excluded.contains($0.id) }
    let actions = OverwriteEngine.computeOverwrite(
        clips: others, regionStart: clip.startFrame, regionEnd: clip.endFrame,
        idProvider: { UUID().uuidString })
    apply(actions, to: &clips)
    clips.append(clip)
    clips.sort { $0.startFrame < $1.startFrame }
}

private func apply(_ actions: [OverwriteEngine.Action], to clips: inout [Clip]) {
    for action in actions {
        switch action {
        case .remove(let clipId):
            clips.removeAll { $0.id == clipId }
        case .trimEnd(let clipId, let newDuration):
            guard let i = clips.firstIndex(where: { $0.id == clipId }) else { continue }
            clips[i].setDuration(newDuration)
        case .trimStart(let clipId, let newStartFrame, let newTrimStart, let newDuration):
            guard let i = clips.firstIndex(where: { $0.id == clipId }) else { continue }
            clips[i].startFrame = newStartFrame
            clips[i].trimStartFrame = newTrimStart
            clips[i].setDuration(newDuration)
        case .split(let clipId, let leftDuration, let rightId, let rightStartFrame, let rightTrimStart, let rightDuration):
            guard let i = clips.firstIndex(where: { $0.id == clipId }) else { continue }
            var right = clips[i]
            right.id = rightId
            right.startFrame = rightStartFrame
            right.trimStartFrame = rightTrimStart
            right.setDuration(rightDuration)
            right.fadeInFrames = 0
            clips[i].setDuration(leftDuration)
            clips[i].fadeOutFrames = 0
            clips.insert(right, at: i + 1)
        }
    }
}

/// Moves a clip toward `newStartFrame`, clamping so it never overlaps other
/// clips: dragging into a neighbor sticks flush against its edge (the clamped
/// placement is the documented contract — a drag is a positional gesture, not
/// a destructive edit). Clips sharing the moved clip's linkGroupId shift by
/// the same delta; the clamp is the tightest across all affected tracks.
/// Returns 1 on success (including a fully-clamped no-op), 0 for an unknown
/// clip or negative frame.
@_cdecl("palmier_timeline_move_clip")
public func palmierTimelineMoveClip(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                                    _ newStartFrame: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let clipId, newStartFrame >= 0 else { return 0 }
    let id = String(cString: clipId)
    return ctx.withTimeline { timeline in
        var requestedDelta: Int?
        var linkGroup: String?
        for track in timeline.tracks {
            if let clip = track.clips.first(where: { $0.id == id }) {
                requestedDelta = Int(newStartFrame) - clip.startFrame
                linkGroup = clip.linkGroupId
                break
            }
        }
        guard let requestedDelta else { return 0 }
        func isMoved(_ clip: Clip) -> Bool {
            clip.id == id || (linkGroup != nil && clip.linkGroupId == linkGroup)
        }

        // Allowed delta interval: each moved clip stays within the gap between
        // its non-moved neighbors (no jumping over clips), and above frame 0.
        var lo = Int.min, hi = Int.max
        for track in timeline.tracks {
            let moved = track.clips.filter(isMoved)
            guard !moved.isEmpty else { continue }
            let others = track.clips.filter { !isMoved($0) }
            for clip in moved {
                lo = max(lo, -clip.startFrame)
                for other in others {
                    if other.endFrame <= clip.startFrame {
                        lo = max(lo, other.endFrame - clip.startFrame)
                    } else if other.startFrame >= clip.endFrame {
                        hi = min(hi, other.startFrame - clip.endFrame)
                    }
                }
            }
        }
        let delta = min(max(requestedDelta, lo), hi)
        if delta == 0 { return 1 }

        for trackIndex in timeline.tracks.indices {
            for clipIndex in timeline.tracks[trackIndex].clips.indices
            where isMoved(timeline.tracks[trackIndex].clips[clipIndex]) {
                timeline.tracks[trackIndex].clips[clipIndex].startFrame += delta
            }
            timeline.tracks[trackIndex].clips.sort { $0.startFrame < $1.startFrame }
        }
        return 1
    }
}

// Source duration cache for trim limits (timeline-fps frames), keyed by path.
private let mediaDurationLock = NSLock()
private nonisolated(unsafe) var mediaDurationCache: [String: Int] = [:]

/// Probes the source lengths an edit is about to consult, before the project
/// lock is taken.
///
/// Trim and roll clamp against the media's length, and the first probe of a
/// file opens it — tens to hundreds of milliseconds of disk I/O. Doing that
/// inside `withTimeline` holds the lock the render thread needs for every
/// frame, so the preview froze for the length of the probe on the first trim
/// of each clip. The results are cached by path, so warming them here costs
/// the edit nothing and the lock nothing.
func warmSourceDurations(_ ctx: ProjectContext, clipIds: Set<String>) {
    let clips = ctx.snapshot().tracks.flatMap(\.clips)
    let groups = Set(clips.filter { clipIds.contains($0.id) }.compactMap(\.linkGroupId))
    var paths = Set<String>()
    for clip in clips where clip.mediaType != .text {
        if clipIds.contains(clip.id) || clip.linkGroupId.map(groups.contains) == true {
            paths.insert(clip.mediaRef)
        }
    }
    for path in paths { _ = sourceDurationFrames(path: path) }
}

/// Media length in timeline frames (30 fps domain), or nil when unknown.
func sourceDurationFrames(path: String) -> Int? {
    mediaDurationLock.lock()
    if let cached = mediaDurationCache[path] {
        mediaDurationLock.unlock()
        return cached >= 0 ? cached : nil
    }
    mediaDurationLock.unlock()
    var buf = [CChar](repeating: 0, count: 128)
    var frames = -1
    if palmierProbeMedia(path, &buf, 128) == 1 {
        let parts = String(cString: buf).split(separator: ",")
        if parts.count == 4, let fpsX100 = Int(parts[2]), let total = Int(parts[3]),
           fpsX100 > 0, total > 0 {
            frames = Int((Double(total) / (Double(fpsX100) / 100.0) * 30.0).rounded())
        }
    }
    mediaDurationLock.lock()
    mediaDurationCache[path] = frames
    mediaDurationLock.unlock()
    return frames >= 0 ? frames : nil
}

/// Trims a clip edge to `boundaryFrame` (timeline frame). edge 0 = left
/// (in-point), 1 = right (out-point). The boundary clamps by contract: at
/// least 1 frame of clip, flush against track neighbors, and within the
/// source media (head trim can restore trimmed-off frames; the out-point
/// cannot run past the file's end). Returns 1 on success (including a
/// clamped result), 0 for unknown clip / invalid edge.
@_cdecl("palmier_clip_trim")
public func palmierClipTrim(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                            _ edge: Int32, _ boundaryFrame: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let clipId, edge == 0 || edge == 1 else { return 0 }
    let id = String(cString: clipId)
    warmSourceDurations(ctx, clipIds: [id])
    return ctx.withTimeline { timeline in
        // Trim the grabbed clip, then mirror the applied boundary onto linked
        // partners that share the trimmed edge. Per-edge on purpose: requiring
        // the whole placement to match meant one divergence — any earlier
        // lone-sided edit — silently ended mirroring forever, and every trim
        // after that pushed the pair further out of sync.
        guard let result = trimClip(id: id, edge: edge, boundaryFrame: Int(boundaryFrame), in: &timeline) else {
            return 0
        }
        if let linkGroup = result.linkGroupId {
            for track in timeline.tracks {
                for partner in track.clips
                where partner.linkGroupId == linkGroup && partner.id != id
                    && (edge == 0 ? partner.startFrame == result.originalStart
                                  : partner.endFrame == result.originalEnd) {
                    _ = trimClip(id: partner.id, edge: edge, boundaryFrame: result.appliedBoundary, in: &timeline)
                }
            }
        }
        return 1
    }
}

/// Rolls the cut between two touching clips: the outgoing clip's tail and the
/// incoming clip's head move together, so the pair's combined length and
/// everything downstream stay put. Both clips must abut, and neither may be
/// rolled past one frame or past the end of its source.
/// Returns the applied boundary, or -1 when the edit is not a valid roll.
@_cdecl("palmier_timeline_roll_edit")
public func palmierTimelineRollEdit(_ handle: UnsafeMutableRawPointer?,
                                    _ leftClipId: UnsafePointer<CChar>?,
                                    _ rightClipId: UnsafePointer<CChar>?,
                                    _ boundaryFrame: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let leftClipId, let rightClipId else { return -1 }
    let left = String(cString: leftClipId), right = String(cString: rightClipId)
    warmSourceDurations(ctx, clipIds: [left, right])
    return ctx.withTimeline { timeline in
        guard let bounds = rollBounds(left: left, right: right, in: timeline) else { return -1 }
        let target = min(max(Int(boundaryFrame), bounds.min), bounds.max)
        guard target != bounds.current else { return Int32(target) }

        // Order matters: shrink first so the grown side always has room, and
        // the intermediate state never has the two clips overlapping.
        if target < bounds.current {
            _ = trimClip(id: left, edge: 1, boundaryFrame: target, in: &timeline)
            _ = trimClip(id: right, edge: 0, boundaryFrame: target, in: &timeline)
        } else {
            _ = trimClip(id: right, edge: 0, boundaryFrame: target, in: &timeline)
            _ = trimClip(id: left, edge: 1, boundaryFrame: target, in: &timeline)
        }
        rollLinkedPartners(of: left, edge: 1, to: target, in: &timeline)
        rollLinkedPartners(of: right, edge: 0, to: target, in: &timeline)
        return Int32(target)
    }
}

/// How far the shared boundary can travel in each direction.
private func rollBounds(left: String, right: String, in timeline: Timeline)
    -> (current: Int, min: Int, max: Int)? {
    let clips = timeline.tracks.flatMap(\.clips)
    guard let a = clips.first(where: { $0.id == left }),
          let b = clips.first(where: { $0.id == right }),
          a.endFrame == b.startFrame else { return nil }

    // Each side keeps at least one frame; the growing side also needs source.
    var lower = a.startFrame + 1
    var upper = b.endFrame - 1
    if a.mediaType != .text, let sourceFrames = sourceDurationFrames(path: a.mediaRef) {
        let available = sourceFrames - a.trimStartFrame
        upper = min(upper, a.startFrame + max(1, Int((Double(available) / max(a.speed, 0.0001)).rounded(.down))))
    }
    if b.mediaType != .text {
        // Rolling left pulls the incoming clip's head earlier, which needs
        // unused source before its current trim point.
        let headroom = Int((Double(b.trimStartFrame) / max(b.speed, 0.0001)).rounded(.down))
        lower = max(lower, b.startFrame - headroom)
    }
    guard lower <= upper else { return nil }
    return (a.endFrame, lower, upper)
}

/// Keeps a rolled clip's linked audio (or video) on the same cut.
private func rollLinkedPartners(of clipId: String, edge: Int32, to boundary: Int,
                                in timeline: inout Timeline) {
    guard let group = linkGroup(of: clipId, in: timeline) else { return }
    let partners = timeline.tracks.flatMap(\.clips)
        .filter { $0.linkGroupId == group && $0.id != clipId }
        .map(\.id)
    for partner in partners {
        _ = trimClip(id: partner, edge: edge, boundaryFrame: boundary, in: &timeline)
    }
}

private struct TrimResult {
    let appliedBoundary: Int
    let originalStart: Int
    let originalEnd: Int
    let linkGroupId: String?
}

private func trimClip(id: String, edge: Int32, boundaryFrame: Int, in timeline: inout Timeline) -> TrimResult? {
        for trackIndex in timeline.tracks.indices {
            guard let clipIndex = timeline.tracks[trackIndex].clips.firstIndex(where: { $0.id == id }) else { continue }
            var clip = timeline.tracks[trackIndex].clips[clipIndex]
            let originalStart = clip.startFrame
            let originalEnd = clip.endFrame
            let others = timeline.tracks[trackIndex].clips.filter { $0.id != id }

            let appliedBoundary: Int
            if edge == 0 {
                var newStart = boundaryFrame
                newStart = min(newStart, clip.endFrame - 1)
                // Can't restore more head than was trimmed off (speed-aware).
                let maxHeadRestore = Int((Double(clip.trimStartFrame) / max(clip.speed, 0.0001)).rounded(.down))
                newStart = max(newStart, clip.startFrame - maxHeadRestore, 0)
                for other in others where other.endFrame <= clip.startFrame {
                    newStart = max(newStart, other.endFrame)
                }
                appliedBoundary = newStart
                let delta = newStart - clip.startFrame
                if delta != 0 {
                    clip.trimStartFrame = max(0, clip.trimStartFrame + Int((Double(delta) * clip.speed).rounded()))
                    clip.startFrame = newStart
                    clip.setDuration(clip.durationFrames - delta)
                }
            } else {
                var newEnd = boundaryFrame
                newEnd = max(newEnd, clip.startFrame + 1)
                for other in others where other.startFrame >= clip.endFrame {
                    newEnd = min(newEnd, other.startFrame)
                }
                if clip.mediaType != .text, let sourceFrames = sourceDurationFrames(path: clip.mediaRef) {
                    let availableSource = sourceFrames - clip.trimStartFrame
                    let maxDuration = Int((Double(availableSource) / max(clip.speed, 0.0001)).rounded(.down))
                    newEnd = min(newEnd, clip.startFrame + max(1, maxDuration))
                }
                appliedBoundary = newEnd
                if newEnd != clip.endFrame {
                    clip.setDuration(newEnd - clip.startFrame)
                }
            }

            timeline.tracks[trackIndex].clips[clipIndex] = clip
            return TrimResult(appliedBoundary: appliedBoundary, originalStart: originalStart,
                              originalEnd: originalEnd, linkGroupId: clip.linkGroupId)
        }
        return nil
}

/// Sets a clip's fade ramps in frames (clamped so head + tail fit inside the
/// clip, mirroring the model's own rule). Returns 1/0.
@_cdecl("palmier_clip_set_fades")
public func palmierClipSetFades(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                                _ fadeInFrames: Int32, _ fadeOutFrames: Int32) -> Int32 {
    guard fadeInFrames >= 0, fadeOutFrames >= 0 else { return 0 }
    return mutateClip(handle, clipId) { clip in
        clip.setFade(.left, frames: Int(fadeInFrames))
        clip.setFade(.right, frames: Int(fadeOutFrames))
        return true
    }
}

/// Adds a text clip at `startFrame` on the first video track (overwrite
/// placement, like a media drop). Writes the new clip id into idBuf.
/// Returns 1 on success, 0 on invalid arguments.
@_cdecl("palmier_timeline_add_text_clip")
public func palmierTimelineAddTextClip(_ handle: UnsafeMutableRawPointer?,
                                       _ text: UnsafePointer<CChar>?,
                                       _ startFrame: Int32, _ durationFrames: Int32,
                                       _ idBuf: UnsafeMutablePointer<CChar>?, _ idBufSize: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let text, let idBuf,
          startFrame >= 0, durationFrames > 0 else { return 0 }
    var clip = Clip(mediaRef: "text", startFrame: Int(startFrame), durationFrames: Int(durationFrames))
    clip.mediaType = .text
    clip.sourceClipType = .text
    clip.textContent = String(cString: text)
    clip.textStyle = TextStyle()
    guard writeCString(clip.id, into: idBuf, size: idBufSize) == 1 else { return 0 }
    return ctx.withTimeline { timeline in
        guard let trackIndex = timeline.tracks.firstIndex(where: { $0.type == .video }) else { return 0 }
        insertOverwriting(clip, into: &timeline.tracks[trackIndex].clips)
        return 1
    }
}

/// Replaces a text clip's content. Returns 1, or 0 for non-text clips.
@_cdecl("palmier_clip_set_text")
public func palmierClipSetText(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                               _ text: UnsafePointer<CChar>?) -> Int32 {
    guard let text else { return 0 }
    let content = String(cString: text)
    return mutateClip(handle, clipId) { clip in
        guard clip.mediaType == .text else { return false }
        clip.textContent = content
        return true
    }
}

/// Patches a text clip's style from a flat JSON object. Known keys:
/// "fontSize" (positive number), "color" ("#RGB"/"#RRGGBB"/"#RRGGBBAA"),
/// "alignment" ("left"/"center"/"right"). Unknown keys or malformed values
/// refuse the whole patch. Returns 1 on success, 0 for non-text clips.
@_cdecl("palmier_clip_set_text_style")
public func palmierClipSetTextStyle(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                                    _ styleJson: UnsafePointer<CChar>?) -> Int32 {
    guard let styleJson,
          let data = String(cString: styleJson).data(using: .utf8),
          let raw = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return 0 }
    var fontSize: Double?
    var color: TextStyle.RGBA?
    var alignment: TextStyle.Alignment?
    for (key, value) in raw {
        switch key {
        case "fontSize":
            guard let number = value as? NSNumber, String(cString: number.objCType) != "c",
                  number.doubleValue.isFinite, number.doubleValue > 0 else { return 0 }
            fontSize = number.doubleValue
        case "color":
            guard let hex = value as? String, let parsed = TextStyle.RGBA(hex: hex) else { return 0 }
            color = parsed
        case "alignment":
            guard let name = value as? String, let parsed = TextStyle.Alignment(rawValue: name) else { return 0 }
            alignment = parsed
        default:
            return 0
        }
    }
    return mutateClip(handle, clipId) { clip in
        guard clip.mediaType == .text else { return false }
        var style = clip.textStyle ?? TextStyle()
        if let fontSize { style.fontSize = fontSize }
        if let color { style.color = color }
        if let alignment { style.alignment = alignment }
        clip.textStyle = style
        return true
    }
}

/// Adds (or replaces) a keyframe at a timeline frame inside the clip. The
/// property selects the track and value encoding — "opacity" (v1: 0…1),
/// "rotation" (v1: degrees), "volume" (v1: dB), "position" (v1/v2: top-left
/// x/y in canvas space), "scale" (v1/v2: width/height as canvas fractions).
/// The frame is clamped into the clip's range. Returns 1/0.
@_cdecl("palmier_clip_add_keyframe")
public func palmierClipAddKeyframe(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                                   _ property: UnsafePointer<CChar>?, _ timelineFrame: Int32,
                                   _ v1: Double, _ v2: Double) -> Int32 {
    guard let property, v1.isFinite, v2.isFinite else { return 0 }
    let name = String(cString: property)
    return mutateClip(handle, clipId) { clip in
        let offset = max(0, min(Int(timelineFrame) - clip.startFrame, clip.durationFrames))
        switch name {
        case "opacity":
            guard (0...1).contains(v1) else { return false }
            var track = clip.opacityTrack ?? KeyframeTrack<Double>()
            track.upsert(Keyframe(frame: offset, value: v1))
            clip.opacityTrack = track
        case "rotation":
            var track = clip.rotationTrack ?? KeyframeTrack<Double>()
            track.upsert(Keyframe(frame: offset, value: v1))
            clip.rotationTrack = track
        case "volume":
            guard v1 >= -96, v1 <= 12 else { return false }
            var track = clip.volumeTrack ?? KeyframeTrack<Double>()
            track.upsert(Keyframe(frame: offset, value: v1))
            clip.volumeTrack = track
        case "position":
            var track = clip.positionTrack ?? KeyframeTrack<AnimPair>()
            track.upsert(Keyframe(frame: offset, value: AnimPair(a: v1, b: v2)))
            clip.positionTrack = track
        case "scale":
            guard v1 > 0, v2 > 0 else { return false }
            var track = clip.scaleTrack ?? KeyframeTrack<AnimPair>()
            track.upsert(Keyframe(frame: offset, value: AnimPair(a: v1, b: v2)))
            clip.scaleTrack = track
        default:
            return false
        }
        return true
    }
}

/// Removes the keyframe of `property` at a timeline frame (clamped into the
/// clip, mirroring add). A track emptied by the removal is dropped entirely so
/// the clip stops reporting keyframes it no longer has. Returns 1, or 0 when
/// there was no keyframe at that frame — deleting nothing must say so.
@_cdecl("palmier_clip_remove_keyframe")
public func palmierClipRemoveKeyframe(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                                      _ property: UnsafePointer<CChar>?, _ timelineFrame: Int32) -> Int32 {
    guard let property else { return 0 }
    let name = String(cString: property)
    return mutateClip(handle, clipId) { clip in
        let offset = max(0, min(Int(timelineFrame) - clip.startFrame, clip.durationFrames))
        func drop<V>(_ track: inout KeyframeTrack<V>?) -> Bool {
            guard var live = track, live.keyframes.contains(where: { $0.frame == offset }) else {
                return false
            }
            live.remove(at: offset)
            track = live.keyframes.isEmpty ? nil : live
            return true
        }
        switch name {
        case "opacity": return drop(&clip.opacityTrack)
        case "rotation": return drop(&clip.rotationTrack)
        case "volume": return drop(&clip.volumeTrack)
        case "position": return drop(&clip.positionTrack)
        case "scale": return drop(&clip.scaleTrack)
        default: return false
        }
    }
}

/// Clears every keyframe of `property` on the clip (see add for names).
/// Returns 1, or 0 for an unknown property/clip.
@_cdecl("palmier_clip_clear_keyframes")
public func palmierClipClearKeyframes(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                                      _ property: UnsafePointer<CChar>?) -> Int32 {
    guard let property else { return 0 }
    let name = String(cString: property)
    return mutateClip(handle, clipId) { clip in
        switch name {
        case "opacity": clip.opacityTrack = nil
        case "rotation": clip.rotationTrack = nil
        case "volume": clip.volumeTrack = nil
        case "position": clip.positionTrack = nil
        case "scale": clip.scaleTrack = nil
        default: return false
        }
        return true
    }
}

/// Sets a track's muted (audio) flag. Returns 1, or 0 for an unknown track.
@_cdecl("palmier_track_set_muted")
public func palmierTrackSetMuted(_ handle: UnsafeMutableRawPointer?, _ trackId: UnsafePointer<CChar>?,
                                 _ muted: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let trackId else { return 0 }
    let id = String(cString: trackId)
    return ctx.withTimeline { timeline in
        guard let index = timeline.tracks.firstIndex(where: { $0.id == id }) else { return 0 }
        timeline.tracks[index].muted = muted != 0
        return 1
    }
}

/// Sets a track's hidden (video) flag. Returns 1, or 0 for an unknown track.
@_cdecl("palmier_track_set_hidden")
public func palmierTrackSetHidden(_ handle: UnsafeMutableRawPointer?, _ trackId: UnsafePointer<CChar>?,
                                  _ hidden: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let trackId else { return 0 }
    let id = String(cString: trackId)
    return ctx.withTimeline { timeline in
        guard let index = timeline.tracks.firstIndex(where: { $0.id == id }) else { return 0 }
        timeline.tracks[index].hidden = hidden != 0
        return 1
    }
}

/// Renames a track. An empty or whitespace-only name clears the custom name,
/// so the track falls back to its derived label (V1, A2…). Returns 1, or 0 for
/// an unknown track.
@_cdecl("palmier_track_rename")
public func palmierTrackRename(_ handle: UnsafeMutableRawPointer?, _ trackId: UnsafePointer<CChar>?,
                               _ name: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = projectContext(handle), let trackId else { return 0 }
    let id = String(cString: trackId)
    let trimmed = name.map { String(cString: $0).trimmingCharacters(in: .whitespacesAndNewlines) } ?? ""
    return ctx.withTimeline { timeline in
        guard let index = timeline.tracks.firstIndex(where: { $0.id == id }) else { return 0 }
        timeline.tracks[index].name = trimmed.isEmpty ? nil : trimmed
        return 1
    }
}

/// Moves a clip to another track of the same kind, landing at `startFrame`
/// (overwriting whatever is there, like a fresh drop). A linked clip loses its
/// link: its partner stays behind, so keeping them paired would be a lie.
/// Returns 1 on success, 0 for an unknown clip/track or a kind mismatch.
@_cdecl("palmier_timeline_move_clip_to_track")
public func palmierTimelineMoveClipToTrack(_ handle: UnsafeMutableRawPointer?,
                                           _ clipId: UnsafePointer<CChar>?,
                                           _ trackId: UnsafePointer<CChar>?,
                                           _ startFrame: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let clipId, let trackId, startFrame >= 0 else { return 0 }
    let id = String(cString: clipId), destinationId = String(cString: trackId)
    return ctx.withTimeline { timeline in
        guard let sourceIndex = timeline.tracks.firstIndex(where: { $0.clips.contains { $0.id == id } }),
              let clipIndex = timeline.tracks[sourceIndex].clips.firstIndex(where: { $0.id == id }),
              let destination = timeline.tracks.firstIndex(where: { $0.id == destinationId }),
              timeline.tracks[destination].type == timeline.tracks[sourceIndex].type else { return 0 }
        if sourceIndex == destination { return 0 }

        var clip = timeline.tracks[sourceIndex].clips.remove(at: clipIndex)
        if let group = clip.linkGroupId {
            // Only break the pair when a partner is actually left behind.
            let partners = timeline.tracks.flatMap(\.clips).filter { $0.linkGroupId == group }
            if !partners.isEmpty {
                clip.linkGroupId = nil
                if partners.count == 1,
                   let t = timeline.tracks.firstIndex(where: { $0.clips.contains { $0.id == partners[0].id } }),
                   let c = timeline.tracks[t].clips.firstIndex(where: { $0.id == partners[0].id }) {
                    timeline.tracks[t].clips[c].linkGroupId = nil
                }
            }
        }
        clip.startFrame = Int(startFrame)
        insertOverwriting(clip, into: &timeline.tracks[destination].clips)
        return 1
    }
}

/// Appends a track of `kind` ("video" or "audio") and writes its id into
/// `idBuf`. Video tracks stack above V1, audio below A1, matching how the
/// timeline is drawn. Returns 1 on success.
@_cdecl("palmier_timeline_add_track")
public func palmierTimelineAddTrack(_ handle: UnsafeMutableRawPointer?,
                                    _ kind: UnsafePointer<CChar>?,
                                    _ idBuf: UnsafeMutablePointer<CChar>?, _ idBufSize: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let kind, let idBuf else { return 0 }
    let type: ClipType = String(cString: kind).lowercased() == "audio" ? .audio : .video
    let track = Track(type: type)
    guard writeCString(track.id, into: idBuf, size: idBufSize) == 1 else { return 0 }
    return ctx.withTimeline { timeline in
        // Video tracks come first in the array, audio after; insert at the end
        // of the matching run so numbering stays stable.
        let insertAt = type == .video
            ? (timeline.tracks.lastIndex { $0.type == .video }.map { $0 + 1 } ?? 0)
            : timeline.tracks.count
        timeline.tracks.insert(track, at: insertAt)
        return 1
    }
}

/// Removes a track and everything on it. Refuses to remove the last video or
/// last audio track so the timeline always has somewhere to drop media.
/// Returns 1 on success, 0 when unknown or the last of its kind.
@_cdecl("palmier_timeline_remove_track")
public func palmierTimelineRemoveTrack(_ handle: UnsafeMutableRawPointer?,
                                       _ trackId: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = projectContext(handle), let trackId else { return 0 }
    let id = String(cString: trackId)
    return ctx.withTimeline { timeline in
        guard let index = timeline.tracks.firstIndex(where: { $0.id == id }) else { return 0 }
        let type = timeline.tracks[index].type
        guard timeline.tracks.count(where: { $0.type == type }) > 1 else { return 0 }
        timeline.tracks.remove(at: index)
        return 1
    }
}

/// Breaks the link between a clip and its partners so audio and video can be
/// trimmed and moved separately. Returns 1 when a link was broken.
@_cdecl("palmier_clip_unlink")
public func palmierClipUnlink(_ handle: UnsafeMutableRawPointer?,
                              _ clipId: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = projectContext(handle), let clipId else { return 0 }
    let id = String(cString: clipId)
    return ctx.withTimeline { timeline in
        guard let group = linkGroup(of: id, in: timeline) else { return 0 }
        for t in timeline.tracks.indices {
            for c in timeline.tracks[t].clips.indices where timeline.tracks[t].clips[c].linkGroupId == group {
                timeline.tracks[t].clips[c].linkGroupId = nil
            }
        }
        return 1
    }
}

/// Links two clips so trims and moves apply to both. Returns 1 on success, 0
/// when either id is unknown.
@_cdecl("palmier_clip_link")
public func palmierClipLink(_ handle: UnsafeMutableRawPointer?,
                            _ clipIdA: UnsafePointer<CChar>?,
                            _ clipIdB: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = projectContext(handle), let clipIdA, let clipIdB else { return 0 }
    let a = String(cString: clipIdA), b = String(cString: clipIdB)
    guard a != b else { return 0 }
    let group = UUID().uuidString
    return ctx.withTimeline { timeline in
        var found = 0
        for t in timeline.tracks.indices {
            for c in timeline.tracks[t].clips.indices {
                let clipId = timeline.tracks[t].clips[c].id
                if clipId == a || clipId == b {
                    timeline.tracks[t].clips[c].linkGroupId = group
                    found += 1
                }
            }
        }
        return found == 2 ? 1 : 0
    }
}

private func linkGroup(of clipId: String, in timeline: Timeline) -> String? {
    timeline.tracks.flatMap(\.clips).first { $0.id == clipId }?.linkGroupId
}

/// Removes the clip with `clipId` from whichever track holds it. Returns 1 if
/// removed, 0 if no such clip exists.
@_cdecl("palmier_timeline_remove_clip")
public func palmierTimelineRemoveClip(_ handle: UnsafeMutableRawPointer?,
                                      _ clipId: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = projectContext(handle), let clipId else { return 0 }
    let id = String(cString: clipId)
    return ctx.withTimeline { timeline in
        // A linked pair goes together: leaving the audio behind when the
        // picture is deleted plays sound from a clip that is no longer there.
        let group = timeline.tracks.flatMap(\.clips).first { $0.id == id }?.linkGroupId
        var removed = false
        for trackIndex in timeline.tracks.indices {
            let before = timeline.tracks[trackIndex].clips.count
            timeline.tracks[trackIndex].clips.removeAll { clip in
                clip.id == id || (group != nil && clip.linkGroupId == group)
            }
            removed = removed || timeline.tracks[trackIndex].clips.count != before
        }
        return removed ? 1 : 0
    }
}

/// Ripple-deletes clips: removes them (with their link groups, like a plain
/// delete) and closes the holes by shifting each affected track's later clips
/// left, via the same RippleEngine upstream uses. Tracks that lost nothing do
/// not move — matching upstream's default for non-sync-locked tracks.
/// `clipIds` is a NUL-separated, double-NUL-terminated list so one call is one
/// atomic intent across a multi-selection. Returns the number removed.
@_cdecl("palmier_timeline_ripple_delete")
public func palmierTimelineRippleDelete(_ handle: UnsafeMutableRawPointer?,
                                        _ clipIds: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = projectContext(handle), let clipIds else { return 0 }
    var requested = Set<String>()
    var cursor = clipIds
    while cursor.pointee != 0 {
        let id = String(cString: cursor)
        requested.insert(id)
        cursor = cursor.advanced(by: id.utf8.count + 1)
    }
    guard !requested.isEmpty else { return 0 }

    return ctx.withTimeline { timeline in
        // Expand to link groups first: the audio goes with its picture, and
        // its track then ripples too because it lost a clip of its own.
        let groups = Set(timeline.tracks.flatMap(\.clips)
            .filter { requested.contains($0.id) }
            .compactMap(\.linkGroupId))
        var removedIds = Set<String>()
        for clip in timeline.tracks.flatMap(\.clips)
        where requested.contains(clip.id) || clip.linkGroupId.map(groups.contains) == true {
            removedIds.insert(clip.id)
        }
        guard !removedIds.isEmpty else { return 0 }

        for trackIndex in timeline.tracks.indices {
            guard timeline.tracks[trackIndex].clips.contains(where: { removedIds.contains($0.id) })
            else { continue }
            let shifts = RippleEngine.computeRippleShifts(
                clips: timeline.tracks[trackIndex].clips, removedIds: removedIds)
            timeline.tracks[trackIndex].clips.removeAll { removedIds.contains($0.id) }
            for shift in shifts {
                guard let i = timeline.tracks[trackIndex].clips.firstIndex(where: { $0.id == shift.clipId })
                else { continue }
                timeline.tracks[trackIndex].clips[i].startFrame = shift.newStartFrame
            }
        }
        return Int32(removedIds.count)
    }
}

/// Closes the empty span `[gapStart, gapEnd)` on `trackId` by shifting every
/// clip at or after `gapEnd` left by the gap's length. Clips linked to a
/// shifted clip shift with it on their own tracks, so a video+audio pair
/// cannot be pushed out of sync by closing a gap under one of them.
/// Refuses (0) when any shifted clip would land on one that is not moving —
/// a silent partial close would misreport what happened.
@_cdecl("palmier_timeline_close_gap")
public func palmierTimelineCloseGap(_ handle: UnsafeMutableRawPointer?,
                                    _ trackId: UnsafePointer<CChar>?,
                                    _ gapStart: Int32, _ gapEnd: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let trackId, gapEnd > gapStart, gapStart >= 0 else { return 0 }
    let track = String(cString: trackId)
    let length = Int(gapEnd - gapStart)

    return ctx.withTimeline { timeline in
        guard let anchor = timeline.tracks.first(where: { $0.id == track }) else { return 0 }
        // The span must actually be empty on the anchor track.
        guard !anchor.clips.contains(where: { $0.startFrame < Int(gapEnd) && $0.endFrame > Int(gapStart) })
        else { return 0 }

        var moving = Set(anchor.clips.filter { $0.startFrame >= Int(gapEnd) }.map(\.id))
        guard !moving.isEmpty else { return 0 }
        let groups = Set(timeline.tracks.flatMap(\.clips)
            .filter { moving.contains($0.id) }
            .compactMap(\.linkGroupId))
        for clip in timeline.tracks.flatMap(\.clips)
        where clip.linkGroupId.map(groups.contains) == true {
            moving.insert(clip.id)
        }

        // Validate before mutating: every mover's new span must be clear of
        // every non-mover on its track.
        for t in timeline.tracks {
            for clip in t.clips where moving.contains(clip.id) {
                let newStart = clip.startFrame - length
                guard newStart >= 0 else { return 0 }
                for other in t.clips
                where !moving.contains(other.id)
                    && other.startFrame < newStart + clip.durationFrames
                    && other.endFrame > newStart {
                    _ = other
                    return 0
                }
            }
        }
        for trackIndex in timeline.tracks.indices {
            for clipIndex in timeline.tracks[trackIndex].clips.indices
            where moving.contains(timeline.tracks[trackIndex].clips[clipIndex].id) {
                timeline.tracks[trackIndex].clips[clipIndex].startFrame -= length
            }
        }
        return 1
    }
}

/// Upserts one effect on a clip: `paramsJson` is a flat JSON object of
/// numbers and strings (e.g. {"clarity":0.4,"path":"C:/x.cube"}). An existing
/// effect of the same type keeps its place in the stack and is replaced;
/// otherwise the effect appends. Empty params remove the effect — a stack
/// entry with nothing set renders as a no-op and only confuses the list.
/// Effect types are the renderer's stable machine names ("detail.clarity",
/// "color.lut", …); unknown types are stored untouched so a newer renderer
/// can pick them up. Returns 1 on success.
@_cdecl("palmier_clip_set_effect")
public func palmierClipSetEffect(_ handle: UnsafeMutableRawPointer?,
                                 _ clipId: UnsafePointer<CChar>?,
                                 _ effectType: UnsafePointer<CChar>?,
                                 _ paramsJson: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = projectContext(handle), let clipId, let effectType else { return 0 }
    let id = String(cString: clipId)
    let type = String(cString: effectType)

    var params: [String: EffectParam] = [:]
    if let paramsJson {
        guard let data = String(cString: paramsJson).data(using: .utf8),
              let raw = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return 0 }
        for (key, value) in raw {
            if let number = value as? NSNumber {
                // JSON booleans arrive as char-typed NSNumbers; `is Bool` is
                // no test here because plain numbers can bridge to Bool too.
                guard String(cString: number.objCType) != "c",
                      number.doubleValue.isFinite else { return 0 }
                params[key] = EffectParam(value: number.doubleValue)
            } else if let text = value as? String {
                params[key] = EffectParam(string: text)
            } else {
                return 0   // malformed request: refuse rather than store junk
            }
        }
    }

    return ctx.withTimeline { timeline in
        for trackIndex in timeline.tracks.indices {
            guard let clipIndex = timeline.tracks[trackIndex].clips.firstIndex(where: { $0.id == id })
            else { continue }
            var effects = timeline.tracks[trackIndex].clips[clipIndex].effects ?? []
            let existing = effects.firstIndex { $0.type == type }
            if params.isEmpty {
                guard let existing else { return 0 }   // removing nothing is a no-op
                effects.remove(at: existing)
            } else if let existing {
                effects[existing].params = params
                effects[existing].enabled = true
            } else {
                effects.append(Effect(type: type, params: params))
            }
            timeline.tracks[trackIndex].clips[clipIndex].effects = effects.isEmpty ? nil : effects
            return 1
        }
        return 0
    }
}

/// Deletes the timeline range `[start, end)` across every track: clips fully
/// inside vanish, straddling clips are trimmed, clips spanning the whole range
/// are split around it. With `ripple` set, everything after the range pulls
/// left to close it — upstream's range delete. Fades touching a new cut edge
/// are zeroed, matching what a blade split does. Returns the number of clips
/// changed, 0 when the range touched nothing.
@_cdecl("palmier_timeline_delete_range")
public func palmierTimelineDeleteRange(_ handle: UnsafeMutableRawPointer?,
                                       _ start: Int32, _ end: Int32,
                                       _ ripple: Int32) -> Int32 {
    guard let ctx = projectContext(handle), end > start, start >= 0 else { return 0 }
    let s = Int(start), e = Int(end)

    return ctx.withTimeline { timeline in
        var touched = 0
        // Right halves of clips split around the range re-link per original
        // group, exactly like a blade split.
        var newGroupFor: [String: String] = [:]

        for trackIndex in timeline.tracks.indices {
            var rebuilt: [Clip] = []
            for clip in timeline.tracks[trackIndex].clips {
                if clip.startFrame >= e || clip.endFrame <= s {
                    rebuilt.append(clip)
                    continue
                }
                touched += 1
                let coversHead = clip.startFrame >= s
                let coversTail = clip.endFrame <= e
                if coversHead && coversTail { continue }   // fully inside: gone

                if !coversHead {
                    // Keep the part before the range.
                    var left = clip
                    left.setDuration(s - clip.startFrame)
                    left.fadeOutFrames = 0
                    rebuilt.append(left)
                }
                if !coversTail {
                    // Keep the part after the range, trimmed to start at `e`.
                    var right = clip
                    let delta = e - clip.startFrame
                    right.trimStartFrame += Int((Double(delta) * clip.speed).rounded())
                    right.startFrame = e
                    right.setDuration(clip.endFrame - e)
                    right.fadeInFrames = 0
                    if !coversHead {
                        // Both halves exist: the right one is a new clip.
                        right.id = UUID().uuidString
                        if let group = clip.linkGroupId {
                            if newGroupFor[group] == nil { newGroupFor[group] = UUID().uuidString }
                            right.linkGroupId = newGroupFor[group]
                        }
                    }
                    rebuilt.append(right)
                }
            }
            timeline.tracks[trackIndex].clips = rebuilt
        }
        guard touched > 0 else { return 0 }

        if ripple != 0 {
            let length = e - s
            for trackIndex in timeline.tracks.indices {
                for clipIndex in timeline.tracks[trackIndex].clips.indices
                where timeline.tracks[trackIndex].clips[clipIndex].startFrame >= e {
                    timeline.tracks[trackIndex].clips[clipIndex].startFrame -= length
                }
            }
        }
        return Int32(touched)
    }
}

/// Sets the playhead's authoritative frame in the project view state (used by
/// snapshots/persistence later; the live render clock stays in the shell).
@_cdecl("palmier_timeline_set_playhead")
public func palmierTimelineSetPlayhead(_ handle: UnsafeMutableRawPointer?, _ frame: Int32) -> Int32 {
    guard projectContext(handle) != nil, frame >= 0 else { return 0 }
    return 1
}

private func mutateClip(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                        _ mutate: (inout Clip) -> Bool) -> Int32 {
    guard let ctx = projectContext(handle), let clipId else { return 0 }
    let id = String(cString: clipId)
    return ctx.withTimeline { timeline in
        for trackIndex in timeline.tracks.indices {
            if let clipIndex = timeline.tracks[trackIndex].clips.firstIndex(where: { $0.id == id }) {
                return mutate(&timeline.tracks[trackIndex].clips[clipIndex]) ? 1 : 0
            }
        }
        return 0
    }
}

/// Sets the clip's normalized transform (center 0…1, size as canvas fraction,
/// rotation in degrees). Rejects non-finite values. Returns 1/0.
@_cdecl("palmier_clip_set_transform")
public func palmierClipSetTransform(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                                    _ centerX: Double, _ centerY: Double,
                                    _ width: Double, _ height: Double, _ rotation: Double) -> Int32 {
    guard centerX.isFinite, centerY.isFinite, width.isFinite, height.isFinite, rotation.isFinite,
          width > 0, height > 0 else { return 0 }
    return mutateClip(handle, clipId) { clip in
        clip.transform.centerX = centerX
        clip.transform.centerY = centerY
        clip.transform.width = width
        clip.transform.height = height
        clip.transform.rotation = rotation
        return true
    }
}

/// Sets the clip's opacity (0…1). Returns 1/0.
@_cdecl("palmier_clip_set_opacity")
public func palmierClipSetOpacity(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                                  _ opacity: Double) -> Int32 {
    guard opacity.isFinite, (0...1).contains(opacity) else { return 0 }
    return mutateClip(handle, clipId) { clip in
        clip.opacity = opacity
        return true
    }
}

/// Sets the clip's playback speed (0.01…100). Returns 1/0.
@_cdecl("palmier_clip_set_speed")
public func palmierClipSetSpeed(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                                _ speed: Double) -> Int32 {
    guard speed.isFinite, speed >= 0.01, speed <= 100 else { return 0 }
    return mutateClip(handle, clipId) { clip in
        clip.speed = speed
        return true
    }
}

/// Sets the clip's volume from decibels (-96…+12 dB, stored linear). Returns 1/0.
@_cdecl("palmier_clip_set_volume_db")
public func palmierClipSetVolumeDb(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                                   _ db: Double) -> Int32 {
    guard db.isFinite, db >= -96, db <= 12 else { return 0 }
    return mutateClip(handle, clipId) { clip in
        clip.volume = VolumeScale.linearFromDb(db)
        return true
    }
}

/// Splits the clip holding timeline `frame` in two at that frame (blade). The
/// left half keeps the clip id; the right half gets a fresh id written into
/// `idBuf`. Trims and speed are preserved so both halves play the same source
/// frames as before.
///
/// A linked clip takes its partner with it: both sides are cut at the same
/// frame and the right-hand halves are re-linked into a group of their own, so
/// each pair stays one-to-one and a later delete removes exactly one pair.
/// Returns 1 on success, 0 when no clip spans `frame` strictly inside its
/// range or arguments are invalid.
@_cdecl("palmier_timeline_split_clip")
public func palmierTimelineSplitClip(_ handle: UnsafeMutableRawPointer?, _ clipId: UnsafePointer<CChar>?,
                                     _ frame: Int32,
                                     _ idBuf: UnsafeMutablePointer<CChar>?, _ idBufSize: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let clipId, let idBuf else { return 0 }
    let id = String(cString: clipId)
    let splitFrame = Int(frame)
    return ctx.withTimeline { timeline in
        guard let target = timeline.tracks.flatMap(\.clips).first(where: { $0.id == id }),
              splitFrame > target.startFrame, splitFrame < target.endFrame else { return 0 }

        // Partners split too, but only where the cut actually falls inside
        // them — a partner that does not span the frame is left alone.
        let group = target.linkGroupId
        let rightGroup = group == nil ? nil : UUID().uuidString
        var newId: String?

        for trackIndex in timeline.tracks.indices {
            for clipIndex in timeline.tracks[trackIndex].clips.indices.reversed() {
                let original = timeline.tracks[trackIndex].clips[clipIndex]
                let partnered = group != nil && original.linkGroupId == group
                guard original.id == id || partnered else { continue }
                guard splitFrame > original.startFrame, splitFrame < original.endFrame else { continue }

                let leftFrames = splitFrame - original.startFrame
                var left = original
                left.setDuration(leftFrames)
                left.fadeOutFrames = 0

                var right = original
                right.id = UUID().uuidString
                right.startFrame = splitFrame
                right.trimStartFrame = original.trimStartFrame
                    + Int((Double(leftFrames) * original.speed).rounded())
                right.setDuration(original.durationFrames - leftFrames)
                right.fadeInFrames = 0
                right.linkGroupId = rightGroup

                if original.id == id { newId = right.id }
                timeline.tracks[trackIndex].clips[clipIndex] = left
                timeline.tracks[trackIndex].clips.insert(right, at: clipIndex + 1)
            }
        }

        guard let newId, writeCString(newId, into: idBuf, size: idBufSize) == 1 else { return 0 }
        return 1
    }
}

/// Replaces the whole timeline from a JSON snapshot previously produced by
/// palmier_timeline_json (undo/redo restore). Returns 1 on success, 0 when
/// the JSON does not decode.
@_cdecl("palmier_timeline_load_json")
public func palmierTimelineLoadJson(_ handle: UnsafeMutableRawPointer?,
                                    _ json: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = projectContext(handle), let json else { return 0 }
    let data = Data(bytes: json, count: strlen(json))
    guard let decoded = try? JSONDecoder().decode(Timeline.self, from: data) else { return 0 }
    return ctx.withTimeline { timeline in
        timeline = decoded
        return 1
    }
}

/// One clip in a copy payload: the clip plus the track it came from, so paste
/// can land it on the same track at a new time.
private struct CopiedClip: Codable {
    var trackId: String
    var clip: Clip
}

/// Serializes the given clips (NUL-separated, double-NUL-terminated ids) with
/// their track ids into buf as a clipboard payload. The payload is a value —
/// deleting the originals cannot invalidate it. Returns bytes written,
/// -(required size), or 0 when no id resolved.
@_cdecl("palmier_timeline_copy_clips")
public func palmierTimelineCopyClips(_ handle: UnsafeMutableRawPointer?,
                                     _ clipIds: UnsafePointer<CChar>?,
                                     _ buf: UnsafeMutablePointer<CChar>?, _ bufSize: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let clipIds else { return 0 }
    var requested = Set<String>()
    var cursor = clipIds
    while cursor.pointee != 0 {
        let id = String(cString: cursor)
        requested.insert(id)
        cursor = cursor.advanced(by: id.utf8.count + 1)
    }
    let timeline = ctx.snapshot()
    var copied: [CopiedClip] = []
    for track in timeline.tracks {
        for clip in track.clips where requested.contains(clip.id) {
            copied.append(CopiedClip(trackId: track.id, clip: clip))
        }
    }
    guard !copied.isEmpty else { return 0 }
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    guard let data = try? encoder.encode(copied) else { return 0 }
    let needed = data.count + 1
    guard let buf, Int(bufSize) >= needed else { return -Int32(needed) }
    data.withUnsafeBytes { raw in
        buf.withMemoryRebound(to: UInt8.self, capacity: needed) { dst in
            dst.update(from: raw.baseAddress!.assumingMemoryBound(to: UInt8.self), count: data.count)
            dst[data.count] = 0
        }
    }
    return Int32(data.count)
}

/// Pastes a copy payload with its earliest clip at `atFrame`, preserving the
/// clips' relative offsets and tracks. Every pasted clip gets a fresh id;
/// link groups are regenerated so pasted pairs stay linked to each other,
/// never to their originals. Atomic: if any clip's track is gone or its new
/// span would overlap an existing clip, nothing is pasted (0). Returns the
/// number of clips pasted.
@_cdecl("palmier_timeline_paste")
public func palmierTimelinePaste(_ handle: UnsafeMutableRawPointer?,
                                 _ payload: UnsafePointer<CChar>?, _ atFrame: Int32) -> Int32 {
    guard let ctx = projectContext(handle), let payload, atFrame >= 0 else { return 0 }
    let data = Data(bytes: payload, count: strlen(payload))
    guard let copied = try? JSONDecoder().decode([CopiedClip].self, from: data),
          !copied.isEmpty else { return 0 }
    guard let earliest = copied.map({ $0.clip.startFrame }).min() else { return 0 }
    let delta = Int(atFrame) - earliest

    return ctx.withTimeline { timeline in
        var groupMap: [String: String] = [:]
        var landed: [(trackIndex: Int, clip: Clip)] = []
        for entry in copied {
            guard let trackIndex = timeline.tracks.firstIndex(where: { $0.id == entry.trackId })
            else { return 0 }   // its track is gone: refuse the whole paste
            var clip = entry.clip
            clip.id = UUID().uuidString
            clip.startFrame += delta
            guard clip.startFrame >= 0 else { return 0 }
            if let group = clip.linkGroupId {
                if groupMap[group] == nil { groupMap[group] = UUID().uuidString }
                clip.linkGroupId = groupMap[group]
            }
            // Overlap check against existing clips and the already-planned
            // pastes on the same track.
            let blocked = timeline.tracks[trackIndex].clips + landed
                .filter { $0.trackIndex == trackIndex }.map(\.clip)
            for other in blocked
            where other.startFrame < clip.endFrame && other.endFrame > clip.startFrame {
                _ = other
                return 0
            }
            landed.append((trackIndex, clip))
        }
        for (trackIndex, clip) in landed {
            timeline.tracks[trackIndex].clips.append(clip)
            timeline.tracks[trackIndex].clips.sort { $0.startFrame < $1.startFrame }
        }
        return Int32(landed.count)
    }
}

/// Serializes the timeline as JSON into buf (NUL-terminated). Returns bytes
/// written (excluding NUL), -(required buffer size) when buf is null or too
/// small, or 0 on error.
@_cdecl("palmier_timeline_json")
public func palmierTimelineJson(_ handle: UnsafeMutableRawPointer?,
                                _ buf: UnsafeMutablePointer<CChar>?, _ bufSize: Int32) -> Int32 {
    guard let ctx = projectContext(handle) else { return 0 }
    let timeline = ctx.snapshot()
    // Sorted keys keep snapshots byte-stable so the shell's unchanged-state
    // check (no-op intents must not create undo entries) is reliable.
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    guard let data = try? encoder.encode(timeline) else { return 0 }
    if buf == nil || Int32(data.count) >= bufSize { return -Int32(data.count + 1) }
    data.withUnsafeBytes { raw in
        memcpy(buf!, raw.baseAddress!, data.count)
        buf!.advanced(by: data.count).pointee = 0
    }
    return Int32(data.count)
}
