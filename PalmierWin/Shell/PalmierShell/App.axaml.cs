using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using PalmierShell.Core;
using PalmierShell.Views;

namespace PalmierShell;

public partial class App : Application {
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted() {
        // Not marked handled: the handler reports, then the fatal still ends
        // the process. Swallow-and-continue leaves a half-dead editor running.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            CrashHandler.OnUiThreadException(e.Exception);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
