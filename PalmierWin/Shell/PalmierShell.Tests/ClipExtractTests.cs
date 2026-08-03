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
}
