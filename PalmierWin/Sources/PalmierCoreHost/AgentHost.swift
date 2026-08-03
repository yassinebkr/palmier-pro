import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif
import PalmierCore

// Agent ABI: the editing agent behind the poll-based contract from
// docs/windows-shell-design.md (no callbacks across the ABI). The shell sends
// a message; a background thread runs the tool-use loop, executing tools
// against the project through the same intent functions the UI uses; the
// shell polls JSON events to render chat, tool chips, and timeline refreshes.
//
// Providers live in AgentProvider.swift: Anthropic speaks the Messages API,
// the rest speak OpenAI chat/completions. Both transports hand back
// Anthropic-shaped blocks, so the loop below never branches on provider.

let agentMaxTokens = 16000
private let agentMaxToolLoops = 25

/// Tools that mutate destructively enough to require user confirmation.
private let agentDestructiveTools: Set<String> = ["remove_clip"]

let agentSystemPrompt = """
You are the editing agent inside Palmier Pro, a video editor for filmmakers. \
You edit the user's timeline directly through tools.

Rules:
- The timeline runs at 30 frames per second. All frame arguments are timeline \
frames (1 second = 30 frames).
- Call get_timeline before editing so you act on current state, and list_media \
to see what footage is available. Clip ids from get_timeline are the only \
valid ids.
- Perform the requested edit fully, then summarize what changed in one or two \
plain sentences. Keep responses focused, brief, and concise.
- If a request is impossible (no such clip, no media), say so plainly instead \
of guessing.
"""

/// Retained agent state behind the opaque handle. All mutable state is
/// guarded by `lock` (UI thread + agent thread); Sendable is safe under that
/// invariant.
final class AgentContext: @unchecked Sendable {
    let project: ProjectContext
    let lock = NSLock()
    var events: [String] = []
    var busy = false
    var conversation: [[String: Any]] = []
    var mediaJSON: [[String: Any]] = []
    var configuredKey: String?
    var provider = AgentProvider.all[0]
    var model = AgentProvider.all[0].defaultModel
    var refreshingModels = false
    var cancelled = false
    var alwaysAllowedTools: Set<String> = []
    var permissionSemaphore: DispatchSemaphore?
    var permissionAllowed = false
    var permissionAlways = false

    init(project: ProjectContext) {
        self.project = project
    }

    var projectHandle: UnsafeMutableRawPointer {
        Unmanaged.passUnretained(project).toOpaque()
    }

    func emit(_ event: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: event),
              let json = String(data: data, encoding: .utf8) else { return }
        lock.lock()
        events.append(json)
        lock.unlock()
    }

    var isCancelled: Bool {
        lock.lock()
        defer { lock.unlock() }
        return cancelled
    }

    func drainEvents() -> [String] {
        lock.lock()
        defer { lock.unlock() }
        let drained = events
        events.removeAll()
        return drained
    }
}

// MARK: - ABI

/// Creates an agent bound to a project, defaulting to Anthropic. Without a
/// configured key, send falls back to the provider's environment variable.
@_cdecl("palmier_agent_create")
public func palmierAgentCreate(_ projectHandle: UnsafeMutableRawPointer?) -> UnsafeMutableRawPointer? {
    guard let projectHandle else { return nil }
    let project = Unmanaged<ProjectContext>.fromOpaque(projectHandle).takeUnretainedValue()
    return Unmanaged.passRetained(AgentContext(project: project)).toOpaque()
}

@_cdecl("palmier_agent_destroy")
public func palmierAgentDestroy(_ handle: UnsafeMutableRawPointer?) {
    guard let handle else { return }
    Unmanaged<AgentContext>.fromOpaque(handle).release()
}

private func agentContext(_ handle: UnsafeMutableRawPointer?) -> AgentContext? {
    guard let handle else { return nil }
    return Unmanaged<AgentContext>.fromOpaque(handle).takeUnretainedValue()
}

/// Replaces the agent's view of the media library: a JSON array of
/// {path, name, duration_frames, width, height} objects. Returns 1/0.
@_cdecl("palmier_agent_set_media")
public func palmierAgentSetMedia(_ handle: UnsafeMutableRawPointer?, _ json: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = agentContext(handle), let json else { return 0 }
    let data = Data(bytes: json, count: strlen(json))
    guard let parsed = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return 0 }
    ctx.lock.lock()
    ctx.mediaJSON = parsed
    ctx.lock.unlock()
    return 1
}

