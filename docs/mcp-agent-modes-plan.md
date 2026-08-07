# MCP server + agent mode switch — plan

Let external MCP clients (Claude Desktop, Kimi, Hermes, OpenClaw…) drive
PalmierWin's editor, alongside the built-in inline agent — and let the user
switch between the two, see exactly which client is connected, and manage the
connection live. Mode is chosen at first run (welcome step) and switchable any
time (settings). Reference: the macOS app already ships this surface — an MCP
HTTP server on 127.0.0.1:19789 inside the app plus `mcpb/server/index.js`, a
stdio→HTTP shim for Claude Desktop.

## Current state (map)

- The inline agent runs in the engine (`PalmierCoreHost/AgentHost.swift`):
  chat-oriented ABI (`create/configure/send/poll/retry/permission/cancel`),
  with exactly 10 tools executed inside the chat loop — `get_timeline`,
  `list_media`, `add_clip`, `add_text_clip`, `remove_clip`, `split_clip`,
  `move_clip`, `trim_clip`, `set_clip_properties`, `set_playhead`
  (`AgentHost.swift:618-672`). There is no raw "execute tool" ABI, so an
  external client cannot ride AgentHost directly.
- The shell already performs every one of those domain operations through
  `CoreApi` + `TimelineViewModel`, wrapped by `UndoStack` (one user intent =
  one undo entry). The MCP server routes through the same operations.
- macOS reference implementation: `Sources/PalmierPro/Agent/MCP/`
  (MCPHTTPServer.swift raw HTTP+SSE, MCPService.swift method routing,
  MCPClientInfo.swift captures client name/version at `initialize`).
  `mcpb/server/index.js` is a Node stdio→HTTP shim — platform-independent,
  usable as-is on Windows.

## Architecture

- **`Core/Mcp/McpServer.cs`** (shell): `HttpListener` bound to
  `http://127.0.0.1:19789/` (loopback only — never LAN). One endpoint
  `POST /mcp` speaking JSON-RPC 2.0, Streamable-HTTP style with plain
  `application/json` responses (SSE deferred — the shim tolerates both).
  Methods: `initialize` (assigns `Mcp-Session-Id`, captures clientInfo),
  `notifications/initialized`, `ping`, `tools/list`, `tools/call`. Unknown
  method → -32601; bad params → -32602. Port busy (second instance) → status
  state, never a crash.
- **`Core/Mcp/McpTools.cs`**: the same 10 tools, schemas mirroring the
  AgentHost definitions (same names/params — external clients get the Windows
  capability set, no more, no less). Execution maps to the shell's domain
  operations; each mutating call is exactly one undo entry and returns a
  structured receipt (ids, no-op state, warnings) as MCP tool content.
- **Session registry** (`McpSession`): clientInfo, connected-at, last
  activity, rolling log of recent tool calls (name, target, ok/error) — the
  data the connection panel renders.
- **Approval gate**: a new client's `initialize` enters a pending state; the
  panel shows it with Accept/Deny. `tools/list` and `tools/call` are refused
  (`-32001` unauthorized) until accepted. Approved client names persist in
  settings; Deny drops the session. Read-only vs mutating split stays simple:
  the gate covers everything.

## Modes and UI

- `SettingsStore`: `AgentMode` = `inline` (default) | `external`. The left
  "Edit with AI" panel swaps: inline = current chat; external = connection
  panel (server state + port, connected client name/version, session age,
  recent tool calls, Disconnect button, and a copyable config snippet pointing
  at the shim). Switching modes starts/stops the server; app teardown stops it
  before the engine handles die (ordering mirrors the agent shutdown guard).
- Welcome dialog (first install only): a mode choice step — inline with an
  API key vs external MCP client. Skip = inline.
- Settings window: agent mode section (radio + the same connection state
  summary). The mode is switchable at any time, no restart.

## Slices

1. This plan doc + `McpServer`/`McpTools` + tests, server off by default,
   startable via `--mcp` dev flag (no UI). End-to-end proof: a temporary
   script client drives initialize → tools/list → add_clip → get_timeline
   read-back against a real project.
2. Mode switch + connection panel + approval gate + welcome step + settings.
3. Docs: README section for connecting Claude Desktop via the shim.

Out of scope: SSE streaming responses, resources/prompts (macOS's
`palmier://models/*` resources can follow), MCP client (PalmierWin calling
out to other MCP servers), remote access of any kind.

## Tests

JSON-RPC method matrix, schema discovery (10 tools, exact names), argument
validation failures, one-undo-entry-per-mutation, read-back-after-mutation
through the real engine (interop suite), approval state machine, session
registry, real HTTP round-trip on an ephemeral port, port-busy status.
UI verification needs a real run: the connection panel with a script client
connected (see `PalmierWin/.build/uix.ps1` conventions).
