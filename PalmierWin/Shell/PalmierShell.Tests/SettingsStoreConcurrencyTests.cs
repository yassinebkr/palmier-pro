using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// The "2 takes generates 1" regression: two jobs read the settings for
/// their API key at the same moment the model-memory write rewrites the
/// file. A read landing mid-write fell back to defaults — no key — and one
/// take failed instantly. The store is serialized now; these tests hammer
/// the interleavings that used to lose.
///
/// Isolated in its own collection because PathOverride is process-global.
[Collection("settings-file")]
public sealed class SettingsStoreConcurrencyTests : IDisposable {
    readonly string path = Path.Combine(
        Path.GetTempPath(), $"palmier-settings-{Guid.NewGuid():N}.json");

    public SettingsStoreConcurrencyTests() {
        SettingsStore.PathOverride = path;
        SettingsStore.Save(AppSettings.Default.WithKey("replicate", "test-key"));
    }

    public void Dispose() {
        SettingsStore.PathOverride = null;
        File.Delete(path);
    }

    [Fact]
    public async Task ConcurrentReadsNeverLoseTheKeyToAnInFlightWrite() {
        var tasks = new List<Task>();
        for (int i = 0; i < 50; i++) {
            int n = i;
            tasks.Add(Task.Run(() =>
                SettingsStore.Update(s => s.WithModel("generate:replicate", $"model-{n}"))));
            tasks.Add(Task.Run(() => {
                var settings = SettingsStore.Load();
                Assert.Equal("test-key", settings.KeyFor("replicate"));
            }));
        }
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ConcurrentUpdatesKeepEachOthersWrites() {
        await Task.WhenAll(
            Task.Run(() => SettingsStore.Update(s => s.WithModel("generate:a", "1"))),
            Task.Run(() => SettingsStore.Update(s => s.WithModel("generate:b", "2"))));
        var settings = SettingsStore.Load();
        Assert.Equal("1", settings.Models["generate:a"]);
        Assert.Equal("2", settings.Models["generate:b"]);
    }
}
