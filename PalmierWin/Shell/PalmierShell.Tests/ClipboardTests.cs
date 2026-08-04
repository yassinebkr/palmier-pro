using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// Clip copy/paste: the payload is a value snapshot, paste is atomic, and
/// pasted linked pairs stay linked to each other, never to their originals.
public class ClipboardTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    static TimelineState State(IntPtr project) =>
        TimelineState.Parse(CoreApi.GetTimelineJson(project));

    [Fact]
    public void PastePlacesAtTheFrameWithFreshIdsAndPreservedTrim() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 40)!;
            CoreApi.SplitClip(project, id, 25);   // gives the original a trim to preserve

            string payload = CoreApi.CopyClips(project, [id])!;
            Assert.Equal(1, CoreApi.palmier_timeline_paste(project, payload, 100));

            var state = State(project);
            var pasted = state.Tracks.SelectMany(t => t.Clips).Single(c => c.StartFrame == 100);
            Assert.NotEqual(id, pasted.Id);
            Assert.Equal(25, pasted.DurationFrames);
            Assert.Equal(0, pasted.TrimStartFrame);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void PayloadSurvivesDeletingTheOriginal() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30)!;
            string payload = CoreApi.CopyClips(project, [id])!;
            CoreApi.palmier_timeline_remove_clip(project, id);
            Assert.Empty(State(project).Tracks.SelectMany(t => t.Clips));

            Assert.Equal(1, CoreApi.palmier_timeline_paste(project, payload, 0));
            Assert.Single(State(project).Tracks.SelectMany(t => t.Clips));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void PastedLinkedPairSharesAFreshGroup() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 30)!;
            var original = State(project);
            var pairIds = original.Tracks.SelectMany(t => t.Clips).Select(c => c.Id).ToList();
            string originalGroup = original.FindClip(id)!.LinkGroupId!;

            string payload = CoreApi.CopyClips(project, pairIds)!;
            Assert.Equal(2, CoreApi.palmier_timeline_paste(project, payload, 60));

            var pasted = State(project).Tracks.SelectMany(t => t.Clips)
                .Where(c => c.StartFrame == 60).ToList();
            Assert.Equal(2, pasted.Count);
            Assert.NotNull(pasted[0].LinkGroupId);
            Assert.Equal(pasted[0].LinkGroupId, pasted[1].LinkGroupId);
            Assert.NotEqual(originalGroup, pasted[0].LinkGroupId);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void PasteOntoAnOccupiedSpanNudgesRightToFirstFreePosition() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string a = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 30)!;
            var ids = State(project).Tracks.SelectMany(t => t.Clips).Select(c => c.Id).ToList();
            string payload = CoreApi.CopyClips(project, ids)!;

            // Pasting at 10 overlaps the originals (0..30): the whole pair
            // nudges right to the first free position (30..60), linked.
            Assert.Equal(2, CoreApi.palmier_timeline_paste(project, payload, 10));

            var clips = State(project).Tracks.SelectMany(t => t.Clips).ToList();
            Assert.Equal(4, clips.Count);
            Assert.NotNull(State(project).FindClip(a));
            var pasted = clips.Where(c => c.StartFrame == 30 && c.Id != a).ToList();
            Assert.Equal(2, pasted.Count);
            Assert.Equal(pasted[0].LinkGroupId, pasted[1].LinkGroupId);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
