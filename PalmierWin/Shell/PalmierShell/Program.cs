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
        if (!CoreApi.TryLoadNativeHost()) {
            CrashHandler.ShowFatal("PalmierWin cannot start",
                "PalmierCoreHost.dll could not be loaded.\n\n" +
                "Reinstall PalmierWin, or keep PalmierShell.exe together with the files it shipped with.");
            return 2;
        }
        Layout = LayoutStore.Load();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
