using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using JetBrains.ProjectModel;
using JetBrains.Util;
using ReSharperPlugin.Fathom.Models;

namespace ReSharperPlugin.Fathom.Services;

public class CompanionPluginService
{
    private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<CompanionPluginService>();
    private const string InstallHashFileName = ".fathom-install-hash";

    private readonly ISolution _solution;
    private readonly ServerConfiguration _config;
    private readonly UeProjectService _ueProject;

    public CompanionPluginService(ISolution solution, ServerConfiguration config, UeProjectService ueProject)
    {
        _solution = solution;
        _config = config;
        _ueProject = ueProject;
    }

    public CompanionPluginDetectionResult Detect()
    {
        try
        {
            var bundledVersion = GetBundledVersion();
            var pluginName = _config.CompanionPluginName;

            var engineStatus = DetectAtDir(GetEnginePluginDir());
            var gameStatus = DetectAtDir(GetGamePluginDir());

            // Determine install location
            string installLocation;
            if (engineStatus.Found && gameStatus.Found) installLocation = "Both";
            else if (engineStatus.Found) installLocation = "Engine";
            else if (gameStatus.Found) installLocation = "Game";
            else installLocation = "None";

            // Pick installed version from best source (prefer engine, fall back to game)
            var installedVersion = engineStatus.Version ?? gameStatus.Version ?? "";

            // Determine overall status
            string status;
            string message;
            if (!engineStatus.Found && !gameStatus.Found)
            {
                status = "NotInstalled";
                message = $"{pluginName} is not installed. Use the status bar menu to install it.";
            }
            else if ((engineStatus.Found && engineStatus.Outdated) ||
                     (gameStatus.Found && gameStatus.Outdated))
            {
                status = "Outdated";
                var locations = installLocation == "Both" ? "Engine and Game" : installLocation;
                message = $"{pluginName} {installedVersion} is outdated (bundled: {bundledVersion}). " +
                          $"Installed to: {locations}.";
            }
            else
            {
                status = "UpToDate";
                message = $"{pluginName} {installedVersion} is up to date ({installLocation}).";
            }

            return new CompanionPluginDetectionResult
            {
                Status = status,
                InstalledVersion = installedVersion,
                BundledVersion = bundledVersion,
                InstallLocation = installLocation,
                Message = message
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
                InstallLocation = "None",
                Message = "Detection failed: " + ex.Message
            };
        }
    }

