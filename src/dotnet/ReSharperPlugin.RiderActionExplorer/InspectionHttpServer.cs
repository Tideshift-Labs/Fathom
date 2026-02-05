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

                _ = Task.Run(() => AcceptLoopAsync());

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
                    default:
                        Respond(ctx, 404, "text/plain",
                            "Not found. Try: /, /health, /files, /inspect?file=path, /blueprints?class=ClassName");
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
            var workItems = new List<(FileResult result, IPsiSourceFile source)>();
            var notFoundResults = new List<FileResult>();

            foreach (var fileParam in fileParams)
            {
                var key = NormalizePath(fileParam);
                var result = new FileResult { RequestedPath = fileParam };

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
            Parallel.ForEach(workItems, item =>
            {
                item.result.SyncResult = WaitForPsiSync(item.source);
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
            Parallel.ForEach(inspectableItems, item =>
            {
                var result = item.result;
                var sourceFile = item.source;

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

                        break;
                    }
                    catch (OperationCanceledException) when (attempt < MaxInspectionRetries)
                    {
                        Log.Warn("InspectionHttpServer: OperationCanceledException on " +
                                 result.ResolvedPath + ", retry " + attempt + "/" + MaxInspectionRetries);
                        Thread.Sleep(RetryDelayMs);
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

        // ── /blueprints ──

        private class BlueprintClassResult
        {
            public string Name;
            public string FilePath;
        }

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
            List<BlueprintClassResult> blueprints;
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

        private List<BlueprintClassResult> QueryDerivedBlueprints(
            string className, object assetsCache, VirtualFileSystemPath solutionDir,
            bool debug, out string debugInfo)
        {
            debugInfo = null;
            var debugSb = debug ? new StringBuilder() : null;
            var results = new List<BlueprintClassResult>();

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
                        results.Add(new BlueprintClassResult { Name = name, FilePath = filePath });

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
            string cacheStatus, List<BlueprintClassResult> blueprints,
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
            string cacheStatus, List<BlueprintClassResult> blueprints,
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
                "\"GET /inspect?file=path\": \"Run code inspection on file(s). Multiple: &file=a&file=b. Default output is markdown; add &format=json for JSON. Add &debug=true for diagnostics.\", " +
                "\"GET /blueprints?class=ClassName\": \"List UE5 Blueprint classes deriving from a C++ class. Add &format=json for JSON.\"" +
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
