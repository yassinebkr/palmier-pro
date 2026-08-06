using Avalonia;
using PalmierShell.Core;

namespace PalmierShell;

static class Program {
    /// Read once here, before the UI loop exists, so the startup layout costs
    /// the render thread nothing.
    internal static WorkspaceLayout Layout { get; private set; } = WorkspaceLayout.Default;

    [STAThread]
    public static int Main(string[] args) {
        // Crash-reporter mode: launched by the native crash guard of a dying
        // instance to write its crash log + minidump from outside. Keep this
        // first and minimal — no crash handler, no engine, no Avalonia.
        if (args is ["--crash-report", var pid, var tid, var ep, var code, var addr, var uptime])
            return CrashHandler.CrashReport(pid, tid, ep, code, addr, uptime);
        CrashHandler.Install();
        SessionLog.Start();
        if (!CoreApi.TryLoadNativeHost()) {
            SessionLog.Event("startup", "PalmierCoreHost.dll could not be loaded");
            CrashHandler.ShowFatal("PalmierWin cannot start",
                "PalmierCoreHost.dll could not be loaded.\n\n" +
                "Reinstall PalmierWin, or keep PalmierShell.exe together with the files it shipped with.");
            return 2;
        }
        if (Environment.ProcessPath is { } exePath)
            CoreApi.palmier_install_crash_guard(exePath);
        if (args.Contains("--native-crash-test")) {
            // A real native fault inside the Swift DLL: exactly the wild case
            // the crash guard exists for.
            CoreApi.palmier_crash_test();
        }
        Layout = LayoutStore.Load();
        // Held for the process lifetime so the installer can detect — and ask
        // to close — a running instance before replacing its files.
        using var appMutex = CreateAppMutex();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    static Mutex? CreateAppMutex() {
        try { return new Mutex(true, "PalmierWinShell", out _); } catch { return null; }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
