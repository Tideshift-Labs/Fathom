using System;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Rider.Model;
using JetBrains.Util;

namespace ReSharperPlugin.Fathom.Services;

/// <summary>
/// Coordinates companion plugin install and build workflows, forwarding
/// status updates to the Rider frontend via the RD protocol model.
/// </summary>
public class CompanionPluginOrchestrator
{
    private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<CompanionPluginOrchestrator>();

    private readonly CompanionPluginService _companionPlugin;
    private readonly UeProjectService _ueProject;
    private readonly ReflectionService _reflection;
    private readonly JetBrains.Collections.Viewable.IScheduler _rdScheduler;
    private int _companionActionRunning; // 0 = idle, 1 = running

    public CompanionPluginOrchestrator(
        CompanionPluginService companionPlugin,
        UeProjectService ueProject,
        ReflectionService reflection,
        JetBrains.Collections.Viewable.IScheduler rdScheduler)
    {
        _companionPlugin = companionPlugin;
        _ueProject = ueProject;
        _reflection = reflection;
        _rdScheduler = rdScheduler;
    }

    public void HandleInstallRequest(string location, FathomModel model)
    {
        if (_reflection.IsRiderLinkInstallationInProgress())
        {
            Log.Info("CompanionPlugin: install request blocked, RiderLink installation is in progress");
            model.CompanionPluginStatus(new CompanionPluginInfo(
                CompanionPluginStatus.NotInstalled, "", "", "None",
                "Cannot proceed right now. Rider is installing the RiderLink plugin, which also uses Unreal Build Tool. Please wait for the RiderLink installation to finish, then try again."));
            return;
        }

        if (Interlocked.CompareExchange(ref _companionActionRunning, 1, 0) != 0)
        {
            Log.Info("CompanionPlugin: install request ignored, another action is already running");
            return;
        }

        // TODO: pass cancellation token for clean shutdown
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
    }

    public void HandleBuildRequest(FathomModel model)
    {
        if (_reflection.IsRiderLinkInstallationInProgress())
        {
            Log.Info("CompanionPlugin: build request blocked, RiderLink installation is in progress");
            model.CompanionBuildFinished(false);
            model.CompanionPluginStatus(new CompanionPluginInfo(
                CompanionPluginStatus.Installed, "", "", "None",
                "Cannot proceed right now. Rider is installing the RiderLink plugin, which also uses Unreal Build Tool. Please wait for the RiderLink installation to finish, then try again."));
            return;
        }

        if (Interlocked.CompareExchange(ref _companionActionRunning, 1, 0) != 0)
        {
            Log.Info("CompanionPlugin: build request ignored, another action is already running");
            return;
        }

        // TODO: pass cancellation token for clean shutdown
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
    }
}
