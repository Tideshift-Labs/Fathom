using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JetBrains.Util;
using ReSharperPlugin.CoRider.Formatting;
using ReSharperPlugin.CoRider.Models;

namespace ReSharperPlugin.CoRider.Services;

public class BlueprintAuditService
{
    /// <summary>
    /// Must match FBlueprintAuditor::AuditSchemaVersion in the UE plugin.
    /// Bump both together when the JSON schema changes.
    /// </summary>
    private const int AuditSchemaVersion = 2;

    private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<BlueprintAuditService>();

    private readonly UeProjectService _ueProject;
    private readonly ServerConfiguration _config;

    private readonly object _auditLock = new object();
    private bool _auditRefreshInProgress;
    private DateTime? _lastAuditRefresh;
    private string _auditProcessOutput;
    private string _auditProcessError;
    private bool _bootCheckCompleted;
    private string _bootCheckResult;
    private bool _commandletMissing;
    private int? _lastExitCode;

    public BlueprintAuditService(UeProjectService ueProject, ServerConfiguration config)
    {
        _ueProject = ueProject;
        _config = config;
    }

    public BlueprintAuditResult GetAuditData()
    {
        var ueInfo = _ueProject.GetUeProjectInfo();

        if (!ueInfo.IsUnrealProject)
        {
            return new BlueprintAuditResult
            {
                Status = "not_ready",
                Message = "This endpoint is only available for Unreal Engine projects."
            };
        }

        // Check if we've detected the commandlet is missing
        bool isMissing;
        lock (_auditLock) { isMissing = _commandletMissing; }
        if (isMissing)
        {
            return new BlueprintAuditResult
            {
                Status = "commandlet_missing",
                Message = BlueprintAuditConstants.CommandletMissingMessage
            };
        }

        var uprojectDir = ueInfo.ProjectDirectory;
        var auditDir = Path.Combine(uprojectDir, "Saved", "Audit", $"v{AuditSchemaVersion}", "Blueprints");

        if (!Directory.Exists(auditDir))
        {
            return new BlueprintAuditResult
            {
                Status = "not_ready",
                Message = "Audit directory does not exist. Run /blueprint-audit/refresh first.",
                Action = "Call /blueprint-audit/refresh to generate audit data"
            };
        }

        var blueprints = new List<BlueprintAuditEntry>();
        var staleCount = 0;
        var errorCount = 0;

        foreach (var jsonFile in Directory.GetFiles(auditDir, "*.json", SearchOption.AllDirectories))
        {
            var entry = ReadAndCheckBlueprintAudit(jsonFile, uprojectDir);
            blueprints.Add(entry);
            if (entry.IsStale) staleCount++;
            if (entry.Error != null) errorCount++;
        }

        if (blueprints.Count == 0)
        {
            return new BlueprintAuditResult
            {
                Status = "not_ready",
                Message = "No audit files found. Run /blueprint-audit/refresh first.",
                Action = "Call /blueprint-audit/refresh to generate audit data"
            };
        }

        if (staleCount > 0)
        {
            DateTime? lastRefresh;
            lock (_auditLock) { lastRefresh = _lastAuditRefresh; }

            return new BlueprintAuditResult
            {
                Status = "stale",
                Message = "Audit data is stale. Refresh required before data can be returned.",
                TotalCount = blueprints.Count,
                StaleCount = staleCount,
                ErrorCount = errorCount,
                Action = "Call /blueprint-audit/refresh to update audit data",
                LastRefresh = lastRefresh?.ToString("o"),
                StaleExamples = blueprints.Where(b => b.IsStale).Take(_config.MaxStaleExamples).ToList()
            };
        }

        // All fresh - return the data
        DateTime? refresh;
        lock (_auditLock) { refresh = _lastAuditRefresh; }

        return new BlueprintAuditResult
        {
            Status = "fresh",
            TotalCount = blueprints.Count,
            ErrorCount = errorCount,
            LastRefresh = refresh?.ToString("o"),
            Blueprints = blueprints
        };
    }

    public bool TriggerRefresh(UeProjectInfo ueInfo)
    {
        lock (_auditLock)
        {
            if (_auditRefreshInProgress)
                return false;

            _auditRefreshInProgress = true;
            _auditProcessOutput = null;
            _auditProcessError = null;
        }

        _ = Task.Run(() => RunBlueprintAuditCommandlet(ueInfo));
        return true;
    }

    public bool IsRefreshInProgress
    {
        get { lock (_auditLock) { return _auditRefreshInProgress; } }
    }

