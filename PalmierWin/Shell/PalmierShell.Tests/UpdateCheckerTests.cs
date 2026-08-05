using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// Update selection is pure logic over the releases JSON — the GitHub call
/// itself is behind FetchOverride and never runs here.
public sealed class UpdateCheckerTests {
    static readonly Version Current = new(0, 1, 0);
    static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    static string Release(string tag, string? asset = "PalmierWin-Setup-0.2.0.exe", string notes = "notes") {
        string assets = asset is null ? "[]"
            : $$"""[{"name":"{{asset}}","browser_download_url":"https://example.com/{{asset}}"}]""";
        return $$"""{"tag_name":"{{tag}}","body":"{{notes}}","prerelease":true,"assets":{{assets}}}""";
    }

    static string Releases(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Theory]
    [InlineData("v0.2.0-win", "0.2.0")]
    [InlineData("v10.20.30-win", "10.20.30")]
    [InlineData("v0.2.0", null)]        // macOS tag
    [InlineData("0.2.0-win", null)]     // missing v
    [InlineData("v0.2-win", null)]      // two components
    [InlineData("v0.2.0.1-win", null)]  // four components
    [InlineData("v0.2.0-beta-win", null)]
    [InlineData("", null)]
    public void TagParsingRequiresThreePartSemverWithWinSuffix(string tag, string? expected) {
        Assert.Equal(expected is not null, UpdateChecker.TryParseTag(tag, out var version));
        if (expected is not null) Assert.Equal(expected, version!.ToString());
    }

    [Fact]
    public void NewestWinTagWinsOverOlderAndMacReleases() {
        string json = Releases(
            Release("v0.3.0"),                          // mac build: not a candidate
            Release("v0.2.0-win"),
            Release("v0.1.5-win"));
        var info = UpdateChecker.SelectUpdate(json, Current, AppSettings.Default, Now);
        Assert.Equal(new Version(0, 2, 0), info!.Version);
    }

    [Fact]
    public void PrereleasesAreCandidates() {
        // Every Windows build is a GitHub prerelease; the flag must not filter them out.
        string json = Releases(Release("v0.2.0-win"));
        Assert.NotNull(UpdateChecker.SelectUpdate(json, Current, AppSettings.Default, Now));
    }

    [Fact]
    public void EqualVersionIsNotAnUpdate() {
        string json = Releases(Release("v0.1.0-win"));
        Assert.Null(UpdateChecker.SelectUpdate(json, Current, AppSettings.Default, Now));
    }

    [Fact]
    public void OlderVersionIsNotAnUpdate() {
        string json = Releases(Release("v0.1.0-win"));
        Assert.Null(UpdateChecker.SelectUpdate(json, new Version(0, 2, 0), AppSettings.Default, Now));
    }

    [Fact]
    public void SkippedVersionIsNotOffered() {
        var settings = AppSettings.Default with { UpdateSkipVersion = "0.2.0" };
        string json = Releases(Release("v0.2.0-win"));
        Assert.Null(UpdateChecker.SelectUpdate(json, Current, settings, Now));
    }

    [Fact]
    public void NewerThanSkippedIsStillOffered() {
        var settings = AppSettings.Default with { UpdateSkipVersion = "0.2.0" };
        string json = Releases(Release("v0.2.0-win"), Release("v0.3.0-win", "PalmierWin-Setup-0.3.0.exe"));
        var info = UpdateChecker.SelectUpdate(json, Current, settings, Now);
        Assert.Equal(new Version(0, 3, 0), info!.Version);
    }

    [Fact]
    public void ActiveSnoozeSuppressesThePrompt() {
        var settings = AppSettings.Default with { UpdateSnoozeUntil = Now.AddHours(1) };
        string json = Releases(Release("v0.2.0-win"));
        Assert.Null(UpdateChecker.SelectUpdate(json, Current, settings, Now));
    }

    [Fact]
    public void ExpiredSnoozeLetsThePromptThrough() {
        var settings = AppSettings.Default with { UpdateSnoozeUntil = Now.AddHours(-1) };
        string json = Releases(Release("v0.2.0-win"));
        Assert.NotNull(UpdateChecker.SelectUpdate(json, Current, settings, Now));
    }

    [Fact]
    public void ReleaseWithoutInstallerFallsBackToNewestWithAsset() {
        string json = Releases(
            Release("v0.3.0-win", asset: null),   // notes-only release
            Release("v0.2.0-win"));
        var info = UpdateChecker.SelectUpdate(json, Current, AppSettings.Default, Now);
        Assert.Equal(new Version(0, 2, 0), info!.Version);
    }

