using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Models;
using ReSharperPlugin.Fathom.Serialization;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

/// <summary>
/// Composite endpoint that returns audit data, dependencies, and referencers
/// for one or more Blueprints. Supports multiple file= query params.
/// </summary>
public class BlueprintInfoHandler : IRequestHandler
{
    private readonly BlueprintAuditService _auditService;
    private readonly AssetRefProxyService _assetRefProxy;
    private readonly UeProjectService _ueProject;
    private readonly ServerConfiguration _config;

    public BlueprintInfoHandler(BlueprintAuditService auditService,
        AssetRefProxyService assetRefProxy, UeProjectService ueProject,
        ServerConfiguration config)
    {
        _auditService = auditService;
        _assetRefProxy = assetRefProxy;
        _ueProject = ueProject;
        _config = config;
    }

    public bool CanHandle(string path) => path == "/bp";

    public void Handle(HttpListenerContext ctx)
    {
        var files = ctx.Request.QueryString.GetValues("file");
        if (files == null || files.Length == 0)
        {
            HttpHelpers.Respond(ctx, 400, "text/plain",
                "Missing 'file' query parameter.\n" +
                "Usage: /bp?file=/Game/Path/To/Blueprint\n" +
                "Multiple files: /bp?file=/Game/A&file=/Game/B\n" +
                "Add &format=json for JSON output.");
            return;
        }

        var format = HttpHelpers.GetFormat(ctx);
        var blueprintInfos = new List<BlueprintInfo>();
        var editorAvailable = _assetRefProxy.IsAvailable();

        foreach (var file in files)
        {
            var info = new BlueprintInfo { PackagePath = file };

            // Look up audit entry directly (works regardless of staleness)
            try
            {
                info.Audit = _auditService.FindAuditEntry(file);
            }
            catch
            {
                // Audit data not available
            }

            // Get dependencies from UE editor (filter out native /Script/ refs)
            if (editorAvailable)
            {
                var depsJson = _assetRefProxy.ProxyGet(
                    $"asset-refs/dependencies?asset={Uri.EscapeDataString(file)}");
                if (depsJson != null)
                    info.Dependencies = FilterNativeRefs(ParseAssetRefs(depsJson, "dependencies"));
            }

            // Get referencers from UE editor (filter out native /Script/ refs)
            if (editorAvailable)
            {
                var refsJson = _assetRefProxy.ProxyGet(
                    $"asset-refs/referencers?asset={Uri.EscapeDataString(file)}");
                if (refsJson != null)
                    info.Referencers = FilterNativeRefs(ParseAssetRefs(refsJson, "referencers"));
            }

            info.EditorAvailable = editorAvailable;
            blueprintInfos.Add(info);
        }

        if (format == "json")
        {
            var jsonResult = new BlueprintInfoJsonResult
            {
                Blueprints = blueprintInfos.Select(b => new BlueprintInfoJsonEntry
                {
                    PackagePath = b.PackagePath,
                    Audit = b.Audit?.Data,
                    AuditContent = b.Audit?.AuditContent,
                    Dependencies = b.Dependencies,
                    Referencers = b.Referencers
                }).ToList()
            };
            HttpHelpers.Respond(ctx, 200, "application/json; charset=utf-8", Json.Serialize(jsonResult));
        }
        else
        {
            var markdown = FormatAsMarkdown(blueprintInfos);
            HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", markdown);
        }
    }

    /// <summary>
    /// Removes native C++ module references (/Script/...) which are usually noise.
    /// </summary>
    private static List<AssetRefEntry> FilterNativeRefs(List<AssetRefEntry> refs)
    {
        return refs?.Where(r => !r.Package.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static List<AssetRefEntry> ParseAssetRefs(string jsonBody, string arrayKey)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;
            if (root.TryGetProperty(arrayKey, out var items) && items.ValueKind == JsonValueKind.Array)
            {
                var result = new List<AssetRefEntry>();
                foreach (var item in items.EnumerateArray())
                {
                    result.Add(new AssetRefEntry
                    {
                        Package = item.TryGetProperty("package", out var p) ? p.GetString() : "?",
                        Category = item.TryGetProperty("category", out var c) ? c.GetString() : "?",
                        Type = item.TryGetProperty("type", out var t) ? t.GetString() : "?",
                        AssetClass = item.TryGetProperty("assetClass", out var ac) ? ac.GetString() : null
                    });
                }
                return result;
            }
        }
        catch
        {
            // Parse failure
        }
        return null;
    }

    private static string FormatAsMarkdown(List<BlueprintInfo> infos)
    {
        var sb = new StringBuilder();

        foreach (var info in infos)
        {
            var displayName = info.PackagePath;
            var lastSlash = displayName.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < displayName.Length - 1)
                displayName = displayName.Substring(lastSlash + 1);

            sb.Append("# Blueprint Info: ").AppendLine(displayName);
            sb.AppendLine();

            // Audit section
            sb.AppendLine("## Audit Data");
            if (!string.IsNullOrEmpty(info.Audit?.AuditContent))
            {
                sb.AppendLine(info.Audit.AuditContent.TrimEnd());
            }
            else
            {
                sb.AppendLine("No audit data available.");
            }
            sb.AppendLine();

            // Dependencies section
            FormatRefSection(sb, "Dependencies", info.Dependencies, info.EditorAvailable);

            // Referencers section
            FormatRefSection(sb, "Referencers", info.Referencers, info.EditorAvailable);
        }

        return sb.ToString();
    }

    private static void FormatRefSection(StringBuilder sb, string title,
        List<AssetRefEntry> refs, bool editorAvailable)
    {
        if (!editorAvailable)
        {
            sb.Append("## ").AppendLine(title);
            sb.AppendLine("UE editor not running. Start the editor with CoRider plugin to see asset references.");
            sb.AppendLine();
            return;
        }

        if (refs == null)
        {
            sb.Append("## ").AppendLine(title);
            sb.AppendLine("Failed to retrieve data from UE editor.");
            sb.AppendLine();
            return;
        }

        sb.Append("## ").Append(title).Append(" (").Append(refs.Count).AppendLine(")");
        sb.AppendLine();

        if (refs.Count == 0)
        {
            sb.AppendLine(title == "Dependencies" ? "No dependencies found." : "No referencers found.");
            sb.AppendLine();
            return;
        }

        foreach (var r in refs)
        {
            sb.Append("### ").AppendLine(r.Package);
            if (!string.IsNullOrEmpty(r.AssetClass))
                sb.Append("Asset Class: ").AppendLine(r.AssetClass);
            sb.Append("Type: ").AppendLine(r.Type);
            sb.AppendLine();
        }
    }

    // Internal models

    private class BlueprintInfo
    {
        public string PackagePath { get; set; }
        public BlueprintAuditEntry Audit { get; set; }
        public List<AssetRefEntry> Dependencies { get; set; }
        public List<AssetRefEntry> Referencers { get; set; }
        public bool EditorAvailable { get; set; }
    }

    private class AssetRefEntry
    {
        public string Package { get; set; }
        public string Category { get; set; }
        public string Type { get; set; }
        public string AssetClass { get; set; }
    }

    private class BlueprintInfoJsonResult
    {
        public List<BlueprintInfoJsonEntry> Blueprints { get; set; }
    }

    private class BlueprintInfoJsonEntry
    {
        public string PackagePath { get; set; }
        public Dictionary<string, object> Audit { get; set; }
        public string AuditContent { get; set; }
        public List<AssetRefEntry> Dependencies { get; set; }
        public List<AssetRefEntry> Referencers { get; set; }
    }
}