    public BlueprintAuditStatus GetStatus()
    {
        bool inProgress;
        DateTime? lastRefresh;
        string output, error;
        bool isMissing;
        int? exitCode;

        lock (_auditLock)
        {
            inProgress = _auditRefreshInProgress;
            lastRefresh = _lastAuditRefresh;
            output = _auditProcessOutput;
            error = _auditProcessError;
            isMissing = _commandletMissing;
            exitCode = _lastExitCode;
        }

        return new BlueprintAuditStatus
        {
            InProgress = inProgress,
            CommandletMissing = isMissing,
            BootCheckCompleted = _bootCheckCompleted,
            BootCheckResult = _bootCheckResult,
            LastRefresh = lastRefresh?.ToString("o"),
            LastExitCode = exitCode,
            Output = output != null ? HttpHelpers.TruncateForJson(output, _config.MaxOutputLength) : null,
            Error = error != null ? HttpHelpers.TruncateForJson(error, _config.MaxErrorLength) : null
        };
    }

    public void CheckAndRefreshOnBoot()
    {
        try
        {
            var ueInfo = _ueProject.GetUeProjectInfo();
            if (!ueInfo.IsUnrealProject)
            {
                _bootCheckResult = "Not an Unreal project - skipping boot check";
                _bootCheckCompleted = true;
                Log.Warn("InspectionHttpServer: " + _bootCheckResult);
                return;
            }

            var uprojectDir = ueInfo.ProjectDirectory;
            var auditDir = Path.Combine(uprojectDir, "Saved", "Audit", $"v{AuditSchemaVersion}", "Blueprints");

            if (!Directory.Exists(auditDir))
            {
                _bootCheckResult = "Audit directory does not exist - triggering refresh";
                _bootCheckCompleted = true;
                Log.Warn("InspectionHttpServer: " + _bootCheckResult);
                TriggerRefresh(ueInfo);
                return;
            }

            var staleCount = 0;
            var totalCount = 0;

            foreach (var jsonFile in Directory.GetFiles(auditDir, "*.json", SearchOption.AllDirectories))
            {
                totalCount++;
                var entry = ReadAndCheckBlueprintAudit(jsonFile, uprojectDir);
                if (entry.IsStale) staleCount++;
            }

            if (totalCount == 0)
            {
                _bootCheckResult = "No audit files found - triggering refresh";
                _bootCheckCompleted = true;
                Log.Warn("InspectionHttpServer: " + _bootCheckResult);
                TriggerRefresh(ueInfo);
                return;
            }

            if (staleCount > 0)
            {
                _bootCheckResult = $"Found {staleCount}/{totalCount} stale blueprints - triggering refresh";
                _bootCheckCompleted = true;
                Log.Warn("InspectionHttpServer: " + _bootCheckResult);
                TriggerRefresh(ueInfo);
                return;
            }

            _bootCheckResult = $"All {totalCount} blueprints are fresh - no refresh needed";
            _bootCheckCompleted = true;
            Log.Warn("InspectionHttpServer: " + _bootCheckResult);
        }
        catch (Exception ex)
        {
            _bootCheckResult = "Boot check failed: " + ex.Message;
            _bootCheckCompleted = true;
            Log.Error(ex, "InspectionHttpServer: Boot check failed");
        }
    }

    public void SetBootCheckResult(string result)
    {
        _bootCheckResult = result;
        _bootCheckCompleted = true;
    }

