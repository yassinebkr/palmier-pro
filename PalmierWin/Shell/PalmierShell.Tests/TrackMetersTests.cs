using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public sealed class TrackMetersTests {
    [Fact]
    public void LevelRisesInstantlyAndDecaysAt24DbPerSecond() {
        var m = new TrackMeters();
        m.Tick(1f, 1.0 / 30);
        Assert.Equal(0, m.LevelDb);
        m.Tick(0f, 0.5);
        Assert.Equal(-12, m.LevelDb, 3);
    }

    [Fact]
    public void PeakHolds15SecondsThenDecaysAt18DbPerSecond() {
        var m = new TrackMeters();
        m.Tick(0.5f, 1.0 / 30);   // ≈ −6.02 dB
        double peak = m.PeakDb;
        for (int i = 0; i < 45; i++) m.Tick(0f, 1.0 / 30);  // 1.5 s of silence
        Assert.Equal(peak, m.PeakDb, 3);
        m.Tick(0f, 0.5);
        Assert.Equal(peak - 9, m.PeakDb, 1);
    }

    [Fact]
    public void ClipLatchesAndSurvivesDecay() {
        var m = new TrackMeters();
        m.Tick(1.2f, 1.0 / 30);
        Assert.True(m.Clipped);
        for (int i = 0; i < 300; i++) m.Tick(0f, 1.0 / 30);
        Assert.True(m.Clipped);
        m.Reset();
        Assert.False(m.Clipped);
        Assert.Equal(-60, m.LevelDb);
    }

    [Fact]
    public void SilenceDecaysToTheFloorAndStaysThere() {
        var m = new TrackMeters();
        m.Tick(1f, 1.0 / 30);
        for (int i = 0; i < 1000; i++) m.Tick(0f, 1.0 / 30);
        Assert.Equal(-60, m.LevelDb);
        Assert.Equal(-60, m.PeakDb);
    }

    [Fact]
    public void SubFloorSamplesClampToFloor() {
        var m = new TrackMeters();
        m.Tick(0.0001f, 1.0 / 30);   // ≈ −80 dB
        Assert.Equal(-60, m.LevelDb);
        Assert.Equal(-60, m.PeakDb);
    }
}
