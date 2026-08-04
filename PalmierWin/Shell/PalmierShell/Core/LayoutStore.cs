using System.Text.Json;

namespace PalmierShell.Core;

/// Window size and panel sizes in device-independent units; the window origin
/// is in screen pixels (NaN = let the platform place the window).
public sealed record WorkspaceLayout(
    double WindowWidth, double WindowHeight, double WindowX, double WindowY, bool Maximized,
    double AgentWidth, double MediaWidth, double InspectorWidth, double TimelineHeight) {
    public static readonly WorkspaceLayout Default =
        new(1440, 900, double.NaN, double.NaN, false, 230, 280, 340, 240);

    /// Agent panel collapsed to its icon rail.
    public bool AgentCollapsed { get; init; }

    /// Rejects values a corrupt or stale file could carry (off-screen origins,
    /// degenerate sizes) so a bad layout can never make the window unusable.
    public WorkspaceLayout Sanitised() => new(
        Clamp(WindowWidth, 960, 12000, Default.WindowWidth),
        Clamp(WindowHeight, 600, 12000, Default.WindowHeight),
        double.IsFinite(WindowX) ? WindowX : double.NaN,
        double.IsFinite(WindowY) ? WindowY : double.NaN,
        Maximized,
        Clamp(AgentWidth, 170, 420, Default.AgentWidth),
        Clamp(MediaWidth, 220, 520, Default.MediaWidth),
        Clamp(InspectorWidth, 240, 520, Default.InspectorWidth),
        Clamp(TimelineHeight, 140, 900, Default.TimelineHeight)) { AgentCollapsed = AgentCollapsed };

    static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// Persists the workspace layout under %APPDATA%\PalmierPro. Synchronous file
/// I/O — call off the UI thread.
public static class LayoutStore {
    /// Test seam: redirects the layout file. Never set in production code.
    public static string? PathOverride;

    static string LayoutPath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PalmierPro", "layout.json");

    public static WorkspaceLayout Load() {
        try {
            if (!File.Exists(LayoutPath)) return WorkspaceLayout.Default;
            return JsonSerializer.Deserialize<WorkspaceLayout>(File.ReadAllText(LayoutPath))
                ?.Sanitised() ?? WorkspaceLayout.Default;
        } catch {
            return WorkspaceLayout.Default;
        }
    }

    public static void Save(WorkspaceLayout layout) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
            File.WriteAllText(LayoutPath, JsonSerializer.Serialize(layout.Sanitised()));
        } catch {
            // Layout is a convenience; a failed write must not break shutdown.
        }
    }
}
