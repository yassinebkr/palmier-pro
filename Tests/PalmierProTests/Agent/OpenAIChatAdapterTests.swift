import Foundation
import Testing
import PalmierCore
@testable import PalmierPro

@Suite("OpenAIChatAdapter")
struct OpenAIChatAdapterTests {

    // MARK: requestBody — system + tool schema

    @Test func requestBodyEmitsSystemMessageFirst() {
        let body = OpenAIChatAdapter.requestBody(
            model: "gpt-4o", system: "You are an editor.", tools: [], messages: []
        )
        let messages = body["messages"] as? [[String: Any]]
        #expect(messages?.count == 1)
        #expect(messages?[0]["role"] as? String == "system")
        #expect(messages?[0]["content"] as? String == "You are an editor.")
        #expect(body["model"] as? String == "gpt-4o")
        #expect(body["stream"] as? Bool == true)
        #expect(body["tools"] == nil)
    }

    @Test func requestBodyWrapsToolSchemaInFunctionEnvelope() {
        let schema = ToolSchema(
            name: "add_clips",
            description: "Add clips to a track.",
            inputSchema: JSONValue(["type": "object", "properties": ["trackId": ["type": "string"]]])
        )
        let body = OpenAIChatAdapter.requestBody(model: "m", system: "", tools: [schema], messages: [])
        let tools = body["tools"] as? [[String: Any]]
        #expect(tools?.count == 1)
        #expect(tools?[0]["type"] as? String == "function")
        let function = tools?[0]["function"] as? [String: Any]
        #expect(function?["name"] as? String == "add_clips")
        #expect(function?["description"] as? String == "Add clips to a track.")
        let params = function?["parameters"] as? [String: Any]
        #expect(params?["type"] as? String == "object")
    }

    // MARK: messages — neutral → OpenAI

    @Test func userTextMessageBecomesStringContent() {
        let out = OpenAIChatAdapter.messages(from: ChatMessage(role: .user, content: [.text("hi")]))
        #expect(out.count == 1)
        #expect(out[0]["role"] as? String == "user")
        #expect(out[0]["content"] as? String == "hi")
    }

    @Test func userImageMessageUsesDataURIImageURL() {
        let out = OpenAIChatAdapter.messages(from: ChatMessage(role: .user, content: [
            .text("what is this"),
            .image(mediaType: "image/png", base64: "QUJD"),
        ]))
        #expect(out.count == 1)
        let content = out[0]["content"] as? [[String: Any]]
        #expect(content?.count == 2)
        #expect(content?[0]["type"] as? String == "text")
        let img = content?[1]
        #expect(img?["type"] as? String == "image_url")
        let url = (img?["image_url"] as? [String: Any])?["url"] as? String
        #expect(url == "data:image/png;base64,QUJD")
    }

