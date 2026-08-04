import CVulkan
import Foundation
import PalmierCore
import PalmierWin

// Export ABI: renders the whole timeline offscreen through the same
// WinFrameRenderer path as playback and encodes it to H.264/MP4. Runs on a
// background thread with its own headless Vulkan device; the shell polls
// progress. Cancellation is cooperative, checked once per frame: a cancelled
// export stops mid-run and its partial output file is deleted (an MP4 cut
// before the trailer is unplayable, so a truncated file would only pretend
// to be a finished export).

/// Retained export state behind the opaque handle.
final class ExportContext: @unchecked Sendable {
    let lock = NSLock()
    var framesDone = 0
    var totalFrames = 1
    var finished = false
    var cancelRequested = false
    var cancelled = false
    var errorMessage: String?

    func update(done: Int, total: Int) {
        lock.lock()
        framesDone = done
        totalFrames = max(1, total)
        lock.unlock()
    }

    /// Returns false when the export already finished — a late cancel must not
    /// turn a completed job into a cancelled one.
    func requestCancel() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        if finished { return false }
        cancelRequested = true
        return true
    }

    var isCancelRequested: Bool {
        lock.lock()
        defer { lock.unlock() }
        return cancelRequested
    }

    func finish(error: String?) {
        lock.lock()
        finished = true
        errorMessage = error
        lock.unlock()
    }

    func finishCancelled() {
        lock.lock()
        finished = true
        cancelled = true
        lock.unlock()
    }
}

/// Starts exporting `project`'s timeline to `path` (H.264/MP4 at the
/// project's render size and the timeline's fps). Returns an export handle to
/// poll, or NULL when the timeline is empty or the encoder/device can't be
/// created.
@_cdecl("palmier_export_start")
public func palmierExportStart(_ projectHandle: UnsafeMutableRawPointer?,
                               _ path: UnsafePointer<CChar>?) -> UnsafeMutableRawPointer? {
    guard let projectHandle, let outputPath = nonEmptyPath(path) else { return nil }
    let project = Unmanaged<ProjectContext>.fromOpaque(projectHandle).takeUnretainedValue()
    let timeline = project.snapshot()
    guard timeline.totalFrames > 0 else { return nil }

    let ctx = ExportContext()
    ctx.totalFrames = timeline.totalFrames
    let renderSize = project.renderSize

    let thread = Thread {
        runExport(ctx, timeline: timeline, renderSize: renderSize, outputPath: outputPath)
    }
    thread.name = "palmier-export"
    thread.start()
    let handle = Unmanaged.passRetained(ctx).toOpaque()
    HandleRegistry.shared.register(handle)
    return handle
}

/// Progress: 0–100 while running, 101 when finished successfully, -1 on
/// failure (read the message via palmier_export_error), -2 when cancelled.
@_cdecl("palmier_export_status")
public func palmierExportStatus(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    guard let handle else { return -1 }
    let ctx = Unmanaged<ExportContext>.fromOpaque(handle).takeUnretainedValue()
    ctx.lock.lock()
    defer { ctx.lock.unlock() }
    if ctx.finished {
        if ctx.cancelled { return -2 }
        return ctx.errorMessage == nil ? 101 : -1
    }
    return Int32(min(100, ctx.framesDone * 100 / ctx.totalFrames))
}

/// Requests cooperative cancellation of a running export. Returns 1 when the
/// request was accepted, 0 when the export had already finished.
@_cdecl("palmier_export_cancel")
public func palmierExportCancel(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    guard let handle else { return 0 }
    let ctx = Unmanaged<ExportContext>.fromOpaque(handle).takeUnretainedValue()
    return ctx.requestCancel() ? 1 : 0
}

/// Writes the failure message (NUL-terminated UTF-8) into buf. Returns 1
/// when a message was written, 0 otherwise.
@_cdecl("palmier_export_error")
public func palmierExportError(_ handle: UnsafeMutableRawPointer?,
                               _ buf: UnsafeMutablePointer<CChar>?, _ bufSize: Int32) -> Int32 {
    guard let handle, let buf, bufSize > 1 else { return 0 }
    let ctx = Unmanaged<ExportContext>.fromOpaque(handle).takeUnretainedValue()
    ctx.lock.lock()
    let message = ctx.errorMessage
    ctx.lock.unlock()
    guard let message else { return 0 }
    let utf8 = Array(message.utf8.prefix(Int(bufSize) - 1))
    buf.withMemoryRebound(to: UInt8.self, capacity: utf8.count + 1) { dst in
        utf8.withUnsafeBufferPointer { src in
            dst.update(from: src.baseAddress!, count: utf8.count)
        }
        dst[utf8.count] = 0
    }
    return 1
}

@_cdecl("palmier_export_destroy")
public func palmierExportDestroy(_ handle: UnsafeMutableRawPointer?) {
    guard let handle, HandleRegistry.shared.unregister(handle) else { return }
    Unmanaged<ExportContext>.fromOpaque(handle).release()
}

private func runExport(_ ctx: ExportContext, timeline: Timeline,
                       renderSize: (width: Int, height: Int), outputPath: String) {
    // Headless Vulkan: offscreen rendering needs no surface extensions.
    guard let instance = Vulkan.createInstance(appName: "palmier-export", extensions: []) else {
        ctx.finish(error: "Could not create the GPU instance for export.")
        return
    }
    // The device, exporter, and every pool they own must be released before
    // the instance goes away — an inner scope guarantees that ordering, which
    // a `defer` here would not.
    render(ctx, instance: instance, timeline: timeline, renderSize: renderSize, outputPath: outputPath)
    Vulkan.destroyInstance(instance)
}

private func render(_ ctx: ExportContext, instance: VkInstance,
                    timeline: Timeline, renderSize: (width: Int, height: Int),
                    outputPath: String) {
    guard let device = VulkanDevice.create(instance: instance) else {
        ctx.finish(error: "Could not create the GPU device for export.")
        return
    }

    let size = Size2D(width: Double(renderSize.width), height: Double(renderSize.height))
    var natCache: [String: Size2D] = [:]
    let (trackSlots, mediaPaths, _) = buildVideoSlots(timeline: timeline, natCache: &natCache)
    guard !trackSlots.isEmpty else {
        ctx.finish(error: "Nothing to export — the timeline has no renderable video clips.")
        return
    }

    let config = FFmpegEncoder.Config(width: renderSize.width, height: renderSize.height,
                                      fps: timeline.fps)
    guard let exporter = WinExporter(
        device: device, timeline: timeline, renderSize: size,
        trackSlots: trackSlots, mediaPaths: mediaPaths,
        outputPath: outputPath, encoderConfig: config
    ) else {
        ctx.finish(error: "Could not open the encoder for \(outputPath).")
        return
    }
    exporter.onFrame = { done, total in ctx.update(done: done, total: total) }
    exporter.shouldCancel = { ctx.isCancelRequested }

    do {
        let frames = try exporter.export()
        vkDeviceWaitIdle(device.device)
        ctx.update(done: frames, total: max(1, frames))
        ctx.finish(error: nil)
    } catch is CancellationError {
        // Close releases the file handle so the partial output can be deleted.
        try? exporter.encoder.close()
        try? FileManager.default.removeItem(atPath: outputPath)
        ctx.finishCancelled()
    } catch {
        ctx.finish(error: "Export failed: \(error.localizedDescription)")
    }
}
