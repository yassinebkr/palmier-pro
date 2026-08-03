using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class PreviewSourceTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    [Fact]
    public void SetPreviewSource_ReturnsTheSourceLengthInTimelineFrames() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            int frames = CoreApi.palmier_project_set_preview_source(project, TestMediaPath("testsrc.mp4"));
            Assert.True(frames > 0);
            var probe = CoreApi.ProbeMedia(TestMediaPath("testsrc.mp4"));
            Assert.True(probe.HasValue);
            int expected = (int)Math.Round(probe!.Value.TotalFrames / probe.Value.Fps * 30);
            Assert.InRange(frames, expected - 1, expected + 1);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void PreviewSource_DoesNotShowUpInTheTimelineSnapshot() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60);
            string before = CoreApi.GetTimelineJson(project);

            Assert.True(CoreApi.palmier_project_set_preview_source(project, TestMediaPath("testav.mp4")) > 0);
            Assert.Equal(before, CoreApi.GetTimelineJson(project));

            CoreApi.palmier_project_clear_preview_source(project);
            Assert.Equal(before, CoreApi.GetTimelineJson(project));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void EditsWhileAPreviewSourceIsSet_StillTargetTheTimeline() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            Assert.True(CoreApi.palmier_project_set_preview_source(project, TestMediaPath("testav.mp4")) > 0);
            Assert.NotNull(CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 45));
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.Equal(45, state.TotalFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void SetPreviewSource_RejectsAFileItCannotProbe() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string missing = Path.Combine(Path.GetTempPath(), $"palmier-missing-{Guid.NewGuid():N}.mp4");
            Assert.Equal(0, CoreApi.palmier_project_set_preview_source(project, missing));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
