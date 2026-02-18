# Live Coding MCP Tools

## Problem

External tools (Claude Code, CLI agents, MCP clients) cannot trigger Live Coding builds from outside the Unreal Editor. Live Coding is an in-process API (`ILiveCodingModule::Compile()`) that must be called from within the running editor. There is no CLI or socket interface. An agent that edits C++ source files has no way to hot-reload those changes into the running game without the developer manually pressing Ctrl+Alt+F11 in the editor.

## Solution

Two new MCP tools (`live_coding_compile` and `live_coding_status`) that route through Fathom's existing Rider-to-UE HTTP proxy to call the Live Coding API from within the editor process. The compile tool captures `LogLiveCoding` output and returns it alongside the compile result.

## Architecture

```
MCP client (Claude Code, CLI, etc.)
  |
  | JSON-RPC tool call (timeout: 130s)
  v
FathomMcpServer (Rider plugin, port 19876)
  |
  | Internal HTTP GET to self
  v
LiveCodingHandler (Rider plugin)
  |
  | HTTP GET via AssetRefProxyService (timeout: 130s)
  v
FFathomHttpServer (UE editor plugin, ports 19900-19910)
  |
  | ILiveCodingModule::Compile(WaitForCompletion) on game thread
  v
LiveCodingConsole.exe (IPC, launched by UE)
```

### Request flow for `/live-coding/compile`

1. **MCP client** calls `live_coding_compile` tool (timeout: 130s)
2. **FathomMcpServer** maps the tool to `GET /live-coding/compile` on the Rider HTTP server
3. **LiveCodingHandler** (Rider) validates the UE project and editor connection, then proxies `GET /live-coding/compile` to the UE editor server using a dedicated `HttpClient` with 130s timeout
4. **FFathomHttpServer** (UE) validates Live Coding state, sets up log capture, and calls `Compile(WaitForCompletion)` synchronously on the game thread
5. The editor freezes during compile (same behavior as pressing Ctrl+Alt+F11)
6. When compile finishes, UE returns the result with build logs in the HTTP response
7. Rider forwards the response to the MCP client

### Why synchronous compile on the game thread

`ILiveCodingModule::Compile()` internally performs operations that require the game thread (e.g. texture compilation setup). Calling it from a background thread triggers `IsInGameThread()` assertions and crashes. UE's `FHttpServerModule` extends `FTSTickerObjectBase`, so all HTTP handlers already run on the game thread via `Tick()`.

This matches normal Live Coding behavior: when a developer presses Ctrl+Alt+F11 in the editor, the compile runs synchronously on the game thread and the editor freezes until it finishes. Our HTTP handler does the same thing.

### Timeout cascade

```
MCP tool timeout:          130s (FathomMcpServer ToolDef.TimeoutMs)
Rider compile HttpClient:  130s (LiveCodingHandler._compileClient)
UE compile:                blocks for compile duration (typically 2-30s)
```

Both the MCP and Rider timeouts are set to 130s, which exceeds any realistic compile duration. If a compile takes longer than 130s, the HTTP request will time out and the client receives an error, but the compile continues in the editor.

## UE-Side Implementation

### Files

| File | Role |
|------|------|
| `FathomHttpServerLiveCoding.cpp` | Handler implementations + log capture |
| `FathomHttpServer.cpp` | Route registration in `TryBind()` |
| `FathomHttpServer.h` | Handler declarations |
| `FathomUELink.Build.cs` | `LiveCoding` module dependency (Win64 only) |

### Log capture

`FLiveCodingLogCapture` is an `FOutputDevice` subclass registered with `GLog` before the compile call and unregistered after. It captures lines where `Category == "LogLiveCoding"` into a thread-safe buffer (`FCriticalSection` + `TArray<FString>`). The log capture is stack-allocated in the handler and lives for exactly the duration of the compile call.

### Platform guard

All Live Coding code is wrapped in `#if PLATFORM_WINDOWS ... #endif`. On non-Windows platforms, both endpoints return HTTP 501 "Live Coding is only available on Windows". The `LiveCoding` module dependency in `Build.cs` is also guarded by `Target.Platform == UnrealTargetPlatform.Win64`.

