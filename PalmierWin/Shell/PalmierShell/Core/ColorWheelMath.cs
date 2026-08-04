namespace PalmierShell.Core;

/// Mapping between a color wheel's gesture state and the `color.wheels`
/// kernel parameters. A wheel is a hue-saturation disc plus a luma slider:
///
///   vector v = (x, y), |v| ≤ 1 — hue θ = atan2(y, x), amount a = |v|
///   slider s ∈ [-1, 1] — moves all three channels together
///
/// The hue direction is the pure HSV hue with its mean removed, scaled so the
/// strongest channel is ±1 (zero-sum, so hue drags barely touch luma):
///
///   d(θ) = (h(θ) − mean(h)) / max|h − mean(h)|,  h = HSV(θ, 1, 1)
///
/// Each wheel applies the same shape around its own neutral:
///
///   lift.c  = LiftRange  · (a·d.c + s)      additive around 0
///   gain.c  = 1 + GainRange  · (a·d.c + s)  multiplier around 1
///   gamma.c = 1 + GammaRange · (a·d.c + s)  multiplier around 1
///
/// `ToVector` inverts the mapping so a refreshed inspector can place the
/// handle where the committed params left it: the slider takes the mean
/// deviation, the hue direction takes what remains.
public static class ColorWheelMath {
    public enum WheelKind { Lift, Gain, Gamma }

    public const double LiftRange = 0.25;
    public const double GainRange = 0.5;
    public const double GammaRange = 0.5;

    public static double Neutral(WheelKind kind) => kind == WheelKind.Lift ? 0 : 1;

    static double Range(WheelKind kind) => kind switch {
        WheelKind.Lift => LiftRange,
        WheelKind.Gain => GainRange,
        _ => GammaRange,
    };

    /// Wheel gesture → kernel triplet for one wheel.
    public static (double R, double G, double B) ToParams(
        WheelKind kind, double x, double y, double offset) {
        double amount = Math.Sqrt(x * x + y * y);
        double range = Range(kind);
        double neutral = Neutral(kind);
        double luma = range * Math.Clamp(offset, -1, 1);
        if (amount < 1e-9)
            return (neutral + luma, neutral + luma, neutral + luma);
        var (dr, dg, db) = HueDirection(Math.Atan2(y, x));
        double scale = range * amount;
        return (neutral + luma + scale * dr,
                neutral + luma + scale * dg,
                neutral + luma + scale * db);
    }

    /// Kernel triplet → wheel gesture (inverse of ToParams, clamped to the
    /// disc). Values the mapping cannot express land at the disc's edge.
    public static (double X, double Y, double Offset) ToVector(
        WheelKind kind, double r, double g, double b) {
        double range = Range(kind);
        double neutral = Neutral(kind);
        double dr = r - neutral, dg = g - neutral, db = b - neutral;
        double offset = Math.Clamp((dr + dg + db) / 3 / range, -1, 1);
        dr -= offset * range; dg -= offset * range; db -= offset * range;
        double amount = Math.Clamp(new[] { dr, dg, db }.Select(Math.Abs).Max() / range, 0, 1);
        if (amount < 1e-9) return (0, 0, offset);
        double hue = HueOf(dr, dg, db);
        return (amount * Math.Cos(hue), amount * Math.Sin(hue), offset);
    }

    /// Zero-sum unit direction for a hue angle, per the header comment.
    public static (double R, double G, double B) HueDirection(double angle) {
        // HSV → RGB at full saturation and value.
        double h = ((angle * 180 / Math.PI) % 360 + 360) % 360 / 60;
        double f = h - Math.Floor(h);
        double q = 1 - f;
        var (r, g, b) = ((int)Math.Floor(h) % 6) switch {
            0 => (1.0, f, 0.0),
            1 => (q, 1.0, 0.0),
            2 => (0.0, 1.0, f),
            3 => (0.0, q, 1.0),
            4 => (f, 0.0, 1.0),
            _ => (1.0, 0.0, q),
        };
        double mean = (r + g + b) / 3;
        r -= mean; g -= mean; b -= mean;
        double max = new[] { r, g, b }.Select(Math.Abs).Max();
        return (r / max, g / max, b / max);
    }

    /// Hue angle of a (possibly zero-sum) RGB direction.
    static double HueOf(double r, double g, double b) {
        double min = Math.Min(r, Math.Min(g, b));
        r -= min; g -= min; b -= min;
        double max = Math.Max(r, Math.Max(g, b));
        if (max < 1e-9) return 0;
        double d = max;
        double h;
        if (max == r) h = ((g - b) / d % 6 + 6) % 6;
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return h * Math.PI / 3;
    }
}
