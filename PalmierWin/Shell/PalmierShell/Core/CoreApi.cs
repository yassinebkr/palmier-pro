using System.Runtime.InteropServices;
using System.Text;

namespace PalmierShell.Core;

/// Thin P/Invoke over PalmierCoreHost.dll (Swift @_cdecl). Every create has a
/// matching destroy; handles are opaque IntPtrs.
public static partial class CoreApi {
    const string Dll = "PalmierCoreHost.dll";

    /// Probed before the first real P/Invoke so a missing PalmierCoreHost.dll
    /// (or a missing Swift/FFmpeg dependency) is an actionable startup message
    /// instead of a DllNotFoundException mid-constructor.
    public static bool TryLoadNativeHost() => TryLoadLibrary(Dll);

    /// Dev-only: native AV inside the Swift DLL (crash-handler verification).
    [LibraryImport(Dll)]
    public static partial void palmier_crash_test();

    /// Registers the native vectored crash handler (CCrashGuard) with the exe
    /// to spawn as the crash reporter on a fatal fault.
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf16)]
    public static partial void palmier_install_crash_guard(string reporterPath);

    internal static bool TryLoadLibrary(string name) {
        try {
            // The handle is deliberately not freed: unloading a Swift runtime
            // host and reloading it on first P/Invoke leaves the runtime in a
            // broken state — the app would hang before its window appears.
            return NativeLibrary.TryLoad(name, out _);
        } catch {
            return false;
        }
    }

    [LibraryImport(Dll)] public static partial IntPtr palmier_engine_create(IntPtr hwnd);
    [LibraryImport(Dll)] public static partial int palmier_engine_render_frame(IntPtr engine, int frame);
    [LibraryImport(Dll)] public static partial void palmier_engine_destroy(IntPtr engine);
    [LibraryImport(Dll)] public static partial int palmier_engine_set_project(IntPtr engine, IntPtr project);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_engine_set_selection(IntPtr engine, string? clipId);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_track_rename(IntPtr project, string trackId, string name);
    [LibraryImport(Dll)] public static partial double palmier_selection_handle_size();
    [LibraryImport(Dll)] public static partial double palmier_selection_rotate_offset();

    [LibraryImport(Dll)] public static partial IntPtr palmier_project_create();
    [LibraryImport(Dll)] public static partial void palmier_project_destroy(IntPtr project);

    [LibraryImport(Dll)] public static partial int palmier_project_timeline_count(IntPtr project);
    [LibraryImport(Dll)] public static partial int palmier_project_active_timeline(IntPtr project);
    [LibraryImport(Dll)] public static partial int palmier_project_set_active_timeline(IntPtr project, int index);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_project_add_timeline(IntPtr project, string name);
    [LibraryImport(Dll)] public static partial int palmier_project_remove_timeline(IntPtr project, int index);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_project_rename_timeline(IntPtr project, int index, string name);
    [LibraryImport(Dll)]
    public static partial int palmier_project_timeline_name(IntPtr project, int index, byte[] buf, int bufSize);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_project_set_preview_source(IntPtr project, string path);
    [LibraryImport(Dll)] public static partial void palmier_project_clear_preview_source(IntPtr project);

    /// The project's render size — the canvas preview, capture, and export
    /// composite at. Even dimensions, 16…7680; the setter returns 0 otherwise.
    /// A project setting, not an undoable timeline edit.
    [LibraryImport(Dll)] public static partial int palmier_project_set_render_size(IntPtr project, int width, int height);
    [LibraryImport(Dll)] public static partial int palmier_project_render_size(IntPtr project, out int width, out int height);

    public static string TimelineName(IntPtr project, int index) {
        var buf = new byte[256];
        return palmier_project_timeline_name(project, index, buf, buf.Length) == 1
            ? Encoding.UTF8.GetString(buf, 0, Array.IndexOf(buf, (byte)0))
            : $"Timeline {index + 1}";
    }

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_probe_media(string path, byte[] buf, int bufSize);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_timeline_add_clip(IntPtr project, string mediaPath, int durationFrames, byte[] idBuf, int idBufSize);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_timeline_remove_clip(IntPtr project, string clipId);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_set_transform(IntPtr project, string clipId, double centerX, double centerY, double width, double height, double rotation);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_set_opacity(IntPtr project, string clipId, double opacity);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_set_speed(IntPtr project, string clipId, double speed);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_set_volume_db(IntPtr project, string clipId, double db);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_trim(IntPtr project, string clipId, int edge, int boundaryFrame);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_timeline_roll_edit(IntPtr project, string leftClipId, string rightClipId, int boundaryFrame);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_timeline_move_clip_to_track(IntPtr project, string clipId, string trackId, int startFrame);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_timeline_add_track(IntPtr project, string kind, byte[] idBuf, int idBufSize);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_timeline_remove_track(IntPtr project, string trackId);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_unlink(IntPtr project, string clipId);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_link(IntPtr project, string clipIdA, string clipIdB);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    /// `timelineFrame` is in the timeline's domain at `timelineFps`, not the
    /// file's own rate — clip trims and durations are stored that way.
    public static partial int palmier_extract_frame(string path, int timelineFrame, int timelineFps,
                                                    byte[] buf, int bufSize);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int palmier_timeline_ripple_delete(IntPtr project, byte[] clipIds);

    /// Removes the clips (and their link groups) and closes the holes by
    /// shifting later clips left on every track that lost something. One call,
    /// one atomic intent. Returns how many clips were removed.
    public static int RippleDelete(IntPtr project, IEnumerable<string> clipIds) {
        // NUL-separated, double-NUL-terminated — the list crosses the ABI as
        // one argument so a multi-selection cannot half-apply.
        var bytes = new List<byte>();
        foreach (string id in clipIds) {
            bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(id));
            bytes.Add(0);
        }
        if (bytes.Count == 0) return 0;
        bytes.Add(0);
        return palmier_timeline_ripple_delete(project, bytes.ToArray());
    }

    /// Closes the empty span on a track, pulling everything after it left.
    /// Linked clips follow; refused when anything would collide.
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_timeline_close_gap(IntPtr project, string trackId,
                                                         int gapStart, int gapEnd);

    /// Deletes `[start, end)` across all tracks — trimming, splitting or
    /// removing whatever it crosses; `ripple` 1 pulls later clips left.
    [LibraryImport(Dll)]
    public static partial int palmier_timeline_delete_range(IntPtr project, int start, int end, int ripple);

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int palmier_timeline_copy_clips(IntPtr project, byte[] clipIds,
                                                           byte[] buf, int bufSize);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_timeline_paste(IntPtr project, string payload, int atFrame);

    /// Snapshot of the given clips as a clipboard payload — a value, so
    /// deleting the originals cannot invalidate it. Null when nothing resolved.
    public static string? CopyClips(IntPtr project, IEnumerable<string> clipIds) {
        var ids = new List<byte>();
        foreach (string id in clipIds) {
            ids.AddRange(System.Text.Encoding.UTF8.GetBytes(id));
            ids.Add(0);
        }
        if (ids.Count == 0) return null;
        ids.Add(0);
        var idsBytes = ids.ToArray();
        int result = palmier_timeline_copy_clips(project, idsBytes, [], 0);
        if (result >= 0) return null;
        var buf = new byte[-result];
        int written = palmier_timeline_copy_clips(project, idsBytes, buf, buf.Length);
        return written > 0 ? System.Text.Encoding.UTF8.GetString(buf, 0, written) : null;
    }

    /// Removes one property's keyframe at a timeline frame; 0 when there was
    /// none there.
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_remove_keyframe(IntPtr project, string clipId,
                                                           string property, int timelineFrame);

    /// Upserts one effect on a clip; `paramsJson` is a flat object of numbers
    /// and strings. Empty params ("{}") remove the effect. Types are the
    /// renderer's stable names ("detail.clarity", "color.lut", …).
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_set_effect(IntPtr project, string clipId,
                                                      string effectType, string paramsJson);

    /// Renders timeline `frame` composited — what the preview shows there —
    /// into `buf` as BGRA. Blocking GPU work; call off the UI thread.
    [LibraryImport(Dll)]
    public static partial int palmier_project_capture_frame(IntPtr project, int frame,
                                                            byte[] buf, int bufSize,
                                                            out int width, out int height);

    [LibraryImport(Dll)]
    private static unsafe partial int palmier_project_json(IntPtr project, byte* buf, int bufSize);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_project_load_json(IntPtr project, string json);

    /// Whole-project JSON (every timeline). Grows the buffer when the core
    /// reports it was too small, same contract as the timeline snapshot.
    public static unsafe string GetProjectJson(IntPtr project) {
        int size = 1 << 16;
        for (int attempt = 0; attempt < 3; attempt++) {
            var buf = new byte[size];
            fixed (byte* p = buf) {
                int written = palmier_project_json(project, p, size);
                if (written > 0) return Encoding.UTF8.GetString(buf, 0, written);
                if (written == 0) return "";
                size = -written;
            }
        }
        return "";
    }

    /// Appends a "video" or "audio" track; returns its id or null on failure.
    public static string? AddTrack(IntPtr project, string kind) {
        var buf = new byte[64];
        return palmier_timeline_add_track(project, kind, buf, buf.Length) == 1
            ? Encoding.UTF8.GetString(buf, 0, Array.IndexOf(buf, (byte)0))
            : null;
    }
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_set_fades(IntPtr project, string clipId, int fadeInFrames, int fadeOutFrames);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_timeline_add_text_clip(IntPtr project, string text, int startFrame, int durationFrames, byte[] idBuf, int idBufSize);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_set_text(IntPtr project, string clipId, string text);
    /// Patches a text clip's style; `styleJson` is a flat object with optional
    /// "fontSize" (positive number), "color" (hex), "alignment"
    /// ("left"/"center"/"right"). Malformed patches are refused wholesale.
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_set_text_style(IntPtr project, string clipId, string styleJson);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_add_keyframe(IntPtr project, string clipId, string property, int timelineFrame, double v1, double v2);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_clip_clear_keyframes(IntPtr project, string clipId, string property);

    /// Adds a text clip at `startFrame`. Returns its id or null.
    public static string? AddTextClip(IntPtr project, string text, int startFrame, int durationFrames) {
        var idBuf = new byte[64];
        if (palmier_timeline_add_text_clip(project, text, startFrame, durationFrames, idBuf, idBuf.Length) != 1) return null;
        int len = Array.IndexOf(idBuf, (byte)0) is >= 0 and var n ? n : idBuf.Length;
        return Encoding.UTF8.GetString(idBuf, 0, len);
    }
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_timeline_split_clip(IntPtr project, string clipId, int frame, byte[] idBuf, int idBufSize);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_timeline_move_clip(IntPtr project, string clipId, int newStartFrame);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_timeline_load_json(IntPtr project, string json);
    [LibraryImport(Dll)] private static unsafe partial int palmier_timeline_json(IntPtr project, byte* buf, int bufSize);

    /// Splits `clipId` at `frame`. Returns the right half's new clip id, or
    /// null when the frame isn't strictly inside the clip.
    public static string? SplitClip(IntPtr project, string clipId, int frame) {
        var idBuf = new byte[64];
        if (palmier_timeline_split_clip(project, clipId, frame, idBuf, idBuf.Length) != 1) return null;
        int len = Array.IndexOf(idBuf, (byte)0) is >= 0 and var n ? n : idBuf.Length;
        return Encoding.UTF8.GetString(idBuf, 0, len);
    }

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int palmier_detect_silence(string path, double thresholdDb,
                                                             int minSilenceMs, int paddingMs,
                                                             byte* buf, int bufSize);

    /// Silent spans of `path` in source-media milliseconds, or null when the
    /// file has no decodable audio. Blocking full decode — call off the UI
    /// thread.
    public static unsafe List<SilentRange>? DetectSilence(string path, double thresholdDb,
                                                          int minSilenceMs, int paddingMs) {
        int size = 1024;
        for (int attempt = 0; attempt < 3; attempt++) {
            var buf = new byte[size];
            fixed (byte* p = buf) {
                int written = palmier_detect_silence(path, thresholdDb, minSilenceMs, paddingMs,
                                                     p, size);
                if (written == 0) return null;
                if (written > 0)
                    return SilenceRemoval.ParseRanges(Encoding.UTF8.GetString(buf, 0, written));
                size = -written;
            }
        }
        return null;
    }

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_thumbnails(string path, byte[] buf, int bufSize, int count);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_waveform(string path, float[] buf, int columns);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_track_set_muted(IntPtr project, string trackId, int muted);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_track_set_hidden(IntPtr project, string trackId, int hidden);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_track_set_display_height(IntPtr project, string trackId, double height);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_track_set_gain_db(IntPtr project, string trackId, double gainDb);

    [LibraryImport(Dll)] public static partial IntPtr palmier_agent_create(IntPtr project);
    [LibraryImport(Dll)] public static partial void palmier_agent_destroy(IntPtr agent);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_agent_set_media(IntPtr agent, string json);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_agent_send(IntPtr agent, string message);
    [LibraryImport(Dll)] public static partial int palmier_agent_busy(IntPtr agent);
    [LibraryImport(Dll)] public static partial int palmier_agent_retry(IntPtr agent);
    [LibraryImport(Dll)] public static partial int palmier_agent_cancel(IntPtr agent);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_agent_configure(IntPtr agent, string provider, string apiKey, string model);
    [LibraryImport(Dll)] public static partial int palmier_agent_permission(IntPtr agent, int allow, int always);
    [LibraryImport(Dll)] public static partial int palmier_agent_refresh_models(IntPtr agent);
    [LibraryImport(Dll)] public static partial int palmier_agent_providers(byte[] buf, int bufSize);

    static readonly ProviderInfo[] FallbackProviders =
        [new ProviderInfo("anthropic", "Anthropic", "claude-opus-5")];

    /// The providers the core can talk to, in display order. Never empty — an
    /// unreadable list still leaves the UI with a usable default.
    public static IReadOnlyList<ProviderInfo> AgentProviders() {
        var buf = new byte[2048];
        if (palmier_agent_providers(buf, buf.Length) != 1) return FallbackProviders;
        string json = Encoding.UTF8.GetString(buf, 0, Array.IndexOf(buf, (byte)0));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var providers = doc.RootElement.EnumerateArray()
            .Select(e => new ProviderInfo(
                e.GetProperty("id").GetString() ?? "",
                e.GetProperty("name").GetString() ?? "",
                e.GetProperty("default_model").GetString() ?? "") {
                PublicModelList = e.TryGetProperty("public_model_list", out var open) && open.GetBoolean(),
            })
            .ToList();
        return providers.Count > 0 ? providers : FallbackProviders;
    }
    [LibraryImport(Dll)] private static unsafe partial int palmier_agent_poll(IntPtr agent, byte* buf, int bufSize);

    /// Drains pending agent events as a JSON array string, or null when none.
    public static string? PollAgent(IntPtr agent) {
        unsafe {
            int size = 16 * 1024;
            for (int attempt = 0; attempt < 4; attempt++) {
                var buf = new byte[size];
                fixed (byte* p = buf) {
                    int written = palmier_agent_poll(agent, p, size);
                    if (written == 0) return null;
                    if (written > 0) return Encoding.UTF8.GetString(buf, 0, written);
                    size = -written;
                }
            }
            return null;
        }
    }

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr palmier_export_start(IntPtr project, string path);
    [LibraryImport(Dll)] public static partial int palmier_export_status(IntPtr export);
    [LibraryImport(Dll)] public static partial int palmier_export_cancel(IntPtr export);
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_export_error(IntPtr export, byte[] buf, int bufSize);
    [LibraryImport(Dll)] public static partial void palmier_export_destroy(IntPtr export);

    /// Writes the active timeline as FCPXML (UTF-8 path). 1 on success, 0 on failure.
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_export_fcpxml(IntPtr project, string path);

    public static string GetExportError(IntPtr export) {
        var buf = new byte[512];
        if (palmier_export_error(export, buf, buf.Length) != 1) return "Export failed.";
        int len = Array.IndexOf(buf, (byte)0) is >= 0 and var n ? n : buf.Length;
        return Encoding.UTF8.GetString(buf, 0, len);
    }

    [LibraryImport(Dll)] public static partial IntPtr palmier_audio_create(IntPtr project);
    [LibraryImport(Dll)] public static partial void palmier_audio_destroy(IntPtr audio);
    [LibraryImport(Dll)] public static partial int palmier_audio_set_playing(IntPtr audio, int playing, int frame);
    [LibraryImport(Dll)] public static partial int palmier_audio_seek(IntPtr audio, int frame);
    [LibraryImport(Dll)] public static partial int palmier_audio_sync(IntPtr audio);
    /// Test seam: track gain folded into the clip's mix entry (NaN when not mixed).
    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial double palmier_audio_clip_track_gain(IntPtr audio, string clipId);
    /// Per-track peaks (max |sample| since the previous call, reset-on-read)
    /// as f32 in timeline track order, audio tracks only. Count, or 0.
    [LibraryImport(Dll)]
    public static partial int palmier_audio_track_peaks(IntPtr audio, float[] buf, int maxCount);

    /// Min/max pairs (2*columns floats), or null when the media has no audio.
    public static float[]? GetWaveform(string path, int columns) {
        var buf = new float[columns * 2];
        return palmier_waveform(path, buf, columns) == 1 ? buf : null;
    }

    public const int ThumbTileWidth = 96;
    public const int ThumbTileHeight = 54;

    /// Decodes `count` evenly-spaced 96x54 BGRA tiles. Returns the raw tile
    /// buffer and how many tiles were actually decoded, or null on failure.
    public static (byte[] Tiles, int Count)? GetThumbnails(string path, int count) {
        var buf = new byte[ThumbTileWidth * ThumbTileHeight * 4 * count];
        int decoded = palmier_thumbnails(path, buf, buf.Length, count);
        return decoded > 0 ? (buf, decoded) : null;
    }

    public readonly record struct MediaProbe(int Width, int Height, double Fps, int TotalFrames);

    /// Probes a media file. Returns null when the core can't open it.
    public static MediaProbe? ProbeMedia(string path) {
        var buf = new byte[128];
        if (palmier_probe_media(path, buf, buf.Length) != 1) return null;
        string text = Encoding.ASCII.GetString(buf, 0, Array.IndexOf(buf, (byte)0) is >= 0 and var n ? n : buf.Length);
        var parts = text.Split(',');
        if (parts.Length != 4) return null;
        return new MediaProbe(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]) / 100.0, int.Parse(parts[3]));
    }

    [LibraryImport(Dll, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int palmier_timeline_add_clip_at(IntPtr project, string mediaPath, int durationFrames, int startFrame, byte[] idBuf, int idBufSize);

    /// Adds a clip at the end of the video track. Returns the new clip's
    /// stable id, or null on failure.
    public static string? AddClip(IntPtr project, string mediaPath, int durationFrames) {
        var idBuf = new byte[64];
        if (palmier_timeline_add_clip(project, mediaPath, durationFrames, idBuf, idBuf.Length) <= 0) return null;
        int len = Array.IndexOf(idBuf, (byte)0) is >= 0 and var n ? n : idBuf.Length;
        return Encoding.UTF8.GetString(idBuf, 0, len);
    }

    /// Adds a clip starting at an explicit frame (drag-and-drop placement).
    public static string? AddClipAt(IntPtr project, string mediaPath, int durationFrames, int startFrame) {
        var idBuf = new byte[64];
        if (palmier_timeline_add_clip_at(project, mediaPath, durationFrames, startFrame, idBuf, idBuf.Length) <= 0) return null;
        int len = Array.IndexOf(idBuf, (byte)0) is >= 0 and var n ? n : idBuf.Length;
        return Encoding.UTF8.GetString(idBuf, 0, len);
    }

    public static string GetTimelineJson(IntPtr project) {
        unsafe {
            int probe = palmier_timeline_json(project, null, 0);
            if (probe >= 0) throw new InvalidOperationException($"palmier_timeline_json size probe failed ({probe})");
            int size = -probe;
            // The timeline can grow between the size probe and the fill; retry
            // with the newly reported size until it fits.
            for (int attempt = 0; attempt < 8; attempt++) {
                var buf = new byte[size];
                fixed (byte* p = buf) {
                    int written = palmier_timeline_json(project, p, size);
                    if (written > 0) return Encoding.UTF8.GetString(buf, 0, written);
                    if (written == 0) throw new InvalidOperationException("palmier_timeline_json failed");
                    size = -written;
                }
            }
            throw new InvalidOperationException("palmier_timeline_json: size kept changing");
        }
    }
}
