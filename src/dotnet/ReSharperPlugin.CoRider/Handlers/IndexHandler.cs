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
                    ["GET /"] = "This help message",
                    ["GET /health"] = "Server and solution status",
                    ["GET /files"] = "List all user source files under solution directory",
                    ["GET /classes"] = "List game C++ classes grouped by header/source pair. Optional: &search=term (name substring), &base=ACharacter (filter by base class). Default markdown; add &format=json for JSON.",
                    ["GET /inspect?file=path"] = "Run code inspection on file(s). Multiple: &file=a&file=b. Default output is markdown; add &format=json for JSON. Add &debug=true for diagnostics.",
                    ["GET /describe_code?file=path"] = "Structural description of source file(s). Multiple: &file=a&file=b. Default output is markdown; add &format=json for JSON. Add &debug=true for diagnostics.",
                    ["GET /blueprints?class=ClassName"] = "[UE5 only] List Blueprint classes deriving from a C++ class. Add &format=json for JSON.",
                    ["GET /blueprint-audit"] = "[UE5 only] Get Blueprint audit data (returns 409 if stale, 503 if not ready)",
                    ["GET /blueprint-audit/refresh"] = "[UE5 only] Trigger background refresh of Blueprint audit data",
                    ["GET /blueprint-audit/status"] = "[UE5 only] Check status of Blueprint audit refresh",
                    ["GET /asset-refs/dependencies?asset="] = "[UE5 only] Asset dependencies (requires live UE editor)",
                    ["GET /asset-refs/referencers?asset="] = "[UE5 only] Asset referencers (requires live UE editor)",
                    ["GET /asset-refs/status"] = "[UE5 only] UE editor connection status",
                    ["GET /uassets?search=term"] = "[UE5 only] Fuzzy search for UAssets by name (space-separated tokens, all must match). Optional: &class=WidgetBlueprint (filter by asset class), &pathPrefix=/Game (default; use &pathPrefix= to search all), &limit=50 (max results). Requires live UE editor.",
                    ["GET /ue-project"] = "Diagnostic: show UE project detection info"
                },
                isUnrealProject = _ueProject.IsUnrealProject()
            });
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", indexJson);
        }
    }
}
