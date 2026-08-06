using System.Security.Cryptography;
using System.Text;

namespace PalmierShell.Core;

/// Disk-cached, semaphore-bounded waveform extraction. One extraction per
/// media ever: the raw min/max floats persist under Waveforms/, keyed by
/// path+size+mtime+columns+algorithm version, so a reload skips decoding.
/// Callers invoke off the UI thread — the cache-hit path reads synchronously.
public static class WaveformCache {
    /// Test seam: redirects the cache directory. Never set in production code.
    public static string? DirectoryOverride;
    /// Test seam: replaces the native decode. Never set in production code.
    public static Func<string, int, float[]?>? DecodeOverride;

    // Bump when the extraction algorithm changes; stale files become misses.
    const string AlgorithmVersion = "v1";

    static string Dir => DirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PalmierPro", "Waveforms");

    static readonly SemaphoreSlim Gate = new(2);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<float[]?>> InFlight = new();

    public static async Task<float[]?> GetAsync(string path, int columns, CancellationToken ct = default) {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        string file = Path.Combine(Dir, Key(path, columns) + ".wf");
        if (TryRead(file, columns, out var cached)) return cached;
        // Identical requests share one decode; the gate stays for distinct media.
        // The shared task carries the first caller's ct: if that caller cancels,
        // later awaiters of the same task may see OperationCanceledException.
        var task = InFlight.GetOrAdd(file, _ => DecodeGated(file, path, columns, ct));
        try {
            return await task;
        } finally {
            InFlight.TryRemove(file, out _);
        }
    }

    static async Task<float[]?> DecodeGated(string file, string path, int columns, CancellationToken ct) {
        await Gate.WaitAsync(ct);
        try {
            if (TryRead(file, columns, out var cached)) return cached;  // filled while we waited
            // ct only cancels pre-start; a mid-decode cancellation still completes
            // and caches — the work outlives the caller.
            var floats = await Task.Run(
                () => (DecodeOverride ?? CoreApi.GetWaveform)(path, columns), ct);
            if (floats is null) return null;
            WriteAtomic(file, floats);
            return floats;
        } finally {
            Gate.Release();
        }
    }

    static string Key(string path, int columns) {
        var info = new FileInfo(path);
        // Missing media keys as length 0: decode then fails and nothing is cached.
        long length = info.Exists ? info.Length : 0;
        string seed = $"{path}|{length}|{info.LastWriteTimeUtc.Ticks}|{columns}|{AlgorithmVersion}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..32];
    }

    static bool TryRead(string file, int columns, out float[] floats) {
        floats = [];
        try {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length != columns * 2 * sizeof(float)) return false;  // stale/corrupt: treat as miss
            floats = new float[columns * 2];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return true;
        } catch {
            return false;
        }
    }

    static void WriteAtomic(string file, float[] floats) {
        try {
            Directory.CreateDirectory(Dir);
            var bytes = new byte[floats.Length * sizeof(float)];
            Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
            string tmp = file + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, file, true);
        } catch {
            // A cache that cannot persist only costs a re-decode next run.
        }
    }
}
