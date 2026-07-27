import Foundation
import PalmierCore

extension Notification.Name {
    static let openaiAPIKeyChanged = Notification.Name("openaiAPIKeyChanged")
}

enum OpenAIKeychain {
    private static let account = "openai-api-key"

    static func save(_ key: String) {
        KeychainStore.save(key, account: account)
        NotificationCenter.default.post(name: .openaiAPIKeyChanged, object: nil)
    }

    static func load() -> String? {
        #if DEBUG
        if let env = ProcessInfo.processInfo.environment["OPENAI_API_KEY"]?
            .trimmingCharacters(in: .whitespacesAndNewlines),
           !env.isEmpty {
            return env
        }
        #endif
        return KeychainStore.load(account: account)
    }

    static func delete() {
        KeychainStore.delete(account: account)
        NotificationCenter.default.post(name: .openaiAPIKeyChanged, object: nil)
    }
}

enum OpenAIClientError: LocalizedError {
    case missingAPIKey
    case httpError(status: Int, body: String)
    case streamError(String)

    var errorDescription: String? {
        switch self {
        case .missingAPIKey: "No OpenAI API key is set."
        case .httpError(let status, let body): "OpenAI API error (\(status)): \(body.prefix(500))"
        case .streamError(let msg): "Stream error: \(msg)"
        }
    }
}

/// OpenAI Chat Completions client. Conforms to the provider-neutral `ChatClient`
/// via `OpenAIChatAdapter`; everything OpenAI-specific (SSE shape, tool-call
/// fan-out, `data:[DONE]`) lives in the adapter. `baseURL` is overridable so
/// OpenAI-compatible endpoints (OpenRouter, Together, local) can reuse this.
struct OpenAIClient: ChatClient {
    let apiKey: String
    let model: String
    let baseURL: URL
    var maxTokens: Int? = nil

    init(
        apiKey: String,
        model: String,
        baseURL: URL = URL(string: "https://api.openai.com/v1")!,
        maxTokens: Int? = nil
    ) {
        self.apiKey = apiKey
        self.model = model
        self.baseURL = baseURL
        self.maxTokens = maxTokens
    }

    func stream(
        system: String,
        tools: [ToolSchema],
        messages: [ChatMessage]
    ) -> AsyncThrowingStream<ChatStreamEvent, Error> {
        AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    try await run(system: system, tools: tools, messages: messages, continuation: continuation)
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    private func run(
        system: String,
        tools: [ToolSchema],
        messages: [ChatMessage],
        continuation: AsyncThrowingStream<ChatStreamEvent, Error>.Continuation
    ) async throws {
        guard !apiKey.isEmpty else { throw OpenAIClientError.missingAPIKey }

        var body = OpenAIChatAdapter.requestBody(
            model: model, system: system, tools: tools, messages: messages
        )
        if let maxTokens { body["max_tokens"] = maxTokens }

        var request = URLRequest(url: baseURL.appendingPathComponent("chat/completions"))
        request.httpMethod = "POST"
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "content-type")
        request.setValue("text/event-stream", forHTTPHeaderField: "accept")
        request.httpBody = try JSONSerialization.data(withJSONObject: body, options: [.sortedKeys])

        let (bytes, response) = try await URLSession.shared.bytes(for: request)
        if let http = response as? HTTPURLResponse, http.statusCode >= 400 {
            var responseBody = ""
            for try await line in bytes.lines { responseBody += line + "\n" }
            throw OpenAIClientError.httpError(status: http.statusCode, body: responseBody)
        }

        try await OpenAIChatAdapter.parse(bytes: bytes) { continuation.yield($0) }
    }
}
