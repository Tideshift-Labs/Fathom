using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using JetBrains.ProjectModel;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Models;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

public class UeProjectHandler : IRequestHandler
{
    private readonly ISolution _solution;
    private readonly UeProjectService _ueProject;
    private readonly ReflectionService _reflection;
    private readonly BlueprintAuditService _auditService;
    private readonly AssetRefProxyService _assetRefProxy;
    private readonly CompanionPluginService _companionPlugin;
    private readonly int _serverPort;

    public UeProjectHandler(
        ISolution solution,
        UeProjectService ueProject,
        ReflectionService reflection,
        BlueprintAuditService auditService,
        AssetRefProxyService assetRefProxy,
        CompanionPluginService companionPlugin,
        int serverPort)
    {
        _solution = solution;
        _ueProject = ueProject;
        _reflection = reflection;
        _auditService = auditService;
        _assetRefProxy = assetRefProxy;
        _companionPlugin = companionPlugin;
        _serverPort = serverPort;
    }

    public bool CanHandle(string path) => path == "/ue-project";

    public void Handle(HttpListenerContext ctx)
    {
        var format = HttpHelpers.GetFormat(ctx);
        var info = BuildProjectStatus();

        if (format == "json")
        {
            HttpHelpers.RespondWithFormat(ctx, format, 200, null, info);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# UE Project Info");
        sb.AppendLine();

        sb.AppendLine("## Versions");
        sb.AppendLine($"- Fathom: {info.FathomVersion}");
        sb.AppendLine($"- FathomUELink: {info.FathomUELinkVersion}");
        sb.AppendLine($"- MCP endpoint: {info.McpEndpoint}");
        sb.AppendLine();

        sb.AppendLine("## Project");
        sb.AppendLine($"- IsUnrealProject: {info.IsUnrealProject}");
        sb.AppendLine($"- UProjectPath: {info.UProjectPath ?? "(null)"}");
        sb.AppendLine($"- ProjectDirectory: {info.ProjectDirectory ?? "(null)"}");
        sb.AppendLine($"- EnginePath: {info.EnginePath ?? "(null)"}");
        sb.AppendLine($"- EngineVersion: {info.EngineVersion ?? "(null)"}");
        sb.AppendLine($"- CommandletExePath: {info.CommandletExePath ?? "(null)"}");
        sb.AppendLine($"- UnrealBuildToolDllPath: {info.UnrealBuildToolDllPath ?? "(null)"}");
        sb.AppendLine($"- EditorTargetName: {info.EditorTargetName ?? "(null)"}");
        if (info.Error != null)
            sb.AppendLine($"- Error: {info.Error}");
        sb.AppendLine();

        sb.AppendLine("## UE Editor Connection (FathomUELink)");
        sb.AppendLine($"- Status: {info.UeLink.Status}");
        if (info.UeLink.Connected)
        {
            sb.AppendLine($"- Port: {info.UeLink.Port}");
            sb.AppendLine($"- PID: {info.UeLink.Pid}");
        }
        sb.AppendLine();

        sb.AppendLine("## Audit");
        if (info.Audit != null)
        {
            sb.AppendLine($"- Directory: {info.Audit.AuditDirectory}");
            sb.AppendLine($"- Schema version: v{info.Audit.SchemaVersion}");
            sb.AppendLine("- Supported asset types:");
            foreach (var t in info.Audit.SupportedAssetTypes)
                sb.AppendLine($"  - {t.Name}: {t.Description}");
            sb.AppendLine($"- Total audited: {info.Audit.TotalAudited}");
            sb.AppendLine($"- Blueprints: {info.Audit.BlueprintCount}");
            sb.AppendLine($"- DataTables: {info.Audit.DataTableCount}");
            sb.AppendLine($"- DataAssets: {info.Audit.DataAssetCount}");
            sb.AppendLine($"- Structures: {info.Audit.StructureCount}");
            sb.AppendLine($"- ControlRigs: {info.Audit.ControlRigCount}");
            sb.AppendLine($"- Materials: {info.Audit.MaterialCount}");
            sb.AppendLine($"- Stale: {info.Audit.StaleCount}");
            sb.AppendLine($"- Errors: {info.Audit.ErrorCount}");
        }
        else
        {
            sb.AppendLine("- Not available (not a UE project or audit directory missing)");
        }

        if (HttpHelpers.IsDebug(ctx))
            AppendDebugInfo(sb);

        HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
    }

    private ProjectStatusInfo BuildProjectStatus()
    {
        var ueInfo = _ueProject.GetUeProjectInfo();

        var info = new ProjectStatusInfo
        {
            IsUnrealProject = ueInfo.IsUnrealProject,
            UProjectPath = ueInfo.UProjectPath,
            ProjectDirectory = ueInfo.ProjectDirectory,
            EnginePath = ueInfo.EnginePath,
            EngineVersion = ueInfo.EngineVersion,
            CommandletExePath = ueInfo.CommandletExePath,
            UnrealBuildToolDllPath = ueInfo.UnrealBuildToolDllPath,
            EditorTargetName = ueInfo.EditorTargetName,
            Error = ueInfo.Error,
            FathomVersion = ServerConfiguration.FathomVersion,
            FathomUELinkVersion = _companionPlugin.GetBundledVersion(),
            McpEndpoint = $"http://localhost:{_serverPort}/mcp",
        };

        // UE editor link status
        var linkStatus = _assetRefProxy.GetStatus();
        info.UeLink = new UeLinkStatus
        {
            Connected = linkStatus.Connected,
            Port = linkStatus.Port,
            Pid = linkStatus.Pid,
            Status = linkStatus.Connected
                ? $"Connected (port {linkStatus.Port}, PID {linkStatus.Pid})"
                : linkStatus.Message ?? "Not connected",
        };

        // Audit stats
        if (ueInfo.IsUnrealProject && !string.IsNullOrEmpty(ueInfo.ProjectDirectory))
        {
            info.Audit = BuildAuditInfo(ueInfo);
        }

        return info;
    }

    private AuditInfo BuildAuditInfo(UeProjectInfo ueInfo)
    {
        var auditData = _auditService.GetAuditData();

        var version = BlueprintAuditService.AuditSchemaVersion;
        var auditDir = Path.Combine(ueInfo.ProjectDirectory, "Saved", "Fathom", "Audit", $"v{version}");

        return new AuditInfo
        {
            AuditDirectory = auditDir,
            SchemaVersion = version,
            SupportedAssetTypes = AuditCapabilities.SupportedTypes,
            TotalAudited = auditData.TotalCount,
            BlueprintCount = auditData.TotalCount
                             - auditData.DataTableCount
                             - auditData.DataAssetCount
                             - auditData.StructureCount
                             - auditData.ControlRigCount
                             - auditData.MaterialCount,
            DataTableCount = auditData.DataTableCount,
            DataAssetCount = auditData.DataAssetCount,
            StructureCount = auditData.StructureCount,
            ControlRigCount = auditData.ControlRigCount,
            MaterialCount = auditData.MaterialCount,
            StaleCount = auditData.StaleCount,
            ErrorCount = auditData.ErrorCount,
        };
    }

    private void AppendDebugInfo(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("# Debug: Assembly Discovery");
        sb.AppendLine();

        var solutionDir = _solution.SolutionDirectory;
        sb.AppendLine("## Solution");
        sb.AppendLine($"- Directory: {solutionDir.FullPath}");
        sb.AppendLine();

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

            object componentInstance = null;
            try
            {
                componentInstance = _reflection.ResolveComponent(foundType);
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
    }
}
