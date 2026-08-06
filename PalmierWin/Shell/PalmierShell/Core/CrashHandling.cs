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
            File.WriteAllText(path, Format(kind, exception, null));
            return path;
        } catch {
            return null;
        }
    }

    /// Native-crash variant: free-form body (no managed exception exists) and
    /// the faulting process's uptime, measured by the native crash guard.
    public static string? Write(string kind, string body, TimeSpan? uptime = null) {
        try {
            Directory.CreateDirectory(LogDirectory);
            string path = Path.Combine(LogDirectory,
                $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, Format(kind, new Exception(body), uptime));
            return path;
        } catch {
            return null;
        }
    }

    internal static string Format(string kind, Exception exception, TimeSpan? uptime = null) {
        var text = new StringBuilder();
        text.AppendLine("PalmierWin crash log");
        text.AppendLine($"Time: {DateTime.UtcNow:O}");
        text.AppendLine($"Kind: {kind}");
        text.AppendLine($"Version: {Version}");
        text.AppendLine($"Uptime: {uptime ?? (DateTime.UtcNow - StartedAt):g}");
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
    }

    // --- Native crashes (access violations etc. in the Swift engine or CLR) ---
    // A native vectored handler inside PalmierCoreHost.dll (CCrashGuard)
    // catches them; a MANAGED vectored handler used to run here, but entering
    // managed code on an arbitrary faulting thread is a fatal CLR reverse-
    // pinvoke transition under GC (0x80131506) — the handler itself killed the
    // app during playback. The native guard instead re-launches this exe as
    // `--crash-report <pid> <tid> <exceptionPointers> <code> <address>
    // <uptimeMs>`; that healthy process writes the .log and the minidump.

    /// Crash-reporter mode entry (see Program.Main). Exit codes: 0 dump with
    /// exception context, 2 dump without it, 3 OpenProcess failed, 4 dump
    /// failed, 5 bad arguments or unexpected failure.
    public static int CrashReport(string pidText, string tidText, string epText,
                                  string codeText, string addrText, string uptimeText) {
        string? logPath = null;
        try {
            string code = codeText;  // the guard formats it "0x" + 8 upper hex
            long addr = long.Parse(addrText.AsSpan(2), System.Globalization.NumberStyles.HexNumber);
            var uptime = TimeSpan.FromMilliseconds(ulong.Parse(uptimeText));
            string body = $"Native crash {code} at 0x{addr:X16}\n" +
                          "(Swift engine or CLR fault — no managed stack available; see the .dmp next to this log)";
            logPath = CrashLog.Write($"native crash {code}", body, uptime);
            if (logPath is null)
                Console.Error.WriteLine("crash log could not be written");
        } catch { }
        try {
            string dmpPath = logPath is not null
                ? Path.ChangeExtension(logPath, ".dmp")
                : Path.Combine(CrashLog.LogDirectory,
                               $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.dmp");
            return WriteDump(uint.Parse(pidText), tidText, epText, dmpPath);
        } catch { return 5; }
    }

    static int WriteDump(uint pid, string tidText, string epText, string dmpPath) {
        IntPtr infoPtr = IntPtr.Zero;
        try {
            var info = new MinidumpExceptionInformation {
                ThreadId = uint.Parse(tidText),
                ExceptionPointers = (IntPtr)long.Parse(epText.AsSpan(2),
                    System.Globalization.NumberStyles.HexNumber),
                ClientPointers = 0,  // the pointers live in the target process
            };
            infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(info));
            Marshal.StructureToPtr(info, infoPtr, false);
            IntPtr target = OpenProcess(0x001F0FFF /* PROCESS_ALL_ACCESS */, false, pid);
            if (target == IntPtr.Zero) return 3;
            using var file = new FileStream(dmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
            const int MiniDumpNormal = 0;  // every thread's stack
            IntPtr handle = file.SafeFileHandle.DangerousGetHandle();
            if (MiniDumpWriteDump(target, pid, handle, MiniDumpNormal, infoPtr, IntPtr.Zero, IntPtr.Zero))
                return 0;
            // Retry without the exception context: stacks still land, and the
            // .log already carries the exception code + fault address.
            if (MiniDumpWriteDump(target, pid, handle, MiniDumpNormal, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
                return 2;
            return 4;
        } catch { return 5; }
        finally { if (infoPtr != IntPtr.Zero) Marshal.FreeHGlobal(infoPtr); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MinidumpExceptionInformation { public uint ThreadId; public IntPtr ExceptionPointers; public int ClientPointers; }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("dbghelp.dll", SetLastError = true)] static extern bool MiniDumpWriteDump(IntPtr process, uint pid, IntPtr file,
        int dumpType, IntPtr exceptionInfo, IntPtr userStream, IntPtr callback);

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
    /// dispatcher may not be healthy enough to show one. Task-modal, topmost
    /// and foreground so the dialog cannot hide behind other apps' windows.
    public static void ShowFatal(string title, string body) {
        try { _ = MessageBox(IntPtr.Zero, body, title, 0x10 | 0x1000 | 0x10000 | 0x40000); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}
