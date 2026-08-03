import Foundation

/// Engine diagnostics go to stderr, never `print`. Swift's stdout is fully
/// buffered when it is a pipe, so every diagnostic written with `print` is lost
/// unless the process exits cleanly — which a hung or force-killed preview
/// never does. stderr is unbuffered, so these survive the case they exist for.
public func engineLog(_ message: String) {
    FileHandle.standardError.write(Data((message + "\n").utf8))
}

/// Per-second preview health counters, printed only when
/// `PALMIER_PREVIEW_STATS=1`. A preview that has stopped updating looks the
/// same from outside whatever the cause — a starved render loop, a swapchain
/// rebuilt every frame, or a decode walk blocking the thread all present as a
/// still picture. These separate them without a debugger attached.
public final class PreviewStats: @unchecked Sendable {
    public static let shared = PreviewStats()
    public static let enabled = ProcessInfo.processInfo.environment["PALMIER_PREVIEW_STATS"] == "1"

    private let lock = NSLock()
    private var frames = 0
    private var stalls = 0
    private var rebuilds = 0
    private var decodedFrames = 0
    private var seeks = 0
    private var decodeSeconds = 0.0
    private var frameSeconds = 0.0
    private var worstFrameSeconds = 0.0
    private var windowStart = Date()

    /// Records one completed render-thread frame and prints a line once a
    /// second. Call from the render thread only.
    public func endFrame(seconds: Double) {
        guard Self.enabled else { return }
        lock.lock()
        frames += 1
        frameSeconds += seconds
        worstFrameSeconds = max(worstFrameSeconds, seconds)
        let elapsed = -windowStart.timeIntervalSinceNow
        guard elapsed >= 1 else { lock.unlock(); return }
        let line = String(
            format: "[preview] %.1f fps  frame avg %.1f ms worst %.0f ms  decode %.0f ms " +
                    "(%d frames, %d seeks)  stalls %d  swapchain rebuilds %d",
            Double(frames) / elapsed,
            frameSeconds / Double(max(1, frames)) * 1000,
            worstFrameSeconds * 1000,
            decodeSeconds * 1000,
            decodedFrames, seeks, stalls, rebuilds)
        frames = 0; stalls = 0; rebuilds = 0; decodedFrames = 0; seeks = 0
        decodeSeconds = 0; frameSeconds = 0; worstFrameSeconds = 0
        windowStart = Date()
        lock.unlock()
        engineLog(line)
    }

    public func recordDecode(seconds: Double, frames: Int, seeks seekCount: Int) {
        guard Self.enabled else { return }
        lock.lock()
        decodeSeconds += seconds
        decodedFrames += frames
        seeks += seekCount
        lock.unlock()
    }

    public func recordStall() {
        guard Self.enabled else { return }
        lock.lock(); stalls += 1; lock.unlock()
    }

    public func recordSwapchainRebuild() {
        guard Self.enabled else { return }
        lock.lock(); rebuilds += 1; lock.unlock()
    }
}
