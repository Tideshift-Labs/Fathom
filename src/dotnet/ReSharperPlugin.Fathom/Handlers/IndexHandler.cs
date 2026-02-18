using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using JetBrains.ProjectModel;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Serialization;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

public class IndexHandler : IRequestHandler
{
    private readonly ISolution _solution;
    private readonly ServerConfiguration _config;
    private readonly UeProjectService _ueProject;
    private readonly AssetRefProxyService _assetRefProxy;

    private static readonly Lazy<string> HtmlTemplate = new Lazy<string>(LoadHtmlTemplate);
    private static readonly Lazy<string> LogoBase64 = new Lazy<string>(LoadLogoBase64);

    public IndexHandler(ISolution solution, ServerConfiguration config, UeProjectService ueProject,
        AssetRefProxyService assetRefProxy)
    {
        _solution = solution;
        _config = config;
        _ueProject = ueProject;
        _assetRefProxy = assetRefProxy;
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
            return;
        }

        var format = HttpHelpers.GetFormat(ctx);
        var isUe = _ueProject.IsUnrealProject();

        if (format == "json")
        {
            var indexJson = Json.Serialize(new
            {
                endpoints = new Dictionary<string, string>
                {
                    // Source code
                    ["GET /files"] = "List all source files in the solution (JSON only). For C++ class discovery, prefer /classes.",
                    ["GET /classes"] = "List game C++ classes with header/source pairs and base class. Optional: &search=term (name substring), &base=ACharacter (filter by base class). Default markdown; &format=json for JSON.",
                    ["GET /describe_code?file=path"] = "Structural description of source file(s). Multiple: &file=a&file=b. Default markdown; &format=json for JSON. &debug=true for diagnostics.",
                    ["GET /inspect?file=path"] = "Run code inspection on file(s). Multiple: &file=a&file=b. Default markdown; &format=json for JSON. &debug=true for diagnostics.",
                    // Blueprints & Assets (UE5)
                    ["GET /blueprints?class=ClassName"] = "[UE5] List Blueprint classes deriving from a C++ class. &format=json for JSON. &debug=true for diagnostics.",
                    ["GET /bp?file=/Game/Path"] = "[UE5] Blueprint composite info (audit + dependencies + referencers). Multiple: &file=a&file=b. &format=json for JSON. Requires live UE editor for dependency data.",
                    ["GET /blueprint-audit"] = "[UE5] Get Blueprint audit data. Returns 409 if stale, 503 if not ready. &format=json for JSON.",
                    ["GET /blueprint-audit/refresh"] = "[UE5] Trigger background refresh of Blueprint audit data.",
                    ["GET /blueprint-audit/status"] = "[UE5] Check status of Blueprint audit refresh.",
                    ["GET /uassets?search=term"] = "[UE5] Search or browse UAssets. search is plain substrings (space-separated, all must match; no wildcards). search and/or filters (class, pathPrefix) required. Optional: &class=WidgetBlueprint, &pathPrefix=/Game (default; &pathPrefix= for all), &limit=50. Requires live UE editor.",
                    ["GET /uassets/show?package=/Game/Path"] = "[UE5] Asset detail: registry metadata, disk size, tags, dependency/referencer counts. Multiple: &package=/Game/A&package=/Game/B. Requires live UE editor.",
                    ["GET /asset-refs/dependencies?asset=/Game/Path"] = "[UE5] Asset dependencies. Requires live UE editor.",
                    ["GET /asset-refs/referencers?asset=/Game/Path"] = "[UE5] Asset referencers. Requires live UE editor.",
                    // Diagnostics
                    ["GET /health"] = "Server and solution status (JSON).",
                    ["GET /ue-project"] = "Diagnostic: UE project detection info and engine path.",
                    ["GET /asset-refs/status"] = "[UE5] UE editor connection status.",
                    ["GET /debug-psi-tree?file=path"] = "Diagnostic: raw PSI tree dump for a source file.",
                    // MCP
                    ["POST /mcp"] = "MCP Streamable HTTP endpoint (JSON-RPC). Supports initialize, tools/list, tools/call. 15 tools available."
                },
                isUnrealProject = isUe
            });
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", indexJson);
            return;
        }

        // Default: branded HTML home page
        var ueInfo = _ueProject.GetUeProjectInfo();
        var editorConnected = _assetRefProxy.IsAvailable();

        var projectName = "";
        if (!string.IsNullOrEmpty(ueInfo.UProjectPath))
            projectName = Path.GetFileNameWithoutExtension(ueInfo.UProjectPath);

        var html = HtmlTemplate.Value
            .Replace("{{LOGO_BASE64}}", LogoBase64.Value)
            .Replace("{{PORT}}", _config.Port.ToString())
            .Replace("{{IS_UE}}", isUe ? "Yes" : "No")
            .Replace("{{UE_BAR_VISIBILITY}}", isUe ? "" : "ue-hidden")
            .Replace("{{UE_PROJECT_NAME}}", string.IsNullOrEmpty(projectName) ? "Unknown" : projectName)
            .Replace("{{UE_ENGINE_VERSION}}", string.IsNullOrEmpty(ueInfo.EngineVersion) ? "Unknown" : ueInfo.EngineVersion)
            .Replace("{{UE_EDITOR_STATUS}}", editorConnected ? "Connected" : "Not Running")
            .Replace("{{UE_EDITOR_DOT_CLASS}}", editorConnected ? "" : "disconnected");

        HttpHelpers.Respond(ctx, 200, "text/html; charset=utf-8", html);
    }

    private static string LoadHtmlTemplate()
    {
        var asm = Assembly.GetExecutingAssembly();
        using (var stream = asm.GetManifestResourceStream("ReSharperPlugin.Fathom.Resources.index.html"))
        {
            if (stream == null)
                return "<html><body><h1>Fathom</h1><p>Home page template not found.</p></body></html>";
            using (var reader = new StreamReader(stream))
                return reader.ReadToEnd();
        }
    }

    private static string LoadLogoBase64()
    {
        var asm = Assembly.GetExecutingAssembly();
        using (var stream = asm.GetManifestResourceStream("ReSharperPlugin.Fathom.Resources.fathom-logo-200.png"))
        {
            if (stream == null)
                return "";
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return Convert.ToBase64String(ms.ToArray());
            }
        }
    }
}
