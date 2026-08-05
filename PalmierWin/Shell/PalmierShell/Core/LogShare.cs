using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace PalmierShell.Core;

/// "Report a problem" log sharing. Bundles the session/crash logs + system
/// info into a zip, redacts anything that looks like an API key, and uploads
/// to 0x0.st (no account; links expire ~30 days) for the user to send us.
/// The upload URL is replaceable — the VPS endpoint lands there later.
public static partial class LogShare {
    public static string UploadEndpoint = "https://0x0.st";

    /// Zips recent logs + a system-info header into `zipPath`.
    public static int CollectLogs(string zipPath, string? userNote = null) {
        string logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PalmierPro", "logs");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var info = new StringBuilder()
            .AppendLine($"PalmierWin {AppVersion} diagnostic bundle")
            .AppendLine($"collected: {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine($"os: {Environment.OSVersion}")
            .AppendLine($"64-bit: {Environment.Is64BitOperatingSystem}")
            .AppendLine($"machine: {Environment.MachineName}");
        if (userNote is { Length: > 0 })
            info.AppendLine($"note: {userNote}");
        AddText(zip, "info.txt", info.ToString());

        int count = 0;
        if (Directory.Exists(logsDir)) {
            var files = Directory.GetFiles(logsDir, "*.log")
                .OrderByDescending(File.GetLastWriteTime).Take(10);
            foreach (var file in files) {
                AddText(zip, Path.GetFileName(file), Redact(File.ReadAllText(file)));
                count++;
            }
        }
        return count;
    }

    /// Uploads the bundle. Returns the shareable URL.
    public static async Task<string> UploadAsync(string zipPath, CancellationToken ct = default) {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(zipPath, ct));
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(bytes, "file", Path.GetFileName(zipPath));
        using var req = new HttpRequestMessage(HttpMethod.Put, UploadEndpoint) { Content = form };
        req.Headers.UserAgent.ParseAdd($"PalmierWin/{AppVersion}");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadAsStringAsync(ct)).Trim();
    }

    public static string AppVersion =>
        typeof(LogShare).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.1.0";

    /// Strips API-key-shaped strings so a bundle never carries credentials.
    public static string Redact(string text) =>
        KeyPattern().Replace(text, "<redacted>");

    [GeneratedRegex(@"(sk-[A-Za-z0-9_-]{8,}|Bearer\s+\S+|x-api-key[""':\s]*\S+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeyPattern();

    static void AddText(ZipArchive zip, string name, string content) {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
