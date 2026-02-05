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
//   GET /blueprints?class=X → List UE5 Blueprint classes derived from a C++ class
//                              Uses reflection to access UE4AssetsCache (no DLL ref needed)
//                              &format=json for JSON (default is markdown)
//
// How it works:
//   Uses System.Net.HttpListener (built into .NET, zero dependencies).
//   For /inspect, finds matching IPsiSourceFile by relative path, then runs
//   InspectCodeDaemon.DoHighlighting() — the same proven mechanism from
//   InspectCodeDaemonExperiment.cs.
//   Default output is markdown (LLM-friendly). Use &format=json for structured data.
// =============================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
using ReSharperPlugin.RiderActionExplorer.Models;
using ReSharperPlugin.RiderActionExplorer.Serialization;
using FileImages = JetBrains.ReSharper.Daemon.SolutionAnalysis.FileImages.FileImages;

namespace ReSharperPlugin.RiderActionExplorer
{
    [SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
    public class InspectionHttpServer
    {
        private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<InspectionHttpServer>();

        private readonly ISolution _solution;
        private readonly Lifetime _lifetime;
        private readonly ServerConfiguration _config;
        private HttpListener _listener;

        public InspectionHttpServer(Lifetime lifetime, ISolution solution)
            : this(lifetime, solution, ServerConfiguration.Default)
        {
        }

        /// <summary>
        /// Constructor with explicit configuration (for testing).
        /// </summary>
        public InspectionHttpServer(Lifetime lifetime, ISolution solution, ServerConfiguration config)
        {
            _solution = solution;
            _lifetime = lifetime;
            _config = config;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_config.Port}/");
                _listener.Start();
                Log.Warn($"InspectionHttpServer: listening on http://localhost:{_config.Port}/");

                lifetime.OnTermination(() =>
                {
                    try { _listener.Stop(); } catch { }
                    try { _listener.Close(); } catch { }
                    Log.Warn("InspectionHttpServer: stopped");
                });

                _ = Task.Run(() => AcceptLoopAsync());

                // Write a marker so the user knows the server started
                var markerPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    _config.MarkerFileName);
                File.WriteAllText(markerPath,
                    $"InspectionHttpServer running\n" +
                    $"URL: http://localhost:{_config.Port}/\n" +
                    $"Solution: {solution.SolutionDirectory}\n" +
                    $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");

                // Schedule on-boot staleness check (delayed to allow solution to fully load)
                // Only runs for Unreal Engine projects
                _ = Task.Run(async () =>
                {
                    await Task.Delay(_config.BootCheckDelayMs); // Wait for solution to settle
                    if (IsUnrealProject())
                    {
                        CheckAndRefreshOnBoot();
                    }
                    else
                    {
                        _bootCheckCompleted = true;
                        _bootCheckResult = "Not an Unreal Engine project - Blueprint audit not applicable";
                        Log.Info("InspectionHttpServer: Not a UE project, skipping Blueprint boot check");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"InspectionHttpServer: failed to start on port {_config.Port}");
            }
        }

        private async Task AcceptLoopAsync()
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
                    case "/blueprints":
                        HandleBlueprints(ctx);
                        break;
                    case "/ue-project":
                        HandleUEProjectDiagnostics(ctx);
                        break;
                    case "/blueprint-audit":
                        HandleBlueprintAudit(ctx);
                        break;
                    case "/blueprint-audit/refresh":
                        HandleBlueprintAuditRefresh(ctx);
                        break;
                    case "/blueprint-audit/status":
                        HandleBlueprintAuditStatus(ctx);
                        break;
                    default:
                        Respond(ctx, 404, "text/plain",
                            "Not found. Available endpoints:\n" +
                            "  /              - List all endpoints\n" +
                            "  /health        - Server status\n" +
                            "  /files         - List source files\n" +
                            "  /inspect?file= - Code inspection\n" +
                            "  /blueprints?class= - [UE5] Find derived Blueprints\n" +
                            "  /blueprint-audit   - [UE5] Blueprint audit data\n" +
                            "  /ue-project        - [UE5] Project detection diagnostics");
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

            // Resolve all files and pair with their source files
            var workItems = new List<(FileInspectionResult result, IPsiSourceFile source)>();
            var notFoundResults = new List<FileInspectionResult>();

            foreach (var fileParam in fileParams)
            {
                var key = NormalizePath(fileParam);
                var result = new FileInspectionResult { RequestedPath = fileParam };

                IPsiSourceFile sourceFile;
                if (!fileIndex.TryGetValue(key, out sourceFile))
                {
                    result.Error = "File not found in solution";
                    notFoundResults.Add(result);
                    continue;
                }

                result.ResolvedPath = sourceFile.GetLocation()
                    .MakeRelativeTo(solutionDir).ToString().Replace('\\', '/');
                workItems.Add((result, sourceFile));
            }

            // If ALL files are not found, fail early
            if (workItems.Count == 0)
            {
                var earlyTotalMs = (int)requestSw.ElapsedMilliseconds;
                if (format == "json")
                    RespondInspectJson(ctx, solutionDir, notFoundResults, earlyTotalMs, debug);
                else
                    RespondInspectMarkdown(ctx, notFoundResults, earlyTotalMs, debug);
                return;
            }

            // Step A: Wait for PSI sync on all files in parallel
            var psiTimeoutMs = _config.PsiSyncTimeoutMs;
            var psiPollMs = _config.PsiSyncPollIntervalMs;
            Parallel.ForEach(workItems, item =>
            {
                item.result.SyncResult = WaitForPsiSync(item.source, psiTimeoutMs, psiPollMs);
                if (item.result.SyncResult.Status == "timeout")
                    item.result.Error = "PSI sync timeout: document does not match disk content after " +
                                        item.result.SyncResult.WaitedMs + "ms";
                else if (item.result.SyncResult.Status == "disk_read_error")
                    item.result.Error = "Cannot read file from disk: " + item.result.SyncResult.Message;
            });

            // Step B (CommitAllDocuments) removed — requires main thread, can't run from
            // HTTP handler's ThreadPool. Steps A + C are sufficient: A ensures document
            // matches disk, C retries on OperationCanceledException if PSI is still settling.

            // Step C: Run inspections in parallel with retry on OperationCanceledException
            var inspectableItems = workItems.Where(item => item.result.Error == null).ToList();
            var maxRetries = _config.MaxInspectionRetries;
            var retryDelayMs = _config.RetryDelayMs;
            Parallel.ForEach(inspectableItems, item =>
            {
                var result = item.result;
                var sourceFile = item.source;

                var inspectSw = Stopwatch.StartNew();
                for (var attempt = 1; attempt <= maxRetries; attempt++)
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

                            result.Issues.Add(new InspectionIssue
                            {
                                Severity = severity,
                                Line = line,
                                Message = message
                            });
                        });

                        break;
                    }
                    catch (OperationCanceledException) when (attempt < maxRetries)
                    {
                        Log.Warn("InspectionHttpServer: OperationCanceledException on " +
                                 result.ResolvedPath + ", retry " + attempt + "/" + maxRetries);
                        Thread.Sleep(retryDelayMs);
                    }
                    catch (Exception ex)
                    {
                        result.Error = ex.GetType().Name + ": " + ex.Message;
                        break;
                    }
                }
                result.InspectionMs = (int)inspectSw.ElapsedMilliseconds;
            });

            // Combine results in original request order
            var resultsByPath = workItems.Select(w => w.result)
                .Concat(notFoundResults)
                .ToDictionary(r => r.RequestedPath);
            var results = fileParams.Select(fp => resultsByPath[fp]).ToList();

            var totalMs = (int)requestSw.ElapsedMilliseconds;

            if (format == "json")
                RespondInspectJson(ctx, solutionDir, results, totalMs, debug);
            else
                RespondInspectMarkdown(ctx, results, totalMs, debug);
        }

        // ── /inspect formatters ──

        private static void RespondInspectMarkdown(
            HttpListenerContext ctx, List<FileInspectionResult> results, int totalMs, bool debug)
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
            List<FileInspectionResult> results, int totalMs, bool debug)
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

        // ── /blueprints ──

        private void HandleBlueprints(HttpListenerContext ctx)
        {
            var className = ctx.Request.QueryString["class"];
            if (string.IsNullOrWhiteSpace(className))
            {
                Respond(ctx, 400, "text/plain",
                    "Missing 'class' query parameter.\n" +
                    "Usage: /blueprints?class=AMyActor\n" +
                    "Add &format=json for JSON output. Add &debug=true for diagnostics.");
                return;
            }

            var format = GetFormat(ctx);
            var debug = IsDebug(ctx);

            // Resolve UE4AssetsCache via reflection
            object assetsCache;
            try
            {
                assetsCache = ResolveUE4AssetsCache();
            }
            catch (Exception ex)
            {
                Respond(ctx, 501, "text/plain",
                    "UE4 Blueprint cache is not available. This feature requires a UE5 C++ project open in Rider.\n" +
                    "Detail: " + ex.Message);
                return;
            }

            // Check cache readiness via DeferredCacheController
            bool cacheReady;
            string cacheStatus;
            try
            {
                cacheReady = CheckCacheReadiness();
                cacheStatus = cacheReady ? "ready" : "building";
            }
            catch
            {
                cacheReady = false;
                cacheStatus = "unknown";
            }

            // Query derived blueprints via reflection
            List<BlueprintClassInfo> blueprints;
            string debugInfo = null;
            var solutionDir = _solution.SolutionDirectory;
            try
            {
                blueprints = QueryDerivedBlueprints(className, assetsCache, solutionDir, debug, out debugInfo);
            }
            catch (Exception ex)
            {
                Respond(ctx, 500, "text/plain",
                    "Reflection error querying Blueprint classes.\n" +
                    "This may indicate a Rider API change.\n" +
                    "Detail: " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            if (format == "json")
                RespondBlueprintsJson(ctx, className, cacheReady, cacheStatus, blueprints, debug, debugInfo);
            else
                RespondBlueprintsMarkdown(ctx, className, cacheReady, cacheStatus, blueprints, debug, debugInfo);
        }

        /// Resolve a component from the solution container by Type, handling extension methods.
        private object ResolveComponent(Type componentType)
        {
            // Strategy 1: Look for instance GetComponent<T>() on solution interfaces
            MethodInfo getComponentMethod = null;
            foreach (var iface in _solution.GetType().GetInterfaces())
            {
                getComponentMethod = iface.GetMethod("GetComponent",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getComponentMethod != null && getComponentMethod.IsGenericMethodDefinition)
                    break;
                getComponentMethod = null;
            }

            // Strategy 2: Search concrete type hierarchy
            if (getComponentMethod == null)
            {
                for (var type = _solution.GetType(); type != null; type = type.BaseType)
                {
                    getComponentMethod = type.GetMethod("GetComponent",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, Type.EmptyTypes, null);
                    if (getComponentMethod != null && getComponentMethod.IsGenericMethodDefinition)
                        break;
                    getComponentMethod = null;
                }
            }

            if (getComponentMethod != null)
            {
                var gm = getComponentMethod.MakeGenericMethod(componentType);
                return gm.Invoke(_solution, null);
            }

            // Strategy 3: Find the static extension method in loaded assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name ?? "";
                if (!asmName.Contains("JetBrains")) continue;
                try
                {
                    foreach (var t in asm.GetExportedTypes())
                    {
                        if (!t.IsAbstract || !t.IsSealed) continue; // static classes
                        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (m.Name != "GetComponent" || !m.IsGenericMethodDefinition) continue;
                            var ps = m.GetParameters();
                            if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(_solution.GetType()))
                            {
                                var gm = m.MakeGenericMethod(componentType);
                                return gm.Invoke(null, new object[] { _solution });
                            }
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        private object ResolveUE4AssetsCache()
        {
            // Find the UE4AssetsCache type from loaded assemblies — try several known names
            var candidateNames = new[]
            {
                "JetBrains.ReSharper.Feature.Services.Cpp.Caches.UE4AssetsCache",
                "JetBrains.ReSharper.Feature.Services.Cpp.UE4.Caches.UE4AssetsCache",
                "JetBrains.ReSharper.Features.Cpp.Caches.UE4AssetsCache",
                "JetBrains.ReSharper.Plugins.Unreal.Caches.UE4AssetsCache",
            };

            Type assetsCacheType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var candidate in candidateNames)
                {
                    try
                    {
                        assetsCacheType = asm.GetType(candidate);
                        if (assetsCacheType != null) break;
                    }
                    catch { }
                }
                if (assetsCacheType != null) break;
            }

            // If still not found, search by short name across all types in Cpp-related assemblies
            if (assetsCacheType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var asmName = asm.GetName().Name ?? "";
                    if (!asmName.Contains("Cpp") && !asmName.Contains("Unreal") && !asmName.Contains("UE"))
                        continue;
                    try
                    {
                        foreach (var t in asm.GetExportedTypes())
                        {
                            if (t.Name == "UE4AssetsCache" || t.Name == "UnrealAssetsCache" ||
                                t.Name == "UEAssetsCache" || t.Name == "BlueprintAssetsCache")
                            {
                                assetsCacheType = t;
                                break;
                            }
                        }
                    }
                    catch { }
                    if (assetsCacheType != null) break;
                }
            }

            if (assetsCacheType == null)
            {
                // Build diagnostic info
                var diag = new StringBuilder();
                diag.AppendLine("Type not found. Diagnostics:");
                diag.AppendLine();
                diag.AppendLine("== Assemblies containing 'Cpp', 'Unreal', or 'UE' ==");
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var asmName = asm.GetName().Name ?? "";
                    if (asmName.Contains("Cpp") || asmName.Contains("Unreal") || asmName.Contains("UE"))
                        diag.AppendLine("  " + asm.GetName().FullName);
                }
                diag.AppendLine();
                diag.AppendLine("== Types containing 'Asset' or 'Blueprint' in those assemblies ==");
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var asmName = asm.GetName().Name ?? "";
                    if (!asmName.Contains("Cpp") && !asmName.Contains("Unreal") && !asmName.Contains("UE"))
                        continue;
                    try
                    {
                        foreach (var t in asm.GetExportedTypes())
                        {
                            if (t.Name.Contains("Asset") || t.Name.Contains("Blueprint") || t.Name.Contains("UE4"))
                                diag.AppendLine("  " + t.FullName);
                        }
                    }
                    catch (Exception ex)
                    {
                        diag.AppendLine("  [" + asmName + ": GetExportedTypes() threw " + ex.GetType().Name + "]");
                    }
                }
                throw new InvalidOperationException(diag.ToString());
            }

            var componentResult = ResolveComponent(assetsCacheType);
            if (componentResult == null)
            {
                // Diagnostics: show what's available on the solution
                var diag = new StringBuilder();
                diag.AppendLine("ResolveComponent returned null for " + assetsCacheType.FullName);
                diag.AppendLine();
                diag.AppendLine("Solution type: " + _solution.GetType().FullName);
                diag.AppendLine();
                diag.AppendLine("== Interfaces on solution ==");
                foreach (var iface in _solution.GetType().GetInterfaces())
                    diag.AppendLine("  " + iface.FullName);
                diag.AppendLine();
                diag.AppendLine("== Methods containing 'Component' or 'Resolve' on solution type ==");
                foreach (var m in _solution.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name.Contains("Component") || m.Name.Contains("Resolve") || m.Name.Contains("GetInstance"))
                        diag.AppendLine("  " + m.DeclaringType?.Name + "." + m.Name +
                            "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")" +
                            (m.IsGenericMethodDefinition ? " [generic]" : ""));
                }
                throw new InvalidOperationException(diag.ToString());
            }

            return componentResult;
        }

        private bool CheckCacheReadiness()
        {
            // Find DeferredCacheController type
            Type controllerType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    controllerType = asm.GetType("JetBrains.ReSharper.Feature.Services.DeferredCaches.DeferredCacheController");
                    if (controllerType != null) break;
                }
                catch { }
            }

            if (controllerType == null) return false;

            // Get the controller instance
            var controller = ResolveComponent(controllerType);
            if (controller == null) return false;

            // Read CompletedOnce property
            var completedOnceProp = controllerType.GetProperty("CompletedOnce",
                BindingFlags.Public | BindingFlags.Instance);
            if (completedOnceProp == null) return false;

            var completedOnceObj = completedOnceProp.GetValue(controller);
            if (completedOnceObj == null) return false;

            // CompletedOnce is IProperty<bool> — read .Value
            var valueProp = completedOnceObj.GetType().GetProperty("Value",
                BindingFlags.Public | BindingFlags.Instance);
            if (valueProp == null) return false;

            var value = valueProp.GetValue(completedOnceObj);
            if (value is bool b && !b) return false;

            // Check HasDirtyFiles()
            var hasDirtyMethod = controllerType.GetMethod("HasDirtyFiles",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (hasDirtyMethod != null)
            {
                var hasDirty = hasDirtyMethod.Invoke(controller, null);
                if (hasDirty is bool dirty && dirty) return false;
            }

            return true;
        }

        private List<BlueprintClassInfo> QueryDerivedBlueprints(
            string className, object assetsCache, VirtualFileSystemPath solutionDir,
            bool debug, out string debugInfo)
        {
            debugInfo = null;
            var debugSb = debug ? new StringBuilder() : null;
            var results = new List<BlueprintClassInfo>();

            // Find a method named GetDerivedBlueprintClasses (or similar) across Cpp/Unreal assemblies
            MethodInfo targetMethod = null;
            var assetsCacheRuntimeType = assetsCache.GetType();

            // Strategy 1: Search by method name across all types in relevant assemblies
            var methodSearchNames = new[] { "GetDerivedBlueprintClasses", "GetDerivedBlueprints", "FindDerivedBlueprints" };
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name ?? "";
                if (!asmName.Contains("Cpp") && !asmName.Contains("Unreal") && !asmName.Contains("UE"))
                    continue;
                try
                {
                    foreach (var t in asm.GetExportedTypes())
                    {
                        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (!methodSearchNames.Contains(m.Name)) continue;
                            var ps = m.GetParameters();
                            // Look for (string, UE4AssetsCache) or (UE4AssetsCache, string) signatures
                            if (ps.Length == 2)
                            {
                                if (ps[0].ParameterType == typeof(string) &&
                                    ps[1].ParameterType.IsAssignableFrom(assetsCacheRuntimeType))
                                {
                                    targetMethod = m;
                                    break;
                                }
                                if (ps[1].ParameterType == typeof(string) &&
                                    ps[0].ParameterType.IsAssignableFrom(assetsCacheRuntimeType))
                                {
                                    targetMethod = m;
                                    break;
                                }
                            }
                        }
                        if (targetMethod != null) break;
                    }
                }
                catch { }
                if (targetMethod != null) break;
            }

            if (targetMethod == null)
            {
                // Diagnostics: dump all methods mentioning "Blueprint" or "Derived" in Cpp/Unreal assemblies
                var diag = new StringBuilder();
                diag.AppendLine("Could not find GetDerivedBlueprintClasses method.");
                diag.AppendLine("AssetsCache runtime type: " + assetsCacheRuntimeType.FullName);
                diag.AppendLine();
                diag.AppendLine("== Static methods containing 'Blueprint' or 'Derived' in Cpp/Unreal assemblies ==");
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var asmName = asm.GetName().Name ?? "";
                    if (!asmName.Contains("Cpp") && !asmName.Contains("Unreal") && !asmName.Contains("UE"))
                        continue;
                    try
                    {
                        foreach (var t in asm.GetExportedTypes())
                        {
                            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                            {
                                if (m.Name.Contains("Blueprint") || m.Name.Contains("Derived") ||
                                    m.Name.Contains("blueprint") || m.Name.Contains("derived"))
                                    diag.AppendLine("  " + t.FullName + "." + m.Name +
                                        "(" + string.Join(", ", m.GetParameters().Select(p =>
                                            p.ParameterType.Name + " " + p.Name)) + ")");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        diag.AppendLine("  [" + asmName + ": " + ex.GetType().Name + "]");
                    }
                }

                // Also show instance methods on the assetsCache object itself
                diag.AppendLine();
                diag.AppendLine("== Methods on assetsCache containing 'Blueprint' or 'Derived' or 'Class' ==");
                foreach (var m in assetsCacheRuntimeType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name.Contains("Blueprint") || m.Name.Contains("Derived") ||
                        m.Name.Contains("Class") || m.Name.Contains("Asset"))
                        diag.AppendLine("  " + m.Name + "(" + string.Join(", ",
                            m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")" +
                            " -> " + m.ReturnType.Name);
                }

                throw new InvalidOperationException(diag.ToString());
            }

            if (debugSb != null)
            {
                debugSb.AppendLine("Matched method: " + targetMethod.DeclaringType?.FullName + "." + targetMethod.Name);
                debugSb.Append("  Signature: " + targetMethod.Name + "(");
                debugSb.Append(string.Join(", ", targetMethod.GetParameters().Select(p =>
                    p.ParameterType.FullName + " " + p.Name)));
                debugSb.AppendLine(")");
                debugSb.AppendLine("  Return type: " + targetMethod.ReturnType.FullName);
                debugSb.AppendLine();

                // Dump ALL methods on UE4SearchUtil
                var searchUtilType = targetMethod.DeclaringType;
                if (searchUtilType != null)
                {
                    debugSb.AppendLine("== All methods on " + searchUtilType.Name + " ==");
                    foreach (var m in searchUtilType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        debugSb.AppendLine("  " + m.ReturnType.Name + " " + m.Name + "(" +
                            string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
                    }
                    debugSb.AppendLine();
                }

                // Dump ALL public methods on UE4AssetsCache (excluding Object base methods)
                debugSb.AppendLine("== ALL public methods on " + assetsCacheRuntimeType.Name + " ==");
                foreach (var m in assetsCacheRuntimeType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.DeclaringType != typeof(object))
                    .OrderBy(m => m.Name))
                {
                    debugSb.AppendLine("  " + m.ReturnType.Name + " " + m.Name + "(" +
                        string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
                }
                debugSb.AppendLine();

                // Also dump public properties on UE4AssetsCache
                debugSb.AppendLine("== ALL public properties on " + assetsCacheRuntimeType.Name + " ==");
                foreach (var p in assetsCacheRuntimeType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    debugSb.AppendLine("  " + p.PropertyType.Name + " " + p.Name);
                }
                debugSb.AppendLine();
            }

            // BFS: recursively find all derived Blueprints
            // GetDerivedBlueprintClasses only returns direct children, so we need to
            // recurse into each found Blueprint name to find grandchildren, etc.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(className);
            seen.Add(className);

            var isFirstParam0String = targetMethod.GetParameters()[0].ParameterType == typeof(string);

            while (queue.Count > 0)
            {
                var currentClass = queue.Dequeue();
                object[] invokeArgs = isFirstParam0String
                    ? new object[] { currentClass, assetsCache }
                    : new object[] { assetsCache, currentClass };

                var enumerable = targetMethod.Invoke(null, invokeArgs);
                if (enumerable == null) continue;

                foreach (var item in (IEnumerable)enumerable)
                {
                    if (item == null) continue;
                    var itemType = item.GetType();

                    // Dump schema of the first item in debug mode
                    if (results.Count == 0 && debugSb != null)
                    {
                        debugSb.AppendLine("Result item type: " + itemType.FullName);
                        debugSb.AppendLine("  Fields:");
                        foreach (var f in itemType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                        {
                            object val = null;
                            try { val = f.GetValue(item); } catch { }
                            debugSb.AppendLine("    " + f.FieldType.Name + " " + f.Name +
                                " = " + (val?.ToString() ?? "null"));
                        }
                        debugSb.AppendLine();
                    }

                    var name = "";
                    var filePath = "";

                    // Read Name
                    var nameField = itemType.GetField("Name", BindingFlags.Public | BindingFlags.Instance);
                    if (nameField != null)
                        name = nameField.GetValue(item)?.ToString() ?? "";
                    else
                    {
                        var nameProp = itemType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                        if (nameProp != null)
                            name = nameProp.GetValue(item)?.ToString() ?? "";
                        else
                            name = item.ToString() ?? "";
                    }

                    // Read ContainingFile
                    var filePropertyNames = new[] { "ContainingFile", "File", "Path", "Location" };
                    object containingFile = null;
                    foreach (var fpName in filePropertyNames)
                    {
                        var field = itemType.GetField(fpName, BindingFlags.Public | BindingFlags.Instance);
                        if (field != null) { containingFile = field.GetValue(item); break; }
                        var prop = itemType.GetProperty(fpName, BindingFlags.Public | BindingFlags.Instance);
                        if (prop != null) { containingFile = prop.GetValue(item); break; }
                    }

                    if (containingFile != null)
                    {
                        var getLocationMethod = containingFile.GetType().GetMethod("GetLocation",
                            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                        if (getLocationMethod != null)
                        {
                            var location = getLocationMethod.Invoke(containingFile, null);
                            if (location != null)
                            {
                                var makeRelMethod = location.GetType().GetMethod("MakeRelativeTo",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (makeRelMethod != null)
                                {
                                    try
                                    {
                                        var relPath = makeRelMethod.Invoke(location, new object[] { solutionDir });
                                        filePath = relPath?.ToString()?.Replace('\\', '/') ?? "";
                                    }
                                    catch { filePath = location.ToString()?.Replace('\\', '/') ?? ""; }
                                }
                                else
                                    filePath = location.ToString()?.Replace('\\', '/') ?? "";
                            }
                        }
                        else
                            filePath = containingFile.ToString()?.Replace('\\', '/') ?? "";
                    }

                    if (!string.IsNullOrEmpty(name) && seen.Add(name))
                    {
                        results.Add(new BlueprintClassInfo { Name = name, FilePath = filePath });

                        // Enqueue this Blueprint name for recursive search
                        // Try both with and without _C suffix
                        queue.Enqueue(name);
                        if (name.EndsWith("_C"))
                        {
                            var withoutC = name.Substring(0, name.Length - 2);
                            if (seen.Add(withoutC))
                                queue.Enqueue(withoutC);
                        }
                        else if (seen.Add(name + "_C"))
                        {
                            queue.Enqueue(name + "_C");
                        }
                    }
                }
            }

            if (debugSb != null)
            {
                debugSb.AppendLine("Total results (recursive BFS): " + results.Count);
                debugSb.AppendLine("Classes queried: " + string.Join(", ", seen));
                debugInfo = debugSb.ToString();
            }

            return results;
        }

        private static void RespondBlueprintsMarkdown(
            HttpListenerContext ctx, string className, bool cacheReady,
            string cacheStatus, List<BlueprintClassInfo> blueprints,
            bool debug, string debugInfo)
        {
            var sb = new StringBuilder();
            sb.Append("# Blueprints derived from ").AppendLine(className);
            sb.AppendLine();
            sb.Append("**Cache status:** ").AppendLine(cacheStatus);
            sb.Append("**Total descendants:** ").AppendLine(blueprints.Count.ToString());
            sb.AppendLine();

            if (!cacheReady)
            {
                sb.AppendLine("> **Warning:** Cache is still building. Results may be incomplete.");
                sb.AppendLine();
            }

            if (blueprints.Count == 0)
            {
                sb.AppendLine("No derived Blueprint classes found.");
            }
            else
            {
                sb.AppendLine("## Derived Blueprint Classes");
                sb.AppendLine();
                for (var i = 0; i < blueprints.Count; i++)
                {
                    var bp = blueprints[i];
                    sb.Append(i + 1).Append(". ").Append(bp.Name);
                    if (!string.IsNullOrEmpty(bp.FilePath))
                        sb.Append(" — ").Append(bp.FilePath);
                    sb.AppendLine();
                }
            }

            if (debug && debugInfo != null)
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine("## Debug Info");
                sb.AppendLine("```");
                sb.AppendLine(debugInfo);
                sb.AppendLine("```");
            }

            Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
        }

        private static void RespondBlueprintsJson(
            HttpListenerContext ctx, string className, bool cacheReady,
            string cacheStatus, List<BlueprintClassInfo> blueprints,
            bool debug, string debugInfo)
        {
            var bpJsons = blueprints.Select(bp =>
                "{\"name\": \"" + EscapeJson(bp.Name) + "\", " +
                "\"file\": \"" + EscapeJson(bp.FilePath) + "\"}");

            var json =
                "{\"class\": \"" + EscapeJson(className) + "\", " +
                "\"cacheReady\": " + (cacheReady ? "true" : "false") + ", " +
                "\"cacheStatus\": \"" + EscapeJson(cacheStatus) + "\", " +
                "\"totalCount\": " + blueprints.Count + ", " +
                "\"blueprints\": [" + string.Join(",", bpJsons) + "]";
            if (debug && debugInfo != null)
                json += ", \"debug\": \"" + EscapeJson(debugInfo) + "\"";
            json += "}";

            Respond(ctx, 200, "application/json; charset=utf-8", json);
        }

        // ── /ue-project (diagnostic) ──

        private void HandleUEProjectDiagnostics(HttpListenerContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# UE Project Diagnostics");
            sb.AppendLine();

            // Basic solution info
            var solutionDir = _solution.SolutionDirectory;
            sb.AppendLine("## Solution");
            sb.AppendLine($"- Directory: {solutionDir.FullPath}");
            sb.AppendLine();

            // Look for .uproject files in solution directory
            sb.AppendLine("## .uproject files in solution directory");
            try
            {
                var uprojectFiles = Directory.GetFiles(solutionDir.FullPath, "*.uproject");
                if (uprojectFiles.Length == 0)
                    sb.AppendLine("- (none found)");
                else
                    foreach (var f in uprojectFiles)
                        sb.AppendLine($"- {f}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"- Error: {ex.Message}");
            }
            sb.AppendLine();

            // Search for UE-related components
            sb.AppendLine("## UE-related components (searching loaded assemblies)");
            sb.AppendLine();

            var componentCandidates = new[]
            {
                "ICppUE4ProjectPropertiesProvider",
                "ICppUE4SolutionDetector",
                "CppUE4Configuration",
                "UE4ProjectModel",
                "UnrealProjectModel",
                "IUnrealProjectProvider",
                "UnrealEngineSettings",
            };

            foreach (var candidateName in componentCandidates)
            {
                sb.AppendLine($"### Searching for: {candidateName}");

                Type foundType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var asmName = asm.GetName().Name ?? "";
                    if (!asmName.Contains("Cpp") && !asmName.Contains("Unreal") && !asmName.Contains("UE") &&
                        !asmName.Contains("Rider") && !asmName.Contains("JetBrains"))
                        continue;

                    try
                    {
                        foreach (var t in asm.GetExportedTypes())
                        {
                            if (t.Name == candidateName || t.Name == "I" + candidateName)
                            {
                                foundType = t;
                                break;
                            }
                        }
                    }
                    catch { }
                    if (foundType != null) break;
                }

                if (foundType == null)
                {
                    sb.AppendLine("- Type not found");
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine($"- Found: {foundType.FullName}");
                sb.AppendLine($"- Assembly: {foundType.Assembly.GetName().Name}");

                // Try to resolve as component
                object componentInstance = null;
                try
                {
                    componentInstance = ResolveComponent(foundType);
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"- ResolveComponent error: {ex.Message}");
                }

                if (componentInstance == null)
                {
                    sb.AppendLine("- Component instance: null (not registered or not resolvable)");
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine($"- Component instance: {componentInstance.GetType().FullName}");
                sb.AppendLine();

                // Dump properties
                sb.AppendLine("#### Properties:");
                foreach (var prop in componentInstance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    try
                    {
                        var value = prop.GetValue(componentInstance);
                        var valueStr = value?.ToString() ?? "null";
                        if (valueStr.Length > 200) valueStr = valueStr.Substring(0, 200) + "...";
                        sb.AppendLine($"- {prop.PropertyType.Name} {prop.Name} = {valueStr}");
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"- {prop.PropertyType.Name} {prop.Name} = (error: {ex.Message})");
                    }
                }
                sb.AppendLine();

                // Dump methods (excluding Object base methods)
                sb.AppendLine("#### Methods:");
                foreach (var method in componentInstance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.DeclaringType != typeof(object))
                    .OrderBy(m => m.Name))
                {
                    var paramStr = string.Join(", ", method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    sb.AppendLine($"- {method.ReturnType.Name} {method.Name}({paramStr})");
                }
                sb.AppendLine();
            }

            // Try to call GetUProjectPath() and GetUE4EngineProject() on ICppUE4SolutionDetector
            sb.AppendLine("## UE Project Info (via GetUeProjectInfo helper)");
            var ueInfo = GetUeProjectInfo();
            sb.AppendLine($"- IsUnrealProject: {ueInfo.IsUnrealProject}");
            sb.AppendLine($"- UProjectPath: {ueInfo.UProjectPath ?? "(null)"}");
            sb.AppendLine($"- EnginePath: {ueInfo.EnginePath ?? "(null)"}");
            sb.AppendLine($"- EngineVersion: {ueInfo.EngineVersion ?? "(null)"}");
            sb.AppendLine($"- CommandletExePath: {ueInfo.CommandletExePath ?? "(null)"}");
            if (ueInfo.Error != null)
                sb.AppendLine($"- Error: {ueInfo.Error}");
            if (!string.IsNullOrEmpty(ueInfo.CommandletExePath))
                sb.AppendLine($"- Commandlet exists: {File.Exists(ueInfo.CommandletExePath)}");
            sb.AppendLine();

            sb.AppendLine("## Calling ICppUE4SolutionDetector methods (raw)");
            try
            {
                Type detectorType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        detectorType = asm.GetType("JetBrains.ReSharper.Psi.Cpp.UE4.ICppUE4SolutionDetector");
                        if (detectorType != null) break;
                    }
                    catch { }
                }

                if (detectorType != null)
                {
                    var detector = ResolveComponent(detectorType);
                    if (detector != null)
                    {
                        // Call GetUProjectPath()
                        var getUProjectPath = detector.GetType().GetMethod("GetUProjectPath",
                            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                        if (getUProjectPath != null)
                        {
                            var uprojectPath = getUProjectPath.Invoke(detector, null);
                            sb.AppendLine($"- GetUProjectPath() = {uprojectPath}");
                        }
                        else
                        {
                            sb.AppendLine("- GetUProjectPath() method not found");
                        }

                        // Call GetUE4EngineProject()
                        var getEngineProject = detector.GetType().GetMethod("GetUE4EngineProject",
                            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                        if (getEngineProject != null)
                        {
                            var engineProject = getEngineProject.Invoke(detector, null);
                            sb.AppendLine($"- GetUE4EngineProject() = {engineProject}");

                            if (engineProject != null)
                            {
                                // Try to get more info from the engine project
                                var engineType = engineProject.GetType();
                                sb.AppendLine($"  - Type: {engineType.FullName}");

                                // Look for path-related properties
                                foreach (var prop in engineType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                                {
                                    if (prop.Name.Contains("Path") || prop.Name.Contains("Directory") ||
                                        prop.Name.Contains("Location") || prop.Name.Contains("Folder"))
                                    {
                                        try
                                        {
                                            var val = prop.GetValue(engineProject);
                                            sb.AppendLine($"  - {prop.Name} = {val}");
                                        }
                                        catch { }
                                    }
                                }

                                // Try GetProperty("ProjectFileLocation") or similar
                                var getPropMethod = engineType.GetMethod("GetProperty",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (getPropMethod != null)
                                {
                                    sb.AppendLine($"  - Has GetProperty method");
                                }

                                // Look for ProjectFileLocation
                                var projFileLoc = engineType.GetProperty("ProjectFileLocation",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (projFileLoc != null)
                                {
                                    var loc = projFileLoc.GetValue(engineProject);
                                    sb.AppendLine($"  - ProjectFileLocation = {loc}");
                                }
                            }
                        }
                        else
                        {
                            sb.AppendLine("- GetUE4EngineProject() method not found");
                        }
                    }
                    else
                    {
                        sb.AppendLine("- Could not resolve ICppUE4SolutionDetector component");
                    }
                }
                else
                {
                    sb.AppendLine("- ICppUE4SolutionDetector type not found");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"- Error: {ex.Message}");
                if (ex.InnerException != null)
                    sb.AppendLine($"  Inner: {ex.InnerException.Message}");
            }
            sb.AppendLine();

            // Also dump types containing "Engine" or "Project" in Cpp/Unreal assemblies
            sb.AppendLine("## All types containing 'Engine', 'Project', or 'Uproject' in Cpp/Unreal assemblies");
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name ?? "";
                if (!asmName.Contains("Cpp") && !asmName.Contains("Unreal") && !asmName.Contains("UE"))
                    continue;

                try
                {
                    foreach (var t in asm.GetExportedTypes())
                    {
                        if (t.Name.Contains("Engine") || t.Name.Contains("Project") || t.Name.Contains("Uproject"))
                        {
                            sb.AppendLine($"- {t.FullName}");
                        }
                    }
                }
                catch { }
            }

            Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
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
            return Json.Serialize(new
            {
                status = "ok",
                solution = _solution.SolutionDirectory.FullPath.Replace('\\', '/'),
                port = _config.Port
            });
        }

        // ── / ──

        private string BuildIndexResponse()
        {
            return Json.Serialize(new
            {
                endpoints = new Dictionary<string, string>
                {
                    ["GET /"] = "This help message",
                    ["GET /health"] = "Server and solution status",
                    ["GET /files"] = "List all user source files under solution directory",
                    ["GET /inspect?file=path"] = "Run code inspection on file(s). Multiple: &file=a&file=b. Default output is markdown; add &format=json for JSON. Add &debug=true for diagnostics.",
                    ["GET /blueprints?class=ClassName"] = "[UE5 only] List Blueprint classes deriving from a C++ class. Add &format=json for JSON.",
                    ["GET /blueprint-audit"] = "[UE5 only] Get Blueprint audit data (returns 409 if stale, 503 if not ready)",
                    ["GET /blueprint-audit/refresh"] = "[UE5 only] Trigger background refresh of Blueprint audit data",
                    ["GET /blueprint-audit/status"] = "[UE5 only] Check status of Blueprint audit refresh",
                    ["GET /ue-project"] = "Diagnostic: show UE project detection info"
                },
                isUnrealProject = IsUnrealProject()
            });
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

        private static PsiSyncResult WaitForPsiSync(
            IPsiSourceFile sourceFile, int timeoutMs, int pollIntervalMs)
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
            while (sw.ElapsedMilliseconds < timeoutMs)
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

                Thread.Sleep(pollIntervalMs);
            }

            return new PsiSyncResult
            {
                Status = "timeout",
                WaitedMs = (int)sw.ElapsedMilliseconds,
                Attempts = attempts,
                Message = "PSI document did not match disk content within " + timeoutMs + "ms"
            };
        }

        private static string NormalizeLineEndings(string text)
        {
            return text.Replace("\r\n", "\n").Replace("\r", "\n");
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

        // ── UE Project Info ──

        private UeProjectInfo GetUeProjectInfo()
        {
            var result = new UeProjectInfo();

            try
            {
                // Find ICppUE4SolutionDetector type
                Type detectorType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        detectorType = asm.GetType("JetBrains.ReSharper.Psi.Cpp.UE4.ICppUE4SolutionDetector");
                        if (detectorType != null) break;
                    }
                    catch { }
                }

                if (detectorType == null)
                {
                    result.Error = "ICppUE4SolutionDetector type not found";
                    return result;
                }

                var detector = ResolveComponent(detectorType);
                if (detector == null)
                {
                    result.Error = "ICppUE4SolutionDetector component not resolvable";
                    return result;
                }

                // Get UProjectPath FIRST - this is the most reliable indicator
                // The IsUnrealSolution property can be unreliable (timing/state issues)
                var getUProjectPath = detector.GetType().GetMethod("GetUProjectPath",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (getUProjectPath != null)
                {
                    var uprojectPath = getUProjectPath.Invoke(detector, null);
                    result.UProjectPath = uprojectPath?.ToString();
                }

                // Determine IsUnrealProject based on whether we have a valid .uproject path
                // This is more reliable than the IsUnrealSolution property
                if (!string.IsNullOrEmpty(result.UProjectPath) && File.Exists(result.UProjectPath))
                {
                    result.IsUnrealProject = true;
                }
                else
                {
                    // Fallback: check IsUnrealSolution property
                    var isUnrealProp = detector.GetType().GetProperty("IsUnrealSolution",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (isUnrealProp != null)
                    {
                        var isUnrealObj = isUnrealProp.GetValue(detector);
                        // It's IProperty<bool>, get .Value
                        var valueProp = isUnrealObj?.GetType().GetProperty("Value",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (valueProp != null)
                        {
                            var val = valueProp.GetValue(isUnrealObj);
                            result.IsUnrealProject = val is true;
                        }
                    }
                }

                if (!result.IsUnrealProject)
                {
                    result.Error = "Not an Unreal project (no valid .uproject file found)";
                    return result;
                }

                // Get UnrealContext property and parse engine path
                var unrealContextProp = detector.GetType().GetProperty("UnrealContext",
                    BindingFlags.Public | BindingFlags.Instance);
                if (unrealContextProp != null)
                {
                    var contextObj = unrealContextProp.GetValue(detector);
                    // It's IProperty<UnrealEngineContext>, get .Value
                    var valueProp = contextObj?.GetType().GetProperty("Value",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (valueProp != null)
                    {
                        var contextValue = valueProp.GetValue(contextObj);
                        if (contextValue != null)
                        {
                            // Try to get Path property directly from UnrealEngineContext
                            var pathProp = contextValue.GetType().GetProperty("Path",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (pathProp != null)
                            {
                                var pathVal = pathProp.GetValue(contextValue);
                                result.EnginePath = pathVal?.ToString();
                            }

                            // Try to get Version property
                            var versionProp = contextValue.GetType().GetProperty("Version",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (versionProp != null)
                            {
                                var versionVal = versionProp.GetValue(contextValue);
                                result.EngineVersion = versionVal?.ToString();
                            }

                            // If Path property didn't work, try parsing ToString()
                            if (string.IsNullOrEmpty(result.EnginePath))
                            {
                                var contextStr = contextValue.ToString();
                                // Format: "Path: D:\UE\Engines\UE_5.7\Engine. Version: 5.7.1. ..."
                                var pathMatch = System.Text.RegularExpressions.Regex.Match(
                                    contextStr, @"Path:\s*([^.]+(?:\.[^.]+)*?)\.\s*Version:");
                                if (pathMatch.Success)
                                {
                                    result.EnginePath = pathMatch.Groups[1].Value.Trim();
                                }

                                var versionMatch = System.Text.RegularExpressions.Regex.Match(
                                    contextStr, @"Version:\s*([0-9.]+)");
                                if (versionMatch.Success)
                                {
                                    result.EngineVersion = versionMatch.Groups[1].Value.Trim();
                                }
                            }
                        }
                    }
                }

                // Build commandlet path
                if (!string.IsNullOrEmpty(result.EnginePath))
                {
                    result.CommandletExePath = Path.Combine(
                        result.EnginePath, "Binaries", "Win64", "UnrealEditor-Cmd.exe");
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                if (ex.InnerException != null)
                    result.Error += " | Inner: " + ex.InnerException.Message;
            }

            return result;
        }

        // ── UE Project Detection (lightweight) ──

        /// <summary>
        /// Quick check for whether this is an Unreal Engine project.
        /// Uses file system check (looks for .uproject) rather than heavy reflection.
        /// </summary>
        private bool IsUnrealProject()
        {
            try
            {
                var solutionDir = _solution.SolutionDirectory.FullPath;
                var uprojectFiles = Directory.GetFiles(solutionDir, "*.uproject");
                return uprojectFiles.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        // ── Blueprint Audit Infrastructure ──

        private readonly object _auditLock = new object();
        private bool _auditRefreshInProgress;
        private DateTime? _lastAuditRefresh;
        private Process _auditProcess;
        private string _auditProcessOutput;
        private string _auditProcessError;
        private bool _bootCheckCompleted;
        private string _bootCheckResult;
        private bool _commandletMissing;
        private int? _lastExitCode;

        private const string CommandletMissingMessage =
            "The BlueprintAudit commandlet is not installed.\n\n" +
            "To fix this, install the UnrealBlueprintAudit plugin in your UE project:\n" +
            "1. Clone https://github.com/[your-username]/UnrealBlueprintAudit\n" +
            "2. Copy or symlink it to your project's Plugins/ folder\n" +
            "3. Rebuild your UE project\n" +
            "4. Restart Rider and try again";

        private void CheckAndRefreshOnBoot()
        {
            try
            {
                var ueInfo = GetUeProjectInfo();
                if (!ueInfo.IsUnrealProject)
                {
                    _bootCheckResult = "Not an Unreal project - skipping boot check";
                    _bootCheckCompleted = true;
                    Log.Warn("InspectionHttpServer: " + _bootCheckResult);
                    return;
                }

                var uprojectDir = ueInfo.ProjectDirectory;
                var auditDir = Path.Combine(uprojectDir, "Saved", "Audit", "Blueprints");

                if (!Directory.Exists(auditDir))
                {
                    _bootCheckResult = "Audit directory does not exist - triggering refresh";
                    _bootCheckCompleted = true;
                    Log.Warn("InspectionHttpServer: " + _bootCheckResult);
                    TriggerRefresh(ueInfo);
                    return;
                }

                // Check for staleness
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

        /// <summary>
        /// Triggers a Blueprint audit refresh in the background.
        /// Returns true if refresh was started, false if one is already in progress.
        /// </summary>
        private bool TriggerRefresh(UeProjectInfo ueInfo)
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

        private void HandleBlueprintAudit(HttpListenerContext ctx)
        {
            var format = GetFormat(ctx);
            var ueInfo = GetUeProjectInfo();

            if (!ueInfo.IsUnrealProject)
            {
                Respond(ctx, 404, "text/plain",
                    "This endpoint is only available for Unreal Engine projects.\n" +
                    "No .uproject file found in solution directory.");
                return;
            }

            // Check if we've detected the commandlet is missing
            bool isMissing;
            lock (_auditLock) { isMissing = _commandletMissing; }
            if (isMissing)
            {
                RespondCommandletMissing(ctx, format);
                return;
            }

            // Build path to audit directory: <ProjectDir>/Saved/Audit/Blueprints/
            var uprojectDir = ueInfo.ProjectDirectory;
            var auditDir = Path.Combine(uprojectDir, "Saved", "Audit", "Blueprints");

            if (!Directory.Exists(auditDir))
            {
                RespondAuditNotReady(ctx, format, "Audit directory does not exist. Run /blueprint-audit/refresh first.");
                return;
            }

            // Scan all JSON files and check freshness
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
                RespondAuditNotReady(ctx, format, "No audit files found. Run /blueprint-audit/refresh first.");
                return;
            }

            // Per mandate: NEVER return stale data
            if (staleCount > 0)
            {
                RespondAuditStale(ctx, format, blueprints, staleCount);
                return;
            }

            // All fresh - return the data
            RespondAuditSuccess(ctx, format, blueprints, errorCount);
        }

        private void HandleBlueprintAuditRefresh(HttpListenerContext ctx)
        {
            var format = GetFormat(ctx);
            var ueInfo = GetUeProjectInfo();

            if (!ueInfo.IsUnrealProject)
            {
                Respond(ctx, 404, "text/plain",
                    "This endpoint is only available for Unreal Engine projects.\n" +
                    "No .uproject file found in solution directory.");
                return;
            }

            if (string.IsNullOrEmpty(ueInfo.CommandletExePath) || !File.Exists(ueInfo.CommandletExePath))
            {
                Respond(ctx, 500, "text/plain",
                    "Cannot find UnrealEditor-Cmd.exe.\n" +
                    "CommandletExePath: " + (ueInfo.CommandletExePath ?? "(null)"));
                return;
            }

            lock (_auditLock)
            {
                if (_auditRefreshInProgress)
                {
                    if (format == "json")
                    {
                        Respond(ctx, 202, "application/json; charset=utf-8",
                            "{\"status\": \"in_progress\", \"message\": \"Refresh already in progress\"}");
                    }
                    else
                    {
                        Respond(ctx, 202, "text/plain", "Refresh already in progress. Check /blueprint-audit/status");
                    }
                    return;
                }

                _auditRefreshInProgress = true;
                _auditProcessOutput = null;
                _auditProcessError = null;
            }

            // Start the commandlet in background
            _ = Task.Run(() => RunBlueprintAuditCommandlet(ueInfo));

            if (format == "json")
            {
                Respond(ctx, 202, "application/json; charset=utf-8",
                    "{\"status\": \"started\", \"message\": \"Blueprint audit refresh started\"}");
            }
            else
            {
                Respond(ctx, 202, "text/plain",
                    "Blueprint audit refresh started.\n" +
                    "Check /blueprint-audit/status for progress.\n" +
                    "Once complete, query /blueprint-audit for results.");
            }
        }

        private void HandleBlueprintAuditStatus(HttpListenerContext ctx)
        {
            var format = GetFormat(ctx);

            bool inProgress;
            DateTime? lastRefresh;
            string output, error;
            bool bootCheckDone;
            string bootResult;
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

            bootCheckDone = _bootCheckCompleted;
            bootResult = _bootCheckResult;

            if (format == "json")
            {
                var json = new StringBuilder();
                json.Append("{\"inProgress\": ").Append(inProgress ? "true" : "false");
                json.Append(", \"commandletMissing\": ").Append(isMissing ? "true" : "false");
                json.Append(", \"bootCheckCompleted\": ").Append(bootCheckDone ? "true" : "false");
                if (bootResult != null)
                    json.Append(", \"bootCheckResult\": \"").Append(EscapeJson(bootResult)).Append("\"");
                if (lastRefresh.HasValue)
                    json.Append(", \"lastRefresh\": \"").Append(lastRefresh.Value.ToString("o")).Append("\"");
                if (exitCode.HasValue)
                    json.Append(", \"lastExitCode\": ").Append(exitCode.Value);
                if (output != null)
                    json.Append(", \"output\": \"").Append(EscapeJson(TruncateForJson(output, 2000))).Append("\"");
                if (error != null)
                    json.Append(", \"error\": \"").Append(EscapeJson(TruncateForJson(error, 1000))).Append("\"");
                json.Append("}");

                Respond(ctx, 200, "application/json; charset=utf-8", json.ToString());
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Blueprint Audit Status");
                sb.AppendLine();
                sb.Append("**In Progress:** ").AppendLine(inProgress ? "Yes" : "No");
                if (isMissing)
                {
                    sb.AppendLine("**Commandlet Missing:** Yes");
                    sb.AppendLine();
                    sb.AppendLine("> **WARNING:** The BlueprintAudit commandlet is not installed.");
                    sb.AppendLine("> Install the UnrealBlueprintAudit plugin in your UE project.");
                    sb.AppendLine();
                }
                sb.Append("**Boot Check Completed:** ").AppendLine(bootCheckDone ? "Yes" : "No");
                if (bootResult != null)
                    sb.Append("**Boot Check Result:** ").AppendLine(bootResult);
                if (lastRefresh.HasValue)
                    sb.Append("**Last Refresh:** ").AppendLine(lastRefresh.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                if (exitCode.HasValue)
                    sb.Append("**Last Exit Code:** ").AppendLine(exitCode.Value.ToString());
                if (error != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("## Last Error");
                    sb.AppendLine("```");
                    sb.AppendLine(error);
                    sb.AppendLine("```");
                }
                if (output != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("## Last Output (truncated)");
                    sb.AppendLine("```");
                    sb.AppendLine(TruncateForJson(output, 2000));
                    sb.AppendLine("```");
                }

                Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
            }
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
                    lock (_auditLock)
                    {
                        _auditProcess = process;
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    var exitCode = process.ExitCode;

                    // Detect if commandlet is missing
                    // UE typically outputs "Unknown command" or similar when commandlet isn't found
                    var isMissing = IsCommandletMissingError(output, error, exitCode);

                    lock (_auditLock)
                    {
                        _auditProcessOutput = output;
                        _lastExitCode = exitCode;

                        if (isMissing)
                        {
                            _commandletMissing = true;
                            _auditProcessError = CommandletMissingMessage;
                        }
                        else
                        {
                            _commandletMissing = false;
                            _auditProcessError = string.IsNullOrWhiteSpace(error) ? null : error;
                            if (exitCode == 0)
                                _lastAuditRefresh = DateTime.Now;
                        }

                        _auditRefreshInProgress = false;
                        _auditProcess = null;
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
                    _auditProcess = null;
                }

                Log.Error(ex, "InspectionHttpServer: Blueprint audit failed");
            }
        }

        private static bool IsCommandletMissingError(string output, string error, int exitCode)
        {
            if (exitCode == 0) return false;

            var combined = (output ?? "") + (error ?? "");
            var lower = combined.ToLowerInvariant();

            // UE error patterns when commandlet is not found
            return lower.Contains("unknown commandlet") ||
                   lower.Contains("commandlet not found") ||
                   lower.Contains("failed to find commandlet") ||
                   lower.Contains("can't find commandlet") ||
                   lower.Contains("unable to find commandlet") ||
                   (lower.Contains("blueprintaudit") && lower.Contains("not recognized"));
        }

        // ── Blueprint Audit Data Structures ──

        private BlueprintAuditEntry ReadAndCheckBlueprintAudit(string jsonFile, string uprojectDir)
        {
            var entry = new BlueprintAuditEntry { JsonFile = jsonFile };

            try
            {
                var jsonContent = File.ReadAllText(jsonFile);
                entry.Data = ParseSimpleJson(jsonContent);

                entry.Name = entry.Data.TryGetValue("Name", out var name) ? name?.ToString() : null;
                entry.Path = entry.Data.TryGetValue("Path", out var path) ? path?.ToString() : null;
                entry.SourceFileHash = entry.Data.TryGetValue("SourceFileHash", out var hash) ? hash?.ToString() : null;

                // Compute current hash if we have a path
                if (!string.IsNullOrEmpty(entry.Path))
                {
                    var uassetPath = ConvertPackagePathToFilePath(entry.Path, uprojectDir);
                    if (!string.IsNullOrEmpty(uassetPath) && File.Exists(uassetPath))
                    {
                        entry.CurrentFileHash = ComputeMD5Hash(uassetPath);
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
                    // No hash in file - consider stale (old format or missing data)
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
            // Package path formats:
            //   /Game/UI/Widgets/WBP_Foo              (package only)
            //   /Game/UI/Widgets/WBP_Foo.WBP_Foo      (package.object - common for BPs)
            //   /Game/UI/Widgets/WBP_Foo.WBP_Foo_C    (package.class)
            // File path: <uprojectDir>/Content/UI/Widgets/WBP_Foo.uasset
            if (string.IsNullOrEmpty(packagePath)) return null;

            var relativePath = packagePath;

            // Strip object name if present (everything after the dot)
            // The dot separates package path from object name in UE paths
            var dotIndex = relativePath.LastIndexOf('.');
            if (dotIndex > 0)
            {
                // Make sure the dot is after the last slash (it's an object separator, not a directory)
                var lastSlash = relativePath.LastIndexOf('/');
                if (dotIndex > lastSlash)
                {
                    relativePath = relativePath.Substring(0, dotIndex);
                }
            }

            if (relativePath.StartsWith("/Game/"))
            {
                relativePath = relativePath.Substring(6); // Remove "/Game/"
            }
            else if (relativePath.StartsWith("/"))
            {
                // Other mount points - skip for now
                return null;
            }

            return Path.Combine(uprojectDir, "Content", relativePath.Replace('/', Path.DirectorySeparatorChar) + ".uasset");
        }

        private static string ComputeMD5Hash(string filePath)
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
            // Simple JSON parser for flat objects - just extract top-level string fields
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            // Match "key": "value" or "key": number or "key": bool
            var stringPattern = new Regex(@"""(\w+)""\s*:\s*""([^""\\]*(?:\\.[^""\\]*)*)""");
            var numberPattern = new Regex(@"""(\w+)""\s*:\s*(-?\d+(?:\.\d+)?)");
            var boolPattern = new Regex(@"""(\w+)""\s*:\s*(true|false)");

            // Keep first match only - top-level fields come before nested array fields
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

        // ── Blueprint Audit Response Helpers ──

        private void RespondAuditNotReady(HttpListenerContext ctx, string format, string message)
        {
            if (format == "json")
            {
                Respond(ctx, 503, "application/json; charset=utf-8",
                    "{\"status\": \"not_ready\", \"message\": \"" + EscapeJson(message) + "\", " +
                    "\"action\": \"Call /blueprint-audit/refresh to generate audit data\"}");
            }
            else
            {
                Respond(ctx, 503, "text/markdown; charset=utf-8",
                    "# Blueprint Audit Not Ready\n\n" +
                    message + "\n\n" +
                    "**Action:** Call `/blueprint-audit/refresh` to generate audit data.");
            }
        }

        private void RespondCommandletMissing(HttpListenerContext ctx, string format)
        {
            if (format == "json")
            {
                Respond(ctx, 501, "application/json; charset=utf-8",
                    "{\"status\": \"commandlet_missing\", " +
                    "\"message\": \"" + EscapeJson(CommandletMissingMessage) + "\", " +
                    "\"installUrl\": \"https://github.com/[your-username]/UnrealBlueprintAudit\"}");
            }
            else
            {
                Respond(ctx, 501, "text/markdown; charset=utf-8",
                    "# Blueprint Audit - Plugin Not Installed\n\n" +
                    CommandletMissingMessage.Replace("\n", "\n\n"));
            }
        }

        private void RespondAuditStale(HttpListenerContext ctx, string format,
            List<BlueprintAuditEntry> blueprints, int staleCount)
        {
            // Per mandate: NEVER return stale data - return error instead
            var staleEntries = blueprints.Where(b => b.IsStale).Take(10).ToList();

            if (format == "json")
            {
                var staleJson = staleEntries.Select(e =>
                    "{\"name\": \"" + EscapeJson(e.Name ?? "") + "\", " +
                    "\"path\": \"" + EscapeJson(e.Path ?? "") + "\"}");

                Respond(ctx, 409, "application/json; charset=utf-8",
                    "{\"status\": \"stale\", " +
                    "\"message\": \"Audit data is stale. Refresh required before data can be returned.\", " +
                    "\"staleCount\": " + staleCount + ", " +
                    "\"totalCount\": " + blueprints.Count + ", " +
                    "\"staleExamples\": [" + string.Join(",", staleJson) + "], " +
                    "\"action\": \"Call /blueprint-audit/refresh to update audit data\"}");
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Blueprint Audit - STALE DATA");
                sb.AppendLine();
                sb.AppendLine("**Status:** Data is stale and cannot be returned.");
                sb.AppendLine();
                sb.Append("**Stale Blueprints:** ").Append(staleCount).Append(" of ").AppendLine(blueprints.Count.ToString());
                sb.AppendLine();
                sb.AppendLine("## Stale Examples (first 10)");
                foreach (var e in staleEntries)
                {
                    sb.Append("- ").AppendLine(e.Name ?? e.Path ?? "(unknown)");
                }
                sb.AppendLine();
                sb.AppendLine("**Action:** Call `/blueprint-audit/refresh` to update audit data.");

                Respond(ctx, 409, "text/markdown; charset=utf-8", sb.ToString());
            }
        }

        private void RespondAuditSuccess(HttpListenerContext ctx, string format,
            List<BlueprintAuditEntry> blueprints, int errorCount)
        {
            if (format == "json")
            {
                var bpJsons = blueprints.Select(e =>
                {
                    var sb = new StringBuilder();
                    sb.Append("{\"name\": \"").Append(EscapeJson(e.Name ?? "")).Append("\", ");
                    sb.Append("\"path\": \"").Append(EscapeJson(e.Path ?? "")).Append("\", ");
                    sb.Append("\"jsonFile\": \"").Append(EscapeJson(e.JsonFile.Replace('\\', '/'))).Append("\"");
                    if (e.Error != null)
                        sb.Append(", \"error\": \"").Append(EscapeJson(e.Error)).Append("\"");
                    sb.Append("}");
                    return sb.ToString();
                });

                DateTime? lastRefresh;
                lock (_auditLock) { lastRefresh = _lastAuditRefresh; }

                var json = new StringBuilder();
                json.Append("{\"status\": \"fresh\", ");
                json.Append("\"totalCount\": ").Append(blueprints.Count).Append(", ");
                json.Append("\"errorCount\": ").Append(errorCount).Append(", ");
                if (lastRefresh.HasValue)
                    json.Append("\"lastRefresh\": \"").Append(lastRefresh.Value.ToString("o")).Append("\", ");
                json.Append("\"blueprints\": [").Append(string.Join(",", bpJsons)).Append("]}");

                Respond(ctx, 200, "application/json; charset=utf-8", json.ToString());
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Blueprint Audit");
                sb.AppendLine();
                sb.AppendLine("**Status:** Fresh (all data up-to-date)");
                sb.Append("**Total Blueprints:** ").AppendLine(blueprints.Count.ToString());
                if (errorCount > 0)
                    sb.Append("**Errors:** ").AppendLine(errorCount.ToString());
                sb.AppendLine();

                sb.AppendLine("## Blueprints");
                foreach (var e in blueprints.OrderBy(b => b.Name))
                {
                    sb.Append("- **").Append(e.Name ?? "(unknown)").Append("**");
                    if (!string.IsNullOrEmpty(e.Path))
                        sb.Append(" — ").Append(e.Path);
                    if (e.Error != null)
                        sb.Append(" *(error: ").Append(e.Error).Append(")*");
                    sb.AppendLine();
                }

                Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
            }
        }

        private static string TruncateForJson(string s, int maxLength)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maxLength) return s;
            return s.Substring(0, maxLength) + "... [truncated]";
        }
    }
}
