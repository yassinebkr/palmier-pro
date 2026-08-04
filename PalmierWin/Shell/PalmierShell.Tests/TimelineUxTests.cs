using PalmierShell.Core;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// Pure timeline UX math: anchored zoom, overview mapping, waveform levels,
/// playback advance, and the loop points' toggle semantics.
public class TimelineUxTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    [Fact]
    public void AnchoredZoom_KeepsVisiblePlayheadAtSameScreenX() {
        double scroll = TimelineMath.AnchoredZoomScroll(4, 8, 0, 800, 100);
        Assert.Equal(400, scroll, 6);
        Assert.Equal(400, 100 * 8 - scroll, 6);  // playhead still at x 400
    }

    [Fact]
    public void AnchoredZoom_AnchorsCentreWhenPlayheadIsScrolledAway() {
        double scroll = TimelineMath.AnchoredZoomScroll(4, 8, 1000, 800, 0);
        Assert.Equal(350 * 8 - 400, scroll, 6);
    }

    [Fact]
    public void AnchoredZoom_NeverScrollsNegative() {
        Assert.Equal(0, TimelineMath.AnchoredZoomScroll(4, 0.5, 10, 800, 2));
    }

    [Fact]
    public void ScrollCentering_ClampsToContentEnds() {
        Assert.Equal(0, TimelineMath.ScrollCentering(0, 4, 800, 1000));
        Assert.Equal(3200, TimelineMath.ScrollCentering(1000, 4, 800, 1000));
        Assert.Equal(1600, TimelineMath.ScrollCentering(500, 4, 800, 1000));
    }

    [Fact]
    public void OverviewViewport_MapsTheVisibleRangeIntoStripCoordinates() {
        var (x, w) = TimelineMath.OverviewViewport(400, 4, 800, 1000, 500);
        Assert.Equal(50, x, 6);    // 100 of 1000 frames → 10% of the strip
        Assert.Equal(100, w, 6);   // 200 of 1000 frames → 20% of the strip
    }

    [Fact]
    public void OverviewViewport_ClampsWhenScrolledToTheEnd() {
        var (x, w) = TimelineMath.OverviewViewport(100000, 4, 800, 1000, 500);
        Assert.Equal(500, x + w, 6);
    }

    [Fact]
    public void OverviewViewport_CoversEverythingWhenZoomedToFit() {
        var (x, w) = TimelineMath.OverviewViewport(0, 0.5, 500, 1000, 500);
        Assert.Equal(0, x, 6);
        Assert.Equal(500, w, 6);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(0.0562341f, 0.5f)]   // -25 dB lands mid-window
    [InlineData(0.001f, 0f)]         // below the -50 dB floor
    public void WaveformLevel_MapsDbOverAFixedWindow(float peak, float expected) =>
        Assert.Equal(expected, TimelineMath.WaveformLevel(peak), 3);

    [Fact]
    public void AdvancePlayhead_NormalStep() =>
        Assert.Equal((6, false, false), TimelineMath.AdvancePlayhead(5, 1, 100, -1, -1));

    [Fact]
    public void AdvancePlayhead_NormalPlayWrapsAtTheEnd() =>
        Assert.Equal((0, true, false), TimelineMath.AdvancePlayhead(99, 1, 100, -1, -1));

    [Fact]
    public void AdvancePlayhead_ShuttleStopsAtTheEnds() {
        Assert.Equal((99, false, true), TimelineMath.AdvancePlayhead(99, 4, 100, -1, -1));
        Assert.Equal((0, false, true), TimelineMath.AdvancePlayhead(0, -1, 100, -1, -1));
    }

    [Fact]
    public void AdvancePlayhead_LoopsWhenTheTickCrossesLoopEnd() {
        Assert.Equal((10, true, false), TimelineMath.AdvancePlayhead(49, 1, 100, 10, 50));
        Assert.Equal((10, true, false), TimelineMath.AdvancePlayhead(48, 4, 100, 10, 50));
    }

    [Fact]
    public void AdvancePlayhead_LoopDoesNotFireBeforeEndOrOutsideRange() {
        Assert.Equal((49, false, false), TimelineMath.AdvancePlayhead(48, 1, 100, 10, 50));
        Assert.Equal((61, false, false), TimelineMath.AdvancePlayhead(60, 1, 100, 10, 50));
    }

    [Fact]
    public void AdvancePlayhead_IncompleteLoopFallsBackToEndWrap() =>
        Assert.Equal((0, true, false), TimelineMath.AdvancePlayhead(99, 1, 100, 50, -1));

    /// Loop point semantics on the real view model: toggling, invalidation,
    /// and HasLoop. Needs one clip so the playhead can leave frame 0.
    [Fact]
    public void LoopPoints_ToggleAndInvalidateLikeTheDeleteRange() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            var vm = new TimelineViewModel(project);
            Assert.NotNull(vm.AddClip(TestMediaPath("testsrc.mp4"), 300));

            vm.Scrub(30);
            vm.MarkLoopStart();
            Assert.Equal(30, vm.LoopStart);
            Assert.False(vm.HasLoop);

            vm.Scrub(60);
            vm.MarkLoopEnd();
            Assert.Equal(60, vm.LoopEnd);
            Assert.True(vm.HasLoop);

            // A start past the end drops the end.
            vm.Scrub(90);
            vm.MarkLoopStart();
            Assert.Equal(90, vm.LoopStart);
            Assert.Null(vm.LoopEnd);
            Assert.False(vm.HasLoop);

            // Marking where the mark already is clears it.
            vm.MarkLoopStart();
            Assert.Null(vm.LoopStart);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
