using System;
using System.IO;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.Util;

namespace ReSharperPlugin.Fathom.Services;

/// <summary>
/// Writes server marker files so external tools (MCP clients, scripts) can
/// discover the running Fathom HTTP server. Extracted from
/// FathomRiderHttpServer to keep the server class focused on HTTP lifecycle.
/// </summary>
public class ServerMarkerWriter
{
    private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<ServerMarkerWriter>();

    private readonly ISolution _solution;
    private readonly ServerConfiguration _config;
    private readonly Lifetime _lifetime;

    public ServerMarkerWriter(ISolution solution, ServerConfiguration config, Lifetime lifetime)
    {
        _solution = solution;
        _config = config;
        _lifetime = lifetime;
    }

    /// <summary>
    /// Writes both the legacy desktop marker and the local solution marker
    /// (.fathom-server.json) for the given port.
    /// </summary>
    public void WriteMarkerFiles(int port)
    {
        // Write global marker file (legacy)
        var markerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            _config.MarkerFileName);
        File.WriteAllText(markerPath,
            $"FathomRiderHttpServer running\n" +
            $"URL: http://localhost:{port}/\n" +
            $"Solution: {_solution.SolutionDirectory}\n" +
            $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");

        // Write local solution marker for MCP discovery
        try
        {
            var metaDir = GetFathomMetadataDir();
            Directory.CreateDirectory(metaDir);
            var localMarkerPath = Path.Combine(metaDir, ".fathom-server.json");
            // FIXME: use JsonObject + JsonSerializer instead of raw string interpolation
            var json = $"{{\n  \"port\": {port},\n  \"mcpEndpoint\": \"http://localhost:{port}/mcp\",\n  \"mcpTransport\": \"streamable-http\",\n  \"solution\": \"{_solution.SolutionDirectory.FullPath.Replace("\\", "\\\\")}\",\n  \"started\": \"{DateTime.Now:O}\"\n}}";
            File.WriteAllText(localMarkerPath, json);

            _lifetime.OnTermination(() =>
            {
                try { File.Delete(localMarkerPath); } catch { } // TODO: log at Trace level for debuggability
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ServerMarkerWriter: failed to write local marker file");
        }
    }

    /// <summary>
    /// Returns the directory where Fathom should store transient metadata files
    /// (.fathom-server.json, caches, etc.). For UE projects this resolves to
    /// Saved/Fathom/ which is already VCS-ignored, keeping the project root clean.
    /// For other project types, falls back to the solution directory.
    /// </summary>
    public string GetFathomMetadataDir()
    {
        var solutionDir = _solution.SolutionDirectory.FullPath;

        // TODO: delegate to UeProjectService instead of re-scanning
        if (Directory.GetFiles(solutionDir, "*.uproject").Length > 0)
        {
            var savedDir = Path.Combine(solutionDir, "Saved");
            if (Directory.Exists(savedDir))
                return Path.Combine(savedDir, "Fathom");
        }

        return solutionDir;
    }
}
