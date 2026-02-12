using System;
using System.Net;
using System.Text;
using System.Text.Json;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

public class AssetShowHandler : IRequestHandler
{
    private readonly UeProjectService _ueProject;
    private readonly AssetRefProxyService _proxy;
    private readonly ServerConfiguration _config;

    public AssetShowHandler(UeProjectService ueProject, AssetRefProxyService proxy, ServerConfiguration config)
    {
        _ueProject = ueProject;
        _proxy = proxy;
        _config = config;
    }

    public bool CanHandle(string path) => path == "/uassets/show";

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

        var packages = ctx.Request.QueryString.GetValues("package");
        if (packages == null || packages.Length == 0)
        {
            HttpHelpers.RespondWithFormat(ctx, format, 400,
                "Missing required 'package' query parameter.\n\nUsage: `/uassets/show?package=/Game/Path/To/Asset`\nMultiple: `/uassets/show?package=/Game/A&package=/Game/B`",
                new { error = "Missing required 'package' query parameter", usage = "/uassets/show?package=/Game/Path/To/Asset" });
            return;
        }

        if (!_proxy.IsAvailable())
        {
            HttpHelpers.RespondWithFormat(ctx, format, 503,
                "UE editor is not running. Asset detail requires a live editor connection.\n\nOpen the UE project in the Unreal Editor with the CoRider plugin enabled.",
                new { error = "UE editor is not running. Asset detail requires a live editor connection.", hint = "Open the UE project in the Unreal Editor with the CoRider plugin enabled." });
            return;
        }

        if (format == "json")
        {
            HandleJson(ctx, packages);
        }
        else
        {
            HandleMarkdown(ctx, packages);
        }
    }

    private void HandleJson(HttpListenerContext ctx, string[] packages)
    {
        if (packages.Length == 1)
        {
            var proxyPath = $"asset-refs/show?package={Uri.EscapeDataString(packages[0])}";
            var (body, statusCode) = _proxy.ProxyGetWithStatus(proxyPath);

            if (body == null)
            {
                HttpHelpers.Respond(ctx, 502, "application/json; charset=utf-8",
                    "{\"error\":\"Failed to connect to UE editor server\"}");
                return;
            }

            HttpHelpers.Respond(ctx, statusCode, "application/json; charset=utf-8", body);
        }
        else
        {
            var sb = new StringBuilder();
            sb.Append('[');
            var first = true;

            foreach (var pkg in packages)
            {
                var proxyPath = $"asset-refs/show?package={Uri.EscapeDataString(pkg)}";
                var (body, _) = _proxy.ProxyGetWithStatus(proxyPath);

                if (!first) sb.Append(',');
                first = false;
                sb.Append(body ?? $"{{\"error\":\"failed\",\"package\":\"{pkg}\"}}");
            }

            sb.Append(']');
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", sb.ToString());
        }
    }

    private void HandleMarkdown(HttpListenerContext ctx, string[] packages)
    {
        var sb = new StringBuilder();

        foreach (var pkg in packages)
        {
            var proxyPath = $"asset-refs/show?package={Uri.EscapeDataString(pkg)}";
            var (body, statusCode) = _proxy.ProxyGetWithStatus(proxyPath);

            if (body == null)
            {
                sb.Append("# ").AppendLine(pkg);
                sb.AppendLine("Failed to connect to UE editor server.");
                sb.AppendLine();
                continue;
            }

            if (statusCode >= 400)
            {
                sb.Append("# ").AppendLine(pkg);
                sb.Append("Error: ").AppendLine(TryExtractError(body));
                sb.AppendLine();
                continue;
            }

            FormatAssetMarkdown(sb, body, pkg, _config.Port);
        }

        HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
    }

    private static void FormatAssetMarkdown(StringBuilder sb, string jsonBody, string pkg, int port)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;

            var name = root.TryGetProperty("name", out var n) ? n.GetString() : "?";
            var assetClass = root.TryGetProperty("assetClass", out var c) ? c.GetString() : "?";
            var package = root.TryGetProperty("package", out var p) ? p.GetString() : pkg;

            sb.Append("# ").Append(name).Append(" (").Append(assetClass).AppendLine(")");
            sb.Append("package: ").AppendLine(package);

            // Disk info
            if (root.TryGetProperty("diskPath", out var dp))
            {
                sb.Append("disk: ").Append(dp.GetString());
                if (root.TryGetProperty("diskSizeBytes", out var sz))
                {
                    var bytes = sz.GetInt64();
                    sb.Append(" (").Append(FormatSize(bytes)).Append(')');
                }
                sb.AppendLine();
            }

            sb.AppendLine();

            // Tags
            if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                foreach (var tag in tags.EnumerateObject())
                {
                    sb.Append(tag.Name).Append(": ").AppendLine(tag.Value.GetString());
                }
                sb.AppendLine();
            }

            // Counts
            if (root.TryGetProperty("dependencyCount", out var dc))
                sb.Append("dependencies: ").AppendLine(dc.GetInt32().ToString());
            if (root.TryGetProperty("referencerCount", out var rc))
                sb.Append("referencers: ").AppendLine(rc.GetInt32().ToString());
            sb.AppendLine();

            // Navigation links
            var escapedPkg = Uri.EscapeDataString(package);
            sb.Append("[dependencies](http://localhost:").Append(port)
                .Append("/asset-refs/dependencies?asset=").Append(escapedPkg).AppendLine(")");
            sb.Append("[referencers](http://localhost:").Append(port)
                .Append("/asset-refs/referencers?asset=").Append(escapedPkg).AppendLine(")");

            if (assetClass != null && assetClass.Contains("Blueprint"))
            {
                sb.Append("[blueprint-info](http://localhost:").Append(port)
                    .Append("/bp?file=").Append(escapedPkg).AppendLine(")");
            }

            sb.Append("[search-similar](http://localhost:").Append(port)
                .Append("/uassets?search=").Append(Uri.EscapeDataString(name))
                .Append("&class=").Append(Uri.EscapeDataString(assetClass)).AppendLine(")");

            sb.AppendLine();
        }
        catch
        {
            sb.Append("# ").AppendLine(pkg);
            sb.AppendLine("(failed to parse response)");
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(jsonBody);
            sb.AppendLine("```");
            sb.AppendLine();
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }

    private static string TryExtractError(string jsonBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString();
        }
        catch
        {
            // Not valid JSON
        }
        return jsonBody;
    }
}
