using System;
using System.Net;
using JetBrains.ProjectModel;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Serialization;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

public class BlueprintsHandler : IRequestHandler
{
    private readonly ISolution _solution;
    private readonly ReflectionService _reflection;
    private readonly BlueprintQueryService _blueprintQuery;
    private readonly ServerConfiguration _config;

    public BlueprintsHandler(ISolution solution, ReflectionService reflection,
        BlueprintQueryService blueprintQuery, ServerConfiguration config)
    {
        _solution = solution;
        _reflection = reflection;
        _blueprintQuery = blueprintQuery;
        _config = config;
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

        // Strip UE C++ prefix (U/A/F) if present, since the asset cache uses unprefixed names.
        // Try both the original and stripped name to cover either convention.
        var strippedName = StripUePrefix(className);

        // Resolve UE4AssetsCache via reflection
        object assetsCache;
        try
        {
            assetsCache = _reflection.ResolveUe4AssetsCache();
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
            var result = _blueprintQuery.Query(strippedName, assetsCache, solutionDir, debug);

            // If stripping the prefix changed the name and got no results, try the original
            if (result.Blueprints.Count == 0 && strippedName != className)
                result = _blueprintQuery.Query(className, assetsCache, solutionDir, debug);

            result.ClassName = strippedName;
            result.CacheReady = cacheReady;
            result.CacheStatus = cacheStatus;

            // Populate MoreInfoUrl on each Blueprint entry (omitted for MCP to save tokens)
            var isMcp = ctx.Request.QueryString["source"] == "mcp";
            if (!isMcp)
            {
                foreach (var bp in result.Blueprints)
                {
                    if (!string.IsNullOrEmpty(bp.PackagePath))
                        bp.MoreInfoUrl = $"http://localhost:{_config.Port}/bp?file={Uri.EscapeDataString(bp.PackagePath)}";
                }
            }

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

    /// <summary>
    /// Strips UE C++ type prefix (A for Actor, U for UObject, F for struct) if the
    /// remainder starts with an uppercase letter. Also handles full object paths like
    /// "/Script/ModuleName.ClassName" by extracting just the class name.
    /// The asset cache stores unprefixed names.
    /// </summary>
    private static string StripUePrefix(string name)
    {
        // Handle full UE object paths: "/Script/ModuleName.ClassName"
        if (name.StartsWith("/Script/") || name.StartsWith("/Game/"))
        {
            var dotIndex = name.LastIndexOf('.');
            if (dotIndex >= 0 && dotIndex < name.Length - 1)
                name = name.Substring(dotIndex + 1);
            else
            {
                // No dot, take the last path segment
                var slashIndex = name.LastIndexOf('/');
                if (slashIndex >= 0 && slashIndex < name.Length - 1)
                    name = name.Substring(slashIndex + 1);
            }
        }

        // Strip single-letter UE prefix (A/U/F)
        if (name.Length > 1 &&
            (name[0] == 'U' || name[0] == 'A' || name[0] == 'F') &&
            char.IsUpper(name[1]))
        {
            return name.Substring(1);
        }

        return name;
    }
}
