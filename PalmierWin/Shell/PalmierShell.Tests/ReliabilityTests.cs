using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// The crash-log writer is the one piece of the crash path that runs before
/// any dialog: it must produce a complete, parseable log and never throw,
/// even when the log directory itself is the problem.
///
/// Isolated in its own collection because DirectoryOverride is process-global.
[Collection("crash-log")]
public sealed class CrashLogTests : IDisposable {
    readonly string dir = Path.Combine(Path.GetTempPath(), $"palmier-logs-{Guid.NewGuid():N}");

    public CrashLogTests() => CrashLog.DirectoryOverride = dir;

    public void Dispose() {
        CrashLog.DirectoryOverride = null;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Write_CreatesACompleteLog() {
        string? path = CrashLog.Write("unhandled UI thread exception",
            new InvalidOperationException("preview exploded"));

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Contains("crash-", Path.GetFileName(path));
        string text = File.ReadAllText(path);
        Assert.Contains("Kind: unhandled UI thread exception", text);
        Assert.Contains("Version:", text);
        Assert.Contains("Uptime:", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("preview exploded", text);
    }

    [Fact]
    public void Write_WhenDirectoryCannotBeCreated_ReturnsNull() {
        // A file where the log directory should be: CreateDirectory fails.
        string blocker = Path.Combine(dir, "blocker");
        Directory.CreateDirectory(dir);
        TempFiles.Run(() => File.WriteAllText(blocker, ""));
        CrashLog.DirectoryOverride = Path.Combine(blocker, "logs");

        Assert.Null(CrashLog.Write("test", new Exception("boom")));
    }
}

/// Corrupt settings files must fall back to defaults — truncated JSON,
/// wrong value types, and a literal "null" document included, not only a
/// missing file.
[Collection("settings-file")]
public sealed class SettingsFallbackTests : IDisposable {
    readonly string path = Path.Combine(
        Path.GetTempPath(), $"palmier-settings-{Guid.NewGuid():N}.json");

    public SettingsFallbackTests() => SettingsStore.PathOverride = path;

    public void Dispose() {
        SettingsStore.PathOverride = null;
        if (File.Exists(path)) TempFiles.Run(() => File.Delete(path));
    }

    [Theory]
    [InlineData("{\"Provider\": \"anthropic\", \"SnapEn")]                 // truncated
    [InlineData("{\"Provider\": 42, \"Model\": [], \"SnapEnabled\": \"yes\"}")] // wrong types
    [InlineData("null")]                                                   // valid JSON, no object
    [InlineData("not json at all")]
    public void Load_CorruptFile_FallsBackToDefault(string content) {
        TempFiles.Run(() => File.WriteAllText(path, content));
        Assert.Equal(AppSettings.Default, SettingsStore.Load());
    }

    [Fact]
    public void Save_ToUnwritablePath_DegradesWithoutThrowing() {
        // A file where the settings directory should be: CreateDirectory
        // throws IOException, and the save must degrade, not crash.
        string blocker = Path.Combine(Path.GetTempPath(), $"palmier-blocker-{Guid.NewGuid():N}");
        TempFiles.Run(() => File.WriteAllText(blocker, ""));
        SettingsStore.PathOverride = Path.Combine(blocker, "settings.json");
        try {
            SettingsStore.Save(AppSettings.Default.WithKey("anthropic", "k"));
        } finally {
            TempFiles.Run(() => File.Delete(blocker));
        }
    }
}

/// Same fallback contract for the window layout file.
[Collection("layout-file")]
public sealed class LayoutFallbackTests : IDisposable {
    readonly string path = Path.Combine(
        Path.GetTempPath(), $"palmier-layout-{Guid.NewGuid():N}.json");

    public LayoutFallbackTests() => LayoutStore.PathOverride = path;

    public void Dispose() {
        LayoutStore.PathOverride = null;
        if (File.Exists(path)) TempFiles.Run(() => File.Delete(path));
    }

    [Theory]
    [InlineData("{\"WindowWidth\": 1440, \"Wind")]          // truncated
    [InlineData("{\"WindowWidth\": \"wide\", \"Maximized\": 7}")] // wrong types
    [InlineData("null")]                                    // valid JSON, no object
    public void Load_CorruptFile_FallsBackToDefault(string content) {
        TempFiles.Run(() => File.WriteAllText(path, content));
        Assert.Equal(WorkspaceLayout.Default, LayoutStore.Load());
    }
}

public class NativeHostProbeTests {
    [Fact]
    public void NativeHost_LoadsInTheTestEnvironment() =>
        Assert.True(CoreApi.TryLoadNativeHost());
}

/// A destroy against an already-destroyed or foreign handle must be a no-op
/// across the ABI — before the handle registry, both were use-after-free.
public class HandleLifecycleTests {
    [Fact]
    public void ProjectDestroy_Twice_IsSafe() {
        IntPtr project = CoreApi.palmier_project_create();
        CoreApi.palmier_project_destroy(project);
        CoreApi.palmier_project_destroy(project);
    }

    [Fact]
    public void AgentDestroy_Twice_IsSafe() {
        IntPtr project = CoreApi.palmier_project_create();
        IntPtr agent = CoreApi.palmier_agent_create(project);
        CoreApi.palmier_agent_destroy(agent);
        CoreApi.palmier_agent_destroy(agent);
        CoreApi.palmier_project_destroy(project);
    }

    [Fact]
    public void Destroy_NullAndForeignPointers_IsSafe() {
        CoreApi.palmier_project_destroy(IntPtr.Zero);
        CoreApi.palmier_agent_destroy(IntPtr.Zero);
        CoreApi.palmier_project_destroy(new IntPtr(0x1000));
    }

    [Fact]
    public void AddClip_EmptyPath_IsRefused() {
        IntPtr project = CoreApi.palmier_project_create();
        try {
            Assert.Null(CoreApi.AddClip(project, "", 60));
            var state = TimelineState.Parse(CoreApi.GetTimelineJson(project));
            Assert.Empty(state.Tracks.SelectMany(t => t.Clips));
        } finally {
            CoreApi.palmier_project_destroy(project);
        }
    }
}
