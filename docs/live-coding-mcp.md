# Live Coding MCP Tools

## Problem

External tools (Claude Code, CLI agents, MCP clients) cannot trigger Live Coding builds from outside the Unreal Editor. Live Coding is an in-process API (`ILiveCodingModule::Compile()`) that must be called from within the running editor. There is no CLI or socket interface. An agent that edits C++ source files has no way to hot-reload those changes into the running game without the developer manually pressing Ctrl+Alt+F11 in the editor.

## Solution

Two new MCP tools (`live_coding_compile` and `live_coding_status`) that route through Fathom's existing Rider-to-UE HTTP proxy to call the Live Coding API from within the editor process. The compile tool captures `LogLiveCoding` output and returns it alongside the compile result.

## Architecture

```
MCP client (Claude Code, CLI, etc.)
  |
  | JSON-RPC tool call
  v
FathomMcpServer (Rider plugin, port 19876)
  |
  | Internal HTTP GET to self
  v
LiveCodingHandler (Rider plugin)
  |
  | HTTP GET via AssetRefProxyService
  v
FFathomHttpServer (UE editor plugin, ports 19900-19910)
  |
  | ILiveCodingModule API (in-process)
  v
LiveCodingConsole.exe (IPC, launched by UE)
```

### Request flow for `/live-coding/compile`

1. **MCP client** calls `live_coding_compile` tool (timeout: 130s)
2. **FathomMcpServer** maps the tool to `GET /live-coding/compile` on the Rider HTTP server
3. **LiveCodingHandler** (Rider) validates the UE project and editor connection, then proxies `GET /live-coding/compile` to the UE editor server
4. **FFathomHttpServer** (UE) validates Live Coding state, dispatches `ILiveCodingModule::Compile(WaitForCompletion)` to a background thread, and returns `{"result":"CompileStarted"}` immediately
5. **LiveCodingHandler** (Rider) receives the immediate response, then enters a polling loop: every 1 second, it calls `GET /live-coding/status` on UE and checks for a `lastCompile` object in the response
6. When the compile finishes (or after 120s timeout), Rider returns the final result to the MCP client

### Why async dispatch is required

UE's `FHttpServerModule` extends `FTSTickerObjectBase`. All HTTP request handlers execute on the game thread via `Tick()`. If `Compile(WaitForCompletion)` ran synchronously in the handler, it would block the entire editor (UI, tick, other HTTP requests) for the duration of the compile (typically 2-30+ seconds). The async pattern avoids this:

- The `/live-coding/compile` handler dispatches to `Async(EAsyncExecution::ThreadPool, ...)` and returns immediately
- The background thread calls `Compile(WaitForCompletion)`, which communicates with `LiveCodingConsole.exe` via IPC
- Shared state (`GCompileStateLock`) tracks the in-flight compile and stores the result
- The `/live-coding/status` handler reads the shared state (quick lock, no blocking) and returns it

### Timeout cascade

```
MCP tool timeout:     130s (FathomMcpServer ToolDef.TimeoutMs)
Rider poll loop:      120s (LiveCodingHandler.CompileTimeoutSeconds)
Individual UE calls:  ~10s each (default HttpWebRequest timeout)
```

The MCP timeout exceeds the Rider poll timeout, which in turn exceeds any individual UE HTTP call. This ensures clean error propagation: Rider returns a 504 timeout before MCP times out, and individual transient HTTP failures within the poll loop are retried silently.

## UE-Side Implementation

### Files

| File | Role |
|------|------|
| `FathomHttpServerLiveCoding.cpp` | Handler implementations + compile state |
| `FathomHttpServer.cpp` | Route registration in `TryBind()` |
| `FathomHttpServer.h` | Handler declarations |
| `FathomUELink.Build.cs` | `LiveCoding` module dependency (Win64 only) |

### Compile state (file-scoped globals)

All protected by `GCompileStateLock` (`FCriticalSection`):

| Variable | Type | Purpose |
|----------|------|---------|
| `GCompileInFlight` | `bool` | True while a compile is running on the background thread |
| `GActiveLogCapture` | `TUniquePtr<FLiveCodingLogCapture>` | Captures `LogLiveCoding` lines during compile |
| `GCompileStartTime` | `double` | `FPlatformTime::Seconds()` at compile start |
| `GHasLastCompileResult` | `bool` | True after any compile has completed |
| `GLastCompileResult` | `FString` | Result enum as string (Success, Failure, NoChanges, Cancelled) |
| `GLastCompileResultText` | `FString` | Human-readable description |
| `GLastCompileLogs` | `TArray<FString>` | Captured log lines from the compile |
| `GLastCompileDurationMs` | `int32` | Compile wall-clock duration |

