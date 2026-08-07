using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// Video context extraction: a clip's head/tail leaves as its own playable
/// file, cut in the source domain through the clip's own trim/speed mapping.
public class ClipExtractTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    [Fact]
    public void ExtractsTheHeadOfAClipToAPlayableFile() {
        IntPtr project = CoreApi.palmier_project_create();
        string? extracted = null;
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60)!;
            var clip = TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(id)!;

            extracted = ClipExtract.SaveHead(clip, frames: 30, fps: 30);
            Assert.NotNull(extracted);
            var probe = CoreApi.ProbeMedia(extracted!);
            Assert.NotNull(probe);
            double seconds = probe!.Value.Fps > 0
                ? probe.Value.TotalFrames / probe.Value.Fps : 0;
            Assert.InRange(seconds, 0.6, 1.6);
        } finally {
            CoreApi.palmier_project_destroy(project);
            if (extracted is not null) File.Delete(extracted);
        }
    }

    [Fact]
    public void AZeroLengthRangeRefusesInsteadOfSpawningFfmpeg() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60)!;
            var clip = TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(id)!;
            Assert.Null(ClipExtract.SaveHead(clip, frames: 0, fps: 30));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    /// Five seconds of frames past a shorter clip clamps to the whole used
    /// range rather than reading before the clip starts.
    [Fact]
    public void ATailLongerThanTheClipReadsTheWholeUsedRange() {
        IntPtr project = CoreApi.palmier_project_create();
        string? extracted = null;
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 30)!;
            var clip = TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(id)!;

            extracted = ClipExtract.SaveTail(clip, frames: 150, fps: 30);
            Assert.NotNull(extracted);
            AssertInSeconds(extracted!, 0.6, 1.6);
        } finally {
            CoreApi.palmier_project_destroy(project);
            if (extracted is not null) File.Delete(extracted);
        }
    }

    /// The tail is cut in the source domain through the clip's own speed
    /// mapping: sixty timeline frames at 2x consume four seconds of source.
    [Fact]
    public void ASpedUpClipsTailSpansTheSourceDomain() {
        IntPtr project = CoreApi.palmier_project_create();
        string? extracted = null;
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("longtest.mp4"), 60)!;
            Assert.Equal(1, CoreApi.palmier_clip_set_speed(project, id, 2.0));
            var clip = TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(id)!;

            extracted = ClipExtract.SaveTail(clip, frames: 150, fps: 30);
            Assert.NotNull(extracted);
            AssertInSeconds(extracted!, 3.4, 4.6);
        } finally {
            CoreApi.palmier_project_destroy(project);
            if (extracted is not null) File.Delete(extracted);
        }
    }

    /// A head trim narrows the used range the tail is taken from: the
    /// trimmed clip yields its thirty surviving frames, not the original
    /// sixty.
    [Fact]
    public void AHeadTrimNarrowsTheTailToTheUsedRange() {
        IntPtr project = CoreApi.palmier_project_create();
        string? extracted = null;
        try {
            string id = CoreApi.AddClip(project, TestMediaPath("testav.mp4"), 60)!;
            Assert.Equal(1, CoreApi.palmier_clip_trim(project, id, edge: 0, boundaryFrame: 30));
            var clip = TimelineState.Parse(CoreApi.GetTimelineJson(project)).FindClip(id)!;
            Assert.Equal(30, clip.DurationFrames);

            extracted = ClipExtract.SaveTail(clip, frames: 150, fps: 30);
            Assert.NotNull(extracted);
            AssertInSeconds(extracted!, 0.6, 1.6);
        } finally {
            CoreApi.palmier_project_destroy(project);
            if (extracted is not null) File.Delete(extracted);
        }
    }

    static void AssertInSeconds(string path, double min, double max) {
        var probe = CoreApi.ProbeMedia(path);
        Assert.NotNull(probe);
        double seconds = probe!.Value.Fps > 0 ? probe.Value.TotalFrames / probe.Value.Fps : 0;
        Assert.InRange(seconds, min, max);
    }
}
