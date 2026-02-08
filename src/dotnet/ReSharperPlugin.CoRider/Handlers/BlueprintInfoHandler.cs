using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using ReSharperPlugin.CoRider.Formatting;
using ReSharperPlugin.CoRider.Models;
using ReSharperPlugin.CoRider.Serialization;
using ReSharperPlugin.CoRider.Services;

namespace ReSharperPlugin.CoRider.Handlers;

/// <summary>
/// Composite endpoint that returns audit data, dependencies, and referencers
/// for one or more Blueprints. Supports multiple file= query params.
/// </summary>
public class BlueprintInfoHandler : IRequestHandler
{
    private readonly BlueprintAuditService _auditService;
    private readonly AssetRefProxyService _assetRefProxy;
    private readonly ServerConfiguration _config;

    public BlueprintInfoHandler(BlueprintAuditService auditService,
        AssetRefProxyService assetRefProxy, ServerConfiguration config)
    {
        _auditService = auditService;
        _assetRefProxy = assetRefProxy;
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

        // Get audit data once (may be null if not available)
        BlueprintAuditResult auditResult = null;
        try
        {
            auditResult = _auditService.GetAuditData();
        }
        catch
        {
            // Audit data not available
        }

        var editorAvailable = _assetRefProxy.IsAvailable();

        foreach (var file in files)
        {
            var info = new BlueprintInfo { PackagePath = file };

            // Find matching audit entry
            if (auditResult?.Blueprints != null)
            {
                info.Audit = auditResult.Blueprints
                    .FirstOrDefault(b => string.Equals(b.Path, file, StringComparison.OrdinalIgnoreCase));
            }

            // Get dependencies from UE editor
            if (editorAvailable)
            {
                var depsJson = _assetRefProxy.ProxyGet(
                    $"asset-refs/dependencies?asset={Uri.EscapeDataString(file)}");
                if (depsJson != null)
                    info.Dependencies = ParseAssetRefs(depsJson, "dependencies");
            }

            // Get referencers from UE editor
            if (editorAvailable)
            {
                var refsJson = _assetRefProxy.ProxyGet(
                    $"asset-refs/referencers?asset={Uri.EscapeDataString(file)}");
                if (refsJson != null)
                    info.Referencers = ParseAssetRefs(refsJson, "referencers");
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
                        Type = item.TryGetProperty("type", out var t) ? t.GetString() : "?"
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
            if (info.Audit?.Data != null)
            {
                var data = info.Audit.Data;
                AppendDataField(sb, data, "Name");
                AppendDataField(sb, data, "Path");
                AppendDataField(sb, data, "ParentClass", "Parent Class");
                AppendDataField(sb, data, "BlueprintType", "Blueprint Type");
                AppendDataField(sb, data, "Variables");
                AppendDataField(sb, data, "Components");
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

    private static void AppendDataField(StringBuilder sb, Dictionary<string, object> data,
        string key, string displayName = null)
    {
        if (data.TryGetValue(key, out var value) && value != null)
        {
            sb.Append(displayName ?? key).Append(": ").AppendLine(value.ToString());
        }
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
            sb.Append("Category: ").AppendLine(r.Category);
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
    }

    private class BlueprintInfoJsonResult
    {
        public List<BlueprintInfoJsonEntry> Blueprints { get; set; }
    }

    private class BlueprintInfoJsonEntry
    {
        public string PackagePath { get; set; }
        public Dictionary<string, object> Audit { get; set; }
        public List<AssetRefEntry> Dependencies { get; set; }
        public List<AssetRefEntry> Referencers { get; set; }
    }
}
