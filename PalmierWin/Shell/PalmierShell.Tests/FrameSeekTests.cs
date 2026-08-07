using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// A seek lands on the nearest keyframe before the target, so decoding one
/// frame after it returns that keyframe — usually frame 0. Everything that
/// asks for a specific frame has to walk forward from where the seek landed.
/// When it did not, both sides of a cut came back as the same still and every
/// timeline scrub showed frame 0 while playback looked perfectly correct.
public class FrameSeekTests {
    static string TestMediaPath(string name) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    static byte[]? Extract(string media, int sourceFrame) {
        var probe = CoreApi.ProbeMedia(media);
        Assert.NotNull(probe);
        var pixels = new byte[probe!.Value.Width * probe.Value.Height * 4];
        return CoreApi.palmier_extract_frame(media, sourceFrame, 30, pixels, pixels.Length) == 1
            ? pixels
            : null;
    }

    [Fact]
    public void DifferentFramesExtractDifferentPixels() {
        string media = TestMediaPath("testsrc.mp4");
        byte[]? first = Extract(media, 0);
        byte[]? later = Extract(media, 40);
        Assert.NotNull(first);
        Assert.NotNull(later);
        Assert.False(first!.AsSpan().SequenceEqual(later), "frame 40 decoded as frame 0");
    }

    [Fact]
    public void TheSameFrameExtractsTheSamePixels() {
        string media = TestMediaPath("testsrc.mp4");
        byte[]? once = Extract(media, 25);
        byte[]? twice = Extract(media, 25);
        Assert.NotNull(once);
        Assert.True(once!.AsSpan().SequenceEqual(twice));
    }

    /// The two stills a transition travels between come from opposite ends of
    /// a cut; identical bytes there mean the model is handed the same picture
    /// twice and asked to move between them.
    [Fact]
    public void BothSidesOfACutExtractDistinctStills() {
        string media = TestMediaPath("testsrc.mp4");
        byte[]? outgoing = Extract(media, 29);
        byte[]? incoming = Extract(media, 30);
        Assert.NotNull(outgoing);
        Assert.NotNull(incoming);
        Assert.False(outgoing!.AsSpan().SequenceEqual(incoming));
    }

    [Fact]
    public void AFramePastTheEndStillReturnsSomething() {
        Assert.NotNull(Extract(TestMediaPath("testsrc.mp4"), 100_000));
    }

    /// colorbands24.mp4 is 24 fps and holds one solid colour per second, so a
    /// frame index read at the file's rate instead of the timeline's lands a
    /// quarter late — far enough to be a different colour.
    ///
    /// Frame indices everywhere in the app are timeline-domain (30 fps): clip
    /// trims and durations are converted into it. `palmier_extract_frame` used
    /// to probe the file's own rate, so every still from a clip that was not
    /// 30 fps came from the wrong time, and a generated transition was built
    /// from two shots nowhere near the cut.
    [Theory]
    [InlineData(15, "red")]       // 0.5 s
    [InlineData(45, "green")]     // 1.5 s
    [InlineData(75, "blue")]      // 2.5 s — at 24 fps this would be 3.125 s, yellow
    [InlineData(105, "yellow")]   // 3.5 s — at 24 fps this would be 4.375 s, magenta
    public void FrameIndicesAreTimelineDomainNotTheFilesOwnRate(int timelineFrame, string expected) {
        string media = TestMediaPath("colorbands24.mp4");
        byte[]? pixels = Extract(media, timelineFrame);
        Assert.NotNull(pixels);
        Assert.Equal(expected, NearestBand(pixels!, 160, 120));
    }

    /// The stills a generated transition travels between, end to end: split a
    /// 24 fps clip exactly on a colour change, then take the outgoing clip's
    /// last frame and the incoming clip's first. They must be the two colours
    /// either side of the cut. This covers the split's `trimStartFrame`, the
    /// source-frame resolution, and the extraction domain together — the user
    /// saw two stills from nowhere near the cut, and each of those steps could
    /// have caused it.
    [Fact]
    public void TransitionStillsComeFromEitherSideOfTheCut() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string media = TestMediaPath("colorbands24.mp4");
            // 150 timeline frames = 5 s of 24 fps source in the 30 fps domain.
            string clipId = CoreApi.AddClip(project, media, 150)!;
            // 3.0 s: the blue -> yellow boundary.
            string rightId = CoreApi.SplitClip(project, clipId, 90)!;

            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            var left = state.FindClip(clipId)!;
            var right = state.FindClip(rightId)!;

            Assert.Equal("blue", NearestBand(Extract(media, left.LastSourceFrame)!, 160, 120));
            Assert.Equal("yellow", NearestBand(Extract(media, right.FirstSourceFrame)!, 160, 120));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// The transition stills now come from the composited timeline, the way
    /// upstream captures them — so this covers the whole chain the user sees:
    /// split → trim bookkeeping → planner → decoder → compositor → readback.
    /// The captured colours must be the two either side of the cut.
    [Fact]
    [Trait("Category", "Hardware")]
    public void CompositedCaptureReadsEitherSideOfACut() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string media = TestMediaPath("colorbands24.mp4");
            string clipId = CoreApi.AddClip(project, media, 150)!;
            CoreApi.SplitClip(project, clipId, 90);   // the blue -> yellow boundary

            Assert.Equal("blue", CaptureBand(project, 89));
            Assert.Equal("yellow", CaptureBand(project, 90));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    static string CaptureBand(IntPtr project, int frame) {
        int needed = -CoreApi.palmier_project_capture_frame(project, frame, [], 0, out _, out _);
        Assert.True(needed > 0, $"capture at {frame} reported no size");
        var pixels = new byte[needed];
        Assert.Equal(1, CoreApi.palmier_project_capture_frame(
            project, frame, pixels, pixels.Length, out int w, out int h));
        return NearestBand(pixels, w, h);
    }

    static readonly (string Name, int R, int G, int B)[] Bands = [
        ("red", 255, 0, 0), ("green", 0, 128, 0), ("blue", 0, 0, 255),
        ("yellow", 255, 255, 0), ("magenta", 255, 0, 255),
    ];

    /// Nearest of the five band colours to the centre pixel, so yuv420p
    /// rounding does not make the assertion brittle.
    static string NearestBand(byte[] bgra, int width, int height) {
        int i = ((height / 2) * width + width / 2) * 4;
        int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
        return Bands.MinBy(c => (c.R - r) * (c.R - r) + (c.G - g) * (c.G - g) + (c.B - b) * (c.B - b)).Name;
    }
}
