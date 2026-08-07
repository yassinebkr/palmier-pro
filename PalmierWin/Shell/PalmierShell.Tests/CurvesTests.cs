using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

/// The C# port of PalmierCore's ColorCurves model: JSON shape shared with the
/// engine, eval parity with the Swift original, identity semantics.
public class CurvesTests {
    [Fact]
    public void GradeCurveJson_RoundTrips() {
        var curve = new GradeCurve {
            Master = [new(0, 0), new(0.5, 0.7), new(1, 1)],
            Red = [new(0, 0.1), new(1, 0.9)],
        };
        var parsed = GradeCurve.Parse(curve.ToJson());
        Assert.Equal(curve.Master, parsed.Master);
        Assert.Equal(curve.Red, parsed.Red);
        Assert.Empty(parsed.Green);
        Assert.Empty(parsed.Blue);
    }

    [Fact]
    public void GradeCurveJson_MatchesTheSwiftCodableShape() {
        var curve = new GradeCurve { Master = [new(0, 0), new(1, 1)] };
        Assert.Equal("""{"master":[{"x":0,"y":0},{"x":1,"y":1}],"red":[],"green":[],"blue":[]}""",
            curve.ToJson());
    }

    [Fact]
    public void HueCurvesJson_RoundTrips() {
        var curves = new HueCurves { HueVsSat = [new(0.25, 0.75), new(0.5, 0.5)] };
        var parsed = HueCurves.Parse(curves.ToJson());
        Assert.Empty(parsed.HueVsHue);
        Assert.Equal(curves.HueVsSat, parsed.HueVsSat);
        Assert.Empty(parsed.HueVsLum);
    }

    [Fact]
    public void HueCurvesJson_MatchesTheSwiftCodableShape() {
        var curves = new HueCurves { HueVsSat = [new(0.25, 0.75)] };
        Assert.Equal("""{"hueVsHue":[],"hueVsSat":[{"x":0.25,"y":0.75}],"hueVsLum":[]}""",
            curves.ToJson());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"master":null}""")]
    public void GradeCurveParse_MalformedOrMissing_ReadsAsIdentity(string? json) {
        var curve = GradeCurve.Parse(json);
        Assert.True(curve.IsIdentity);
        Assert.Empty(curve.Master);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    public void HueCurvesParse_MalformedOrMissing_ReadsAsIdentity(string? json) =>
        Assert.True(HueCurves.Parse(json).IsIdentity);

    [Fact]
    public void GradeEval_EmptyIsIdentity() {
        Assert.Equal(0.3, GradeCurve.Eval([], 0.3));
        Assert.Equal(0.4, GradeCurve.Eval(GradeCurve.IdentityPoints, 0.4));
    }

    [Fact]
    public void GradeEval_InterpolatesAndClampsFlatOutsideTheRange() {
        var pts = new List<CurvePoint> { new(0.25, 0.5), new(0.75, 1.0) };
        Assert.Equal(0.5, GradeCurve.Eval(pts, 0.25));
        Assert.Equal(0.75, GradeCurve.Eval(pts, 0.5));
        Assert.Equal(1.0, GradeCurve.Eval(pts, 0.75));
        Assert.Equal(0.5, GradeCurve.Eval(pts, 0));    // flat below the first point
        Assert.Equal(0.5, GradeCurve.Eval(pts, -1));
        Assert.Equal(1.0, GradeCurve.Eval(pts, 1));    // flat above the last point
        Assert.Equal(1.0, GradeCurve.Eval(pts, 2));
    }

    [Fact]
    public void GradeEval_SortsUnsortedInput() {
        var pts = new List<CurvePoint> { new(0.75, 1.0), new(0.25, 0.5) };
        Assert.Equal(0.75, GradeCurve.Eval(pts, 0.5));
    }

    [Fact]
    public void HueEval_EmptyIsNeutral() {
        Assert.Equal(0.5, HueCurves.Eval([], 0));
        Assert.Equal(0.5, HueCurves.Eval([], 0.37));
        Assert.Equal(0.5, HueCurves.Eval([], 0.99));
    }

    [Fact]
    public void HueEval_WrapsAcrossTheSeam() {
        var pts = new List<CurvePoint> { new(0.25, 0.8), new(0.75, 0.2) };
        Assert.Equal(0.8, HueCurves.Eval(pts, 0.25));   // exact point hit
        Assert.Equal(0.2, HueCurves.Eval(pts, 0.75), 10);
        Assert.Equal(0.5, HueCurves.Eval(pts, 0.5), 10);
        // The wrap segment lerps last → first across 0/1, closing the cycle.
        Assert.Equal(0.5, HueCurves.Eval(pts, 0), 10);
        Assert.Equal(HueCurves.Eval(pts, 0), HueCurves.Eval(pts, 1));
        Assert.Equal(HueCurves.Eval(pts, 0), HueCurves.Eval(pts, 0.999), 2);
    }

    [Fact]
    public void HueEval_SinglePointIsFlat() {
        var pts = new List<CurvePoint> { new(0.5, 0.2) };
        Assert.Equal(0.2, HueCurves.Eval(pts, 0.1));
        Assert.Equal(0.2, HueCurves.Eval(pts, 0.9));
    }

    [Fact]
    public void GradeIsIdentity_EmptyOrIdentityChannels() {
        Assert.True(new GradeCurve().IsIdentity);
        Assert.True(new GradeCurve { Master = [new(0, 0), new(1, 1)] }.IsIdentity);
        Assert.False(new GradeCurve { Master = [new(0, 0), new(0.5, 0.6), new(1, 1)] }.IsIdentity);
        Assert.False(new GradeCurve { Blue = [new(0, 0.2), new(1, 0.8)] }.IsIdentity);
    }

    [Fact]
    public void HueIsIdentity_AllChannelsNeutral() {
        Assert.True(new HueCurves().IsIdentity);
        Assert.True(new HueCurves { HueVsHue = [.. HueCurves.DefaultPoints] }.IsIdentity);
        Assert.True(new HueCurves { HueVsSat = [new(0, 0.50009), new(1, 0.5)] }.IsIdentity);
        Assert.False(new HueCurves { HueVsSat = [new(0, 0.5002), new(1, 0.5)] }.IsIdentity);
    }

    [Fact]
    public void HueDefaults_SixNeutralPoints() {
        Assert.Equal(0.5, HueCurves.NeutralY);
        Assert.Equal(6, HueCurves.DefaultPoints.Length);
        for (int i = 0; i < 6; i++) {
            Assert.Equal(i / 6.0, HueCurves.DefaultPoints[i].X, 10);
            Assert.Equal(HueCurves.NeutralY, HueCurves.DefaultPoints[i].Y);
        }
    }

    [Fact]
    public void GradeNormalize_IdentityTwoPointStoresEmpty() {
        Assert.Empty(GradeCurve.NormalizeChannel(GradeCurve.IdentityPoints));
        var lifted = new List<CurvePoint> { new(0, 0), new(0.5, 0.6), new(1, 1) };
        Assert.Equal(lifted, GradeCurve.NormalizeChannel(lifted));
    }

    [Fact]
    public void HueNormalize_FlatChannelStoresEmpty() {
        Assert.Empty(HueCurves.NormalizeChannel([new CurvePoint(0, 0.5), new CurvePoint(1, 0.5)]));
        var bent = new List<CurvePoint> { new(0, 0.5), new(0.5, 0.8), new(1, 0.5) };
        Assert.Equal(bent, HueCurves.NormalizeChannel(bent));
    }
}
