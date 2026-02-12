using System.Collections.Generic;
using System.Net;
using JetBrains.ProjectModel;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Serialization;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

public class IndexHandler : IRequestHandler
{
    private readonly ISolution _solution;
    private readonly ServerConfiguration _config;
    private readonly UeProjectService _ueProject;

    public IndexHandler(ISolution solution, ServerConfiguration config, UeProjectService ueProject)
    {
        _solution = solution;
        _config = config;
        _ueProject = ueProject;
    }

    public bool CanHandle(string path) => path == "" || path == "/" || path == "/health";

    public void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";

        if (path == "/health")
        {
            var healthJson = Json.Serialize(new
            {
                status = "ok",
                solution = _solution.SolutionDirectory.FullPath.Replace('\\', '/'),
                port = _config.Port
            });
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", healthJson);
            return;
        }

        var format = HttpHelpers.GetFormat(ctx);
        var isUe = _ueProject.IsUnrealProject();

        if (format == "json")
        {
            var indexJson = Json.Serialize(new
            {
                endpoints = new Dictionary<string, string>
                {
                    // Source code
                    ["GET /files"] = "List all source files in the solution (JSON only). For C++ class discovery, prefer /classes.",
                    ["GET /classes"] = "List game C++ classes with header/source pairs and base class. Optional: &search=term (name substring), &base=ACharacter (filter by base class). Default markdown; &format=json for JSON.",
                    ["GET /describe_code?file=path"] = "Structural description of source file(s). Multiple: &file=a&file=b. Default markdown; &format=json for JSON. &debug=true for diagnostics.",
                    ["GET /inspect?file=path"] = "Run code inspection on file(s). Multiple: &file=a&file=b. Default markdown; &format=json for JSON. &debug=true for diagnostics.",
                    // Blueprints & Assets (UE5)
                    ["GET /blueprints?class=ClassName"] = "[UE5] List Blueprint classes deriving from a C++ class. &format=json for JSON. &debug=true for diagnostics.",
                    ["GET /bp?file=/Game/Path"] = "[UE5] Blueprint composite info (audit + dependencies + referencers). Multiple: &file=a&file=b. &format=json for JSON. Requires live UE editor for dependency data.",
                    ["GET /blueprint-audit"] = "[UE5] Get Blueprint audit data. Returns 409 if stale, 503 if not ready. &format=json for JSON.",
                    ["GET /blueprint-audit/refresh"] = "[UE5] Trigger background refresh of Blueprint audit data.",
                    ["GET /blueprint-audit/status"] = "[UE5] Check status of Blueprint audit refresh.",
                    ["GET /uassets?search=term"] = "[UE5] Search or browse UAssets. search is plain substrings (space-separated, all must match; no wildcards). search and/or filters (class, pathPrefix) required. Optional: &class=WidgetBlueprint, &pathPrefix=/Game (default; &pathPrefix= for all), &limit=50. Requires live UE editor.",
                    ["GET /uassets/show?package=/Game/Path"] = "[UE5] Asset detail: registry metadata, disk size, tags, dependency/referencer counts. Multiple: &package=/Game/A&package=/Game/B. Requires live UE editor.",
                    ["GET /asset-refs/dependencies?asset=/Game/Path"] = "[UE5] Asset dependencies. Requires live UE editor.",
                    ["GET /asset-refs/referencers?asset=/Game/Path"] = "[UE5] Asset referencers. Requires live UE editor.",
                    // Diagnostics
                    ["GET /health"] = "Server and solution status (JSON).",
                    ["GET /ue-project"] = "Diagnostic: UE project detection info and engine path.",
                    ["GET /asset-refs/status"] = "[UE5] UE editor connection status.",
                    ["GET /debug-psi-tree?file=path"] = "Diagnostic: raw PSI tree dump for a source file.",
                    // MCP
                    ["POST /mcp"] = "MCP Streamable HTTP endpoint (JSON-RPC). Supports initialize, tools/list, tools/call. 15 tools available."
                },
                isUnrealProject = isUe
            });
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", indexJson);
            return;
        }

        // Default: markdown
        var md = new System.Text.StringBuilder();
        md.AppendLine("# Fathom API");
        md.AppendLine();
        md.AppendLine($"Unreal Engine project: {(isUe ? "yes" : "no")}");
        md.AppendLine();
        md.AppendLine("All endpoints default to markdown output. Add `&format=json` for JSON.");
        md.AppendLine();

        md.AppendLine("## Source Code");
        md.AppendLine();
        md.AppendLine("- `GET /files` - List all source files in the solution (JSON only). For C++ class discovery, prefer `/classes`.");
        md.AppendLine("- `GET /classes` - List game C++ classes with header/source pairs and base class. Optional: `&search=term`, `&base=ACharacter`.");
        md.AppendLine("- `GET /describe_code?file=path` - Structural description of source file(s). Multiple: `&file=a&file=b`. `&debug=true` for diagnostics.");
        md.AppendLine("- `GET /inspect?file=path` - Run code inspection on file(s). Multiple: `&file=a&file=b`. `&debug=true` for diagnostics.");
        md.AppendLine();

        md.AppendLine("## Unreal Engine 5");
        md.AppendLine();
        md.AppendLine("- `GET /blueprints?class=ClassName` - List Blueprint classes deriving from a C++ class. `&debug=true` for diagnostics.");
        md.AppendLine("- `GET /bp?file=/Game/Path` - Blueprint composite info (audit + dependencies + referencers). Multiple: `&file=a&file=b`. Requires live UE editor for dependency data.");
        md.AppendLine("- `GET /blueprint-audit` - Blueprint audit data. Returns 409 if stale, 503 if not ready.");
        md.AppendLine("- `GET /blueprint-audit/refresh` - Trigger background refresh of Blueprint audit data.");
        md.AppendLine("- `GET /blueprint-audit/status` - Check status of Blueprint audit refresh.");
        md.AppendLine("- `GET /uassets?search=term` - Search or browse UAssets. `search` uses plain substrings (space-separated, all must match; no wildcards). Provide `search` and/or filters (`class`, `pathPrefix`). Optional: `&class=WidgetBlueprint`, `&pathPrefix=/Game`, `&limit=50`. Requires live UE editor.");
        md.AppendLine("- `GET /uassets/show?package=/Game/Path` - Asset detail: registry metadata, disk size, tags, dependency/referencer counts. Multiple: `&package=...`. Requires live UE editor.");
        md.AppendLine("- `GET /asset-refs/dependencies?asset=/Game/Path` - Asset dependencies. Requires live UE editor.");
        md.AppendLine("- `GET /asset-refs/referencers?asset=/Game/Path` - Asset referencers. Requires live UE editor.");
        md.AppendLine();

        md.AppendLine("## Diagnostics");
        md.AppendLine();
        md.AppendLine("- `GET /health` - Server and solution status (JSON).");
        md.AppendLine("- `GET /ue-project` - UE project detection info and engine path.");
        md.AppendLine("- `GET /asset-refs/status` - UE editor connection status.");
        md.AppendLine("- `GET /debug-psi-tree?file=path` - Raw PSI tree dump for a source file.");
        md.AppendLine();

        md.AppendLine("## MCP");
        md.AppendLine();
        md.AppendLine("- `POST /mcp` - MCP Streamable HTTP endpoint (JSON-RPC). Supports `initialize`, `tools/list`, `tools/call`. 15 tools available.");
        md.AppendLine("- Configure AI clients with `{\"url\": \"http://localhost:19876/mcp\"}` (Streamable HTTP transport).");

        HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", md.ToString());
    }
}
