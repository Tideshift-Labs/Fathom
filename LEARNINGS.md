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

## SWEA vs Local Daemon: Independence

### RunLocalInspections does NOT depend on SWEA

This is a critical architectural point. `CollectInspectionResults.RunLocalInspections()` is entirely self-contained:

- Creates its own `InspectionDaemon` per file
- Runs all registered daemon stages synchronously
- Collects `HighlightingInfo` results directly from those stages
- Never touches the SWEA cache, `SolutionAnalysisManager`, or `IssueSet`

Our POC proved this — we got 88 real issues without SWEA involvement. The `SolutionAnalysisManager` was inaccessible and `CompletedOnceAfterStart` was never checked.

### What SWEA on/off affects

| Capability | SWEA on | SWEA off |
|-----------|---------|----------|
| `RunLocalInspections` (batch/any file) | Works | **Works** |
| `IDaemon.ForceReHighlight` (open files) | Works | **Works** |
| `IDaemon.DaemonStateChanged` (open files) | Works | **Works** |
| `CollectIssuesFromSolutionAnalysis` (cached) | Works | Dead — no cache |
| `CompletedOnceAfterStart` | Eventually true | Always false |
| Cross-file analysis (unused imports, etc.) | Available | **Lost** |

The only thing lost with SWEA off is global cross-file analysis (e.g., detecting unused public members referenced from other files). Per-file inspections — syntax errors, type mismatches, null dereferences, style issues — all work.

### UE5 C++ projects: SWEA is off by default

In Unreal Engine 5 C++ projects, SWEA is disabled by default due to solution size. This means:

- The SWEA cache path is dead. Don't rely on `CompletedOnceAfterStart` or `CollectIssuesFromSolutionAnalysis`.
- `RunLocalInspections` still works — the daemon stage system is language-agnostic. C++ daemon stages (RiderCpp analyzers) run the same way as C# ones.
- **Performance is a concern**: C++ per-file analysis is heavier than C#. A UE5 file with thousands of transitive includes can take seconds to tens of seconds per file vs. milliseconds for C#.
- **PSI readiness matters**: C++ symbol resolution depends on include paths, compilation database, and UE5 generated headers being indexed. `RunLocalInspections` only produces meaningful results after Rider's initial project indexing completes (can take minutes for UE5).

## SWEA Lifecycle APIs (for future use)

### IDaemon — per-document lifecycle (registered component)

The `IDaemon` interface (`JetBrains.ReSharper.Feature.Services.Daemon`) provides per-document analysis control. It is a registered component, accessible via `solution.GetComponent<IDaemon>()`.

| Member | What it does |
|--------|-------------|
| `DaemonStateChanged` | Signal fired when daemon state changes for any document |
| `StateWithDescription(IDocument)` | Get current daemon state for a specific document (`DaemonStateWithDescription`) |
| `ForceReHighlight(IDocument)` | Force async re-analysis of a specific document; returns false if daemon wasn't started |
| `Invalidate(IDocument)` | Mark a document's daemon results as stale (requires reader lock) |
| `Invalidate()` | Mark ALL daemon results as stale |
| `Invalidate(string, IDocument)` | Invalidate with a reason string |

**Important**: `ForceReHighlight` and `DaemonStateChanged` are part of the regular daemon, not SWEA. They work regardless of SWEA state, but only for documents with active editor sessions (open files).

### SolutionAnalysisConfiguration — global SWEA state

Already documented above. Key addition: the `Pause(Lifetime, string)` method lets you programmatically pause SWEA for a scoped lifetime, useful if you want to prevent SWEA from interfering during a batch operation.

### What's NOT available in the public API

- **No global progress counter** — can't ask "how many files has SWEA analyzed out of N total"
- **No IssueSet / HighlightingResultsMap** — locked behind internal `SolutionAnalysisManager`
- **No per-file "last analyzed timestamp"** — you know the state enum, not when it was reached
- **No ISwaProcessor** — internal only, not in SDK packages
- **No file-level "is stale" query** — `IDaemonProcess.IsRangeInvalidated()` exists but only during an active daemon process

## Architecture: On-Demand Inspection API for LLMs

### The PSI staleness problem

When a file is modified on disk, the ReSharper PSI doesn't reflect the change instantly:

```
File written to disk
  → FileSystemWatcher detects change
    → Document model updates
      → PSI reparses the file
        → Daemon stages can now analyze it
```

There is an unpredictable delay between "file saved" and "PSI reflects the new content." Running `RunLocalInspections` too early returns results for the **old** file content. There is no simple public "flush and wait" API for this.

### New files must be in a project

`projectFile.ToSourceFile()` only works for files that belong to a `.csproj` / `.uproject`. A brand-new file not yet part of any project has no `IPsiSourceFile` and cannot be inspected. SDK-style C# projects auto-glob `**/*.cs`, but the project model still needs to reload. UE5 projects use `.uproject` + generated project files, so the situation is similar.

### Recommended per-file flow for LLM integration

```
1. LLM writes/modifies a file on disk
2. Plugin detects the change (or is told via API request)
3. Plugin calls IDaemon.ForceReHighlight(document) for open files
   — OR for non-open files, runs RunLocalInspections directly
4. For open files: listen to DaemonStateChanged, wait for completion
5. Run RunLocalInspections on that specific file (single-item stack)
6. Return issues to LLM
```

### Threading and concurrency

- `RunLocalInspections` uses daemon infrastructure — safe from background threads (our POC proves this)
- PSI access may require `ReadLockCookie.Create()` which blocks write operations while held
- Concurrent API requests could cause read lock contention
- A serial queue for inspection requests is the safest starting point

### MCP vs REST

Both would run inside the Rider process. MCP (Model Context Protocol) is purpose-built for LLM tool calling and avoids HTTP overhead. REST is more general. Either way, the API dies when the IDE closes and competes with the IDE for resources.
