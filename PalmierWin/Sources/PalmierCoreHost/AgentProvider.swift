import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

// Provider layer for the agent. Anthropic speaks the Messages API; OpenAI,
// Z.AI, Moonshot, and OpenRouter all speak OpenAI chat/completions. Both
// transports return Anthropic-shaped content blocks and a stop reason, so the
// tool-use loop in AgentHost never branches on provider.

enum AgentWireFormat {
    case anthropicMessages
    case openAIChatCompletions
}

struct AgentProvider: Sendable {
    let id: String
    let displayName: String
    let baseURL: String
    let wire: AgentWireFormat
    /// Environment variable consulted when no key is configured in settings.
    let environmentKey: String
    let defaultModel: String
    /// True when GET {base}/models needs no credentials, so the real catalogue
    /// can replace the built-in default before the user has a key.
    let publicModelList: Bool

    static let all: [AgentProvider] = [
        AgentProvider(id: "anthropic", displayName: "Anthropic",
                      baseURL: "https://api.anthropic.com/v1", wire: .anthropicMessages,
                      environmentKey: "ANTHROPIC_API_KEY", defaultModel: "claude-opus-5",
                      publicModelList: false),
        AgentProvider(id: "openai", displayName: "OpenAI",
                      baseURL: "https://api.openai.com/v1", wire: .openAIChatCompletions,
                      environmentKey: "OPENAI_API_KEY", defaultModel: "gpt-5",
                      publicModelList: false),
        AgentProvider(id: "zai", displayName: "Z.AI",
                      baseURL: "https://api.z.ai/api/paas/v4", wire: .openAIChatCompletions,
                      environmentKey: "ZAI_API_KEY", defaultModel: "glm-4.6",
                      publicModelList: false),
        AgentProvider(id: "moonshot", displayName: "Moonshot AI",
                      baseURL: "https://api.moonshot.ai/v1", wire: .openAIChatCompletions,
                      environmentKey: "MOONSHOT_API_KEY", defaultModel: "kimi-k2-turbo-preview",
                      publicModelList: false),
        AgentProvider(id: "openrouter", displayName: "OpenRouter",
                      baseURL: "https://openrouter.ai/api/v1", wire: .openAIChatCompletions,
                      environmentKey: "OPENROUTER_API_KEY", defaultModel: "anthropic/claude-opus-5",
                      publicModelList: true),
    ]

    static func named(_ id: String) -> AgentProvider? {
        let key = id.trimmingCharacters(in: .whitespaces).lowercased()
        return all.first { $0.id == key || $0.displayName.lowercased() == key }
    }

    func authorize(_ request: inout URLRequest, key: String) {
        guard !key.isEmpty else { return }
        switch wire {
        case .anthropicMessages:
            request.setValue(key, forHTTPHeaderField: "x-api-key")
            request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        case .openAIChatCompletions:
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            if id == "openrouter" {
                // OpenRouter attributes traffic with these; both are optional.
                request.setValue("https://palmier.pro", forHTTPHeaderField: "HTTP-Referer")
                request.setValue("Palmier Pro", forHTTPHeaderField: "X-Title")
            }
        }
    }
}

// MARK: - Model listing

/// Fetches the provider's model ids. Every supported provider exposes
/// `GET {base}/models` returning `{"data":[{"id":…}]}`.
func fetchProviderModels(_ provider: AgentProvider, key: String) throws -> [String] {
    var request = URLRequest(url: URL(string: "\(provider.baseURL)/models")!)
    request.httpMethod = "GET"
    request.timeoutInterval = 30
    provider.authorize(&request, key: key)

    let (data, status) = try synchronousData(for: request)
    guard status == 200 else {
        throw NSError(domain: "PalmierAgent", code: status, userInfo: [
            NSLocalizedDescriptionKey: providerErrorMessage(data) ?? "HTTP \(status) listing models",
        ])
    }
    guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
          let entries = root["data"] as? [[String: Any]] else {
        throw NSError(domain: "PalmierAgent", code: 3,
                      userInfo: [NSLocalizedDescriptionKey: "Unexpected model list format."])
    }
    // Newest first where the provider dates its models (OpenRouter and OpenAI
    // both send `created`), so browsing lands on current models instead of
    // whatever sorts alphabetically first. Undated lists stay alphabetical.
    let dated = entries.compactMap { entry -> (id: String, created: Double)? in
        guard let id = entry["id"] as? String else { return nil }
        return (id, (entry["created"] as? Double) ?? 0)
    }
    guard dated.contains(where: { $0.created > 0 }) else {
        return dated.map(\.id).sorted()
    }
    return dated.sorted { $0.created > $1.created }.map(\.id)
}

