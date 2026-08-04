using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PalmierShell.Core;

/// The one accent colour: timecode, playhead ruler marks, keyframes, snap
/// guides, the active timeline tab, and export progress. XAML consumers bind
/// `ThemeTimecodeBrush`; custom-drawn surfaces read `Current` and redraw on
/// `Changed`. Primary-action buttons never follow it — they keep the warm
/// off-white capsule.
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
    /// Mutating the shared brush updates every XAML binding in place.
    public static void Apply(string hex) {
        var color = Color.TryParse(string.IsNullOrWhiteSpace(hex) ? DefaultHex : hex, out var parsed)
            ? parsed : Color.Parse(DefaultHex);
        if (color == Current) return;
        Current = color;
        if (Application.Current is { } app &&
            app.TryFindResource("ThemeTimecodeBrush", out var resource) &&
            resource is SolidColorBrush brush)
            brush.Color = color;
        Changed?.Invoke();
    }
}
