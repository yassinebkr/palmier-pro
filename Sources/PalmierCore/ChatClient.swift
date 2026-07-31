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
    case toolResult(toolCallID: String, content: [ToolResultBlock], isError: Bool)
}

/// One block within a tool result. Tools may return text and/or images
/// (e.g. `capture_frame` returns a JPEG); the neutral type preserves both so
/// image feedback to the model is not dropped by the adapter layer.
public enum ToolResultBlock: Sendable, Equatable {
    case text(String)
    case image(mediaType: String, base64: String)
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
    public var inputSchema: JSONValue

    public init(name: String, description: String, inputSchema: JSONValue) {
        self.name = name
        self.description = description
        self.inputSchema = inputSchema
    }
}

/// Type-safe, Sendable JSON value tree for tool schemas and other arbitrary
/// JSON payloads. Swift 6 forbids storing `Any` in a Sendable struct; this
/// recursive enum is the Sendable-correct equivalent of `[String: Any]`.
public indirect enum JSONValue: Sendable, Equatable {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    /// Initialize from a plain JSON tree of stdlib types
    /// (`String/Bool/Int/Double/[Any]/[String:Any]`). Non-JSON inputs become
    /// `.null`. Lets app code build schemas from literals while the stored
    /// form stays type-safe.
    public init(_ value: Any) {
        self = JSONValue.from(value)
    }

    private static func from(_ value: Any) -> JSONValue {
        switch value {
        case is NSNull: return .null
        case let v as Bool: return .bool(v)
        case let v as Int: return .number(Double(v))
        case let v as Double: return .number(v)
        case let v as String: return .string(v)
        case let v as [Any]: return .array(v.map(from))
        case let v as [String: Any]: return .object(v.mapValues(from))
        default: return .null
        }
    }

    /// Encode back to a plain JSON tree suitable for JSONSerialization.
    public func unwrap() -> Any {
        switch self {
        case .null: return NSNull()
        case .bool(let v): return v
        case .number(let v):
            // Preserve integer-ness for clean JSON.
            if v == v.rounded() && v.isFinite && abs(v) < 1e15 {
                return Int(v)
            }
            return v
        case .string(let v): return v
        case .array(let v): return v.map { $0.unwrap() }
        case .object(let v): return v.mapValues { $0.unwrap() }
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

/// Telemetry/trace identifiers attached to one agent request. Provider-neutral
/// data; how (or whether) a client forwards it is each adapter's choice.
public struct AgentRequestContext: Equatable, Sendable {
    public let conversationID: UUID
    public let traceID: UUID
    public let spanID: UUID
    public let inputMessageID: UUID
    public let outputMessageID: UUID
    public let projectID: String?

    public init(conversationID: UUID, traceID: UUID, spanID: UUID,
                inputMessageID: UUID, outputMessageID: UUID, projectID: String?) {
        self.conversationID = conversationID
        self.traceID = traceID
        self.spanID = spanID
        self.inputMessageID = inputMessageID
        self.outputMessageID = outputMessageID
        self.projectID = projectID
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

    /// Context-ful variant for callers that track telemetry. Declared as a
    /// requirement so existential calls dispatch to a conformer's witness.
    func stream(
        system: String,
        tools: [ToolSchema],
        messages: [ChatMessage],
        context: AgentRequestContext
    ) -> AsyncThrowingStream<ChatStreamEvent, Error>
}

public extension ChatClient {
    /// Providers without a telemetry channel ignore the context.
    func stream(
        system: String,
        tools: [ToolSchema],
        messages: [ChatMessage],
        context: AgentRequestContext
    ) -> AsyncThrowingStream<ChatStreamEvent, Error> {
        stream(system: system, tools: tools, messages: messages)
    }
}
