// =============================================================================
// InspectionHttpServer.cs — HTTP API for code inspections (Active, POC)
// =============================================================================
// GOAL PROGRESS: YES — Enables LLMs and manual testing to trigger inspections
//   via simple HTTP GET requests without relying on broken keyboard shortcuts.
//
// What it does:
//   Starts an HttpListener on localhost:19876 when the solution loads.
//   Exposes endpoints to inspect files, list available files, and check health.
//
// Endpoints:
//   GET /                → List available endpoints
//   GET /health          → Solution info and server status
//   GET /files           → List all user source files (under solution dir)
//   GET /inspect?file=X  → Run InspectCodeDaemon on file(s), return JSON issues
//                          Supports multiple: ?file=a.cpp&file=b.cpp
//                          Add &debug=true for per-file diagnostic info (psiSync, timing)
//
// How it works:
//   Uses System.Net.HttpListener (built into .NET, zero dependencies).
//   For /inspect, finds matching IPsiSourceFile by relative path, then runs
//   InspectCodeDaemon.DoHighlighting() — the same proven mechanism from
//   InspectCodeDaemonExperiment.cs.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Application.Parts;
using JetBrains.DocumentModel;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon.SolutionAnalysis;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.InspectCode;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.Issues;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.Util;
using FileImages = JetBrains.ReSharper.Daemon.SolutionAnalysis.FileImages.FileImages;

namespace ReSharperPlugin.RiderActionExplorer
{
    [SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
    public class InspectionHttpServer
    {
        private const int Port = 19876;
        private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<InspectionHttpServer>();

        private readonly ISolution _solution;
        private readonly Lifetime _lifetime;
        private HttpListener _listener;

        public InspectionHttpServer(Lifetime lifetime, ISolution solution)
        {
            _solution = solution;
            _lifetime = lifetime;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{Port}/");
                _listener.Start();
                Log.Warn($"InspectionHttpServer: listening on http://localhost:{Port}/");

                lifetime.OnTermination(() =>
                {
                    try { _listener.Stop(); } catch { }
                    try { _listener.Close(); } catch { }
                    Log.Warn("InspectionHttpServer: stopped");
                });

                Task.Run(() => AcceptLoop());

                // Write a marker so the user knows the server started
                var markerPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "resharper-http-server.txt");
                File.WriteAllText(markerPath,
                    $"InspectionHttpServer running\n" +
                    $"URL: http://localhost:{Port}/\n" +
                    $"Solution: {solution.SolutionDirectory}\n" +
                    $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"InspectionHttpServer: failed to start on port {Port}");
            }
        }

