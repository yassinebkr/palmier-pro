using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PalmierShell.Core;

/// Writes one session log per day under %APPDATA%\PalmierPro\logs: startup
/// context (version, OS, GPU) plus milestone events. Console.Error is teed in,
/// so the existing stderr diagnostics land here too. Like CrashLog, a log that
/// cannot be written is dropped — logging must never take the app down.
public static class SessionLog {
    static readonly object Gate = new();
    static StreamWriter? writer;
    static TextWriter? originalError;
    static int started;

    /// Test seam: redirects the log directory. Never set in production code.
    public static string? DirectoryOverride;

    static string LogDirectory => DirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PalmierPro", "logs");

    /// Opens today's log and tees stderr into it. Idempotent; any failure
    /// leaves the app running without a session log.
    public static void Start() {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        try {
            Directory.CreateDirectory(LogDirectory);
            string path = Path.Combine(LogDirectory, $"session-{DateTime.Now:yyyyMMdd}.log");
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            lock (Gate) {
                writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                WriteLocked($"--- session {DateTime.UtcNow:O}");
                WriteLocked($"version: {CrashLog.Version}");
                WriteLocked($"os: {RuntimeInformation.OSDescription}");
                WriteLocked($"gpu: {GpuName() ?? "unknown"}");
                originalError = Console.Error;
                Console.SetError(new TeeWriter(originalError, writer, Gate));
            }
        } catch {
            lock (Gate) { writer = null; }
        }
    }

    /// One timestamped line: `category: message`. Safe from any thread.
    public static void Event(string category, string message) {
        lock (Gate) WriteLocked($"{category}: {message}");
    }

    static void WriteLocked(string line) {
        try { writer?.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {line}"); } catch { }
    }

    /// Test hook: closes the log so a test can read the file, and lets the
    /// next Start run again. Never called in production code.
    public static void Reset() {
        lock (Gate) {
            if (originalError is not null) Console.SetError(originalError);
            originalError = null;
            try { writer?.Dispose(); } catch { }
            writer = null;
            Interlocked.Exchange(ref started, 0);
        }
    }

    /// Display adapter name(s) from the registry — no WMI dependency, no
    /// Vulkan call (the engine reports its own device once it starts).
    static string? GpuName() {
        try {
            using var adapters = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (adapters is null) return null;
            var names = new List<string>();
            foreach (string sub in adapters.GetSubKeyNames()) {
                using var key = adapters.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is string desc && desc.Length > 0 && !names.Contains(desc))
                    names.Add(desc);
                if (names.Count == 2) break;
            }
            return names.Count > 0 ? string.Join(" · ", names) : null;
        } catch {
            return null;
        }
    }

    /// Mirrors every stderr line into the session log, keeping the original
    /// console target intact.
    sealed class TeeWriter(TextWriter inner, TextWriter log, object gate) : TextWriter {
        public override Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value) {
            inner.WriteLine(value);
            lock (gate) {
                try { log.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {value}"); } catch { }
            }
        }

        public override void Write(string? value) {
            inner.Write(value);
            lock (gate) {
                try { log.Write(value); } catch { }
            }
        }

        public override void Flush() {
            inner.Flush();
            lock (gate) {
                try { log.Flush(); } catch { }
            }
        }
    }
}
