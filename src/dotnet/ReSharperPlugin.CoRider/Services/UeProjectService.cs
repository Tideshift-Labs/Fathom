using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using JetBrains.ProjectModel;
using ReSharperPlugin.CoRider.Models;

namespace ReSharperPlugin.CoRider.Services;

public class UeProjectService
{
    private readonly ISolution _solution;
    private readonly ReflectionService _reflection;
    private readonly ServerConfiguration _config;

    public UeProjectService(ISolution solution, ReflectionService reflection, ServerConfiguration config)
    {
        _solution = solution;
        _reflection = reflection;
        _config = config;
    }

    public bool IsUnrealProject()
    {
        try
        {
            var solutionDir = _solution.SolutionDirectory.FullPath;
            var uprojectFiles = Directory.GetFiles(solutionDir, "*.uproject");
            return uprojectFiles.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public UeProjectInfo GetUeProjectInfo()
    {
        var result = new UeProjectInfo();

        try
        {
            // Find ICppUE4SolutionDetector type
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

            if (detectorType == null)
            {
                result.Error = "ICppUE4SolutionDetector type not found";
                return result;
            }

            var detector = _reflection.ResolveComponent(detectorType);
            if (detector == null)
            {
                result.Error = "ICppUE4SolutionDetector component not resolvable";
                return result;
            }

            // Get UProjectPath FIRST - this is the most reliable indicator
            var getUProjectPath = detector.GetType().GetMethod("GetUProjectPath",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (getUProjectPath != null)
            {
                var uprojectPath = getUProjectPath.Invoke(detector, null);
                result.UProjectPath = uprojectPath?.ToString();
            }

            // Determine IsUnrealProject based on whether we have a valid .uproject path
            if (!string.IsNullOrEmpty(result.UProjectPath) && File.Exists(result.UProjectPath))
            {
                result.IsUnrealProject = true;
            }
            else
            {
                // Fallback: check IsUnrealSolution property
                var isUnrealProp = detector.GetType().GetProperty("IsUnrealSolution",
                    BindingFlags.Public | BindingFlags.Instance);
                if (isUnrealProp != null)
                {
                    var isUnrealObj = isUnrealProp.GetValue(detector);
                    var valueProp = isUnrealObj?.GetType().GetProperty("Value",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (valueProp != null)
                    {
                        var val = valueProp.GetValue(isUnrealObj);
                        result.IsUnrealProject = val is true;
                    }
                }
            }

            if (!result.IsUnrealProject)
            {
                result.Error = "Not an Unreal project (no valid .uproject file found)";
                return result;
            }

            // Get UnrealContext property and parse engine path
            var unrealContextProp = detector.GetType().GetProperty("UnrealContext",
                BindingFlags.Public | BindingFlags.Instance);
            if (unrealContextProp != null)
            {
                var contextObj = unrealContextProp.GetValue(detector);
                var valueProp = contextObj?.GetType().GetProperty("Value",
                    BindingFlags.Public | BindingFlags.Instance);
                if (valueProp != null)
                {
                    var contextValue = valueProp.GetValue(contextObj);
                    if (contextValue != null)
                    {
                        var pathProp = contextValue.GetType().GetProperty("Path",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (pathProp != null)
                        {
                            var pathVal = pathProp.GetValue(contextValue);
                            result.EnginePath = pathVal?.ToString();
                        }

                        var versionProp = contextValue.GetType().GetProperty("Version",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (versionProp != null)
                        {
                            var versionVal = versionProp.GetValue(contextValue);
                            result.EngineVersion = versionVal?.ToString();
                        }

                        // If Path property didn't work, try parsing ToString()
                        if (string.IsNullOrEmpty(result.EnginePath))
                        {
                            var contextStr = contextValue.ToString();
                            var pathMatch = Regex.Match(
                                contextStr, @"Path:\s*([^.]+(?:\.[^.]+)*?)\.\s*Version:");
                            if (pathMatch.Success)
                            {
                                result.EnginePath = pathMatch.Groups[1].Value.Trim();
                            }

                            var versionMatch = Regex.Match(
                                contextStr, @"Version:\s*([0-9.]+)");
                            if (versionMatch.Success)
                            {
                                result.EngineVersion = versionMatch.Groups[1].Value.Trim();
                            }
                        }
                    }
                }
            }

            // Build commandlet path
            if (!string.IsNullOrEmpty(result.EnginePath))
            {
                result.CommandletExePath = Path.Combine(
                    result.EnginePath, "Binaries",
                    _config.PlatformBinaryFolder,
                    _config.CommandletExecutable);

                result.UnrealBuildToolDllPath = Path.Combine(
                    result.EnginePath, _config.UnrealBuildToolDllRelativePath);

                result.EditorTargetName =
                    Path.GetFileNameWithoutExtension(result.UProjectPath) + "Editor";
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            if (ex.InnerException != null)
                result.Error += " | Inner: " + ex.InnerException.Message;
        }

        return result;
    }
}