    [Fact]
    public void NotesAndDownloadUrlComeFromTheRelease() {
        string json = Releases(Release("v0.2.0-win", notes: "bug fixes"));
        var info = UpdateChecker.SelectUpdate(json, Current, AppSettings.Default, Now);
        Assert.Equal("bug fixes", info!.Notes);
        Assert.Equal("https://example.com/PalmierWin-Setup-0.2.0.exe", info.DownloadUrl);
        Assert.Equal("v0.2.0-win", info.Tag);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[{\"no_tag\":true}]")]
    public void MalformedPayloadsMeanNoUpdate(string json) {
        Assert.Null(UpdateChecker.SelectUpdate(json, Current, AppSettings.Default, Now));
    }

    [Fact]
    public async Task CheckAsyncUsesTheFetchSeam() {
        UpdateChecker.FetchOverride = _ => Task.FromResult<string?>(Releases(Release("v99.0.0-win")));
        try {
            var info = await UpdateChecker.CheckAsync(AppSettings.Default);
            Assert.Equal(new Version(99, 0, 0), info!.Version);
        } finally {
            UpdateChecker.FetchOverride = null;
        }
    }

    [Fact]
    public async Task CheckAsyncWithNoFetchResultMeansNoUpdate() {
        UpdateChecker.FetchOverride = _ => Task.FromResult<string?>(null);
        try {
            Assert.Null(await UpdateChecker.CheckAsync(AppSettings.Default));
        } finally {
            UpdateChecker.FetchOverride = null;
        }
    }
}

/// Snooze and skip must survive a restart — they persist in the settings file.
/// Same collection as the other settings tests: PathOverride is process-global.
[Collection("settings-file")]
public sealed class UpdateSettingsPersistenceTests : IDisposable {
    readonly string path = Path.Combine(
        Path.GetTempPath(), $"palmier-settings-{Guid.NewGuid():N}.json");

    public UpdateSettingsPersistenceTests() => SettingsStore.PathOverride = path;

    public void Dispose() {
        SettingsStore.PathOverride = null;
        File.Delete(path);
    }

    [Fact]
    public void SnoozeAndSkipRoundTrip() {
        var until = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        SettingsStore.Save(AppSettings.Default with {
            UpdateSnoozeUntil = until,
            UpdateSkipVersion = "1.2.3",
        });
        var loaded = SettingsStore.Load();
        Assert.Equal(until, loaded.UpdateSnoozeUntil);
        Assert.Equal("1.2.3", loaded.UpdateSkipVersion);
    }

    [Fact]
    public void UpdateFieldsDefaultToUnset() {
        SettingsStore.Save(AppSettings.Default);
        var loaded = SettingsStore.Load();
        Assert.Null(loaded.UpdateSnoozeUntil);
        Assert.Equal("", loaded.UpdateSkipVersion);
    }
}

/// The session log: startup header lands, events append, and an unwritable
/// destination costs the app nothing.
[Collection("session-log")]
public sealed class SessionLogTests : IDisposable {
    readonly string dir = Path.Combine(Path.GetTempPath(), $"palmier-sessionlog-{Guid.NewGuid():N}");

    public void Dispose() {
        SessionLog.Reset();
        SessionLog.DirectoryOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void StartWritesTheHeaderAndEventsAppend() {
        SessionLog.DirectoryOverride = dir;
        SessionLog.Start();
        SessionLog.Event("test", "hello world");
        SessionLog.Reset();
        string text = File.ReadAllText(Directory.GetFiles(dir, "session-*.log").Single());
        Assert.Contains("version:", text);
        Assert.Contains("os:", text);
        Assert.Contains("gpu:", text);
        Assert.Contains("test: hello world", text);
    }

    [Fact]
    public void UnwritableLogDirectoryNeverThrows() {
        // A file where the log directory should be forces CreateDirectory to fail.
        string file = Path.Combine(Path.GetTempPath(), $"palmier-notdir-{Guid.NewGuid():N}");
        File.WriteAllText(file, "x");
        try {
            SessionLog.DirectoryOverride = file;
            Assert.Null(Record.Exception(() => {
                SessionLog.Start();
                SessionLog.Event("test", "line");
            }));
        } finally {
            SessionLog.Reset();
            File.Delete(file);
        }
    }
}