## Rider-Side Implementation

### Files

| File | Role |
|------|------|
| `Handlers/LiveCodingHandler.cs` | HTTP handler (compile proxy + status proxy) |
| `InspectionHttpServer2.cs` | Handler registration |
| `Mcp/FathomMcpServer.cs` | MCP tool definitions + per-tool timeout |

### LiveCodingHandler

Implements `IRequestHandler`. Routes:

- **`/live-coding/compile`**: Single blocking proxy call to UE using a dedicated `HttpClient` with 130s timeout. The UE side compiles synchronously and returns the result.
- **`/live-coding/status`**: Simple proxy pass-through to UE.

The handler uses `AssetRefProxyService` for UE server discovery (reads `Saved/Fathom/.fathom-ue-server.json` marker file) and HTTP proxying. The `ProxyGetWithStatus` overload accepts a custom `HttpClient` for long-running requests.

### MCP tool definitions

| Tool | Description | Timeout |
|------|-------------|---------|
| `live_coding_compile` | Trigger a Live Coding compile, returns result + build log | 130s |
| `live_coding_status` | Check Live Coding availability and compile state | default (10s) |

### Per-tool timeout support

`ToolDef` has a `TimeoutMs` field. `FathomMcpServer.InternalHttpGet` uses `HttpWebRequest` with per-request timeout. The compile tool uses 130s; all other tools use the default 10s.

## Endpoint Contracts

### `GET /live-coding/compile` (UE)

Blocks for the duration of the compile, then returns the result.

**Success:**
```json
{
  "result": "Success",
  "resultText": "Live coding succeeded",
  "durationMs": 2340,
  "logs": [
    "Starting Live Coding compile.",
    "Running link.exe @...",
    "Live coding succeeded"
  ]
}
```

**Error (Live Coding not started):**
```json
{
  "result": "NotStarted",
  "resultText": "Live Coding has not been started. Enable Live Coding in the editor and ensure it has started."
}
```

**Error (already compiling):**
```json
{
  "result": "AlreadyCompiling",
  "resultText": "A Live Coding compile is already in progress."
}
```

### `GET /live-coding/status` (UE)

**Response:**
```json
{
  "hasStarted": true,
  "isEnabledForSession": true,
  "isCompiling": false
}
```

### Rider compile endpoint (proxied)

The Rider `/live-coding/compile` endpoint proxies the request to UE and blocks until the compile finishes. The response is the same JSON as the UE endpoint.

**Markdown format (`?format=md`):**
```
# Live Coding Compile

**Result:** Success
**Details:** Live coding succeeded
**Duration:** 2.3s

## Build Log
```
Starting Live Coding compile.
Running link.exe @...
Live coding succeeded
```
```

## Edge Cases

| Scenario | Behavior |
|----------|----------|
| UE editor not running | Rider returns 503 with hint to start editor |
| Live Coding not started | UE returns `NotStarted`, Rider passes through |
| Compile already in progress | UE returns `AlreadyCompiling`, Rider passes through |
| Editor disconnects mid-compile | Rider proxy call fails, returns 502 |
| Compile exceeds 130s | HTTP timeout on Rider side, compile continues in editor |
| No code changes | UE returns `NoChanges` result |
| Non-Windows platform | UE returns 501 "only available on Windows" |
| Non-UE project | Rider returns 404 before attempting proxy |

## Verification

```bash
# UE endpoints (direct, ports 19900-19910)
curl http://localhost:19900/live-coding/status
curl http://localhost:19900/live-coding/compile    # blocks during compile

# Rider proxy endpoints (port 19876)
curl http://localhost:19876/live-coding/status
curl http://localhost:19876/live-coding/compile          # blocks during compile
curl "http://localhost:19876/live-coding/compile?format=md"

# MCP tools (via JSON-RPC or MCP client)
# live_coding_status
# live_coding_compile
```
