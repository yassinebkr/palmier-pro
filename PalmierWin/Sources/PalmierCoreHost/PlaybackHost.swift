import CVulkan
import Foundation
import PalmierCore
import PalmierWin

// Timeline playback through the engine: with a project attached,
// palmier_engine_render_frame composites the frame under the playhead via
// WinFrameRenderer (decode → composite → blit → present).

/// Attaches a project to the engine so render_frame plays its timeline.
/// Passing NULL detaches (render_frame falls back to the diagnostic clear).
/// Returns 1 on success, 0 on invalid handles.
@_cdecl("palmier_engine_set_project")
public func palmierEngineSetProject(_ engineHandle: UnsafeMutableRawPointer?,
                                    _ projectHandle: UnsafeMutableRawPointer?) -> Int32 {
    guard let engineHandle else { return 0 }
    let ctx = Unmanaged<EngineContext>.fromOpaque(engineHandle).takeUnretainedValue()
    // Handed to the render thread rather than applied here: tearing down its
    // presenter from the shell's thread is a use-after-free waiting to happen.
    ctx.post(project: projectHandle.map {
        Unmanaged<ProjectContext>.fromOpaque($0).takeUnretainedValue()
    })
    return 1
}

/// Marks a clip as selected so the preview draws its manipulation frame.
/// Passing NULL or an unknown id clears it. Always returns 1 for a live engine
/// — a selection that no longer resolves simply draws nothing.
@_cdecl("palmier_engine_set_selection")
public func palmierEngineSetSelection(_ engineHandle: UnsafeMutableRawPointer?,
                                      _ clipId: UnsafePointer<CChar>?) -> Int32 {
    guard let engineHandle else { return 0 }
    let ctx = Unmanaged<EngineContext>.fromOpaque(engineHandle).takeUnretainedValue()
    ctx.post(selection: clipId.map { String(cString: $0) })
    return 1
}

/// Side of a selection handle, in presented pixels. The shell hit-tests the
/// handles the engine draws, so both have to agree on how big they are.
@_cdecl("palmier_selection_handle_size")
public func palmierSelectionHandleSize() -> Double { SelectionOverlay.handlePixels }

/// Distance from the top edge to the rotate knob, in presented pixels.
@_cdecl("palmier_selection_rotate_offset")
public func palmierSelectionRotateOffset() -> Double { SelectionOverlay.rotateOffsetPixels }

/// The selected clip's transform at `frame`, or nil when nothing is selected,
/// the clip is gone, or the playhead is outside it. Uses the same accessor the
/// compositor uses, so the frame lands exactly on the drawn image.
private func selectionTransform(_ ctx: EngineContext, in timeline: Timeline, frame: Int) -> Transform? {
    guard let id = ctx.selectedClipId else { return nil }
    for track in timeline.tracks where !track.hidden {
        guard let clip = track.clips.first(where: { $0.id == id }) else { continue }
        guard frame >= clip.startFrame, frame < clip.endFrame else { return nil }
        return clip.transformAt(frame: frame)
    }
    return nil
}

/// Renders the timeline frame under `frame`. Rebuilds the presenter (plan +
/// decoders) whenever the project generation changed since the last frame,
/// and recreates the swapchain when the window was resized or a present
/// reported the swapchain stale.
func renderProjectFrame(_ ctx: EngineContext, frame: Int) -> Int32 {
    let started = Date()
    defer { PreviewStats.shared.endFrame(seconds: -started.timeIntervalSinceNow) }
    guard let project = ctx.project, let device = ctx.device else { return 0 }

    // Window resized, a present failed, or there is no swapchain yet: rebuild
    // for the current client size. A rebuild the driver refuses is retried
    // when the target size changes rather than every frame — the extent the
    // driver settles on need not equal the client rect, and hammering
    // vkDeviceWaitIdle at a size already rejected costs more than the stale
    // image it is trying to replace.
    let clientSize = ctx.window.clientSize
    let drawable = clientSize.width > 0 && clientSize.height > 0
    let mismatched = ctx.swapchain.map {
        $0.extent.width != UInt32(clientSize.width) || $0.extent.height != UInt32(clientSize.height)
    } ?? true
    let alreadyTried = ctx.lastSwapchainAttempt.map { $0 == clientSize } ?? false
    if drawable && mismatched && !alreadyTried {
        ctx.lastSwapchainAttempt = clientSize
        recreateSwapchain(ctx, device: device)
    }
    // No drawable surface yet (minimised, or mid-resize). Not a failure.
    guard let swapchain = ctx.swapchain else { return 1 }

    let (timeline, generation) = project.renderSnapshotWithGeneration()

    if generation != ctx.presenterGeneration {
        if let next = makePresenter(ctx, device: device, swapchain: swapchain, timeline: timeline,
                                    renderSize: project.renderSize) {
            // The outgoing presenter owns the offscreen image the last blit may
            // still be reading, so it cannot be released until the GPU is done.
            if ctx.presenter != nil { vkDeviceWaitIdle(device.device) }
            ctx.presenter = next
            ctx.presenterEmpty = false
            ctx.presenterGeneration = generation
            ctx.plannedGeneration = nil
        } else if timeline.tracks.contains(where: { $0.type == .video && !$0.clips.isEmpty }) {
            // Video that would not plan — offline media, or a file still being
            // written. Retry when the timeline changes again rather than
            // reopening every decoder from the render thread on every frame,
            // which is a permanent stall dressed up as a retry.
            if ctx.plannedGeneration != generation {
                ctx.plannedGeneration = generation
                engineLog("[plan] no video could be planned at revision \(generation)")
            }
            ctx.presenterEmpty = true
            ctx.presenterGeneration = generation
        } else {
            // Genuinely nothing to draw. The old presenter is kept only so its
            // swapchain path can clear the screen.
            ctx.presenterEmpty = true
            ctx.presenterGeneration = generation
        }
    }
    guard let presenter = ctx.presenter else { return 1 }
    if ctx.presenterEmpty {
        // Present black rather than leaving the last frame up: a preview
        // frozen on a clip the user just deleted reads as a dead engine.
        if !presenter.presentCleared() { retryAfterSwapchainLoss(ctx, device: device) { $0.presentCleared() } }
        return 1
    }
    presenter.selection = selectionTransform(ctx, in: timeline, frame: frame)
    if !presenter.drawTimelineFrame(frame: frame) {
        retryAfterSwapchainLoss(ctx, device: device) { $0.drawTimelineFrame(frame: frame) }
    }
    return 1
}

