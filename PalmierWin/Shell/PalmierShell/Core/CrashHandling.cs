using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace PalmierShell.Core;

/// Writes crash logs under %APPDATA%\PalmierPro\logs. A log that cannot be
/// written returns null — the crash path must never throw its own exception.
public static class CrashLog {
    static readonly DateTime StartedAt = DateTime.UtcNow;

    /// Test seam: redirects the log directory. Never set in production code.
    public static string? DirectoryOverride;

    public static string LogDirectory => DirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PalmierPro", "logs");

    public static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    /// Writes one log file named crash-YYYYMMDD-HHMMSS.log and returns its
    /// path, or null when the log directory is not writable.
    public static string? Write(string kind, Exception exception) {
        try {
            Directory.CreateDirectory(LogDirectory);
            string path = Path.Combine(LogDirectory,
                $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, Format(kind, exception));
            return path;
        } catch {
            return null;
        }
    }

    /// Native-crash variant: free-form body (no managed exception exists).
    public static string? Write(string kind, string body) {
        try {
            Directory.CreateDirectory(LogDirectory);
            string path = Path.Combine(LogDirectory,
                $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, Format(kind, new Exception(body)));
            return path;
        } catch {
            return null;
        }
    }

    internal static string Format(string kind, Exception exception) {
        var text = new StringBuilder();
        text.AppendLine("PalmierWin crash log");
        text.AppendLine($"Time: {DateTime.UtcNow:O}");
        text.AppendLine($"Kind: {kind}");
        text.AppendLine($"Version: {Version}");
        text.AppendLine($"Uptime: {DateTime.UtcNow - StartedAt:g}");
        text.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        text.AppendLine();
        text.Append(exception);
        return text.ToString();
    }
}

/// Global last-resort exception handling: every fatal writes a crash log and
/// tells the user where it went before the process exits. Nothing here marks
/// a UI-thread exception as handled — a fatal is a fatal, never swallowed.
public static class CrashHandler {
    static int reported;

    public static void Install() {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Report("unhandled exception", e.ExceptionObject as Exception
                ?? new InvalidOperationException($"Non-exception throw: {e.ExceptionObject}"));
        TaskScheduler.UnobservedTaskException += (_, e) => {
            // Unobserved task exceptions do not take the process down, but a
            // silent one is a bug nobody ever hears about. Log and move on.
            CrashLog.Write("unobserved task exception", e.Exception);
            e.SetObserved();
        };
        InstallNativeHandler();
    }

    // --- Native crashes (access violations etc. in the Swift engine or CLR) ---
    // Managed handlers never see these; a vectored handler runs before the
    // process dies and leaves a log + minidump behind. Best-effort by design:
    // the process is already broken, so keep the work small and never throw.

    static readonly NativeExceptionDelegate nativeHandler = OnNativeException;
    static int nativeReported;

    delegate uint NativeExceptionDelegate(IntPtr exceptionPointers);

    static void InstallNativeHandler() {
        try {
            AddVectoredExceptionHandler(1, Marshal.GetFunctionPointerForDelegate(nativeHandler));
        } catch { }
    }

    static uint OnNativeException(IntPtr exceptionPointers) {
        const uint CONTINUE_SEARCH = 0;
        try {
            var rec = Marshal.PtrToStructure<ExceptionRecord>(ExceptionRecordOf(exceptionPointers));
            // Report only genuine faults. Everything else (thread naming,
            // debugger/RPC notifications, managed throws, CLR bookkeeping)
            // must not consume the single report slot — a benign blip at
            // startup used to swallow the real crash minutes later.
            if (rec.Code is not (0xC0000005 or 0xC000001D or 0xC00000FD or 0xC0000094
                or 0xC0000374 or 0xC0000409 or 0x80131506 or 0xE06D7363))
                return CONTINUE_SEARCH;
            if (Interlocked.Exchange(ref nativeReported, 1) != 0) return CONTINUE_SEARCH;
            string code = $"0x{rec.Code:X8}";
            string body = $"Native crash {code} at 0x{rec.Address.ToInt64():X16}\n" +
                          "(Swift engine or CLR fault — no managed stack available; see the .dmp next to this log)";
            string? path = CrashLog.Write($"native crash {code}", body);
            if (path is not null)
                WriteMinidump(Path.ChangeExtension(path, ".dmp"), exceptionPointers);
            Console.Error.WriteLine($"fatal: native crash {code} at 0x{rec.Address.ToInt64():X}");
        } catch { }
        return CONTINUE_SEARCH;  // let the process die its normal death
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ExceptionRecord { public uint Code; public uint Flags; public IntPtr Next; public IntPtr Address; }

    static IntPtr ExceptionRecordOf(IntPtr exceptionPointers) => Marshal.ReadIntPtr(exceptionPointers);

    static void WriteMinidump(string path, IntPtr exceptionPointers) {
        try {
            using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var info = new MinidumpExceptionInformation {
                ThreadId = GetCurrentThreadId(),
                ExceptionPointers = exceptionPointers,
                ClientPointers = 0,
            };
            MiniDumpWriteDump(GetCurrentProcess(), GetCurrentProcessId(),
                file.SafeFileHandle.DangerousGetHandle(), 2 /* MiniDumpWithFullMemory? no: 2 = with thread info */,
                ref info, IntPtr.Zero, IntPtr.Zero);
        } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MinidumpExceptionInformation { public uint ThreadId; public IntPtr ExceptionPointers; public int ClientPointers; }

    [DllImport("kernel32.dll")] static extern IntPtr AddVectoredExceptionHandler(uint first, IntPtr handler);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern uint GetCurrentProcessId();
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("dbghelp.dll")] static extern bool MiniDumpWriteDump(IntPtr process, uint pid, IntPtr file,
        int dumpType, ref MinidumpExceptionInformation info, IntPtr userStream, IntPtr callback);

    /// Avalonia UI-thread fatals arrive here from App.
    public static void OnUiThreadException(Exception exception) =>
        Report("unhandled UI thread exception", exception);

    static void Report(string kind, Exception exception) {
        // One report per process: a cascade of secondary exceptions during
        // teardown must not queue dialogs or overwrite the first log.
        if (Interlocked.Exchange(ref reported, 1) != 0) return;
        string? path = CrashLog.Write(kind, exception);
        Console.Error.WriteLine($"fatal: {kind}: {exception}");
        string body = path is not null
            ? $"PalmierWin ran into a problem and has to close.\n\nA crash report was saved to:\n{path}"
            : "PalmierWin ran into a problem and has to close.\n\nThe crash report could not be saved.";
        ShowFatal("PalmierWin", body + "\n\nPress OK to exit.");
    }

    /// A Win32 message box, not an Avalonia window: by the time this runs the
    /// dispatcher may not be healthy enough to show one.
    /// A Win32 message box, not an Avalonia window: by the time this runs the
    /// dispatcher may not be healthy enough to show one. Task-modal, topmost
    /// and foreground so the dialog cannot hide behind other apps' windows.
    public static void ShowFatal(string title, string body) {
        try { _ = MessageBox(IntPtr.Zero, body, title, 0x10 | 0x1000 | 0x10000 | 0x40000); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}
