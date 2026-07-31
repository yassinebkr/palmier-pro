import CVulkan
import WinSDK

/// A minimal Win32 top-level window for Vulkan presentation. Flat-C Win32
/// (CreateWindowExW / RegisterClassExW / DispatchMessageW) — no COM, exposed
/// directly by the toolchain's WinSDK module. The HWND backs a
/// `VK_KHR_win32_surface` that the swapchain presents to.
public final class Win32Window: @unchecked Sendable {
    public let hwnd: HWND
    public let instance: HINSTANCE

    /// False when wrapping a foreign HWND (owned by the .NET shell) — the
    /// wrapper must never register a class for it or destroy it.
    private let ownsWindow: Bool

    // Class-name buffer kept alive for the window's lifetime (WNDCLASSEXW and
    // CreateWindowExW retain the LPCWSTR pointer).
    private let className: [WCHAR]

    /// Wraps an existing HWND owned by someone else (the .NET shell). No class
    /// registration, no DestroyWindow — the surface backs onto their window.
    public init(foreignHwnd: HWND) {
        self.hwnd = foreignHwnd
        // The owning process's module handle — used only for surface creation.
        self.instance = GetModuleHandleW(nil) ?? HINSTANCE(bitPattern: 1)!
        self.ownsWindow = false
        self.className = [0]
    }

    /// Creates a top-level window of `size` (client area, in pixels) with `title`.
    /// The window is hidden until `show()`; pump messages via `pollEvents()`.
    public init?(title: String, width: Int, height: Int) {
        guard let hinst = GetModuleHandleW(nil) else { return nil }
        self.instance = hinst
        self.ownsWindow = true

        // Win32 W APIs take wide (UTF-16) strings; own the class-name buffer.
        let clsName = Win32Window.wide("PalmierWinWindow")
        self.className = clsName

        var wcex = WNDCLASSEXW()
        wcex.cbSize = UINT(MemoryLayout<WNDCLASSEXW>.size)
        wcex.lpfnWndProc = Win32Window.windowProc
        wcex.hInstance = hinst
        wcex.style = UInt32(CS_HREDRAW) | UInt32(CS_VREDRAW)
        wcex.hCursor = nil
        clsName.withUnsafeBufferPointer { buf in
            wcex.lpszClassName = buf.baseAddress
            RegisterClassExW(&wcex)
        }

        // Compute window size for the desired client rect.
        var rect = RECT()
        rect.right = LONG(width)
        rect.bottom = LONG(height)
        AdjustWindowRect(&rect, UInt32(WS_OVERLAPPEDWINDOW), false)

        let wideTitle = Win32Window.wide(title)
        let handle: HWND = wideTitle.withUnsafeBufferPointer { titleBuf in
            clsName.withUnsafeBufferPointer { classBuf in
                CreateWindowExW(
                    0,
                    classBuf.baseAddress,
                    titleBuf.baseAddress,
                    UInt32(WS_OVERLAPPEDWINDOW),
                    Int32(CW_USEDEFAULT), Int32(CW_USEDEFAULT),
                    Int32(rect.right - rect.left), Int32(rect.bottom - rect.top),
                    nil, nil, hinst, nil
                )
            }
        }
        // HWND is non-optional in the WinSDK projection; CreateWindowExW returns
        // a null pointer on failure, detectable via the raw bitPattern.
        guard Int(bitPattern: handle) != 0 else { return nil }
        self.hwnd = handle
    }

    deinit {
        if ownsWindow { DestroyWindow(hwnd) }
    }

    public func show() { ShowWindow(hwnd, Int32(SW_SHOW)) }

    /// Pumps pending window messages (non-blocking). Returns false if the user
    /// closed the window (WM_QUIT received) — the caller's render loop exits then.
    @discardableResult
    public func pollEvents() -> Bool {
        var msg = MSG()
        while PeekMessageW(&msg, nil, 0, 0, UInt32(PM_REMOVE)) {
            if msg.message == UInt32(WM_QUIT) { return false }
            TranslateMessage(&msg)
            DispatchMessageW(&msg)
        }
        return true
    }

    /// Default window procedure: handle close → post WM_QUIT, else DefWindowProcW.
    private static let windowProc: WNDPROC = { hwnd, msg, wParam, lParam in
        if msg == UInt32(WM_CLOSE) {
            PostQuitMessage(0)
            return 0
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam)
    }

    /// Swift String → null-terminated UTF-16 (WCHAR) array for the Win32 W APIs.
    private static func wide(_ s: String) -> [WCHAR] {
        Array(s.utf16) + [0]
    }
}
