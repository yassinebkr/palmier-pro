using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

[Collection("waveform-cache")]
public sealed class WaveformCacheTests : IDisposable {
    readonly string dir = Path.Combine(Path.GetTempPath(), $"palmier-wfcache-{Guid.NewGuid():N}");
    readonly string media;

    public WaveformCacheTests() {
        WaveformCache.DirectoryOverride = dir;
        Directory.CreateDirectory(dir);
        media = Path.Combine(dir, "clip.mp4");
        File.WriteAllText(media, "fake");
    }

    public void Dispose() {
        WaveformCache.DirectoryOverride = null;
        WaveformCache.DecodeOverride = null;
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task DecodeHappensOnceThenPersists() {
        int calls = 0;
        WaveformCache.DecodeOverride = (_, cols) => { calls++; return Enumerable.Repeat(0.5f, cols * 2).ToArray(); };
        var first = await WaveformCache.GetAsync(media, 64);
        var second = await WaveformCache.GetAsync(media, 64);
        Assert.Equal(1, calls);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task CorruptCacheFileIsTreatedAsMiss() {
        int calls = 0;
        WaveformCache.DecodeOverride = (_, cols) => { calls++; return new float[cols * 2]; };
        _ = await WaveformCache.GetAsync(media, 64);
        foreach (var f in Directory.GetFiles(dir, "*.wf")) File.WriteAllText(f, "garbage");
        _ = await WaveformCache.GetAsync(media, 64);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task KeyChangesWithMediaMtime() {
        int calls = 0;
        WaveformCache.DecodeOverride = (_, cols) => { calls++; return new float[cols * 2]; };
        _ = await WaveformCache.GetAsync(media, 64);
        File.SetLastWriteTimeUtc(media, DateTime.UtcNow.AddHours(1));
        _ = await WaveformCache.GetAsync(media, 64);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NullDecodeStaysNullAndIsNotCached() {
        WaveformCache.DecodeOverride = (_, _) => null;
        Assert.Null(await WaveformCache.GetAsync(media, 64));
        Assert.Empty(Directory.EnumerateFiles(dir, "*.wf"));
    }

    [Fact]
    public async Task ConcurrentSameKeySharesOneDecode() {
        int calls = 0;
        WaveformCache.DecodeOverride = (_, cols) => {
            Interlocked.Increment(ref calls);
            Thread.Sleep(50);
            return new float[cols * 2];
        };
        var results = await Task.WhenAll(
            WaveformCache.GetAsync(media, 64),
            WaveformCache.GetAsync(media, 64),
            WaveformCache.GetAsync(media, 64));
        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.NotNull(r));
    }

    [Fact]
    public async Task ZeroColumnsIsRejected() {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WaveformCache.GetAsync(media, 0));
    }

    [Fact]
    public async Task ConcurrentCallsStayInsideTheGate() {
        int inFlight = 0, maxInFlight = 0;
        WaveformCache.DecodeOverride = (_, cols) => {
            int now = Interlocked.Increment(ref inFlight);
            int peak;
            do { peak = maxInFlight; } while (now > peak &&
                Interlocked.CompareExchange(ref maxInFlight, now, peak) != peak);
            Thread.Sleep(30);
            Interlocked.Decrement(ref inFlight);
            return new float[cols * 2];
        };
        var jobs = Enumerable.Range(0, 6)
            .Select(_ => WaveformCache.GetAsync(media + Guid.NewGuid().ToString("N")[..4], 64));
        await Task.WhenAll(jobs);
        Assert.True(maxInFlight <= 2, $"gate allowed {maxInFlight} concurrent decodes");
    }
}
