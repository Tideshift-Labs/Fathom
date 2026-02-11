using System.Collections.Generic;
using System.Net;
using JetBrains.ProjectModel;
using ReSharperPlugin.CoRider.Formatting;
using ReSharperPlugin.CoRider.Serialization;
using ReSharperPlugin.CoRider.Services;

namespace ReSharperPlugin.CoRider.Handlers;

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
        }
        else
        {
            var indexJson = Json.Serialize(new
            {
                endpoints = new Dictionary<string, string>
                {
                    // General
                    ["GET /"] = "This help message",
                    ["GET /health"] = "Server and solution status (JSON)",
                    // Source code
                    ["GET /files"] = "List all source files in the solution (JSON only). For C++ class discovery, prefer /classes.",
                    ["GET /classes"] = "List game C++ classes with header/source pairs and base class. Optional: &search=term (name substring), &base=ACharacter (filter by base class). Default markdown; &format=json for JSON.",
                    ["GET /describe_code?file=path"] = "Structural description of source file(s). Multiple: &file=a&file=b. Default markdown; &format=json for JSON. &debug=true for diagnostics.",
                    ["GET /inspect?file=path"] = "Run code inspection on file(s). Multiple: &file=a&file=b. Default markdown; &format=json for JSON. &debug=true for diagnostics.",
                    // Blueprints (UE5)
                    ["GET /blueprints?class=ClassName"] = "[UE5] List Blueprint classes deriving from a C++ class. &format=json for JSON. &debug=true for diagnostics.",
                    ["GET /bp?file=/Game/Path"] = "[UE5] Blueprint composite info (audit + dependencies + referencers). Multiple: &file=a&file=b. &format=json for JSON. Requires live UE editor for dependency data.",
                    ["GET /blueprint-audit"] = "[UE5] Get Blueprint audit data. Returns 409 if stale, 503 if not ready. &format=json for JSON.",
                    ["GET /blueprint-audit/refresh"] = "[UE5] Trigger background refresh of Blueprint audit data.",
                    ["GET /blueprint-audit/status"] = "[UE5] Check status of Blueprint audit refresh.",
                    // Asset references (UE5, requires live UE editor)
                    ["GET /uassets?search=term"] = "[UE5] Fuzzy search for UAssets by name (space-separated tokens, all must match). Optional: &class=WidgetBlueprint, &pathPrefix=/Game (default; &pathPrefix= to search all), &limit=50. Requires live UE editor.",
                    ["GET /uassets/show?package=/Game/Path"] = "[UE5] Asset detail: registry metadata, disk size, tags, dependency/referencer counts. Multiple: &package=/Game/A&package=/Game/B. Requires live UE editor.",
                    ["GET /asset-refs/dependencies?asset=/Game/Path"] = "[UE5] Asset dependencies. Requires live UE editor.",
                    ["GET /asset-refs/referencers?asset=/Game/Path"] = "[UE5] Asset referencers. Requires live UE editor.",
                    ["GET /asset-refs/status"] = "[UE5] UE editor connection status.",
                    // Diagnostics
                    ["GET /ue-project"] = "Diagnostic: UE project detection info and engine path."
                },
                isUnrealProject = _ueProject.IsUnrealProject()
            });
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", indexJson);
        }
    }
}
