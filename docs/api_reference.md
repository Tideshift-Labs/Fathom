# CoRider API Reference

CoRider exposes a local HTTP API on port `19876` when a solution is open in Rider.

Most endpoints support both Markdown (default) and JSON output. Append `&format=json` for JSON.

## Source Code

### GET `/files`

List all source files in the solution.

**Response format:** JSON only

**Response:**
```json
{
  "solution": "path/to/solution",
  "fileCount": 42,
  "files": [
    {"path": "Source/File.cpp", "ext": "cpp", "language": "C++"}
  ]
}
```

### GET `/classes`

List game C++ classes with header/source file paths and base class.

| Param | Required | Description |
|-------|----------|-------------|
| `search` | No | Name substring filter |
| `base` | No | Filter by base class (e.g. `ACharacter`) |
| `format` | No | `json` for JSON output |

### GET `/describe_code`

Structural description of source file(s): functions, classes, members, etc.

| Param | Required | Description |
|-------|----------|-------------|
| `file` | Yes | File path (repeatable: `&file=a&file=b`) |
| `format` | No | `json` for JSON output |
| `debug` | No | `true` for diagnostics |

### GET `/inspect`

Run ReSharper code inspection on file(s). Returns issues with severity, location, and description.

| Param | Required | Description |
|-------|----------|-------------|
| `file` | Yes | File path (repeatable: `&file=a&file=b`) |
| `format` | No | `json` for JSON output |
| `debug` | No | `true` for diagnostics |

## Unreal Engine

These endpoints provide Blueprint and asset intelligence. Some require a live UE editor with the CoRider-UnrealEngine plugin.

### GET `/blueprints`

List Blueprint classes deriving from a C++ class.

| Param | Required | Description |
|-------|----------|-------------|
| `class` | Yes | C++ class name (e.g. `ACharacter`) |
| `format` | No | `json` for JSON output |
| `debug` | No | `true` for diagnostics |

**Status codes:** 501 if UE features are unavailable.

### GET `/bp`

Blueprint composite view: audit data + dependencies + referencers in one call.

| Param | Required | Description |
|-------|----------|-------------|
| `file` | Yes | Blueprint package path (repeatable: `&file=a&file=b`) |
| `format` | No | `json` for JSON output |

Audit data works offline. Dependencies and referencers require a live UE editor.

### GET `/blueprint-audit`

Get cached Blueprint audit data.

| Param | Required | Description |
|-------|----------|-------------|
| `format` | No | `json` for JSON output |

**Status codes:**
| Code | Meaning |
|------|---------|
| 200 | Fresh data available |
| 409 | Data is stale (asset changed since last audit) |
| 501 | Commandlet not found |
| 503 | Not ready (audit still building) |

### GET `/blueprint-audit/refresh`

Trigger a background refresh of Blueprint audit data.

| Param | Required | Description |
|-------|----------|-------------|
| `format` | No | `json` for JSON output |

**Status codes:**
| Code | Meaning |
|------|---------|
| 202 | Refresh started or already in progress |
| 404 | Not a UE project |
| 500 | Commandlet path error |

### GET `/blueprint-audit/status`

Check the status of a Blueprint audit refresh.

| Param | Required | Description |
|-------|----------|-------------|
| `format` | No | `json` for JSON output |

### GET `/uassets`

Search or browse UAssets. Provide a `search` term and/or filters (`class`, `pathPrefix`). At least one must be provided. Search uses plain name substrings (space-separated, all must match). Wildcards and regex are not supported.

| Param | Required | Description |
|-------|----------|-------------|
| `search` | No* | Plain name substrings (space-separated, all must match; no wildcards/regex) |
| `class` | No* | Filter by asset class (e.g. `WidgetBlueprint`) |
| `pathPrefix` | No | Path prefix filter (default: `/Game`; use `&pathPrefix=` for all) |
| `limit` | No | Max results |
| `format` | No | `json` for JSON output |

\* At least `search` or one filter (`class`, `pathPrefix`) is required.

Requires a live UE editor. Returns 503 if the editor is not running.

**Scoring:** exact name match > name prefix > name substring > path-only match. Final score is the minimum across all tokens.

