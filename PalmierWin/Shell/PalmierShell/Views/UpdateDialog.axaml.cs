using Avalonia.Controls;
using Avalonia.Threading;
using PalmierShell.Core;

namespace PalmierShell.Views;

/// Offers a newer build: release notes, then Update / Later / Skip. Update
/// downloads the installer into the dialog's own progress bar, launches it,
/// and asks the main window to close. A failed download is an inline error,
/// never a crash or a dead dialog.
public partial class UpdateDialog : Window {
    void OnTitleBarPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    readonly UpdateInfo info;
    readonly Action requestClose;
    readonly CancellationTokenSource downloadCts = new();
    bool downloading;

    public UpdateDialog() : this(null!, null!) { }  // XAML designer only

    UpdateDialog(UpdateInfo info, Action requestClose) {
        this.info = info;
        this.requestClose = requestClose;
        InitializeComponent();
        if (info is null) return;
        TitleText.Text = $"PalmierWin v{info.Version} is available";
        NotesText.Text = Excerpt(info.Notes);
        AddButton("Skip this version", OnSkip);
        AddButton("Later", OnLater);
        AddButton("Update", OnUpdate, emphasised: true);
        Closing += (_, _) => downloadCts.Cancel();
    }

    static string Excerpt(string notes) {
        string text = notes.Trim();
        const int max = 4000;
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// `requestClose` runs after the installer launches; the main window's
    /// own unsaved-changes guard still applies.
    public static async Task ShowAsync(Window owner, UpdateInfo info, Action requestClose) {
        var dialog = new UpdateDialog(info, requestClose);
        await dialog.ShowDialog(owner);
    }

    void AddButton(string text, Action onClick, bool emphasised = false) {
        var button = new Button { Content = text, FontSize = 12, MinWidth = 76 };
        if (emphasised) button.Classes.Add("primary");
        button.Click += (_, _) => onClick();
        Buttons.Children.Add(button);
    }

    async void OnSkip() {
        string version = info.Version.ToString();
        await Task.Run(() => SettingsStore.Update(s => s with { UpdateSkipVersion = version }));
        SessionLog.Event("update", $"skipped v{version}");
        Close();
    }

    async void OnLater() {
        await Task.Run(() => SettingsStore.Update(s =>
            s with { UpdateSnoozeUntil = DateTimeOffset.UtcNow.AddDays(1) }));
        Close();
    }

    async void OnUpdate() {
        if (downloading) return;
        downloading = true;
        Buttons.IsVisible = false;
        ProgressPanel.IsVisible = true;
        StatusText.Text = "Downloading…";
        SessionLog.Event("update", $"downloading v{info.Version}");
        var progress = new Progress<int>(percent => {
            Bar.Value = percent;
            PercentText.Text = $"{percent}%";
        });
        try {
            string path = await UpdateChecker.DownloadAsync(info, progress, downloadCts.Token);
            StatusText.Text = "Opening installer…";
            SessionLog.Event("update", $"launching {path}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) {
                UseShellExecute = true,
            });
            Close();
            requestClose();
        } catch (OperationCanceledException) {
            // Window closed mid-download: the partial file is already gone.
        } catch (Exception ex) {
            SessionLog.Event("update", $"download failed: {ex.Message}");
            ProgressPanel.IsVisible = false;
            ErrorText.Text = "The update could not be downloaded. Check your connection and try again.";
            ErrorText.IsVisible = true;
            Buttons.Children.Clear();
            Buttons.IsVisible = true;
            AddButton("Close", Close);
        } finally {
            downloading = false;
        }
    }
}
