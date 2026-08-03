using PalmierShell.Core;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// Range delete: `[start, end)` cut out of every track — trimming, splitting
/// or removing whatever it crosses, then pulling later clips left.
public class RangeDeleteTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    static TimelineState State(IntPtr project) =>
        TimelineState.Parse(CoreApi.GetTimelineJson(project));

    [Fact]
    public void AClipSpanningTheRangeIsSplitAroundIt() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60)!;
            Assert.True(CoreApi.palmier_timeline_delete_range(project, 20, 30, 1) > 0);

            var track = State(project).Tracks.First(t => t.Clips.Count > 0);
            Assert.Equal(2, track.Clips.Count);
            var left = track.Clips[0];
            var right = track.Clips[1];
            Assert.Equal(id, left.Id);
            Assert.Equal(0, left.StartFrame);
            Assert.Equal(20, left.DurationFrames);
            // Rippled: the right half pulls back flush against the left.
            Assert.Equal(20, right.StartFrame);
            Assert.Equal(30, right.DurationFrames);
            // The right half reads source from after the removed span.
            Assert.Equal(30, right.TrimStartFrame);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void ClipsFullyInsideVanishAndStraddlersAreTrimmed() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string a = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 40)!;  // 0..40
            string b = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 20)!;  // 40..60
            string c = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 40)!;  // 60..100

            // Range 30..70: trims a's tail, removes b, trims c's head.
            Assert.True(CoreApi.palmier_timeline_delete_range(project, 30, 70, 1) > 0);

            var state = State(project);
            Assert.Equal(30, state.FindClip(a)!.DurationFrames);
            Assert.Null(state.FindClip(b));
            var tail = state.FindClip(c)!;
            Assert.Equal(30, tail.StartFrame);       // rippled back to a's new end
            Assert.Equal(30, tail.DurationFrames);
            Assert.Equal(10, tail.TrimStartFrame);   // lost its first 10 frames
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void WithoutRippleTheGapStays() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30);
            string b = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30)!;  // 30..60
            Assert.True(CoreApi.palmier_timeline_delete_range(project, 0, 30, 0) > 0);
            Assert.Equal(30, State(project).FindClip(b)!.StartFrame);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void ARangeTouchingNothingReportsZero() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30);
            Assert.Equal(0, CoreApi.palmier_timeline_delete_range(project, 100, 130, 1));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// Linked pairs split around the range stay linked: each original group's
    /// right halves share one fresh group, like a blade split.
    [Fact]
    public void SplitAroundKeepsRightHalvesLinkedAsAPair() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60);
            Assert.True(CoreApi.palmier_timeline_delete_range(project, 20, 30, 1) > 0);

            var state = State(project);
            var rights = state.Tracks.SelectMany(t => t.Clips)
                .Where(c => c.StartFrame == 20 && c.TrimStartFrame == 30).ToList();
            Assert.Equal(2, rights.Count);   // video + its audio
            Assert.NotNull(rights[0].LinkGroupId);
            Assert.Equal(rights[0].LinkGroupId, rights[1].LinkGroupId);
            var lefts = state.Tracks.SelectMany(t => t.Clips).Where(c => c.StartFrame == 0).ToList();
            Assert.NotEqual(lefts[0].LinkGroupId, rights[0].LinkGroupId);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void MarkInPastMarkOutRestartsTheRange() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 120);
            var timeline = new TimelineViewModel(project);
            timeline.Reload();          // scrub clamps to the timeline's length
            timeline.Scrub(10);
            timeline.MarkIn();
            timeline.Scrub(50);
            timeline.MarkOut();
            Assert.True(timeline.HasRange);

            timeline.Scrub(80);
            timeline.MarkIn();          // past the out point: out resets
            Assert.False(timeline.HasRange);
            Assert.Equal(80, timeline.RangeStart);
            Assert.Null(timeline.RangeEnd);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
