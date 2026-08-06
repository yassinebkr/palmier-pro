using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public sealed class TrackLayoutTests {
    static TrackState Track(string type, double height, string? id = null) =>
        new(id ?? Guid.NewGuid().ToString("N")[..8], type, false, false, []) { DisplayHeight = height };

    [Fact]
    public void RowsStackCumulativelyInModelOrder() {
        var v = Track("video", 50); var a1 = Track("audio", 72); var a2 = Track("audio", 100);
        var layout = new TrackLayout([v, a1, a2], top: 24, t => t.RenderHeight);
        Assert.Equal(24, layout.YOf(v.Id));
        Assert.Equal(74, layout.YOf(a1.Id));
        Assert.Equal(146, layout.YOf(a2.Id));
        Assert.Equal(246, layout.Bottom);
    }

    [Fact]
    public void TrackAtReturnsTheRowContainingY() {
        var v = Track("video", 50); var a = Track("audio", 72);
        var layout = new TrackLayout([v, a], top: 24, t => t.RenderHeight);
        Assert.Equal(v.Id, layout.TrackAt(24)!.Id);
        Assert.Equal(v.Id, layout.TrackAt(73.9)!.Id);
        Assert.Equal(a.Id, layout.TrackAt(74)!.Id);
        Assert.Equal(a.Id, layout.TrackAt(145.9)!.Id);
        Assert.Null(layout.TrackAt(146));
        Assert.Null(layout.TrackAt(23.9));
    }

    [Fact]
    public void RowAtReturnsTheFullRowTuple() {
        var v = Track("video", 50); var a = Track("audio", 72);
        var layout = new TrackLayout([v, a], top: 24, t => t.RenderHeight);
        Assert.Null(layout.RowAt(23.9));
        var row = layout.RowAt(74);
        Assert.NotNull(row);
        Assert.Equal(a.Id, row!.Value.Track.Id);
        Assert.Equal(74, row.Value.Y);
        Assert.Equal(72, row.Value.Height);
        Assert.Null(layout.RowAt(146));
    }

    [Fact]
    public void HeightOfOverrideAppliesUniformly() {
        var v = Track("video", 50); var a = Track("audio", 72);
        var layout = new TrackLayout([v, a], top: 0, _ => 28);
        Assert.Equal(28, layout.HeightOf(v.Id));
        Assert.Equal(28, layout.YOf(a.Id));
    }

    [Theory]
    [InlineData(0, "audio", 72)]     // unset → per-type default
    [InlineData(0, "video", 50)]
    [InlineData(44, "audio", 44)]    // upstream default is a real height, honored
    [InlineData(-10, "video", 50)]   // negative counts as unset
    [InlineData(10, "audio", 32)]    // clamped to the model's floor
    [InlineData(500, "video", 200)]  // and ceiling
    [InlineData(96, "video", 96)]    // exact value passes through
    public void RenderHeightDefaultsAndClamps(double height, string type, double expected) {
        Assert.Equal(expected, Track(type, height).RenderHeight);
    }

    [Fact]
    public void MissingDisplayHeightKeyDeserializesToDefault() {
        const string json = """
        {"id":"t","name":"T","fps":30,"width":1920,"height":1080,
         "tracks":[{"clips":[],"hidden":false,"id":"v1","muted":false,"type":"video"},
                   {"clips":[],"displayHeight":96,"hidden":false,"id":"a1","muted":false,"type":"audio"}]}
        """;
        var state = TimelineState.Parse(json);
        Assert.Equal(50, state.Tracks[0].RenderHeight);
        Assert.Equal(96, state.Tracks[1].RenderHeight);
    }
}
