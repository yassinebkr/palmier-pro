using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// Silence removal: detection against a fixture with a known loud/silent
/// structure, source→timeline range math, and the ripple-cut apply path.
public class SilenceRemovalTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    static TimelineState State(IntPtr project) =>
        TimelineState.Parse(CoreApi.GetTimelineJson(project));

    static ClipState Clip(int start, int duration, int trim, double speed) =>
        new("c", "media.mp4", "video", start, duration, trim, speed, 1.0, 1.0, 0, 0,
            new TransformState(0.5, 0.5, 1, 1, 0), null, null, null, null, null, null, null);

    [Fact]
    public void DetectsTheSilentMiddle() {
        var ranges = CoreApi.DetectSilence(TestMediaPath("silence.mp4"), -40, 500, 100);
        var range = Assert.Single(ranges!);
        // 2s of silence starting at 1s, shrunk by 100ms of padding per side.
        Assert.InRange(range.StartMs, 1000, 1300);
        Assert.InRange(range.EndMs, 2700, 3000);
    }

    [Fact]
    public void AMinimumLongerThanTheSilenceFindsNothing() {
        var ranges = CoreApi.DetectSilence(TestMediaPath("silence.mp4"), -40, 5000, 100);
        Assert.Empty(ranges!);
    }

    [Fact]
    public void AFileWithoutAudioReportsNull() {
        Assert.Null(CoreApi.DetectSilence(TestMediaPath("testsrc.mp4"), -40, 500, 100));
    }

    [Fact]
    public void TimelineRangesRespectTheHeadTrim() {
        // Trimmed 1s (30 source frames) in: source 2s..3s lands at 1s..2s of
        // the clip, which starts at frame 100.
        var spans = SilenceRemoval.TimelineRanges(Clip(100, 90, 30, 1.0),
            [new SilentRange(2000, 3000)], 30);
        Assert.Equal([(130, 160)], spans);
    }

    [Fact]
    public void TimelineRangesRespectSpeed() {
        // 2× playback halves the timeline span a source range occupies.
        var spans = SilenceRemoval.TimelineRanges(Clip(0, 60, 0, 2.0),
            [new SilentRange(2000, 3000)], 30);
        Assert.Equal([(30, 45)], spans);
    }

    [Fact]
    public void RangesOutsideTheClipAreClippedOrDropped() {
        var spans = SilenceRemoval.TimelineRanges(Clip(0, 60, 0, 1.0),
            [new SilentRange(1000, 5000), new SilentRange(9000, 9500)], 30);
        Assert.Equal([(30, 60)], spans);
    }

    [Fact]
    public void ApplyingCutsTheSilenceAcrossLinkedTracks() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("silence.mp4"), 120)!;
            var ranges = CoreApi.DetectSilence(TestMediaPath("silence.mp4"), -40, 500, 100)!;
            var clip = State(project).FindClip(id)!;
            var spans = SilenceRemoval.TimelineRanges(clip, ranges, 30);
            Assert.Single(spans);

            Assert.True(SilenceRemoval.Apply(project, spans) > 0);

            var state = State(project);
            var video = state.Tracks.First(t => t.Type == "video");
            var audio = state.Tracks.First(t => t.Type == "audio");
            // The clip splits around the cut; the right half ripples flush.
            Assert.Equal(2, video.Clips.Count);
            Assert.Equal(video.Clips[0].EndFrame, video.Clips[1].StartFrame);
            Assert.Equal(120 - (spans[0].End - spans[0].Start),
                         video.Clips.Sum(c => c.DurationFrames));
            // The linked audio is cut at the same times by the range delete.
            Assert.Equal(2, audio.Clips.Count);
            Assert.Equal(video.Clips[1].StartFrame, audio.Clips[1].StartFrame);
            Assert.Equal(video.Clips[1].DurationFrames, audio.Clips[1].DurationFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void OneApplyIsOneUndoEntry() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("silence.mp4"), 120);
            var undo = new UndoStack(
                () => CoreApi.GetTimelineJson(project),
                json => CoreApi.palmier_timeline_load_json(project, json) == 1);
            var clip = State(project).Tracks.SelectMany(t => t.Clips)
                .First(c => c.MediaType == "video");
            var ranges = CoreApi.DetectSilence(TestMediaPath("silence.mp4"), -40, 500, 100)!;
            var spans = SilenceRemoval.TimelineRanges(clip, ranges, 30);

            undo.Execute("Remove Silence", () => SilenceRemoval.Apply(project, spans) > 0);

            undo.Undo();
            Assert.False(undo.CanUndo);
            var video = State(project).Tracks.First(t => t.Type == "video");
            var restored = Assert.Single(video.Clips);
            Assert.Equal(120, restored.DurationFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