### Log capture

`FLiveCodingLogCapture` is an `FOutputDevice` subclass registered with `GLog` during a compile. It captures lines where `Category == "LogLiveCoding"` into a thread-safe buffer (`FCriticalSection` + `TArray<FString>`). The background thread unregisters it and collects the lines after `Compile()` returns.

### Platform guard

All Live Coding code is wrapped in `#if PLATFORM_WINDOWS ... #endif`. On non-Windows platforms, both endpoints return HTTP 501 "Live Coding is only available on Windows". The `LiveCoding` module dependency in `Build.cs` is also guarded by `Target.Platform == UnrealTargetPlatform.Win64`.

## Rider-Side Implementation

### Files

| File | Role |
|------|------|
| `Handlers/LiveCodingHandler.cs` | HTTP handler (compile polling + status proxy) |
| `InspectionHttpServer2.cs` | Handler registration |
| `Mcp/FathomMcpServer.cs` | MCP tool definitions + per-tool timeout |

### LiveCodingHandler

Implements `IRequestHandler`. Routes:

- **`/live-coding/compile`**: Triggers compile via proxy, then polls status until `lastCompile` appears
- **`/live-coding/status`**: Simple proxy pass-through to UE

The handler uses `AssetRefProxyService` for UE server discovery (reads `Saved/Fathom/.fathom-ue-server.json` marker file) and HTTP proxying.

### MCP tool definitions

| Tool | Description | Timeout |
|------|-------------|---------|
| `live_coding_compile` | Trigger a Live Coding compile, returns result + build log | 130s |
| `live_coding_status` | Check Live Coding availability and compile state | default (10s) |

### Per-tool timeout support

`ToolDef` gained a `TimeoutMs` field. `FathomMcpServer.InternalHttpGet` uses `HttpWebRequest` with per-request timeout (replacing the previous `WebClient` which only supported a global timeout). The compile tool uses 130s; all other tools use the default 10s.

## Endpoint Contracts

### `GET /live-coding/compile` (UE)

Returns immediately after dispatching the compile.

**Success (compile dispatched):**
```json
{
  "result": "CompileStarted",
  "resultText": "Live Coding compile initiated. Poll /live-coding/status for results."
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
  "isCompiling": false,
  "compileInFlight": false,
  "lastCompile": {
    "result": "Success",
    "resultText": "Live coding succeeded",
    "logs": [
      "Starting Live Coding compile.",
      "Running link.exe @...",
      "Live coding succeeded"
    ],
    "durationMs": 2340
  }
}
```

The `lastCompile` object is only present after a compile has completed. The `compileInFlight` bool indicates whether a background compile is currently running. `isCompiling` comes from `ILiveCodingModule::IsCompiling()` and may differ slightly in timing.

### Rider compile endpoint (proxied)

The Rider `/live-coding/compile` endpoint blocks until the compile finishes (via polling). It returns the final `lastCompile` object directly:

**JSON format:**
```json
{
  "result": "Success",
  "resultText": "Live coding succeeded",
  "logs": ["..."],
  "durationMs": 2340
}
```

**Markdown format (`?format=md`):**
```
# Live Coding Compile

**Result:** Success
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
| Editor disconnects mid-compile | Rider poll detects proxy failure, returns 502 |
| Compile exceeds 120s | Rider returns 504 timeout (compile may still finish in editor) |
| No code changes | UE returns `NoChanges` result |
| Non-Windows platform | UE returns 501 "only available on Windows" |
| Non-UE project | Rider returns 404 before attempting proxy |

## Verification

```bash
# UE endpoints (direct, ports 19900-19910)
curl http://localhost:19900/live-coding/status
curl http://localhost:19900/live-coding/compile

# Rider proxy endpoints (port 19876)
curl http://localhost:19876/live-coding/status
curl http://localhost:19876/live-coding/compile          # blocks until done
curl "http://localhost:19876/live-coding/compile?format=md"

# MCP tools (via JSON-RPC or MCP client)
# live_coding_status
# live_coding_compile
```
