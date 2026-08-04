using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PalmierShell.ViewModels;

namespace PalmierShell.Views;

public partial class TimelinePanel : UserControl {
    public TimelinePanel() {
        InitializeComponent();
        UpdateToolButtons(TimelineTool.Select);
        DataContextChanged += (_, _) => {
            UpdateSnapButton();
            if (Vm is { } vm) {
                vm.PreferencesApplied += UpdateSnapButton;
                vm.Timeline.PropertyChanged += OnTimelinePropertyChanged;
                UpdateLoopButtons();
            }
        };
    }

    /// Tool and loop changes from outside the toolbar (Escape, Ctrl+I/O)
    /// still light the right buttons.
    void OnTimelinePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) {
        if (Vm is null) return;
        if (e.PropertyName == nameof(TimelineViewModel.Tool)) UpdateToolButtons(Vm.Timeline.Tool);
        if (e.PropertyName is nameof(TimelineViewModel.LoopStart) or nameof(TimelineViewModel.LoopEnd))
            UpdateLoopButtons();
    }

    void OnLoopStart(object? sender, RoutedEventArgs e) {
        Vm?.Timeline.MarkLoopStart();
        UpdateLoopButtons();
    }

    void OnLoopEnd(object? sender, RoutedEventArgs e) {
        Vm?.Timeline.MarkLoopEnd();
        UpdateLoopButtons();
    }

    void UpdateLoopButtons() {
        if (Vm is null) return;
        LoopStartButton.Background = Vm.Timeline.LoopStart is not null ? ActiveToolBrush : InactiveToolBrush;
        LoopEndButton.Background = Vm.Timeline.LoopEnd is not null ? ActiveToolBrush : InactiveToolBrush;
    }

    /// Escape spends itself on an armed gesture before it means "select tool".
    public bool CancelTimelineGesture() => Timeline.CancelTimelineGesture();

    void OnAddTextClip(object? sender, RoutedEventArgs e) => Vm?.AddTextClipAtPlayhead();

    void OnRemoveSilence(object? sender, RoutedEventArgs e) {
        if (Vm?.Timeline.SelectedClipId is { } id) Vm.Timeline.RequestRemoveSilence(id);
    }

    static TimelineTab? TabOf(object? sender) => (sender as Control)?.DataContext as TimelineTab;

    void OnTabTapped(object? sender, TappedEventArgs e) {
        if (TabOf(sender) is { IsRenaming: false } tab) Vm?.Tabs.SelectCommand.Execute(tab);
    }

    void OnTabDoubleTapped(object? sender, TappedEventArgs e) {
        if (TabOf(sender) is { } tab) Vm?.Tabs.BeginRenameCommand.Execute(tab);
    }

    void OnTabClose(object? sender, RoutedEventArgs e) {
        e.Handled = true;  // don't let the click also select the tab
        if (TabOf(sender) is { } tab) Vm?.Tabs.CloseCommand.Execute(tab);
    }

    void OnTabRenameKeyDown(object? sender, KeyEventArgs e) {
        if (TabOf(sender) is not { } tab) return;
        if (e.Key == Key.Enter) Vm?.Tabs.CommitRename(tab);
        else if (e.Key == Key.Escape) tab.IsRenaming = false;
    }

    void OnTabRenameLostFocus(object? sender, RoutedEventArgs e) {
        if (TabOf(sender) is { IsRenaming: true } tab) Vm?.Tabs.CommitRename(tab);
    }

    void OnSnapToggle(object? sender, RoutedEventArgs e) {
        if (Vm is null) return;
        Vm.Timeline.SnapEnabled = !Vm.Timeline.SnapEnabled;
        UpdateSnapButton();
    }

    void OnDensityToggle(object? sender, RoutedEventArgs e) {
        if (Vm is null) return;
        Vm.Timeline.CompactRows = !Vm.Timeline.CompactRows;
        DensityButton.Background = Vm.Timeline.CompactRows ? ActiveToolBrush : InactiveToolBrush;
        Timeline.InvalidateVisual();
    }

    void UpdateSnapButton() =>
        SnapButton.Background = Vm?.Timeline.SnapEnabled == true ? ActiveToolBrush : InactiveToolBrush;

    MainViewModel? Vm => DataContext as MainViewModel;

    void OnSelectTool(object? sender, RoutedEventArgs e) => SetTool(TimelineTool.Select);

    // Clicking the active blade toggles back to Select.
    void OnBladeTool(object? sender, RoutedEventArgs e) =>
        SetTool(Vm?.Timeline.Tool == TimelineTool.Blade ? TimelineTool.Select : TimelineTool.Blade);

    void SetTool(TimelineTool tool) {
        if (Vm is null) return;
        Vm.Timeline.Tool = tool;
        UpdateToolButtons(tool);
    }

    static readonly Avalonia.Media.IBrush ActiveToolBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#3A3A3A"));
    static readonly Avalonia.Media.IBrush InactiveToolBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2C2C2C"));

    void UpdateToolButtons(TimelineTool tool) {
        SelectToolButton.Background = tool == TimelineTool.Select ? ActiveToolBrush : InactiveToolBrush;
        BladeToolButton.Background = tool == TimelineTool.Blade ? ActiveToolBrush : InactiveToolBrush;
    }
}
