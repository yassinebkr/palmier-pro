using CommunityToolkit.Mvvm.Input;
using PalmierShell.Core;
using PalmierShell.Core.Generation;
using PalmierShell.ViewModels;
using Xunit;

namespace PalmierShell.Tests;

/// The composer's recent-generations list: the sidecars read back newest
/// first, takes whose files were deleted are forgotten, corruption is
/// skipped, and a take already in the library offers no Import.
public class GenerationHistoryTests : IDisposable {
    readonly string dir = Path.Combine(Path.GetTempPath(), $"palmier-history-{Guid.NewGuid():N}");

    public GenerationHistoryTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string WriteTake(string name, string prompt, string model, string createdUtc) {
        string media = Path.Combine(dir, name);
        File.WriteAllBytes(media, [1]);
        GenerationRecord.Write(media, new GenerationRecord("Replicate", model, prompt, 5, createdUtc));
        return media;
    }

    [Fact]
    public void ReadsNewestFirst() {
        WriteTake("old.mp4", "old prompt", "m1", "2026-08-01T10:00:00.0000000Z");
        string newest = WriteTake("new.mp4", "new prompt", "m2", "2026-08-05T10:00:00.0000000Z");
        var entries = GenerationHistory.Load(dir);
        Assert.Equal(2, entries.Count);
        Assert.Equal(newest, entries[0].MediaPath);
        Assert.Equal("m2", entries[0].Model);
        Assert.Equal("new prompt", entries[0].Prompt);
    }

    [Fact]
    public void ASidecarWhoseTakeIsGoneIsForgotten() {
        File.Delete(WriteTake("gone.mp4", "p", "m", "2026-08-05T10:00:00.0000000Z"));
        Assert.Empty(GenerationHistory.Load(dir));
    }

    [Fact]
    public void ACorruptSidecarIsSkipped() {
        WriteTake("ok.mp4", "p", "m", "2026-08-05T10:00:00.0000000Z");
        File.WriteAllBytes(Path.Combine(dir, "bad.mp4"), [1]);
        File.WriteAllText(Path.Combine(dir, "bad.mp4.generation.json"), "{not json");
        Assert.Single(GenerationHistory.Load(dir));
    }

    /// The positional record deserializes even "{}" — Prompt comes out null
    /// and the row build would NRE on it, on every refresh, forever.
    [Fact]
    public void ASidecarWithoutAPromptIsSkipped() {
        WriteTake("ok.mp4", "p", "m", "2026-08-05T10:00:00.0000000Z");
        File.WriteAllBytes(Path.Combine(dir, "empty.mp4"), [1]);
        File.WriteAllText(Path.Combine(dir, "empty.mp4.generation.json"), "{}");
        File.WriteAllBytes(Path.Combine(dir, "promptless.mp4"), [1]);
        File.WriteAllText(Path.Combine(dir, "promptless.mp4.generation.json"),
            """{"Provider":"Replicate","Model":"m","Seconds":5,"CreatedUtc":"2026-08-05T10:00:00.0000000Z"}""");
        Assert.Single(GenerationHistory.Load(dir));
    }

    [Fact]
    public void TheListIsCappedAtTheDefaultLimit() {
        for (int i = 0; i < 12; i++)
            WriteTake($"take{i:00}.mp4", $"prompt {i}", "m", $"2026-08-05T10:{i:00}:00.0000000Z");
        var entries = GenerationHistory.Load(dir);
        Assert.Equal(GenerationHistory.DefaultLimit, entries.Count);
        Assert.Equal("prompt 11", entries[0].Prompt);
    }

    [Fact]
    public void AMissingDirectoryReadsAsEmpty() =>
        Assert.Empty(GenerationHistory.Load(Path.Combine(dir, "nope")));

    [Fact]
    public void RelativeAges() {
        var now = new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("just now", RelativeTime.Ago(now.AddSeconds(-30), now));
        Assert.Equal("5 min ago", RelativeTime.Ago(now.AddMinutes(-5), now));
        Assert.Equal("2 h ago", RelativeTime.Ago(now.AddHours(-2), now));
        Assert.Equal("3 d ago", RelativeTime.Ago(now.AddDays(-3), now));
        Assert.Equal(now.AddDays(-60).ToLocalTime().ToString("d MMM yyyy"),
                     RelativeTime.Ago(now.AddDays(-60), now));
        Assert.Equal("just now", RelativeTime.Ago(now.AddMinutes(1), now));   // clock skew
    }

    [Fact]
    public void ThePromptExcerptIsOneLineAndCapped() {
        var entry = new RecentGeneration("x.mp4", "m",
            "a very long prompt that goes on and on and on and on and on and on and on and on" +
            "\nwith a second line", DateTimeOffset.UtcNow);
        var row = new RecentGenerationViewModel(entry, canImport: true, DateTimeOffset.Now);
        Assert.True(row.PromptExcerpt.Length <= 61);
        Assert.EndsWith("…", row.PromptExcerpt);
        Assert.DoesNotContain("\n", row.PromptExcerpt);
        Assert.Equal("m · just now", row.Detail);
    }

    [Fact]
    public async Task TheListReflectsTheLibraryAndImportLandsThroughTheComposerSeam() {
        string inLibrary = WriteTake("inlib.mp4", "a take in the library", "m1",
                                     "2026-08-06T10:00:00.0000000Z");
        string stray = WriteTake("stray.mp4", "a take that never landed", "m2",
                                 "2026-08-07T09:00:00.0000000Z");
        var imported = new List<string>();
        var panel = new GeneratePanelViewModel(
            (path, _) => { imported.Add(path); return Task.CompletedTask; },
            () => [new MediaItemViewModel(inLibrary, new CoreApi.MediaProbe(1920, 1080, 30, 150))]);
        panel.HistoryDirectory = () => dir;

        await panel.RefreshRecentAsync();

        Assert.Equal(2, panel.RecentGenerations.Count);
        Assert.Equal(stray, panel.RecentGenerations[0].MediaPath);
        Assert.True(panel.RecentGenerations[0].CanImport);
        Assert.False(panel.RecentGenerations[1].CanImport);   // already in the library
        Assert.True(panel.HasRecentGenerations);

        await ((IAsyncRelayCommand)panel.ImportRecentCommand).ExecuteAsync(panel.RecentGenerations[0]);
        Assert.Equal([stray], imported);
        Assert.False(panel.RecentGenerations[0].CanImport);
    }

    [Fact]
    public async Task ADirectoryThatCannotBeReadFailsSoftToEmpty() {
        var panel = new GeneratePanelViewModel((_, _) => Task.CompletedTask, () => []);
        panel.HistoryDirectory = () => Path.Combine(dir, "nope");
        await panel.RefreshRecentAsync();
        Assert.Empty(panel.RecentGenerations);
        Assert.False(panel.HasRecentGenerations);
    }
}