    /// <summary>
    /// Installs the companion plugin to the specified location ("Engine" or "Game").
    /// </summary>
    public (bool success, string message) Install(string location)
    {
        try
        {
            var zipPath = GetBundledZipPath();
            if (zipPath == null || !File.Exists(zipPath))
                return (false, "Bundled ZIP not found at expected location next to the DLL.");

            string targetDir;
            if (string.Equals(location, "Engine", StringComparison.OrdinalIgnoreCase))
            {
                targetDir = GetEnginePluginDir();
                if (targetDir == null)
                    return (false, "Engine path not available. Install to Game instead, or ensure the project is fully loaded.");
            }
            else
            {
                targetDir = GetGamePluginDir();
            }

            // Ensure parent directory exists
            var parentDir = Path.GetDirectoryName(targetDir);
            if (parentDir != null && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

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

            // Write the ZIP's MD5 hash so Detect() can spot content changes
            // even when the version string hasn't been bumped (local dev workflow).
            var zipHash = ComputeFileMd5(zipPath);
            if (zipHash != null)
                File.WriteAllText(Path.Combine(targetDir, InstallHashFileName), zipHash);

            Log.Info($"CompanionPluginService: installed companion plugin to {targetDir} (location={location})");
            return (true, $"Installed to {location} ({targetDir})");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warn($"CompanionPluginService: permission denied installing to {location}: {ex.Message}");
            return (false, $"Permission denied writing to {location} directory. " +
                           (string.Equals(location, "Engine", StringComparison.OrdinalIgnoreCase)
                               ? "Try running Rider as Administrator, or install to Game instead."
                               : ex.Message));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CompanionPluginService.Install failed");
            return (false, "Install failed: " + ex.Message);
        }
    }

    public (bool success, string message) RegenerateProjectFiles(UeProjectInfo ueInfo)
    {
        try
        {
            if (string.IsNullOrEmpty(ueInfo.UnrealBuildToolDllPath) ||
                !File.Exists(ueInfo.UnrealBuildToolDllPath))
            {
                return (false, "UnrealBuildTool.dll not found at: " + ueInfo.UnrealBuildToolDllPath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{ueInfo.UnrealBuildToolDllPath}\" " +
                            $"-mode=GenerateProjectFiles " +
                            $"-project=\"{ueInfo.UProjectPath}\" -game",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = ueInfo.ProjectDirectory
            };
            ApplyDotnetRollForward(startInfo);

            Log.Info($"CompanionPlugin: Regenerating project files: {startInfo.FileName} {startInfo.Arguments}");

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                    return (false, "Failed to start dotnet process for project file regeneration.");

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    Log.Info("CompanionPlugin: Project files regenerated successfully.");
                    return (true, "Project files regenerated.");
                }

                var msg = $"Project file regeneration exited with code {process.ExitCode}.";
                if (!string.IsNullOrWhiteSpace(error))
                    msg += " Error: " + error.Substring(0, Math.Min(error.Length, _config.MaxErrorLength));
                Log.Warn("CompanionPlugin: " + msg);
                return (false, msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CompanionPlugin.RegenerateProjectFiles failed");
            return (false, "Regeneration failed: " + ex.Message);
        }
    }

    public (bool success, string message) BuildEditorTarget(UeProjectInfo ueInfo, Action<string> onOutput = null)
    {
        try
        {
            if (string.IsNullOrEmpty(ueInfo.UnrealBuildToolDllPath) ||
                !File.Exists(ueInfo.UnrealBuildToolDllPath))
            {
                return (false, "UnrealBuildTool.dll not found at: " + ueInfo.UnrealBuildToolDllPath);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{ueInfo.UnrealBuildToolDllPath}\" " +
                            $"{ueInfo.EditorTargetName} {_config.PlatformBinaryFolder} Development " +
                            $"-project=\"{ueInfo.UProjectPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = ueInfo.ProjectDirectory
            };
            ApplyDotnetRollForward(startInfo);

            Log.Info($"CompanionPlugin: Building editor target: {startInfo.FileName} {startInfo.Arguments}");
            return RunProcessWithStreaming(startInfo, "CompanionPlugin.BuildEditorTarget", onOutput);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CompanionPlugin.BuildEditorTarget failed");
            return (false, "Build failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Builds the engine-level plugin using RunUAT BuildPlugin.
    /// Installed/launcher engine builds cannot compile engine plugins at runtime,
    /// so we must pre-build them via the Unreal Automation Tool.
    /// </summary>
    public (bool success, string message) BuildEnginePlugin(UeProjectInfo ueInfo, Action<string> onOutput = null)
    {
        try
        {
            if (string.IsNullOrEmpty(ueInfo.EnginePath))
                return (false, "Engine path not available.");

            var runUatPath = Path.Combine(ueInfo.EnginePath, "Build", "BatchFiles", "RunUAT.bat");
            if (!File.Exists(runUatPath))
                return (false, "RunUAT.bat not found at: " + runUatPath);

            var enginePluginDir = GetEnginePluginDir();
            if (enginePluginDir == null)
                return (false, "Engine plugin directory not available.");

            var upluginPath = Path.Combine(enginePluginDir, _config.CompanionPluginName + ".uplugin");
            if (!File.Exists(upluginPath))
                return (false, "Plugin .uplugin not found at: " + upluginPath);

            // Build to a temp directory, then copy output over the source.
            // BuildPlugin stages both source and binaries to -Package.
            var tempPackageDir = Path.Combine(Path.GetTempPath(), "FathomUELink_Build_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            var startInfo = new ProcessStartInfo
            {
                FileName = runUatPath,
                Arguments = $"BuildPlugin -Plugin=\"{upluginPath}\" -Package=\"{tempPackageDir}\" -Rocket",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = ueInfo.EnginePath
            };

            Log.Info($"CompanionPlugin: Building engine plugin via RunUAT: {startInfo.FileName} {startInfo.Arguments}");
            var buildResult = RunProcessWithStreaming(startInfo, "CompanionPlugin.BuildEnginePlugin", onOutput);

            if (!buildResult.success)
            {
                try { if (Directory.Exists(tempPackageDir)) Directory.Delete(tempPackageDir, true); } catch { }
                return buildResult;
            }

            // Copy built Binaries/ from temp package to engine plugin dir
            var builtBinDir = Path.Combine(tempPackageDir, "Binaries");
            if (Directory.Exists(builtBinDir))
            {
                var targetBinDir = Path.Combine(enginePluginDir, "Binaries");
                CopyDirectory(builtBinDir, targetBinDir);
                Log.Info($"CompanionPlugin: Copied built binaries to {targetBinDir}");
            }

            // Copy built Intermediate/ if present
            var builtIntDir = Path.Combine(tempPackageDir, "Intermediate");
            if (Directory.Exists(builtIntDir))
            {
                var targetIntDir = Path.Combine(enginePluginDir, "Intermediate");
                CopyDirectory(builtIntDir, targetIntDir);
            }

            // Clean up temp dir
            try { Directory.Delete(tempPackageDir, true); } catch { }

            Log.Info("CompanionPlugin: Engine plugin built successfully via RunUAT.");
            return (true, "Plugin compiled successfully.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CompanionPlugin.BuildEnginePlugin failed");
            return (false, "Build failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Recursively copies a directory tree, overwriting existing files.
    /// </summary>
    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
    }

    /// <summary>
    /// Runs a process, streaming stdout/stderr line by line via the onOutput callback.
    /// Returns success/failure and a summary message.
    /// </summary>
    private static (bool success, string message) RunProcessWithStreaming(
        ProcessStartInfo startInfo, string logPrefix, Action<string> onOutput)
    {
        using (var process = Process.Start(startInfo))
        {
            if (process == null)
                return (false, "Failed to start process.");

            // Stream output line by line via event handlers
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) onOutput?.Invoke(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) onOutput?.Invoke(e.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                Log.Info($"{logPrefix}: completed successfully.");
                return (true, "Plugin compiled successfully.");
            }

            var msg = $"Build exited with code {process.ExitCode}.";
            Log.Warn($"{logPrefix}: {msg}");
            return (false, msg);
        }
    }

    /// <summary>
    /// Ensures the dotnet child process can run apps targeting older major versions
    /// (e.g. UE 5.7 UBT targets net8.0 but Rider may only bundle net9.0).
    /// </summary>
    private static void ApplyDotnetRollForward(ProcessStartInfo startInfo)
    {
        startInfo.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
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

    /// <summary>
    /// Returns the plugin directory inside the engine's Plugins/Marketplace/ folder,
    /// or null if the engine path is not available.
    /// </summary>
    private string GetEnginePluginDir()
    {
        try
        {
            var ueInfo = _ueProject.GetUeProjectInfo();
            if (string.IsNullOrEmpty(ueInfo?.EnginePath))
                return null;

            return Path.Combine(ueInfo.EnginePath, "Plugins", "Marketplace", _config.CompanionPluginName);
        }
        catch (Exception ex)
        {
            Log.Warn("CompanionPluginService: failed to resolve engine plugin dir: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Returns the plugin directory inside the game project's Plugins/ folder.
    /// </summary>
    private string GetGamePluginDir()
    {
        var solutionDir = _solution.SolutionDirectory.FullPath;
        return Path.Combine(solutionDir, "Plugins", _config.CompanionPluginName);
    }

    /// <summary>
    /// Checks a single directory for an installed plugin and returns its status.
    /// </summary>
    private LocationDetection DetectAtDir(string pluginDir)
    {
        if (string.IsNullOrEmpty(pluginDir))
            return new LocationDetection { Found = false };

        var upluginFileName = _config.CompanionPluginName + ".uplugin";

        // Check expected location first
        var upluginPath = Path.Combine(pluginDir, upluginFileName);
        if (!File.Exists(upluginPath))
        {
            // Broader search within the directory
            if (Directory.Exists(pluginDir))
            {
                var found = Directory.GetFiles(pluginDir, upluginFileName, SearchOption.AllDirectories);
                if (found.Length > 0)
                    upluginPath = found[0];
                else
                    return new LocationDetection { Found = false };
            }
            else
            {
                return new LocationDetection { Found = false };
            }
        }

        var version = ParseVersionFromUplugin(upluginPath);
        var bundledVersion = GetBundledVersion();
        var installedDir = Path.GetDirectoryName(upluginPath);

        // Version mismatch
        if (version != bundledVersion)
        {
            return new LocationDetection { Found = true, Version = version, Outdated = true };
        }

        // Version matches, but check if ZIP contents changed (local dev workflow)
        if (installedDir != null && HasBundledContentChanged(installedDir))
        {
            return new LocationDetection { Found = true, Version = version, Outdated = true };
        }

        return new LocationDetection { Found = true, Version = version, Outdated = false };
    }

    private bool HasBundledContentChanged(string installedDir)
    {
        try
        {
            var hashFilePath = Path.Combine(installedDir, InstallHashFileName);
            if (!File.Exists(hashFilePath))
                return true; // No hash on record, needs reinstall to start tracking

            var storedHash = File.ReadAllText(hashFilePath).Trim();
            var zipPath = GetBundledZipPath();
            if (zipPath == null || !File.Exists(zipPath))
                return false;

            var currentHash = ComputeFileMd5(zipPath);
            return !string.Equals(storedHash, currentHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("CompanionPluginService: hash comparison failed: " + ex.Message);
            return false;
        }
    }

    private static string ComputeFileMd5(string filePath)
    {
        try
        {
            using (var md5 = MD5.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
        catch
        {
            return null;
        }
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

    private struct LocationDetection
    {
        public bool Found;
        public string Version;
        public bool Outdated;
    }
}

public class CompanionPluginDetectionResult
{
    public string Status { get; set; }
    public string InstalledVersion { get; set; }
    public string BundledVersion { get; set; }
    public string InstallLocation { get; set; }
    public string Message { get; set; }
}
