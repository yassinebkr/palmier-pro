using PalmierShell.Core;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// Ripple editing: delete-and-close-the-hole, and closing a gap outright.
/// Both must keep linked audio/video aligned — a ripple that moves the
/// picture but not its sound is worse than no ripple at all.
public class RippleEditTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    static TimelineState State(IntPtr project) =>
        TimelineState.Parse(CoreApi.GetTimelineJson(project));

    [Fact]
    public void RippleDeleteClosesTheHoleOnBothLinkedTracks() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            // testav has audio, so each add lands a linked video+audio pair.
            string a = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60)!;
            string b = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60)!;
            string c = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60)!;

            Assert.Equal(2, CoreApi.RippleDelete(project, [b]));   // clip + linked audio

            var state = State(project);
            Assert.Null(state.FindClip(b));
            Assert.Equal(60, state.FindClip(c)!.StartFrame);       // pulled left over the hole
            foreach (var track in state.Tracks) {
                foreach (var clip in track.Clips)
                    Assert.True(clip.StartFrame is 0 or 60, $"unexpected start {clip.StartFrame}");
            }
            Assert.NotNull(state.FindClip(a));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void RippleDeleteOfSeveralClipsIsOneAtomicShift() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string a = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 50)!;
            string b = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 50)!;
            string c = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 50)!;
            string d = CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 50)!;

            Assert.Equal(2, CoreApi.RippleDelete(project, [a, c]));

            var state = State(project);
            Assert.Equal(0, state.FindClip(b)!.StartFrame);
            Assert.Equal(50, state.FindClip(d)!.StartFrame);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void CloseGapPullsLaterClipsAndTheirLinkedAudioLeft() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string a = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60)!;
            string b = CoreApi.AddClipAt(project, TestMediaPath("testav.mp4"), 60, 100)!;
            var before = State(project);
            string videoTrack = before.Tracks.First(t => t.Type == "video").Id;

            Assert.Equal(1, CoreApi.palmier_timeline_close_gap(project, videoTrack, 60, 100));

            var state = State(project);
            Assert.Equal(60, state.FindClip(b)!.StartFrame);
            // The linked audio moved with it: every clip sits at 0 or 60.
            foreach (var track in state.Tracks)
                foreach (var clip in track.Clips)
                    Assert.True(clip.StartFrame is 0 or 60, $"unexpected start {clip.StartFrame}");
            Assert.NotNull(state.FindClip(a));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void CloseGapRefusesASpanThatIsNotEmpty() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60);
            var state = State(project);
            string videoTrack = state.Tracks.First(t => t.Type == "video").Id;
            Assert.Equal(0, CoreApi.palmier_timeline_close_gap(project, videoTrack, 30, 60));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// Split at playhead with nothing selected splits the clips under the
    /// playhead — but only once per link group, or the partner split would
    /// leave zero-length slivers.
    [Fact]
    public void SplitAtPlayheadSplitsUnderThePlayheadOncePerLinkGroup() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60);
            var timeline = new TimelineViewModel(project);
            timeline.Reload();
            int splits = 0;
            timeline.BladeRequested += (_, _) => splits++;

            timeline.Scrub(30);
            timeline.SplitAtPlayhead();

            Assert.Equal(1, splits);   // video+audio pair: one split request
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void SplitAtPlayheadOutsideEveryClipDoesNothing() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60);
            var timeline = new TimelineViewModel(project);
            timeline.Reload();
            int splits = 0;
            timeline.BladeRequested += (_, _) => splits++;

            timeline.Scrub(0);          // a clip's first frame is not splittable
            timeline.SplitAtPlayhead();

            Assert.Equal(0, splits);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
