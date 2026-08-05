using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PalmierShell.Core;

/// The one accent colour: timecode, playhead ruler marks, keyframes, snap
/// guides, the active timeline tab, export progress, the user's badge, and
/// the fill of primary-action buttons. XAML consumers bind the theme brushes;
/// custom-drawn surfaces read `Current` and redraw on `Changed`.
public static class Accent {
    public static readonly (string Name, string Hex)[] Choices = [
        ("Amber", "#F29933"),
        ("Green", "#8FBF3F"),
        ("Teal", "#3AB7A8"),
        ("Cyan", "#3AB7C6"),
        ("Blue", "#5B8DEF"),
        ("Violet", "#8A6BD1"),
        ("Red", "#E0544F"),
        ("Coral", "#EF7E5B"),
        ("Warm White", "#F5EFE4"),
    ];

    public const string DefaultHex = "#F29933";

    public static Color Current { get; private set; } = Color.Parse(DefaultHex);

    public static event Action? Changed;

    /// Applies `hex` (empty or unparseable falls back to the built-in amber).
    /// Mutating the shared brushes updates every XAML binding in place.
    public static void Apply(string hex) {
        var color = Color.TryParse(string.IsNullOrWhiteSpace(hex) ? DefaultHex : hex, out var parsed)
            ? parsed : Color.Parse(DefaultHex);
        if (color == Current) return;
        Current = color;
        SetBrush("ThemeTimecodeBrush", color);
        SetBrush("ThemePrimaryBrush", color);
        SetBrush("ThemePrimaryHoverBrush", Shade(color, 0.12));
        SetBrush("ThemePrimaryPressedBrush", Shade(color, -0.12));
        SetBrush("ThemePrimaryTextBrush", ReadableText(color));
        Changed?.Invoke();
    }

    static void SetBrush(string key, Color color) {
        if (Application.Current is { } app &&
            app.TryFindResource(key, out var resource) &&
            resource is SolidColorBrush brush)
            brush.Color = color;
    }

    /// `amount` in -1…1: positive mixes toward white, negative toward black.
    public static Color Shade(Color color, double amount) {
        double t = Math.Clamp(Math.Abs(amount), 0, 1);
        int target = amount >= 0 ? 255 : 0;
        byte Mix(byte channel) => (byte)Math.Round(channel + (target - channel) * t);
        return Color.FromRgb(Mix(color.R), Mix(color.G), Mix(color.B));
    }

    /// The two text colours primary surfaces choose between, theme-dark first.
    public static readonly Color DarkText = Color.Parse("#1C1A17");
    public static readonly Color LightText = Color.Parse("#F5EFE4");

    /// The more readable of the two theme text colours on `fill`, by WCAG
    /// contrast ratio. Dark wins for every accent in the palette today.
    public static Color ReadableText(Color fill) =>
        Contrast(fill, DarkText) >= Contrast(fill, LightText) ? DarkText : LightText;

    static double Contrast(Color a, Color b) {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    static double Luminance(Color c) {
        double Channel(byte v) {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
