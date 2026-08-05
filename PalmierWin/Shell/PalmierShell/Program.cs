using Avalonia;
using PalmierShell.Core;

namespace PalmierShell;

static class Program {
    /// Read once here, before the UI loop exists, so the startup layout costs
    /// the render thread nothing.
    internal static WorkspaceLayout Layout { get; private set; } = WorkspaceLayout.Default;

    [STAThread]
    public static int Main(string[] args) {
        CrashHandler.Install();
        SessionLog.Start();
        if (args.Contains("--native-crash-test")) {
            // A real native fault inside the Swift DLL: exactly the wild case
            // the vectored handler exists for.
            CoreApi.palmier_crash_test();
        }
        if (!CoreApi.TryLoadNativeHost()) {
            SessionLog.Event("startup", "PalmierCoreHost.dll could not be loaded");
            CrashHandler.ShowFatal("PalmierWin cannot start",
                "PalmierCoreHost.dll could not be loaded.\n\n" +
                "Reinstall PalmierWin, or keep PalmierShell.exe together with the files it shipped with.");
            return 2;
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
