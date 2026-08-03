using PalmierShell.Core;
using PalmierShell.Core.Generation;
using Xunit;

namespace PalmierShell.Tests;

public class ProjectFileTests : IDisposable {
    readonly string dir = Path.Combine(Path.GetTempPath(), $"palmier-proj-{Guid.NewGuid():N}");

    public ProjectFileTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, recursive: true); } catch { } }

    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    [Fact]
    public void ProjectJson_RoundTripsEveryTimelineAndTheActiveOne() {
        IntPtr project = CoreApi.palmier_project_create();
        string saved;
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 45);
            CoreApi.palmier_project_add_timeline(project, "Second");
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 20);
            saved = CoreApi.GetProjectJson(project);
            Assert.NotEqual("", saved);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }

        IntPtr restored = CoreApi.palmier_project_create();
        try {
            Assert.Equal(1, CoreApi.palmier_project_load_json(restored, saved));
            Assert.Equal(2, CoreApi.palmier_project_timeline_count(restored));
            Assert.Equal(1, CoreApi.palmier_project_active_timeline(restored));
            Assert.Equal("Second", CoreApi.TimelineName(restored, 1));
            Assert.Equal(20, TimelineState.Parse(CoreApi.GetTimelineJson(restored)).TotalFrames);

            CoreApi.palmier_project_set_active_timeline(restored, 0);
            Assert.Equal(45, TimelineState.Parse(CoreApi.GetTimelineJson(restored)).TotalFrames);
        } finally {
            CoreApi.palmier_project_destroy(restored);
        }
    }

    [Fact]
    public void LoadProjectJson_RejectsRubbishWithoutWreckingTheProject() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 30);
            Assert.Equal(0, CoreApi.palmier_project_load_json(project, "not json"));
            Assert.Equal(0, CoreApi.palmier_project_load_json(project, "{\"timelines\":[]}"));
            Assert.Equal(30, TimelineState.Parse(CoreApi.GetTimelineJson(project)).TotalFrames);
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }

    [Fact]
    public void ProjectDocument_SurvivesSaveAndLoad() {
        string path = Path.Combine(dir, "demo.palmier");
        var document = new ProjectDocument(ProjectDocument.CurrentVersion, "{\"timelines\":[]}") {
            Media = [new SavedMedia(@"C:\clips\a.mp4", "Library"),
                     new SavedMedia(@"C:\clips\b.mp4", "B-Roll")],
            Folders = ["Library", "B-Roll"],
        };
        ProjectStore.Save(path, document);

        var loaded = ProjectStore.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal(document.Core, loaded!.Core);
        Assert.Equal(2, loaded.Media.Count);
        Assert.Equal("B-Roll", loaded.Media[1].Folder);
        Assert.Equal(["Library", "B-Roll"], loaded.Folders);
    }

    [Fact]
    public void Load_RefusesAProjectFromANewerBuild() {
        string path = Path.Combine(dir, "future.palmier");
        ProjectStore.Save(path, new ProjectDocument(ProjectDocument.CurrentVersion + 1, "{}"));
        Assert.Null(ProjectStore.Load(path));
    }

    [Fact]
    public void Load_ReturnsNullRatherThanThrowingOnGarbage() {
        string path = Path.Combine(dir, "broken.palmier");
        File.WriteAllText(path, "{ this is not json");
        Assert.Null(ProjectStore.Load(path));
        Assert.Null(ProjectStore.Load(Path.Combine(dir, "missing.palmier")));
    }

    [Fact]
    public void Save_LeavesNoStagingFileBehind() {
        string path = Path.Combine(dir, "clean.palmier");
        ProjectStore.Save(path, new ProjectDocument(ProjectDocument.CurrentVersion, "{}"));
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".saving"));
    }

    [Fact]
    public void GenerationRecord_RoundTripsThroughItsSidecar() {
        string media = Path.Combine(dir, "clip.mp4");
        File.WriteAllText(media, "not really a video");
        var record = new GenerationRecord("Replicate", "bytedance/seedance-2.0",
            "zoom into the left eye", 5, DateTime.UtcNow.ToString("O")) {
            FirstFrame = @"C:\frames\a.png",
            LastFrame = @"C:\frames\b.png",
        };
        GenerationRecord.Write(media, record);

        var read = GenerationRecord.Read(media);
        Assert.NotNull(read);
        Assert.Equal("bytedance/seedance-2.0", read!.Model);
        Assert.Equal("zoom into the left eye", read.Prompt);
        Assert.Equal(@"C:\frames\b.png", read.LastFrame);
        Assert.Null(GenerationRecord.Read(Path.Combine(dir, "plain.mp4")));
    }
}
