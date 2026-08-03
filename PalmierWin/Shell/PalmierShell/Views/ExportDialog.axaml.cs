using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PalmierShell.Core;
using PalmierShell.ViewModels;

namespace PalmierShell.Views;

/// Adds timeline exports to the shared export queue and mirrors its jobs.
/// Closing never touches the queue — jobs keep running and a reopened dialog
/// shows their live state.
public partial class ExportDialog : Window {
    void OnTitleBarPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    sealed class JobRow {
        public required Border Root { get; init; }
        public required TextBlock State { get; init; }
        public required ProgressBar Bar { get; init; }
        public required Button Action { get; init; }
    }

    readonly MainViewModel main;
    readonly Dictionary<int, JobRow> rows = new();
    readonly DispatcherTimer? refresh;
    string mediaInfo = "";

    public ExportDialog() : this(null!) { }  // XAML designer only

    public ExportDialog(MainViewModel main) {
        this.main = main;
        InitializeComponent();
        if (main is null) return;
        var state = main.Timeline.State;
        int frames = main.Timeline.TotalFrames;
        mediaInfo = $"{main.ProjectDimensions} · {state?.Fps ?? 30} fps · " +
                    $"{TimeSpan.FromSeconds(frames / 30.0):m\\:ss}";
        ApplyFormat();
        refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        refresh.Tick += (_, _) => RefreshJobs();
        refresh.Start();
        Closed += (_, _) => refresh?.Stop();
        RefreshJobs();
    }

    bool IsFcpxml => FormatBox.SelectedIndex == 1;

    void OnFormatChanged(object? sender, SelectionChangedEventArgs e) {
        // SelectionChanged fires while InitializeComponent is still parsing;
        // named controls further down the XAML are not assigned yet.
        if (main is null || PathBox is null) return;
        ApplyFormat();
    }

