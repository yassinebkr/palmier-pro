import Foundation
import PalmierCore

/// A chat provider the in-app agent can use. `id` is the `ChatModel.provider`
/// value; `requiresAPIKey` distinguishes providers backed by a user-supplied
/// key (Anthropic, OpenAI) from the Palmier account backend (Convex).
struct ChatProvider: Identifiable, Hashable, Sendable {
    let id: String
    let displayName: String
    let requiresAPIKey: Bool

    static let anthropic = ChatProvider(id: "anthropic", displayName: "Anthropic", requiresAPIKey: true)
    static let openai = ChatProvider(id: "openai", displayName: "OpenAI", requiresAPIKey: true)
    static let palmier = ChatProvider(id: "palmier", displayName: "Palmier Cloud", requiresAPIKey: false)
}

/// Catalog of providers and the chat models each exposes. `model.id` matches
/// the provider's model identifier and — for Anthropic — the
/// `AnthropicModel.rawValue`, so a stored id round-trips through
/// `AnthropicModel(rawValue:)` at the client boundary.
enum ChatModelCatalog {

    /// Providers selectable in the UI, in display order. The Palmier cloud
    /// backend is available only when the user is signed in.
    static let apiKeyProviders: [ChatProvider] = [.anthropic, .openai]

    static let anthropicModels: [ChatModel] = [
        ChatModel(provider: "anthropic", id: "claude-sonnet-5", displayName: "Sonnet 5"),
        ChatModel(provider: "anthropic", id: "claude-opus-4-8", displayName: "Opus 4.8"),
        ChatModel(provider: "anthropic", id: "claude-haiku-4-5-20251001", displayName: "Haiku 4.5"),
    ]

    static let openaiModels: [ChatModel] = [
        ChatModel(provider: "openai", id: "gpt-4o", displayName: "GPT-4o"),
        ChatModel(provider: "openai", id: "gpt-4o-mini", displayName: "GPT-4o mini"),
    ]

    static func models(for providerID: String) -> [ChatModel] {
        switch providerID {
        case "anthropic": return anthropicModels
        case "openai": return openaiModels
        default: return []
        }
    }

    /// Default selection when no model is stored or the stored id is unknown.
    static let defaultModel: ChatModel = anthropicModels[0]

    /// Resolve a stored `(provider, id)` pair back to a catalog entry,
    /// defaulting to Sonnet 5.
    static func resolve(provider: String, id: String) -> ChatModel {
        models(for: provider).first { $0.id == id }
            ?? models(for: provider).first
            ?? defaultModel
    }
}
