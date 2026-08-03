using System.Runtime.CompilerServices;

namespace PalmierShell.Tests;

/// The interop tests load PalmierCoreHost.dll, which in turn needs the Swift
/// runtime and FFmpeg DLLs. Put all three on PATH before any P/Invoke so
/// `dotnet test` works from any shell, not only one primed by run-shell.ps1.
static class NativeSearchPath {
    [ModuleInitializer]
    internal static void Install() {
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        var dirs = new List<string> {
            Path.Combine(repo, "PalmierWin", ".build", "x86_64-unknown-windows-msvc", "debug"),
            Path.Combine(repo, "PalmierWin", "ThirdParty", "ffmpeg", "bin"),
        };
        if (SwiftRuntimeDirectory() is { } swift) dirs.Add(swift);
        Environment.SetEnvironmentVariable(
            "PATH", string.Join(';', dirs.Where(Directory.Exists).Append(Environment.GetEnvironmentVariable("PATH") ?? "")));
    }

    /// Newest installed Swift toolchain runtime, e.g. …\Swift\Runtimes\6.3.3\usr\bin.
    static string? SwiftRuntimeDirectory() {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Swift", "Runtimes");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root)
            .Select(d => Path.Combine(d, "usr", "bin"))
            .Where(Directory.Exists)
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
