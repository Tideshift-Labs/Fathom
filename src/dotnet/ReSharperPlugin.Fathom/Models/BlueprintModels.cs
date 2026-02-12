using System.Collections.Generic;

namespace ReSharperPlugin.Fathom.Models;

/// <summary>
/// A Blueprint class derived from a C++ base class.
/// Used by the /blueprints endpoint.
/// </summary>
public class BlueprintClassInfo
{
        /// <summary>
        /// Name of the Blueprint class (e.g., "BP_MyActor").
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// File path relative to solution directory.
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// UE package path (e.g., "/Game/UI/Widgets/WBP_MyWidget").
        /// </summary>
        public string PackagePath { get; set; }

        /// <summary>
        /// Link to the /bp composite endpoint for this Blueprint.
        /// </summary>
        public string MoreInfoUrl { get; set; }
    }

    /// <summary>
    /// An entry from the Blueprint audit system.
    /// Contains metadata about a Blueprint and its staleness status.
    /// </summary>
    public class BlueprintAuditEntry
    {
        /// <summary>
        /// Name of the Blueprint (e.g., "WBP_MainMenu").
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// UE package path (e.g., "/Game/UI/Widgets/WBP_MainMenu").
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// Full path to the audit .md file.
        /// </summary>
        public string AuditFile { get; set; }

        /// <summary>
        /// MD5 hash of the .uasset file when the audit was generated.
        /// </summary>
        public string SourceFileHash { get; set; }

        /// <summary>
        /// Current MD5 hash of the .uasset file.
        /// </summary>
        public string CurrentFileHash { get; set; }

        /// <summary>
        /// True if SourceFileHash != CurrentFileHash (audit is out of date).
        /// </summary>
        public bool IsStale { get; set; }

        /// <summary>
        /// True if we could not compute the current hash (file not found, etc.).
        /// </summary>
        public bool HashCheckFailed { get; set; }

        /// <summary>
        /// Error message if something went wrong reading or parsing the audit file.
        /// </summary>
        public string Error { get; set; }

        /// <summary>
        /// Header fields parsed from the audit file (Name, Path, ParentClass, BlueprintType).
        /// </summary>
        public Dictionary<string, object> Data { get; set; }

        /// <summary>
        /// Raw markdown content of the audit file.
        /// </summary>
        public string AuditContent { get; set; }
    }

    /// <summary>
    /// Response model for the /blueprints endpoint.
    /// </summary>
    public class BlueprintQueryResult
    {
        /// <summary>
        /// The C++ class name that was queried.
        /// </summary>
        public string ClassName { get; set; }

        /// <summary>
        /// Whether the deferred cache has completed building.
        /// </summary>
        public bool CacheReady { get; set; }

        /// <summary>
        /// Human-readable cache status ("ready", "building", "unknown").
        /// </summary>
        public string CacheStatus { get; set; }

        /// <summary>
        /// Total number of derived Blueprint classes found.
        /// </summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// List of derived Blueprint classes.
        /// </summary>
        public List<BlueprintClassInfo> Blueprints { get; } = new List<BlueprintClassInfo>();

        /// <summary>
        /// Debug information (only populated when debug=true).
        /// </summary>
        public string DebugInfo { get; set; }
    }

    /// <summary>
    /// Response model for the /blueprint-audit endpoint.
    /// </summary>
    public class BlueprintAuditResult
    {
        /// <summary>
        /// Status: "fresh", "stale", "not_ready", "commandlet_missing".
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Human-readable message describing the status.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Total number of Blueprint audit entries.
        /// </summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// Number of stale entries (when status is "stale").
        /// </summary>
        public int StaleCount { get; set; }

        /// <summary>
        /// Number of entries with errors.
        /// </summary>
        public int ErrorCount { get; set; }

        /// <summary>
        /// Suggested action to resolve the current status.
        /// </summary>
        public string Action { get; set; }

        /// <summary>
        /// Timestamp of last successful refresh.
        /// </summary>
        public string LastRefresh { get; set; }

        /// <summary>
        /// List of Blueprint audit entries (only populated when fresh).
        /// </summary>
        public List<BlueprintAuditEntry> Blueprints { get; set; }

        /// <summary>
        /// Examples of stale entries (when status is "stale", first 10).
        /// </summary>
        public List<BlueprintAuditEntry> StaleExamples { get; set; }
    }

    /// <summary>
    /// Status of the Blueprint audit refresh process.
    /// </summary>
    public class BlueprintAuditStatus
    {
        /// <summary>
        /// True if a refresh is currently in progress.
        /// </summary>
        public bool InProgress { get; set; }

        /// <summary>
        /// True if the commandlet is not installed.
        /// </summary>
        public bool CommandletMissing { get; set; }

        /// <summary>
        /// True if the boot check has completed.
        /// </summary>
        public bool BootCheckCompleted { get; set; }

        /// <summary>
        /// Result of the boot check (e.g., "All 42 blueprints are fresh").
        /// </summary>
        public string BootCheckResult { get; set; }

        /// <summary>
        /// Timestamp of last successful refresh.
        /// </summary>
        public string LastRefresh { get; set; }

        /// <summary>
        /// Exit code from the last commandlet run.
        /// </summary>
        public int? LastExitCode { get; set; }

        /// <summary>
        /// Truncated stdout from the last commandlet run.
        /// </summary>
        public string Output { get; set; }

        /// <summary>
        /// Error message from the last commandlet run.
        /// </summary>
        public string Error { get; set; }
    }