/// A present reported the swapchain unusable: rebuild it and run `draw` once
/// more. Both present paths need this — a discarded failure leaves the acquire
/// semaphore signalled with nothing to consume it, and every later frame fails
/// the same way.
private func retryAfterSwapchainLoss(_ ctx: EngineContext, device: VulkanDevice,
                                     _ draw: (WinPlayback) -> Bool) {
    // The size did not change, so clear the attempt record or the guard in
    // renderProjectFrame would suppress this rebuild.
    ctx.lastSwapchainAttempt = nil
    recreateSwapchain(ctx, device: device)
    guard let retrySwap = ctx.swapchain, let presenter = ctx.presenter else { return }
    presenter.replaceSwapchain(retrySwap)
    _ = draw(presenter)
}

/// Recreates the swapchain for the current window size, retiring the previous
/// one against the window's shared surface and pointing the live presenter at
/// the result. Decoders and the offscreen composite survive.
///
/// The old swapchain is held until the new one exists, so a refused rebuild
/// leaves a working preview rather than nothing to draw into.
private func recreateSwapchain(_ ctx: EngineContext, device: VulkanDevice) {
    guard let surface = ctx.surface else { return }
    PreviewStats.shared.recordSwapchainRebuild()
    vkDeviceWaitIdle(device.device)
    let retiring = ctx.swapchain
    guard let next = VulkanSwapchain(device: device, instance: ctx.instance, window: ctx.window,
                                     surface: surface, oldSwapchain: retiring?.swapchain) else {
        engineLog("[engine] swapchain not created for \(ctx.window.clientSize); keeping the previous one")
        return
    }
    ctx.swapchain = next
    ctx.presenter?.replaceSwapchain(next)
    // `retiring` releases here, after the replacement exists and the presenter
    // has stopped referring to it.
}

/// One slot per video clip (keyed by clip id), natural sizes probed once per
/// media path. Shared by the playback presenter and the exporter so both
/// plan the timeline identically. Offline media is skipped (the rest of the
/// timeline still renders).
func buildVideoSlots(timeline: Timeline, natCache: inout [String: Size2D])
    -> (slots: [String: TrackSlot], mediaPaths: [TrackID: String], clipIds: [TrackID: String]) {
    var trackSlots: [String: TrackSlot] = [:]
    var mediaPaths: [TrackID: String] = [:]
    var clipIds: [TrackID: String] = [:]
    var nextTrackID: Int32 = 1

    for track in timeline.tracks where track.type == .video {
        for clip in track.clips where clip.mediaType == .video {
            let natSize: Size2D
            if let cached = natCache[clip.mediaRef] {
                natSize = cached
            } else if let decoder = try? FFmpegDecoder(path: clip.mediaRef) {
                natSize = Size2D(width: Double(decoder.info.width), height: Double(decoder.info.height))
                natCache[clip.mediaRef] = natSize
            } else {
                engineLog("[plan] no decoder for \(clip.mediaRef); clip skipped")
                continue
            }
            let trackID = TrackID(rawValue: nextTrackID)
            nextTrackID += 1
            trackSlots[clip.id] = TrackSlot(trackID: trackID, natSize: natSize, transform: .identity)
            mediaPaths[trackID] = clip.mediaRef
            clipIds[trackID] = clip.id
        }
    }
    return (trackSlots, mediaPaths, clipIds)
}

/// Builds a WinPlayback presenter for the current timeline snapshot.
private func makePresenter(_ ctx: EngineContext, device: VulkanDevice,
                           swapchain: VulkanSwapchain, timeline: Timeline,
                           renderSize: (width: Int, height: Int)) -> WinPlayback? {
    let size = Size2D(width: Double(renderSize.width), height: Double(renderSize.height))
    let (trackSlots, mediaPaths, clipIds) = buildVideoSlots(timeline: timeline, natCache: &ctx.natSizeCache)
    guard !trackSlots.isEmpty else { return nil }

    return WinPlayback(
        device: device, swapchain: swapchain, timeline: timeline,
        renderSize: size, trackSlots: trackSlots, mediaPaths: mediaPaths,
        clipIds: clipIds, caches: ctx.decodeCaches
    )
}
