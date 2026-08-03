using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class RenderSizeTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    [Fact]
    public void RenderSize_DefaultsTo1920x1080() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            Assert.Equal(1, CoreApi.palmier_project_render_size(project, out int w, out int h));
            Assert.Equal(1920, w);
            Assert.Equal(1080, h);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void RenderSize_ReflectsInProjectJsonAndRoundTrips() {
        IntPtr project = CoreApi.palmier_project_create();
        string saved;
        try {
            Assert.Equal(1, CoreApi.palmier_project_set_render_size(project, 1080, 1920));
            Assert.Equal(1, CoreApi.palmier_project_render_size(project, out int w, out int h));
            Assert.Equal(1080, w);
            Assert.Equal(1920, h);
            saved = CoreApi.GetProjectJson(project);
            Assert.Contains("\"renderWidth\":1080", saved);
            Assert.Contains("\"renderHeight\":1920", saved);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }

        IntPtr restored = CoreApi.palmier_project_create();
        try {
            Assert.Equal(1, CoreApi.palmier_project_load_json(restored, saved));
            Assert.Equal(1, CoreApi.palmier_project_render_size(restored, out int w, out int h));
            Assert.Equal(1080, w);
            Assert.Equal(1920, h);
        } finally {
            CoreApi.palmier_project_destroy(restored);
        }
    }

    [Fact]
    public void LoadProjectJson_WithoutRenderSize_FallsBackToDefault() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            Assert.Equal(1, CoreApi.palmier_project_set_render_size(project, 1080, 1080));
            // A project saved before render sizes existed carries no keys.
            const string legacy = "{\"timelines\":[{\"fps\":30,\"width\":1920,\"height\":1080," +
                                  "\"tracks\":[{\"type\":\"video\"},{\"type\":\"audio\"}]}]," +
                                  "\"activeIndex\":0}";
            Assert.Equal(1, CoreApi.palmier_project_load_json(project, legacy));
            Assert.Equal(1, CoreApi.palmier_project_render_size(project, out int w, out int h));
            Assert.Equal(1920, w);
            Assert.Equal(1080, h);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Theory]
    [InlineData(1081, 1920)]   // odd width
    [InlineData(1080, 1921)]   // odd height
    [InlineData(0, 1080)]
    [InlineData(1920, -2)]
    [InlineData(14, 1080)]     // below the floor
    [InlineData(1920, 14)]
    [InlineData(7682, 1080)]   // above the ceiling
    [InlineData(1920, 7682)]
    public void SetRenderSize_RejectsInvalidSizesWithoutTouchingTheProject(int width, int height) {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string before = CoreApi.GetProjectJson(project);
            Assert.Equal(0, CoreApi.palmier_project_set_render_size(project, width, height));
            Assert.Equal(before, CoreApi.GetProjectJson(project));
            Assert.Equal(1, CoreApi.palmier_project_render_size(project, out int w, out int h));
            Assert.Equal(1920, w);
            Assert.Equal(1080, h);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void Export_CompositesAtTheProjectRenderSize() {
        IntPtr project = CoreApi.palmier_project_create();
        string dir = Path.Combine(Path.GetTempPath(), $"palmier-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "out.mp4");
        IntPtr export = IntPtr.Zero;
        try {
            Assert.NotNull(CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 15));
            Assert.Equal(1, CoreApi.palmier_project_set_render_size(project, 1080, 1920));
            export = CoreApi.palmier_export_start(project, path);
            Assert.NotEqual(IntPtr.Zero, export);

            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
            int status;
            while ((status = CoreApi.palmier_export_status(export)) is >= 0 and <= 100
                   && DateTime.UtcNow < deadline)
                Thread.Sleep(50);
            Assert.True(status == 101, $"export did not succeed: {CoreApi.GetExportError(export)}");

            var probe = CoreApi.ProbeMedia(path);
            Assert.True(probe.HasValue, "the exported file could not be probed");
            Assert.Equal(1080, probe!.Value.Width);
            Assert.Equal(1920, probe.Value.Height);
        } finally {
            if (export != IntPtr.Zero) CoreApi.palmier_export_destroy(export);
            CoreApi.palmier_project_destroy(project);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
