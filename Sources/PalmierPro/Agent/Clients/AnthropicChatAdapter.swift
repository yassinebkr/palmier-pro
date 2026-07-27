import Foundation
import PalmierCore

/// Adapter between the provider-neutral `PalmierCore` chat types and the
/// Anthropic wire format. The translation is one-directional on the way in
/// (neutral → Anthropic request body) and on the way out (Anthropic stream
/// event → neutral `ChatStreamEvent`). This is the proof point that the
/// `ChatClient` abstraction holds for a concrete provider: everything specific
/// to Anthropic lives here, not in the protocol or in `AgentService`.
enum AnthropicChatAdapter {
    // MARK: Neutral → Anthropic

    static func toolSchema(from schema: ToolSchema) -> AnthropicToolSchema {
        AnthropicToolSchema(
            name: schema.name,
            description: schema.description,
            inputSchema: schema.inputSchema.unwrap() as? [String: Any] ?? [:]
        )
    }

    static func message(from message: ChatMessage) -> AnthropicMessage {
        AnthropicMessage(
            role: message.role == .user ? .user : .assistant,
            content: message.content.compactMap(blockJSON)
        )
    }

    /// Maps a neutral content block to an Anthropic content-block dict, or nil
    /// when the block is structurally invalid for Anthropic's wire format.
    private static func blockJSON(_ block: ChatContentBlock) -> [String: Any]? {
        switch block {
        case .text(let s):
            guard !s.isEmpty else { return nil }
            return ["type": "text", "text": s]
        case .image(let mediaType, let base64):
            return [
                "type": "image",
                "source": ["type": "base64", "media_type": mediaType, "data": base64],
            ]
        case .toolCall(let id, let name, let inputJSON):
            return [
                "type": "tool_use", "id": id, "name": name,
                "input": parseJSONObject(inputJSON),
            ]
        case .toolResult(let toolCallID, let content, let isError):
            return [
                "type": "tool_result", "tool_use_id": toolCallID,
                "content": content, "is_error": isError,
            ]
        }
    }

    private static func parseJSONObject(_ json: String) -> [String: Any] {
        guard let data = json.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [:] }
        return obj
    }

    // MARK: Anthropic → neutral

    static func stopReason(_ reason: AnthropicStopReason) -> ChatStopReason {
        switch reason {
        case .endTurn: .endTurn
        case .toolUse: .toolUse
        case .maxTokens: .maxTokens
        case .stopSequence: .stopSequence
        case .refusal: .refusal
        case .pauseTurn, .other: .other
        }
    }

    /// Translates one Anthropic stream event to its neutral equivalent. Every
    /// `AnthropicStreamEvent` case maps 1:1, so this never returns nil.
    static func chatStreamEvent(from event: AnthropicStreamEvent) -> ChatStreamEvent {
        switch event {
        case .textDelta(let s):
            return .textDelta(s)
        case .toolUseComplete(let id, let name, let inputJSON):
            return .toolCallComplete(id: id, name: name, inputJSON: inputJSON)
        case .messageStop(let reason):
            return .stop(reason: stopReason(reason))
        }
    }

    /// Decodes Anthropic SSE bytes and yields provider-neutral `ChatStreamEvent`s.
    /// An SSE `error` frame is thrown as `AnthropicClientError.streamError`.
    static func parse(
        bytes: URLSession.AsyncBytes,
        yield: @escaping (ChatStreamEvent) -> Void
    ) async throws {
        try await AnthropicSSE.parse(bytes: bytes) { event in
            yield(chatStreamEvent(from: event))
        }
    }
}

// MARK: - ChatClient conformance

extension AnthropicClient: ChatClient {
    /// Provider-neutral `ChatClient` surface. Same selector as the existing
    /// `AgentClient.stream`; Swift resolves between them by parameter type, so
    /// a `any ChatClient` call site dispatches here while `any AgentClient`
    /// call sites keep using the Anthropic-typed overload unchanged.
    func stream(
        system: String,
        tools: [ToolSchema],
        messages: [ChatMessage]
    ) -> AsyncThrowingStream<ChatStreamEvent, Error> {
        let anthropicTools = tools.map(AnthropicChatAdapter.toolSchema(from:))
        let anthropicMessages = messages.map(AnthropicChatAdapter.message(from:))
        let upstream = stream(system: system, tools: anthropicTools, messages: anthropicMessages)
        return AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    for try await event in upstream {
                        continuation.yield(AnthropicChatAdapter.chatStreamEvent(from: event))
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }
}
