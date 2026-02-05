using System;
using System.Linq;
using System.Reflection;
using System.Text;
using JetBrains.ProjectModel;

namespace ReSharperPlugin.RiderActionExplorer.Services;

public class ReflectionService
{
    private readonly ISolution _solution;

    public ReflectionService(ISolution solution)
    {
        _solution = solution;
    }

    public object ResolveComponent(Type componentType)
    {
        // Strategy 1: Look for instance GetComponent<T>() on solution interfaces
        MethodInfo getComponentMethod = null;
        foreach (var iface in _solution.GetType().GetInterfaces())
        {
            getComponentMethod = iface.GetMethod("GetComponent",
                BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (getComponentMethod != null && getComponentMethod.IsGenericMethodDefinition)
                break;
            getComponentMethod = null;
        }

        // Strategy 2: Search concrete type hierarchy
        if (getComponentMethod == null)
        {
            for (var type = _solution.GetType(); type != null; type = type.BaseType)
            {
                getComponentMethod = type.GetMethod("GetComponent",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (getComponentMethod != null && getComponentMethod.IsGenericMethodDefinition)
                    break;
                getComponentMethod = null;
            }
        }

        if (getComponentMethod != null)
        {
            var gm = getComponentMethod.MakeGenericMethod(componentType);
            return gm.Invoke(_solution, null);
        }

        // Strategy 3: Find the static extension method in loaded assemblies
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var asmName = asm.GetName().Name ?? "";
            if (!asmName.Contains("JetBrains")) continue;
            try
            {
                foreach (var t in asm.GetExportedTypes())
                {
                    if (!t.IsAbstract || !t.IsSealed) continue; // static classes
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "GetComponent" || !m.IsGenericMethodDefinition) continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(_solution.GetType()))
                        {
                            var gm = m.MakeGenericMethod(componentType);
                            return gm.Invoke(null, new object[] { _solution });
                        }
                    }
                }
            }
            catch { }
        }

        return null;
    }

    public object ResolveUe4AssetsCache()
    {
        var candidateNames = new[]
        {
            "JetBrains.ReSharper.Feature.Services.Cpp.Caches.UE4AssetsCache",
            "JetBrains.ReSharper.Feature.Services.Cpp.UE4.Caches.UE4AssetsCache",
            "JetBrains.ReSharper.Features.Cpp.Caches.UE4AssetsCache",
            "JetBrains.ReSharper.Plugins.Unreal.Caches.UE4AssetsCache",
        };

        Type assetsCacheType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var candidate in candidateNames)
            {
                try
                {
                    assetsCacheType = asm.GetType(candidate);
                    if (assetsCacheType != null) break;
                }
                catch { }
            }
            if (assetsCacheType != null) break;
        }

        // If still not found, search by short name across all types in Cpp-related assemblies
        if (assetsCacheType == null)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name ?? "";
                if (!asmName.Contains("Cpp") && !asmName.Contains("Unreal") && !asmName.Contains("UE"))
                    continue;
                try
                {
                    foreach (var t in asm.GetExportedTypes())
                    {
                        if (t.Name == "UE4AssetsCache" || t.Name == "UnrealAssetsCache" ||
                            t.Name == "UEAssetsCache" || t.Name == "BlueprintAssetsCache")
                        {
                            assetsCacheType = t;
                            break;
                        }
                    }
                }
                catch { }
                if (assetsCacheType != null) break;
            }
        }

        if (assetsCacheType == null)
        {
            var diag = new StringBuilder();
            diag.AppendLine("Type not found. Diagnostics:");
            diag.AppendLine();
            diag.AppendLine("== Assemblies containing 'Cpp', 'Unreal', or 'UE' ==");
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name ?? "";
                if (asmName.Contains("Cpp") || asmName.Contains("Unreal") || asmName.Contains("UE"))
                    diag.AppendLine("  " + asm.GetName().FullName);
            }
            diag.AppendLine();
            diag.AppendLine("== Types containing 'Asset' or 'Blueprint' in those assemblies ==");
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var asmName = asm.GetName().Name ?? "";
                if (!asmName.Contains("Cpp") && !asmName.Contains("Unreal") && !asmName.Contains("UE"))
                    continue;
                try
                {
                    foreach (var t in asm.GetExportedTypes())
                    {
                        if (t.Name.Contains("Asset") || t.Name.Contains("Blueprint") || t.Name.Contains("UE4"))
                            diag.AppendLine("  " + t.FullName);
                    }
                }
                catch (Exception ex)
                {
                    diag.AppendLine("  [" + asmName + ": GetExportedTypes() threw " + ex.GetType().Name + "]");
                }
            }
            throw new InvalidOperationException(diag.ToString());
        }

        var componentResult = ResolveComponent(assetsCacheType);
        if (componentResult == null)
        {
            var diag = new StringBuilder();
            diag.AppendLine("ResolveComponent returned null for " + assetsCacheType.FullName);
            diag.AppendLine();
            diag.AppendLine("Solution type: " + _solution.GetType().FullName);
            diag.AppendLine();
            diag.AppendLine("== Interfaces on solution ==");
            foreach (var iface in _solution.GetType().GetInterfaces())
                diag.AppendLine("  " + iface.FullName);
            diag.AppendLine();
            diag.AppendLine("== Methods containing 'Component' or 'Resolve' on solution type ==");
            foreach (var m in _solution.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name.Contains("Component") || m.Name.Contains("Resolve") || m.Name.Contains("GetInstance"))
                    diag.AppendLine("  " + m.DeclaringType?.Name + "." + m.Name +
                        "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")" +
                        (m.IsGenericMethodDefinition ? " [generic]" : ""));
            }
            throw new InvalidOperationException(diag.ToString());
        }

        return componentResult;
    }

    public bool CheckCacheReadiness()
    {
        Type controllerType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                controllerType = asm.GetType("JetBrains.ReSharper.Feature.Services.DeferredCaches.DeferredCacheController");
                if (controllerType != null) break;
            }
            catch { }
        }

        if (controllerType == null) return false;

        var controller = ResolveComponent(controllerType);
        if (controller == null) return false;

        var completedOnceProp = controllerType.GetProperty("CompletedOnce",
            BindingFlags.Public | BindingFlags.Instance);
        if (completedOnceProp == null) return false;

        var completedOnceObj = completedOnceProp.GetValue(controller);
        if (completedOnceObj == null) return false;

        var valueProp = completedOnceObj.GetType().GetProperty("Value",
            BindingFlags.Public | BindingFlags.Instance);
        if (valueProp == null) return false;

        var value = valueProp.GetValue(completedOnceObj);
        if (value is bool b && !b) return false;

        var hasDirtyMethod = controllerType.GetMethod("HasDirtyFiles",
            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (hasDirtyMethod != null)
        {
            var hasDirty = hasDirtyMethod.Invoke(controller, null);
            if (hasDirty is bool dirty && dirty) return false;
        }

        return true;
    }
}
