using System.IO;
using System.Net;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Serialization;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

public class BlueprintAuditHandler : IRequestHandler
{
    private readonly UeProjectService _ueProject;
    private readonly BlueprintAuditService _auditService;

    public BlueprintAuditHandler(UeProjectService ueProject, BlueprintAuditService auditService)
    {
        _ueProject = ueProject;
        _auditService = auditService;
    }

    public bool CanHandle(string path) =>
        path == "/blueprint-audit" ||
        path == "/blueprint-audit/refresh" ||
        path == "/blueprint-audit/status";

    public void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";

        switch (path)
        {
            case "/blueprint-audit":
                HandleAudit(ctx);
                break;
            case "/blueprint-audit/refresh":
                HandleRefresh(ctx);
                break;
            case "/blueprint-audit/status":
                HandleStatus(ctx);
                break;
        }
    }

    private void HandleAudit(HttpListenerContext ctx)
    {
        var format = HttpHelpers.GetFormat(ctx);
        var result = _auditService.GetAuditData();

        switch (result.Status)
        {
            case "not_ready" when result.Message.Contains("only available"):
                HttpHelpers.Respond(ctx, 404, "text/plain",
                    "This endpoint is only available for Unreal Engine projects.\n" +
                    "No .uproject file found in solution directory.");
                return;

            case "not_ready":
                if (format == "json")
                    HttpHelpers.Respond(ctx, 503, "application/json; charset=utf-8", Json.Serialize(result));
                else
                    HttpHelpers.Respond(ctx, 503, "text/markdown; charset=utf-8",
                        AuditMarkdownFormatter.FormatNotReady(result.Message));
                return;

            case "commandlet_missing":
                if (format == "json")
                    HttpHelpers.Respond(ctx, 501, "application/json; charset=utf-8", Json.Serialize(result));
                else
                    HttpHelpers.Respond(ctx, 501, "text/markdown; charset=utf-8",
                        AuditMarkdownFormatter.FormatCommandletMissing());
                return;

            case "stale":
                if (format == "json")
                    HttpHelpers.Respond(ctx, 409, "application/json; charset=utf-8", Json.Serialize(result));
                else
                    HttpHelpers.Respond(ctx, 409, "text/markdown; charset=utf-8",
                        AuditMarkdownFormatter.FormatStale(result));
                return;

            default: // "fresh"
                if (format == "json")
                    HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", Json.Serialize(result));
                else
                    HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8",
                        AuditMarkdownFormatter.FormatAuditResult(result));
                return;
        }
    }

    private void HandleRefresh(HttpListenerContext ctx)
    {
        var format = HttpHelpers.GetFormat(ctx);
        var ueInfo = _ueProject.GetUeProjectInfo();

        if (!ueInfo.IsUnrealProject)
        {
            HttpHelpers.Respond(ctx, 404, "text/plain",
                "This endpoint is only available for Unreal Engine projects.\n" +
                "No .uproject file found in solution directory.");
            return;
        }

        if (string.IsNullOrEmpty(ueInfo.CommandletExePath) || !File.Exists(ueInfo.CommandletExePath))
        {
            HttpHelpers.Respond(ctx, 500, "text/plain",
                "Cannot find UnrealEditor-Cmd.exe.\n" +
                "CommandletExePath: " + (ueInfo.CommandletExePath ?? "(null)"));
            return;
        }

        if (_auditService.IsRefreshInProgress)
        {
            if (format == "json")
            {
                HttpHelpers.Respond(ctx, 202, "application/json; charset=utf-8",
                    Json.Serialize(new { status = "in_progress", message = "Refresh already in progress" }));
            }
            else
            {
                HttpHelpers.Respond(ctx, 202, "text/plain",
                    "Refresh already in progress. Check /blueprint-audit/status");
            }
            return;
        }

        _auditService.TriggerRefresh(ueInfo);

        if (format == "json")
        {
            HttpHelpers.Respond(ctx, 202, "application/json; charset=utf-8",
                Json.Serialize(new { status = "started", message = "Blueprint audit refresh started" }));
        }
        else
        {
            HttpHelpers.Respond(ctx, 202, "text/plain",
                "Blueprint audit refresh started.\n" +
                "Check /blueprint-audit/status for progress.\n" +
                "Once complete, query /blueprint-audit for results.");
        }
    }

    private void HandleStatus(HttpListenerContext ctx)
    {
        var format = HttpHelpers.GetFormat(ctx);
        var status = _auditService.GetStatus();

        if (format == "json")
        {
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", Json.Serialize(status));
        }
        else
        {
            HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8",
                AuditMarkdownFormatter.FormatAuditStatus(status));
        }
    }
}
