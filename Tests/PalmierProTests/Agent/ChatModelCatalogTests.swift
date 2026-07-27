import Foundation
import Testing
import PalmierCore
@testable import PalmierPro

@Suite("ChatModelCatalog")
struct ChatModelCatalogTests {

    @Test func anthropicModelsIncludeSonnetOpusHaiku() {
        let ids = ChatModelCatalog.anthropicModels.map(\.id)
        #expect(ids.contains("claude-sonnet-5"))
        #expect(ids.contains("claude-opus-4-8"))
        #expect(ids.contains("claude-haiku-4-5-20251001"))
        #expect(ChatModelCatalog.anthropicModels.allSatisfy { $0.provider == "anthropic" })
    }

    @Test func openaiModelsAreTaggedOpenAI() {
        #expect(!ChatModelCatalog.openaiModels.isEmpty)
        #expect(ChatModelCatalog.openaiModels.allSatisfy { $0.provider == "openai" })
    }

    @Test func modelsForProviderReturnsCatalog() {
        #expect(ChatModelCatalog.models(for: "anthropic").count == ChatModelCatalog.anthropicModels.count)
        #expect(ChatModelCatalog.models(for: "openai").count == ChatModelCatalog.openaiModels.count)
        #expect(ChatModelCatalog.models(for: "unknown").isEmpty)
    }

    @Test func resolveKnownIdReturnsMatchingModel() {
        let model = ChatModelCatalog.resolve(provider: "anthropic", id: "claude-opus-4-8")
        #expect(model.id == "claude-opus-4-8")
        #expect(model.displayName == "Opus 4.8")
    }

    @Test func resolveUnknownIdFallsBackToProviderDefault() {
        let model = ChatModelCatalog.resolve(provider: "anthropic", id: "nope")
        #expect(model == ChatModelCatalog.anthropicModels.first)
    }

    @Test func resolveUnknownProviderFallsBackToGlobalDefault() {
        let model = ChatModelCatalog.resolve(provider: "unknown", id: "")
        #expect(model == ChatModelCatalog.defaultModel)
    }

    @Test func defaultModelIsAnthropicSonnet() {
        #expect(ChatModelCatalog.defaultModel.provider == "anthropic")
        #expect(ChatModelCatalog.defaultModel.id == "claude-sonnet-5")
    }
}

@Suite("AgentService - provider/model selection")
@MainActor
struct AgentServiceProviderTests {

    @Test func switchingProviderClampsModelIntoNewCatalog() {
        let service = AgentService()
        // Start on anthropic with a known anthropic model.
        service.provider = .anthropic
        service.model = ChatModel(provider: "anthropic", id: "claude-opus-4-8", displayName: "Opus 4.8")
        // Switching provider must not leave an OpenAI-incompatible model selected.
        service.provider = .openai
        #expect(service.model.provider == "openai")
        #expect(ChatModelCatalog.openaiModels.contains(service.model))
    }

    @Test func effectiveModelFallsBackWhenSelectionOutsideCatalog() {
        let service = AgentService()
        // Force a model that's not in the openai catalog.
        service.provider = .openai
        service.model = ChatModel(provider: "openai", id: "ghost-model", displayName: "Ghost")
        let effective = service.effectiveModel
        #expect(ChatModelCatalog.openaiModels.contains(effective))
    }

    @Test func availableModelsFollowsEffectiveProvider() {
        let service = AgentService()
        service.provider = .anthropic
        #expect(service.availableModels == ChatModelCatalog.anthropicModels)
        service.provider = .openai
        #expect(service.availableModels == ChatModelCatalog.openaiModels)
    }
}
