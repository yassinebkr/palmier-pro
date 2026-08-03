using Avalonia.Controls;

namespace PalmierShell.Views;

/// A small modal question. Avalonia ships no message box, and losing a
/// project to a silent overwrite is exactly the case that needs one.
public partial class MessageDialog : Window {
    void OnTitleBarPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    public enum Choice { Primary, Secondary, Cancel }

    Choice result = Choice.Cancel;

    public MessageDialog() => InitializeComponent();

    /// Shows `title`/`body` with up to three buttons, right to left in the
    /// order given. Returns which one was pressed; closing counts as Cancel.
    public static async Task<Choice> AskAsync(Window owner, string title, string body,
                                              string primary, string? secondary = null,
                                              string? cancel = null) {
        var dialog = new MessageDialog { Title = title };
        dialog.TitleText.Text = title;
        dialog.BodyText.Text = body;

        if (cancel is not null) dialog.AddButton(cancel, Choice.Cancel);
        if (secondary is not null) dialog.AddButton(secondary, Choice.Secondary);
        dialog.AddButton(primary, Choice.Primary, emphasised: true);

        await dialog.ShowDialog(owner);
        return dialog.result;
    }

    /// Asks for one line of text. Returns null when cancelled or left blank —
    /// callers treat both as "no change".
    public static async Task<string?> PromptAsync(Window owner, string title, string body,
                                                  string initial, string confirm = "Rename") {
        var dialog = new MessageDialog { Title = title };
        dialog.TitleText.Text = title;
        dialog.BodyText.Text = body;
        dialog.Input.IsVisible = true;
        dialog.Input.Text = initial;
        dialog.Input.KeyDown += (_, e) => {
            if (e.Key is not (Avalonia.Input.Key.Enter or Avalonia.Input.Key.Return)) return;
            dialog.result = Choice.Primary;
            dialog.Close();
        };
        dialog.Opened += (_, _) => {
            dialog.Input.Focus();
            dialog.Input.SelectAll();
        };

        dialog.AddButton("Cancel", Choice.Cancel);
        dialog.AddButton(confirm, Choice.Primary, emphasised: true);

        await dialog.ShowDialog(owner);
        string text = dialog.Input.Text?.Trim() ?? "";
        return dialog.result == Choice.Primary && text.Length > 0 ? text : null;
    }

    void AddButton(string text, Choice choice, bool emphasised = false) {
        var button = new Button { Content = text, FontSize = 12, MinWidth = 76 };
        if (emphasised) {
            button.Background = (Avalonia.Media.IBrush?)this.FindResource("ThemeTimecodeBrush");
            button.Foreground = Avalonia.Media.Brushes.Black;
        }
        button.Click += (_, _) => {
            result = choice;
            Close();
        };
        Buttons.Children.Add(button);
    }
}