    /// Switches path extension, info line, and queueing between MP4 (queued
    /// render) and FCPXML (written immediately — it's instant).
    void ApplyFormat() {
        InfoText.Text = IsFcpxml ? mediaInfo + " · FCPXML 1.10" : mediaInfo + " · H.264 MP4";
        QueueButton.IsVisible = !IsFcpxml;
        string path = PathBox.Text?.Trim() ?? "";
        string ext = IsFcpxml ? ".fcpxml" : ".mp4";
        if (path.Length == 0) {
            PathBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                "palmier-export" + ext);
        } else if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) {
            PathBox.Text = Path.ChangeExtension(path, ext);
        }
    }

    async void OnBrowse(object? sender, RoutedEventArgs e) {
        var file = await StorageProvider.SaveFilePickerAsync(IsFcpxml
            ? new FilePickerSaveOptions {
                Title = "Export FCPXML",
                SuggestedFileName = "palmier-export.fcpxml",
                DefaultExtension = "fcpxml",
                FileTypeChoices = [new FilePickerFileType("FCPXML") { Patterns = ["*.fcpxml"] }],
            }
            : new FilePickerSaveOptions {
                Title = "Export video",
                SuggestedFileName = "palmier-export.mp4",
                DefaultExtension = "mp4",
                FileTypeChoices = [new FilePickerFileType("MP4 video") { Patterns = ["*.mp4"] }],
            });
        if (file?.TryGetLocalPath() is { } path) PathBox.Text = path;
    }

    void OnQueue(object? sender, RoutedEventArgs e) {
        if (!TryEnqueue(out string path)) return;
        StatusText.Text = $"Queued — {path}";
        RefreshJobs();
    }

    async void OnExportNow(object? sender, RoutedEventArgs e) {
        if (IsFcpxml) {
            await ExportFcpxmlAsync();
            return;
        }
        if (!TryEnqueue(out _)) return;
        Close();
    }

    async Task ExportFcpxmlAsync() {
        string path = PathBox.Text?.Trim() ?? "";
        if (path.Length == 0 || main.Timeline.TotalFrames == 0) {
            StatusText.Text = main.Timeline.TotalFrames == 0
                ? "The timeline is empty — nothing to export."
                : "Choose an output file first.";
            return;
        }
        if (CoreApi.palmier_export_fcpxml(main.Project, path) == 1) {
            await MessageDialog.AskAsync(this, "Export complete",
                $"FCPXML written to {path}", "OK");
        } else {
            await MessageDialog.AskAsync(this, "Export failed",
                "The timeline could not be written as FCPXML.", "OK");
        }
    }

    bool TryEnqueue(out string path) {
        path = PathBox.Text?.Trim() ?? "";
        if (path.Length == 0 || main.Timeline.TotalFrames == 0) {
            StatusText.Text = main.Timeline.TotalFrames == 0
                ? "The timeline is empty — nothing to export."
                : "Choose an output file first.";
            return false;
        }
        string outputPath = path;
        if (main.Exports.Jobs.Any(j => j.OutputPath == outputPath &&
                                       j.State is ExportJobState.Queued or ExportJobState.Running)) {
            StatusText.Text = "That output file is already queued.";
            return false;
        }
        main.Exports.Enqueue(path);
        return true;
    }

    /// Syncs the job rows with the queue: adds rows for new jobs, updates
    /// progress/state/actions on the rest, drops rows for removed jobs.
    void RefreshJobs() {
        var jobs = main.Exports.Jobs;
        foreach (var job in jobs) {
            if (!rows.TryGetValue(job.Id, out var row)) {
                row = AddRow(job);
                rows[job.Id] = row;
            }
            row.Bar.Value = job.Progress;
            row.Bar.IsVisible = job.State is ExportJobState.Running or ExportJobState.Done;
            row.State.Text = StateText(job);
            row.Action.IsVisible = job.State is ExportJobState.Queued or ExportJobState.Running;
        }
        foreach (int stale in rows.Keys.Except(jobs.Select(j => j.Id)).ToList()) {
            JobsPanel.Children.Remove(rows[stale].Root);
            rows.Remove(stale);
        }
    }

    static string StateText(ExportJob job) => job.State switch {
        ExportJobState.Queued => "Queued",
        ExportJobState.Running => $"Exporting… {job.Progress}%",
        ExportJobState.Done => "Done",
        ExportJobState.Failed => job.Error ?? "Export failed.",
        ExportJobState.Cancelled => "Cancelled",
        _ => "",
    };

    JobRow AddRow(ExportJob job) {
        var name = new TextBlock {
            Text = Path.GetFileName(job.OutputPath),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var state = new TextBlock {
            FontSize = 11,
            Foreground = (IBrush?)this.FindResource("ThemeTextTertiaryBrush"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        var bar = new ProgressBar {
            Minimum = 0, Maximum = 100, Height = 4,
            Foreground = (IBrush?)this.FindResource("ThemeTimecodeBrush"),
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
        };
        var action = new Button {
            Content = job.State == ExportJobState.Queued ? "Remove" : "Cancel",
            FontSize = 11,
            Padding = new Avalonia.Thickness(10, 2),
            Margin = new Avalonia.Thickness(8, 4, 0, 0),
        };
        action.Click += (_, _) => {
            if (job.State == ExportJobState.Queued) main.Exports.Remove(job);
            else main.Exports.Cancel(job);
            RefreshJobs();
        };

        var grid = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };
        grid.Children.Add(name);
        Grid.SetColumn(state, 1);
        grid.Children.Add(state);
        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);
        Grid.SetRow(action, 1);
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);

        int index = main.Exports.Jobs.Select(j => j.Id).ToList().IndexOf(job.Id);
        var root = new Border {
            Background = (IBrush?)this.FindResource("ThemeRaisedBrush"),
            BorderBrush = (IBrush?)this.FindResource("ThemeBorderBrush"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(4),
            Padding = new Avalonia.Thickness(10, 6),
            Child = grid,
        };
        // Rows mirror queue order; jobs are only ever appended and removed.
        JobsPanel.Children.Insert(index < 0 ? JobsPanel.Children.Count : index, root);
        return new JobRow { Root = root, State = state, Bar = bar, Action = action };
    }

    void OnClose(object? sender, RoutedEventArgs e) => Close();
}