    private void RunBlueprintAuditCommandlet(UeProjectInfo ueInfo)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ueInfo.CommandletExePath,
                Arguments = $"\"{ueInfo.UProjectPath}\" -run=BlueprintAudit -unattended -nopause",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = ueInfo.ProjectDirectory
            };

            Log.Warn($"InspectionHttpServer: Starting Blueprint audit: {startInfo.FileName} {startInfo.Arguments}");

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                    throw new InvalidOperationException("Failed to start process: " + startInfo.FileName);

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                var exitCode = process.ExitCode;
                var isMissing = IsCommandletMissingError(output, error, exitCode);

                lock (_auditLock)
                {
                    _auditProcessOutput = output;
                    _lastExitCode = exitCode;

                    if (isMissing)
                    {
                        _commandletMissing = true;
                        _auditProcessError = BlueprintAuditConstants.CommandletMissingMessage;
                    }
                    else
                    {
                        _commandletMissing = false;
                        _auditProcessError = string.IsNullOrWhiteSpace(error) ? null : error;
                        if (exitCode == 0)
                            _lastAuditRefresh = DateTime.Now;
                    }

                    _auditRefreshInProgress = false;

                }

                Log.Warn($"InspectionHttpServer: Blueprint audit completed. Exit code: {exitCode}, Missing: {isMissing}");
            }
        }
        catch (Exception ex)
        {
            lock (_auditLock)
            {
                _auditProcessError = ex.GetType().Name + ": " + ex.Message;
                _auditRefreshInProgress = false;
            }

            Log.Error(ex, "InspectionHttpServer: Blueprint audit failed");
        }
    }

    private static bool IsCommandletMissingError(string output, string error, int exitCode)
    {
        if (exitCode == 0) return false;

        var combined = (output ?? "") + (error ?? "");
        var lower = combined.ToLowerInvariant();

        return lower.Contains("unknown commandlet") ||
               lower.Contains("commandlet not found") ||
               lower.Contains("failed to find commandlet") ||
               lower.Contains("can't find commandlet") ||
               lower.Contains("unable to find commandlet") ||
               (lower.Contains("blueprintaudit") && lower.Contains("not recognized"));
    }

    private static BlueprintAuditEntry ReadAndCheckBlueprintAudit(string jsonFile, string uprojectDir)
    {
        var entry = new BlueprintAuditEntry { JsonFile = jsonFile };

        try
        {
            var jsonContent = File.ReadAllText(jsonFile);
            entry.Data = ParseSimpleJson(jsonContent);

            entry.Name = entry.Data.TryGetValue("Name", out var name) ? name?.ToString() : null;
            entry.Path = entry.Data.TryGetValue("Path", out var path) ? path?.ToString() : null;
            entry.SourceFileHash = entry.Data.TryGetValue("SourceFileHash", out var hash) ? hash?.ToString() : null;

            if (!string.IsNullOrEmpty(entry.Path))
            {
                var uassetPath = ConvertPackagePathToFilePath(entry.Path, uprojectDir);
                if (!string.IsNullOrEmpty(uassetPath) && File.Exists(uassetPath))
                {
                    entry.CurrentFileHash = ComputeMd5Hash(uassetPath);
                    entry.IsStale = !string.Equals(entry.SourceFileHash, entry.CurrentFileHash,
                        StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    entry.HashCheckFailed = true;
                    entry.Error = "Source file not found: " + (uassetPath ?? entry.Path);
                }
            }
            else if (string.IsNullOrEmpty(entry.SourceFileHash))
            {
                entry.IsStale = true;
                entry.Error = "No SourceFileHash in audit file";
            }
        }
        catch (Exception ex)
        {
            entry.Error = ex.GetType().Name + ": " + ex.Message;
        }

        return entry;
    }

    private static string ConvertPackagePathToFilePath(string packagePath, string uprojectDir)
    {
        if (string.IsNullOrEmpty(packagePath)) return null;

        var relativePath = packagePath;

        var dotIndex = relativePath.LastIndexOf('.');
        if (dotIndex > 0)
        {
            var lastSlash = relativePath.LastIndexOf('/');
            if (dotIndex > lastSlash)
            {
                relativePath = relativePath.Substring(0, dotIndex);
            }
        }

        if (relativePath.StartsWith("/Game/"))
        {
            relativePath = relativePath.Substring(6);
        }
        else if (relativePath.StartsWith("/"))
        {
            return null;
        }

        return Path.Combine(uprojectDir, "Content", relativePath.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
    }

    private static string ComputeMd5Hash(string filePath)
    {
        using (var md5 = MD5.Create())
        using (var stream = File.OpenRead(filePath))
        {
            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }

    private static Dictionary<string, object> ParseSimpleJson(string json)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        var stringPattern = new Regex(@"""(\w+)""\s*:\s*""([^""\\]*(?:\\.[^""\\]*)*)""");
        var numberPattern = new Regex(@"""(\w+)""\s*:\s*(-?\d+(?:\.\d+)?)");
        var boolPattern = new Regex(@"""(\w+)""\s*:\s*(true|false)");

        foreach (Match m in stringPattern.Matches(json))
        {
            if (!result.ContainsKey(m.Groups[1].Value))
                result[m.Groups[1].Value] = m.Groups[2].Value;
        }

        foreach (Match m in numberPattern.Matches(json))
        {
            if (!result.ContainsKey(m.Groups[1].Value))
                result[m.Groups[1].Value] = double.Parse(m.Groups[2].Value);
        }

        foreach (Match m in boolPattern.Matches(json))
        {
            if (!result.ContainsKey(m.Groups[1].Value))
                result[m.Groups[1].Value] = m.Groups[2].Value == "true";
        }

        return result;
    }
}
