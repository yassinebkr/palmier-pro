using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using PalmierShell.Core;

namespace PalmierShell.Views;

/// Embeds the Swift-rendered Vulkan surface. The native child HWND covers
/// this control exactly (airspace rule: nothing draws over it). The engine is
/// created after the first layout so the HWND never has a zero-sized surface.
public sealed class PreviewHost : NativeControlHost {
    IntPtr hwnd;
    PreviewInput? input;
    bool creating;

    public EngineSession? Session { get; private set; }

    /// Fired on the UI thread once the engine session exists.
    public event Action<EngineSession>? SessionReady;

    /// Mouse over the preview surface, once the native window exists.
    public event Action<PreviewInput>? InputReady;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent) {
        var handle = base.CreateNativeControlCore(parent);
        hwnd = handle.Handle;
        Dispatcher.UIThread.Post(TryCreateSession, DispatcherPriority.Loaded);
        return handle;
    }

    void TryCreateSession() {
        if (Session != null || creating || hwnd == IntPtr.Zero) return;
        if (Bounds.Width < 1 || Bounds.Height < 1) {
            Dispatcher.UIThread.Post(TryCreateSession, DispatcherPriority.Background);
            return;
        }
        // Device + swapchain creation runs to seconds on a cold GPU driver.
        // Off the UI thread: while it ran there right after launch, every
        // first interaction — opening the composer included — queued behind it.
        creating = true;
        IntPtr surface = hwnd;
        _ = Task.Run(() => {
            EngineSession? session = null;
            try {
                // Creation can still be in flight against the HWND while the
                // window closes at exit: the attach is guarded (hwnd == Zero),
                // and the residual risk is a driver-level fault at exit — accepted.
                session = new EngineSession(surface);
            } catch (Exception ex) {
                // No Vulkan-capable GPU or driver: the preview stays dark, the
                // rest of the editor keeps working, and the window says why.
                Console.Error.WriteLine($"preview: engine unavailable: {ex.Message}");
            }
            // Post never throws on dispatcher shutdown — the operation aborts
            // silently — so at process exit the attach may never run and the
            // session is reclaimed by the OS.
            Dispatcher.UIThread.Post(() => AttachSession(session));
        });
    }

    /// Back on the UI thread with the created session. A close that raced the
    /// creation disposes it instead of attaching — a live engine must never
    /// outlive the surface it renders into.
    void AttachSession(EngineSession? session) {
        creating = false;
        if (hwnd == IntPtr.Zero) {
            session?.Dispose();
            return;
        }
        if (session is null) {
            SessionFailed?.Invoke();
            return;
        }
        Session = session;
        Session.Start();
        SessionReady?.Invoke(Session);

        input = new PreviewInput(hwnd);
        InputReady?.Invoke(input);
    }

    /// Fired on the UI thread when the engine cannot start (no Vulkan device).
    public event Action? SessionFailed;

    protected override void DestroyNativeControlCore(IPlatformHandle control) {
        input?.Dispose();
        input = null;
        Session?.Dispose();
        Session = null;
        hwnd = IntPtr.Zero;
        base.DestroyNativeControlCore(control);
    }
}
