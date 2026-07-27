import Foundation
import PalmierCore

/// Catalog of chat models the in-app agent can select, keyed by provider.
/// `id` matches the provider's model identifier and — for Anthropic — the
/// `AnthropicModel.rawValue`, so a stored `id` round-trips through
/// `AnthropicModel(rawValue:)` at the client boundary.
enum ChatModelCatalog {
    static let anthropic: [ChatModel] = [
        ChatModel(provider: "anthropic", id: "claude-sonnet-5", displayName: "Sonnet 5"),
        ChatModel(provider: "anthropic", id: "claude-opus-4-8", displayName: "Opus 4.8"),
        ChatModel(provider: "anthropic", id: "claude-haiku-4-5-20251001", displayName: "Haiku 4.5"),
    ]

    /// Default selection when no model is stored or the stored id is unknown.
    static let defaultModel: ChatModel = anthropic[0]

    /// Resolve a stored model id back to a catalog entry, defaulting to Sonnet 5.
    static func resolve(id: String) -> ChatModel {
        anthropic.first { $0.id == id } ?? defaultModel
    }
}
