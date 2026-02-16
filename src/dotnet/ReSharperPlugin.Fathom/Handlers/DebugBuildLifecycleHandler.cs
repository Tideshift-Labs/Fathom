using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using JetBrains.ProjectModel;
using ReSharperPlugin.Fathom.Formatting;
using ReSharperPlugin.Fathom.Services;

namespace ReSharperPlugin.Fathom.Handlers;

/// <summary>
/// Debug endpoint to explore JetBrains SDK build lifecycle and UnrealLink types via reflection.
///
/// GET /debug/build-lifecycle
///   Scans all loaded JetBrains assemblies for build-related and UnrealLink types,
///   listing their public members (methods, properties, events) to help identify
///   the right hooks for detecting active builds.
///
/// GET /debug/build-lifecycle?resolve=true
///   Additionally attempts to resolve promising components via the ISolution container
///   and reports which ones are available at runtime.
///
/// This endpoint is for experimentation and will be removed once the right APIs are identified.
/// </summary>
public class DebugBuildLifecycleHandler : IRequestHandler
{
    private readonly ISolution _solution;
    private readonly ReflectionService _reflection;

    // Keywords for assembly filtering
    private static readonly string[] AssemblyKeywords =
    {
        "Rider", "Unreal", "Build", "Task", "Platform", "Host"
    };

    // Keywords for type name matching (build lifecycle, tasks, UnrealLink)
    private static readonly string[] TypeKeywords =
    {
        // Build lifecycle
        "Build", "Builder", "BuildSession", "SolutionBuilder",
        // Background task management
        "BackgroundTask", "TaskHost", "TaskRunner", "TaskManager",
        // UnrealLink / RiderLink
        "UnrealHost", "UnrealLink", "RiderLink", "UnrealPlugin",
        "UnrealPluginInstaller", "UnrealPluginDetector",
        // Process tracking
        "ProcessRunner", "ExternalProcess", "ToolRunner",
        // General build/compile signals
        "Compil", "MSBuild",
    };

    // High-priority type names we especially want to see details for
    private static readonly string[] HighPriorityTypeNames =
    {
        "UnrealHost",
        "UnrealPluginInstaller",
        "UnrealLinkHost",
        "RiderBackendHost",
        "ISolutionBuilder",
        "SolutionBuilder",
        "IBuildSession",
        "IBackgroundTaskHost",
        "BackgroundTaskHost",
        "IBuildStatusProvider",
        "IToolWindowHost",
        "BuildToolWindowManager",
        "UnrealBuildRunner",
        "ICppBuildSessionHost",
        "CppBuildSessionHost",
    };

    public DebugBuildLifecycleHandler(ISolution solution, ReflectionService reflection)
    {
        _solution = solution;
        _reflection = reflection;
    }

    public bool CanHandle(string path) => path == "/debug/build-lifecycle";

