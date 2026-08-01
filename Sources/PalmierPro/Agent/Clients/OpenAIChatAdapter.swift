import Foundation
import PalmierCore

/// Adapter between the provider-neutral `PalmierCore` chat types and the
/// OpenAI Chat Completions wire format. This is the second concrete provider
/// against the `ChatClient` abstraction: it proves the neutral union is a real
/// translation, not an Anthropic-shaped passthrough.
///
/// OpenAI's message model differs structurally from the neutral one:
/// - system is a `role: "system"` message, not a top-level field;
/// - an assistant's tool calls live in a `tool_calls` array beside `content`;
/// - each tool result is its own `role: "tool"` message keyed by `tool_call_id`.
/// So one neutral message can expand to several OpenAI messages (the tool-result
/// fan-out), and the neutral conversation must be re-segmented here.
enum OpenAIChatAdapter {

    // MARK: Neutral → OpenAI

    /// Builds the OpenAI Chat Completions request body from neutral inputs.
    /// `system` becomes the first message; each neutral message may expand into
    /// multiple OpenAI messages (assistant tool calls split out, tool results
    /// fanned into one message per call).
    static func requestBody(
        model: String,
        system: String,
        tools: [ToolSchema],
        messages: [ChatMessage]
    ) -> [String: Any] {
        var openAIMessages: [[String: Any]] = []
        if !system.isEmpty {
            openAIMessages.append(["role": "system", "content": system])
        }
        for message in messages {
            openAIMessages.append(contentsOf: self.messages(from: message))
        }
        var body: [String: Any] = [
            "model": model,
            "stream": true,
            "messages": openAIMessages,
        ]
        let toolDefs = tools.map(toolDefinition(from:))
        if !toolDefs.isEmpty { body["tools"] = toolDefs }
        return body
    }

    /// Translates one neutral message into one or more OpenAI messages.
    /// - An assistant message with tool calls emits a single assistant message
    ///   carrying a `tool_calls` array (text blocks join into `content`).
    /// - A user message that is only tool results emits one `role: "tool"`
    ///   message per result, in order. A user message mixing text and tool
    ///   results is split: the text becomes its own user message first.
    static func messages(from message: ChatMessage) -> [[String: Any]] {
        let role = message.role == .user ? "user" : "assistant"
        var textParts: [String] = []
        var imageParts: [[String: Any]] = []
        var toolCalls: [[String: Any]] = []
        var toolResults: [[String: Any]] = []
        for block in message.content {
            switch block {
            case .text(let s):
                if !s.isEmpty { textParts.append(s) }
            case .image(let mediaType, let base64):
                imageParts.append([
                    "type": "image_url",
                    "image_url": ["url": "data:\(mediaType);base64,\(base64)"],
                ])
            case .toolCall(let id, let name, let inputJSON):
                toolCalls.append([
                    "id": id,
                    "type": "function",
                    "function": ["name": name, "arguments": inputJSON],
                ])
            case .toolResult(let toolCallID, let content, let isError):
                toolResults.append(toolResultMessage(toolCallID: toolCallID, content: content, isError: isError))
            case .thinking, .redactedThinking:
                // Anthropic-only reasoning blocks; OpenAI has no equivalent.
                break
            }
        }

        var out: [[String: Any]] = []

        if role == "assistant" {
            // Assistant tool calls live beside content; content is null when
            // only tool calls are present (OpenAI requires content or tool_calls).
            var assistantMsg: [String: Any] = ["role": "assistant"]
            let joined = textParts.joined()
            assistantMsg["content"] = joined.isEmpty ? NSNull() : joined
            if !toolCalls.isEmpty { assistantMsg["tool_calls"] = toolCalls }
            out.append(assistantMsg)
        } else {
            // User message: text + inline images first (as a user message)...
            if !imageParts.isEmpty {
                var parts: [[String: Any]] = imageParts
                if !textParts.isEmpty { parts.insert(["type": "text", "text": textParts.joined(separator: "\n")], at: 0) }
                out.append(["role": "user", "content": parts])
            } else if !textParts.isEmpty {
                out.append(["role": "user", "content": textParts.joined(separator: "\n")])
            }
            // ...then one `role: tool` message per tool result.
            out.append(contentsOf: toolResults)
        }
        return out
    }

    /// Builds a single OpenAI `role: "tool"` message. Text-only results use a
    /// string `content`; results containing an image use the multipart array
    /// form (`{type: text}` / `{type: image_url}`) so image tool feedback is
    /// preserved on vision-capable models.
    private static func toolResultMessage(
        toolCallID: String,
        content: [ToolResultBlock],
        isError: Bool
    ) -> [String: Any] {
        let textBlocks = content.compactMap { block -> String? in
            if case .text(let s) = block { return s }
            return nil
        }
        let hasImage = content.contains { if case .image = $0 { return true } else { return false } }
        var msg: [String: Any] = ["role": "tool", "tool_call_id": toolCallID]
        if isError && !textBlocks.isEmpty {
            msg["content"] = "[tool error] " + textBlocks.joined(separator: "\n")
        } else if hasImage {
            // Multipart form preserves images on models that accept them.
            msg["content"] = content.map(resultPart(from:))
        } else {
            msg["content"] = textBlocks.joined(separator: "\n")
        }
        return msg
    }

