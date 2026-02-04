# Learnings: ReSharper Plugin Development for Rider

Hard-won lessons from building a plugin that runs code inspections on all solution files.

## API Discovery

### SolutionAnalysisManager is not a component

`SolutionAnalysisManager` is described in the SDK docs as the "Internal Facade for SWEA." Despite being a public class in `JetBrains.ReSharper.Daemon.SolutionAnalysis`, it is **not** registered in the component container. Calling `solution.GetComponent<SolutionAnalysisManager>()` throws:

```
Could not find the component's SolutionAnalysisManager descriptor.
```

This means `CollectInspectionResults.CollectIssuesFromSolutionAnalysis()` (the static method that reads cached SWEA results) is not usable from plugin code, since it requires a `SolutionAnalysisManager` instance as a parameter.

**What works instead:** `CollectInspectionResults.RunLocalInspections()` — this runs the daemon per-file and does not need `SolutionAnalysisManager`.

### SolutionAnalysisConfiguration IS a component

`SolutionAnalysisConfiguration` (in `JetBrains.ReSharper.Daemon`, not `...Daemon.SolutionAnalysis`) is accessible via `solution.GetComponent<>()`. Its documented properties:

- `CompletedOnceAfterStart` — `IProperty<bool>`, true after SWEA finishes at least one full pass
- `Paused` — `IProperty<bool>`, true if SWEA is paused for any reason
- `PausedByUser` — whether the user explicitly paused SWEA

The plan originally referenced `config.Enabled.Value` and `config.Completed.Value` — these don't exist. Use `CompletedOnceAfterStart` and `Paused` instead.

### Namespace gotcha: JetBrains.ReSharper.Daemon vs ...Daemon.SolutionAnalysis

- `SolutionAnalysisConfiguration` → `JetBrains.ReSharper.Daemon` (no sub-namespace)
- `SolutionAnalysisManager` → `JetBrains.ReSharper.Daemon.SolutionAnalysis`
- `CollectInspectionResults` → `JetBrains.ReSharper.Daemon.SolutionAnalysis.RunInspection`
- `IssuePointer` → `JetBrains.ReSharper.Daemon.SolutionAnalysis`
- `Severity` → `JetBrains.ReSharper.Feature.Services.Daemon`

Missing the `using JetBrains.ReSharper.Daemon;` directive for `SolutionAnalysisConfiguration` gives a confusing `CS0246` since the class exists but is in a parent namespace you wouldn't guess from the other imports.

## Component Registration

### [SolutionComponent] requires an Instantiation parameter

In SDK version 253.x, the parameterless `[SolutionComponent]` constructor is obsolete (CS0619). You must specify an instantiation strategy:

```csharp
// Old (doesn't compile):
[SolutionComponent]

// New:
[SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
```

The `Instantiation` enum is in `JetBrains.Application.Parts`. Common values:
- `DemandAnyThreadSafe` — created on demand when first requested
- `ContainerAsyncAnyThreadSafe` — created during container compose, any thread

### ILogger API

The JetBrains logging API is `ILogger` from `JetBrains.Util`, not `Logger` from `JetBrains.Diagnostics`:

```csharp
// Wrong:
var logger = Logger.GetLogger<T>();

// Right:
private static readonly ILogger Log = JetBrains.Util.Logging.Logger.GetLogger<T>();
Log.Verbose("message");
Log.Error(ex, "message");
```

### IProperty<T>.Advise is an extension method

`IProperty<bool>.Advise(lifetime, callback)` is not a built-in method — it's an extension that requires a specific `using` directive (likely `JetBrains.DataFlow` or similar). If you just need to poll the value, use `.Value` directly instead of subscribing reactively.

## Lifetime Management (Critical)

### Do NOT terminate lifetimes immediately after RunLocalInspections

This was the most impactful bug. `CollectInspectionResults.RunLocalInspections()` fires callbacks synchronously, but the daemon stages internally check `lifetime.IsAlive` during execution. Wrapping the call in `try/finally { lifetimeDef.Terminate(); }` causes the lifetime to be terminated as part of normal control flow, which interferes with the daemon's internal lifetime checks.

```csharp
// BROKEN — returns 0 issues despite callbacks firing:
var lifetimeDef = lifetime.CreateNested();
try
{
    var runner = new CollectInspectionResults(solution, lifetimeDef, progress);
    runner.RunLocalInspections(files, callback, null);
}
finally
{
    lifetimeDef.Terminate();  // Daemon stages see dead lifetime → empty results
}

// WORKS — 88 issues found:
var lifetimeDef = lifetime.CreateNested();
var runner = new CollectInspectionResults(solution, lifetimeDef, progress);
runner.RunLocalInspections(files, callback, null);
// ... use results ...
lifetimeDef.Terminate();  // Only after fully done
```

The symptom is silent: no exceptions, no errors, just 0 issues. The callbacks fire (you can count them), but the issue lists are empty.

## Action Registration in Rider

### IdeaShortcuts may not work

Actions registered with `[Action(..., IdeaShortcuts = new[] { "Control+Alt+Shift+W" })]` compile and the action appears in the backend, but the shortcut may not reach the backend from Rider's IntelliJ frontend. The action may not appear in Find Action (`Ctrl+Shift+A`) either.

This was never fully debugged. For testing, a `[SolutionComponent]` that runs automatically on solution load is more reliable than relying on action shortcuts.

### Action attribute is obsolete

The two-parameter `[Action(string, string)]` constructor is obsolete in 253.x. Suppress with `#pragma warning disable CS0612`.

## File Collection

### Duplicate files across .csproj targets

When a solution has both `Foo.csproj` and `Foo.Rider.csproj` that include the same source files, iterating `solution.GetAllProjects()` → `project.GetAllProjectFiles()` → `projectFile.ToSourceFile()` yields duplicate `IPsiSourceFile` entries for the same physical file. This causes every issue to appear twice in the output. Deduplicate by `(filePath, line, message)` or by `sourceFile.GetLocation()`.

## Debugging Tips

### Write marker files early

In a `[SolutionComponent]` constructor, write a file to disk immediately before doing any real work. This proves the component loaded and narrows down whether failures are in loading vs. logic:

```csharp
File.WriteAllText(outputPath, $"Component loaded at {DateTime.Now}\n");
// ... real work ...
```

### Write exceptions to the output file

Wrap everything in `try/catch` and write `ex.Message + ex.StackTrace` to the output file. The backend log is hard to search and may not surface plugin exceptions prominently.

### Backend logs location

Sandbox IDE logs are at:
```
RiderActionExplorer/build/idea-sandbox/RD-{version}/log/backend.{date}.{pid}.log
```

Search for your component/class name. Verbose-level logging appears with `|V|` prefix.

### The "exit code 1" on startup is normal

The Rider startup log shows `ReSharperProcess has exited by request with exit code 1` early on. This is the `EarlyStartServerWire` process that exits and gets replaced by the real backend — it's not an error.
