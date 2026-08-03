using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// The filmstrip is 8 tiles across the whole media; a clip draws only its own
/// slice. Ignoring the slice made both halves of a cut show the entire movie
/// squeezed into their width — "cutting a clip breaks its thumbnails".
public class FilmstripWindowTests {
    const int Strip = 8;

    static int[] Tiles(int tileCount, int trim, int consumed, int total) =>
        Enumerable.Range(0, tileCount)
            .Select(i => FilmstripWindow.TileIndex(i, tileCount, Strip, trim, consumed, total))
            .ToArray();

    [Fact]
    public void AnUncutClipStillSpansTheWholeStrip() {
        int[] tiles = Tiles(8, trim: 0, consumed: 300, total: 300);
        Assert.Equal(0, tiles.First());
        Assert.Equal(Strip - 1, tiles.Last());
    }

    /// A clip split down the middle: the left half draws only the strip's
    /// first half, the right half only its second — no overlap, no full-movie
    /// repeat on either side.
    [Fact]
    public void SplitHalvesShowDisjointHalvesOfTheStrip() {
        int[] left = Tiles(6, trim: 0, consumed: 150, total: 300);
        int[] right = Tiles(6, trim: 150, consumed: 150, total: 300);
        Assert.All(left, t => Assert.InRange(t, 0, 3));
        Assert.All(right, t => Assert.InRange(t, 4, 7));
        Assert.Contains(0, left);
        Assert.Contains(7, right);
    }

    [Fact]
    public void AHeadTrimmedClipStartsPastTheStripStart() {
        int[] tiles = Tiles(6, trim: 225, consumed: 75, total: 300);
        Assert.All(tiles, t => Assert.InRange(t, 6, 7));
    }

    /// Unknown media length (still probing, or text) falls back to the old
    /// full spread rather than guessing a window.
    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void UnknownSourceLengthSpreadsTheWholeStrip(int total) {
        int[] tiles = Tiles(8, trim: 150, consumed: 150, total: total);
        Assert.Equal(0, tiles.First());
        Assert.Equal(Strip - 1, tiles.Last());
    }

    /// A 2x-speed clip consumes twice its duration in source, so its window is
    /// wider than an equal-length 1x clip's.
    [Fact]
    public void SpeedWidensTheWindowThroughSourceFramesConsumed() {
        int[] fast = Tiles(6, trim: 0, consumed: 300, total: 300);   // 150 frames at 2x
        int[] slow = Tiles(6, trim: 0, consumed: 150, total: 300);
        Assert.Equal(Strip - 1, fast.Last());
        Assert.True(slow.Last() < Strip - 1);
    }

    [Fact]
    public void DegenerateInputsStayInBounds() {
        Assert.Equal(0, FilmstripWindow.TileIndex(0, 1, Strip, 0, 0, 300));
        Assert.Equal(0, FilmstripWindow.TileIndex(0, 4, 0, 0, 100, 300));
        Assert.InRange(FilmstripWindow.TileIndex(3, 4, Strip, 400, 100, 300), 0, Strip - 1);
    }
}
