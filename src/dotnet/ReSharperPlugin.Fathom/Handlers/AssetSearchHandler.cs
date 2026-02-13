using System;
using System.Net;
using System.Text;
using System.Text.Json;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

public class AssetSearchHandler : IRequestHandler
{
    private readonly UeProjectService _ueProject;
    private readonly AssetRefProxyService _proxy;
    private readonly ServerConfiguration _config;

    public AssetSearchHandler(UeProjectService ueProject, AssetRefProxyService proxy, ServerConfiguration config)
    {
        _ueProject = ueProject;
        _proxy = proxy;
        _config = config;
    }

    public bool CanHandle(string path) => path == "/uassets";

    public void Handle(HttpListenerContext ctx)
    {
        var format = HttpHelpers.GetFormat(ctx);

        if (!_ueProject.IsUnrealProject())
        {
            HttpHelpers.RespondWithFormat(ctx, format, 404,
                "This endpoint is only available for Unreal Engine projects.",
                new { error = "This endpoint is only available for Unreal Engine projects." });
            return;
        }

        var search = ctx.Request.QueryString["search"];
        var classFilter = ctx.Request.QueryString["class"];
        var pathPrefix = ctx.Request.QueryString["pathPrefix"];
        var hasFilters = !string.IsNullOrEmpty(classFilter) || pathPrefix != null;

        if (string.IsNullOrEmpty(search) && !hasFilters)
        {
            HttpHelpers.RespondWithFormat(ctx, format, 400,
                "Provide a 'search' term and/or filters ('class', 'pathPrefix').\n\nExamples:\n- `/uassets?search=player`\n- `/uassets?class=WidgetBlueprint&pathPrefix=/Game/UI`\n- `/uassets?search=main&class=Blueprint&limit=20`",
                new { error = "Provide a 'search' term and/or filters ('class', 'pathPrefix')", usage = "/uassets?search=term or /uassets?class=X&pathPrefix=/Game/..." });
            return;
        }

        // Detect wildcard/regex attempts and give a helpful nudge
        if (!string.IsNullOrEmpty(search) && (search.Contains("*") || search.Contains("?") || search.Contains("[")))
        {
            HttpHelpers.RespondWithFormat(ctx, format, 400,
                "Fuzzy search does not support wildcards or regex. Provide plain name substrings instead (space-separated, all must match).\n\nExample: `/uassets?search=main menu` matches assets whose name contains both \"main\" and \"menu\".",
                new { error = "Fuzzy search does not support wildcards or regex. Provide plain name substrings instead (space-separated, all must match)." });
            return;
        }

        if (!_proxy.IsAvailable())
        {
            HttpHelpers.RespondWithFormat(ctx, format, 503,
                "UE editor is not running. Asset search requires a live editor connection.\n\nOpen the UE project in the Unreal Editor with the FathomUELink plugin enabled.",
                new { error = "UE editor is not running. Asset search requires a live editor connection.", hint = "Open the UE project in the Unreal Editor with the FathomUELink plugin enabled." });
            return;
        }

        // Build proxy URL
        var proxyPath = "asset-refs/search?";
        if (!string.IsNullOrEmpty(search))
            proxyPath += $"q={System.Uri.EscapeDataString(search)}&";

        if (!string.IsNullOrEmpty(classFilter))
            proxyPath += $"class={System.Uri.EscapeDataString(classFilter)}&";

        var limitRaw = ctx.Request.QueryString["limit"];
        var limit = _config.MaxAssetSearchResults;
        if (int.TryParse(limitRaw, out var parsed) && parsed > 0)
            limit = Math.Min(parsed, _config.MaxAssetSearchResults);
        proxyPath += $"limit={limit}&";

        // Default to /Game (project assets only). Callers can opt out with &pathPrefix= or &pathPrefix=/
        if (pathPrefix == null)
            pathPrefix = "/Game";
        if (!string.IsNullOrEmpty(pathPrefix))
            proxyPath += $"pathPrefix={System.Uri.EscapeDataString(pathPrefix)}&";

        proxyPath = proxyPath.TrimEnd('&');

        var (body, statusCode) = _proxy.ProxyGetWithStatus(proxyPath);

        if (body == null)
        {
            HttpHelpers.RespondWithFormat(ctx, format, 502,
                "Failed to connect to UE editor server.\n\nThe editor may have just closed. Try again shortly.",
                new { error = "Failed to connect to UE editor server", hint = "The editor may have just closed. Try again shortly." });
            return;
        }

        if (format == "md")
        {
            var mdBody = FormatAsMarkdown(body, search ?? "(browse)", _config.Port);
            HttpHelpers.Respond(ctx, statusCode, "text/markdown; charset=utf-8", mdBody);
        }
        else
        {
            HttpHelpers.Respond(ctx, statusCode, "application/json; charset=utf-8", body);
        }
    }

    private static string FormatAsMarkdown(string jsonBody, string query, int port)
    {
        var sb = new StringBuilder();

        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                var count = results.GetArrayLength();
                if (count == 0)
                {
                    sb.AppendLine($"No assets found for \"{query}\".");
                    return sb.ToString();
                }

                sb.AppendLine($"{count} {(count == 1 ? "result" : "results")} for \"{query}\"");
                sb.AppendLine();

                foreach (var item in results.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : "?";
                    var pkg = item.TryGetProperty("package", out var p) ? p.GetString() : "?";
                    var cls = item.TryGetProperty("assetClass", out var c) ? c.GetString() : "?";

                    sb.Append("# ").Append(name).Append(" (").Append(cls).AppendLine(")");
                    sb.Append("package: ").AppendLine(pkg);
                    sb.AppendLine();

                    var escapedPkg = Uri.EscapeDataString(pkg);
                    sb.Append("[show](http://localhost:").Append(port)
                        .Append("/uassets/show?package=").Append(escapedPkg).AppendLine(")");
                    sb.Append("[dependencies](http://localhost:").Append(port)
                        .Append("/asset-refs/dependencies?asset=").Append(escapedPkg).AppendLine(")");
                    sb.Append("[referencers](http://localhost:").Append(port)
                        .Append("/asset-refs/referencers?asset=").Append(escapedPkg).AppendLine(")");

                    // Blueprint assets get a link to the detailed audit view
                    if (cls != null && cls.Contains("Blueprint"))
                    {
                        sb.Append("[blueprint-info](http://localhost:").Append(port)
                            .Append("/bp?file=").Append(escapedPkg).AppendLine(")");
                    }

                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine("(unexpected response format)");
            }
        }
        catch
        {
            sb.AppendLine("(failed to parse response)");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(jsonBody);
            sb.AppendLine("```");
        }

        return sb.ToString();
    }
}
