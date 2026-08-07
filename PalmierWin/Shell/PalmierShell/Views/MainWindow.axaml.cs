using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PalmierShell.Core;
using PalmierShell.Core.Mcp;
using PalmierShell.ViewModels;

namespace PalmierShell.Views;

public partial class MainWindow : Window {
    /// Active viewer tab reads as raised; inactive tabs sit flat on the header.
    public static readonly Avalonia.Data.Converters.IValueConverter TabBackground =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.IBrush>(
            active => new Avalonia.Media.SolidColorBrush(
                Avalonia.Media.Color.Parse(active ? "#2C2C2C" : "#00000000")));

    readonly MainViewModel viewModel = new();

    /// The title strip is the drag surface now that the OS chrome is gone.
    /// Interactive children (File, rename, Export…) handle their own presses,
    /// so only clicks on the empty strip arrive here.
    void OnTitleBarPressed(object? sender, PointerPressedEventArgs e) {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // A rename in progress commits on any click elsewhere in the bar —
        // the bar itself takes no focus, so LostFocus alone never fires.
        if (viewModel.RenamingProject) {
            _ = viewModel.CommitRenameProjectAsync();
            return;
        }
        if (e.ClickCount == 2) Caption.ToggleMaximize();
        else BeginMoveDrag(e);
    }

