using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using JetBrains.Application.Parts;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.Util;
using ReSharperPlugin.RiderActionExplorer.Formatting;
using ReSharperPlugin.RiderActionExplorer.Handlers;
using ReSharperPlugin.RiderActionExplorer.Services;

namespace ReSharperPlugin.RiderActionExplorer
{
    [SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
    public class InspectionHttpServer2
    {
        private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<InspectionHttpServer2>();

        private readonly Lifetime _lifetime;
        private readonly HttpListener _listener;
        private readonly IRequestHandler[] _handlers;

        public InspectionHttpServer2(Lifetime lifetime, ISolution solution)
            : this(lifetime, solution, ServerConfiguration.Default)
        {
        }

        public InspectionHttpServer2(Lifetime lifetime, ISolution solution, ServerConfiguration config)
        {
            _lifetime = lifetime;

            try
            {
                // Wire services
                var reflection = new ReflectionService(solution);
                var ueProject = new UeProjectService(solution, reflection, config);
                var fileIndex = new FileIndexService(solution);
                var psiSync = new PsiSyncService(config);
                var inspection = new InspectionService(solution, psiSync, config);
                var blueprintQuery = new BlueprintQueryService();
                var blueprintAudit = new BlueprintAuditService(ueProject, config);

                // Wire handlers
                _handlers = new IRequestHandler[]
                {
                    new IndexHandler(solution, config, ueProject),
                    new FilesHandler(solution, fileIndex),
                    new InspectHandler(solution, fileIndex, inspection),
                    new BlueprintsHandler(solution, reflection, blueprintQuery),
                    new BlueprintAuditHandler(ueProject, blueprintAudit),
                    new UeProjectHandler(solution, ueProject, reflection),
                };

                // Start listener
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{config.Port}/");
                _listener.Start();
                Log.Warn($"InspectionHttpServer2: listening on http://localhost:{config.Port}/");

                lifetime.OnTermination(() =>
                {
                    try { _listener.Stop(); } catch { }
                    try { _listener.Close(); } catch { }
                    Log.Warn("InspectionHttpServer2: stopped");
                });

                _ = Task.Run(() => AcceptLoopAsync());

                // Write marker file
                var markerPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    config.MarkerFileName);
                File.WriteAllText(markerPath,
                    $"InspectionHttpServer2 running\n" +
                    $"URL: http://localhost:{config.Port}/\n" +
                    $"Solution: {solution.SolutionDirectory}\n" +
                    $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");

                // Schedule on-boot staleness check
                _ = Task.Run(async () =>
                {
                    await Task.Delay(config.BootCheckDelayMs);
                    if (ueProject.IsUnrealProject())
                    {
                        blueprintAudit.CheckAndRefreshOnBoot();
                    }
                    else
                    {
                        blueprintAudit.SetBootCheckResult(
                            "Not an Unreal Engine project - Blueprint audit not applicable");
                        Log.Info("InspectionHttpServer2: Not a UE project, skipping Blueprint boot check");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"InspectionHttpServer2: failed to start on port {config.Port}");
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
                    "  /blueprints?class= - [UE5] Find derived Blueprints\n" +
                    "  /blueprint-audit   - [UE5] Blueprint audit data\n" +
                    "  /ue-project        - [UE5] Project detection diagnostics");
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
