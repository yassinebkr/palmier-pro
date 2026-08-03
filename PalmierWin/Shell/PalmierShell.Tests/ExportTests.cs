using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class ExportTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    [Fact]
    public void ExportStart_RefusesAnEmptyTimeline() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            string path = Path.Combine(Path.GetTempPath(), $"palmier-empty-{Guid.NewGuid():N}.mp4");
            Assert.Equal(IntPtr.Zero, CoreApi.palmier_export_start(project, path));
            Assert.False(File.Exists(path));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void Export_WritesAPlayableFileAndReportsCompletion() {
        IntPtr project = CoreApi.palmier_project_create();
        string dir = Path.Combine(Path.GetTempPath(), $"palmier-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "out.mp4");
        IntPtr export = IntPtr.Zero;
        try {
            Assert.NotNull(CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 15));
            export = CoreApi.palmier_export_start(project, path);
            Assert.NotEqual(IntPtr.Zero, export);

            int status = WaitForTerminalStatus(export, TimeSpan.FromMinutes(2));
            Assert.True(status == 101, $"export did not succeed: {CoreApi.GetExportError(export)}");
            Assert.True(new FileInfo(path).Length > 1024);

            var probe = CoreApi.ProbeMedia(path);
            Assert.True(probe.HasValue, "the exported file could not be probed");
            Assert.Equal(1920, probe!.Value.Width);
            Assert.Equal(1080, probe.Value.Height);
        } finally {
            if (export != IntPtr.Zero) CoreApi.palmier_export_destroy(export);
            CoreApi.palmier_project_destroy(project);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Export_CanBeCancelledMidRunAndDeletesThePartialFile() {
        IntPtr project = CoreApi.palmier_project_create();
        string dir = Path.Combine(Path.GetTempPath(), $"palmier-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "out.mp4");
        IntPtr export = IntPtr.Zero;
        try {
            // Long enough that a cancel always lands mid-run.
            Assert.NotNull(CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30000));
            export = CoreApi.palmier_export_start(project, path);
            Assert.NotEqual(IntPtr.Zero, export);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (CoreApi.palmier_export_status(export) == 0 && DateTime.UtcNow < deadline)
                Thread.Sleep(20);
            Assert.True(CoreApi.palmier_export_status(export) is > 0 and <= 100,
                "export did not reach mid-run progress");

            Assert.Equal(1, CoreApi.palmier_export_cancel(export));
            Assert.Equal(-2, WaitForTerminalStatus(export, TimeSpan.FromMinutes(1)));
            Assert.False(File.Exists(path), "a cancelled export must not leave a partial file");
        } finally {
            if (export != IntPtr.Zero) CoreApi.palmier_export_destroy(export);
            CoreApi.palmier_project_destroy(project);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// Polls until the status leaves the 0–100 running range.
    static int WaitForTerminalStatus(IntPtr export, TimeSpan timeout) {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline) {
            int status = CoreApi.palmier_export_status(export);
            if (status is < 0 or > 100) return status;
            Thread.Sleep(50);
        }
        return -1;
    }
}
