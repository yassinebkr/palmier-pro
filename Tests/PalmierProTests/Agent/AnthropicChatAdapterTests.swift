import Foundation
import Testing
import PalmierCore
@testable import PalmierPro

@Suite("AnthropicChatAdapter")
struct AnthropicChatAdapterTests {

    // MARK: toolSchema

    @Test func toolSchemaPreservesNameAndDescription() {
        let schema = ToolSchema(
            name: "manage_project",
            description: "Manage the project.",
            inputSchema: JSONValue(["type": "object"])
        )
        let result = AnthropicChatAdapter.toolSchema(from: schema)
        #expect(result.name == "manage_project")
        #expect(result.description == "Manage the project.")
    }

    @Test func toolSchemaUnwrapsJSONValueToPlainDict() {
        // ToolDefinitions builds schemas as [String: Any]; JSONValue(_:) is the
        // bridge into ToolSchema. Mirror that here.
        let schema = ToolSchema(
            name: "t",
            description: "",
            inputSchema: JSONValue([
                "type": "object",
                "required": ["frame"],
                "properties": ["frame": ["type": "integer"]],
            ])
        )
        let result = AnthropicChatAdapter.toolSchema(from: schema)
        let input = result.inputSchema
        #expect(input["type"] as? String == "object")
        #expect(input["required"] as? [String] == ["frame"])
        let props = input["properties"] as? [String: Any]
        #expect((props?["frame"] as? [String: Any])?["type"] as? String == "integer")
    }

    // MARK: message — block translation

    @Test func messageMapsRoleAndTextBlock() {
        let msg = ChatMessage(role: .user, content: [.text("hello")])
        let result = AnthropicChatAdapter.message(from: msg)
        #expect(result.role == .user)
        #expect(result.content.count == 1)
        #expect(result.content[0]["type"] as? String == "text")
        #expect(result.content[0]["text"] as? String == "hello")
    }

    @Test func messageDropsEmptyTextBlocks() {
        let msg = ChatMessage(role: .assistant, content: [.text(""), .text("ok")])
        let result = AnthropicChatAdapter.message(from: msg)
        #expect(result.content.count == 1)
        #expect(result.content[0]["text"] as? String == "ok")
    }

    @Test func messageMapsImageBlockToBase64Source() {
        let msg = ChatMessage(role: .user, content: [.image(mediaType: "image/png", base64: "QUJD")])
        let result = AnthropicChatAdapter.message(from: msg)
        #expect(result.content.count == 1)
        let block = result.content[0]
        #expect(block["type"] as? String == "image")
        let source = block["source"] as? [String: Any]
        #expect(source?["type"] as? String == "base64")
        #expect(source?["media_type"] as? String == "image/png")
        #expect(source?["data"] as? String == "QUJD")
    }

    @Test func messageMapsToolCallBlock() {
        let msg = ChatMessage(role: .assistant, content: [
            .toolCall(id: "tu_1", name: "split_clip", inputJSON: #"{"frame":120}"#),
        ])
        let result = AnthropicChatAdapter.message(from: msg)
        #expect(result.content.count == 1)
        let block = result.content[0]
        #expect(block["type"] as? String == "tool_use")
        #expect(block["id"] as? String == "tu_1")
        #expect(block["name"] as? String == "split_clip")
        #expect((block["input"] as? [String: Any])?["frame"] as? Int == 120)
    }

    @Test func messageMapsToolResultTextBlock() {
        let msg = ChatMessage(role: .user, content: [
            .toolResult(toolCallID: "tu_1", content: [.text("done")], isError: false),
        ])
        let result = AnthropicChatAdapter.message(from: msg)
        #expect(result.content.count == 1)
        let block = result.content[0]
        #expect(block["type"] as? String == "tool_result")
        #expect(block["tool_use_id"] as? String == "tu_1")
        let contentArr = block["content"] as? [[String: Any]]
        #expect(contentArr?.count == 1)
        #expect(contentArr?[0]["type"] as? String == "text")
        #expect(contentArr?[0]["text"] as? String == "done")
        #expect(block["is_error"] as? Bool == false)
    }

    @Test func messageMapsToolResultImageBlockPreservingBase64() {
        // capture_frame / inspect_media return image tool results; the adapter
        // must not drop them when translating to Anthropic's wire format.
        let msg = ChatMessage(role: .user, content: [
            .toolResult(
                toolCallID: "tu_2",
                content: [.image(mediaType: "image/jpeg", base64: "QUJD")],
                isError: false
            ),
        ])
        let result = AnthropicChatAdapter.message(from: msg)
        let block = result.content[0]
        let contentArr = block["content"] as? [[String: Any]]
        #expect(contentArr?.count == 1)
        #expect(contentArr?[0]["type"] as? String == "image")
        let source = contentArr?[0]["source"] as? [String: Any]
        #expect(source?["type"] as? String == "base64")
        #expect(source?["media_type"] as? String == "image/jpeg")
        #expect(source?["data"] as? String == "QUJD")
    }

    @Test func assistantRoleMapsToAssistant() {
        let msg = ChatMessage(role: .assistant, content: [.text("x")])
        #expect(AnthropicChatAdapter.message(from: msg).role == .assistant)
    }

    // MARK: stopReason

    @Test func stopReasonMapsDirectCases() {
        #expect(AnthropicChatAdapter.stopReason(.endTurn) == .endTurn)
        #expect(AnthropicChatAdapter.stopReason(.toolUse) == .toolUse)
        #expect(AnthropicChatAdapter.stopReason(.maxTokens) == .maxTokens)
        #expect(AnthropicChatAdapter.stopReason(.stopSequence) == .stopSequence)
        #expect(AnthropicChatAdapter.stopReason(.refusal) == .refusal)
    }

    @Test func stopReasonCollapsesProviderSpecificCasesToOther() {
        // pause_turn is Anthropic-specific with no neutral equivalent.
        #expect(AnthropicChatAdapter.stopReason(.pauseTurn) == .other)
        #expect(AnthropicChatAdapter.stopReason(.other) == .other)
    }

    // MARK: chatStreamEvent — Anthropic stream → neutral stream

    @Test func chatStreamEventMapsTextDelta() {
        let result = AnthropicChatAdapter.chatStreamEvent(from: .textDelta("chunk"))
        if case .textDelta(let s) = result {
            #expect(s == "chunk")
        } else {
            Issue.record("expected .textDelta")
        }
    }

    @Test func chatStreamEventMapsToolUseComplete() {
        let result = AnthropicChatAdapter.chatStreamEvent(from: .toolUseComplete(id: "tu_9", name: "add_clips", inputJSON: "{}"))
        if case .toolCallComplete(let id, let name, let json) = result {
            #expect(id == "tu_9")
            #expect(name == "add_clips")
            #expect(json == "{}")
        } else {
            Issue.record("expected .toolCallComplete")
        }
    }

    @Test func chatStreamEventMapsMessageStop() {
        let result = AnthropicChatAdapter.chatStreamEvent(from: .messageStop(stopReason: .toolUse))
        if case .stop(let reason) = result {
            #expect(reason == .toolUse)
        } else {
            Issue.record("expected .stop")
        }
    }
}
