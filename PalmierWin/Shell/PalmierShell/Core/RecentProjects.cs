using System.Text.Json;

namespace PalmierShell.Core;

/// The Open Recent list, newest first. Blocking file I/O — call off the UI
/// thread.
public static class RecentProjects {
    const int Limit = 10;

    static string ListPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PalmierPro", "recent.json");

    /// Entries whose file has since been deleted or moved are dropped, so the
    /// menu never offers something that cannot open.
    public static IReadOnlyList<string> Load() {
        try {
            if (!File.Exists(ListPath)) return [];
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ListPath)) ?? [];
            return paths.Where(File.Exists).Take(Limit).ToList();
        } catch {
            return [];
        }
    }

    public static void Add(string path) {
        try {
            var paths = Load().Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                              .Prepend(path)
                              .Take(Limit)
                              .ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(ListPath)!);
            File.WriteAllText(ListPath, JsonSerializer.Serialize(paths));
        } catch {
            // A missing recent list is a small loss; never break a save over it.
        }
    }
}
