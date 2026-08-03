using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalmierShell.Core;

namespace PalmierShell.ViewModels;

/// Transport bar state: play/pause, frame stepping, and the amber timecode.
/// Attached to the engine session, timeline, and viewer once the preview HWND
/// exists. Every control acts on whichever the viewer is showing — the
/// timeline, or a library file in the source monitor.
public sealed partial class TransportViewModel : ObservableObject {
    EngineSession? engine;
    TimelineViewModel? timeline;
    ViewerTabsViewModel? viewer;

    [ObservableProperty] bool playing;
    [ObservableProperty] string currentTimecode = "00:00:00:00";
    [ObservableProperty] string totalTimecode = "00:00:00:00";

    public void Attach(EngineSession session, TimelineViewModel timelineVm, ViewerTabsViewModel viewerVm) {
        engine = session;
        timeline = timelineVm;
        viewer = viewerVm;
        // Seed from the session, don't wait for a change: --autoplay starts
        // playback before this subscription exists, so the first transition is
        // never delivered and the button shows play while the timeline runs.
        Playing = session.Playing;
        session.PlayingChanged += (playing, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Playing = playing);
        timeline.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(TimelineViewModel.PlayheadFrame)
                or nameof(TimelineViewModel.State))
                UpdateTimecode();
        };
        timeline.StateReloaded += UpdateTimecode;
        viewer.PropertyChanged += (_, e) => {
            if (e.PropertyName is nameof(ViewerTabsViewModel.SourceFrame)
                or nameof(ViewerTabsViewModel.SourceTotalFrames)
                or nameof(ViewerTabsViewModel.ShowingSource))
                UpdateTimecode();
        };
        UpdateTimecode();
    }

    bool ShowingSource => viewer?.ShowingSource == true;
    int CurrentFrame => ShowingSource ? viewer!.SourceFrame : timeline?.PlayheadFrame ?? 0;
    int TotalFrames => ShowingSource ? viewer!.SourceTotalFrames : timeline?.TotalFrames ?? 0;

    /// Scrub bar under the preview. Reads whichever the viewer is showing and
    /// seeks it on drag, so the timeline can be scrubbed without reaching for
    /// the timeline panel.
    public int PositionFrames {
        get => CurrentFrame;
        set { if (value != CurrentFrame) Seek(value); }
    }

    /// Slider maximum; never 0, or the thumb has nowhere to sit.
    public int LengthFrames => Math.Max(1, TotalFrames - 1);

    void UpdateTimecode() {
        CurrentTimecode = Format(CurrentFrame);
        TotalTimecode = Format(TotalFrames);
        OnPropertyChanged(nameof(PositionFrames));
        OnPropertyChanged(nameof(LengthFrames));
    }

    static string Format(int frame) => Timecode.Format(frame, TimelineViewModel.TimelineFps);

    [RelayCommand]
    void PlayPause() {
        if (engine is null) return;
        engine.Playing = !engine.Playing;  // PlayingChanged mirrors into Playing
    }

    /// Seeks whatever the viewer is showing; the source monitor scrubs itself,
    /// the timeline goes through Scrub so audio and the engine follow.
    public void Seek(int frame) {
        int clamped = Math.Clamp(frame, 0, Math.Max(0, TotalFrames - 1));
        if (ShowingSource) viewer!.SourceFrame = clamped;
        else timeline?.Scrub(clamped);
    }

    [RelayCommand] void SkipToStart() => Seek(0);
    [RelayCommand] void SkipToEnd() => Seek(TotalFrames - 1);
    [RelayCommand] void StepBack() => Seek(CurrentFrame - 1);
    [RelayCommand] void StepForward() => Seek(CurrentFrame + 1);
}
