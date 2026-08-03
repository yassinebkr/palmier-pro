using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalmierShell.Core;

namespace PalmierShell.ViewModels;

public sealed partial class ViewerTab : ObservableObject {
    /// The timeline tab is permanent and cannot be closed.
    public bool IsTimeline { get; init; }
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public int TotalFrames { get; init; }

    [ObservableProperty] bool isActive;

    /// Each source remembers where it was left.
    public int PlayheadFrame { get; set; }
}

/// The preview header's tab strip: the timeline plus any library media opened
/// in the source monitor. Activating a source points the core's preview and
/// audio mixer at that file; the timeline panel keeps showing the timeline.
public sealed partial class ViewerTabsViewModel : ObservableObject {
    readonly IntPtr project;

    public ObservableCollection<ViewerTab> Tabs { get; } = new();

    [ObservableProperty] ViewerTab active;
    [ObservableProperty] int sourceFrame;
    [ObservableProperty] int sourceTotalFrames;

    /// True while a source tab is active — the transport and scrub bar read
    /// the source's own position instead of the timeline playhead.
    public bool ShowingSource => !Active.IsTimeline;

    /// Raised after the active tab changes, so the engine can be retargeted.
    public event Action? ActiveChanged;

    public ViewerTabsViewModel(IntPtr project) {
        this.project = project;
        active = new ViewerTab { IsTimeline = true, Name = "Timeline", IsActive = true };
        Tabs.Add(active);
    }

    /// Opens `item` in the source monitor, reusing an existing tab for the
    /// same file. No-ops when the core cannot probe the media.
    public void Open(MediaItemViewModel item) {
        if (Tabs.FirstOrDefault(t => t.Path == item.Path) is { } existing) {
            Activate(existing);
            return;
        }
        var tab = new ViewerTab {
            Path = item.Path,
            Name = item.Name,
            TotalFrames = TimelineViewModel.TimelineFramesFor(item),
        };
        Tabs.Add(tab);
        Activate(tab);
    }

    [RelayCommand]
    public void Activate(ViewerTab? tab) {
        if (tab is null || !Tabs.Contains(tab)) return;
        Active.PlayheadFrame = Active.IsTimeline ? Active.PlayheadFrame : SourceFrame;
        if (!tab.IsTimeline) {
            int frames = CoreApi.palmier_project_set_preview_source(project, tab.Path);
            if (frames <= 0) return;  // unreadable media: stay where we are
            SourceTotalFrames = frames;
            SourceFrame = Math.Clamp(tab.PlayheadFrame, 0, Math.Max(0, frames - 1));
        } else {
            CoreApi.palmier_project_clear_preview_source(project);
        }
        foreach (var t in Tabs) t.IsActive = t == tab;
        Active = tab;
        OnPropertyChanged(nameof(ShowingSource));
        ActiveChanged?.Invoke();
    }

    [RelayCommand]
    void Close(ViewerTab? tab) {
        if (tab is null || tab.IsTimeline) return;
        bool wasActive = tab.IsActive;
        Tabs.Remove(tab);
        if (wasActive) Activate(Tabs[0]);
    }
}
