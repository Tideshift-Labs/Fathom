# Proposal: MCP Direct Service Calls (Remove Loopback HTTP Proxy)

## Problem

The MCP handler (`CoRiderMcpServer`) translates `tools/call` requests into loopback
HTTP GET requests back to the same process:

```
MCP client --> POST /mcp --> McpHandler --> CoRiderMcpServer.HandleToolsCall()
  --> WebClient.DownloadString("http://localhost:{port}/classes?search=foo")
  --> HttpListener (same process) --> ClassesHandler --> ClassIndexService
  --> response string flows back through the entire chain
```

Every MCP tool call pays the cost of URL construction, TCP round-trip through
localhost, HTTP parsing, handler routing, and string-based error handling, all
to reach services that live in the same C# process.

## Proposed Change

Wire `CoRiderMcpServer` directly to the service layer, bypassing HTTP entirely.
Each MCP tool maps 1:1 to a service call that already returns typed objects.

```
MCP client --> POST /mcp --> McpHandler --> CoRiderMcpServer.HandleToolsCall()
  --> ClassIndexService.BuildClassIndex(search, baseClass)
  --> serialize result --> JSON-RPC response
```

## Current Service Signatures (all return typed objects)

| Service | Method | Returns |
|---------|--------|---------|
| `FileIndexService` | `BuildFileIndex()` | `Dictionary<string, IPsiSourceFile>` |
| `ClassIndexService` | `BuildClassIndex(search?, baseClass?)` | `List<ClassEntry>` |
| `CodeStructureService` | `DescribeFile(sourceFile, path, debug, diag)` | `FileStructure` |
| `InspectionService` | `RunInspections(workItems)` | `List<FileInspectionResult>` |
| `BlueprintQueryService` | `Query(className, cache, solutionDir, debug)` | `BlueprintQueryResult` |
| `AssetRefProxyService` | `ProxyGetWithStatus(path)` | `(int status, string body)` |
| `BlueprintAuditService` | various | typed objects |
| `UeProjectService` | `GetUeProjectInfo()` | typed object |

## Implementation Plan

### 1. Expand `CoRiderMcpServer` constructor to accept services

Instead of just `int port`, inject the services it needs:

```csharp
public CoRiderMcpServer(
    ISolution solution,
    FileIndexService fileIndex,
    ClassIndexService classIndex,
    CodeStructureService codeStructure,
    InspectionService inspection,
    BlueprintQueryService blueprintQuery,
    BlueprintAuditService blueprintAudit,
    AssetRefProxyService assetRefProxy,
    UeProjectService ueProject,
    ServerConfiguration config)
```

Update the instantiation in `InspectionHttpServer2.StartServer()` (line 209)
to pass these services instead of just `port`.

### 2. Replace `HandleToolsCall` dispatch

Replace the current flow (build URL, HTTP GET, return string) with direct
service method calls. Each tool gets a small dispatch method:

```csharp
private object HandleToolsCall(JsonElement paramsEl)
{
    // ... extract toolName, argsEl (unchanged) ...

    try
    {
        var result = DispatchTool(toolName, argsEl);
        return new { content = new[] { new { type = "text", text = result } } };
    }
    catch (Exception ex)
    {
        return new
        {
            content = new[] { new { type = "text", text = "Error: " + ex.Message } },
            isError = true
        };
    }
}

private string DispatchTool(string toolName, JsonElement args)
{
    switch (toolName)
    {
        case "list_solution_files":
            return HandleListFiles();
        case "list_cpp_classes":
            return HandleListClasses(args);
        case "describe_code":
            return HandleDescribeCode(args);
        case "inspect_code":
            return HandleInspectCode(args);
        // ... etc
        default:
            throw new ArgumentException("Unknown tool: " + toolName);
    }
}
```

### 3. Implement per-tool methods

Each method extracts parameters from `JsonElement args`, calls the service,
and serializes the result. Example for `list_cpp_classes`:

```csharp
private string HandleListClasses(JsonElement args)
{
    var search = GetStringArg(args, "search");
    var baseClass = GetStringArg(args, "base");
    var entries = _classIndex.BuildClassIndex(search, baseClass);
    return Json.Serialize(new { count = entries.Count, classes = entries });
}
```

The response format should match what the HTTP handlers currently produce so
MCP clients see the same data shape.

### 4. Handle multi-file tools (describe_code, inspect_code)

These need the same file-resolution logic that `DescribeCodeHandler` and
`InspectHandler` currently do (resolve relative paths via `FileIndexService`).
Extract that logic into a shared helper or inline it in the MCP dispatch methods.

### 5. Handle proxy-based tools (asset refs, blueprint audit)

Tools like `get_asset_dependencies` and `get_asset_referencers` currently proxy
through `AssetRefProxyService.ProxyGetWithStatus()` which itself makes HTTP
calls to the UE editor companion plugin. These should still call the proxy
service directly (not loop back through our own HTTP server).

### 6. Remove dead code

- Delete `BuildInternalUrl()` method
- Delete `InternalHttpGet()` method
- Remove the `_port` field from `CoRiderMcpServer` (no longer needed)
- Remove the `Endpoint` field from `ToolDef` (tool definitions still needed
  for `tools/list` but no longer need an endpoint path)

### 7. Update `ToolDef`

The `ToolDef` class no longer needs an `Endpoint` field since tools are
dispatched by name, not by URL path. The endpoint string is only used by
`BuildInternalUrl` which gets deleted.

```csharp
private class ToolDef
{
    public readonly string Name;
    public readonly string Description;
    public readonly ToolParam[] Params;
    // Endpoint removed
}
```

## Files Changed

| File | Change |
|------|--------|
| `Mcp/CoRiderMcpServer.cs` | Major rewrite: inject services, direct dispatch, remove HTTP proxy code |
| `InspectionHttpServer2.cs` | Update `CoRiderMcpServer` instantiation (line ~209) |

No changes to handlers, services, or `McpHandler.cs` (it still receives
JSON-RPC and delegates to `CoRiderMcpServer`).

## Benefits

- Eliminates per-request TCP round-trip overhead
- Type-safe service calls instead of string URL construction
- Proper exception propagation instead of `"HTTP 409: ..."` string parsing
- Removes dependency on deprecated `WebClient`
- Simpler debugging (no self-referential HTTP traffic in logs/traces)

## Risks / Considerations

- **Response format parity**: The HTTP handlers sometimes do post-processing
  (e.g. `BlueprintsHandler` populates URL fields using the server port,
  handlers choose JSON vs Markdown based on `Accept` header). The MCP path
  should always return JSON, but the shape must match. Verify each tool's
  output against the current HTTP response.
- **File resolution logic duplication**: `InspectHandler` and
  `DescribeCodeHandler` both resolve relative file paths using
  `FileIndexService`. This logic either needs to be extracted into a shared
  helper or duplicated in the MCP dispatch. A shared helper is preferred.
- **Testing**: Each tool should be tested via MCP `tools/call` after the
  change to confirm output parity. A `curl`-based smoke test against
  `/mcp` would suffice.

## Verification

```bash
# Initialize MCP session
curl -X POST http://localhost:12015/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'

# Call a tool directly
curl -X POST http://localhost:12015/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_cpp_classes","arguments":{"search":"Character"}}}'
```

Compare output before and after the refactor to confirm parity.
