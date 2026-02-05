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
//   GET /inspect?file=X  → Run InspectCodeDaemon on file(s), return issues
//                          Supports multiple: ?file=a.cpp&file=b.cpp
//                          &format=json for JSON (default is markdown)
//                          &debug=true  for per-file diagnostic info (psiSync, timing)
//
// How it works:
//   Uses System.Net.HttpListener (built into .NET, zero dependencies).
//   For /inspect, finds matching IPsiSourceFile by relative path, then runs
//   InspectCodeDaemon.DoHighlighting() — the same proven mechanism from
//   InspectCodeDaemonExperiment.cs.
//   Default output is markdown (LLM-friendly). Use &format=json for structured data.
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
using JetBrains.ReSharper.Psi.Files;
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
                        Respond(ctx, 200, "application/json; charset=utf-8", BuildIndexResponse());
                        break;
                    case "/health":
                        Respond(ctx, 200, "application/json; charset=utf-8", BuildHealthResponse());
                        break;
                    case "/files":
                        HandleFiles(ctx);
                        break;
                    case "/inspect":
                        HandleInspect(ctx);
                        break;
                    default:
                        Respond(ctx, 404, "text/plain",
                            "Not found. Try: /, /health, /files, /inspect?file=path");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "InspectionHttpServer: unhandled error in request handler");
                try
                {
                    Respond(ctx, 500, "text/plain", ex.GetType().Name + ": " + ex.Message);
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
                Respond(ctx, 400, "text/plain", "Missing 'file' query parameter.\nUsage: /inspect?file=Source/Foo.cpp&file=Source/Bar.cpp");
                return;
            }

            var debug = IsDebug(ctx);
            var format = GetFormat(ctx);

            IssueClasses issueClasses;
            FileImages fileImages;
            try
            {
                issueClasses = _solution.GetComponent<IssueClasses>();
                fileImages = FileImages.GetInstance(_solution);
            }
            catch (Exception ex)
            {
                Respond(ctx, 500, "text/plain", "Failed to get inspection components: " + ex.Message);
                return;
            }

            var solutionDir = _solution.SolutionDirectory;
            var fileIndex = BuildFileIndex(solutionDir);
            var requestSw = Stopwatch.StartNew();

            var results = new List<FileResult>();

            foreach (var fileParam in fileParams)
            {
                var key = NormalizePath(fileParam);
                var result = new FileResult { RequestedPath = fileParam };

                IPsiSourceFile sourceFile;
                if (!fileIndex.TryGetValue(key, out sourceFile))
                {
                    result.Error = "File not found in solution";
                    results.Add(result);
                    continue;
                }

                result.ResolvedPath = sourceFile.GetLocation()
                    .MakeRelativeTo(solutionDir).ToString().Replace('\\', '/');

                // Step A: Wait for PSI to match disk content before inspecting
                result.SyncResult = WaitForPsiSync(sourceFile);

                if (result.SyncResult.Status == "timeout")
                {
                    result.Error = "PSI sync timeout: document does not match disk content after " +
                                   result.SyncResult.WaitedMs + "ms";
                    results.Add(result);
                    continue;
                }

                if (result.SyncResult.Status == "disk_read_error")
                {
                    result.Error = "Cannot read file from disk: " + result.SyncResult.Message;
                    results.Add(result);
                    continue;
                }

                // Step B: Commit pending document changes into PSI tree
                // (runs after PSI sync so the updated document is ready to flush)
                try
                {
                    _solution.GetPsiServices().Files.CommitAllDocuments();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "InspectionHttpServer: CommitAllDocuments failed");
                }

                // Step C: Run inspection with retry on OperationCanceledException
                var inspectSw = Stopwatch.StartNew();
                for (var attempt = 1; attempt <= MaxInspectionRetries; attempt++)
                {
                    result.Retries = attempt - 1;
                    result.Issues.Clear();
                    result.Error = null;

                    try
                    {
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

                            result.Issues.Add(new IssueInfo
                            {
                                Severity = severity,
                                Line = line,
                                Message = message
                            });
                        });

                        // Success — break out of retry loop
                        break;
                    }
                    catch (OperationCanceledException) when (attempt < MaxInspectionRetries)
                    {
                        // PSI may still be settling — wait and retry
                        Log.Warn("InspectionHttpServer: OperationCanceledException on " +
                                 result.ResolvedPath + ", retry " + attempt + "/" + MaxInspectionRetries);
                        Thread.Sleep(RetryDelayMs);

                        // Re-commit documents before retry
                        try { _solution.GetPsiServices().Files.CommitAllDocuments(); }
                        catch { /* ignore */ }
                    }
                    catch (Exception ex)
                    {
                        result.Error = ex.GetType().Name + ": " + ex.Message;
                        break;
                    }
                }
                result.InspectionMs = (int)inspectSw.ElapsedMilliseconds;

                results.Add(result);
            }

            var totalMs = (int)requestSw.ElapsedMilliseconds;

            if (format == "json")
                RespondInspectJson(ctx, solutionDir, results, totalMs, debug);
            else
                RespondInspectMarkdown(ctx, results, totalMs, debug);
        }

        // ── /inspect formatters ──

        private static void RespondInspectMarkdown(
            HttpListenerContext ctx, List<FileResult> results, int totalMs, bool debug)
        {
            var sb = new StringBuilder();
            var totalIssues = results.Sum(r => r.Issues.Count);

            foreach (var r in results)
            {
                var displayPath = r.ResolvedPath ?? r.RequestedPath;

                if (r.Error != null)
                {
                    sb.Append("## ").Append(displayPath).AppendLine(" (error)");
                    sb.Append("**Error:** ").AppendLine(r.Error);
                }
                else if (r.Issues.Count == 0)
                {
                    sb.Append("## ").Append(displayPath).AppendLine(" (0 issues)");
                }
                else
                {
                    sb.Append("## ").Append(displayPath)
                        .Append(" (").Append(r.Issues.Count)
                        .Append(r.Issues.Count == 1 ? " issue)" : " issues)")
                        .AppendLine();
                    for (var i = 0; i < r.Issues.Count; i++)
                    {
                        var issue = r.Issues[i];
                        sb.Append(i + 1).Append(". ")
                            .Append(issue.Message)
                            .Append(" [").Append(issue.Severity).Append("]")
                            .Append(" (line ").Append(issue.Line).Append(")")
                            .AppendLine();
                    }
                }

                if (debug && r.SyncResult != null)
                {
                    sb.Append("> Debug: PSI ").Append(r.SyncResult.Status);
                    if (r.SyncResult.WaitedMs > 0)
                        sb.Append(" (").Append(r.SyncResult.WaitedMs).Append("ms, ")
                            .Append(r.SyncResult.Attempts).Append(" attempts)");
                    sb.Append(" | Inspection: ").Append(r.InspectionMs).Append("ms");
                    if (r.Retries > 0)
                        sb.Append(" | Retries: ").Append(r.Retries);
                    sb.AppendLine();
                }

                sb.AppendLine();
            }

            if (debug)
            {
                sb.Append("---").AppendLine();
                sb.Append("Total: ").Append(totalIssues).Append(" issues across ")
                    .Append(results.Count).Append(" files in ")
                    .Append(totalMs).Append("ms").AppendLine();
            }

            Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
        }

        private void RespondInspectJson(
            HttpListenerContext ctx, VirtualFileSystemPath solutionDir,
            List<FileResult> results, int totalMs, bool debug)
        {
            var totalIssues = results.Sum(r => r.Issues.Count);
            var fileJsons = new List<string>();

            foreach (var r in results)
            {
                var displayPath = r.ResolvedPath ?? r.RequestedPath;
                var issueJsons = r.Issues.Select(issue =>
                    "{\"severity\": \"" + EscapeJson(issue.Severity) + "\", " +
                    "\"line\": " + issue.Line + ", " +
                    "\"message\": \"" + EscapeJson(issue.Message) + "\"}");

                var entry =
                    "{\"file\": \"" + EscapeJson(displayPath) + "\", " +
                    "\"issues\": [" + string.Join(",", issueJsons) + "], " +
                    "\"error\": " + (r.Error != null
                        ? "\"" + EscapeJson(r.Error) + "\""
                        : "null");
                if (debug && r.SyncResult != null)
                    entry += ", \"debug\": " + BuildFileDebugJson(r.SyncResult, r.InspectionMs, r.Retries);
                entry += "}";
                fileJsons.Add(entry);
            }

            var json =
                "{\"solution\": \"" + EscapeJson(solutionDir.FullPath.Replace('\\', '/')) + "\", " +
                "\"files\": [" + string.Join(",", fileJsons) + "], " +
                "\"totalIssues\": " + totalIssues + ", " +
                "\"totalFiles\": " + results.Count;
            if (debug)
                json += ", \"debug\": {\"totalMs\": " + totalMs + "}";
            json += "}";

            Respond(ctx, 200, "application/json; charset=utf-8", json);
        }

        private static string BuildFileDebugJson(PsiSyncResult sync, int inspectMs, int retries)
        {
            var sb = new StringBuilder();
            sb.Append("{\"psiSync\": {\"status\": \"").Append(EscapeJson(sync.Status)).Append("\"");
            sb.Append(", \"waitedMs\": ").Append(sync.WaitedMs);
            sb.Append(", \"attempts\": ").Append(sync.Attempts);
            if (sync.Message != null)
                sb.Append(", \"message\": \"").Append(EscapeJson(sync.Message)).Append("\"");
            sb.Append("}");
            sb.Append(", \"inspectionMs\": ").Append(inspectMs);
            sb.Append(", \"retries\": ").Append(retries);
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

            Respond(ctx, 200, "application/json; charset=utf-8", json);
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
                "\"GET /inspect?file=path\": \"Run code inspection on file(s). Multiple: &file=a&file=b. Default output is markdown; add &format=json for JSON. Add &debug=true for diagnostics.\"" +
                "}}";
        }

        // ── Result data structures ──

        private class FileResult
        {
            public string RequestedPath;
            public string ResolvedPath;
            public string Error;
            public List<IssueInfo> Issues = new List<IssueInfo>();
            public PsiSyncResult SyncResult;
            public int InspectionMs;
            public int Retries;     // 0 = succeeded first try, 1+ = retried after OperationCanceledException
        }

        private class IssueInfo
        {
            public string Severity;
            public int Line;
            public string Message;
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
        private const int MaxInspectionRetries = 3;
        private const int RetryDelayMs = 1000;

        private static PsiSyncResult WaitForPsiSync(IPsiSourceFile sourceFile)
        {
            var diskPath = sourceFile.GetLocation().FullPath;
            var sw = Stopwatch.StartNew();

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
            public string Status;
            public int WaitedMs;
            public int Attempts;
            public string Message;
        }

        // ── Helpers ──

        private static bool IsDebug(HttpListenerContext ctx)
        {
            var val = ctx.Request.QueryString["debug"];
            return val != null &&
                   (val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    val.Equals("1", StringComparison.OrdinalIgnoreCase));
        }

        private static string GetFormat(HttpListenerContext ctx)
        {
            var val = ctx.Request.QueryString["format"];
            if (val != null && val.Equals("json", StringComparison.OrdinalIgnoreCase))
                return "json";
            return "md";
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

        private static void Respond(HttpListenerContext ctx, int statusCode, string contentType, string body)
        {
            var buffer = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = buffer.Length;
            ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}