**Examples:**
- `/uassets?search=player` - find assets with "player" in the name
- `/uassets?class=WidgetBlueprint&pathPrefix=/Game/UI` - list all widget blueprints under /Game/UI
- `/uassets?search=main menu&limit=10` - assets matching both "main" and "menu"

### GET `/uassets/show`

Asset detail: registry metadata, disk size, tags, dependency/referencer counts.

| Param | Required | Description |
|-------|----------|-------------|
| `package` | Yes | Full package path (repeatable: `&package=a&package=b`) |
| `format` | No | `json` for JSON output |

Requires a live UE editor.

### GET `/asset-refs/dependencies`

List what a given asset depends on.

| Param | Required | Description |
|-------|----------|-------------|
| `asset` | Yes | Asset package path (e.g. `/Game/UI/WBP_MainMenu`) |
| `format` | No | `json` for JSON output |

Requires a live UE editor.

### GET `/asset-refs/referencers`

List what depends on a given asset.

| Param | Required | Description |
|-------|----------|-------------|
| `asset` | Yes | Asset package path (e.g. `/Game/UI/WBP_MainMenu`) |
| `format` | No | `json` for JSON output |

Requires a live UE editor.

### GET `/asset-refs/status`

Check the connection status to the UE editor's asset reference server.

| Param | Required | Description |
|-------|----------|-------------|
| `format` | No | `json` for JSON output |

**Response:**
```json
{
  "connected": true,
  "port": 19900,
  "pid": 9999,
  "message": "..."
}
```

## Diagnostics

### GET `/health`

Server and solution health check.

**Response format:** JSON only

**Response:**
```json
{
  "status": "ok",
  "solution": "path/to/solution",
  "port": 19876
}
```

### GET `/ue-project`

Diagnostic info for UE project detection and engine path discovery.

**Response format:** Markdown only

### GET `/debug-psi-tree`

Raw PSI tree dump for a source file. Useful for debugging ReSharper analysis.

| Param | Required | Description |
|-------|----------|-------------|
| `file` | Yes | File path |
| `maxdepth` | No | Max tree depth (default: 8) |
| `maxtext` | No | Max text length per node (default: 100) |

**Response format:** Markdown only

## MCP (Model Context Protocol)

### POST `/mcp`

MCP Streamable HTTP endpoint. Accepts JSON-RPC 2.0 requests and exposes all CoRider functionality as MCP tools.

**Content-Type:** `application/json`

**Supported methods:**

| Method | Description |
|--------|-------------|
| `initialize` | Handshake, returns server capabilities |
| `tools/list` | Returns all 15 available tools with input schemas |
| `tools/call` | Execute a tool by name with arguments |
| `ping` | Liveness check |

**Available tools:** `list_solution_files`, `list_cpp_classes`, `describe_code`, `inspect_code`, `find_derived_blueprints`, `get_blueprint_info`, `get_blueprint_audit`, `refresh_blueprint_audit`, `get_audit_status`, `search_assets`, `show_asset`, `get_asset_dependencies`, `get_asset_referencers`, `get_ue_project_info`, `get_editor_status`.

**Example: initialize**
```bash
curl -X POST http://localhost:19876/mcp -H "Content-Type: application/json" -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"clientInfo\":{\"name\":\"test\",\"version\":\"1.0\"}}}"
```

**Example: list tools**
```bash
curl -X POST http://localhost:19876/mcp -H "Content-Type: application/json" -d "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}"
```

**Example: call a tool**
```bash
curl -X POST http://localhost:19876/mcp -H "Content-Type: application/json" -d "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"list_solution_files\",\"arguments\":{}}}"
```

**AI client configuration (Streamable HTTP):**
```json
{
  "mcpServers": {
    "corider": {
      "url": "http://localhost:19876/mcp"
    }
  }
}
```

## Common Conventions

**Format negotiation:** Most endpoints default to Markdown. Append `&format=json` for JSON. Exceptions: `/files` and `/health` are JSON-only; `/ue-project` and `/debug-psi-tree` are Markdown-only.

**Repeatable parameters:** Some parameters accept multiple values (e.g. `&file=a&file=b`).

**Status codes:** 400 for missing required parameters, 404 for file not found, 502 for UE editor connection lost, 503 for UE editor not running.
