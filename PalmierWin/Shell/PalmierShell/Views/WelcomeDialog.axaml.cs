using Avalonia.Controls;
using Avalonia.Input;
using PalmierShell.Core;

namespace PalmierShell.Views;

/// First run only: collect the name behind the top-right badge, an accent,
/// and which agent drives the tools. The accent previews live; closing
/// without Continue saves nothing, so the dialog comes back next launch.
public partial class WelcomeDialog : Window {
    public sealed record Choice(string Name, string AccentHex, string AgentMode);

    readonly string accentOnOpen;
    string accentHex;
    Choice? result;

    public WelcomeDialog() {
        InitializeComponent();
        accentOnOpen = accentHex = HexOf(Accent.Current);
        AccentPicker.SelectedHex = accentHex;
        AccentPicker.SelectionChanged += hex => {
            accentHex = hex;
            Accent.Apply(hex);
        };
        Opened += (_, _) => NameBox.Focus();
        Closed += (_, _) => { if (result is null) Accent.Apply(accentOnOpen); };
    }

    /// Returns the chosen name, accent, and agent mode — or null when closed.
    public static async Task<Choice?> ShowAsync(Window owner) {
        var dialog = new WelcomeDialog();
        await dialog.ShowDialog(owner);
        return dialog.result;
    }

    static string HexOf(Avalonia.Media.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    void OnNameChanged(object? sender, TextChangedEventArgs e) =>
        ContinueButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);

    void OnNameKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key is Key.Enter or Key.Return) {
            Commit();
            e.Handled = true;
        }
    }

    void OnContinue(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Commit();

    void Commit() {
        string name = NameBox.Text?.Trim() ?? "";
        if (name.Length == 0) return;
        string mode = ModeExternal.IsChecked == true
            ? AppSettings.AgentModeExternal : AppSettings.AgentModeInline;
        result = new Choice(name, accentHex, mode);
        Close();
    }

    void OnTitleBarPressed(object? sender, PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
