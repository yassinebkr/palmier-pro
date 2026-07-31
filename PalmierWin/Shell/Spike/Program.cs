using System.Runtime.InteropServices;

// Interop spike: part 1 calls PalmierCore functions; part 2 creates a Win32
// window in C#, hands its HWND to the Swift engine, and lets Swift render +
// present Vulkan frames into it for a few seconds.
int add = Native.palmier_add(2, 3);
int frames = Native.palmier_demo_total_frames();
int ripple = Native.palmier_demo_ripple_shift();
Console.WriteLine($"palmier_add(2, 3) = {add} (expect 5)");
Console.WriteLine($"palmier_demo_total_frames() = {frames} (expect 60)");
Console.WriteLine($"palmier_demo_ripple_shift() = {ripple} (expect 0)");
bool spike1 = add == 5 && frames == 60 && ripple == 0;
Console.WriteLine(spike1 ? "SPIKE-1 PASS" : "SPIKE-1 FAIL");
if (!spike1) return 1;

// --- Spike 2: Swift renders into our window ---
using var window = new Win32Window("Palmier Interop Spike", 960, 540);
window.Show();

IntPtr engine = Native.palmier_engine_create(window.Hwnd);
if (engine == IntPtr.Zero) {
    Console.WriteLine("SPIKE-2 FAIL: palmier_engine_create returned NULL");
    return 1;
}
Console.WriteLine("Engine created on C#-owned HWND; rendering 300 frames...");

int rendered = 0;
var sw = System.Diagnostics.Stopwatch.StartNew();
while (rendered < 300 && window.PumpMessages()) {
    if (Native.palmier_engine_render_frame(engine, rendered) == 0) {
        Console.WriteLine($"SPIKE-2 FAIL: render_frame failed at frame {rendered}");
        Native.palmier_engine_destroy(engine);
        return 1;
    }
    rendered++;
    Thread.Sleep(16);
}
Native.palmier_engine_destroy(engine);
Console.WriteLine($"Rendered + presented {rendered} frame(s) in {sw.ElapsedMilliseconds} ms");
Console.WriteLine(rendered == 300 ? "SPIKE-2 PASS" : "SPIKE-2 FAIL (window closed early)");
return rendered == 300 ? 0 : 1;

static partial class Native {
    const string Dll = "PalmierCoreHost.dll";

    [LibraryImport(Dll)] public static partial int palmier_add(int a, int b);
    [LibraryImport(Dll)] public static partial int palmier_demo_total_frames();
    [LibraryImport(Dll)] public static partial int palmier_demo_ripple_shift();

    [LibraryImport(Dll)] public static partial IntPtr palmier_engine_create(IntPtr hwnd);
    [LibraryImport(Dll)] public static partial int palmier_engine_render_frame(IntPtr engine, int frame);
    [LibraryImport(Dll)] public static partial void palmier_engine_destroy(IntPtr engine);
}

/// Minimal Win32 top-level window owned by the C# process.
sealed class Win32Window : IDisposable {
    public IntPtr Hwnd { get; }
    readonly Win32Interop.WndProcDelegate wndProc; // keep delegate alive

    public Win32Window(string title, int width, int height) {
        wndProc = WndProc;
        Hwnd = Win32Interop.CreateAppWindow(title, width, height, wndProc);
        if (Hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowExW failed");
    }

    public void Show() => Win32Interop.ShowWindow(Hwnd, Win32Interop.SW_SHOW);

    /// Non-blocking message pump. Returns false once WM_QUIT was posted.
    public bool PumpMessages() => Win32Interop.PumpMessages(Hwnd);

    IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) {
        if (msg == Win32Interop.WM_CLOSE) {
            Win32Interop.PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return Win32Interop.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose() => Win32Interop.DestroyWindow(Hwnd);
}

static partial class Win32Interop {
    public const int SW_SHOW = 5;
    public const uint WM_CLOSE = 0x0010;
    const uint WM_QUIT = 0x0012;
    const uint PM_REMOVE = 0x0001;
    const uint CS_HREDRAW = 0x0002, CS_VREDRAW = 0x0001;
    const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    const int CW_USEDEFAULT = unchecked((int)0x80000000);

    public delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc;
        public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG {
        public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam;
        public uint time; public POINT pt; public uint lPrivate;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    // String-marshaling calls stay on classic DllImport (the source generator
    // only handles blittable signatures).
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wcex);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);
    [LibraryImport("user32.dll")] public static partial IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [LibraryImport("user32.dll")] public static partial int ShowWindow(IntPtr hwnd, int cmdShow);
    [LibraryImport("user32.dll")] public static partial int DestroyWindow(IntPtr hwnd);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int exitCode);
    [LibraryImport("user32.dll")] private static partial int PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] private static partial int TranslateMessage(ref MSG msg);
    [LibraryImport("user32.dll")] private static partial IntPtr DispatchMessageW(ref MSG msg);
    [LibraryImport("user32.dll")] private static partial int AdjustWindowRect(ref RECT rect, uint style, int menu);

    public static IntPtr CreateAppWindow(string title, int width, int height, WndProcDelegate proc) {
        IntPtr hinst = GetModuleHandleW(null);
        var wcex = new WNDCLASSEXW {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = hinst,
            lpszClassName = "PalmierShellSpikeWindow",
        };
        RegisterClassExW(ref wcex);
        var rect = new RECT { right = width, bottom = height };
        AdjustWindowRect(ref rect, WS_OVERLAPPEDWINDOW, 0);
        return CreateWindowExW(0, wcex.lpszClassName, title, WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT, rect.right - rect.left, rect.bottom - rect.top,
            IntPtr.Zero, IntPtr.Zero, hinst, IntPtr.Zero);
    }

    public static bool PumpMessages(IntPtr hwnd) {
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE) != 0) {
            if (msg.message == WM_QUIT) return false;
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        return true;
    }
}
