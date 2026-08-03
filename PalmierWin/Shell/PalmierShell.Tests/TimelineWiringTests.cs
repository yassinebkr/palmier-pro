using PalmierShell.Core;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// Covers the seam the timeline view drives: the view raises a request, the
/// handler applies it to the core through the undo stack. The pointer handling
/// above this line still needs a human.
public class TimelineWiringTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    /// Mirrors how MainViewModel binds the timeline's request events.
    sealed class Harness : IDisposable {
        public IntPtr Project { get; } = CoreApi.palmier_project_create();
        public TimelineViewModel Timeline { get; }
        public UndoStack Undo { get; }

        public Harness() {
            Timeline = new TimelineViewModel(Project);
            Undo = new UndoStack(Timeline.CaptureSnapshot, Timeline.RestoreSnapshot);
            Timeline.RollRequested += (l, r, b) => {
                Undo.Execute("Roll Edit", () => CoreApi.palmier_timeline_roll_edit(Project, l, r, b) >= 0);
                Timeline.Reload();
            };
            Timeline.LinkChangeRequested += (id, _) => {
                Undo.Execute("Unlink Audio", () => CoreApi.palmier_clip_unlink(Project, id) == 1);
                Timeline.Reload();
            };
            Timeline.AddTrackRequested += kind => {
                Undo.Execute("Add Track", () => CoreApi.AddTrack(Project, kind) is not null);
                Timeline.Reload();
            };
        }

        public TimelineState State => TimelineState.Parse(CoreApi.GetTimelineJson(Project));
        public void Dispose() => CoreApi.palmier_project_destroy(Project);
    }

    [Fact]
    public void RollRequest_MovesTheCutAndIsUndoable() {
        using var h = new Harness();
        string whole = CoreApi.AddClip(h.Project, TestMediaPath("testsrc.mp4"), 60)!;
        string right = CoreApi.SplitClip(h.Project, whole, 30)!;
        h.Timeline.Reload();

        h.Timeline.RequestRoll(whole, right, 20);
        Assert.Equal(20, h.State.Tracks.SelectMany(t => t.Clips)
                                       .Single(c => c.Id == whole).DurationFrames);

        Assert.True(h.Undo.CanUndo);
        h.Undo.Undo();
        Assert.Equal(30, h.State.Tracks.SelectMany(t => t.Clips)
                                       .Single(c => c.Id == whole).DurationFrames);
    }

    [Fact]
    public void RollRequest_ThatCannotMove_CreatesNoUndoEntry() {
        using var h = new Harness();
        // Two fresh clips: neither has spare source, so the roll is a no-op.
        string a = CoreApi.AddClip(h.Project, TestMediaPath("testsrc.mp4"), 30)!;
        string b = CoreApi.AddClipAt(h.Project, TestMediaPath("testsrc.mp4"), 30, 30)!;
        h.Timeline.Reload();

        h.Timeline.RequestRoll(a, b, 10);
        Assert.False(h.Undo.CanUndo);
    }

    [Fact]
    public void UnlinkRequest_BreaksTheLinkAndIsUndoable() {
        using var h = new Harness();
        CoreApi.AddClip(h.Project, TestMediaPath("testav.mp4"), 60);
        h.Timeline.Reload();
        string video = h.State.Tracks.SelectMany(t => t.Clips).First(c => c.MediaType == "video").Id;

        h.Timeline.RequestUnlink(video);
        Assert.All(h.State.Tracks.SelectMany(t => t.Clips), c => Assert.Null(c.LinkGroupId));

        h.Undo.Undo();
        Assert.All(h.State.Tracks.SelectMany(t => t.Clips), c => Assert.NotNull(c.LinkGroupId));
    }

    [Fact]
    public void AddTrackRequest_AppearsInTheSnapshotAndUndoes() {
        using var h = new Harness();
        h.Timeline.RequestAddTrack("video");
        Assert.Equal(3, h.State.Tracks.Count);

        h.Undo.Undo();
        Assert.Equal(2, h.State.Tracks.Count);
    }
}
