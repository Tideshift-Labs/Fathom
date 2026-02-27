using System;
using System.Net;
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
    public class FathomRiderHttpServer
    {
        private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<FathomRiderHttpServer>();

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
        private readonly SymbolSearchService _symbolSearch;
        private readonly ServerConfiguration _config;

        private CompanionPluginOrchestrator _companionOrchestrator;
        private readonly ServerMarkerWriter _markerWriter;
        private readonly BootCheckOrchestrator _bootChecks;

        public FathomRiderHttpServer(Lifetime lifetime, ISolution solution)
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
            _symbolSearch = new SymbolSearchService(solution);
            _markerWriter = new ServerMarkerWriter(solution, _config, lifetime);
            _bootChecks = new BootCheckOrchestrator(_ueProject, _blueprintAudit, _companionPlugin, _config);

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

                            _companionOrchestrator = new CompanionPluginOrchestrator(
                                _companionPlugin, _ueProject, _reflection, _rdScheduler);

                            if (!_envPortLocked)
                            {
                                model.Port.Advise(lifetime, newPort =>
                                {
                                    if (newPort <= 0 || newPort == _currentPort) return;
                                    Log.Info($"FathomRiderHttpServer: port changed to {newPort} via settings");
                                    StopServer();
                                    StartServer(newPort);
                                });
                            }

                            // Handle companion plugin install requests from frontend
                            model.InstallCompanionPlugin.Advise(lifetime,
                                location => _companionOrchestrator.HandleInstallRequest(location, model));

                            // Handle companion plugin build requests from frontend
                            model.BuildCompanionPlugin.Advise(lifetime,
                                _ => _companionOrchestrator.HandleBuildRequest(model));

                            // Schedule on-boot staleness check and companion plugin detection.
                            // Must run here (not in StartServer) so that model and _rdScheduler
                            // are guaranteed non-null when the boot check fires the RD sink.
                            _bootChecks.Run(model, _rdScheduler);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn("FathomRiderHttpServer: RD model wire-up failed: " + ex.Message);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Info("FathomRiderHttpServer: RD model not available (non-Rider host?): " + ex.Message);
            }
        }

        private (HttpListener listener, int boundPort) BindListener(int port)
        {
            const int maxPortAttempts = 10;
            for (int attempt = 0; attempt < maxPortAttempts; attempt++)
            {
                var candidatePort = port + attempt;
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://localhost:{candidatePort}/");
                try
                {
                    candidate.Start();
                    if (attempt > 0)
                        Log.Info($"FathomRiderHttpServer: port {port} was in use, bound to {candidatePort} instead");
                    return (candidate, candidatePort);
                }
                catch (HttpListenerException) when (attempt < maxPortAttempts - 1)
                {
                    Log.Info($"FathomRiderHttpServer: port {candidatePort} is in use, trying next");
                    try { candidate.Close(); } catch { } // TODO: log at Trace level for debuggability
                }
            }

            throw new InvalidOperationException(
                $"FathomRiderHttpServer: failed to bind on ports {port}-{port + maxPortAttempts - 1}");
        }

        private IRequestHandler[] CreateHandlers(int port)
        {
            var mcpServer = new FathomMcpServer(port);
            return new IRequestHandler[]
            {
                new McpHandler(mcpServer),
                new IndexHandler(_solution, _config, _ueProject, _assetRefProxy),
                new FilesHandler(_solution, _fileIndex),
                new ClassesHandler(_classIndex, _config),
                new InspectHandler(_solution, _fileIndex, _inspection),
                new DescribeCodeHandler(_solution, _fileIndex, _codeStructure),
                new DebugPsiTreeHandler(_solution, _fileIndex),
                new BlueprintsHandler(_solution, _reflection, _blueprintQuery, _config),
                new BlueprintAuditHandler(_ueProject, _blueprintAudit),
                new BlueprintInfoHandler(_blueprintAudit, _assetRefProxy, _ueProject, _config),
                new AssetRefHandler(_ueProject, _assetRefProxy),
                new LiveCodingHandler(_ueProject, _assetRefProxy),
                new AssetSearchHandler(_ueProject, _assetRefProxy, _config),
                new AssetShowHandler(_ueProject, _assetRefProxy, _config),
                new UeProjectHandler(_solution, _ueProject, _reflection),
                new SymbolsHandler(_symbolSearch, _config),
                new DebugBuildLifecycleHandler(_solution, _reflection),
            };
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
                catch { } // TODO: log at Trace level for debuggability

                try
                {
                    var (listener, boundPort) = BindListener(port);
                    _listener = listener;
                    port = boundPort;

                    _handlers = CreateHandlers(port);
                    _config.Port = port;
                    _currentPort = port;
                    Log.Info($"FathomRiderHttpServer: listening on http://localhost:{port}/");

                    _lifetime.OnTermination(() =>
                    {
                        StopServer();
                    });

                    // TODO: pass _lifetime.ToCancellationToken() for clean shutdown
                    _ = Task.Run(() => AcceptLoopAsync());

                    _markerWriter.WriteMarkerFiles(port);

                    // Auto-provision MCP config files for AI agents
                    McpConfigWriter.WriteMcpConfigFiles(port, _solution.SolutionDirectory.FullPath, model, _rdScheduler);

                    // Fire success notification via RD (must be on protocol scheduler thread)
                    if (model != null && _rdScheduler != null)
                        _rdScheduler.Queue(() =>
                            model.ServerStatus.Fire(new ServerStatus(true, port,
                                $"Listening on http://localhost:{port}/")));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"FathomRiderHttpServer: failed to start on port {port}");
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
                try { _listener.Stop(); } catch { } // TODO: log at Trace level for debuggability
                try { _listener.Close(); } catch { } // TODO: log at Trace level for debuggability
                _listener = null;
                Log.Info("FathomRiderHttpServer: stopped");
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
                    Log.Error(ex, "FathomRiderHttpServer: error in accept loop");
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
                Log.Error(ex, "FathomRiderHttpServer: unhandled error in request handler");
                try
                {
                    HttpHelpers.Respond(ctx, 500, "text/plain", ex.GetType().Name + ": " + ex.Message);
                }
                catch { } // TODO: log at Trace level for debuggability
            }
        }
    }
}
