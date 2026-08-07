using System.Text.Json.Nodes;
using PalmierShell.ViewModels;

namespace PalmierShell.Core.Mcp;

/// Outcome of one tool execution: the receipt text (structured JSON on
/// success, a plain reason on failure), whether it failed, and a short
/// summary for the session log.
public sealed record McpToolResult(bool IsError, string Text, string Summary);

/// The ten editing tools, mirroring the engine-side agent host
/// (AgentHost.swift): same names, parameters, validation order, and error
/// strings, so external MCP clients get exactly the inline agent's capability
/// set. Execution routes through the same CoreApi + TimelineViewModel
/// operations the UI drives, every mutating call wrapped as exactly one
/// UndoStack intent — failed, refused, and no-op calls create none.
public sealed class McpTools {
    readonly Func<IntPtr> project;
    readonly TimelineViewModel timeline;
    readonly Func<IReadOnlyList<MediaItemViewModel>> mediaItems;
    readonly UndoStack undo;

    public McpTools(Func<IntPtr> project, TimelineViewModel timeline,
                    Func<IReadOnlyList<MediaItemViewModel>> mediaItems, UndoStack undo) {
        this.project = project;
        this.timeline = timeline;
        this.mediaItems = mediaItems;
        this.undo = undo;
    }

    public static readonly string[] ToolNames = [
        "get_timeline", "list_media", "add_clip", "add_text_clip", "remove_clip",
        "split_clip", "move_clip", "trim_clip", "set_clip_properties", "set_playhead",
    ];

    /// MCP `inputSchema` for every tool. Rebuilt per call: construction is
    /// trivially cheap and JsonNode graphs cannot be shared across responses.
    public static JsonArray ToolSchemas() {
        static JsonObject Prop(string type, string? description = null) {
            var p = new JsonObject { ["type"] = type };
            if (description is not null) p["description"] = description;
            return p;
        }
        static JsonObject Schema(JsonObject properties, params string[] required) {
            var s = new JsonObject { ["type"] = "object", ["properties"] = properties };
            if (required.Length > 0)
                s["required"] = new JsonArray(required.Select<string, JsonNode>(r => r).ToArray());
            else
                s["required"] = new JsonArray();
            return s;
        }
        static JsonObject Tool(string name, string description, JsonObject inputSchema) =>
            new() { ["name"] = name, ["description"] = description, ["inputSchema"] = inputSchema };

        var trimEdge = Prop("string");
        trimEdge["enum"] = new JsonArray("left", "right");
        return [
            Tool("get_timeline",
                "Read the current timeline: tracks, clips with their stable ids, frame positions, and properties. Call this before any edit so you act on current state.",
                Schema(new JsonObject())),
            Tool("list_media",
                "List the media library: files the user imported, with path, name, and duration in timeline frames. Use these paths with add_clip.",
                Schema(new JsonObject())),
            Tool("add_clip",
                "Add a media file from the library to the timeline. Omit start_frame to append at the end of the video track; placing over existing clips overwrites that region. Linked audio is added automatically when the source has sound.",
                Schema(new JsonObject {
                    ["media_path"] = Prop("string", "Path from list_media"),
                    ["start_frame"] = Prop("integer", "Timeline frame; omit to append"),
                    ["duration_frames"] = Prop("integer", "Omit to use the full media length"),
                }, "media_path")),
            Tool("add_text_clip",
                "Add a text/title clip on the video track.",
                Schema(new JsonObject {
                    ["text"] = Prop("string"),
                    ["start_frame"] = Prop("integer"),
                    ["duration_frames"] = Prop("integer", "Default 120 (4 seconds)"),
                }, "text", "start_frame")),
            Tool("remove_clip",
                "Remove a clip from the timeline by its stable id.",
                Schema(new JsonObject { ["clip_id"] = Prop("string") }, "clip_id")),
            Tool("split_clip",
                "Blade a clip in two at a timeline frame strictly inside it. The left half keeps the id; the result reports the new right-half id.",
                Schema(new JsonObject {
                    ["clip_id"] = Prop("string"),
                    ["frame"] = Prop("integer"),
                }, "clip_id", "frame")),
            Tool("move_clip",
                "Move a clip (and its linked audio) to a new start frame. The move clamps flush against neighboring clips instead of overlapping them; the result reports the actual position.",
                Schema(new JsonObject {
                    ["clip_id"] = Prop("string"),
                    ["start_frame"] = Prop("integer"),
                }, "clip_id", "start_frame")),
            Tool("trim_clip",
                "Trim a clip edge to a timeline frame. edge is \"left\" (in-point) or \"right\" (out-point). Clamped to at least 1 frame, neighbors, and the source length; linked audio follows.",
                Schema(new JsonObject {
                    ["clip_id"] = Prop("string"),
                    ["edge"] = trimEdge,
                    ["frame"] = Prop("integer"),
                }, "clip_id", "edge", "frame")),
            Tool("set_clip_properties",
                "Set one or more properties on a clip. Only provided fields change. center_x/center_y are 0-1 canvas coordinates, width/height are canvas fractions, rotation is degrees, opacity 0-1, speed 0.01-100, volume_db -96 to 12, fades in seconds, text replaces a text clip's content.",
                Schema(new JsonObject {
                    ["clip_id"] = Prop("string"),
                    ["center_x"] = Prop("number"), ["center_y"] = Prop("number"),
                    ["width"] = Prop("number"), ["height"] = Prop("number"),
                    ["rotation"] = Prop("number"), ["opacity"] = Prop("number"),
                    ["speed"] = Prop("number"), ["volume_db"] = Prop("number"),
                    ["fade_in_seconds"] = Prop("number"), ["fade_out_seconds"] = Prop("number"),
                    ["text"] = Prop("string"),
                }, "clip_id")),
            Tool("set_playhead",
                "Move the preview playhead to a timeline frame so the user sees that moment.",
                Schema(new JsonObject { ["frame"] = Prop("integer") }, "frame")),
        ];
    }

