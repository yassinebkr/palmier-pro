using System.Runtime.InteropServices;

namespace PalmierShell.Views;

/// Mouse input over the preview's native child window.
///
/// The preview is a Win32 HWND owned by Avalonia's NativeControlHost, so
/// pointer events never reach the Avalonia tree (the airspace rule). The
/// window is subclassed here to read the mouse directly, and positions are
/// reported in normalised 0…1 preview coordinates so the caller never has to
/// know about pixels or DPI.
public sealed class PreviewInput : IDisposable {
    /// A pointer position over the preview, with the modifiers held with it.
    public sealed record Point(double X, double Y, bool Shift, bool Alt, bool Control);

    /// A drag in progress: where it started, and where it is now.
    public sealed record Drag(double FromX, double FromY, double X, double Y,
                              bool Shift, bool Alt, bool Control);

    /// Button down. Raised before any Dragging, so the caller can decide what
    /// the gesture is (and what it selects) from the press alone.
    public event Action<Point>? Pressed;
    /// Pointer moved with no button down — for cursor and hover feedback.
    public event Action<Point>? Hovered;
    /// Raised on the UI thread while the button is down, then once on release.
    public event Action<Drag>? Dragging;
    public event Action<Drag>? Dropped;
    /// The drag was abandoned (capture lost, Escape); nothing was committed.
    public event Action? Cancelled;

    /// Which cursor the preview shows. Set from the caller's hit-test.
    public enum CursorShape { Arrow, Move, SizeNS, SizeWE, SizeNWSE, SizeNESW, Rotate }

    public CursorShape Cursor { get; set; } = CursorShape.Arrow;

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    const int GwlpWndProc = -4;
    const uint WmLButtonDown = 0x0201, WmLButtonUp = 0x0202, WmMouseMove = 0x0200;
    const uint WmCaptureChanged = 0x0215, WmSetCursor = 0x0020, WmKeyDown = 0x0100;
    const int VkEscape = 0x1B, VkShift = 0x10, VkMenu = 0x12, VkControl = 0x11;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", EntryPoint = "SetCursor")]
    static extern IntPtr SetCursorHandle(IntPtr cursor);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    readonly IntPtr hwnd;
    readonly IntPtr previous;
    readonly WndProc handler;   // held so the delegate is not collected
    bool dragging;
    double startX, startY;

    public PreviewInput(IntPtr hwnd) {
        this.hwnd = hwnd;
        handler = Handle;
        previous = SetWindowLongPtr(hwnd, GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(handler));
    }

    /// The preview's client size in pixels — the caller needs it to size hit
    /// targets in screen terms. Zero when the window has no area yet.
    public (double Width, double Height) SurfaceSize =>
        GetClientRect(hwnd, out var rect)
            ? (rect.Right - rect.Left, rect.Bottom - rect.Top)
            : (0, 0);

    IntPtr Handle(IntPtr window, uint msg, IntPtr wParam, IntPtr lParam) {
        // A managed exception thrown across this native callback tears the
        // process down, so every case stays inside the try.
        try {
            switch (msg) {
                case WmSetCursor:
                    SetCursorHandle(LoadCursor(IntPtr.Zero, CursorId(Cursor)));
                    return 1;   // handled: stop DefWindowProc resetting it

                case WmLButtonDown:
                    if (Normalise(lParam) is { } down) {
                        (startX, startY) = down;
                        dragging = true;
                        SetCapture(window);
                        Pressed?.Invoke(new Point(down.X, down.Y, ShiftDown, AltDown, ControlDown));
                    }
                    break;

                case WmMouseMove:
                    if (Normalise(lParam) is not { } move) break;
                    if (dragging) Dragging?.Invoke(MakeDrag(move));
                    else Hovered?.Invoke(new Point(move.X, move.Y, ShiftDown, AltDown, ControlDown));
                    break;

                case WmLButtonUp:
                    if (dragging) {
                        dragging = false;
                        ReleaseCapture();
                        if (Normalise(lParam) is { } up) Dropped?.Invoke(MakeDrag(up));
                    }
                    break;

                case WmKeyDown when (int)wParam == VkEscape && dragging:
                    dragging = false;
                    ReleaseCapture();
                    Cancelled?.Invoke();
                    break;

                case WmCaptureChanged:
                    if (dragging) {
                        dragging = false;   // capture stolen: abandon rather than jump
                        Cancelled?.Invoke();
                    }
                    break;
            }
        } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"preview input: {ex}");
        }
        return CallWindowProc(previous, window, msg, wParam, lParam);
    }

    static bool ShiftDown => (GetKeyState(VkShift) & 0x8000) != 0;
    static bool AltDown => (GetKeyState(VkMenu) & 0x8000) != 0;
    static bool ControlDown => (GetKeyState(VkControl) & 0x8000) != 0;

    static IntPtr CursorId(CursorShape shape) => shape switch {
        CursorShape.Move => 32646,      // IDC_SIZEALL
        CursorShape.SizeNS => 32645,    // IDC_SIZENS
        CursorShape.SizeWE => 32644,    // IDC_SIZEWE
        CursorShape.SizeNWSE => 32642,  // IDC_SIZENWSE
        CursorShape.SizeNESW => 32643,  // IDC_SIZENESW
        CursorShape.Rotate => 32515,    // IDC_CROSS — Windows ships no rotate cursor
        _ => 32512,                     // IDC_ARROW
    };

    Drag MakeDrag((double X, double Y) at) =>
        new(startX, startY, at.X, at.Y, ShiftDown, AltDown, ControlDown);

    /// Client pixels → 0…1 of the preview surface. Null when the window has
    /// no area yet.
    ///
    /// The coordinates are *signed* 16-bit halves of lParam and go negative as
    /// soon as a captured drag leaves the window to the left or above, so they
    /// have to be unpacked from the low 32 bits — `IntPtr.ToInt32` overflows on
    /// exactly those values.
    (double X, double Y)? Normalise(IntPtr lParam) {
        if (!GetClientRect(hwnd, out var rect)) return null;
        double width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width < 1 || height < 1) return null;
        uint packed = unchecked((uint)lParam.ToInt64());
        return ((short)(packed & 0xFFFF) / width, (short)(packed >> 16) / height);
    }

    public void Dispose() {
        if (previous != IntPtr.Zero) SetWindowLongPtr(hwnd, GwlpWndProc, previous);
    }
}
