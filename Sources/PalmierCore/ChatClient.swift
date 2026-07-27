import Foundation

// Provider-neutral chat/agent value types. Per-provider wire adapters
// (Anthropic, OpenAI, etc.) live in the app and translate to/from these.
// Design rule: the union shape covers what every major chat-completions API
// expresses (text + tool calls + tool results + inline images), in a form
// none of them natively uses, so no provider gets a "home-field advantage"
// and every adapter is a translation, not a passthrough.

/// One content block within a chat message. The neutral union of text,
/// inline images, assistant tool calls, and tool results returned by the user.
public enum ChatContentBlock: Sendable, Equatable {
    case text(String)
    case image(mediaType: String, base64: String)
    case toolCall(id: String, name: String, inputJSON: String)
    case toolResult(toolCallID: String, content: String, isError: Bool)
}

/// A single message in a chat conversation.
public struct ChatMessage: Sendable, Equatable {
    public enum Role: String, Sendable { case user, assistant }

    public var role: Role
    public var content: [ChatContentBlock]

    public init(role: Role, content: [ChatContentBlock]) {
        self.role = role
        self.content = content
    }
}

/// A tool the agent may call. `inputSchema` is a JSON-Schema dictionary; both
/// Anthropic (`input_schema`) and OpenAI (`parameters`) use the same shape.
public struct ToolSchema: Sendable, Equatable {
    public var name: String
    public var description: String
    public var inputSchema: [String: AnyCodable]

    public init(name: String, description: String, inputSchema: [String: AnyCodable]) {
        self.name = name
        self.description = description
        self.inputSchema = inputSchema
    }
}

/// Type-erased JSON value for tool schemas (plain `[String: Any]` isn't Sendable
/// or Equatable). Wraps any JSON-serializable nesting.
public struct AnyCodable: Sendable, Equatable {
    public let value: Any

    public init(_ value: Any) {
        self.value = AnyCodable.normalize(value)
    }

    /// Recursively normalize containers so value-equality works and Sendable
    /// conformance is sound (only Sendable JSON types are retained).
    private static func normalize(_ value: Any) -> Any {
        if let dict = value as? [String: Any] {
            return dict.mapValues(normalize)
        }
        if let array = value as? [Any] {
            return array.map(normalize)
        }
        return value
    }

    public static func == (lhs: AnyCodable, rhs: AnyCodable) -> Bool {
        isEqual(lhs.value, rhs.value)
    }

    private static func isEqual(_ a: Any, _ b: Any) -> Bool {
        switch (a, b) {
        case let (x as String, y as String): return x == y
        case let (x as Bool, y as Bool): return x == y
        case let (x as Int, y as Int): return x == y
        case let (x as Double, y as Double): return x == y
        case let (x as [String: Any], y as [String: Any]):
            return x.count == y.count && x.allSatisfy { k, v in
                guard let w = y[k] else { return false }
                return isEqual(v, w)
            }
        case let (x as [Any], y as [Any]):
            return x.count == y.count && zip(x, y).allSatisfy(isEqual)
        default: return false
        }
    }
}

/// Why the model stopped generating. Normalized across providers.
public enum ChatStopReason: Sendable, Equatable {
    case endTurn
    case toolUse
    case maxTokens
    case stopSequence
    case refusal
    case other
}

/// A provider-neutral stream event. Adapters translate per-provider SSE
/// shapes into these.
public enum ChatStreamEvent: Sendable {
    case textDelta(String)
    case toolCallComplete(id: String, name: String, inputJSON: String)
    case stop(reason: ChatStopReason)
}

/// A model the agent can use, tagged with its provider. Providers expose the
/// models they support; the active model is selected in Settings.
public struct ChatModel: Sendable, Equatable, Hashable {
    public let provider: String
    public let id: String
    public let displayName: String

    public init(provider: String, id: String, displayName: String) {
        self.provider = provider
        self.id = id
        self.displayName = displayName
    }
}

/// A chat-completions client. Per-provider adapters conform and translate
/// the neutral inputs to their wire format, then parse their SSE into
/// `ChatStreamEvent`s. `system` is the top-level system prompt, `tools` is the
/// schema of tools the model may call, `messages` is the conversation history.
public protocol ChatClient: Sendable {
    func stream(
        system: String,
        tools: [ToolSchema],
        messages: [ChatMessage]
    ) -> AsyncThrowingStream<ChatStreamEvent, Error>
}
