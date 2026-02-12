using System.Collections.Generic;

namespace ReSharperPlugin.Fathom.Models;

/// <summary>
/// Result of inspecting a single file via the /inspect endpoint.
/// </summary>
public class FileInspectionResult
{
        /// <summary>
        /// The path as provided in the request (may be relative or partial).
        /// </summary>
        public string RequestedPath { get; set; }

        /// <summary>
        /// The resolved path relative to solution directory (normalized with forward slashes).
        /// Null if file was not found.
        /// </summary>
        public string ResolvedPath { get; set; }

        /// <summary>
        /// Error message if inspection failed. Null on success.
        /// </summary>
        public string Error { get; set; }

        /// <summary>
        /// List of issues found during inspection.
        /// </summary>
        public List<InspectionIssue> Issues { get; } = new List<InspectionIssue>();

        /// <summary>
        /// PSI synchronization result (document vs disk content).
        /// </summary>
        public PsiSyncResult SyncResult { get; set; }

        /// <summary>
        /// Time spent running the inspection in milliseconds.
        /// </summary>
        public int InspectionMs { get; set; }

        /// <summary>
        /// Number of retries after OperationCanceledException (0 = succeeded first try).
        /// </summary>
        public int Retries { get; set; }
    }

    /// <summary>
    /// A single issue found during code inspection.
    /// </summary>
    public class InspectionIssue
    {
        /// <summary>
        /// Severity level (ERROR, WARNING, SUGGESTION, HINT).
        /// </summary>
        public string Severity { get; set; }

        /// <summary>
        /// 1-based line number where the issue occurs.
        /// </summary>
        public int Line { get; set; }

        /// <summary>
        /// Human-readable description of the issue.
        /// </summary>
        public string Message { get; set; }
    }

    /// <summary>
    /// Result of waiting for PSI document to synchronize with disk content.
    /// </summary>
    public class PsiSyncResult
    {
        /// <summary>
        /// Status of the sync operation:
        /// - "synced": Document matched disk on first check
        /// - "synced_after_wait": Document matched after polling
        /// - "timeout": Document did not match within timeout
        /// - "disk_read_error": Could not read file from disk
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Total milliseconds spent waiting for sync.
        /// </summary>
        public int WaitedMs { get; set; }

        /// <summary>
        /// Number of polling attempts made.
        /// </summary>
        public int Attempts { get; set; }

        /// <summary>
        /// Additional message (e.g., error details for failures).
        /// </summary>
        public string Message { get; set; }
    }

    public class InspectResponse
    {
        public string Solution { get; set; }
        public List<FileInspectionResult> Files { get; set; }
        public int TotalIssues { get; set; }
        public int TotalFiles { get; set; }
        public InspectDebugInfo Debug { get; set; }
    }

    public class InspectDebugInfo
    {
        public int TotalMs { get; set; }
    }