    public MainWindow() {
        viewModel.AutoPlay = Environment.GetCommandLineArgs().Contains("--autoplay");
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Media.StorageProviderSource = () => StorageProvider;
        viewModel.DialogOwner = () => this;
        // The composer is a window of its own; the view model only says whether
        // it should be up, so arming a transition can raise it from anywhere.
        viewModel.Media.Generate.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(ViewModels.GeneratePanelViewModel.IsOpen))
                ShowGenerateWindow(viewModel.Media.Generate.IsOpen);
        };
        Preview.SessionReady += viewModel.AttachEngine;
        Preview.SessionFailed += () => {
            Preview.IsVisible = false;
            PreviewFallback.IsVisible = true;
        };
        Preview.InputReady += viewModel.AttachPreviewInput;
        ApplyLayout(Program.Layout);
        Closing += OnClosing;
        Closed += (_, _) => {
            // The MCP server stops inside Dispose, before the engine handles
            // die (the agent shutdown ordering); a request abandoned
            // mid-flight is refused by the core's dead-handle guards.
            viewModel.Dispose();
        };
        Opened += OnOpened;
        KeyDown += OnKeyDown;
        viewModel.PreferencesApplied += MaybeShowWelcome;
        // Resized arrives after the native resize, unlike the WindowState
        // change itself — the margin math needs the final window rect.
        Resized += (_, _) => {
            if (WindowState == WindowState.Maximized) UpdateMaximizedMargin();
        };

        autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        autosave.Tick += (_, _) => viewModel.SaveRecovery();
        autosave.Start();

        // The queue is dialog-independent, so its pump lives here for the
        // app's lifetime; Tick is a no-op while the queue is idle.
        exportPump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        exportPump.Tick += (_, _) => viewModel.Exports.Tick();
        exportPump.Start();

        // Update checks: once shortly after startup, then hourly — during the
        // beta a new build should land the moment it is published.
        updateDaily = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        updateDaily.Tick += async (_, _) => await CheckForUpdateAsync();
        updateStartup = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        updateStartup.Tick += async (_, _) => {
            updateStartup.Stop();
            await CheckForUpdateAsync();
            updateDaily.Start();
        };
        Closed += (_, _) => {
            updateStartup.Stop();
            updateDaily.Stop();
        };
    }

    readonly DispatcherTimer autosave;
    readonly DispatcherTimer exportPump;
    readonly DispatcherTimer updateStartup;
    readonly DispatcherTimer updateDaily;
    bool closeConfirmed;
    bool exportNoticeShowing;
    bool updatePromptOpen;

    /// Offers a newer build when one exists. A check must never block or
    /// break the session: offline, rate-limited, and malformed responses all
    /// read as "no update".
    async Task CheckForUpdateAsync() {
        if (updatePromptOpen) return;
        try {
            if (await UpdateChecker.CheckAsync() is not { } info) return;
            SessionLog.Event("update", $"v{info.Version} available");
            updatePromptOpen = true;
            try {
                await UpdateDialog.ShowAsync(this, info, Close);
            } finally {
                updatePromptOpen = false;
            }
        } catch {
            // Stay silent: the next daily tick tries again.
        }
    }

    bool opened;
    bool welcomeShown;

    /// First launch only: collect the name and accent that drive the badge.
    /// Waits for both the window and the settings read before showing.
    void MaybeShowWelcome() {
        if (welcomeShown || !opened || !viewModel.PreferencesReady || !viewModel.NeedsWelcome) return;
        welcomeShown = true;
        _ = ShowWelcomeAsync();
    }

    async Task ShowWelcomeAsync() {
        var choice = await WelcomeDialog.ShowAsync(this);
        if (choice is null) return;  // closed: nothing saved, asked again next launch
        viewModel.SetUserName(choice.Name);
        var updated = await Task.Run(() => SettingsStore.Update(s =>
            s with { UserName = choice.Name, Accent = choice.AccentHex,
                     AgentMode = choice.AgentMode }));
        viewModel.Mcp.ApplySettings(updated);
    }

    /// Borderless windows are sized past the screen edges when maximized;
    /// the root margin in UpdateMaximizedMargin pulls the content back in.
    /// Restore can go straight to zero; the maximize direction is corrected
    /// by the Resized handler above once the native rect is final.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && WindowState != WindowState.Maximized)
            RootGrid.Margin = new Thickness(0);
    }

    void UpdateMaximizedMargin() {
        if (WindowState != WindowState.Maximized) return;
        var margin = FrameOverhang();
        if (margin != RootGrid.Margin) RootGrid.Margin = margin;
    }

    /// How far the maximized borderless window hangs past the monitor's
    /// working area, in DIPs, per side. Windows sizes a maximized window to
    /// the work area plus the classic frame overhang even when there is no
    /// frame, so the working area — not the window rect — is the measure.
    Thickness FrameOverhang() {
        try {
            if (TryGetPlatformHandle() is not { } handle) return new Thickness(8);
            if (!GetWindowRect(handle.Handle, out RECT window)) return new Thickness(8);
            var area = (Screens.ScreenFromWindow(this) ?? Screens.Primary)?.WorkingArea;
            if (area is not { } workArea) return new Thickness(8);
            double scale = RenderScaling > 0 ? RenderScaling : 1;
            double Side(double outside, double inside) => Math.Max(0, (inside - outside) / scale);
            return new Thickness(
                Side(window.Left, workArea.X), Side(window.Top, workArea.Y),
                Side(workArea.X + workArea.Width, window.Right),
                Side(workArea.Y + workArea.Height, window.Bottom));
        } catch {
            return new Thickness(8);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// Never drop unsaved work on close: cancel, ask, then close for real.
    async void OnClosing(object? sender, WindowClosingEventArgs e) {
        SaveLayout();
        if (closeConfirmed) return;
        if (viewModel.Exports.HasActiveWork) {
            e.Cancel = true;
            if (exportNoticeShowing) return;
            exportNoticeShowing = true;
            await MessageDialog.AskAsync(this, "Exports in progress",
                "An export is still queued or running. Wait for it to finish, or cancel it from the Export panel.",
                "OK");
            exportNoticeShowing = false;
            return;
        }
        if (!viewModel.ProjectDirty) return;
        e.Cancel = true;
        if (!await ConfirmDiscardAsync()) return;
        closeConfirmed = true;
        autosave.Stop();
        Close();
    }

    /// Collapsed width of the agent rail, in DIPs.
    const double AgentRailWidth = 40;

    void OnToggleAgentPanel(object? sender, RoutedEventArgs e) {
        bool collapsing = !viewModel.AgentCollapsed;
        if (collapsing) expandedAgentWidth = RootGrid.ColumnDefinitions[0].ActualWidth;
        viewModel.AgentCollapsed = collapsing;
        AnimateAgentWidth(collapsing ? AgentRailWidth : expandedAgentWidth);
    }

    double expandedAgentWidth = WorkspaceLayout.Default.AgentWidth;
    DispatcherTimer? agentSlide;

    /// Avalonia cannot animate a GridLength, so the column is stepped by hand
    /// on a frame timer with an ease-out curve.
    void AnimateAgentWidth(double target) {
        var column = RootGrid.ColumnDefinitions[0];
        // Limits have to be released first, or they clamp the animation.
        column.MinWidth = 0;
        column.MaxWidth = double.PositiveInfinity;

        agentSlide?.Stop();
        double from = column.ActualWidth;
        var started = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(180);

        agentSlide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 90) };
        agentSlide.Tick += (_, _) => {
            double t = Math.Clamp((DateTime.UtcNow - started) / duration, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3);
            column.Width = new GridLength(from + (target - from) * eased);
            if (t < 1) return;
            agentSlide!.Stop();
            ApplyAgentWidth();
        };
        agentSlide.Start();
    }

    void ApplyAgentWidth() {
        var column = RootGrid.ColumnDefinitions[0];
        if (viewModel.AgentCollapsed) {
            column.MinWidth = 0;
            column.Width = new GridLength(AgentRailWidth);
            column.MaxWidth = AgentRailWidth;
        } else {
            column.MaxWidth = 420;
            column.MinWidth = 170;
            column.Width = new GridLength(expandedAgentWidth);
        }
    }

    void ApplyLayout(WorkspaceLayout layout) {
        Width = layout.WindowWidth;
        Height = layout.WindowHeight;
        if (double.IsFinite(layout.WindowX) && double.IsFinite(layout.WindowY)) {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint((int)layout.WindowX, (int)layout.WindowY);
        }
        if (layout.Maximized) WindowState = WindowState.Maximized;
        expandedAgentWidth = layout.AgentWidth;
        viewModel.AgentCollapsed = layout.AgentCollapsed;
        ApplyAgentWidth();
        RootGrid.ColumnDefinitions[2].Width = new GridLength(layout.MediaWidth);
        RootGrid.ColumnDefinitions[6].Width = new GridLength(layout.InspectorWidth);
        RootGrid.RowDefinitions[3].Height = new GridLength(layout.TimelineHeight);
    }

    /// Written on Closing: a few hundred bytes to local AppData, and the app is
    /// on its way out — deferring it off-thread risks losing the write.
    void SaveLayout() {
        bool maximized = WindowState == WindowState.Maximized;
        var restore = maximized ? null : (double?)Width;
        LayoutStore.Save(new WorkspaceLayout(
            restore ?? Program.Layout.WindowWidth,
            maximized ? Program.Layout.WindowHeight : Height,
            maximized ? Program.Layout.WindowX : Position.X,
            maximized ? Program.Layout.WindowY : Position.Y,
            maximized,
            // Store the expanded width, not the rail's, so unfolding restores
            // the panel the user actually sized.
            viewModel.AgentCollapsed ? expandedAgentWidth : RootGrid.ColumnDefinitions[0].ActualWidth,
            RootGrid.ColumnDefinitions[2].ActualWidth,
            RootGrid.ColumnDefinitions[6].ActualWidth,
            RootGrid.RowDefinitions[3].ActualHeight) { AgentCollapsed = viewModel.AgentCollapsed });
    }

    /// A saved origin can point at a monitor that is no longer attached.
    void RecentreIfOffScreen() {
        var bounds = new PixelRect(Position, PixelSize.FromSize(ClientSize, RenderScaling));
        if (Screens.All.Any(s => s.WorkingArea.Intersects(bounds))) return;
        var area = (Screens.Primary ?? Screens.All[0]).WorkingArea;
        Position = new PixelPoint(
            area.X + (area.Width - bounds.Width) / 2,
            area.Y + (area.Height - bounds.Height) / 2);
    }

    /// Dev convenience: `PalmierShell.exe [--add-to-timeline] [--select-first-clip]
    /// <files…>` imports the files (and optionally appends them to the
    /// timeline and selects the first clip).
    async void OnOpened(object? sender, EventArgs e) {
        RecentreIfOffScreen();
        opened = true;
        MaybeShowWelcome();
        UpdateMaximizedMargin();
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        // Dev flag: throw on the UI thread to exercise the global crash
        // handler end to end (log on disk, dialog, exit).
        if (args.Contains("--crash-test"))
            throw new InvalidOperationException("Deliberate crash from --crash-test.");
        // Dev flag: --update-demo <url> opens the update dialog against a
        // fake release, so the download bar can be exercised from any URL.
        if (Array.IndexOf(args, "--update-demo") is var demoAt && demoAt >= 0 &&
            args.ElementAtOrDefault(demoAt + 1) is { } demoUrl) {
            var demo = new UpdateInfo(new Version(9, 9, 9), "v9.9.9-win",
                "Demo release notes.\n\n• Download progress in this dialog\n• Later / Skip this version",
                demoUrl);
            await UpdateDialog.ShowAsync(this, demo, Close);
            return;
        }
#if !DEBUG
        updateStartup.Start();
#endif
        // Dev builds never poll for updates — a 0.1.0 dev version would
        // always "update" to the latest release and stop being the dev
        // build. --update-demo still opens the dialog on demand, and
        // --no-update stays as the belt-and-braces kill switch.
        if (args.Contains("--no-update")) {
            updateStartup.Stop();
            updateDaily.Stop();
        }
        // Dev flag: --mcp [--mcp-port N] serves the editor's tools to external
        // MCP clients (Claude Desktop et al.) on 127.0.0.1 — external mode for
        // this session only, every client pre-approved, nothing persisted.
        if (args.Contains("--mcp")) viewModel.Mcp.StartDevServer(ParseMcpPort(args));
        // Model manifest sync: data, not app updates — runs in every build,
        // quiet unless the model list actually changed.
        _ = Task.Run(() => viewModel.Media.Generate.StartupSyncAsync());
        bool addToTimeline = args.Contains("--add-to-timeline");
        foreach (var path in args.Where(a => !a.StartsWith("--") && File.Exists(a))) {
            await viewModel.Media.ImportFileAsync(Path.GetFullPath(path));
            if (addToTimeline && viewModel.Media.Items.LastOrDefault() is { } item)
                viewModel.Media.AddToTimelineCommand.Execute(item);
        }
        if (args.Contains("--select-first-clip") && viewModel.Timeline.State is { } state)
            viewModel.Timeline.SelectOnly(state.Tracks.SelectMany(t => t.Clips).FirstOrDefault()?.Id);
        // Dev flag: arm a transition across the first two timeline clips,
        // driving the still-capture path without pixel-driving the menu.
        if (args.Contains("--transition-repro") && viewModel.Timeline.State is { } reproState) {
            var pair = reproState.Tracks.SelectMany(t => t.Clips)
                                 .OrderBy(c => c.StartFrame).ToArray();
            if (pair.Length >= 2) viewModel.Timeline.RequestTransition(pair[0], pair[1]);
        }
    }

    static int ParseMcpPort(string[] args) {
        if (Array.IndexOf(args, "--mcp-port") is var portAt && portAt >= 0 &&
            int.TryParse(args.ElementAtOrDefault(portAt + 1), out int parsed) &&
            parsed is > 0 and < 65536)
            return parsed;
        return McpServer.DefaultPort;
    }

    void OnViewerTabClose(object? sender, PointerPressedEventArgs e) {
        if ((sender as Control)?.DataContext is ViewerTab tab)
            viewModel.Viewer.CloseCommand.Execute(tab);
        e.Handled = true;  // the tab button underneath must not also activate
    }

    static readonly FilePickerFileType ProjectFileType =
        new("Palmier project") { Patterns = ["*." + ProjectStore.Extension] };

    /// Rebuilt each time the menu opens, so a project deleted since last time
    /// is not offered.
    void OnFileMenuOpening(object? sender, EventArgs e) {
        var recent = RecentProjects.Load();
        RecentMenu.IsEnabled = recent.Count > 0;
        var items = new List<MenuItem>();
        foreach (string path in recent) {
            var item = new MenuItem { Header = Path.GetFileNameWithoutExtension(path) };
            ToolTip.SetTip(item, path);
            item.Click += async (_, _) => {
                if (!await ConfirmDiscardAsync()) return;
                var missing = await viewModel.OpenProjectAsync(path);
                if (missing.Count > 0) await ReportMissingAsync(missing);
            };
            items.Add(item);
        }
        RecentMenu.ItemsSource = items;
    }

    /// One click starts the rename. The press is consumed so the title bar
    /// never treats it as the start of a drag — or, doubled, a maximize.
    void OnRenameProject(object? sender, PointerPressedEventArgs e) {
        e.Handled = true;
        viewModel.BeginRenameProject();
        Dispatcher.UIThread.Post(() => {
            ProjectNameBox.Focus();
            ProjectNameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    async void OnProjectNameKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key is Key.Enter or Key.Return) {
            await viewModel.CommitRenameProjectAsync();
            e.Handled = true;
        } else if (e.Key == Key.Escape) {
            viewModel.RenamingProject = false;
            e.Handled = true;
        }
    }

    async void OnProjectNameCommitted(object? sender, RoutedEventArgs e) {
        if (viewModel.RenamingProject) await viewModel.CommitRenameProjectAsync();
    }

    async void OnNewProject(object? sender, RoutedEventArgs e) {
        if (!await ConfirmDiscardAsync()) return;
        await viewModel.NewProjectAsync();
    }

    async void OnOpenProject(object? sender, RoutedEventArgs e) {
        if (!await ConfirmDiscardAsync()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Open project", AllowMultiple = false, FileTypeFilter = [ProjectFileType],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        var missing = await viewModel.OpenProjectAsync(path);
        if (missing.Count > 0) await ReportMissingAsync(missing);
    }

    async void OnSaveProject(object? sender, RoutedEventArgs e) => await SaveAsync(askForPath: false);
    async void OnSaveProjectAs(object? sender, RoutedEventArgs e) => await SaveAsync(askForPath: true);

    async Task<bool> SaveAsync(bool askForPath) {
        string? path = askForPath ? null : viewModel.ProjectPath;
        if (path is null) {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Save project",
                SuggestedFileName = viewModel.ProjectName + "." + ProjectStore.Extension,
                DefaultExtension = ProjectStore.Extension,
                FileTypeChoices = [ProjectFileType],
            });
            path = file?.TryGetLocalPath();
        }
        if (path is null) return false;
        await viewModel.SaveProjectAsync(path);
        return true;
    }

    /// Offers a save before work is thrown away. False cancels the action.
    async Task<bool> ConfirmDiscardAsync() {
        if (!viewModel.ProjectDirty) return true;
        var choice = await MessageDialog.AskAsync(this, "Unsaved changes",
            "Save this project before continuing?", "Save", "Discard", "Cancel");
        return choice switch {
            MessageDialog.Choice.Primary => await SaveAsync(askForPath: false),
            MessageDialog.Choice.Secondary => true,
            _ => false,
        };
    }

    Task ReportMissingAsync(IReadOnlyList<string> missing) =>
        MessageDialog.AskAsync(this, "Some media was not found",
            string.Join(Environment.NewLine, missing.Take(10)) +
            (missing.Count > 10 ? $"{Environment.NewLine}…and {missing.Count - 10} more" : ""),
            "OK");

    GenerateWindow? generateWindow;

    /// Raises or closes the composer. One instance, reused: closing it from
    /// its title bar has to leave the view model agreeing that it is shut.
    void ShowGenerateWindow(bool show) {
        if (!show) {
            generateWindow?.Close();
            return;
        }
        if (generateWindow is null) {
            generateWindow = new GenerateWindow { DataContext = viewModel.Media.Generate };
            generateWindow.Closed += (_, _) => {
                generateWindow = null;
                viewModel.Media.Generate.IsOpen = false;
            };
            generateWindow.Show(this);
            return;
        }
        generateWindow.Activate();
    }

    void OnSettingsClick(object? sender, RoutedEventArgs e) => ShowSettings();

    async void OnAboutClick(object? sender, RoutedEventArgs e) {
        string version = typeof(App).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.1.0";
        await MessageDialog.AskAsync(this, $"PalmierWin {version}",
            "A Windows-native AI video editor.\n\nBuilt on the open source Palmier Pro codebase (GPLv3).",
            "OK");
    }

    void OnReportProblemClick(object? sender, RoutedEventArgs e) =>
        _ = ReportProblemDialog.ShowAsync(this);

    void OnProjectSettings(object? sender, RoutedEventArgs e) =>
        new ProjectSettingsDialog(viewModel).ShowDialog(this);

    /// `tabIndex` picks the pane: 0 Appearance, 1 AI, 2 Generation.
    public void ShowSettings(int tabIndex = 0) =>
        new SettingsWindow(viewModel, tabIndex).ShowDialog(this);

    void OnExportClick(object? sender, RoutedEventArgs e) =>
        new ExportDialog(viewModel).ShowDialog(this);

    void OnKeyDown(object? sender, KeyEventArgs e) {
        if (e.Source is TextBox) return;
        var timeline = viewModel.Timeline;
        switch (e.Key) {
            case Key.Space:
                viewModel.TogglePlayback();
                e.Handled = true;
                break;
            // JKL shuttle: reverse / stop / forward, repeats double the speed.
            case Key.J when e.KeyModifiers == KeyModifiers.None:
                viewModel.Shuttle(-1);
                e.Handled = true;
                break;
            case Key.K when e.KeyModifiers == KeyModifiers.None:
                viewModel.Shuttle(0);
                e.Handled = true;
                break;
            case Key.L when e.KeyModifiers == KeyModifiers.None:
                viewModel.Shuttle(1);
                e.Handled = true;
                break;
            // I/O mark the range, X clears it.
            case Key.I when e.KeyModifiers == KeyModifiers.None:
                timeline.MarkIn();
                e.Handled = true;
                break;
            case Key.O when e.KeyModifiers == KeyModifiers.None:
                timeline.MarkOut();
                e.Handled = true;
                break;
            case Key.X when e.KeyModifiers == KeyModifiers.None:
                timeline.ClearRange();
                e.Handled = true;
                break;
            // Ctrl+I / Ctrl+O mark the loop range (I/O alone are the delete
            // range, so the loop rides the modifier).
            case Key.I when e.KeyModifiers == KeyModifiers.Control:
                timeline.MarkLoopStart();
                e.Handled = true;
                break;
            case Key.O when e.KeyModifiers == KeyModifiers.Control:
                timeline.MarkLoopEnd();
                e.Handled = true;
                break;
            // Escape: an armed timeline gesture cancels first; nothing armed
            // means fall back to the Select tool. Dialogs and renames consume
            // their own Escape before it reaches here.
            case Key.Escape when e.KeyModifiers == KeyModifiers.None:
                if (TimelinePanelHost.CancelTimelineGesture()) {
                    e.Handled = true;
                } else if (timeline.Tool != TimelineTool.Select) {
                    timeline.Tool = TimelineTool.Select;
                    e.Handled = true;
                }
                break;
            // Shift+Delete ripples the selection, or the marked range when
            // nothing is selected; plain Delete lifts and leaves the gap.
            case Key.Delete or Key.Back
                when e.KeyModifiers.HasFlag(KeyModifiers.Shift) && timeline.SelectedClipId is { } rippled:
                timeline.RequestRippleDelete(rippled);
                e.Handled = true;
                break;
            case Key.Delete or Key.Back
                when e.KeyModifiers.HasFlag(KeyModifiers.Shift) && timeline.HasRange:
                timeline.RequestDeleteRange();
                e.Handled = true;
                break;
            case Key.Delete or Key.Back when timeline.SelectedClipId is { } selected:
                timeline.RequestDeleteClip(selected);
                e.Handled = true;
                break;
            // Both editor conventions for the blade: Ctrl+B and Ctrl+K.
            case Key.B or Key.K when e.KeyModifiers == KeyModifiers.Control:
                timeline.SplitAtPlayhead();
                e.Handled = true;
                break;
            // Q/W: trim the selection's in/out point to the playhead.
            case Key.Q when e.KeyModifiers == KeyModifiers.None:
                timeline.TrimSelectionToPlayhead(0);
                e.Handled = true;
                break;
            case Key.W when e.KeyModifiers == KeyModifiers.None:
                timeline.TrimSelectionToPlayhead(1);
                e.Handled = true;
                break;
            // Arrow keys step the playhead; Shift jumps ten frames.
            case Key.Left or Key.Right when e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift: {
                int step = (e.Key == Key.Left ? -1 : 1) * (e.KeyModifiers == KeyModifiers.Shift ? 10 : 1);
                timeline.Scrub(timeline.PlayheadFrame + step);
                e.Handled = true;
                break;
            }
            case Key.Home:
                timeline.Scrub(0);
                e.Handled = true;
                break;
            case Key.End:
                timeline.Scrub(Math.Max(0, timeline.TotalFrames - 1));
                e.Handled = true;
                break;
            case Key.C when e.KeyModifiers == KeyModifiers.Control:
                viewModel.CopySelection();
                e.Handled = true;
                break;
            case Key.X when e.KeyModifiers == KeyModifiers.Control:
                viewModel.CutSelection();
                e.Handled = true;
                break;
            case Key.V when e.KeyModifiers == KeyModifiers.Control:
                viewModel.PasteAtPlayhead();
                e.Handled = true;
                break;
            case Key.A when e.KeyModifiers == KeyModifiers.Control:
                timeline.SelectAll();
                e.Handled = true;
                break;
            case Key.S when e.KeyModifiers == KeyModifiers.Control:
                _ = SaveAsync(askForPath: false);
                e.Handled = true;
                break;
            case Key.Z when e.KeyModifiers == KeyModifiers.Control:
                viewModel.PerformUndoCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Z when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift):
            case Key.Y when e.KeyModifiers == KeyModifiers.Control:
                viewModel.PerformRedoCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
