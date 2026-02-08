using System.Net;
using System.Text;
using System.Text.Json;
using ReSharperPlugin.CoRider.Formatting;
using ReSharperPlugin.CoRider.Serialization;
using ReSharperPlugin.CoRider.Services;

namespace ReSharperPlugin.CoRider.Handlers;

public class AssetRefHandler : IRequestHandler
{
    private readonly UeProjectService _ueProject;
    private readonly AssetRefProxyService _proxy;

    public AssetRefHandler(UeProjectService ueProject, AssetRefProxyService proxy)
    {
        _ueProject = ueProject;
        _proxy = proxy;
    }

    public bool CanHandle(string path) =>
        path == "/asset-refs/dependencies" ||
        path == "/asset-refs/referencers" ||
        path == "/asset-refs/status";

    public void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";

        switch (path)
        {
            case "/asset-refs/dependencies":
                HandleQuery(ctx, "dependencies");
                break;
            case "/asset-refs/referencers":
                HandleQuery(ctx, "referencers");
                break;
            case "/asset-refs/status":
                HandleStatus(ctx);
                break;
        }
    }

    private void HandleQuery(HttpListenerContext ctx, string queryType)
    {
        var format = HttpHelpers.GetFormat(ctx);

        if (!_ueProject.IsUnrealProject())
        {
            HttpHelpers.RespondWithFormat(ctx, format, 404,
                "This endpoint is only available for Unreal Engine projects.",
                new { error = "This endpoint is only available for Unreal Engine projects." });
            return;
        }

        var asset = ctx.Request.QueryString["asset"];
        if (string.IsNullOrEmpty(asset))
        {
            HttpHelpers.RespondWithFormat(ctx, format, 400,
                $"Missing required 'asset' query parameter.\n\nUsage: `/asset-refs/{queryType}?asset=/Game/Path/To/Asset`",
                new { error = "Missing required 'asset' query parameter", usage = $"/asset-refs/{queryType}?asset=/Game/Path/To/Asset" });
            return;
        }

        if (!_proxy.IsAvailable())
        {
            HttpHelpers.RespondWithFormat(ctx, format, 503,
                "UE editor is not running. Asset reference queries require a live editor connection.\n\nOpen the UE project in the Unreal Editor with the CoRider plugin enabled.",
                new { error = "UE editor is not running. Asset reference queries require a live editor connection.", hint = "Open the UE project in the Unreal Editor with the CoRider plugin enabled." });
            return;
        }

        var proxyPath = $"asset-refs/{queryType}?asset={System.Uri.EscapeDataString(asset)}";
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
            var mdBody = FormatAsMarkdown(body, queryType, asset);
            HttpHelpers.Respond(ctx, statusCode, "text/markdown; charset=utf-8", mdBody);
        }
        else
        {
            HttpHelpers.Respond(ctx, statusCode, "application/json; charset=utf-8", body);
        }
    }

    private void HandleStatus(HttpListenerContext ctx)
    {
        var format = HttpHelpers.GetFormat(ctx);
        var status = _proxy.GetStatus();

        if (format == "json")
        {
            var statusCode = status.Connected ? 200 : 503;
            HttpHelpers.Respond(ctx, statusCode, "application/json; charset=utf-8",
                Json.Serialize(status));
        }
        else
        {
            var statusCode = status.Connected ? 200 : 503;
            HttpHelpers.Respond(ctx, statusCode, "text/markdown; charset=utf-8",
                FormatStatusAsMarkdown(status));
        }
    }

    private static string FormatAsMarkdown(string jsonBody, string queryType, string asset)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Asset {(queryType == "dependencies" ? "Dependencies" : "Referencers")}");
        sb.AppendLine();
        sb.AppendLine($"**Asset:** `{asset}`");
        sb.AppendLine();

        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;

            if (root.TryGetProperty(queryType, out var items) && items.ValueKind == JsonValueKind.Array)
            {
                var count = items.GetArrayLength();
                if (count == 0)
                {
                    sb.AppendLine(queryType == "dependencies"
                        ? "No dependencies found."
                        : "No referencers found.");
                }
                else
                {
                    sb.AppendLine($"**Total:** {count}");
                    sb.AppendLine();

                    foreach (var item in items.EnumerateArray())
                    {
                        var pkg = item.TryGetProperty("package", out var p) ? p.GetString() : "?";
                        var type = item.TryGetProperty("type", out var t) ? t.GetString() : "?";
                        var assetClass = item.TryGetProperty("assetClass", out var ac) ? ac.GetString() : null;
                        sb.AppendLine($"### {pkg}");
                        if (!string.IsNullOrEmpty(assetClass))
                            sb.AppendLine($"Asset Class: {assetClass}");
                        sb.AppendLine($"Type: {type}");
                        sb.AppendLine();
                    }
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

    private static string FormatStatusAsMarkdown(AssetRefStatus status)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Asset Reference Server Status");
        sb.AppendLine();

        if (status.Connected)
        {
            sb.AppendLine($"**Status:** Connected");
            sb.AppendLine($"**Port:** {status.Port}");
            sb.AppendLine($"**PID:** {status.Pid}");
        }
        else
        {
            sb.AppendLine($"**Status:** Not connected");
            sb.AppendLine($"**Reason:** {status.Message}");
        }

        return sb.ToString();
    }
}
