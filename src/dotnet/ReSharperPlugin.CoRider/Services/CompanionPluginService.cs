using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using JetBrains.ProjectModel;
using JetBrains.Util;

namespace ReSharperPlugin.CoRider.Services;

public class CompanionPluginService
{
    private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<CompanionPluginService>();

    private readonly ISolution _solution;
    private readonly ServerConfiguration _config;

    public CompanionPluginService(ISolution solution, ServerConfiguration config)
    {
        _solution = solution;
        _config = config;
    }

    public CompanionPluginDetectionResult Detect()
    {
        try
        {
            var solutionDir = _solution.SolutionDirectory.FullPath;
            var pluginsDir = Path.Combine(solutionDir, "Plugins");
            var pluginName = _config.CompanionPluginName;
            var upluginFileName = pluginName + ".uplugin";

            string installedUpluginPath = null;

            // Search for the .uplugin in Plugins/<PluginName>/
            var expectedPath = Path.Combine(pluginsDir, pluginName, upluginFileName);
            if (File.Exists(expectedPath))
            {
                installedUpluginPath = expectedPath;
            }
            else if (Directory.Exists(pluginsDir))
            {
                // Broader search: look recursively under Plugins/
                var found = Directory.GetFiles(pluginsDir, upluginFileName, SearchOption.AllDirectories);
                if (found.Length > 0)
                    installedUpluginPath = found[0];
            }

            var bundledVersion = GetBundledVersion();

            if (installedUpluginPath == null)
            {
                return new CompanionPluginDetectionResult
                {
                    Status = "NotInstalled",
                    InstalledVersion = "",
                    BundledVersion = bundledVersion,
                    Message = $"{pluginName} is not installed. Click Install to add it to Plugins/."
                };
            }

            var installedVersion = ParseVersionFromUplugin(installedUpluginPath);

            if (installedVersion != bundledVersion)
            {
                return new CompanionPluginDetectionResult
                {
                    Status = "Outdated",
                    InstalledVersion = installedVersion,
                    BundledVersion = bundledVersion,
                    Message = $"{pluginName} {installedVersion} is outdated (bundled: {bundledVersion}). Click Install to update."
                };
            }

            return new CompanionPluginDetectionResult
            {
                Status = "UpToDate",
                InstalledVersion = installedVersion,
                BundledVersion = bundledVersion,
                Message = $"{pluginName} {installedVersion} is up to date."
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CompanionPluginService.Detect failed");
            return new CompanionPluginDetectionResult
            {
                Status = "NotInstalled",
                InstalledVersion = "",
                BundledVersion = "",
                Message = "Detection failed: " + ex.Message
            };
        }
    }

    public (bool success, string message) Install()
    {
        try
        {
            var zipPath = GetBundledZipPath();
            if (zipPath == null || !File.Exists(zipPath))
                return (false, "Bundled ZIP not found at expected location next to the DLL.");

            var solutionDir = _solution.SolutionDirectory.FullPath;
            var pluginsDir = Path.Combine(solutionDir, "Plugins");
            var targetDir = Path.Combine(pluginsDir, _config.CompanionPluginName);

            // Ensure Plugins/ directory exists
            if (!Directory.Exists(pluginsDir))
                Directory.CreateDirectory(pluginsDir);

            // Remove existing installation if present
            if (Directory.Exists(targetDir))
            {
                Log.Info($"CompanionPluginService: removing existing installation at {targetDir}");
                Directory.Delete(targetDir, recursive: true);
            }

            // Extract ZIP contents into target directory.
            // The ZIP root contains the plugin files directly (no wrapper folder),
            // so we extract into the target directory.
            ZipFile.ExtractToDirectory(zipPath, targetDir);

            Log.Info($"CompanionPluginService: installed companion plugin to {targetDir}");
            return (true, $"Installed to {targetDir}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CompanionPluginService.Install failed");
            return (false, "Install failed: " + ex.Message);
        }
    }

    public string GetBundledVersion()
    {
        try
        {
            var zipPath = GetBundledZipPath();
            if (zipPath == null || !File.Exists(zipPath))
                return "unknown";

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var upluginEntry = archive.Entries
                    .FirstOrDefault(e => e.Name.EndsWith(".uplugin", StringComparison.OrdinalIgnoreCase));

                if (upluginEntry == null)
                    return "unknown";

                using (var stream = upluginEntry.Open())
                using (var reader = new StreamReader(stream))
                {
                    var content = reader.ReadToEnd();
                    return ParseVersionFromContent(content);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("CompanionPluginService: failed to read bundled version: " + ex.Message);
            return "unknown";
        }
    }

    private string GetBundledZipPath()
    {
        var dllPath = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrEmpty(dllPath))
            return null;

        var dllDir = Path.GetDirectoryName(dllPath);
        return dllDir == null ? null : Path.Combine(dllDir, _config.CompanionPluginZipName);
    }

    private static string ParseVersionFromUplugin(string upluginPath)
    {
        try
        {
            var content = File.ReadAllText(upluginPath);
            return ParseVersionFromContent(content);
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ParseVersionFromContent(string json)
    {
        var match = Regex.Match(json, @"""VersionName""\s*:\s*""([^""]+)""");
        return match.Success ? match.Groups[1].Value : "unknown";
    }
}

public class CompanionPluginDetectionResult
{
    public string Status { get; set; }
    public string InstalledVersion { get; set; }
    public string BundledVersion { get; set; }
    public string Message { get; set; }
}
