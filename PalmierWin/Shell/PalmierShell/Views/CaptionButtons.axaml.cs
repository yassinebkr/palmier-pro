using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PalmierShell.Views;

/// The window controls for borderless chrome. Drop into any title strip; it
/// finds its own window. `ShowMinimize`/`ShowMaximize` trim it down for
/// dialogs that should only close.
public partial class CaptionButtons : UserControl {
    public static readonly StyledProperty<bool> ShowMinimizeProperty =
        AvaloniaProperty.Register<CaptionButtons, bool>(nameof(ShowMinimize), true);
    public static readonly StyledProperty<bool> ShowMaximizeProperty =
        AvaloniaProperty.Register<CaptionButtons, bool>(nameof(ShowMaximize), true);

    public bool ShowMinimize {
        get => GetValue(ShowMinimizeProperty);
        set => SetValue(ShowMinimizeProperty, value);
    }

    public bool ShowMaximize {
        get => GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

    public CaptionButtons() {
        InitializeComponent();
        MinimizeButton.Bind(IsVisibleProperty, this.GetObservable(ShowMinimizeProperty));
        MaximizeButton.Bind(IsVisibleProperty, this.GetObservable(ShowMaximizeProperty));
    }

    Window? Host => VisualRoot as Window;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        if (Host is { } window)
            window.GetObservable(Window.WindowStateProperty).Subscribe(new StateWatch(this));
    }

    void UpdateGlyph() {
        // The restore glyph is the two offset squares; maximize is one.
        MaximizeGlyph.Data = Avalonia.Media.Geometry.Parse(
            Host?.WindowState == WindowState.Maximized
                ? "M 0,2 H 7 V 9 H 0 Z M 2,2 V 0 H 9 V 7 H 7"
                : "M 0,0 H 9 V 9 H 0 Z");
    }

    void OnMinimize(object? sender, RoutedEventArgs e) {
        if (Host is { } window) window.WindowState = WindowState.Minimized;
    }

    void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximize();

    public void ToggleMaximize() {
        if (Host is { } window)
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
    }

    void OnClose(object? sender, RoutedEventArgs e) => Host?.Close();

    sealed class StateWatch(CaptionButtons owner) : IObserver<WindowState> {
        public void OnNext(WindowState value) => owner.UpdateGlyph();
        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