        private async Task AcceptLoop()
        {
            while (_lifetime.IsAlive && _listener != null && _listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(ctx));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "InspectionHttpServer: error in accept loop");
                }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";

                switch (path)
                {
                    case "":
                    case "/":
                        RespondJson(ctx, 200, BuildIndexResponse());
                        break;
                    case "/health":
                        RespondJson(ctx, 200, BuildHealthResponse());
                        break;
                    case "/files":
                        HandleFiles(ctx);
                        break;
                    case "/inspect":
                        HandleInspect(ctx);
                        break;
                    default:
                        RespondJson(ctx, 404,
                            "{\"error\": \"Not found. Try: /, /health, /files, /inspect?file=path\"}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "InspectionHttpServer: unhandled error in request handler");
                try
                {
                    RespondJson(ctx, 500,
                        "{\"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"}");
                }
                catch { /* give up */ }
            }
        }

        // ── /inspect ──

        private void HandleInspect(HttpListenerContext ctx)
        {
            var fileParams = ctx.Request.QueryString.GetValues("file");
            if (fileParams == null || fileParams.Length == 0)
            {
                RespondJson(ctx, 400,
                    "{\"error\": \"Missing 'file' query parameter. Usage: /inspect?file=Source/Foo.cpp&file=Source/Bar.cpp\"}");
                return;
            }

            var debug = IsDebug(ctx);

            IssueClasses issueClasses;
            FileImages fileImages;
            try
            {
                issueClasses = _solution.GetComponent<IssueClasses>();
                fileImages = FileImages.GetInstance(_solution);
            }
            catch (Exception ex)
            {
                RespondJson(ctx, 500,
                    "{\"error\": \"Failed to get inspection components: " + EscapeJson(ex.Message) + "\"}");
                return;
            }

            var solutionDir = _solution.SolutionDirectory;
            var fileIndex = BuildFileIndex(solutionDir);
            var requestSw = Stopwatch.StartNew();

            var fileResults = new List<string>();
            var totalIssues = 0;

            foreach (var fileParam in fileParams)
            {
                var key = NormalizePath(fileParam);

                IPsiSourceFile sourceFile;
                if (!fileIndex.TryGetValue(key, out sourceFile))
                {
                    fileResults.Add(
                        "{\"file\": \"" + EscapeJson(fileParam) + "\", " +
                        "\"error\": \"File not found in solution\", \"issues\": []}");
                    continue;
                }

                // Step A: Wait for PSI to match disk content before inspecting
                var syncResult = WaitForPsiSync(sourceFile);

                if (syncResult.Status == "timeout")
                {
                    var entry =
                        "{\"file\": \"" + EscapeJson(fileParam) + "\", " +
                        "\"error\": \"PSI sync timeout: document does not match disk content after " +
                        syncResult.WaitedMs + "ms\", " +
                        "\"issues\": []";
                    if (debug)
                        entry += ", \"debug\": " + BuildFileDebugJson(syncResult, 0);
                    entry += "}";
                    fileResults.Add(entry);
                    continue;
                }

                if (syncResult.Status == "disk_read_error")
                {
                    var entry =
                        "{\"file\": \"" + EscapeJson(fileParam) + "\", " +
                        "\"error\": \"Cannot read file from disk: " + EscapeJson(syncResult.Message) + "\", " +
                        "\"issues\": []";
                    if (debug)
                        entry += ", \"debug\": " + BuildFileDebugJson(syncResult, 0);
                    entry += "}";
                    fileResults.Add(entry);
                    continue;
                }

                try
                {
                    var inspectSw = Stopwatch.StartNew();
                    var issues = new List<string>();
                    var daemon = new InspectCodeDaemon(issueClasses, sourceFile, fileImages);
                    daemon.DoHighlighting(DaemonProcessKind.OTHER, issue =>
                    {
                        var severity = issue.GetSeverity().ToString().ToUpperInvariant();
                        var message = issue.Message ?? "";
                        var line = 0;

                        try
                        {
                            var doc = sourceFile.Document;
                            if (doc != null && issue.Range.HasValue)
                            {
                                var offset = issue.Range.Value.StartOffset;
                                if (offset >= 0 && offset <= doc.GetTextLength())
                                    line = (int)new DocumentOffset(doc, offset)
                                        .ToDocumentCoords().Line + 1;
                            }
                        }
                        catch { /* ignore offset errors */ }

                        issues.Add(
                            "{\"severity\": \"" + EscapeJson(severity) + "\", " +
                            "\"line\": " + line + ", " +
                            "\"message\": \"" + EscapeJson(message) + "\"}");
                    });
                    var inspectMs = (int)inspectSw.ElapsedMilliseconds;

                    totalIssues += issues.Count;
                    var relPath = sourceFile.GetLocation()
                        .MakeRelativeTo(solutionDir).ToString().Replace('\\', '/');
                    var entry =
                        "{\"file\": \"" + EscapeJson(relPath) + "\", " +
                        "\"issues\": [" + string.Join(",", issues) + "], " +
                        "\"error\": null";
                    if (debug)
                        entry += ", \"debug\": " + BuildFileDebugJson(syncResult, inspectMs);
                    entry += "}";
                    fileResults.Add(entry);
                }
                catch (Exception ex)
                {
                    var entry =
                        "{\"file\": \"" + EscapeJson(fileParam) + "\", " +
                        "\"issues\": [], " +
                        "\"error\": \"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"";
                    if (debug)
                        entry += ", \"debug\": " + BuildFileDebugJson(syncResult, 0);
                    entry += "}";
                    fileResults.Add(entry);
                }
            }

            var totalMs = (int)requestSw.ElapsedMilliseconds;
            var json =
                "{\"solution\": \"" + EscapeJson(solutionDir.FullPath.Replace('\\', '/')) + "\", " +
                "\"files\": [" + string.Join(",", fileResults) + "], " +
                "\"totalIssues\": " + totalIssues + ", " +
                "\"totalFiles\": " + fileParams.Length;
            if (debug)
                json += ", \"debug\": {\"totalMs\": " + totalMs + "}";
            json += "}";

            RespondJson(ctx, 200, json);
        }

        private static string BuildFileDebugJson(PsiSyncResult sync, int inspectMs)
        {
            var sb = new StringBuilder();
            sb.Append("{\"psiSync\": {\"status\": \"").Append(EscapeJson(sync.Status)).Append("\"");
            sb.Append(", \"waitedMs\": ").Append(sync.WaitedMs);
            sb.Append(", \"attempts\": ").Append(sync.Attempts);
            if (sync.Message != null)
                sb.Append(", \"message\": \"").Append(EscapeJson(sync.Message)).Append("\"");
            sb.Append("}");
            sb.Append(", \"inspectionMs\": ").Append(inspectMs);
            sb.Append("}");
            return sb.ToString();
        }

        // ── /files ──

        private void HandleFiles(HttpListenerContext ctx)
        {
            var solutionDir = _solution.SolutionDirectory;
            var fileIndex = BuildFileIndex(solutionDir);

            var fileEntries = new List<string>();
            foreach (var kvp in fileIndex.OrderBy(x => x.Key))
            {
                var relPath = kvp.Value.GetLocation()
                    .MakeRelativeTo(solutionDir).ToString().Replace('\\', '/');
                var ext = kvp.Value.GetLocation().ExtensionNoDot.ToLowerInvariant();
                var lang = kvp.Value.LanguageType?.Name ?? "unknown";
                fileEntries.Add(
                    "{\"path\": \"" + EscapeJson(relPath) + "\", " +
                    "\"ext\": \"" + EscapeJson(ext) + "\", " +
                    "\"language\": \"" + EscapeJson(lang) + "\"}");
            }

            var json =
                "{\"solution\": \"" + EscapeJson(solutionDir.FullPath.Replace('\\', '/')) + "\", " +
                "\"fileCount\": " + fileEntries.Count + ", " +
                "\"files\": [" + string.Join(",", fileEntries) + "]}";

            RespondJson(ctx, 200, json);
        }

        // ── /health ──

        private string BuildHealthResponse()
        {
            return
                "{\"status\": \"ok\", " +
                "\"solution\": \"" + EscapeJson(_solution.SolutionDirectory.FullPath.Replace('\\', '/')) + "\", " +
                "\"port\": " + Port + "}";
        }

        // ── / ──

        private string BuildIndexResponse()
        {
            return
                "{\"endpoints\": {" +
                "\"GET /\": \"This help message\", " +
                "\"GET /health\": \"Server and solution status\", " +
                "\"GET /files\": \"List all user source files under solution directory\", " +
                "\"GET /inspect?file=path\": \"Run code inspection on file(s). Supports multiple: ?file=a.cpp&file=b.cpp. Paths are relative to solution directory. Add &debug=true for per-file timing and PSI sync diagnostics.\"" +
                "}}";
        }

        // ── File index ──

        private Dictionary<string, IPsiSourceFile> BuildFileIndex(VirtualFileSystemPath solutionDir)
        {
            var index = new Dictionary<string, IPsiSourceFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in _solution.GetAllProjects())
            {
                foreach (var projectFile in project.GetAllProjectFiles())
                {
                    var sourceFile = projectFile.ToSourceFile();
                    if (sourceFile == null) continue;

                    var path = sourceFile.GetLocation();
                    if (!path.StartsWith(solutionDir)) continue;

                    var key = NormalizePath(path.MakeRelativeTo(solutionDir).ToString());
                    if (!index.ContainsKey(key))
                        index[key] = sourceFile;
                }
            }
            return index;
        }

        // ── PSI sync ──

        private const int PsiSyncTimeoutMs = 15000;
        private const int PsiSyncPollIntervalMs = 250;

        /// <summary>
        /// Waits until the PSI document content matches the file on disk.
        /// Returns a diagnostic object with sync status info.
        /// </summary>
        private static PsiSyncResult WaitForPsiSync(IPsiSourceFile sourceFile)
        {
            var diskPath = sourceFile.GetLocation().FullPath;
            var sw = Stopwatch.StartNew();

            // Read disk content, normalize line endings for comparison
            string diskContent;
            try
            {
                diskContent = NormalizeLineEndings(File.ReadAllText(diskPath));
            }
            catch (Exception ex)
            {
                return new PsiSyncResult
                {
                    Status = "disk_read_error",
                    WaitedMs = 0,
                    Message = ex.GetType().Name + ": " + ex.Message
                };
            }

            // Poll until document matches disk or timeout
            var attempts = 0;
            while (sw.ElapsedMilliseconds < PsiSyncTimeoutMs)
            {
                attempts++;
                try
                {
                    var doc = sourceFile.Document;
                    if (doc != null)
                    {
                        var docContent = NormalizeLineEndings(doc.GetText().ToString());
                        if (docContent == diskContent)
                        {
                            return new PsiSyncResult
                            {
                                Status = attempts == 1 ? "synced" : "synced_after_wait",
                                WaitedMs = (int)sw.ElapsedMilliseconds,
                                Attempts = attempts
                            };
                        }
                    }
                }
                catch
                {
                    // Document may be in a transitional state, keep polling
                }

                Thread.Sleep(PsiSyncPollIntervalMs);
            }

            return new PsiSyncResult
            {
                Status = "timeout",
                WaitedMs = (int)sw.ElapsedMilliseconds,
                Attempts = attempts,
                Message = "PSI document did not match disk content within " + PsiSyncTimeoutMs + "ms"
            };
        }

        private static string NormalizeLineEndings(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private class PsiSyncResult
        {
            public string Status;   // "synced", "synced_after_wait", "timeout", "disk_read_error"
            public int WaitedMs;
            public int Attempts;
            public string Message;
        }

        // ── Helpers ──

        /// <summary>
        /// Parses the &amp;debug=true query parameter. Usable on any endpoint.
        /// </summary>
        private static bool IsDebug(HttpListenerContext ctx)
        {
            var val = ctx.Request.QueryString["debug"];
            return val != null &&
                   (val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    val.Equals("1", StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        private static void RespondJson(HttpListenerContext ctx, int statusCode, string json)
        {
            var buffer = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}
