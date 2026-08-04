using PalmierShell.Core;
using Xunit;
using static PalmierShell.Core.ColorWheelMath;

namespace PalmierShell.Tests;

/// The wheel → color.wheels mapping: neutral at center, slider as the luma
/// term, hue drags as zero-sum offsets, and a round trip through ToVector so
/// a refreshed inspector re-places the handle where the user left it.
public class ColorWheelMathTests {
    [Fact]
    public void CenterIsNeutral() {
        Assert.Equal((0, 0, 0), ToParams(WheelKind.Lift, 0, 0, 0));
        Assert.Equal((1, 1, 1), ToParams(WheelKind.Gain, 0, 0, 0));
        Assert.Equal((1, 1, 1), ToParams(WheelKind.Gamma, 0, 0, 0));
    }

    [Fact]
    public void NeutralParamsMapBackToCenter() {
        Assert.Equal((0, 0, 0), ToVector(WheelKind.Lift, 0, 0, 0));
        Assert.Equal((0, 0, 0), ToVector(WheelKind.Gain, 1, 1, 1));
        Assert.Equal((0, 0, 0), ToVector(WheelKind.Gamma, 1, 1, 1));
    }

    [Fact]
    public void SliderMovesAllChannelsTogether() {
        Assert.Equal((LiftRange, LiftRange, LiftRange), ToParams(WheelKind.Lift, 0, 0, 1));
        Assert.Equal((-LiftRange, -LiftRange, -LiftRange), ToParams(WheelKind.Lift, 0, 0, -1));
        Assert.Equal((1 + GainRange, 1 + GainRange, 1 + GainRange), ToParams(WheelKind.Gain, 0, 0, 1));
    }

    /// Red sits at angle 0: a full drag right lifts red and drops the other
    /// two by half as much — the zero-sum hue direction.
    [Fact]
    public void FullRedDragOnGain() {
        var (r, g, b) = ToParams(WheelKind.Gain, 1, 0, 0);
        Assert.Equal(1 + GainRange, r, 6);
        Assert.Equal(1 - GainRange / 2, g, 6);
        Assert.Equal(1 - GainRange / 2, b, 6);
    }

    [Fact]
    public void HueDragSumsToZero() {
        for (double angle = 0; angle < 2 * Math.PI; angle += 0.3) {
            var (r, g, b) = HueDirection(angle);
            Assert.Equal(0, r + g + b, 6);
            Assert.Equal(1, new[] { r, g, b }.Select(Math.Abs).Max(), 6);
        }
    }

    [Theory]
    [InlineData(WheelKind.Lift, 0.8, -0.4, 0.5)]
    [InlineData(WheelKind.Gain, -0.3, 0.9, -1)]
    [InlineData(WheelKind.Gamma, 0.2, 0.2, 0)]
    public void GestureSurvivesTheRoundTrip(WheelKind kind, double x, double y, double offset) {
        var (r, g, b) = ToParams(kind, x, y, offset);
        var (rx, ry, ro) = ToVector(kind, r, g, b);
        Assert.Equal(x, rx, 6);
        Assert.Equal(y, ry, 6);
        Assert.Equal(offset, ro, 6);
    }

    /// Values past the disc edge (hand-edited params) clamp into the disc.
    [Fact]
    public void OutOfRangeParamsClampIntoTheDisc() {
        var (x, y, _) = ToVector(WheelKind.Gain, 1 + 3 * GainRange, 1, 1);
        double amount = Math.Sqrt(x * x + y * y);
        Assert.True(amount <= 1 + 1e-9, $"vector escaped the disc: {amount}");
        var (r, _, _) = ToParams(WheelKind.Gain, x, y, 0);
        Assert.True(r >= 1 + GainRange - 1e-9, $"clamped vector lost its red: {r}");
    }
}
