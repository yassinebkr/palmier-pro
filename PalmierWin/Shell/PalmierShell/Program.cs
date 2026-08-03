using Avalonia;
using PalmierShell.Core;

namespace PalmierShell;

static class Program {
    /// Read once here, before the UI loop exists, so the startup layout costs
    /// the render thread nothing.
    internal static WorkspaceLayout Layout { get; private set; } = WorkspaceLayout.Default;

    [STAThread]
    public static void Main(string[] args) {
        Layout = LayoutStore.Load();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