/// Both wire formats bury the message differently; try each shape.
func providerErrorMessage(_ data: Data) -> String? {
    guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return nil }
    if let error = root["error"] as? [String: Any], let message = error["message"] as? String { return message }
    if let message = root["error"] as? String { return message }
    if let message = root["message"] as? String { return message }
    return nil
}

/// URLSession has no synchronous API; the agent already owns a background
/// thread, so blocking it on a semaphore is the simplest correct thing.
private func synchronousData(for request: URLRequest) throws -> (Data, Int) {
    final class Box: @unchecked Sendable {
        var data = Data()
        var status = 0
        var error: Error?
    }
    let box = Box()
    let done = DispatchSemaphore(value: 0)
    URLSession.shared.dataTask(with: request) { data, response, error in
        box.data = data ?? Data()
        box.status = (response as? HTTPURLResponse)?.statusCode ?? 0
        box.error = error
        done.signal()
    }.resume()
    done.wait()
    if let error = box.error { throw error }
    return (box.data, box.status)
}

// MARK: - OpenAI-compatible chat completions

/// Streams one chat/completions request and returns Anthropic-shaped blocks
/// plus a stop reason, so the caller's tool loop is provider-agnostic.
func openAIStreamRequest(_ ctx: AgentContext, provider: AgentProvider, apiKey: String,
                         model: String, messages: [[String: Any]]) throws -> ([[String: Any]], String) {
    var request = URLRequest(url: URL(string: "\(provider.baseURL)/chat/completions")!)
    request.httpMethod = "POST"
    request.timeoutInterval = 600
    request.setValue("application/json", forHTTPHeaderField: "Content-Type")
    provider.authorize(&request, key: apiKey)

    let body: [String: Any] = [
        "model": model,
        "stream": true,
        "messages": openAIMessages(from: messages),
        "tools": openAIToolSchemas(),
    ]
    request.httpBody = try JSONSerialization.data(withJSONObject: body)

    // Tool calls arrive as fragments keyed by index; text arrives as deltas.
    final class StreamState: @unchecked Sendable {
        var text = ""
        var toolIds: [Int: String] = [:]
        var toolNames: [Int: String] = [:]
        var toolArguments: [Int: String] = [:]
        var finishReason = "stop"
        var apiError: String?
        var emittedText = false
    }
    let state = StreamState()

    let collector = SSECollector { line in
        guard line.hasPrefix("data:") else { return }
        let payload = line.dropFirst(5).trimmingCharacters(in: .whitespaces)
        guard payload != "[DONE]", let data = payload.data(using: .utf8),
              let event = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
        if let error = event["error"] as? [String: Any] {
            state.apiError = error["message"] as? String ?? "Stream error"
            return
        }
        guard let choice = (event["choices"] as? [[String: Any]])?.first else { return }
        if let reason = choice["finish_reason"] as? String { state.finishReason = reason }
        guard let delta = choice["delta"] as? [String: Any] else { return }

        if let text = delta["content"] as? String, !text.isEmpty {
            state.text += text
            state.emittedText = true
            ctx.emit(["type": "text_delta", "text": text])
        }
        for call in delta["tool_calls"] as? [[String: Any]] ?? [] {
            let index = call["index"] as? Int ?? 0
            if let id = call["id"] as? String { state.toolIds[index] = id }
            guard let function = call["function"] as? [String: Any] else { continue }
            if let name = function["name"] as? String, !name.isEmpty { state.toolNames[index] = name }
            if let arguments = function["arguments"] as? String {
                state.toolArguments[index, default: ""] += arguments
            }
        }
    }

    let session = URLSession(configuration: .default, delegate: collector, delegateQueue: nil)
    session.dataTask(with: request).resume()
    collector.done.wait()
    session.finishTasksAndInvalidate()

    if let error = collector.completionError { throw error }
    if collector.status != 200 {
        throw NSError(domain: "PalmierAgent", code: collector.status, userInfo: [
            NSLocalizedDescriptionKey: providerErrorMessage(collector.errorBody) ?? "HTTP \(collector.status)",
        ])
    }
    if let apiError = state.apiError {
        throw NSError(domain: "PalmierAgent", code: 2, userInfo: [NSLocalizedDescriptionKey: apiError])
    }

    var blocks: [[String: Any]] = []
    if !state.text.isEmpty {
        blocks.append(["type": "text", "text": state.text])
        if state.emittedText { ctx.emit(["type": "text_end"]) }
    }
    for index in state.toolNames.keys.sorted() {
        let rawArguments = state.toolArguments[index] ?? "{}"
        let input = (try? JSONSerialization.jsonObject(
            with: (rawArguments.isEmpty ? "{}" : rawArguments).data(using: .utf8) ?? Data()))
            as? [String: Any] ?? [:]
        blocks.append([
            "type": "tool_use",
            "id": state.toolIds[index] ?? "call_\(index)",
            "name": state.toolNames[index] ?? "",
            "input": input,
        ])
    }
    // finish_reason is "tool_calls" on OpenAI, but some gateways omit it when
    // the stream ends on a tool call — trust the blocks we actually collected.
    let stop = blocks.contains { $0["type"] as? String == "tool_use" } ? "tool_use"
             : state.finishReason == "content_filter" ? "refusal" : "end_turn"
    return (blocks, stop)
}