/// Configures the agent's provider, API key, and model. `provider` is one of
/// the ids from palmier_agent_providers; an empty key clears the configured
/// key (send then falls back to the provider's environment variable).
/// Returns 1 on success, 0 for an unknown provider.
@_cdecl("palmier_agent_configure")
public func palmierAgentConfigure(_ handle: UnsafeMutableRawPointer?,
                                  _ provider: UnsafePointer<CChar>?,
                                  _ apiKey: UnsafePointer<CChar>?,
                                  _ model: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = agentContext(handle), let provider,
          let selected = AgentProvider.named(String(cString: provider)) else { return 0 }
    ctx.lock.lock()
    defer { ctx.lock.unlock() }
    if selected.id != ctx.provider.id {
        ctx.provider = selected
        ctx.model = selected.defaultModel
    }
    if let apiKey {
        let key = String(cString: apiKey).trimmingCharacters(in: .whitespacesAndNewlines)
        ctx.configuredKey = key.isEmpty ? nil : key
    }
    if let model {
        let name = String(cString: model).trimmingCharacters(in: .whitespacesAndNewlines)
        if !name.isEmpty { ctx.model = name }
    }
    return 1
}

/// Writes the known providers as JSON: [{"id","name","default_model"}].
/// Returns 1 on success, or a negative required buffer size when too small.
@_cdecl("palmier_agent_providers")
public func palmierAgentProviders(_ buf: UnsafeMutablePointer<CChar>?, _ bufSize: Int32) -> Int32 {
    let entries: [[String: Any]] = AgentProvider.all.map {
        ["id": $0.id, "name": $0.displayName, "default_model": $0.defaultModel,
         "public_model_list": $0.publicModelList]
    }
    guard let data = try? JSONSerialization.data(withJSONObject: entries),
          let json = String(data: data, encoding: .utf8) else { return 0 }
    guard let buf else { return -Int32(json.utf8.count + 1) }
    return writeCString(json, into: buf, size: bufSize) == 1 ? 1 : -Int32(json.utf8.count + 1)
}

/// Asks the configured provider for its model list on a background thread.
/// The result arrives through palmier_agent_poll as
/// {"type":"models","provider":…,"models":[…]} or a "models_error" event.
/// Returns 1 when a fetch started, 0 when one is already running or the
/// provider needs a key that is not set.
@_cdecl("palmier_agent_refresh_models")
public func palmierAgentRefreshModels(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    guard let ctx = agentContext(handle) else { return 0 }
    ctx.lock.lock()
    if ctx.refreshingModels {
        ctx.lock.unlock()
        return 0
    }
    let provider = ctx.provider
    let key = ctx.configuredKey ?? ProcessInfo.processInfo.environment[provider.environmentKey] ?? ""
    guard !key.isEmpty || provider.publicModelList else {
        ctx.lock.unlock()
        ctx.emit(["type": "models_error",
                  "message": "Add a \(provider.displayName) API key to load its models."])
        return 0
    }
    ctx.refreshingModels = true
    ctx.lock.unlock()

    let thread = Thread {
        do {
            let models = try fetchProviderModels(provider, key: key)
            ctx.emit(["type": "models", "provider": provider.id, "models": models])
        } catch {
            ctx.emit(["type": "models_error", "message": error.localizedDescription])
        }
        ctx.lock.lock()
        ctx.refreshingModels = false
        ctx.lock.unlock()
    }
    thread.name = "palmier-agent-models"
    thread.start()
    return 1
}

/// Answers a pending permission prompt: allow 1/0; always 1 remembers the
/// decision for this tool for the rest of the session (allow only).
/// Returns 1 when a prompt was waiting, 0 otherwise.
@_cdecl("palmier_agent_permission")
public func palmierAgentPermission(_ handle: UnsafeMutableRawPointer?,
                                   _ allow: Int32, _ always: Int32) -> Int32 {
    guard let ctx = agentContext(handle) else { return 0 }
    ctx.lock.lock()
    guard let semaphore = ctx.permissionSemaphore else {
        ctx.lock.unlock()
        return 0
    }
    ctx.permissionAllowed = allow != 0
    ctx.permissionAlways = always != 0
    ctx.permissionSemaphore = nil
    ctx.lock.unlock()
    semaphore.signal()
    return 1
}