    private static func resultPart(from block: ToolResultBlock) -> [String: Any] {
        switch block {
        case .text(let s):
            return ["type": "text", "text": s]
        case .image(let mediaType, let base64):
            return [
                "type": "image_url",
                "image_url": ["url": "data:\(mediaType);base64,\(base64)"],
            ]
        }
    }

    static func toolDefinition(from schema: ToolSchema) -> [String: Any] {
        [
            "type": "function",
            "function": [
                "name": schema.name,
                "description": schema.description,
                "parameters": schema.inputSchema.unwrap() as? [String: Any] ?? [:],
            ] as [String: Any],
        ]
    }

    // MARK: OpenAI finish_reason → neutral stop reason

    static func stopReason(_ finishReason: String?) -> ChatStopReason {
        guard let finishReason else { return .endTurn }
        switch finishReason {
        case "stop": return .endTurn
        case "tool_calls", "function_call": return .toolUse
        case "length": return .maxTokens
        case "content_filter": return .refusal
        default: return .other
        }
    }

    // MARK: OpenAI streaming → neutral stream

    /// Accumulator for streamed tool calls, keyed by their `index` across deltas.
    struct StreamState {
        var toolCalls: [Int: (id: String, name: String, arguments: String)] = [:]
        var finishReason: String?
        var emittedToolCalls: Set<Int> = []
    }

    /// Pure core: process one parsed SSE chunk (`data: <json>` payload) against
    /// the accumulator state. Returns neutral events to yield. The first chunk
    /// for a tool-call index carries its `id` + `function.name`; later chunks
    /// append `arguments` fragments. Tool calls are flushed when a `tool_calls`
    /// finish_reason is observed or the stream closes ([DONE]).
    static func processChunk(_ chunk: [String: Any], state: inout StreamState) -> [ChatStreamEvent] {
        var events: [ChatStreamEvent] = []
        guard let choices = chunk["choices"] as? [[String: Any]], let choice = choices.first else {
            return events
        }
        let delta = choice["delta"] as? [String: Any] ?? [:]

        if let text = delta["content"] as? String, !text.isEmpty {
            events.append(.textDelta(text))
        }

        if let toolCallDeltas = delta["tool_calls"] as? [[String: Any]] {
            for tcd in toolCallDeltas {
                guard let index = tcd["index"] as? Int else { continue }
                let function = tcd["function"] as? [String: Any] ?? [:]
                var entry = state.toolCalls[index] ?? (id: "", name: "", arguments: "")
                if let id = tcd["id"] as? String, !id.isEmpty { entry.id = id }
                if let name = function["name"] as? String, !name.isEmpty { entry.name = name }
                if let args = function["arguments"] as? String { entry.arguments += args }
                state.toolCalls[index] = entry
            }
        }

        if let reason = choice["finish_reason"] as? String, !reason.isEmpty {
            state.finishReason = reason
            // Flush accumulated tool calls before the terminal stop event.
            for index in state.toolCalls.keys.sorted() {
                guard let entry = state.toolCalls[index], !state.emittedToolCalls.contains(index) else { continue }
                events.append(.toolCallComplete(id: entry.id, name: entry.name, inputJSON: entry.arguments))
                state.emittedToolCalls.insert(index)
            }
            events.append(.stop(reason: stopReason(reason)))
        }
        return events
    }

    /// Final flush for streams that close without a `finish_reason` (the
    /// `data: [DONE]` sentinel). Emits any tool calls that were accumulated but
    /// never flushed, then a stop event inferred from whether tools ran.
    static func flush(state: inout StreamState) -> [ChatStreamEvent] {
        var events: [ChatStreamEvent] = []
        for index in state.toolCalls.keys.sorted() {
            guard let entry = state.toolCalls[index], !state.emittedToolCalls.contains(index) else { continue }
            events.append(.toolCallComplete(id: entry.id, name: entry.name, inputJSON: entry.arguments))
            state.emittedToolCalls.insert(index)
        }
        if state.finishReason == nil {
            events.append(.stop(reason: events.isEmpty ? .endTurn : .toolUse))
        }
        return events
    }

    /// Parses OpenAI Chat Completions SSE bytes into neutral stream events.
    /// Throws on a transport `error` field; otherwise yields until `[DONE]`.
    static func parse(
        bytes: URLSession.AsyncBytes,
        yield: @escaping (ChatStreamEvent) -> Void
    ) async throws {
        var state = StreamState()
        for try await line in bytes.lines {
            try Task.checkCancellation()
            let payload = line.dropFirst("data:".count).trimmingCharacters(in: .whitespaces)
            if payload == "[DONE]" {
                for event in flush(state: &state) { yield(event) }
                return
            }
            guard payload != "",
                  let data = payload.data(using: .utf8),
                  let chunk = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { continue }
            if let err = chunk["error"] as? [String: Any], let msg = err["message"] as? String {
                throw OpenAIClientError.streamError(msg)
            }
            for event in processChunk(chunk, state: &state) { yield(event) }
        }
        // Stream ended without [DONE]; flush anything still pending.
        for event in flush(state: &state) { yield(event) }
    }
}
