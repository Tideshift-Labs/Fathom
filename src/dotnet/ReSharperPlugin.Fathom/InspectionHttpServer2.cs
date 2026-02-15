using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Application.Parts;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.Collections.Viewable;
using JetBrains.Rd.Base;
using JetBrains.ReSharper.Feature.Services.Protocol;
using JetBrains.Rider.Model;
using JetBrains.Util;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Handlers;
using ReSharperPlugin.Fathom.Mcp;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom
{
    [SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
    public class InspectionHttpServer2
    {
        private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<InspectionHttpServer2>();

        private readonly Lifetime _lifetime;
        private readonly ISolution _solution;
        private readonly object _lock = new();

        private HttpListener _listener;
        private IRequestHandler[] _handlers;
        private int _currentPort;
        private bool _envPortLocked;
        private JetBrains.Collections.Viewable.IScheduler _rdScheduler;
        private int _companionActionRunning; // 0 = idle, 1 = running

        // Services (created once, reused across restarts)
        private readonly ReflectionService _reflection;
        private readonly UeProjectService _ueProject;
        private readonly FileIndexService _fileIndex;
        private readonly PsiSyncService _psiSync;
        private readonly InspectionService _inspection;
        private readonly BlueprintQueryService _blueprintQuery;
        private readonly BlueprintAuditService _blueprintAudit;
        private readonly AssetRefProxyService _assetRefProxy;
        private readonly CodeStructureService _codeStructure;
        private readonly ClassIndexService _classIndex;
        private readonly CompanionPluginService _companionPlugin;
        private readonly ServerConfiguration _config;

        public InspectionHttpServer2(Lifetime lifetime, ISolution solution)
        {
            _lifetime = lifetime;
            _solution = solution;
            _config = ServerConfiguration.Default;

            // Wire services once
            _reflection = new ReflectionService(solution);
            _ueProject = new UeProjectService(solution, _reflection, _config);
            _fileIndex = new FileIndexService(solution);
            _psiSync = new PsiSyncService(_config);
            _inspection = new InspectionService(solution, _psiSync, _config);
            _blueprintQuery = new BlueprintQueryService();
            _blueprintAudit = new BlueprintAuditService(_ueProject, _config);
            _assetRefProxy = new AssetRefProxyService(_ueProject);
            _codeStructure = new CodeStructureService(solution);
            _classIndex = new ClassIndexService(solution, _fileIndex, _codeStructure);
            _companionPlugin = new CompanionPluginService(solution, _config, _ueProject);

            // Check for env var override (takes absolute priority)
            var envPort = Environment.GetEnvironmentVariable("RIDER_INSPECTOR_PORT");
            if (int.TryParse(envPort, out var port) && port > 0)
            {
                _envPortLocked = true;
                StartServer(port);
            }
            else
            {
                // Start on default port immediately; RD model will push settings port later
                StartServer(_config.Port);
            }

            // Wire up RD protocol model (all RD operations must run on the protocol scheduler thread)
            try
            {
                var protocolSolution = solution.GetProtocolSolution();
                _rdScheduler = protocolSolution.TryGetProto()?.Scheduler;

                if (_rdScheduler != null)
                {
                    _rdScheduler.Queue(() =>
                    {
                        try
                        {
                            var model = protocolSolution.GetFathomModel();

                            if (!_envPortLocked)
                            {
                                model.Port.Advise(lifetime, newPort =>
                                {
                                    if (newPort <= 0 || newPort == _currentPort) return;
                                    Log.Info($"InspectionHttpServer2: port changed to {newPort} via settings");
                                    StopServer();
                                    StartServer(newPort);
                                });
                            }

                            // Handle companion plugin install requests from frontend
                            model.InstallCompanionPlugin.Advise(lifetime, location =>
                            {
                                if (Interlocked.CompareExchange(ref _companionActionRunning, 1, 0) != 0)
                                {
                                    Log.Info("CompanionPlugin: install request ignored, another action is already running");
                                    return;
                                }
                                _ = Task.Run(() =>
                                {
                                    try
                                    {
                                        var installResult = _companionPlugin.Install(location);
                                        Log.Info($"CompanionPlugin install ({location}): success={installResult.success}, {installResult.message}");

                                        if (!installResult.success)
                                        {
                                            var detection = _companionPlugin.Detect();
                                            _rdScheduler.Queue(() =>
                                                model.CompanionPluginStatus(new CompanionPluginInfo(
                                                    Enum.TryParse<CompanionPluginStatus>(detection.Status, out var s)
                                                        ? s : CompanionPluginStatus.NotInstalled,
                                                    detection.InstalledVersion,
                                                    detection.BundledVersion,
                                                    detection.InstallLocation ?? "None",
                                                    $"Installation failed. {installResult.message}")));
                                            return;
                                        }

                                        // Attempt project file regeneration so the plugin appears in the solution explorer
                                        var ueInfo = _ueProject.GetUeProjectInfo();
                                        var regenResult = _companionPlugin.RegenerateProjectFiles(ueInfo);
                                        Log.Info($"CompanionPlugin regen: success={regenResult.success}, {regenResult.message}");

                                        var det = _companionPlugin.Detect();
                                        var statusMessage = regenResult.success
                                            ? $"Installed to {location} and project files regenerated. Click Build Now to compile."
                                            : $"Installed to {location} but project file regeneration failed: {regenResult.message}. Click Build Now to compile.";

                                        _rdScheduler.Queue(() =>
                                            model.CompanionPluginStatus(new CompanionPluginInfo(
                                                CompanionPluginStatus.Installed,
                                                det.InstalledVersion,
                                                det.BundledVersion,
                                                det.InstallLocation ?? location,
                                                statusMessage)));
                                    }
                                    finally
                                    {
                                        Interlocked.Exchange(ref _companionActionRunning, 0);
                                    }
                                });
                            });

                            // Handle companion plugin build requests from frontend
                            model.BuildCompanionPlugin.Advise(lifetime, unit =>
                            {
                                if (Interlocked.CompareExchange(ref _companionActionRunning, 1, 0) != 0)
                                {
                                    Log.Info("CompanionPlugin: build request ignored, another action is already running");
                                    return;
                                }
                                _ = Task.Run(() =>
                                {
                                    try
                                    {
                                        var ueInfo = _ueProject.GetUeProjectInfo();
                                        var det0 = _companionPlugin.Detect();
                                        var useRunUat = det0.InstallLocation == "Engine" || det0.InstallLocation == "Both";

                                        // Stream build output lines to the frontend
                                        Action<string> onOutput = line =>
                                            _rdScheduler.Queue(() => model.CompanionBuildLog(line));

                                        var buildResult = useRunUat
                                            ? _companionPlugin.BuildEnginePlugin(ueInfo, onOutput)
                                            : _companionPlugin.BuildEditorTarget(ueInfo, onOutput);
                                        Log.Info($"CompanionPlugin build (RunUAT={useRunUat}): success={buildResult.success}, {buildResult.message}");

                                        // Signal build completion
                                        _rdScheduler.Queue(() => model.CompanionBuildFinished(buildResult.success));

                                        var det = _companionPlugin.Detect();

                                        if (buildResult.success)
                                        {
                                            _rdScheduler.Queue(() =>
                                                model.CompanionPluginStatus(new CompanionPluginInfo(
                                                    CompanionPluginStatus.UpToDate,
                                                    det.InstalledVersion,
                                                    det.BundledVersion,
                                                    det.InstallLocation ?? "None",
                                                    buildResult.message)));
                                        }
                                        else
                                        {
                                            _rdScheduler.Queue(() =>
                                                model.CompanionPluginStatus(new CompanionPluginInfo(
                                                    CompanionPluginStatus.Installed,
                                                    det.InstalledVersion,
                                                    det.BundledVersion,
                                                    det.InstallLocation ?? "None",
                                                    $"Build failed: {buildResult.message}")));
                                        }
                                    }
                                    finally
                                    {
                                        Interlocked.Exchange(ref _companionActionRunning, 0);
                                    }
                                });
                            });
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("InspectionHttpServer2: RD model wire-up failed: " + ex.Message);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Info("InspectionHttpServer2: RD model not available (non-Rider host?): " + ex.Message);
            }
        }

        private void StartServer(int port)
        {
            lock (_lock)
            {
                FathomModel model = null;
                try
                {
                    model = _solution.GetProtocolSolution().GetFathomModel();
                }
                catch { /* RD model may not be available */ }

                try
                {
                    // Wire handlers
                    var mcpServer = new FathomMcpServer(port);
                    _handlers = new IRequestHandler[]
                    {
                        new McpHandler(mcpServer),
                        new IndexHandler(_solution, _config, _ueProject),
                        new FilesHandler(_solution, _fileIndex),
                        new ClassesHandler(_classIndex, _config),
                        new InspectHandler(_solution, _fileIndex, _inspection),
                        new DescribeCodeHandler(_solution, _fileIndex, _codeStructure),
                        new DebugPsiTreeHandler(_solution, _fileIndex),
                        new BlueprintsHandler(_solution, _reflection, _blueprintQuery, _config),
                        new BlueprintAuditHandler(_ueProject, _blueprintAudit),
                        new BlueprintInfoHandler(_blueprintAudit, _assetRefProxy, _ueProject, _config),
                        new AssetRefHandler(_ueProject, _assetRefProxy),
                        new AssetSearchHandler(_ueProject, _assetRefProxy, _config),
                        new AssetShowHandler(_ueProject, _assetRefProxy, _config),
                        new UeProjectHandler(_solution, _ueProject, _reflection),
                        new DebugSymbolHandler(_solution),
                    };

                    _config.Port = port;
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://localhost:{port}/");
                    _listener.Start();
                    _currentPort = port;
                    Log.Info($"InspectionHttpServer2: listening on http://localhost:{port}/");

                    _lifetime.OnTermination(() =>
                    {
                        StopServer();
                    });

                    _ = Task.Run(() => AcceptLoopAsync());

                    // Write global marker file (legacy)
                    var markerPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        _config.MarkerFileName);
                    File.WriteAllText(markerPath,
                        $"InspectionHttpServer2 running\n" +
                        $"URL: http://localhost:{port}/\n" +
                        $"Solution: {_solution.SolutionDirectory}\n" +
                        $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");

                    // Write local solution marker for MCP discovery
                    try
                    {
                        var metaDir = GetFathomMetadataDir();
                        Directory.CreateDirectory(metaDir);
                        var localMarkerPath = Path.Combine(metaDir, ".fathom-server.json");
                        var json = $"{{\n  \"port\": {port},\n  \"mcpEndpoint\": \"http://localhost:{port}/mcp\",\n  \"mcpTransport\": \"streamable-http\",\n  \"solution\": \"{_solution.SolutionDirectory.FullPath.Replace("\\", "\\\\")}\",\n  \"started\": \"{DateTime.Now:O}\"\n}}";
                        File.WriteAllText(localMarkerPath, json);

                        _lifetime.OnTermination(() =>
                        {
                            try { File.Delete(localMarkerPath); } catch { }
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "InspectionHttpServer2: failed to write local marker file");
                    }

                    // Auto-provision MCP config files for AI agents
                    WriteMcpConfigFiles(port, model);

                    // Fire success notification via RD (must be on protocol scheduler thread)
                    if (model != null && _rdScheduler != null)
                        _rdScheduler.Queue(() =>
                            model.ServerStatus.Fire(new ServerStatus(true, port,
                                $"Listening on http://localhost:{port}/")));

                    // Schedule on-boot staleness check and companion plugin detection
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(_config.BootCheckDelayMs);
                        Log.Info($"InspectionHttpServer2: Boot check task running (IsUE={_ueProject.IsUnrealProject()})");
                        if (_ueProject.IsUnrealProject())
                        {
                            _blueprintAudit.CheckAndRefreshOnBoot();

                            // Check companion plugin after an additional delay
                            var extraDelay = _config.CompanionCheckDelayMs - _config.BootCheckDelayMs;
                            if (extraDelay > 0)
                                await Task.Delay(extraDelay);

                            try
                            {
                                var detection = _companionPlugin.Detect();
                                if (detection.Status != "UpToDate")
                                {
                                    Log.Info($"CompanionPlugin: {detection.Status} location={detection.InstallLocation} (installed={detection.InstalledVersion}, bundled={detection.BundledVersion})");
                                    if (model != null)
                                        _rdScheduler.Queue(() =>
                                            model.CompanionPluginStatus(new CompanionPluginInfo(
                                                Enum.TryParse<CompanionPluginStatus>(detection.Status, out var s)
                                                    ? s : CompanionPluginStatus.NotInstalled,
                                                detection.InstalledVersion,
                                                detection.BundledVersion,
                                                detection.InstallLocation ?? "None",
                                                detection.Message)));
                                }
                            }
                            catch (Exception cpEx)
                            {
                                Log.Warn("CompanionPlugin detection failed: " + cpEx.Message);
                            }
                        }
                        else
                        {
                            _blueprintAudit.SetBootCheckResult(
                                "Not an Unreal Engine project - Blueprint audit not applicable");
                            Log.Info("InspectionHttpServer2: Not a UE project, skipping Blueprint boot check");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"InspectionHttpServer2: failed to start on port {port}");
                    if (model != null && _rdScheduler != null)
                        _rdScheduler.Queue(() =>
                            model.ServerStatus.Fire(new ServerStatus(false, port, ex.Message)));
                }
            }
        }

        private void StopServer()
        {
            lock (_lock)
            {
                if (_listener == null) return;
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
                _listener = null;
                Log.Info("InspectionHttpServer2: stopped");
            }
        }

        private void WriteMcpConfigFiles(int port, FathomModel model)
        {
            var written = new List<string>();
            var solutionDir = _solution.SolutionDirectory.FullPath;

            var fathomEntry = new JsonObject
            {
                ["type"] = "http",
                ["url"] = $"http://localhost:{port}/mcp"
            };

            // Always write {solutionDir}/.mcp.json
            try
            {
                var path = Path.Combine(solutionDir, ".mcp.json");
                MergeMcpEntry(path, "mcpServers", fathomEntry);
                written.Add(".mcp.json");
            }
            catch (Exception ex)
            {
                Log.Warn("WriteMcpConfigFiles: failed to write .mcp.json: " + ex.Message);
            }

            // Write .vscode/mcp.json only if .vscode/ exists
            var vscodeDir = Path.Combine(solutionDir, ".vscode");
            if (Directory.Exists(vscodeDir))
            {
                try
                {
                    var path = Path.Combine(vscodeDir, "mcp.json");
                    MergeMcpEntry(path, "servers", fathomEntry);
                    written.Add(".vscode/mcp.json");
                }
                catch (Exception ex)
                {
                    Log.Warn("WriteMcpConfigFiles: failed to write .vscode/mcp.json: " + ex.Message);
                }
            }

            // Write .cursor/mcp.json only if .cursor/ exists
            var cursorDir = Path.Combine(solutionDir, ".cursor");
            if (Directory.Exists(cursorDir))
            {
                try
                {
                    var path = Path.Combine(cursorDir, "mcp.json");
                    MergeMcpEntry(path, "mcpServers", fathomEntry);
                    written.Add(".cursor/mcp.json");
                }
                catch (Exception ex)
                {
                    Log.Warn("WriteMcpConfigFiles: failed to write .cursor/mcp.json: " + ex.Message);
                }
            }

            // Merge into opencode.json only if it already exists (OpenCode uses "mcp" root key + "type":"remote")
            var openCodePath = Path.Combine(solutionDir, "opencode.json");
            if (File.Exists(openCodePath))
            {
                try
                {
                    var openCodeEntry = new JsonObject
                    {
                        ["type"] = "remote",
                        ["url"] = $"http://localhost:{port}/mcp"
                    };
                    MergeMcpEntry(openCodePath, "mcp", openCodeEntry);
                    written.Add("opencode.json");
                }
                catch (Exception ex)
                {
                    Log.Warn("WriteMcpConfigFiles: failed to write opencode.json: " + ex.Message);
                }
            }

            if (written.Count > 0)
            {
                var message = "Added MCP entry to " + string.Join(", ", written);
                Log.Info("WriteMcpConfigFiles: " + message);

                if (model != null && _rdScheduler != null)
                    _rdScheduler.Queue(() => model.McpConfigStatus.Fire(message));
            }
        }

        /// <summary>
        /// Returns the directory where Fathom should store transient metadata files
        /// (.fathom-server.json, caches, etc.). For UE projects this resolves to
        /// Saved/Fathom/ which is already VCS-ignored, keeping the project root clean.
        /// For other project types, falls back to the solution directory.
        /// </summary>
        private string GetFathomMetadataDir()
        {
            var solutionDir = _solution.SolutionDirectory.FullPath;

            // UE project: has a .uproject file and a Saved/ directory
            if (Directory.GetFiles(solutionDir, "*.uproject").Length > 0)
            {
                var savedDir = Path.Combine(solutionDir, "Saved");
                if (Directory.Exists(savedDir))
                    return Path.Combine(savedDir, "Fathom");
            }

            return solutionDir;
        }

        private static void MergeMcpEntry(string filePath, string rootKey, JsonObject fathomEntry)
        {
            JsonObject root;

            if (File.Exists(filePath))
            {
                var existing = File.ReadAllText(filePath);
                root = JsonNode.Parse(existing)?.AsObject() ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            if (root[rootKey] is not JsonObject servers)
            {
                servers = new JsonObject();
                root[rootKey] = servers;
            }

            // Deep-clone the entry so each file gets its own node instance
            servers["fathom"] = JsonNode.Parse(fathomEntry.ToJsonString());

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(filePath, root.ToJsonString(options));
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
                    Log.Error(ex, "InspectionHttpServer2: error in accept loop");
                }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";

                foreach (var handler in _handlers)
                {
                    if (handler.CanHandle(path))
                    {
                        handler.Handle(ctx);
                        return;
                    }
                }

                HttpHelpers.Respond(ctx, 404, "text/plain",
                    "Not found. Visit / for the full API reference.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "InspectionHttpServer2: unhandled error in request handler");
                try
                {
                    HttpHelpers.Respond(ctx, 500, "text/plain", ex.GetType().Name + ": " + ex.Message);
                }
                catch { /* give up */ }
            }
        }
    }
}