/// Anthropic-shaped conversation → OpenAI chat messages. Tool results become
/// their own `role: "tool"` messages, which is the only structural difference.
private func openAIMessages(from conversation: [[String: Any]]) -> [[String: Any]] {
    var out: [[String: Any]] = [["role": "system", "content": agentSystemPrompt]]
    for message in conversation {
        let role = message["role"] as? String ?? "user"
        if let text = message["content"] as? String {
            out.append(["role": role, "content": text])
            continue
        }
        let blocks = message["content"] as? [[String: Any]] ?? []
        if role == "assistant" {
            var text = ""
            var toolCalls: [[String: Any]] = []
            for block in blocks {
                switch block["type"] as? String {
                case "text":
                    text += block["text"] as? String ?? ""
                case "tool_use":
                    let input = block["input"] as? [String: Any] ?? [:]
                    let arguments = (try? JSONSerialization.data(withJSONObject: input))
                        .flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
                    toolCalls.append([
                        "id": block["id"] as? String ?? "",
                        "type": "function",
                        "function": ["name": block["name"] as? String ?? "", "arguments": arguments],
                    ])
                default:
                    break
                }
            }
            var assistant: [String: Any] = ["role": "assistant", "content": text]
            if !toolCalls.isEmpty { assistant["tool_calls"] = toolCalls }
            out.append(assistant)
        } else {
            for block in blocks where block["type"] as? String == "tool_result" {
                out.append([
                    "role": "tool",
                    "tool_call_id": block["tool_use_id"] as? String ?? "",
                    "content": block["content"] as? String ?? "",
                ])
            }
        }
    }
    return out
}

private func openAIToolSchemas() -> [[String: Any]] {
    agentToolSchemas().map { tool in
        [
            "type": "function",
            "function": [
                "name": tool["name"] as? String ?? "",
                "description": tool["description"] as? String ?? "",
                "parameters": tool["input_schema"] as? [String: Any] ?? [:],
            ],
        ]
    }
}
