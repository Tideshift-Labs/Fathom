using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using JetBrains.Rider.Model;
using JetBrains.Util;

namespace ReSharperPlugin.Fathom.Services;

/// <summary>
/// Writes Fathom MCP server entries into config files consumed by various
/// AI coding assistants (Claude Code, VS Code Copilot, Cursor, OpenCode).
/// </summary>
public static class McpConfigWriter
{
    private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger(typeof(McpConfigWriter));

    public static void WriteMcpConfigFiles(
        int port,
        string solutionDir,
        FathomModel model,
        JetBrains.Collections.Viewable.IScheduler rdScheduler)
    {
        var written = new List<string>();

        var fathomEntry = new JsonObject
        {
            ["type"] = "http",
            ["url"] = $"http://localhost:{port}/mcp"
        };

        // Always write {solutionDir}/.mcp.json
        try
        {
            var path = Path.Combine(solutionDir, ".mcp.json");
            MergeMcpEntry(path, "mcpServers", fathomEntry);
            written.Add(".mcp.json");
        }
        catch (Exception ex)
        {
            Log.Warn("WriteMcpConfigFiles: failed to write .mcp.json: " + ex.Message);
        }

        // Write .vscode/mcp.json only if .vscode/ exists
        var vscodeDir = Path.Combine(solutionDir, ".vscode");
        if (Directory.Exists(vscodeDir))
        {
            try
            {
                var path = Path.Combine(vscodeDir, "mcp.json");
                MergeMcpEntry(path, "servers", fathomEntry);
                written.Add(".vscode/mcp.json");
            }
            catch (Exception ex)
            {
                Log.Warn("WriteMcpConfigFiles: failed to write .vscode/mcp.json: " + ex.Message);
            }
        }

        // Write .cursor/mcp.json only if .cursor/ exists
        var cursorDir = Path.Combine(solutionDir, ".cursor");
        if (Directory.Exists(cursorDir))
        {
            try
            {
                var path = Path.Combine(cursorDir, "mcp.json");
                MergeMcpEntry(path, "mcpServers", fathomEntry);
                written.Add(".cursor/mcp.json");
            }
            catch (Exception ex)
            {
                Log.Warn("WriteMcpConfigFiles: failed to write .cursor/mcp.json: " + ex.Message);
            }
        }

        // Merge into opencode.json only if it already exists (OpenCode uses "mcp" root key + "type":"remote")
        var openCodePath = Path.Combine(solutionDir, "opencode.json");
        if (File.Exists(openCodePath))
        {
            try
            {
                var openCodeEntry = new JsonObject
                {
                    ["type"] = "remote",
                    ["url"] = $"http://localhost:{port}/mcp"
                };
                MergeMcpEntry(openCodePath, "mcp", openCodeEntry);
                written.Add("opencode.json");
            }
            catch (Exception ex)
            {
                Log.Warn("WriteMcpConfigFiles: failed to write opencode.json: " + ex.Message);
            }
        }

        if (written.Count > 0)
        {
            var message = "Added MCP entry to " + string.Join(", ", written);
            Log.Info("WriteMcpConfigFiles: " + message);

            if (model != null && rdScheduler != null)
                rdScheduler.Queue(() => model.McpConfigStatus.Fire(message));
        }
    }

    internal static void MergeMcpEntry(string filePath, string rootKey, JsonObject fathomEntry)
    {
        JsonObject root;

        if (File.Exists(filePath))
        {
            var existing = File.ReadAllText(filePath);
            root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root[rootKey] is not JsonObject servers)
        {
            servers = new JsonObject();
            root[rootKey] = servers;
        }

        // Deep-clone the entry so each file gets its own node instance
        servers["fathom"] = JsonNode.Parse(fathomEntry.ToJsonString());

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(filePath, root.ToJsonString(options));
    }
}
