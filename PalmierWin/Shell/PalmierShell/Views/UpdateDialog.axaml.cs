using Avalonia.Controls;
using Avalonia.Threading;
using PalmierShell.Core;

namespace PalmierShell.Views;

/// Installs a newer build the moment one exists: the dialog opens already
/// downloading, shows the release notes and the progress bar, then runs the
/// installer silently and asks the main window to close. There is no
/// Later/Skip during the beta — a failed download is an inline error, never
/// a crash or a dead dialog.
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
        TitleText.Text = $"Updating PalmierWin to v{info.Version}";
        NotesText.Text = Excerpt(info.Notes);
        Closing += (_, _) => downloadCts.Cancel();
        Opened += (_, _) => Download();
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

    async void Download() {
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
            StatusText.Text = "Installing…";
            SessionLog.Event("update", $"launching {path}");
            // Silent: shortcut and logs keep their installer defaults, the app
            // is closed via its mutex, and the postinstall entry relaunches it.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) {
                UseShellExecute = true,
                Arguments = "/VERYSILENT /NORESTART",
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
            var close = new Button { Content = "Close", FontSize = 12, MinWidth = 76 };
            close.Click += (_, _) => Close();
            Buttons.Children.Add(close);
        } finally {
            downloading = false;
        }
    }
}
