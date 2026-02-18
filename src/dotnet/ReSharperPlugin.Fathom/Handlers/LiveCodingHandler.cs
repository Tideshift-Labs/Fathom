using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

public class LiveCodingHandler : IRequestHandler
{
    private readonly UeProjectService _ueProject;
    private readonly AssetRefProxyService _proxy;

    // Separate HttpClient with long timeout for compile (blocks until LiveCodingConsole.exe finishes)
    private readonly HttpClient _compileClient = new()
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    public LiveCodingHandler(UeProjectService ueProject, AssetRefProxyService proxy)
    {
        _ueProject = ueProject;
        _proxy = proxy;
    }

    public bool CanHandle(string path) =>
        path == "/live-coding/compile" ||
        path == "/live-coding/status";

    public void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";

        switch (path)
        {
            case "/live-coding/compile":
                HandleCompile(ctx);
                break;
            case "/live-coding/status":
                HandleStatus(ctx);
                break;
        }
    }

    private void HandleCompile(HttpListenerContext ctx)
    {
        var format = HttpHelpers.GetFormat(ctx);

        if (!_ueProject.IsUnrealProject())
        {
            HttpHelpers.RespondWithFormat(ctx, format, 404,
                "This endpoint is only available for Unreal Engine projects.",
                new { error = "This endpoint is only available for Unreal Engine projects." });
            return;
        }

        if (!_proxy.IsAvailable())
        {
            HttpHelpers.RespondWithFormat(ctx, format, 503,
                "UE editor is not running. Live Coding requires a live editor connection.\n\nOpen the UE project in the Unreal Editor with the FathomUELink plugin enabled.",
                new { error = "UE editor is not running. Live Coding requires a live editor connection.", hint = "Open the UE project in the Unreal Editor with the FathomUELink plugin enabled." });
            return;
        }

        // Use long-timeout client for compile requests
        var (body, statusCode) = ProxyGetLongTimeout("live-coding/compile");

        if (body == null)
        {
            HttpHelpers.RespondWithFormat(ctx, format, 502,
                "Failed to connect to UE editor server.\n\nThe editor may have just closed. Try again shortly.",
                new { error = "Failed to connect to UE editor server", hint = "The editor may have just closed. Try again shortly." });
            return;
        }

        if (format == "md")
        {
            var mdBody = FormatCompileAsMarkdown(body);
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

        if (!_ueProject.IsUnrealProject())
        {
            HttpHelpers.RespondWithFormat(ctx, format, 404,
                "This endpoint is only available for Unreal Engine projects.",
                new { error = "This endpoint is only available for Unreal Engine projects." });
            return;
        }

        if (!_proxy.IsAvailable())
        {
            HttpHelpers.RespondWithFormat(ctx, format, 503,
                "UE editor is not running. Live Coding requires a live editor connection.\n\nOpen the UE project in the Unreal Editor with the FathomUELink plugin enabled.",
                new { error = "UE editor is not running. Live Coding requires a live editor connection.", hint = "Open the UE project in the Unreal Editor with the FathomUELink plugin enabled." });
            return;
        }

        var (body, statusCode) = _proxy.ProxyGetWithStatus("live-coding/status");

        if (body == null)
        {
            HttpHelpers.RespondWithFormat(ctx, format, 502,
                "Failed to connect to UE editor server.\n\nThe editor may have just closed. Try again shortly.",
                new { error = "Failed to connect to UE editor server", hint = "The editor may have just closed. Try again shortly." });
            return;
        }

        if (format == "md")
        {
            var mdBody = FormatStatusAsMarkdown(body);
            HttpHelpers.Respond(ctx, statusCode, "text/markdown; charset=utf-8", mdBody);
        }
        else
        {
            HttpHelpers.Respond(ctx, statusCode, "application/json; charset=utf-8", body);
        }
    }

    private (string Body, int StatusCode) ProxyGetLongTimeout(string path)
    {
        if (!_proxy.IsAvailable())
            return (null, 0);

        var status = _proxy.GetStatus();
        if (!status.Connected)
            return (null, 0);

        try
        {
            var url = $"http://localhost:{status.Port}/{path.TrimStart('/')}";
#pragma warning disable VSTHRD002 // Safe: runs on thread pool thread, no SynchronizationContext
            var response = _compileClient.GetAsync(url).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
            return (body, (int)response.StatusCode);
        }
        catch
        {
            return (null, 0);
        }
    }

    private static string FormatCompileAsMarkdown(string jsonBody)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Live Coding Compile");
        sb.AppendLine();

        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorEl))
            {
                sb.AppendLine($"**Error:** {errorEl.GetString()}");
                return sb.ToString();
            }

            var result = root.TryGetProperty("result", out var r) ? r.GetString() : "Unknown";
            var resultText = root.TryGetProperty("resultText", out var rt) ? rt.GetString() : "";
            var durationMs = root.TryGetProperty("durationMs", out var d) ? d.GetInt32() : 0;

            sb.AppendLine($"**Result:** {result}");
            if (!string.IsNullOrEmpty(resultText))
                sb.AppendLine($"**Details:** {resultText}");
            if (durationMs > 0)
                sb.AppendLine($"**Duration:** {durationMs / 1000.0:F1}s");
            sb.AppendLine();

            if (root.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array)
            {
                var logCount = logs.GetArrayLength();
                if (logCount > 0)
                {
                    sb.AppendLine("## Build Log");
                    sb.AppendLine("```");
                    foreach (var line in logs.EnumerateArray())
                    {
                        sb.AppendLine(line.GetString() ?? "");
                    }
                    sb.AppendLine("```");
                }
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

    private static string FormatStatusAsMarkdown(string jsonBody)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Live Coding Status");
        sb.AppendLine();

        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorEl))
            {
                sb.AppendLine($"**Error:** {errorEl.GetString()}");
                return sb.ToString();
            }

            var hasStarted = root.TryGetProperty("hasStarted", out var hs) && hs.GetBoolean();
            var isEnabled = root.TryGetProperty("isEnabledForSession", out var ie) && ie.GetBoolean();
            var isCompiling = root.TryGetProperty("isCompiling", out var ic) && ic.GetBoolean();

            sb.AppendLine($"**Has Started:** {(hasStarted ? "Yes" : "No")}");
            sb.AppendLine($"**Enabled for Session:** {(isEnabled ? "Yes" : "No")}");
            sb.AppendLine($"**Currently Compiling:** {(isCompiling ? "Yes" : "No")}");
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
