using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

public class LiveCodingHandler : IRequestHandler
{
    private readonly UeProjectService _ueProject;
    private readonly AssetRefProxyService _proxy;

    private const int CompileTimeoutSeconds = 120;
    private const int PollIntervalMs = 1000;

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

        // 1. Trigger compile (returns immediately from UE side)
        var (startBody, startStatus) = _proxy.ProxyGetWithStatus("live-coding/compile");
        if (startBody == null)
        {
            HttpHelpers.RespondWithFormat(ctx, format, 502,
                "Failed to connect to UE editor server.\n\nThe editor may have just closed. Try again shortly.",
                new { error = "Failed to connect to UE editor server", hint = "The editor may have just closed. Try again shortly." });
            return;
        }

        // Check if UE returned an error (NotStarted, AlreadyCompiling)
        try
        {
            using var startDoc = JsonDocument.Parse(startBody);
            var startResult = startDoc.RootElement.TryGetProperty("result", out var r) ? r.GetString() : null;
            if (startResult != "CompileStarted")
            {
                // Pass through the error response from UE
                if (format == "md")
                    HttpHelpers.Respond(ctx, startStatus, "text/markdown; charset=utf-8",
                        FormatCompileAsMarkdown(startBody));
                else
                    HttpHelpers.Respond(ctx, startStatus, "application/json; charset=utf-8", startBody);
                return;
            }
        }
        catch
        {
            HttpHelpers.Respond(ctx, startStatus, "application/json; charset=utf-8", startBody);
            return;
        }

        // 2. Poll /live-coding/status until lastCompile appears or timeout
        var sw = Stopwatch.StartNew();
        string compileResultJson = null;

        while (sw.Elapsed < TimeSpan.FromSeconds(CompileTimeoutSeconds))
        {
            Thread.Sleep(PollIntervalMs);

            if (!_proxy.IsAvailable())
            {
                HttpHelpers.RespondWithFormat(ctx, format, 502,
                    "UE editor disconnected during compile.",
                    new { error = "UE editor disconnected during compile." });
                return;
            }

            var (statusBody, statusCode) = _proxy.ProxyGetWithStatus("live-coding/status");
            if (statusBody == null)
                continue; // transient failure, retry

            try
            {
                using var doc = JsonDocument.Parse(statusBody);
                var root = doc.RootElement;

                if (root.TryGetProperty("lastCompile", out var lastCompile))
                {
                    compileResultJson = lastCompile.GetRawText();
                    break;
                }
            }
            catch
            {
                // malformed response, retry
            }
        }

        // 3. Return result
        if (compileResultJson != null)
        {
            if (format == "md")
                HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8",
                    FormatCompileAsMarkdown(compileResultJson));
            else
                HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", compileResultJson);
        }
        else
        {
            HttpHelpers.RespondWithFormat(ctx, format, 504,
                "Live Coding compile timed out after " + CompileTimeoutSeconds + "s.\n\nThe compile may still be running in the editor.",
                new { error = "Live Coding compile timed out", hint = "The compile may still be running in the editor." });
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

            if (root.TryGetProperty("lastCompile", out var lastCompile))
            {
                sb.AppendLine();
                sb.AppendLine("## Last Compile");
                var lastResult = lastCompile.TryGetProperty("result", out var lr) ? lr.GetString() : "?";
                var lastDuration = lastCompile.TryGetProperty("durationMs", out var ld) ? ld.GetInt32() : 0;
                sb.AppendLine($"**Result:** {lastResult}");
                if (lastDuration > 0)
                    sb.AppendLine($"**Duration:** {lastDuration / 1000.0:F1}s");
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
