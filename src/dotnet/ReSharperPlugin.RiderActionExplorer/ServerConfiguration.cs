namespace ReSharperPlugin.RiderActionExplorer;

/// <summary>
/// Configuration settings for the InspectionHttpServer.
/// Centralizes all magic numbers and configurable values.
/// </summary>
public class ServerConfiguration
{
        /// <summary>
        /// Default configuration instance.
        /// </summary>
        public static ServerConfiguration Default { get; } = new ServerConfiguration();

        // ── HTTP Server ──

        /// <summary>
        /// Port for the HTTP server (default: 19876).
        /// </summary>
        public int Port { get; set; } = 19876;

        // ── PSI Synchronization ──

        /// <summary>
        /// Maximum time to wait for PSI document to match disk content (default: 15000ms).
        /// </summary>
        public int PsiSyncTimeoutMs { get; set; } = 15000;

        /// <summary>
        /// Interval between PSI sync polling attempts (default: 250ms).
        /// </summary>
        public int PsiSyncPollIntervalMs { get; set; } = 250;

        // ── Code Inspection ──

        /// <summary>
        /// Maximum number of inspection retries on OperationCanceledException (default: 3).
        /// </summary>
        public int MaxInspectionRetries { get; set; } = 3;

        /// <summary>
        /// Delay between inspection retries (default: 1000ms).
        /// </summary>
        public int RetryDelayMs { get; set; } = 1000;

        // ── Blueprint Audit ──

        /// <summary>
        /// Delay before boot check for Blueprint staleness (default: 5000ms).
        /// Allows solution to fully load before checking.
        /// </summary>
        public int BootCheckDelayMs { get; set; } = 5000;

        /// <summary>
        /// Maximum length of process output to include in status responses (default: 2000).
        /// </summary>
        public int MaxOutputLength { get; set; } = 2000;

        /// <summary>
        /// Maximum length of error messages to include in status responses (default: 1000).
        /// </summary>
        public int MaxErrorLength { get; set; } = 1000;

        /// <summary>
        /// Maximum number of stale Blueprint examples to include in error responses (default: 10).
        /// </summary>
        public int MaxStaleExamples { get; set; } = 10;

        // ── Platform-Specific Paths ──

        /// <summary>
        /// Platform subfolder for UE binaries (default: "Win64").
        /// </summary>
        public string PlatformBinaryFolder { get; set; } = "Win64";

        /// <summary>
        /// Name of the UE commandlet executable (default: "UnrealEditor-Cmd.exe").
        /// </summary>
        public string CommandletExecutable { get; set; } = "UnrealEditor-Cmd.exe";

        // ── File Markers ──

        /// <summary>
        /// Name of the marker file written to Desktop on server start.
        /// </summary>
        public string MarkerFileName { get; set; } = "resharper-http-server.txt";
    }