    public void Handle(HttpListenerContext ctx)
    {
        var resolve = (ctx.Request.QueryString["resolve"] ?? "").Equals("true", StringComparison.OrdinalIgnoreCase);

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Build Lifecycle & UnrealLink Type Discovery");
            sb.AppendLine();

            // Section 1: Relevant assemblies
            sb.AppendLine("## Loaded JetBrains Assemblies (filtered)");
            sb.AppendLine();
            var allAssemblies = AppDomain.CurrentDomain.GetAssemblies();
            var relevantAssemblies = allAssemblies
                .Where(a =>
                {
                    var name = a.GetName().Name ?? "";
                    return name.StartsWith("JetBrains", StringComparison.OrdinalIgnoreCase)
                           && AssemblyKeywords.Any(k =>
                               name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
                })
                .OrderBy(a => a.GetName().Name)
                .ToArray();

            foreach (var asm in relevantAssemblies)
                sb.AppendLine("- `" + asm.GetName().Name + "`");
            sb.AppendLine();
            sb.AppendLine($"({relevantAssemblies.Length} assemblies matched out of {allAssemblies.Length} total)");
            sb.AppendLine();

            // Section 2: Matching types with full member details
            sb.AppendLine("## Matching Types (detailed)");
            sb.AppendLine();

            var matchingTypes = allAssemblies
                .Where(a => (a.GetName().Name ?? "").StartsWith("JetBrains", StringComparison.OrdinalIgnoreCase))
                .SelectMany(a =>
                {
                    try { return a.GetExportedTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .Where(t => TypeKeywords.Any(k =>
                    t.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(t => t.FullName)
                .ToArray();

            sb.AppendLine($"Found **{matchingTypes.Length}** types matching keywords.");
            sb.AppendLine();

            foreach (var type in matchingTypes)
            {
                var isHighPriority = HighPriorityTypeNames.Any(hp =>
                    type.Name.Equals(hp, StringComparison.OrdinalIgnoreCase) ||
                    type.Name.Equals("I" + hp, StringComparison.OrdinalIgnoreCase));

                // Show condensed line for low-priority, detailed block for high-priority
                if (!isHighPriority)
                {
                    sb.AppendLine($"- `{type.FullName}` ({FormatTypeKind(type)}) [{type.Assembly.GetName().Name}]");
                    continue;
                }

                sb.AppendLine($"### {type.Name}");
                sb.AppendLine();
                sb.AppendLine($"- **Full name**: `{type.FullName}`");
                sb.AppendLine($"- **Assembly**: `{type.Assembly.GetName().Name}`");
                sb.AppendLine($"- **Kind**: {FormatTypeKind(type)}");

                if (type.BaseType != null && type.BaseType != typeof(object))
                    sb.AppendLine($"- **Base**: `{type.BaseType.FullName}`");

                var interfaces = type.GetInterfaces();
                if (interfaces.Length > 0)
                {
                    sb.AppendLine("- **Interfaces**:");
                    foreach (var iface in interfaces.Take(20))
                        sb.AppendLine($"  - `{iface.FullName}`");
                    if (interfaces.Length > 20)
                        sb.AppendLine($"  - ... and {interfaces.Length - 20} more");
                }

                AppendMembers(sb, type);
                sb.AppendLine();
            }

            // Section 3: Broader search for event-based hooks (types with events containing "Build")
            sb.AppendLine("## Types with Build/Task-related Events");
            sb.AppendLine();

            var typesWithBuildEvents = allAssemblies
                .Where(a => (a.GetName().Name ?? "").StartsWith("JetBrains", StringComparison.OrdinalIgnoreCase))
                .SelectMany(a =>
                {
                    try { return a.GetExportedTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .Select(t =>
                {
                    try
                    {
                        var events = t.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                            .Where(e => e.Name.IndexOf("Build", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        e.Name.IndexOf("Compil", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                        e.Name.IndexOf("Task", StringComparison.OrdinalIgnoreCase) >= 0)
                            .ToArray();
                        return events.Length > 0 ? (type: t, events) : default;
                    }
                    catch { return default; }
                })
                .Where(x => x.type != null)
                .OrderBy(x => x.type.FullName)
                .ToArray();

            if (typesWithBuildEvents.Length == 0)
            {
                sb.AppendLine("(none found)");
            }
            else
            {
                foreach (var (type, events) in typesWithBuildEvents)
                {
                    sb.AppendLine($"### {type.FullName}");
                    foreach (var evt in events)
                        sb.AppendLine($"  - event `{evt.EventHandlerType?.Name}` **{evt.Name}**");
                    sb.AppendLine();
                }
            }

            // Section 4: Types with "Advise" or observable properties (RD signal pattern)
            sb.AppendLine("## UnrealLink Types with Observable/Signal Properties");
            sb.AppendLine();

            var unrealTypes = allAssemblies
                .Where(a =>
                {
                    var name = a.GetName().Name ?? "";
                    return name.IndexOf("Unreal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           name.IndexOf("RiderLink", StringComparison.OrdinalIgnoreCase) >= 0;
                })
                .SelectMany(a =>
                {
                    try { return a.GetExportedTypes(); }
                    catch { return Array.Empty<Type>(); }
                })
                .OrderBy(t => t.FullName)
                .ToArray();

            sb.AppendLine($"Found **{unrealTypes.Length}** types in Unreal/RiderLink assemblies.");
            sb.AppendLine();

            foreach (var type in unrealTypes)
            {
                sb.AppendLine($"### {type.FullName}");
                sb.AppendLine($"- Kind: {FormatTypeKind(type)}, Assembly: `{type.Assembly.GetName().Name}`");
                AppendMembers(sb, type);
                sb.AppendLine();
            }

            // Section 5: Resolve promising components (if requested)
            if (resolve)
            {
                sb.AppendLine("## Component Resolution Attempts");
                sb.AppendLine();

                var candidateTypeNames = new[]
                {
                    // UnrealLink
                    "JetBrains.ReSharper.Plugins.Unreal.UnrealHost",
                    "JetBrains.ReSharper.Plugins.Unreal.UnrealPluginInstaller",
                    "JetBrains.ReSharper.Plugins.Unreal.UnrealLinkHost",
                    // Build
                    "JetBrains.ReSharper.Host.Features.Build.SolutionBuilderHost",
                    "JetBrains.ReSharper.Host.Features.BackgroundTasks.BackgroundTaskHost",
                    "JetBrains.Platform.RdFramework.Tasks.RdTaskHost",
                    // Cpp build
                    "JetBrains.ReSharper.Feature.Services.Cpp.Build.CppBuildSessionHost",
                    "JetBrains.ReSharper.Plugins.Unreal.Build.UnrealBuildRunner",
                };

                foreach (var typeName in candidateTypeNames)
                {
                    Type type = null;
                    foreach (var asm in allAssemblies)
                    {
                        try
                        {
                            type = asm.GetType(typeName);
                            if (type != null) break;
                        }
                        catch { }
                    }

                    if (type == null)
                    {
                        // Try fuzzy match by short name
                        var shortName = typeName.Split('.').Last();
                        type = allAssemblies
                            .SelectMany(a =>
                            {
                                try { return a.GetExportedTypes(); }
                                catch { return Array.Empty<Type>(); }
                            })
                            .FirstOrDefault(t => t.Name == shortName);
                    }

                    if (type == null)
                    {
                        sb.AppendLine($"- `{typeName}`: **TYPE NOT FOUND**");
                        continue;
                    }

                    sb.AppendLine($"- `{type.FullName}` (from `{type.Assembly.GetName().Name}`):");

                    try
                    {
                        var component = _reflection.ResolveComponent(type);
                        if (component != null)
                        {
                            sb.AppendLine($"  - **RESOLVED** as `{component.GetType().FullName}`");

                            // Dump runtime property values for observables
                            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                            {
                                try
                                {
                                    var val = prop.GetValue(component);
                                    var valStr = val?.ToString() ?? "null";
                                    if (valStr.Length > 200) valStr = valStr.Substring(0, 200) + "...";
                                    sb.AppendLine($"  - .{prop.Name} = `{valStr}`");
                                }
                                catch (Exception ex)
                                {
                                    sb.AppendLine($"  - .{prop.Name} = (threw {ex.GetType().Name})");
                                }
                            }
                        }
                        else
                        {
                            sb.AppendLine("  - ResolveComponent returned null");
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"  - ResolveComponent threw: {ex.GetType().Name}: {ex.Message}");
                    }

                    sb.AppendLine();
                }
            }

            HttpHelpers.Respond(ctx, 200, "text/markdown; charset=utf-8", sb.ToString());
        }
        catch (Exception ex)
        {
            HttpHelpers.Respond(ctx, 500, "text/plain; charset=utf-8",
                "DebugBuildLifecycleHandler error: " + ex);
        }
    }

    private static void AppendMembers(StringBuilder sb, Type type)
    {
        try
        {
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (props.Length > 0)
            {
                sb.AppendLine("- **Properties**:");
                foreach (var p in props.Take(30))
                    sb.AppendLine($"  - `{FormatTypeName(p.PropertyType)}` **{p.Name}** " +
                                  $"{{ {(p.CanRead ? "get; " : "")}{(p.CanWrite ? "set; " : "")}}}");
                if (props.Length > 30)
                    sb.AppendLine($"  - ... and {props.Length - 30} more");
            }

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName) // skip property getters/setters
                .ToArray();
            if (methods.Length > 0)
            {
                sb.AppendLine("- **Methods**:");
                foreach (var m in methods.Take(30))
                {
                    var pars = string.Join(", ", m.GetParameters().Select(p =>
                        FormatTypeName(p.ParameterType) + " " + p.Name));
                    sb.AppendLine($"  - `{FormatTypeName(m.ReturnType)}` **{m.Name}**({pars})");
                }
                if (methods.Length > 30)
                    sb.AppendLine($"  - ... and {methods.Length - 30} more");
            }

            var events = type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (events.Length > 0)
            {
                sb.AppendLine("- **Events**:");
                foreach (var e in events)
                    sb.AppendLine($"  - `{FormatTypeName(e.EventHandlerType)}` **{e.Name}**");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  - (reflection error: {ex.GetType().Name}: {ex.Message})");
        }
    }

    private static string FormatTypeKind(Type t)
    {
        if (t.IsInterface) return "interface";
        if (t.IsAbstract && t.IsSealed) return "static class";
        if (t.IsAbstract) return "abstract class";
        if (t.IsEnum) return "enum";
        if (t.IsValueType) return "struct";
        return "class";
    }

    private static string FormatTypeName(Type t)
    {
        if (t == null) return "void";
        if (t == typeof(void)) return "void";
        if (t == typeof(string)) return "string";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(int)) return "int";

        if (t.IsGenericType)
        {
            var name = t.Name;
            var tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);
            var args = string.Join(", ", t.GetGenericArguments().Select(FormatTypeName));
            return $"{name}<{args}>";
        }

        return t.Name;
    }
}