/// Asks the running turn to stop at the next tool-loop boundary. The request
/// in flight still completes — cancelling mid-request would leave the
/// conversation without its assistant reply. Returns 1 when a turn was
/// running, 0 otherwise.
@_cdecl("palmier_agent_cancel")
public func palmierAgentCancel(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    guard let ctx = agentContext(handle) else { return 0 }
    ctx.lock.lock()
    let running = ctx.busy
    if running { ctx.cancelled = true }
    let waiting = ctx.permissionSemaphore
    ctx.permissionSemaphore = nil
    ctx.permissionAllowed = false
    ctx.lock.unlock()
    // A turn parked on a permission prompt has to be released to notice.
    waiting?.signal()
    return running ? 1 : 0
}

/// 1 while a turn is running (poll for events), else 0.
@_cdecl("palmier_agent_busy")
public func palmierAgentBusy(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    guard let ctx = agentContext(handle) else { return 0 }
    ctx.lock.lock()
    defer { ctx.lock.unlock() }
    return ctx.busy ? 1 : 0
}

/// Starts a turn with the user's message on a background thread. Returns 1
/// when started, 0 when a turn is already running or arguments are invalid.
@_cdecl("palmier_agent_send")
public func palmierAgentSend(_ handle: UnsafeMutableRawPointer?, _ message: UnsafePointer<CChar>?) -> Int32 {
    guard let ctx = agentContext(handle), let message else { return 0 }
    let text = String(cString: message).trimmingCharacters(in: .whitespacesAndNewlines)
    guard !text.isEmpty else { return 0 }

    ctx.lock.lock()
    if ctx.busy {
        ctx.lock.unlock()
        return 0
    }
    ctx.busy = true
    ctx.lock.unlock()

    startTurn(ctx) { runAgentTurn(ctx, userText: text) }
    return 1
}

/// Re-runs the last turn after a failure (rate limit, no credit, dropped
/// connection) without appending the user's message again — the conversation
/// already holds it, so a plain resend would duplicate it. Returns 1 when a
/// retry started, 0 when a turn is running or there is nothing to retry.
@_cdecl("palmier_agent_retry")
public func palmierAgentRetry(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    guard let ctx = agentContext(handle) else { return 0 }
    ctx.lock.lock()
    let canRetry = !ctx.busy && ctx.conversation.last?["role"] as? String == "user"
    if !canRetry {
        ctx.lock.unlock()
        return 0
    }
    ctx.busy = true
    ctx.lock.unlock()
    startTurn(ctx) { resumeAgentTurn(ctx) }
    return 1
}

/// Runs `body` on the agent thread and clears busy when it returns.
private func startTurn(_ ctx: AgentContext, _ body: @escaping @Sendable () -> Void) {
    let thread = Thread {
        body()
        ctx.lock.lock()
        ctx.busy = false
        ctx.lock.unlock()
        ctx.emit(["type": "done"])
    }
    thread.name = "palmier-agent"
    thread.start()
}

/// Drains pending events as a JSON array (NUL-terminated). Returns bytes
/// written, -(required size) when the buffer is too small (events are
/// preserved for the retry), or 0 when there are no events.
@_cdecl("palmier_agent_poll")
public func palmierAgentPoll(_ handle: UnsafeMutableRawPointer?,
                             _ buf: UnsafeMutablePointer<CChar>?, _ bufSize: Int32) -> Int32 {
    guard let ctx = agentContext(handle) else { return 0 }
    ctx.lock.lock()
    defer { ctx.lock.unlock() }
    guard !ctx.events.isEmpty else { return 0 }
    let json = "[" + ctx.events.joined(separator: ",") + "]"
    let utf8 = Array(json.utf8)
    guard let buf, utf8.count < Int(bufSize) else { return -Int32(utf8.count + 1) }
    utf8.withUnsafeBufferPointer { src in
        buf.withMemoryRebound(to: UInt8.self, capacity: utf8.count + 1) { dst in
            dst.update(from: src.baseAddress!, count: utf8.count)
            dst[utf8.count] = 0
        }
    }
    ctx.events.removeAll()
    return Int32(utf8.count)
}

// MARK: - Agent loop

private func runAgentTurn(_ ctx: AgentContext, userText: String) {
    ctx.conversation.append(["role": "user", "content": userText])
    resumeAgentTurn(ctx)
}

