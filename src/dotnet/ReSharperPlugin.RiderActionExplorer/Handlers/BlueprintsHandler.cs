using System;
using System.Net;
using JetBrains.ProjectModel;
using ReSharperPlugin.RiderActionExplorer.Formatting;
using ReSharperPlugin.RiderActionExplorer.Serialization;
using ReSharperPlugin.RiderActionExplorer.Services;

namespace ReSharperPlugin.RiderActionExplorer.Handlers;

public class BlueprintsHandler : IRequestHandler
{
    private readonly ISolution _solution;
    private readonly ReflectionService _reflection;
    private readonly BlueprintQueryService _blueprintQuery;

    public BlueprintsHandler(ISolution solution, ReflectionService reflection, BlueprintQueryService blueprintQuery)
    {
        _solution = solution;
        _reflection = reflection;
        _blueprintQuery = blueprintQuery;
    }

    public bool CanHandle(string path) => path == "/blueprints";

    public void Handle(HttpListenerContext ctx)
    {
        var className = ctx.Request.QueryString["class"];
        if (string.IsNullOrWhiteSpace(className))
        {
            HttpHelpers.Respond(ctx, 400, "text/plain",
                "Missing 'class' query parameter.\n" +
                "Usage: /blueprints?class=AMyActor\n" +
                "Add &format=json for JSON output. Add &debug=true for diagnostics.");
            return;
        }

        var format = HttpHelpers.GetFormat(ctx);
        var debug = HttpHelpers.IsDebug(ctx);

        // Resolve UE4AssetsCache via reflection
        object assetsCache;
        try
        {
            assetsCache = _reflection.ResolveUE4AssetsCache();
        }
        catch (Exception ex)
        {
            HttpHelpers.Respond(ctx, 501, "text/plain",
                "UE4 Blueprint cache is not available. This feature requires a UE5 C++ project open in Rider.\n" +
                "Detail: " + ex.Message);
            return;
        }

        // Check cache readiness
        bool cacheReady;
        string cacheStatus;
        try
        {
            cacheReady = _reflection.CheckCacheReadiness();
            cacheStatus = cacheReady ? "ready" : "building";
        }
        catch
        {
            cacheReady = false;
            cacheStatus = "unknown";
        }

        // Query derived blueprints via reflection
        var solutionDir = _solution.SolutionDirectory;
        try
        {
            var result = _blueprintQuery.Query(className, assetsCache, solutionDir, debug);
            result.CacheReady = cacheReady;
            result.CacheStatus = cacheStatus;

            if (format == "json")
            {
                HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", Json.Serialize(result));
            }
            else
            {
                var markdown = BlueprintMarkdownFormatter.Format(result, debug);
                HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", markdown);
            }
        }
        catch (Exception ex)
        {
            HttpHelpers.Respond(ctx, 500, "text/plain",
                "Reflection error querying Blueprint classes.\n" +
                "This may indicate a Rider API change.\n" +
                "Detail: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
