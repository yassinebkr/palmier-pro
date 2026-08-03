using System.Xml;
using System.Xml.Linq;
using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class FcpxmlExportTests {
    static string TestMediaPath(string name) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "test_media", name));

    static readonly XmlReaderSettings XmlSettings = new() { DtdProcessing = DtdProcessing.Parse };

    static XDocument LoadFcpxml(string path) {
        using var reader = XmlReader.Create(path, XmlSettings);
        return XDocument.Load(reader);
    }

    [Fact]
    public void FcpxmlExport_WritesResolveCompatibleXml() {
        IntPtr project = CoreApi.palmier_project_create();
        string dir = Path.Combine(Path.GetTempPath(), $"palmier-fcpxml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "out.fcpxml");
        try {
            // 30 fps timeline: 60 frames = 2s, then 30 frames = 1s.
            Assert.NotNull(CoreApi.AddClip(project, TestMediaPath("testsrc.mp4"), 60));
            Assert.NotNull(CoreApi.AddClip(project, TestMediaPath("colorbands24.mp4"), 30));

            Assert.Equal(1, CoreApi.palmier_export_fcpxml(project, path));
            Assert.True(File.Exists(path));

            var doc = LoadFcpxml(path);
            Assert.Equal("fcpxml", doc.Root!.Name.LocalName);
            Assert.Equal("1.10", doc.Root.Attribute("version")!.Value);

            var gap = doc.Root.Descendants("gap").Single();
            var clips = gap.Elements("asset-clip").ToList();
            Assert.Equal(2, clips.Count);
            Assert.Equal("0s", clips[0].Attribute("offset")!.Value);
            Assert.Equal("2s", clips[0].Attribute("duration")!.Value);
            Assert.Equal("0s", clips[0].Attribute("start")!.Value);
            Assert.Equal("2s", clips[1].Attribute("offset")!.Value);
            Assert.Equal("1s", clips[1].Attribute("duration")!.Value);

            var assets = doc.Root.Element("resources")!.Elements("asset").ToList();
            Assert.Equal(2, assets.Count);
            var srcs = assets.Select(a => a.Element("media-rep")!.Attribute("src")!.Value).ToList();
            Assert.All(srcs, s => Assert.StartsWith("file:///", s));
            Assert.Contains(srcs, s => s.EndsWith("testsrc.mp4"));
            Assert.Contains(srcs, s => s.EndsWith("colorbands24.mp4"));

            var sequence = doc.Root.Descendants("sequence").Single();
            Assert.Equal("3s", sequence.Attribute("duration")!.Value);
        } finally {
            CoreApi.palmier_project_destroy(project);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void FcpxmlExport_WritesTextClipsAsTitles() {
        IntPtr project = CoreApi.palmier_project_create();
        string dir = Path.Combine(Path.GetTempPath(), $"palmier-fcpxml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "titles.fcpxml");
        try {
            Assert.NotNull(CoreApi.AddTextClip(project, "Hello titles", 30, 30));

            Assert.Equal(1, CoreApi.palmier_export_fcpxml(project, path));
            var doc = LoadFcpxml(path);

            var title = doc.Root!.Descendants("title").Single();
            Assert.Equal("Hello titles", title.Attribute("name")!.Value);
            Assert.Equal("1s", title.Attribute("offset")!.Value);
            Assert.Equal("1s", title.Attribute("duration")!.Value);
            Assert.Equal("Hello titles", title.Element("text")!.Element("text-style")!.Value);
        } finally {
            CoreApi.palmier_project_destroy(project);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void FcpxmlExport_RejectsAnInvalidHandle() {
        string path = Path.Combine(Path.GetTempPath(), $"palmier-fcpxml-{Guid.NewGuid():N}.fcpxml");
        Assert.Equal(0, CoreApi.palmier_export_fcpxml(IntPtr.Zero, path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void FcpxmlExport_RefusesAnEmptyTimeline() {
        IntPtr project = CoreApi.palmier_project_create();
        string path = Path.Combine(Path.GetTempPath(), $"palmier-fcpxml-{Guid.NewGuid():N}.fcpxml");
        try {
            Assert.Equal(0, CoreApi.palmier_export_fcpxml(project, path));
            Assert.False(File.Exists(path));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