/// Drives the tool-use loop over whatever is already in the conversation.
/// Both a fresh send and a retry land here, so a retry costs nothing but the
/// request itself.
private func resumeAgentTurn(_ ctx: AgentContext) {
    ctx.lock.lock()
    let configured = ctx.configuredKey
    let model = ctx.model
    let provider = ctx.provider
    ctx.lock.unlock()
    guard let apiKey = configured ?? ProcessInfo.processInfo.environment[provider.environmentKey],
          !apiKey.isEmpty else {
        ctx.emit(["type": "error", "retryable": true,
                  "message": "No API key configured. Open Settings and add your \(provider.displayName) API key."])
        return
    }

    ctx.lock.lock()
    ctx.cancelled = false
    ctx.lock.unlock()
    ctx.emit(["type": "status", "state": "thinking"])

    for _ in 0..<agentMaxToolLoops {
        if ctx.isCancelled {
            ctx.emit(["type": "text", "text": "Stopped."])
            return
        }
        let content: [[String: Any]]
        let stopReason: String
        do {
            switch provider.wire {
            case .anthropicMessages:
                (content, stopReason) = try anthropicStreamRequest(
                    ctx, apiKey: apiKey, model: model, messages: ctx.conversation)
            case .openAIChatCompletions:
                (content, stopReason) = try openAIStreamRequest(
                    ctx, provider: provider, apiKey: apiKey, model: model, messages: ctx.conversation)
            }
        } catch {
            // Transport and billing failures are worth another attempt once
            // the user has fixed the cause; the conversation is left intact.
            ctx.emit(["type": "error", "retryable": true,
                      "message": "Request failed: \(error.localizedDescription)"])
            return
        }

        if stopReason == "refusal" {
            ctx.emit(["type": "error", "message": "The model declined this request."])
            return
        }

        ctx.conversation.append(["role": "assistant", "content": content])

        guard stopReason == "tool_use" else { return }

        var toolResults: [[String: Any]] = []
        for block in content where block["type"] as? String == "tool_use" {
            guard let id = block["id"] as? String, let name = block["name"] as? String else { continue }
            let input = block["input"] as? [String: Any] ?? [:]
            let summary = toolSummary(name: name, input: input)
            // Arguments ride along so the UI can expand a tool row and show
            // exactly what was sent.
            let arguments = (try? JSONSerialization.data(withJSONObject: input, options: [.sortedKeys]))
                .flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
            ctx.emit(["type": "tool_use", "name": name, "summary": summary, "input": arguments])

            if ctx.isCancelled {
                toolResults.append(["type": "tool_result", "tool_use_id": id,
                                    "content": "The user stopped this turn.", "is_error": true])
                continue
            }
            let (result, isError) = permissionGatedExecute(ctx, name: name, summary: summary, input: input)
            ctx.emit(["type": "tool_result", "name": name, "ok": !isError, "detail": result])
            var resultBlock: [String: Any] = ["type": "tool_result", "tool_use_id": id, "content": result]
            if isError { resultBlock["is_error"] = true }
            toolResults.append(resultBlock)
        }
        ctx.conversation.append(["role": "user", "content": toolResults])
        ctx.emit(["type": "status", "state": "thinking"])
    }
    ctx.emit(["type": "error", "message": "Stopped after \(agentMaxToolLoops) tool steps."])
}

/// Destructive tools block on a user decision (Allow / Always allow / Deny)
/// delivered via palmier_agent_permission; everything else runs directly.
private func permissionGatedExecute(_ ctx: AgentContext, name: String, summary: String,
                                    input: [String: Any]) -> (String, Bool) {
    ctx.lock.lock()
    let needsPrompt = agentDestructiveTools.contains(name) && !ctx.alwaysAllowedTools.contains(name)
    ctx.lock.unlock()
    guard needsPrompt else { return executeTool(ctx, name: name, input: input) }

    let semaphore = DispatchSemaphore(value: 0)
    ctx.lock.lock()
    ctx.permissionSemaphore = semaphore
    ctx.lock.unlock()
    ctx.emit(["type": "permission", "name": name, "summary": summary])
    semaphore.wait()

    ctx.lock.lock()
    let allowed = ctx.permissionAllowed
    if allowed && ctx.permissionAlways { ctx.alwaysAllowedTools.insert(name) }
    ctx.lock.unlock()

    guard allowed else { return ("The user denied permission for this action.", true) }
    return executeTool(ctx, name: name, input: input)
}

