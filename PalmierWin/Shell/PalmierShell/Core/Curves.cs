using System.Text.Json;

namespace PalmierShell.Core;

/// One point on a color curve. Serializes as {"x":…,"y":…} — the exact
/// Codable shape of PalmierCore's CurvePoint, which the engine parses.
public readonly record struct CurvePoint(double X, double Y);

public enum GradeChannel { Master, Red, Green, Blue }
public enum HueChannel { Hue, Sat, Lum }

/// Shared JSON: camelCase names, case-insensitive reads — byte-compatible
/// with what Swift's Codable writes for these records.
static class CurveJson {
    public static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

/// Master + per-channel R/G/B tone curves, stored on a clip as the
/// `color.curves` effect's "curve" param. The eval ports the engine's Swift
/// GradeCurve.eval line for line so the editor stroke matches the rendered
/// pixels; the engine's eval stays authoritative for rendering.
public sealed record GradeCurve {
    public List<CurvePoint> Master { get; init; } = [];
    public List<CurvePoint> Red { get; init; } = [];
    public List<CurvePoint> Green { get; init; } = [];
    public List<CurvePoint> Blue { get; init; } = [];

    public const string EffectType = "color.curves";
    public const string ParamKey = "curve";

    public static readonly CurvePoint[] IdentityPoints = [new(0, 0), new(1, 1)];

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsIdentity => new[] { Master, Red, Green, Blue }
        .All(channel => channel.Count == 0 || channel.SequenceEqual(IdentityPoints));

    public IReadOnlyList<CurvePoint> Points(GradeChannel channel) => channel switch {
        GradeChannel.Master => Master,
        GradeChannel.Red => Red,
        GradeChannel.Green => Green,
        _ => Blue,
    };

    public GradeCurve With(GradeChannel channel, IReadOnlyList<CurvePoint> points) => channel switch {
        GradeChannel.Master => this with { Master = [.. points] },
        GradeChannel.Red => this with { Red = [.. points] },
        GradeChannel.Green => this with { Green = [.. points] },
        _ => this with { Blue = [.. points] },
    };

    /// An identity two-point channel stores as empty — nothing to persist.
    public static IReadOnlyList<CurvePoint> NormalizeChannel(IReadOnlyList<CurvePoint> points) =>
        points.SequenceEqual(IdentityPoints) ? [] : points;

    /// Piecewise-linear, sorted by x, clamped flat outside the point range.
    public static double Eval(IReadOnlyList<CurvePoint> points, double x) {
        var p = (points.Count == 0 ? IdentityPoints : points).OrderBy(q => q.X).ToList();
        if (x <= p[0].X) return p[0].Y;
        if (x >= p[^1].X) return p[^1].Y;
        for (int i = 1; i < p.Count; i++) {
            if (x > p[i].X) continue;
            var (a, b) = (p[i - 1], p[i]);
            double t = b.X - a.X == 0 ? 0 : (x - a.X) / (b.X - a.X);
            return a.Y + (b.Y - a.Y) * t;
        }
        return x;
    }

    public string ToJson() => JsonSerializer.Serialize(this, CurveJson.Options);

    /// Absent or malformed JSON reads as the identity curve.
    public static GradeCurve Parse(string? json) {
        if (string.IsNullOrEmpty(json)) return new GradeCurve();
        GradeCurve? decoded;
        try {
            decoded = JsonSerializer.Deserialize<GradeCurve>(json, CurveJson.Options);
        } catch (JsonException) {
            return new GradeCurve();
        }
        if (decoded is null) return new GradeCurve();
        // STJ honours explicit nulls where the Swift decoder refuses the document.
        return decoded with {
            Master = decoded.Master ?? [], Red = decoded.Red ?? [],
            Green = decoded.Green ?? [], Blue = decoded.Blue ?? [],
        };
    }
}

/// Resolve-style hue curves, stored as the `color.hueCurves` effect's
/// "curves" param: each channel maps source hue (0…1, cyclic) to one
/// adjustment around the neutral y = 0.5.
public sealed record HueCurves {
    public List<CurvePoint> HueVsHue { get; init; } = [];
    public List<CurvePoint> HueVsSat { get; init; } = [];
    public List<CurvePoint> HueVsLum { get; init; } = [];

    public const string EffectType = "color.hueCurves";
    public const string ParamKey = "curves";
    public const double NeutralY = 0.5;

    /// Six evenly spaced neutral points — what an empty channel displays.
    public static readonly CurvePoint[] DefaultPoints =
        Enumerable.Range(0, 6).Select(i => new CurvePoint(i / 6.0, NeutralY)).ToArray();

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsIdentity => new[] { HueVsHue, HueVsSat, HueVsLum }.All(IsNeutral);

    public static bool IsNeutral(IReadOnlyList<CurvePoint> points) =>
        points.Count == 0 || points.All(p => Math.Abs(p.Y - NeutralY) < 1e-4);

    public IReadOnlyList<CurvePoint> Points(HueChannel channel) => channel switch {
        HueChannel.Hue => HueVsHue,
        HueChannel.Sat => HueVsSat,
        _ => HueVsLum,
    };

    public HueCurves With(HueChannel channel, IReadOnlyList<CurvePoint> points) => channel switch {
        HueChannel.Hue => this with { HueVsHue = [.. points] },
        HueChannel.Sat => this with { HueVsSat = [.. points] },
        _ => this with { HueVsLum = [.. points] },
    };

    /// A flat channel stores as empty — nothing to persist.
    public static IReadOnlyList<CurvePoint> NormalizeChannel(IReadOnlyList<CurvePoint> points) =>
        IsNeutral(points) ? [] : points;

    /// Cyclic piecewise-linear eval — wraps across the hue seam so the curve
    /// is seamless at 0/1.
    public static double Eval(IReadOnlyList<CurvePoint> points, double x) {
        var p = (points.Count == 0 ? DefaultPoints : points).OrderBy(q => q.X).ToList();
        var first = p[0];
        var last = p[^1];
        if (x < first.X) return Lerp(new CurvePoint(last.X - 1, last.Y), first, x);
        for (int i = 1; i < p.Count; i++)
            if (x <= p[i].X) return Lerp(p[i - 1], p[i], x);
        return Lerp(last, new CurvePoint(first.X + 1, first.Y), x);
    }

    static double Lerp(CurvePoint a, CurvePoint b, double x) {
        double t = b.X - a.X == 0 ? 0 : (x - a.X) / (b.X - a.X);
        return a.Y + (b.Y - a.Y) * t;
    }

    public string ToJson() => JsonSerializer.Serialize(this, CurveJson.Options);

    /// Absent or malformed JSON reads as the neutral curve.
    public static HueCurves Parse(string? json) {
        if (string.IsNullOrEmpty(json)) return new HueCurves();
        HueCurves? decoded;
        try {
            decoded = JsonSerializer.Deserialize<HueCurves>(json, CurveJson.Options);
        } catch (JsonException) {
            return new HueCurves();
        }
        if (decoded is null) return new HueCurves();
        return decoded with {
            HueVsHue = decoded.HueVsHue ?? [], HueVsSat = decoded.HueVsSat ?? [],
            HueVsLum = decoded.HueVsLum ?? [],
        };
    }
}