    @Test func assistantToolCallsLiftedIntoToolCallsArray() {
        let out = OpenAIChatAdapter.messages(from: ChatMessage(role: .assistant, content: [
            .text("ok"),
            .toolCall(id: "call_1", name: "add_clips", inputJSON: #"{"trackId":"t1"}"#),
        ]))
        #expect(out.count == 1)
        let msg = out[0]
        #expect(msg["role"] as? String == "assistant")
        #expect(msg["content"] as? String == "ok")
        let calls = msg["tool_calls"] as? [[String: Any]]
        #expect(calls?.count == 1)
        #expect(calls?[0]["id"] as? String == "call_1")
        #expect(calls?[0]["type"] as? String == "function")
        let fn = calls?[0]["function"] as? [String: Any]
        #expect(fn?["name"] as? String == "add_clips")
        #expect(fn?["arguments"] as? String == #"{"trackId":"t1"}"#)
    }

    @Test func assistantToolOnlyMessageUsesNullContent() {
        let out = OpenAIChatAdapter.messages(from: ChatMessage(role: .assistant, content: [
            .toolCall(id: "call_1", name: "x", inputJSON: "{}"),
        ]))
        #expect(out.count == 1)
        // content must be present (null) so OpenAI accepts the message.
        #expect(out[0]["content"] is NSNull)
    }

    @Test func toolResultsFanOutToRoleToolMessages() {
        let out = OpenAIChatAdapter.messages(from: ChatMessage(role: .user, content: [
            .toolResult(toolCallID: "call_1", content: [.text("done")], isError: false),
            .toolResult(toolCallID: "call_2", content: [.text("boom")], isError: true),
        ]))
        #expect(out.count == 2)
        #expect(out[0]["role"] as? String == "tool")
        #expect(out[0]["tool_call_id"] as? String == "call_1")
        #expect(out[0]["content"] as? String == "done")
        #expect(out[1]["role"] as? String == "tool")
        #expect(out[1]["tool_call_id"] as? String == "call_2")
        // Error results are prefixed, not dropped.
        #expect(out[1]["content"] as? String == "[tool error] boom")
    }

    @Test func toolResultImagePreservedAsMultipartContent() {
        let out = OpenAIChatAdapter.messages(from: ChatMessage(role: .user, content: [
            .toolResult(toolCallID: "call_1", content: [
                .image(mediaType: "image/jpeg", base64: "QUJD"),
                .text("frame at 120"),
            ], isError: false),
        ]))
        #expect(out.count == 1)
        let content = out[0]["content"] as? [[String: Any]]
        #expect(content?.count == 2)
        #expect(content?[0]["type"] as? String == "image_url")
        #expect(content?[1]["type"] as? String == "text")
    }

    // MARK: stopReason

    @Test(arguments: [
        ("stop", ChatStopReason.endTurn),
        ("tool_calls", ChatStopReason.toolUse),
        ("function_call", ChatStopReason.toolUse),
        ("length", ChatStopReason.maxTokens),
        ("content_filter", ChatStopReason.refusal),
    ])
    func stopReasonMapsKnownFinishReasons(_ input: String, _ expected: ChatStopReason) {
        #expect(OpenAIChatAdapter.stopReason(input) == expected)
    }

    @Test func stopReasonDefaultsToEndTurnForNil() {
        #expect(OpenAIChatAdapter.stopReason(nil) == .endTurn)
    }

    // MARK: processChunk — streaming tool calls

    @Test func processChunkEmitsTextDelta() {
        var state = OpenAIChatAdapter.StreamState()
        let chunk: [String: Any] = [
            "choices": [["delta": ["content": "hello"], "finish_reason": NSNull()]]
        ]
        let events = OpenAIChatAdapter.processChunk(chunk, state: &state)
        #expect(events.count == 1)
        if case .textDelta(let s) = events[0] {
            #expect(s == "hello")
        } else { Issue.record("expected textDelta") }
    }

    @Test func processChunkAccumulatesToolCallAcrossDeltasThenFlushesOnFinish() {
        var state = OpenAIChatAdapter.StreamState()
        // First delta: id + name + first argument fragment.
        let first: [String: Any] = [
            "choices": [["delta": [
                "tool_calls": [[
                    "index": 0,
                    "id": "call_9",
                    "type": "function",
                    "function": ["name": "add_clips", "arguments": #"{"track"#],
                ]],
            ]]]
        ]
        // Second delta: argument fragment only (no id/name).
        let second: [String: Any] = [
            "choices": [["delta": [
                "tool_calls": [[
                    "index": 0,
                    "function": ["arguments": #"Id":"t1"}"#],
                ]],
            ]]]
        ]
        // Terminal chunk: finish_reason = tool_calls.
        let terminal: [String: Any] = [
            "choices": [["delta": [:], "finish_reason": "tool_calls"]]
        ]

        let e1 = OpenAIChatAdapter.processChunk(first, state: &state)
        #expect(e1.isEmpty) // not flushed until finish_reason
        let e2 = OpenAIChatAdapter.processChunk(second, state: &state)
        #expect(e2.isEmpty)
        let e3 = OpenAIChatAdapter.processChunk(terminal, state: &state)
        // toolCallComplete then stop(.toolUse).
        #expect(e3.count == 2)
        if case .toolCallComplete(let id, let name, let json) = e3[0] {
            #expect(id == "call_9")
            #expect(name == "add_clips")
            #expect(json == #"{"trackId":"t1"}"#)
        } else { Issue.record("expected toolCallComplete") }
        if case .stop(let reason) = e3[1] {
            #expect(reason == .toolUse)
        } else { Issue.record("expected stop") }
    }

    @Test func processChunkParallelToolCallsOrderByIndex() {
        var state = OpenAIChatAdapter.StreamState()
        let chunk: [String: Any] = [
            "choices": [["delta": [
                "tool_calls": [
                    ["index": 1, "id": "b", "function": ["name": "y", "arguments": "{}"]],
                    ["index": 0, "id": "a", "function": ["name": "x", "arguments": "{}"]],
                ],
            ], "finish_reason": "tool_calls"]]
        ]
        let events = OpenAIChatAdapter.processChunk(chunk, state: &state)
        // Two tool-call completions in index order, then stop.
        #expect(events.count == 3)
        if case .toolCallComplete(let id, _, _) = events[0] { #expect(id == "a") } else { Issue.record("0") }
        if case .toolCallComplete(let id, _, _) = events[1] { #expect(id == "b") } else { Issue.record("1") }
        if case .stop = events[2] {} else { Issue.record("expected stop") }
    }

    // MARK: flush — stream closes without finish_reason

    @Test func flushEmitsAccumulatedToolCallsThenStopToolUse() {
        var state = OpenAIChatAdapter.StreamState()
        let accumulate: [String: Any] = [
            "choices": [["delta": [
                "tool_calls": [["index": 0, "id": "call_1", "function": ["name": "x", "arguments": "{}"]]],
            ]]]
        ]
        _ = OpenAIChatAdapter.processChunk(accumulate, state: &state)
        let flushed = OpenAIChatAdapter.flush(state: &state)
        #expect(flushed.count == 2)
        if case .toolCallComplete(let id, _, _) = flushed[0] { #expect(id == "call_1") } else { Issue.record("toolCall") }
        if case .stop(let r) = flushed[1] { #expect(r == .toolUse) } else { Issue.record("stop") }
    }

    @Test func flushWithNoToolCallsEmitsEndTurn() {
        var state = OpenAIChatAdapter.StreamState()
        let flushed = OpenAIChatAdapter.flush(state: &state)
        #expect(flushed.count == 1)
        if case .stop(let r) = flushed[0] { #expect(r == .endTurn) } else { Issue.record("stop") }
    }
}
