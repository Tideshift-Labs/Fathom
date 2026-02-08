using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using JetBrains.Util;
using ReSharperPlugin.CoRider.Models;

namespace ReSharperPlugin.CoRider.Services;

public class BlueprintQueryService
{
    public BlueprintQueryResult Query(string className, object assetsCache,
        VirtualFileSystemPath solutionDir, bool debug)
    {
        var result = new BlueprintQueryResult { ClassName = className };
        var debugSb = debug ? new StringBuilder() : null;

        // Find a method named GetDerivedBlueprintClasses (or similar)
        MethodInfo targetMethod = null;
        var assetsCacheRuntimeType = assetsCache.GetType();

        var methodSearchNames = new[] { "GetDerivedBlueprintClasses", "GetDerivedBlueprints", "FindDerivedBlueprints" };
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var asmName = asm.GetName().Name ?? "";
            if (!asmName.Contains("Cpp") && !asmName.Contains("Unreal") && !asmName.Contains("UE"))
                continue;
            try
            {
                foreach (var t in asm.GetExportedTypes())
                {
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (!methodSearchNames.Contains(m.Name)) continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 2)
                        {
                            if (ps[0].ParameterType == typeof(string) &&
                                ps[1].ParameterType.IsAssignableFrom(assetsCacheRuntimeType))
                            {
                                targetMethod = m;
                                break;
                            }
                            if (ps[1].ParameterType == typeof(string) &&
                                ps[0].ParameterType.IsAssignableFrom(assetsCacheRuntimeType))
                            {
                                targetMethod = m;
                                break;
                            }
                        }
                    }
                    if (targetMethod != null) break;
                }
            }
            catch { }
            if (targetMethod != null) break;
        }

        if (targetMethod == null)
        {
            var diag = new StringBuilder();
            diag.AppendLine("Could not find GetDerivedBlueprintClasses method.");
            diag.AppendLine("AssetsCache runtime type: " + assetsCacheRuntimeType.FullName);
            diag.AppendLine();
            diag.AppendLine("== Static methods containing 'Blueprint' or 'Derived' in Cpp/Unreal assemblies ==");
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name ?? "";
                if (!asmName.Contains("Cpp") && !asmName.Contains("Unreal") && !asmName.Contains("UE"))
                    continue;
                try
                {
                    foreach (var t in asm.GetExportedTypes())
                    {
                        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        {
                            if (m.Name.Contains("Blueprint") || m.Name.Contains("Derived") ||
                                m.Name.Contains("blueprint") || m.Name.Contains("derived"))
                                diag.AppendLine("  " + t.FullName + "." + m.Name +
                                    "(" + string.Join(", ", m.GetParameters().Select(p =>
                                        p.ParameterType.Name + " " + p.Name)) + ")");
                        }
                    }
                }
                catch (Exception ex)
                {
                    diag.AppendLine("  [" + asmName + ": " + ex.GetType().Name + "]");
                }
            }

            diag.AppendLine();
            diag.AppendLine("== Methods on assetsCache containing 'Blueprint' or 'Derived' or 'Class' ==");
            foreach (var m in assetsCacheRuntimeType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name.Contains("Blueprint") || m.Name.Contains("Derived") ||
                    m.Name.Contains("Class") || m.Name.Contains("Asset"))
                    diag.AppendLine("  " + m.Name + "(" + string.Join(", ",
                        m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")" +
                        " -> " + m.ReturnType.Name);
            }

            throw new InvalidOperationException(diag.ToString());
        }

        if (debugSb != null)
        {
            debugSb.AppendLine("Matched method: " + targetMethod.DeclaringType?.FullName + "." + targetMethod.Name);
            debugSb.Append("  Signature: " + targetMethod.Name + "(");
            debugSb.Append(string.Join(", ", targetMethod.GetParameters().Select(p =>
                p.ParameterType.FullName + " " + p.Name)));
            debugSb.AppendLine(")");
            debugSb.AppendLine("  Return type: " + targetMethod.ReturnType.FullName);
            debugSb.AppendLine();

            var searchUtilType = targetMethod.DeclaringType;
            if (searchUtilType != null)
            {
                debugSb.AppendLine("== All methods on " + searchUtilType.Name + " ==");
                foreach (var m in searchUtilType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    debugSb.AppendLine("  " + m.ReturnType.Name + " " + m.Name + "(" +
                        string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
                }
                debugSb.AppendLine();
            }

            debugSb.AppendLine("== ALL public methods on " + assetsCacheRuntimeType.Name + " ==");
            foreach (var m in assetsCacheRuntimeType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.DeclaringType != typeof(object))
                .OrderBy(m => m.Name))
            {
                debugSb.AppendLine("  " + m.ReturnType.Name + " " + m.Name + "(" +
                    string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
            }
            debugSb.AppendLine();

            debugSb.AppendLine("== ALL public properties on " + assetsCacheRuntimeType.Name + " ==");
            foreach (var p in assetsCacheRuntimeType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                debugSb.AppendLine("  " + p.PropertyType.Name + " " + p.Name);
            }
            debugSb.AppendLine();
        }

        // BFS: recursively find all derived Blueprints
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(className);
        seen.Add(className);

        var isFirstParam0String = targetMethod.GetParameters()[0].ParameterType == typeof(string);

        while (queue.Count > 0)
        {
            var currentClass = queue.Dequeue();
            object[] invokeArgs = isFirstParam0String
                ? new object[] { currentClass, assetsCache }
                : new object[] { assetsCache, currentClass };

            var enumerable = targetMethod.Invoke(null, invokeArgs);
            if (enumerable == null) continue;

            foreach (var item in (IEnumerable)enumerable)
            {
                if (item == null) continue;
                var itemType = item.GetType();

                // Dump schema of the first item in debug mode
                if (result.Blueprints.Count == 0 && debugSb != null)
                {
                    debugSb.AppendLine("Result item type: " + itemType.FullName);
                    debugSb.AppendLine("  Fields:");
                    foreach (var f in itemType.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        object val = null;
                        try { val = f.GetValue(item); } catch { }
                        debugSb.AppendLine("    " + f.FieldType.Name + " " + f.Name +
                            " = " + (val?.ToString() ?? "null"));
                    }
                    debugSb.AppendLine();
                }

                var name = "";
                var filePath = "";

                // Read Name
                var nameField = itemType.GetField("Name", BindingFlags.Public | BindingFlags.Instance);
                if (nameField != null)
                    name = nameField.GetValue(item)?.ToString() ?? "";
                else
                {
                    var nameProp = itemType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    if (nameProp != null)
                        name = nameProp.GetValue(item)?.ToString() ?? "";
                    else
                        name = item.ToString() ?? "";
                }

                // Read ContainingFile
                var filePropertyNames = new[] { "ContainingFile", "File", "Path", "Location" };
                object containingFile = null;
                foreach (var fpName in filePropertyNames)
                {
                    var field = itemType.GetField(fpName, BindingFlags.Public | BindingFlags.Instance);
                    if (field != null) { containingFile = field.GetValue(item); break; }
                    var prop = itemType.GetProperty(fpName, BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null) { containingFile = prop.GetValue(item); break; }
                }

                if (containingFile != null)
                {
                    var getLocationMethod = containingFile.GetType().GetMethod("GetLocation",
                        BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (getLocationMethod != null)
                    {
                        var location = getLocationMethod.Invoke(containingFile, null);
                        if (location != null)
                        {
                            var makeRelMethod = location.GetType().GetMethod("MakeRelativeTo",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (makeRelMethod != null)
                            {
                                try
                                {
                                    var relPath = makeRelMethod.Invoke(location, new object[] { solutionDir });
                                    filePath = relPath?.ToString()?.Replace('\\', '/') ?? "";
                                }
                                catch { filePath = location.ToString()?.Replace('\\', '/') ?? ""; }
                            }
                            else
                                filePath = location.ToString()?.Replace('\\', '/') ?? "";
                        }
                    }
                    else
                        filePath = containingFile.ToString()?.Replace('\\', '/') ?? "";
                }

                if (!string.IsNullOrEmpty(name) && seen.Add(name))
                {
                    var packagePath = DerivePackagePath(filePath);
                    result.Blueprints.Add(new BlueprintClassInfo
                    {
                        Name = name,
                        FilePath = filePath,
                        PackagePath = packagePath
                    });

                    queue.Enqueue(name);
                    if (name.EndsWith("_C"))
                    {
                        var withoutC = name.Substring(0, name.Length - 2);
                        if (seen.Add(withoutC))
                            queue.Enqueue(withoutC);
                    }
                    else if (seen.Add(name + "_C"))
                    {
                        queue.Enqueue(name + "_C");
                    }
                }
            }
        }

        result.TotalCount = result.Blueprints.Count;

        if (debugSb != null)
        {
            debugSb.AppendLine("Total results (recursive BFS): " + result.Blueprints.Count);
            debugSb.AppendLine("Classes queried: " + string.Join(", ", seen));
            result.DebugInfo = debugSb.ToString();
        }

        return result;
    }

    /// <summary>
    /// Converts a relative file path like "Content/UI/Widgets/WBP_Foo.uasset"
    /// to a UE package path like "/Game/UI/Widgets/WBP_Foo".
    /// </summary>
    private static string DerivePackagePath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;

        var normalized = filePath.Replace('\\', '/');

        // Strip "Content/" prefix and replace with "/Game/"
        if (normalized.StartsWith("Content/"))
            normalized = "/Game/" + normalized.Substring("Content/".Length);
        else
            return null;

        // Strip .uasset extension
        if (normalized.EndsWith(".uasset"))
            normalized = normalized.Substring(0, normalized.Length - ".uasset".Length);

        return normalized;
    }
}