/// One human-readable line for the tool chip in the chat UI.
private func toolSummary(name: String, input: [String: Any]) -> String {
    switch name {
    case "add_clip": return (input["media_path"] as? String).map { URL(fileURLWithPath: $0).lastPathComponent } ?? ""
    case "add_text_clip": return input["text"] as? String ?? ""
    case "set_clip_properties", "remove_clip", "split_clip", "move_clip", "trim_clip":
        return (input["clip_id"] as? String).map { String($0.prefix(8)) } ?? ""
    case "set_playhead": return (input["frame"] as? Int).map { "frame \($0)" } ?? ""
    default: return ""
    }
}

// MARK: - HTTP (raw Messages API over SSE; Swift has no official SDK)

/// Delegate that hands each received chunk to a line parser as it arrives —
/// swift-corelibs URLSession delivers incremental chunks through the
/// delegate path, which is what makes token-level streaming possible.
final class SSECollector: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var buffer = Data()
    let onLine: (String) -> Void
    var status = 0
    var errorBody = Data()
    var completionError: Error?
    let done = DispatchSemaphore(value: 0)

    init(onLine: @escaping (String) -> Void) {
        self.onLine = onLine
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                    didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        status = (response as? HTTPURLResponse)?.statusCode ?? 0
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        lock.lock()
        defer { lock.unlock() }
        if status != 200 {
            errorBody.append(data)
            return
        }
        buffer.append(data)
        while let newline = buffer.firstIndex(of: 0x0A) {
            let lineData = buffer.subdata(in: buffer.startIndex..<newline)
            buffer.removeSubrange(buffer.startIndex...newline)
            if let line = String(data: lineData, encoding: .utf8) {
                onLine(line.hasSuffix("\r") ? String(line.dropLast()) : line)
            }
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        completionError = error
        done.signal()
    }
}

/// Streams one Messages API request. Text deltas are emitted as events while
/// the response arrives; the accumulated content blocks and stop reason are
/// returned for the conversation history.
func anthropicStreamRequest(_ ctx: AgentContext, apiKey: String, model: String,
                                    messages: [[String: Any]]) throws -> ([[String: Any]], String) {
    var request = URLRequest(url: URL(string: "https://api.anthropic.com/v1/messages")!)
    request.httpMethod = "POST"
    request.timeoutInterval = 600
    request.setValue("application/json", forHTTPHeaderField: "Content-Type")
    request.setValue(apiKey, forHTTPHeaderField: "x-api-key")
    request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")

    let body: [String: Any] = [
        "model": model,
        "max_tokens": agentMaxTokens,
        "stream": true,
        "system": agentSystemPrompt,
        "tools": agentToolSchemas(),
        "messages": messages,
    ]
    request.httpBody = try JSONSerialization.data(withJSONObject: body)

    // Accumulator for the in-flight message; only touched from the delegate's
    // serial callback queue.
    final class StreamState: @unchecked Sendable {
        var blocks: [[String: Any]] = []
        var currentText = ""
        var currentToolJSON = ""
        var currentBlockType = ""
        var stopReason = "end_turn"
        var apiError: String?
    }
    let state = StreamState()

    let collector = SSECollector { line in
        guard line.hasPrefix("data:") else { return }
        let payload = line.dropFirst(5).trimmingCharacters(in: .whitespaces)
        guard let data = payload.data(using: .utf8),
              let event = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = event["type"] as? String else { return }
        switch type {
        case "content_block_start":
            guard let block = event["content_block"] as? [String: Any] else { return }
            state.currentBlockType = block["type"] as? String ?? ""
            if state.currentBlockType == "tool_use" {
                state.currentToolJSON = ""
                state.blocks.append(block)
            } else if state.currentBlockType == "text" {
                state.currentText = ""
            } else {
                // thinking/other block types pass through untouched
                state.blocks.append(block)
            }
        case "content_block_delta":
            guard let delta = event["delta"] as? [String: Any] else { return }
            if let text = delta["text"] as? String {
                state.currentText += text
                ctx.emit(["type": "text_delta", "text": text])
            } else if let partial = delta["partial_json"] as? String {
                state.currentToolJSON += partial
            }
        case "content_block_stop":
            if state.currentBlockType == "text" {
                if !state.currentText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    ctx.emit(["type": "text_end"])
                }
                state.blocks.append(["type": "text", "text": state.currentText])
                state.currentText = ""
            } else if state.currentBlockType == "tool_use", var last = state.blocks.last {
                let inputJSON = state.currentToolJSON.isEmpty ? "{}" : state.currentToolJSON
                if let inputData = inputJSON.data(using: .utf8),
                   let input = try? JSONSerialization.jsonObject(with: inputData) as? [String: Any] {
                    last["input"] = input
                    state.blocks[state.blocks.count - 1] = last
                }
            }
            state.currentBlockType = ""
        case "message_delta":
            if let delta = event["delta"] as? [String: Any],
               let reason = delta["stop_reason"] as? String {
                state.stopReason = reason
            }
        case "error":
            state.apiError = ((event["error"] as? [String: Any])?["message"] as? String) ?? "Stream error"
        default:
            break
        }
    }

    let session = URLSession(configuration: .default, delegate: collector, delegateQueue: nil)
    session.dataTask(with: request).resume()
    collector.done.wait()
    session.finishTasksAndInvalidate()

    if let error = collector.completionError { throw error }
    if collector.status != 200 {
        let message = (try? JSONSerialization.jsonObject(with: collector.errorBody) as? [String: Any])
            .flatMap { ($0["error"] as? [String: Any])?["message"] as? String }
        throw NSError(domain: "PalmierAgent", code: collector.status,
                      userInfo: [NSLocalizedDescriptionKey: message ?? "HTTP \(collector.status)"])
    }
    if let apiError = state.apiError {
        throw NSError(domain: "PalmierAgent", code: 2, userInfo: [NSLocalizedDescriptionKey: apiError])
    }
    return (state.blocks, state.stopReason)
}

