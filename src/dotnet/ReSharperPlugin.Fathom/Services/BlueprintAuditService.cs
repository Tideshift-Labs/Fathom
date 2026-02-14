using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Util;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Models;

namespace ReSharperPlugin.Fathom.Services;

public class BlueprintAuditService
{
    /// <summary>
    /// Must match FBlueprintAuditor::AuditSchemaVersion in the UE plugin.
    /// Bump both together when the audit format changes.
    /// </summary>
    private const int AuditSchemaVersion = 5;

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
        var versionDir = Path.Combine(uprojectDir, "Saved", "Fathom", "Audit", $"v{AuditSchemaVersion}");

        var blueprintDir = Path.Combine(versionDir, "Blueprints");
        var dataTableDir = Path.Combine(versionDir, "DataTables");
        var dataAssetDir = Path.Combine(versionDir, "DataAssets");

        if (!Directory.Exists(blueprintDir) && !Directory.Exists(dataTableDir) && !Directory.Exists(dataAssetDir))
        {
            return new BlueprintAuditResult
            {
                Status = "not_ready",
                Message = "Audit directory does not exist. Run /blueprint-audit/refresh first.",
                Action = "Call /blueprint-audit/refresh to generate audit data"
            };
        }

        var blueprints = new List<BlueprintAuditEntry>();
        var dataTables = new List<BlueprintAuditEntry>();
        var dataAssets = new List<BlueprintAuditEntry>();
        var staleCount = 0;
        var errorCount = 0;

