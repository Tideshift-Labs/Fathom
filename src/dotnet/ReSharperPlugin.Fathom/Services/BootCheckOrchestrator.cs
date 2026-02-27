using System;
using System.Threading.Tasks;
using JetBrains.Rider.Model;
using JetBrains.Util;

namespace ReSharperPlugin.Fathom.Services;

/// <summary>
/// Runs delayed boot-time checks: blueprint audit staleness and companion
/// plugin detection. Extracted from FathomRiderHttpServer to keep the
/// server class focused on HTTP lifecycle.
/// </summary>
public class BootCheckOrchestrator
{
    private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<BootCheckOrchestrator>();

    private readonly UeProjectService _ueProject;
    private readonly BlueprintAuditService _blueprintAudit;
    private readonly CompanionPluginService _companionPlugin;
    private readonly ServerConfiguration _config;

    public BootCheckOrchestrator(
        UeProjectService ueProject,
        BlueprintAuditService blueprintAudit,
        CompanionPluginService companionPlugin,
        ServerConfiguration config)
    {
        _ueProject = ueProject;
        _blueprintAudit = blueprintAudit;
        _companionPlugin = companionPlugin;
        _config = config;
    }

    /// <summary>
    /// Schedules boot-time checks on a background thread. For UE projects
    /// this runs audit staleness detection followed by companion plugin
    /// version detection after a configurable delay.
    /// </summary>
    public void Run(FathomModel model, JetBrains.Collections.Viewable.IScheduler rdScheduler)
    {
        // TODO: pass cancellation token for clean shutdown
        _ = Task.Run(async () =>
        {
            await Task.Delay(_config.BootCheckDelayMs);
            Log.Info($"BootCheckOrchestrator: running (IsUE={_ueProject.IsUnrealProject()})");

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
                    Log.Info($"CompanionPlugin: {detection.Status} location={detection.InstallLocation} (installed={detection.InstalledVersion}, bundled={detection.BundledVersion})");
                    if (detection.Status != "UpToDate")
                    {
                        if (model != null && rdScheduler != null)
                            rdScheduler.Queue(() =>
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
                Log.Info("BootCheckOrchestrator: Not a UE project, skipping Blueprint boot check");
            }
        });
    }
}