// MARK: - Tools

// Built per request: [[String: Any]] is not Sendable, so a global would trip
// strict concurrency; construction is trivially cheap next to the HTTP call.
func agentToolSchemas() -> [[String: Any]] { [
    ["name": "get_timeline",
     "description": "Read the current timeline: tracks, clips with their stable ids, frame positions, and properties. Call this before any edit so you act on current state.",
     "input_schema": ["type": "object", "properties": [String: Any](), "required": [String]()]],
    ["name": "list_media",
     "description": "List the media library: files the user imported, with path, name, and duration in timeline frames. Use these paths with add_clip.",
     "input_schema": ["type": "object", "properties": [String: Any](), "required": [String]()]],
    ["name": "add_clip",
     "description": "Add a media file from the library to the timeline. Omit start_frame to append at the end of the video track; placing over existing clips overwrites that region. Linked audio is added automatically when the source has sound.",
     "input_schema": ["type": "object",
                      "properties": ["media_path": ["type": "string", "description": "Path from list_media"],
                                     "start_frame": ["type": "integer", "description": "Timeline frame; omit to append"],
                                     "duration_frames": ["type": "integer", "description": "Omit to use the full media length"]],
                      "required": ["media_path"]]],
    ["name": "add_text_clip",
     "description": "Add a text/title clip on the video track.",
     "input_schema": ["type": "object",
                      "properties": ["text": ["type": "string"],
                                     "start_frame": ["type": "integer"],
                                     "duration_frames": ["type": "integer", "description": "Default 120 (4 seconds)"]],
                      "required": ["text", "start_frame"]]],
    ["name": "remove_clip",
     "description": "Remove a clip from the timeline by its stable id.",
     "input_schema": ["type": "object", "properties": ["clip_id": ["type": "string"]], "required": ["clip_id"]]],
    ["name": "split_clip",
     "description": "Blade a clip in two at a timeline frame strictly inside it. The left half keeps the id; the result reports the new right-half id.",
     "input_schema": ["type": "object",
                      "properties": ["clip_id": ["type": "string"], "frame": ["type": "integer"]],
                      "required": ["clip_id", "frame"]]],
    ["name": "move_clip",
     "description": "Move a clip (and its linked audio) to a new start frame. The move clamps flush against neighboring clips instead of overlapping them; the result reports the actual position.",
     "input_schema": ["type": "object",
                      "properties": ["clip_id": ["type": "string"], "start_frame": ["type": "integer"]],
                      "required": ["clip_id", "start_frame"]]],
    ["name": "trim_clip",
     "description": "Trim a clip edge to a timeline frame. edge is \"left\" (in-point) or \"right\" (out-point). Clamped to at least 1 frame, neighbors, and the source length; linked audio follows.",
     "input_schema": ["type": "object",
                      "properties": ["clip_id": ["type": "string"],
                                     "edge": ["type": "string", "enum": ["left", "right"]],
                                     "frame": ["type": "integer"]],
                      "required": ["clip_id", "edge", "frame"]]],
    ["name": "set_clip_properties",
     "description": "Set one or more properties on a clip. Only provided fields change. center_x/center_y are 0-1 canvas coordinates, width/height are canvas fractions, rotation is degrees, opacity 0-1, speed 0.01-100, volume_db -96 to 12, fades in seconds, text replaces a text clip's content.",
     "input_schema": ["type": "object",
                      "properties": ["clip_id": ["type": "string"],
                                     "center_x": ["type": "number"], "center_y": ["type": "number"],
                                     "width": ["type": "number"], "height": ["type": "number"],
                                     "rotation": ["type": "number"], "opacity": ["type": "number"],
                                     "speed": ["type": "number"], "volume_db": ["type": "number"],
                                     "fade_in_seconds": ["type": "number"], "fade_out_seconds": ["type": "number"],
                                     "text": ["type": "string"]],
                      "required": ["clip_id"]]],
    ["name": "set_playhead",
     "description": "Move the preview playhead to a timeline frame so the user sees that moment.",
     "input_schema": ["type": "object", "properties": ["frame": ["type": "integer"]], "required": ["frame"]]],
] }

