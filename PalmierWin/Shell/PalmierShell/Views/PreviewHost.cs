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
        if (Session != null || hwnd == IntPtr.Zero) return;
        if (Bounds.Width < 1 || Bounds.Height < 1) {
            Dispatcher.UIThread.Post(TryCreateSession, DispatcherPriority.Background);
            return;
        }
        Session = new EngineSession(hwnd);
        Session.Start();
        SessionReady?.Invoke(Session);

        input = new PreviewInput(hwnd);
        InputReady?.Invoke(input);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control) {
        input?.Dispose();
        input = null;
        Session?.Dispose();
        Session = null;
        hwnd = IntPtr.Zero;
        base.DestroyNativeControlCore(control);
    }
}
