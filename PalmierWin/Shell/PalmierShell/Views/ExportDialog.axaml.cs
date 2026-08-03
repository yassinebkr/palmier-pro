using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PalmierShell.Core;
using PalmierShell.ViewModels;

namespace PalmierShell.Views;

/// Renders the timeline offscreen to H.264/MP4 via the core exporter, with
/// polled progress. Closing is blocked while an export runs (no cancel v1).
public partial class ExportDialog : Window {
    void OnTitleBarPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    readonly MainViewModel main;
    IntPtr export;
    DispatcherTimer? pollTimer;

    public ExportDialog() : this(null!) { }  // XAML designer only

    public ExportDialog(MainViewModel main) {
        this.main = main;
        InitializeComponent();
        if (main is null) return;
        var state = main.Timeline.State;
        int frames = main.Timeline.TotalFrames;
        InfoText.Text = $"{state?.Width ?? 1920} × {state?.Height ?? 1080} · {state?.Fps ?? 30} fps · " +
                        $"{TimeSpan.FromSeconds(frames / 30.0):m\\:ss} · H.264 MP4";
        PathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "palmier-export.mp4");
        Closing += (_, e) => { if (export != IntPtr.Zero && !exportFinished) e.Cancel = true; };
    }

    bool exportFinished;

    async void OnBrowse(object? sender, RoutedEventArgs e) {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = "Export video",
            SuggestedFileName = "palmier-export.mp4",
            DefaultExtension = "mp4",
            FileTypeChoices = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4"] }],
        });
        if (file?.TryGetLocalPath() is { } path) PathBox.Text = path;
    }

    void OnExport(object? sender, RoutedEventArgs e) {
        string path = PathBox.Text?.Trim() ?? "";
        if (path.Length == 0 || main.Timeline.TotalFrames == 0) {
            StatusText.Text = main.Timeline.TotalFrames == 0
                ? "The timeline is empty — nothing to export."
                : "Choose an output file first.";
            return;
        }
        export = CoreApi.palmier_export_start(main.Project, path);
        if (export == IntPtr.Zero) {
            StatusText.Text = "Export could not start (empty timeline or encoder failure).";
            return;
        }
        exportFinished = false;
        ExportButton.IsEnabled = false;
        CloseButton.IsEnabled = false;
        PathBox.IsEnabled = false;
        Progress.IsVisible = true;
        StatusText.Text = "Exporting…";
        pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        pollTimer.Tick += (_, _) => Poll(path);
        pollTimer.Start();
    }

    void Poll(string path) {
        int status = CoreApi.palmier_export_status(export);
        if (status is >= 0 and <= 100) {
            Progress.Value = status;
            return;
        }
        pollTimer?.Stop();
        exportFinished = true;
        CloseButton.IsEnabled = true;
        ExportButton.IsEnabled = true;
        PathBox.IsEnabled = true;
        if (status == 101) {
            Progress.Value = 100;
            StatusText.Text = $"Done — {path}";
        } else {
            StatusText.Text = CoreApi.GetExportError(export);
        }
        CoreApi.palmier_export_destroy(export);
        export = IntPtr.Zero;
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();
}