        void ScanDir(string dir, string assetType, List<BlueprintAuditEntry> list)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var mdFile in Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories))
            {
                var entry = ReadAndCheckBlueprintAudit(mdFile, uprojectDir);
                entry.AssetType = assetType;
                list.Add(entry);
                if (entry.IsStale) staleCount++;
                if (entry.Error != null) errorCount++;
            }
        }

        ScanDir(blueprintDir, "Blueprint", blueprints);
        ScanDir(dataTableDir, "DataTable", dataTables);
        ScanDir(dataAssetDir, "DataAsset", dataAssets);

        var totalCount = blueprints.Count + dataTables.Count + dataAssets.Count;

        if (totalCount == 0)
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

            var allEntries = blueprints.Concat(dataTables).Concat(dataAssets);
            return new BlueprintAuditResult
            {
                Status = "stale",
                Message = "Audit data is stale. Refresh required before data can be returned.",
                TotalCount = totalCount,
                StaleCount = staleCount,
                ErrorCount = errorCount,
                DataTableCount = dataTables.Count,
                DataAssetCount = dataAssets.Count,
                Action = "Call /blueprint-audit/refresh to update audit data",
                LastRefresh = lastRefresh?.ToString("o"),
                StaleExamples = allEntries.Where(b => b.IsStale).Take(_config.MaxStaleExamples).ToList()
            };
        }

        // All fresh - return the data
        DateTime? refresh;
        lock (_auditLock) { refresh = _lastAuditRefresh; }

        return new BlueprintAuditResult
        {
            Status = "fresh",
            TotalCount = totalCount,
            ErrorCount = errorCount,
            DataTableCount = dataTables.Count,
            DataAssetCount = dataAssets.Count,
            LastRefresh = refresh?.ToString("o"),
            Blueprints = blueprints,
            DataTables = dataTables,
            DataAssets = dataAssets
        };
    }

    public bool TriggerRefresh(UeProjectInfo ueInfo)
    {
        Log.Info("BlueprintAudit: TriggerRefresh requested");

        if (string.IsNullOrEmpty(ueInfo.CommandletExePath))
        {
            Log.Warn("BlueprintAudit: Cannot start Blueprint audit - CommandletExePath is not resolved (engine path may still be loading)");
            return false;
        }

        lock (_auditLock)
        {
            if (_auditRefreshInProgress)
                return false;

            _auditRefreshInProgress = true;
            _auditProcessOutput = null;
            _auditProcessError = null;
        }

        _ = Task.Run(() => RunBlueprintAuditCommandlet(ueInfo));
        Log.Info("BlueprintAudit: Commandlet task dispatched to background");
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
        Log.Info($"BlueprintAudit: Boot check starting (delay={_config.BootCheckDelayMs}ms, maxRetries={_config.BootCheckMaxRetries}, retryInterval={_config.BootCheckRetryIntervalMs}ms)");

        try
        {
            var ueInfo = _ueProject.GetUeProjectInfo();
            if (!ueInfo.IsUnrealProject)
            {
                _bootCheckResult = "Not an Unreal project - skipping boot check";
                _bootCheckCompleted = true;
                Log.Info("BlueprintAudit: " + _bootCheckResult);
                return;
            }

            // Rider may still be indexing on first open, so the engine path
            // (and therefore CommandletExePath) might not be resolved yet.
            // Poll a few times before giving up.
            var retries = 0;
            while (string.IsNullOrEmpty(ueInfo.CommandletExePath) && retries < _config.BootCheckMaxRetries)
            {
                retries++;
                Log.Info($"BlueprintAudit: Engine path not resolved yet, retry {retries}/{_config.BootCheckMaxRetries} in {_config.BootCheckRetryIntervalMs}ms...");
                Thread.Sleep(_config.BootCheckRetryIntervalMs);
                ueInfo = _ueProject.GetUeProjectInfo();
            }

            if (string.IsNullOrEmpty(ueInfo.CommandletExePath))
            {
                _bootCheckResult = $"Engine path not resolved after {retries} retries - skipping boot audit (use /blueprint-audit/refresh once Rider finishes indexing)";
                _bootCheckCompleted = true;
                Log.Warn("BlueprintAudit: " + _bootCheckResult);
                return;
            }

            var uprojectDir = ueInfo.ProjectDirectory;
            var versionDir = Path.Combine(uprojectDir, "Saved", "Fathom", "Audit", $"v{AuditSchemaVersion}");
            var auditDirs = new[]
            {
                Path.Combine(versionDir, "Blueprints"),
                Path.Combine(versionDir, "DataTables"),
                Path.Combine(versionDir, "DataAssets")
            };

            if (!auditDirs.Any(Directory.Exists))
            {
                _bootCheckResult = "Audit directory does not exist - triggering refresh";
                _bootCheckCompleted = true;
                Log.Info("BlueprintAudit: " + _bootCheckResult);
                TriggerRefresh(ueInfo);
                return;
            }

            var staleCount = 0;
            var totalCount = 0;

            foreach (var auditDir in auditDirs)
            {
                if (!Directory.Exists(auditDir)) continue;
                foreach (var mdFile in Directory.GetFiles(auditDir, "*.md", SearchOption.AllDirectories))
                {
                    totalCount++;
                    var entry = ReadAndCheckBlueprintAudit(mdFile, uprojectDir);
                    if (entry.IsStale) staleCount++;
                }
            }

            if (totalCount == 0)
            {
                _bootCheckResult = "No audit files found - triggering refresh";
                _bootCheckCompleted = true;
                Log.Info("BlueprintAudit: " + _bootCheckResult);
                TriggerRefresh(ueInfo);
                return;
            }

            if (staleCount > 0)
            {
                _bootCheckResult = $"Found {staleCount}/{totalCount} stale assets - triggering refresh";
                _bootCheckCompleted = true;
                Log.Info("BlueprintAudit: " + _bootCheckResult);
                TriggerRefresh(ueInfo);
                return;
            }

            _bootCheckResult = $"All {totalCount} assets are fresh - no refresh needed";
            _bootCheckCompleted = true;
            Log.Info("BlueprintAudit: " + _bootCheckResult);
        }
        catch (Exception ex)
        {
            _bootCheckResult = "Boot check failed: " + ex.Message;
            _bootCheckCompleted = true;
            Log.Error(ex, "BlueprintAudit: Boot check failed");
        }
    }

    /// <summary>
    /// Looks up a single audit entry by package path, regardless of staleness.
    /// Returns null if not found or if audit data is not available.
    /// </summary>
    public BlueprintAuditEntry FindAuditEntry(string packagePath)
    {
        var ueInfo = _ueProject.GetUeProjectInfo();
        if (!ueInfo.IsUnrealProject) return null;

        var uprojectDir = ueInfo.ProjectDirectory;
        var versionDir = Path.Combine(uprojectDir, "Saved", "Fathom", "Audit", $"v{AuditSchemaVersion}");

        var normalizedInput = StripObjectName(packagePath);

        var dirsToSearch = new[]
        {
            (Path.Combine(versionDir, "Blueprints"), "Blueprint"),
            (Path.Combine(versionDir, "DataTables"), "DataTable"),
            (Path.Combine(versionDir, "DataAssets"), "DataAsset")
        };

        foreach (var (dir, assetType) in dirsToSearch)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var mdFile in Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories))
            {
                var entry = ReadAndCheckBlueprintAudit(mdFile, uprojectDir);
                entry.AssetType = assetType;
                var entryPackagePath = StripObjectName(entry.Path);
                if (string.Equals(entryPackagePath, normalizedInput, StringComparison.OrdinalIgnoreCase))
                    return entry;
            }
        }

        return null;
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

            Log.Info($"BlueprintAudit: Starting Blueprint audit: {startInfo.FileName} {startInfo.Arguments}");
            Log.Info($"BlueprintAudit: Working directory: {startInfo.WorkingDirectory}");

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

                if (exitCode == 0)
                    Log.Info($"BlueprintAudit: Blueprint audit completed. Exit code: {exitCode}, Missing: {isMissing}");
                else
                    Log.Warn($"BlueprintAudit: Blueprint audit completed. Exit code: {exitCode}, Missing: {isMissing}");
            }
        }
        catch (Exception ex)
        {
            lock (_auditLock)
            {
                _auditProcessError = ex.GetType().Name + ": " + ex.Message;
                _auditRefreshInProgress = false;
            }

            Log.Error(ex, "BlueprintAudit: Blueprint audit failed");
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

    private static BlueprintAuditEntry ReadAndCheckBlueprintAudit(string auditFile, string uprojectDir)
    {
        var entry = new BlueprintAuditEntry { AuditFile = auditFile };

        try
        {
            var content = File.ReadAllText(auditFile);
            entry.AuditContent = content;
            entry.Data = ParseAuditHeader(content);

            entry.Name = entry.Data.TryGetValue("Name", out var name) ? name?.ToString() : null;
            entry.Path = entry.Data.TryGetValue("Path", out var path) ? path?.ToString() : null;
            entry.SourceFileHash = entry.Data.TryGetValue("Hash", out var hash) ? hash?.ToString() : null;

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
                entry.Error = "No Hash in audit file";
            }
        }
        catch (Exception ex)
        {
            entry.Error = ex.GetType().Name + ": " + ex.Message;
        }

        return entry;
    }

    /// <summary>
    /// Strips the ".ObjectName" suffix from a full UE object path.
    /// E.g. "/Game/UI/WBP_Foo.WBP_Foo" becomes "/Game/UI/WBP_Foo".
    /// Returns the input unchanged if there is no dot after the last slash.
    /// </summary>
    private static string StripObjectName(string objectPath)
    {
        if (string.IsNullOrEmpty(objectPath)) return objectPath;
        var lastSlash = objectPath.LastIndexOf('/');
        var dotIndex = objectPath.IndexOf('.', lastSlash >= 0 ? lastSlash : 0);
        if (dotIndex > 0)
            return objectPath.Substring(0, dotIndex);
        return objectPath;
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

    /// <summary>
    /// Parses the header lines from a markdown audit file.
    /// Extracts Name (from the H1 heading), Path, Parent, Type, and Hash.
    /// </summary>
    private static Dictionary<string, object> ParseAuditHeader(string content)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StringReader(content);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            // Stop at the first section heading (## ...)
            if (line.StartsWith("## "))
                break;

            // H1 heading: "# BlueprintName"
            if (line.StartsWith("# ") && !result.ContainsKey("Name"))
            {
                result["Name"] = line.Substring(2).Trim();
                continue;
            }

            // Key: Value lines
            var colonIndex = line.IndexOf(": ", StringComparison.Ordinal);
            if (colonIndex > 0)
            {
                var key = line.Substring(0, colonIndex).Trim();
                var value = line.Substring(colonIndex + 2).Trim();

                // Map "Parent" to "ParentClass" for backward compat with Data dict consumers
                if (key == "Parent")
                    result["ParentClass"] = value;
                else if (key == "Type")
                    result["BlueprintType"] = value;
                else
                    result[key] = value;
            }
        }

        return result;
    }
}
