using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Application.Parts;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
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

        // Services (created once, reused across restarts)
        private readonly ReflectionService _reflection;
        private readonly UeProjectService _ueProject;
        private readonly FileIndexService _fileIndex;
        private readonly PsiSyncService _psiSync;
        private readonly InspectionService _inspection;
        private readonly BlueprintQueryService _blueprintQuery;
        private readonly BlueprintAuditService _blueprintAudit;
        private readonly AssetRefProxyService _assetRefProxy;
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

            // Wire up RD protocol model
            try
            {
                var model = solution.GetProtocolSolution().GetCoRiderModel();

                if (!_envPortLocked)
                {
                    model.Port.Advise(lifetime, newPort =>
                    {
                        if (newPort <= 0 || newPort == _currentPort) return;
                        Log.Warn($"InspectionHttpServer2: port changed to {newPort} via settings");
                        StopServer();
                        StartServer(newPort);
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warn("InspectionHttpServer2: RD model not available (non-Rider host?): " + ex.Message);
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
                        new InspectHandler(_solution, _fileIndex, _inspection),
                        new BlueprintsHandler(_solution, _reflection, _blueprintQuery, _config),
                        new BlueprintAuditHandler(_ueProject, _blueprintAudit),
                        new BlueprintInfoHandler(_blueprintAudit, _assetRefProxy, _config),
                        new AssetRefHandler(_ueProject, _assetRefProxy),
                        new UeProjectHandler(_solution, _ueProject, _reflection),
                    };

                    _config.Port = port;
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://localhost:{port}/");
                    _listener.Start();
                    _currentPort = port;
                    Log.Warn($"InspectionHttpServer2: listening on http://localhost:{port}/");

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

                    // Fire success notification via RD
                    model?.ServerStatus.Fire(new ServerStatus(true, port,
                        $"Listening on http://localhost:{port}/"));

                    // Schedule on-boot staleness check
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(_config.BootCheckDelayMs);
                        if (_ueProject.IsUnrealProject())
                        {
                            _blueprintAudit.CheckAndRefreshOnBoot();
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
                    model?.ServerStatus.Fire(new ServerStatus(false, port, ex.Message));
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
                Log.Warn("InspectionHttpServer2: stopped");
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
                    "Not found. Available endpoints:\n" +
                    "  /              - List all endpoints\n" +
                    "  /health        - Server status\n" +
                    "  /files         - List source files\n" +
                    "  /inspect?file= - Code inspection\n" +
                    "  /blueprints?class=       - [UE5] Find derived Blueprints\n" +
                    "  /bp?file=                - [UE5] Blueprint composite info\n" +
                    "  /blueprint-audit         - [UE5] Blueprint audit data\n" +
                    "  /asset-refs/dependencies - [UE5] Asset dependencies\n" +
                    "  /asset-refs/referencers  - [UE5] Asset referencers\n" +
                    "  /asset-refs/status       - [UE5] UE editor connection status\n" +
                    "  /ue-project              - [UE5] Project detection diagnostics");
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
