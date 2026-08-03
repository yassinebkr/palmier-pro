using System.Text;
using System.Text.Json;

namespace PalmierShell.Core;

/// A media library entry as saved: where the file is and which folder it sits
/// in. Media stays where the user put it — a `.palmier` file references paths
/// rather than copying media into a package.
public sealed record SavedMedia(string Path, string Folder);

/// On-disk project. `Core` is whatever palmier_project_json produced, kept
/// opaque here so the core owns its own format.
public sealed record ProjectDocument(int Version, string Core) {
    public const int CurrentVersion = 1;

    public List<SavedMedia> Media { get; init; } = [];
    public List<string> Folders { get; init; } = [];
}

/// Reads and writes `.palmier` project files. All calls are blocking file
/// I/O — keep them off the UI thread.
public static class ProjectStore {
    public const string Extension = "palmier";

    /// Writes through a temp file and swaps it in, so a crash mid-save cannot
    /// leave a half-written project where a good one used to be.
    public static void Save(string path, ProjectDocument document) {
        string staging = path + ".saving";
        File.WriteAllText(staging,
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
        File.Move(staging, path, overwrite: true);
    }

    /// Null when the file is missing, malformed, or from a newer build.
    public static ProjectDocument? Load(string path) {
        try {
            if (!File.Exists(path)) return null;
            var document = JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(path));
            return document is null || document.Version > ProjectDocument.CurrentVersion ? null : document;
        } catch {
            return null;
        }
    }

    /// Where unsaved work is parked between autosaves.
    public static string RecoveryPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PalmierPro", "recovery." + Extension);

    public static void SaveRecovery(ProjectDocument document) {
        try {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RecoveryPath)!);
            Save(RecoveryPath, document);
        } catch {
            // Autosave is best effort; a failure must not interrupt editing.
        }
    }

    public static void ClearRecovery() {
        try {
            if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath);
        } catch {
            // Nothing useful to do; the stale file is harmless.
        }
    }
}
