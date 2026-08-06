import Foundation
import WinSDK

/// Engine diagnostics: appended to engine-<date>.log next to the shell's own
/// logs, and mirrored to stderr when one exists. The log file is the durable
/// destination — a GUI launch (shortcut, explorer) has no stderr at all, and
/// writing FileHandle.standardError then is not a no-op but a fatal Swift
/// assertion that took the whole process down.
private enum EngineLogDestination {
    static let fileHandle: FileHandle? = {
        guard let appData = ProcessInfo.processInfo.environment["APPDATA"] else { return nil }
        let dir = URL(fileURLWithPath: appData)
            .appendingPathComponent("PalmierPro", isDirectory: true)
            .appendingPathComponent("logs", isDirectory: true)
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyyMMdd"
        let url = dir.appendingPathComponent("engine-\(formatter.string(from: Date())).log")
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            if !FileManager.default.fileExists(atPath: url.path) {
                FileManager.default.createFile(atPath: url.path, contents: nil)
            }
            let handle = try FileHandle(forWritingTo: url)
            _ = handle.seekToEndOfFile()
            return handle
        } catch {
            return nil
        }
    }()

    static let stderr: FileHandle? = {
        guard let raw = GetStdHandle(STD_ERROR_HANDLE),
              raw != INVALID_HANDLE_VALUE else { return nil }
        return FileHandle.standardError
    }()

    static let lock = NSLock()
}

/// See EngineLogDestination: never `print` (buffered, lost on a hard exit),
/// never an unchecked standardError write (fatal without a console).
public func engineLog(_ message: String) {
    let line = Data((message + "\n").utf8)
    EngineLogDestination.lock.lock()
    EngineLogDestination.fileHandle?.write(line)
    EngineLogDestination.stderr?.write(line)
    EngineLogDestination.lock.unlock()
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
