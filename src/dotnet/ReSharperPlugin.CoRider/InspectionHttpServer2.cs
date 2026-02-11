using System;
using System.IO;
using System.Net;
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
using ReSharperPlugin.CoRider.Formatting;
using ReSharperPlugin.CoRider.Handlers;
using ReSharperPlugin.CoRider.Services;

namespace ReSharperPlugin.CoRider
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
                            var model = protocolSolution.GetCoRiderModel();

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
                            model.InstallCompanionPlugin.Advise(lifetime, unit =>
                            {
                                _ = Task.Run(() =>
                                {
                                    var installResult = _companionPlugin.Install();
                                    Log.Info($"CompanionPlugin install: success={installResult.success}, {installResult.message}");

                                    if (!installResult.success)
                                    {
                                        var detection = _companionPlugin.Detect();
                                        _rdScheduler.Queue(() =>
                                            model.CompanionPluginStatus(new CompanionPluginInfo(
                                                Enum.TryParse<CompanionPluginStatus>(detection.Status, out var s)
                                                    ? s : CompanionPluginStatus.NotInstalled,
                                                detection.InstalledVersion,
                                                detection.BundledVersion,
                                                $"Installation failed. {installResult.message}")));
                                        return;
                                    }

                                    // Attempt project file regeneration so the plugin appears in the solution explorer
                                    var ueInfo = _ueProject.GetUeProjectInfo();
                                    var regenResult = _companionPlugin.RegenerateProjectFiles(ueInfo);
                                    Log.Info($"CompanionPlugin regen: success={regenResult.success}, {regenResult.message}");

                                    var det = _companionPlugin.Detect();
                                    var statusMessage = regenResult.success
                                        ? "Installed and project files regenerated. Click Build Now to compile."
                                        : $"Installed but project file regeneration failed: {regenResult.message}. Click Build Now to compile.";

                                    _rdScheduler.Queue(() =>
                                        model.CompanionPluginStatus(new CompanionPluginInfo(
                                            CompanionPluginStatus.Installed,
                                            det.InstalledVersion,
                                            det.BundledVersion,
                                            statusMessage)));
                                });
                            });

                            // Handle companion plugin build requests from frontend
                            model.BuildCompanionPlugin.Advise(lifetime, unit =>
                            {
                                _ = Task.Run(() =>
                                {
                                    var ueInfo = _ueProject.GetUeProjectInfo();
                                    var buildResult = _companionPlugin.BuildEditorTarget(ueInfo);
                                    Log.Info($"CompanionPlugin build: success={buildResult.success}, {buildResult.message}");

                                    var det = _companionPlugin.Detect();

                                    if (buildResult.success)
                                    {
                                        _rdScheduler.Queue(() =>
                                            model.CompanionPluginStatus(new CompanionPluginInfo(
                                                CompanionPluginStatus.UpToDate,
                                                det.InstalledVersion,
                                                det.BundledVersion,
                                                buildResult.message)));
                                    }
                                    else
                                    {
                                        _rdScheduler.Queue(() =>
                                            model.CompanionPluginStatus(new CompanionPluginInfo(
                                                CompanionPluginStatus.Installed,
                                                det.InstalledVersion,
                                                det.BundledVersion,
                                                $"Build failed: {buildResult.message}")));
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
                CoRiderModel model = null;
                try
                {
                    model = _solution.GetProtocolSolution().GetCoRiderModel();
                }
                catch { /* RD model may not be available */ }

                try
                {
                    // Wire handlers
                    _handlers = new IRequestHandler[]
                    {
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
                        var localMarkerPath = _solution.SolutionDirectory.Combine(".corider-server.json");
                        var json = $"{{\n  \"port\": {port},\n  \"solution\": \"{_solution.SolutionDirectory.FullPath.Replace("\\", "\\\\")}\",\n  \"started\": \"{DateTime.Now:O}\"\n}}";
                        File.WriteAllText(localMarkerPath.FullPath, json);

                        _lifetime.OnTermination(() =>
                        {
                            try { File.Delete(localMarkerPath.FullPath); } catch { }
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "InspectionHttpServer2: failed to write local marker file");
                    }

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
                                    Log.Info($"CompanionPlugin: {detection.Status} (installed={detection.InstalledVersion}, bundled={detection.BundledVersion})");
                                    if (model != null)
                                        _rdScheduler.Queue(() =>
                                            model.CompanionPluginStatus(new CompanionPluginInfo(
                                                Enum.TryParse<CompanionPluginStatus>(detection.Status, out var s)
                                                    ? s : CompanionPluginStatus.NotInstalled,
                                                detection.InstalledVersion,
                                                detection.BundledVersion,
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