    public McpToolResult Execute(string name, JsonObject args) => name switch {
        "get_timeline" => GetTimeline(),
        "list_media" => ListMedia(),
        "add_clip" => AddClip(args),
        "add_text_clip" => AddTextClip(args),
        "remove_clip" => RemoveClip(args),
        "split_clip" => SplitClip(args),
        "move_clip" => MoveClip(args),
        "trim_clip" => TrimClip(args),
        "set_clip_properties" => SetClipProperties(args),
        "set_playhead" => SetPlayhead(args),
        _ => Error($"Unknown tool {name}."),
    };

    static McpToolResult Error(string message) => new(true, message, message);

    static McpToolResult Receipt(JsonObject receipt, string summary) =>
        new(false, receipt.ToJsonString(), summary);

    static string? ArgString(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// Integers arrive as JSON numbers; a fractional value truncates toward
    /// zero, matching the agent host's `Int($0)` coercion.
    static int? ArgInt(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d)
            ? (int)d : null;

    static double? ArgNumber(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d)
            ? d : null;

    ClipState? FindClip(string clipId) => timeline.State?.FindClip(clipId);

    McpToolResult GetTimeline() {
        string json;
        try {
            json = timeline.CaptureSnapshot();
        } catch (InvalidOperationException) {
            return Error("Could not read the timeline.");
        }
        int clips = timeline.State?.Tracks.Sum(t => t.Clips.Count) ?? 0;
        return new McpToolResult(false, json, $"{clips} clip{(clips == 1 ? "" : "s")}");
    }

    McpToolResult ListMedia() {
        var items = mediaItems();
        if (items.Count == 0)
            return new McpToolResult(false,
                "The media library is empty — the user has not imported any files.", "empty library");
        var array = new JsonArray();
        foreach (var item in items) {
            array.Add(new JsonObject {
                ["path"] = item.Path,
                ["name"] = item.Name,
                ["duration_frames"] = TimelineViewModel.TimelineFramesFor(item),
                ["width"] = item.Width,
                ["height"] = item.Height,
            });
        }
        return new McpToolResult(false, array.ToJsonString(),
            $"{items.Count} item{(items.Count == 1 ? "" : "s")}");
    }

    McpToolResult AddClip(JsonObject args) {
        if (ArgString(args, "media_path") is not { } path)
            return Error("media_path is required.");
        int duration = ArgInt(args, "duration_frames") ?? 0;
        if (duration <= 0) {
            if (TimelineViewModel.TimelineFramesFor(path) is not { } probed)
                return Error($"Could not read {path} — is the path exactly as list_media reported?");
            duration = probed;
        }
        string? id = null;
        bool ok = ArgInt(args, "start_frame") is { } start
            ? undo.Execute("Add Clip", () => (id = CoreApi.AddClipAt(project(), path, duration, start)) is not null)
            : undo.Execute("Add Clip", () => (id = CoreApi.AddClip(project(), path, duration)) is not null);
        if (!ok || id is null)
            return Error("Adding the clip failed (invalid path or position).");
        timeline.Reload();
        return Receipt(new JsonObject {
            ["clip_id"] = id,
            ["start_frame"] = FindClip(id)?.StartFrame ?? 0,
            ["duration_frames"] = duration,
        }, $"clip {id} · {duration} frames");
    }

    McpToolResult AddTextClip(JsonObject args) {
        if (ArgString(args, "text") is not { } text || ArgInt(args, "start_frame") is not { } start)
            return Error("text and start_frame are required.");
        int duration = ArgInt(args, "duration_frames") ?? 120;
        string? id = null;
        bool ok = undo.Execute("Add Text",
            () => (id = CoreApi.AddTextClip(project(), text, start, duration)) is not null);
        if (!ok || id is null)
            return Error("Adding the text clip failed.");
        timeline.Reload();
        return Receipt(new JsonObject {
            ["clip_id"] = id,
            ["start_frame"] = start,
            ["duration_frames"] = duration,
        }, $"text clip {id} @ {start}");
    }

    McpToolResult RemoveClip(JsonObject args) {
        if (ArgString(args, "clip_id") is not { } clipId)
            return Error("clip_id is required.");
        if (!undo.Execute("Delete Clip", () => timeline.RemoveClip(clipId)))
            return Error($"No clip with id {clipId}. Call get_timeline for current ids.");
        return Receipt(new JsonObject { ["removed"] = clipId }, $"removed {clipId}");
    }

    McpToolResult SplitClip(JsonObject args) {
        if (ArgString(args, "clip_id") is not { } clipId || ArgInt(args, "frame") is not { } frame)
            return Error("clip_id and frame are required.");
        string? rightId = null;
        bool ok = undo.Execute("Split Clip",
            () => (rightId = timeline.SplitClip(clipId, frame)) is not null);
        if (!ok || rightId is null)
            return Error($"Split failed — frame {frame} is not strictly inside that clip.");
        return Receipt(new JsonObject {
            ["clip_id"] = clipId,
            ["right_clip_id"] = rightId,
            ["frame"] = frame,
        }, $"split {clipId} @ {frame}");
    }

    McpToolResult MoveClip(JsonObject args) {
        if (ArgString(args, "clip_id") is not { } clipId || ArgInt(args, "start_frame") is not { } start)
            return Error("clip_id and start_frame are required.");
        int? before = FindClip(clipId)?.StartFrame;
        if (!undo.Execute("Move Clip",
                () => CoreApi.palmier_timeline_move_clip(project(), clipId, start) == 1))
            return Error("Move failed — unknown clip or negative frame.");
        timeline.Reload();
        int actual = FindClip(clipId)?.StartFrame ?? start;
        bool noOp = before == actual;
        return Receipt(new JsonObject {
            ["clip_id"] = clipId,
            ["start_frame"] = actual,
            ["requested_frame"] = start,
            ["no_op"] = noOp,
        }, noOp ? $"{clipId} already @ {actual}" : $"{clipId} → {actual}");
    }

    McpToolResult TrimClip(JsonObject args) {
        if (ArgString(args, "clip_id") is not { } clipId ||
            ArgString(args, "edge") is not { } edge || edge is not ("left" or "right") ||
            ArgInt(args, "frame") is not { } frame)
            return Error("clip_id, edge (left|right), and frame are required.");
        if (!undo.Execute("Trim Clip",
                () => CoreApi.palmier_clip_trim(project(), clipId, edge == "left" ? 0 : 1, frame) == 1))
            return Error("Trim failed — unknown clip.");
        timeline.Reload();
        var clip = FindClip(clipId);
        return Receipt(new JsonObject {
            ["clip_id"] = clipId,
            ["start_frame"] = clip?.StartFrame ?? 0,
            ["end_frame"] = clip?.EndFrame ?? 0,
        }, $"trim {clipId} {edge} → {frame}");
    }

    static readonly string[] TransformKeys = ["center_x", "center_y", "width", "height", "rotation"];
    static readonly string[] FadeKeys = ["fade_in_seconds", "fade_out_seconds"];

    McpToolResult SetClipProperties(JsonObject args) {
        if (ArgString(args, "clip_id") is not { } clipId)
            return Error("clip_id is required.");
        if (FindClip(clipId) is not { } clip)
            return Error($"No clip with id {clipId}. Call get_timeline for current ids.");
        bool anyProvided = TransformKeys.Any(k => args[k] is not null) ||
                           FadeKeys.Any(k => args[k] is not null) ||
                           args["opacity"] is not null || args["speed"] is not null ||
                           args["volume_db"] is not null || args["text"] is not null;
        if (!anyProvided)
            return new McpToolResult(false, "No properties were provided — nothing changed.", "no-op");

        var applied = new List<string>();
        var failures = new List<string>();
        void Record(bool ok, string label) => (ok ? applied : failures).Add(label);
        bool Commit() {
            IntPtr p = project();
            if (TransformKeys.Any(k => args[k] is not null))
                Record(CoreApi.palmier_clip_set_transform(p, clipId,
                    ArgNumber(args, "center_x") ?? clip.Transform.CenterX,
                    ArgNumber(args, "center_y") ?? clip.Transform.CenterY,
                    ArgNumber(args, "width") ?? clip.Transform.Width,
                    ArgNumber(args, "height") ?? clip.Transform.Height,
                    ArgNumber(args, "rotation") ?? clip.Transform.Rotation) == 1, "transform");
            if (ArgNumber(args, "opacity") is { } opacity)
                Record(CoreApi.palmier_clip_set_opacity(p, clipId, opacity) == 1, "opacity");
            if (ArgNumber(args, "speed") is { } speed)
                Record(CoreApi.palmier_clip_set_speed(p, clipId, speed) == 1, "speed");
            if (ArgNumber(args, "volume_db") is { } db)
                Record(CoreApi.palmier_clip_set_volume_db(p, clipId, db) == 1, "volume");
            if (FadeKeys.Any(k => args[k] is not null)) {
                int fadeIn = Math.Max(0, (int)Math.Round(
                    (ArgNumber(args, "fade_in_seconds") ?? clip.FadeInFrames / 30.0) * 30));
                int fadeOut = Math.Max(0, (int)Math.Round(
                    (ArgNumber(args, "fade_out_seconds") ?? clip.FadeOutFrames / 30.0) * 30));
                Record(CoreApi.palmier_clip_set_fades(p, clipId, fadeIn, fadeOut) == 1, "fades");
            }
            if (ArgString(args, "text") is { } text)
                Record(CoreApi.palmier_clip_set_text(p, clipId, text) == 1, "text");
            return applied.Count > 0;
        }
        undo.Execute("Set Clip Properties", Commit);
        timeline.Reload();
        return new McpToolResult(failures.Count > 0 && applied.Count == 0, new JsonObject {
            ["clip_id"] = clipId,
            ["applied"] = new JsonArray(applied.Select<string, JsonNode>(a => a).ToArray()),
            ["rejected"] = new JsonArray(failures.Select<string, JsonNode>(f => f).ToArray()),
        }.ToJsonString(), $"{clipId}: {applied.Count} applied, {failures.Count} rejected");
    }

    McpToolResult SetPlayhead(JsonObject args) {
        if (ArgInt(args, "frame") is not { } frame || frame < 0)
            return Error("frame must be a non-negative integer.");
        // Scrub clamps to the timeline extent — playback state, no undo entry.
        timeline.Scrub(frame);
        return Receipt(new JsonObject { ["frame"] = timeline.PlayheadFrame },
            $"playhead @ {timeline.PlayheadFrame}");
    }
}
