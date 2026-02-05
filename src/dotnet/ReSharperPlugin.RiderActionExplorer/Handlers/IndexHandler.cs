using System.Collections.Generic;
using System.Net;
using JetBrains.ProjectModel;
using ReSharperPlugin.RiderActionExplorer.Formatting;
using ReSharperPlugin.RiderActionExplorer.Serialization;
using ReSharperPlugin.RiderActionExplorer.Services;

namespace ReSharperPlugin.RiderActionExplorer.Handlers;

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
                    ["GET /inspect?file=path"] = "Run code inspection on file(s). Multiple: &file=a&file=b. Default output is markdown; add &format=json for JSON. Add &debug=true for diagnostics.",
                    ["GET /blueprints?class=ClassName"] = "[UE5 only] List Blueprint classes deriving from a C++ class. Add &format=json for JSON.",
                    ["GET /blueprint-audit"] = "[UE5 only] Get Blueprint audit data (returns 409 if stale, 503 if not ready)",
                    ["GET /blueprint-audit/refresh"] = "[UE5 only] Trigger background refresh of Blueprint audit data",
                    ["GET /blueprint-audit/status"] = "[UE5 only] Check status of Blueprint audit refresh",
                    ["GET /ue-project"] = "Diagnostic: show UE project detection info"
                },
                isUnrealProject = _ueProject.IsUnrealProject()
            });
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", indexJson);
        }
    }
}
