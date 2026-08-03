using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalmierShell.Core;

namespace PalmierShell.ViewModels;

public sealed partial class TimelineTab : ObservableObject {
    [ObservableProperty] int index;
    [ObservableProperty] string name = "";
    [ObservableProperty] bool isActive;
    [ObservableProperty] bool isRenaming;
    [ObservableProperty] string editName = "";

    /// Each tab remembers where its playhead was, so switching back resumes
    /// where the edit was left.
    public int PlayheadFrame { get; set; }
}

/// The timeline tab strip. The core owns the timeline list and which one is
/// active; every other intent targets the active timeline, so switching tabs
/// is all that is needed to retarget edits, playback, audio, and export.
public sealed partial class TimelineTabsViewModel : ObservableObject {
    readonly IntPtr project;
    readonly TimelineViewModel timeline;

    public ObservableCollection<TimelineTab> Tabs { get; } = new();
    public int ActiveIndex { get; private set; }
    public bool CanClose => Tabs.Count > 1;

    /// Raised after a timeline is removed, with the index it occupied.
    public event Action<int>? TimelineClosed;

    public TimelineTabsViewModel(IntPtr project, TimelineViewModel timeline) {
        this.project = project;
        this.timeline = timeline;
        Reload();
    }

    /// Rebuilds the strip from the core — also how opening a project picks up
    /// its timelines.
    public void Reload() {
        var playheads = Tabs.ToDictionary(t => t.Index, t => t.PlayheadFrame);
        Tabs.Clear();
        int count = CoreApi.palmier_project_timeline_count(project);
        for (int i = 0; i < count; i++)
            Tabs.Add(new TimelineTab {
                Index = i,
                Name = CoreApi.TimelineName(project, i),
                PlayheadFrame = playheads.GetValueOrDefault(i),
            });
        ActiveIndex = Math.Max(0, CoreApi.palmier_project_active_timeline(project));
        foreach (var tab in Tabs) tab.IsActive = tab.Index == ActiveIndex;
        OnPropertyChanged(nameof(CanClose));
    }

    /// Switches the core's active timeline and reloads every derived surface.
    /// Returns false only when the index does not exist.
    public bool Activate(int index) {
        if (index == ActiveIndex && Tabs.Any(t => t.Index == index)) return true;
        if (Tabs.FirstOrDefault(t => t.Index == ActiveIndex) is { } leaving)
            leaving.PlayheadFrame = timeline.PlayheadFrame;
        if (CoreApi.palmier_project_set_active_timeline(project, index) != 1) return false;
        ActiveIndex = index;
        foreach (var tab in Tabs) tab.IsActive = tab.Index == index;
        timeline.SelectedClipId = null;
        timeline.Reload();
        timeline.Scrub(Tabs.FirstOrDefault(t => t.Index == index)?.PlayheadFrame ?? 0);
        return true;
    }

    [RelayCommand]
    void Select(TimelineTab? tab) {
        if (tab is not null) Activate(tab.Index);
    }

    [RelayCommand]
    void Add() {
        if (CoreApi.palmier_project_add_timeline(project, "") < 0) return;
        Reload();
        timeline.SelectedClipId = null;
        timeline.Reload();
        timeline.Scrub(0);
    }

    /// Closing is not undoable — a closed timeline takes its history with it.
    [RelayCommand]
    void Close(TimelineTab? tab) {
        if (tab is null || Tabs.Count <= 1) return;
        int removed = tab.Index;
        if (CoreApi.palmier_project_remove_timeline(project, removed) != 1) return;
        Reload();
        TimelineClosed?.Invoke(removed);
        timeline.SelectedClipId = null;
        timeline.Reload();
        timeline.Scrub(Tabs.FirstOrDefault(t => t.Index == ActiveIndex)?.PlayheadFrame ?? 0);
    }

    [RelayCommand]
    void BeginRename(TimelineTab? tab) {
        if (tab is null) return;
        tab.EditName = tab.Name;
        tab.IsRenaming = true;
    }

    public void CommitRename(TimelineTab tab) {
        tab.IsRenaming = false;
        string name = tab.EditName.Trim();
        if (name.Length == 0 || name == tab.Name) return;
        if (CoreApi.palmier_project_rename_timeline(project, tab.Index, name) == 1) tab.Name = name;
    }
}
