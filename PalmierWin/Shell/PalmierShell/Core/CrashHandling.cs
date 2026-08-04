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
    }

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
