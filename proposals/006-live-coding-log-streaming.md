# Proposal: Live Coding Build Log Streaming

**Status:** Proposed
**Created:** 2026-02-18
**Depends on:** Live Coding MCP tools (see `docs/live-coding-mcp.md`)

## Problem

The current Live Coding MCP integration returns all build log output at once when the compile finishes. For compiles that take 10-30+ seconds, the MCP client (and the developer watching it) sees nothing until the entire operation completes. There is no incremental feedback.

This matters because:
- Long compiles with no output feel broken. Developers and agents cannot tell if progress is being made.
- Build errors appear only at the end. An agent could start reasoning about the error sooner if it saw the failing line as it was emitted.
- The existing async architecture already captures logs incrementally (via `FLiveCodingLogCapture` on the UE side), but the current polling loop only checks for the final `lastCompile` result. The in-flight log lines are available in memory but not exposed.

## Goal

Stream `LogLiveCoding` lines to the MCP client in real-time as the compile progresses, so the client sees incremental build output instead of waiting for the final result.

## How the Current Architecture Enables This

The async refactor (documented in `docs/live-coding-mcp.md`) already separates compile dispatch from result retrieval:

1. `GET /live-coding/compile` (UE) dispatches to a background thread and returns immediately
2. `GActiveLogCapture` accumulates log lines in real-time on the background thread
3. `GET /live-coding/status` (UE) reads the shared state under a brief lock
4. `LiveCodingHandler` (Rider) polls status every 1 second

The log lines exist in `GActiveLogCapture` while the compile is running. They just are not exposed yet. Adding a cursor-based log endpoint and streaming them to the MCP client requires changes at three layers.

## Design

### Layer 1: UE log cursor endpoint

Add a new field to the `/live-coding/status` response (or a separate endpoint) that exposes in-flight log lines:

**Option A: Extend `/live-coding/status` (preferred, simpler)**

```json
{
  "hasStarted": true,
  "isCompiling": true,
  "compileInFlight": true,
  "inFlightLogs": {
    "lines": [
      "Starting Live Coding compile.",
      "UbaCli v5.8.0 ..."
    ],
    "cursor": 2
  }
}
```

The `cursor` is the current line count. The caller passes `?logCursor=N` to get only lines after position N, avoiding re-sending the entire log on each poll.

**Implementation:** `GActiveLogCapture` already has thread-safe access. Add a `GetCapturedLinesSince(int32 Cursor)` method that returns a slice of the buffer. The status handler reads this under the existing `GCompileStateLock`.

**Option B: Separate endpoint `GET /live-coding/logs?cursor=N`**

Cleaner separation but adds another route. Use this if the status response becomes too large.

### Layer 2: Rider-side incremental forwarding

`LiveCodingHandler` already polls `/live-coding/status` every second. During the poll loop, it would:

1. Include `?logCursor=N` in each status request
2. Accumulate new lines into a local buffer
3. Forward new lines to the MCP client via the streaming mechanism (see Layer 3)
4. Update the cursor for the next poll

No architectural change to the polling loop, just reading an additional field from each status response.

### Layer 3: MCP streaming

The MCP specification supports real-time progress reporting through `notifications/message` (for SSE/Streamable HTTP transport). The flow:

```
MCP Client                    FathomMcpServer              LiveCodingHandler
    |                              |                              |
    |-- tools/call compile ------->|                              |
    |                              |-- GET /live-coding/compile ->|
    |                              |                              |-- trigger UE compile
    |                              |                              |
    |<- notifications/message -----|<- log line 1 ---------------|  (poll 1)
    |<- notifications/message -----|<- log line 2, 3 ------------|  (poll 2)
    |<- notifications/message -----|<- log line 4 ---------------|  (poll 3)
    |                              |                              |
    |<- tools/call result ---------|<- final result + all logs ---|  (compile done)
```

**MCP transport requirements:**
- **Streamable HTTP (SSE):** Supports `notifications/message` natively. The server sends SSE events during the tool call before sending the final result.
- **stdio:** Does not support interleaved notifications during a tool call in the current MCP spec. Streaming would only work over SSE transport.

**FathomMcpServer changes:**
- `HandleToolsCall` would need a streaming response mode for long-running tools
- Instead of returning a single JSON-RPC response, it would write SSE events for progress notifications, then the final result
- The `McpHandler` (or a new `McpStreamingHandler`) would need to keep the HTTP response open and write incremental events

### MCP notification format

```json
{
  "jsonrpc": "2.0",
  "method": "notifications/message",
  "params": {
    "level": "info",
    "logger": "live-coding",
    "data": {
      "line": "Running link.exe @response.rsp",
      "cursor": 5
    }
  }
}
```

## Implementation Plan

### Phase 1: UE log cursor

1. Add `GetCapturedLinesSince(int32 Cursor)` to `FLiveCodingLogCapture`
2. Add `logCursor` query parameter to `/live-coding/status`
3. Include `inFlightLogs` in the status response when `GCompileInFlight` is true

This is useful on its own: any HTTP client can poll status with a cursor and get incremental logs without MCP streaming.

### Phase 2: Rider-side accumulation

1. Update `LiveCodingHandler` poll loop to pass `logCursor` and collect new lines
2. Include accumulated logs in the final compile response (already done for the `lastCompile.logs` field)
3. Optionally: expose a Rider-side `/live-coding/logs?cursor=N` endpoint that mirrors the accumulation for non-MCP HTTP clients

### Phase 3: MCP streaming

1. Add SSE response support to `McpHandler` or `FathomMcpServer`
2. Mark `live_coding_compile` as a streaming-capable tool
3. During the poll loop, emit `notifications/message` for each new log line
4. Send the final `tools/call` result when the compile completes
5. Fall back to current behavior (batch response) for non-SSE transports

### Phase 4: Client integration

1. Claude Code and other MCP clients that support `notifications/message` will display log lines as they arrive
2. Clients that do not support streaming will still receive the full result at the end (no degradation)

## Risks

### MCP streaming support

The MCP Streamable HTTP transport supports SSE and `notifications/message`, but client-side handling of interleaved notifications during a tool call varies. Claude Code support for this pattern should be verified before investing in Phase 3.

### Log volume

Some compiles produce hundreds of log lines. The cursor-based approach avoids re-sending old lines, but the MCP client must handle rapid notification bursts. A rate limit (e.g., batch lines per second) may be needed.

### Game thread contention

The `/live-coding/status` handler runs on the game thread and acquires `GCompileStateLock`. If the background compile thread is writing frequently, the lock could add micro-stalls to the game thread tick. In practice, log writes are infrequent (one per significant compile step), so this should not be an issue.

## Verification

### Phase 1 (UE log cursor)
```bash
# Start a compile
curl http://localhost:19900/live-coding/compile

# Poll with cursor (immediate, while compiling)
curl "http://localhost:19900/live-coding/status?logCursor=0"
# Returns inFlightLogs with lines and new cursor

curl "http://localhost:19900/live-coding/status?logCursor=3"
# Returns only lines after position 3
```

### Phase 2 (Rider accumulation)
```bash
# Rider compile endpoint still blocks, but now internal polling collects logs incrementally
curl "http://localhost:19876/live-coding/compile?format=md"
# Same final output, but Rider has the logs available for streaming
```

### Phase 3 (MCP streaming)
```
# MCP client calls live_coding_compile
# Client receives notifications/message events with log lines during compile
# Client receives final tool result with complete log + result
```
