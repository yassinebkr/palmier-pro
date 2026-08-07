using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// The curve editors' pointer math (CurveEditing), on a 100×100 plot so one
/// unit of curve space is 100 px; hit radius 15 (half the AppTheme
/// CurvePointHitDiameter).
public class CurveEditingTests {
    const double W = 100, H = 100, Hit = 15;

    static readonly List<CurvePoint> ThreePoints = [new(0, 0), new(0.5, 0.5), new(1, 1)];

    [Fact]
    public void Grab_WithinRadiusGrabsTheNearestPoint() {
        var (points, index) = CurveEditing.Grab(ThreePoints, 50, 48, W, H, Hit);
        Assert.Equal(3, points.Count);
        Assert.Equal(1, index);
    }

    [Fact]
    public void Grab_NearerPointWins() {
        var pts = new List<CurvePoint> { new(0.4, 0.5), new(0.6, 0.5) };
        var (_, index) = CurveEditing.Grab(pts, 44, 50, W, H, Hit);
        Assert.Equal(0, index);
    }

    [Fact]
    public void Grab_OutsideRadiusDropsANewPointAtThePressPosition() {
        var (points, index) = CurveEditing.Grab(ThreePoints, 20, 75, W, H, Hit);
        Assert.Equal(4, points.Count);
        Assert.Equal(1, index);                     // sorted in at x = 0.2
        Assert.Equal(new CurvePoint(0.2, 0.25), points[1]);
    }

    [Fact]
    public void Grab_SortsUnsortedInput() {
        var pts = new List<CurvePoint> { new(1, 1), new(0, 0), new(0.5, 0.5) };
        var (points, index) = CurveEditing.Grab(pts, 50, 50, W, H, Hit);
        Assert.Equal(new[] { 0.0, 0.5, 1.0 }, points.Select(p => p.X));
        Assert.Equal(1, index);
    }

    [Fact]
    public void Move_YIsFreeAndClampedToUnit() {
        var moved = CurveEditing.Move(ThreePoints, 1, 50, -30, W, H);
        Assert.Equal(new CurvePoint(0.5, 1), moved[1]);
        moved = CurveEditing.Move(ThreePoints, 1, 50, 130, W, H);
        Assert.Equal(new CurvePoint(0.5, 0), moved[1]);
    }

    [Fact]
    public void Move_XClampsStrictlyBetweenNeighbours() {
        var moved = CurveEditing.Move(ThreePoints, 1, 120, 50, W, H);
        Assert.Equal(1 - CurveEditing.NeighborGap, moved[1].X);
        moved = CurveEditing.Move(ThreePoints, 1, -50, 50, W, H);
        Assert.Equal(CurveEditing.NeighborGap, moved[1].X);
        // The neighbours themselves are untouched.
        Assert.Equal(new CurvePoint(0, 0), moved[0]);
        Assert.Equal(new CurvePoint(1, 1), moved[2]);
    }

    [Fact]
    public void Move_EndpointsKeepTheirX() {
        var moved = CurveEditing.Move(ThreePoints, 0, 60, 25, W, H);
        Assert.Equal(new CurvePoint(0, 0.75), moved[0]);
        moved = CurveEditing.Move(ThreePoints, 2, 40, 75, W, H);
        Assert.Equal(new CurvePoint(1, 0.25), moved[2]);
    }

    [Fact]
    public void RemoveNearest_RemovesAnInteriorPoint() {
        var remaining = CurveEditing.RemoveNearest(ThreePoints, 50, 50, W, H, Hit);
        Assert.NotNull(remaining);
        Assert.Equal(new List<CurvePoint> { new(0, 0), new(1, 1) }, remaining);
    }

    [Fact]
    public void RemoveNearest_RefusesEndpoints() {
        Assert.Null(CurveEditing.RemoveNearest(ThreePoints, 0, 100, W, H, Hit));
        Assert.Null(CurveEditing.RemoveNearest(ThreePoints, 100, 0, W, H, Hit));
    }

    [Fact]
    public void RemoveNearest_NeverDropsBelowTwoPoints() {
        var two = new List<CurvePoint> { new(0, 0), new(1, 1) };
        Assert.Null(CurveEditing.RemoveNearest(two, 0, 100, W, H, Hit));
    }

    [Fact]
    public void RemoveNearest_OutsideRadiusIsANoOp() {
        Assert.Null(CurveEditing.RemoveNearest(ThreePoints, 20, 20, W, H, Hit));
    }
}