private func executeTool(_ ctx: AgentContext, name: String, input: [String: Any]) -> (String, Bool) {
    let handle = ctx.projectHandle
    func intFrom(_ key: String) -> Int? {
        (input[key] as? Int) ?? (input[key] as? Double).map { Int($0) }
    }

    switch name {
    case "get_timeline":
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        guard let data = try? encoder.encode(ctx.project.snapshot()),
              let json = String(data: data, encoding: .utf8) else {
            return ("Could not read the timeline.", true)
        }
        return (json, false)

    case "list_media":
        ctx.lock.lock()
        let media = ctx.mediaJSON
        ctx.lock.unlock()
        guard !media.isEmpty else { return ("The media library is empty — the user has not imported any files.", false) }
        guard let data = try? JSONSerialization.data(withJSONObject: media),
              let json = String(data: data, encoding: .utf8) else { return ("Could not read the media library.", true) }
        return (json, false)

    case "add_clip":
        guard let path = input["media_path"] as? String else { return ("media_path is required.", true) }
        var duration = intFrom("duration_frames") ?? 0
        if duration <= 0 {
            guard let probed = sourceDurationFrames(path: path) else {
                return ("Could not read \(path) — is the path exactly as list_media reported?", true)
            }
            duration = probed
        }
        var idBuf = [CChar](repeating: 0, count: 64)
        let added: Int32
        if let start = intFrom("start_frame") {
            added = palmierTimelineAddClipAt(handle, path, Int32(duration), Int32(start), &idBuf, 64)
        } else {
            added = palmierTimelineAddClip(handle, path, Int32(duration), &idBuf, 64)
        }
        guard added > 0 else { return ("Adding the clip failed (invalid path or position).", true) }
        return ("Added clip \(String(cString: idBuf)) (\(duration) frames).", false)

    case "add_text_clip":
        guard let text = input["text"] as? String, let start = intFrom("start_frame") else {
            return ("text and start_frame are required.", true)
        }
        let duration = intFrom("duration_frames") ?? 120
        var idBuf = [CChar](repeating: 0, count: 64)
        guard palmierTimelineAddTextClip(handle, text, Int32(start), Int32(duration), &idBuf, 64) == 1 else {
            return ("Adding the text clip failed.", true)
        }
        return ("Added text clip \(String(cString: idBuf)).", false)

    case "remove_clip":
        guard let clipId = input["clip_id"] as? String else { return ("clip_id is required.", true) }
        guard palmierTimelineRemoveClip(handle, clipId) == 1 else {
            return ("No clip with id \(clipId). Call get_timeline for current ids.", true)
        }
        return ("Removed clip \(clipId).", false)

    case "split_clip":
        guard let clipId = input["clip_id"] as? String, let frame = intFrom("frame") else {
            return ("clip_id and frame are required.", true)
        }
        var idBuf = [CChar](repeating: 0, count: 64)
        guard palmierTimelineSplitClip(handle, clipId, Int32(frame), &idBuf, 64) == 1 else {
            return ("Split failed — frame \(frame) is not strictly inside that clip.", true)
        }
        return ("Split done. Right half id: \(String(cString: idBuf)).", false)

    case "move_clip":
        guard let clipId = input["clip_id"] as? String, let start = intFrom("start_frame") else {
            return ("clip_id and start_frame are required.", true)
        }
        guard palmierTimelineMoveClip(handle, clipId, Int32(start)) == 1 else {
            return ("Move failed — unknown clip or negative frame.", true)
        }
        let actual = ctx.project.snapshot().tracks.flatMap(\.clips).first { $0.id == clipId }?.startFrame
        return ("Moved. Clip now starts at frame \(actual ?? start) (clamped against neighbors if different from the request).", false)

    case "trim_clip":
        guard let clipId = input["clip_id"] as? String, let edgeName = input["edge"] as? String,
              let frame = intFrom("frame"), edgeName == "left" || edgeName == "right" else {
            return ("clip_id, edge (left|right), and frame are required.", true)
        }
        guard palmierClipTrim(handle, clipId, edgeName == "left" ? 0 : 1, Int32(frame)) == 1 else {
            return ("Trim failed — unknown clip.", true)
        }
        let clip = ctx.project.snapshot().tracks.flatMap(\.clips).first { $0.id == clipId }
        return ("Trimmed. Clip now spans frames \(clip?.startFrame ?? 0)–\(clip?.endFrame ?? 0) (clamped if different from the request).", false)

    case "set_clip_properties":
        guard let clipId = input["clip_id"] as? String else { return ("clip_id is required.", true) }
        guard let clip = ctx.project.snapshot().tracks.flatMap(\.clips).first(where: { $0.id == clipId }) else {
            return ("No clip with id \(clipId). Call get_timeline for current ids.", true)
        }
        var applied: [String] = []
        var failures: [String] = []
        func record(_ ok: Bool, _ label: String) {
            if ok { applied.append(label) } else { failures.append(label) }
        }
        func num(_ key: String) -> Double? { (input[key] as? Double) ?? (input[key] as? Int).map(Double.init) }

        let transformKeys = ["center_x", "center_y", "width", "height", "rotation"]
        if transformKeys.contains(where: { input[$0] != nil }) {
            record(palmierClipSetTransform(handle, clipId,
                num("center_x") ?? clip.transform.centerX,
                num("center_y") ?? clip.transform.centerY,
                num("width") ?? clip.transform.width,
                num("height") ?? clip.transform.height,
                num("rotation") ?? clip.transform.rotation) == 1, "transform")
        }
        if let opacity = num("opacity") {
            record(palmierClipSetOpacity(handle, clipId, opacity) == 1, "opacity")
        }
        if let speed = num("speed") {
            record(palmierClipSetSpeed(handle, clipId, speed) == 1, "speed")
        }
        if let db = num("volume_db") {
            record(palmierClipSetVolumeDb(handle, clipId, db) == 1, "volume")
        }
        if num("fade_in_seconds") != nil || num("fade_out_seconds") != nil {
            let fadeIn = Int32(((num("fade_in_seconds") ?? Double(clip.fadeInFrames) / 30.0) * 30).rounded())
            let fadeOut = Int32(((num("fade_out_seconds") ?? Double(clip.fadeOutFrames) / 30.0) * 30).rounded())
            record(palmierClipSetFades(handle, clipId, max(0, fadeIn), max(0, fadeOut)) == 1, "fades")
        }
        if let text = input["text"] as? String {
            record(palmierClipSetText(handle, clipId, text) == 1, "text")
        }
        if applied.isEmpty && failures.isEmpty { return ("No properties were provided — nothing changed.", false) }
        var report = applied.isEmpty ? "" : "Applied: \(applied.joined(separator: ", "))."
        if !failures.isEmpty { report += " Rejected (out of range or wrong clip type): \(failures.joined(separator: ", "))." }
        return (report, !failures.isEmpty && applied.isEmpty)

    case "set_playhead":
        guard let frame = intFrom("frame"), frame >= 0 else { return ("frame must be a non-negative integer.", true) }
        ctx.emit(["type": "playhead", "frame": frame])
        return ("Playhead moved to frame \(frame).", false)

    default:
        return ("Unknown tool \(name).", true)
    }
}
