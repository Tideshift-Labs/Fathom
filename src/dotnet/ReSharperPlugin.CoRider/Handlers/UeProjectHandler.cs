using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using JetBrains.ProjectModel;
using ReSharperPlugin.CoRider.Formatting;
using ReSharperPlugin.CoRider.Services;

namespace ReSharperPlugin.CoRider.Handlers;

public class UeProjectHandler : IRequestHandler
{
    private readonly ISolution _solution;
    private readonly UeProjectService _ueProject;
    private readonly ReflectionService _reflection;

    public UeProjectHandler(ISolution solution, UeProjectService ueProject, ReflectionService reflection)
    {
        _solution = solution;
        _ueProject = ueProject;
        _reflection = reflection;
    }

    public bool CanHandle(string path) => path == "/ue-project";

    public void Handle(HttpListenerContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# UE Project Diagnostics");
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

        sb.AppendLine("## UE Project Info (via GetUeProjectInfo helper)");
        var ueInfo = _ueProject.GetUeProjectInfo();
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
                var detector = _reflection.ResolveComponent(detectorType);
                if (detector != null)
                {
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

                    var getEngineProject = detector.GetType().GetMethod("GetUE4EngineProject",
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (getEngineProject != null)
                    {
                        var engineProject = getEngineProject.Invoke(detector, null);
                        sb.AppendLine($"- GetUE4EngineProject() = {engineProject}");

                        if (engineProject != null)
                        {
                            var engineType = engineProject.GetType();
                            sb.AppendLine($"  - Type: {engineType.FullName}");

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

                            var getPropMethod = engineType.GetMethod("GetProperty",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (getPropMethod != null)
                            {
                                sb.AppendLine($"  - Has GetProperty method");
                            }

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

        HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
    }
}
