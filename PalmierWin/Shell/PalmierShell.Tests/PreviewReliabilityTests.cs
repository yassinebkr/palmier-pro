using PalmierShell.Core;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// Regressions for the preview player's reliability pass. Each one stands for
/// a defect that showed up as "the preview stopped working" and had no
/// automated way to notice it.
public class PreviewReliabilityTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    /// A drag delivers a move per mouse event and most land on the frame
    /// already shown. Forwarding those anyway bumped the audio mixer's seek
    /// generation faster than its feeder could refill, which is heard as the
    /// sound cutting in and out while scrubbing.
    [Fact]
    public void ScrubbingToTheSameFrameRaisesNothing() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60);
            var timeline = new TimelineViewModel(project);
            timeline.Reload();
            int seeks = 0;
            timeline.PlayheadScrubbed += _ => seeks++;

            timeline.Scrub(4);
            timeline.Scrub(4);
            timeline.Scrub(4);

            Assert.Equal(1, seeks);
            Assert.Equal(4, timeline.PlayheadFrame);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void ScrubbingToADifferentFrameStillSeeks() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60);
            var timeline = new TimelineViewModel(project);
            timeline.Reload();
            var seen = new List<int>();
            timeline.PlayheadScrubbed += seen.Add;

            timeline.Scrub(2);
            timeline.Scrub(7);
            timeline.Scrub(2);

            Assert.Equal(new[] { 2, 7, 2 }, seen);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// The playhead clamps into the timeline, and a clamped move that lands on
    /// the current frame must stay silent like any other no-op.
    [Fact]
    public void ScrubbingBelowZeroClampsAndOnlyReportsOnce() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60);
            var timeline = new TimelineViewModel(project);
            timeline.Reload();
            int seeks = 0;
            timeline.PlayheadScrubbed += _ => seeks++;

            timeline.Scrub(5);
            timeline.Scrub(-3);
            timeline.Scrub(-90);

            Assert.Equal(0, timeline.PlayheadFrame);
            Assert.Equal(2, seeks);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
