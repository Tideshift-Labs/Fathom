# Learnings: ReSharper Plugin Development for Rider

Hard-won lessons from building a plugin that runs code inspections on all solution files.

**Primary target:** UE5 C++ projects in Rider.
**Secondary target:** C# projects (ReSharper).

---

## VERY IMPORTANT: Dead-End Approaches

These approaches were fully explored and proven to NOT work for our use case. Do not revisit them.

### DEAD END #1: `RunLocalInspections` for C++ files

**Status:** DOES NOT WORK for C++. Park for future C# use only.

`CollectInspectionResults.RunLocalInspections()` is **ReSharper-only infrastructure**. It runs ReSharper daemon stages (C# analyzers) but does NOT run RiderCpp analyzers. When given C++ files:

- Callbacks fire: **0**
- Issues found: **0**
- No errors, no exceptions — **silent failure**

The C++ PSI is healthy (verified: `ShouldBuildPsi: True`, `ProvidesCodeModel: True`, documents present with content), but the daemon stage system simply doesn't include C++ analysis stages. RiderCpp has its own separate analysis pipeline.

**What it IS good for:** C# inspection on all solution files. Our POC proved this works — 88 real issues found on a C# project without SWEA. Keep `RunLocalInspections` parked for when we add C# support.

**Key working pattern (C# only):**
```csharp
var lifetimeDef = lifetime.CreateNested();
var progress = new ProgressIndicator(lifetimeDef.Lifetime);
var runner = new CollectInspectionResults(solution, lifetimeDef, progress);
var files = new Stack<IPsiSourceFile>(sourceFiles);
runner.RunLocalInspections(files, (file, issues) => {
    // process issues
}, null);
// ... use results ...
lifetimeDef.Terminate(); // ONLY after fully done — see Lifetime Management section below
```

### DEAD END #2: `IDaemon.ForceReHighlight` for non-open files

**Status:** REJECTED. Requires open editor tabs, conflicts with developer IDE usage.

`IDaemon.ForceReHighlight(IDocument)` and `DaemonStateChanged` work for C++ — they trigger RiderCpp analysis. However:

- They **only work for documents with active editor sessions** (files open in editor tabs)
- To inspect a file not currently open, you'd have to **programmatically open it in the editor**
- This **directly interferes with the developer's normal IDE usage** — they may be editing other files, and having the plugin open/close tabs or steal focus is unacceptable
- The whole point of this project is to run inspections **without requiring UI interaction**

**Do not revisit this approach.** The constraint is fundamental: the developer must be able to use the IDE normally while the LLM requests inspections in the background.

**However:** Rider's "Inspect Code" feature CAN analyze files without opening them in the editor (see "Inspect Code Log Analysis" section below). It uses the regular `DaemonImpl` with `kind = OTHER`, not `ForceReHighlight`. The mechanism it uses to achieve this is what we need to find.

### DEAD END #3: `SolutionAnalysisManager` as a component

**Status:** Not accessible from plugin code.

`SolutionAnalysisManager` is a public class in `JetBrains.ReSharper.Daemon.SolutionAnalysis`, but it is **not registered in the component container**. `solution.GetComponent<SolutionAnalysisManager>()` throws:

```
Could not find the component's SolutionAnalysisManager descriptor.
```

This means `CollectInspectionResults.CollectIssuesFromSolutionAnalysis()` (the static method that reads cached SWEA results) cannot be called from plugin code. Additionally, SWEA is OFF by default in UE5 projects, making this doubly irrelevant for the primary target.

---

## What Works (Proven)

| Capability | C# | C++ | Notes |
|---|---|---|---|
| **`InspectCodeDaemon.DoHighlighting`** (batch, any file) | **Untested (should work)** | **34 issues on 19 files** | **THE SOLUTION — works for C++ without open editor** |
| `RunLocalInspections` (batch, any file) | 88 issues found | 0 callbacks | ReSharper daemon stages only — C# fallback |
| `IDaemon.ForceReHighlight` (per-document) | Works | Works | Requires open editor tab — rejected |
| `IDaemon.DaemonStateChanged` | Works | Works | Same constraint as ForceReHighlight |
| Rider "Inspect Code" (`kind = OTHER`) | Unknown | Works | Does NOT require open editor — `InspectCodeDaemon` is the mechanism |
| C++ PSI model (file properties, documents) | N/A | Healthy | ShouldBuildPsi, ProvidesCodeModel all true |
| UE5 user file filtering (solution dir) | N/A | ~231 files | vs 247K+ total solution files |

---

## Inspect Code Log Analysis (Breakthrough)

### Key discovery: "Inspect Code" does NOT require open editor tabs

We triggered Rider's built-in "Inspect Code" on individual C++ files and captured backend logs. This revealed that:

1. **C++ daemon stages are standard `IDaemonStage` implementations** that go through the regular `DaemonImpl` / `DaemonProcessBase` infrastructure — not a separate pipeline.
2. **"Inspect Code" can analyze files without opening them in the editor.** It creates a `DaemonProcessBase` with `kind = OTHER` (vs `kind = VISIBLE_DOCUMENT` for open files).
3. **`RunLocalInspections` failed for C++ not because C++ stages are separate**, but because it uses its own internal `InspectionDaemon` that bypasses the regular `DaemonImpl`. The regular daemon already handles C++ just fine.

### C++ daemon stages observed

| Stage | Namespace / Context | Typical Time |
|---|---|---|
| `ParallelReferencesResolverProcess` | `JetBrains.ReSharper.Feature.Services.Cpp.Daemon` | 0-80ms |
| `CppDaemonStageProcess.FastStage` | same | 2-74ms |
| `CppDaemonStageProcess.SlowStage` | same | 1-20ms |
| `CppDaemonStageProcess.CppInlayHints` | same (only for open files) | 7ms |
| `CppGutterProcess` | `...Cpp.Navigation` (only for open files) | 0ms |
| `CppUnusedInternalLinkageEntitiesHighlightingStage` | `...Cpp.Daemon.UsageChecking.Daemon` (.cpp only) | 0ms |
| `UnrealBlueprintClassesDaemonStage` | UE5-specific | 0ms |
| `UnrealBlueprintPropertiesDaemonStage` | UE5-specific | 0ms |
| `UnrealBlueprintDelegateDaemonStage` | UE5-specific | 0-1ms |
| `UnrealBlueprintFunctionsDaemonStage` | UE5-specific | 0-168ms |
| `UnrealHeaderToolDaemonProcess` | UE5-specific, external (.h only) | ~850ms |

### Log: "Inspect Code" on an OPEN .h file (AsyncAction_PushConfirmScreen.h)

The file was already open in the editor. Two daemon runs occurred: first `VISIBLE_DOCUMENT` (regular daemon triggered by viewing), then `OTHER` (the "Inspect Code" pass).

```
18:15:57.067 |I| DocumentHost       | Viewing documentModel ... Path: .../AsyncAction_PushConfirmScreen.h
18:15:57.069 |I| TextControlHost    | Viewing textControl (...)
18:15:57.071 |V| DaemonImpl         | CreateDaemonForDocument ... AsyncAction_PushConfirmScreen.h by StateWithDescription
18:15:57.183 |V| DaemonProcessBase  | [Daemon] Daemon process 1016885 started on file ..., kind = VISIBLE_DOCUMENT
18:15:57.210 |V| ParallelReferencesResolverProcess | <Measured> "...ParallelReferencesResolverProcess.Execute" time=1ms
18:15:57.216 |V| CppDaemonStageProcess | <Measured> "...CppDaemonStageProcess.FastStage" time=3ms
18:15:57.224 |V| CppDaemonStageProcess | <Measured> "...CppDaemonStageProcess.SlowStage" time=2ms
18:15:57.225 |V| CppDaemonStageProcess | <Measured> "...CppDaemonStageProcess.CppInlayHints" time=7ms
18:15:57.225 |V| CppGutterProcess   | <Measured> "...CppGutterProcess.Execute" time=0ms
18:15:57.225 |V| UnrealBlueprintClassesDaemonStage  | time=0ms
18:15:57.226 |V| UnrealBlueprintPropertiesDaemonStage | time=0ms
18:15:57.227 |V| UnrealBlueprintDelegateDaemonStage | time=1ms
18:15:57.394 |V| UnrealBlueprintFunctionsDaemonStage | time=168ms
18:15:57.409 |V| UnrealHeaderToolDaemonProcess | External daemon process 1016885 initialized
18:15:57.409 |V| UnrealHeaderToolRunner | Start UnrealHeaderToolRunner for .../AsyncAction_PushConfirmScreen.h
18:15:57.447 |V| UnrealHeaderToolRunner | call dotnet "...UnrealBuildTool.dll" -Mode=UnrealHeaderTool ...
18:15:58.421 |V| UnrealHeaderToolRunner | UnrealHeaderTool: Result: Failed (OtherCompilationError)
18:15:58.421 |V| UnrealHeaderToolRunner | UnrealHeaderTool: Total execution time: 0.85 seconds
18:15:58.430 |V| DaemonProcessBase  | [Daemon] Daemon process 1016885 finished
18:15:58.436 |V| DaemonImpl         | DaemonStateChanged: ... UP_TO_DATE Analysis is complete.
```

UHT (Unreal Header Tool) runs as an external process via `dotnet UnrealBuildTool.dll -Mode=UnrealHeaderTool`. It reads `.uhtmanifest` files and generates class definitions. It only runs on `.h` files.

### Log: "Inspect Code" on a NOT-OPEN .cpp file (OptionsDataRegistry.cpp)

The file was NOT open in any editor tab. No `DocumentHost` or `TextControlHost` entries. The daemon ran directly with `kind = OTHER`.

```
18:33:44.656 |V| DaemonProcessBase  | [Daemon] Daemon process 25921886 started on file ..., kind = OTHER
18:33:44.746 |V| ParallelReferencesResolverProcess | <Measured> "...Execute" time=80ms
18:33:44.753 |V| CppDaemonStageProcess | <Measured> "...DeferredInit" time=0ms
18:33:44.828 |V| CppDaemonStageProcess | <Measured> "...FastStage" time=74ms
18:33:44.830 |V| CppDaemonStageProcess | <Measured> "...DeferredInit" time=1ms
18:33:44.851 |V| CppDaemonStageProcess | <Measured> "...SlowStage" time=20ms
18:33:44.851 |V| CppUnusedInternalLinkageEntitiesHighlightingStage | <Measured> "...Execute" time=0ms
18:33:44.851 |V| UnrealBlueprintClassesDaemonStage  | time=0ms
18:33:44.852 |V| UnrealBlueprintPropertiesDaemonStage | time=0ms
18:33:44.852 |V| UnrealBlueprintDelegateDaemonStage | time=0ms
18:33:44.852 |V| UnrealBlueprintFunctionsDaemonStage | time=0ms
18:33:44.852 |V| DaemonProcessBase  | [Daemon] Daemon process 25921886 finished
```

Key differences from the open-file run:
- No `DocumentHost` / `TextControlHost` — file was NOT opened in the editor
- No `CppInlayHints` or `CppGutterProcess` — these are editor-only stages
- No `UnrealHeaderToolDaemonProcess` — UHT only runs on `.h` files
- `CppUnusedInternalLinkageEntitiesHighlightingStage` appeared — only relevant for `.cpp` files
- Analysis times were slightly longer (80ms for references vs 1ms) — likely because the file wasn't already cached from being viewed

### Log: "Inspect Code" on a directory (DataObjects/, 8 .cpp files, none open)

None of the files were open. All 8 daemon processes started **in parallel** across different thread pool threads and completed within ~175ms total.

```
18:40:29.385 |V| DaemonProcessBase | [Daemon] process started ... ListDataObject_Base.cpp, kind = OTHER
18:40:29.385 |V| DaemonProcessBase | [Daemon] process started ... ListDataObject_Scalar.cpp, kind = OTHER
18:40:29.385 |V| DaemonProcessBase | [Daemon] process started ... ListDataObject_Value.cpp, kind = OTHER
18:40:29.386 |V| DaemonProcessBase | [Daemon] process started ... ListDataObject_Collection.cpp, kind = OTHER
18:40:29.386 |V| DaemonProcessBase | [Daemon] process started ... ListDataObject_StringResolution.cpp, kind = OTHER
18:40:29.387 |V| DaemonProcessBase | [Daemon] process started ... ListDataObject_StringBool.cpp, kind = OTHER
18:40:29.388 |V| DaemonProcessBase | [Daemon] process started ... ListDataObject_StringEnum.cpp, kind = OTHER
18:40:29.394 |V| DaemonProcessBase | [Daemon] process started ... ListDataObject_String.cpp, kind = OTHER
  (each file runs CppDaemonStageProcess + Unreal stages in parallel on JetPool threads)
18:40:29.422 |V| DaemonProcessBase | [Daemon] process finished ... ListDataObject_StringEnum.cpp
18:40:29.435 |V| DaemonProcessBase | [Daemon] process finished ... ListDataObject_Value.cpp
18:40:29.442 |V| DaemonProcessBase | [Daemon] process finished ... ListDataObject_Collection.cpp
18:40:29.503 |V| DaemonProcessBase | [Daemon] process finished ... ListDataObject_StringBool.cpp
18:40:29.512 |V| DaemonProcessBase | [Daemon] process finished ... ListDataObject_Base.cpp
18:40:29.531 |V| DaemonProcessBase | [Daemon] process finished ... ListDataObject_StringResolution.cpp
18:40:29.537 |V| DaemonProcessBase | [Daemon] process finished ... ListDataObject_Scalar.cpp
18:40:29.560 |V| DaemonProcessBase | [Daemon] process finished ... ListDataObject_String.cpp
```

Key observations:
- **Parallel execution**: All 8 files analyzed concurrently across `JetPool(L) #1-#8` and `JetPool(S) #2` threads
- **No editor involvement**: No `DocumentHost` or `TextControlHost` entries at all
- **Fast**: Entire batch of 8 files completed in ~175ms
- **Same stages per file**: Each file gets `ParallelReferencesResolverProcess` → `CppDaemonStageProcess` (Fast/Slow) → `CppUnusedInternalLinkageEntities` → Unreal Blueprint stages
- **Good sign for scalability**: The daemon infrastructure natively supports concurrent batch analysis

### What this means for our approach

The regular `DaemonImpl` already supports C++ analysis on non-open files via `kind = OTHER`. We do NOT need a separate C++ analysis pipeline. The mechanism is `InspectCodeDaemon` — see the breakthrough section below.

---

## BREAKTHROUGH: `InspectCodeDaemon` Works for C++ (No Open Editor Required)

### Discovery

After decompiling `RunInspection.cs`, `CollectInspectionResults.cs`, and `InspectCodeDaemon.cs`, we found the key difference between the working "Inspect Code" feature and the failing `RunLocalInspections`:

- **`CollectInspectionResults.InspectionDaemon`** (private inner class, used by `RunLocalInspections`): Creates a `DaemonProcessBaseImpl` and calls `DoHighlighting()` directly. Does NOT wrap in `FileImages.DisableCheckThread()`. Result: **0 callbacks for C++**.
- **`InspectCodeDaemon`** (PUBLIC class in `JetBrains.ReSharper.Daemon.SolutionAnalysis.InspectCode`): Also extends `DaemonProcessBaseImpl`, also uses `DaemonProcessKind.OTHER`, but **wraps in `FileImages.DisableCheckThread()` and `CompilationContextCookie.GetOrCreate()`**. Result: **34 real C++ issues found**.

The `FileImages.DisableCheckThread()` call is likely the critical enabler that allows C++ daemon stages to run from background threads.

### Working code pattern

```csharp
using JetBrains.ReSharper.Daemon.SolutionAnalysis.InspectCode;
using JetBrains.ReSharper.Daemon.SolutionAnalysis.Issues;
using JetBrains.ReSharper.Feature.Services.Daemon;
using FileImages = JetBrains.ReSharper.Daemon.SolutionAnalysis.FileImages.FileImages;

// Get required components
var issueClasses = solution.GetComponent<IssueClasses>();
var fileImages = FileImages.GetInstance(solution);

// Run inspection on any file (open or not)
var daemon = new InspectCodeDaemon(issueClasses, sourceFile, fileImages);
daemon.DoHighlighting(DaemonProcessKind.OTHER, issue =>
{
    var severity = issue.GetSeverity().ToString().ToUpperInvariant();
    var message = issue.Message;
    var line = 0;

    try
    {
        var doc = sourceFile.Document;
        if (doc != null && issue.Range.HasValue)
        {
            var offset = issue.Range.Value.StartOffset;
            if (offset >= 0 && offset <= doc.GetTextLength())
                line = (int)new DocumentOffset(doc, offset).ToDocumentCoords().Line + 1;
        }
    }
    catch { /* ignore offset errors */ }

    // Format: [SEVERITY] relative/path:line - message
});
```

### Experiment results

Ran `InspectCodeDaemonExperiment` (a `[SolutionComponent]`) on a UE5 C++ project (first 20 user files under solution directory):

```
User C++ files found: 231
Files processed: 19
Files with errors: 1
Total issues: 34

Sample issues:
[ERROR] Source/.../AsyncAction_PushConfirmScreen.cpp:0 - Cannot resolve symbol 'AsyncAction_PushConfirmScreen'
[WARNING] Source/.../DataObjects/ListDataObject_Base.cpp:25 - Clang-Tidy: Use auto when initializing with new ...
[WARNING] Source/.../OptionsDataRegistry.cpp:37 - Clang-Tidy: Narrowing conversion from 'int' to 'float'
[WARNING] Source/.../Udemy_CUI.cpp:123 - Clang-Tidy: 'override' is redundant since the function is already declared 'final'
```

Real Clang-Tidy warnings, symbol resolution errors, and style issues — all on files NOT open in the editor.

### Known issue: `.h` files throw `OperationCanceledException`

One `.h` file (`Udemy_CUI.h`) threw `OperationCanceledException` during the experiment. This is likely caused by the `UnrealHeaderToolDaemonProcess` stage, which runs `dotnet UnrealBuildTool.dll -Mode=UnrealHeaderTool` as an external process. This external call may have a timeout or cancellation mechanism that doesn't work correctly outside the normal daemon infrastructure.

From the log analysis, UHT only runs on `.h` files and can take ~850ms per file. The error needs investigation — options include:
1. Catching and ignoring `OperationCanceledException` for `.h` files (losing UHT diagnostics but keeping other stages' results)
2. Understanding why UHT cancels when invoked via `InspectCodeDaemon` vs the built-in "Inspect Code"
3. Providing a longer timeout or different lifetime configuration

### Why `InspectCodeDaemon` is the right API

| Property | `InspectCodeDaemon` | `RunLocalInspections` |
|---|---|---|
| C++ support | **Yes (34 issues)** | No (0 callbacks) |
| C# support | Should work (untested) | Yes (88 issues) |
| Open editor required | **No** | No |
| SWEA dependency | **None** | None |
| Public API | **Yes** (public class) | Yes |
| `FileImages.DisableCheckThread()` | **Yes** | No — likely the key difference |
| `CompilationContextCookie` | **Yes** | No |
| Threading | Background-safe | Background-safe |

`InspectCodeDaemon` is the same class used by `inspectcode.exe` (the command-line InspectCode tool). It's a public, supported API that works for both C++ and C# without requiring open editor tabs or SWEA.

---

## Next Steps

### Completed

1. ~~Investigate `.h` file `OperationCanceledException`~~ — Handled via retry loop (Step C), up to 3 retries with 1s delay
2. ~~Parallelize~~ — Done via `Parallel.ForEach` for both PSI sync and inspection phases
3. ~~Build HTTP API~~ — Done: `InspectionHttpServer.cs` on port 19876 with `/inspect`, `/files`, `/health`
4. ~~Handle PSI staleness~~ — Done: content comparison gate (Step A) polls until document matches disk

### Next

5. **Add `/blueprints` endpoint** — Expose `UE4AssetsCache.GetDerivedBlueprintClasses()` via HTTP. Requires adding `JetBrains.ReSharper.Feature.Services.Cpp` assembly reference.
6. **Test `InspectCodeDaemon` on C# files** — Confirm it works for C# as well, potentially replacing `RunLocalInspections` entirely.
7. **Handle new files** — Files must be in a project to get an `IPsiSourceFile`.

### Constraints for the solution

- **Must not require files to be open in editor tabs** (proven: `InspectCodeDaemon` does this)
- **Must not interfere with the developer's normal IDE usage**
- **Must work with SWEA disabled** (default for UE5)
- **Must handle PSI staleness** (file written to disk → PSI update delay)
- **Must handle new files** (file must be in a project to get an `IPsiSourceFile`)
- **Performance**: C++ per-file analysis is heavier than C#. A UE5 file with thousands of transitive includes can take seconds per file.

---

## API Discovery

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

### UE5 file filtering

When analyzing a UE5 project, filter to user source files only:
- Compare file paths against `solution.SolutionDirectory`
- Engine and third-party files are outside the solution directory
- A typical small UE5 project: ~231 user C++ files vs 247K+ total files

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

### RunLocalInspections does NOT depend on SWEA (but is C#-only)

`CollectInspectionResults.RunLocalInspections()` is entirely self-contained:

- Creates its own `InspectionDaemon` per file
- Runs all registered **ReSharper** daemon stages synchronously
- Collects `HighlightingInfo` results directly from those stages
- Never touches the SWEA cache, `SolutionAnalysisManager`, or `IssueSet`
- **Does NOT include RiderCpp C++ analyzers** — see Dead End #1 above

Our POC proved this works for C# — we got 88 real issues without SWEA involvement. But it produces 0 results for C++ files.

### What SWEA on/off affects

| Capability | SWEA on | SWEA off |
|-----------|---------|----------|
| `RunLocalInspections` (C# only) | Works | **Works** |
| `IDaemon.ForceReHighlight` (open files) | Works | **Works** |
| `IDaemon.DaemonStateChanged` (open files) | Works | **Works** |
| `CollectIssuesFromSolutionAnalysis` (cached) | Works | Dead — no cache |
| `CompletedOnceAfterStart` | Eventually true | Always false |
| Cross-file analysis (unused imports, etc.) | Available | **Lost** |

The only thing lost with SWEA off is global cross-file analysis (e.g., detecting unused public members referenced from other files). Per-file inspections — syntax errors, type mismatches, null dereferences, style issues — all work (for C#).

### UE5 C++ projects: SWEA is off by default

In Unreal Engine 5 C++ projects, SWEA is disabled by default due to solution size. This means:

- The SWEA cache path is dead. Don't rely on `CompletedOnceAfterStart` or `CollectIssuesFromSolutionAnalysis`.
- `RunLocalInspections` does **NOT** work for C++ files — see Dead End #1 above.
- **Performance is a concern**: C++ per-file analysis is heavier than C#. A UE5 file with thousands of transitive includes can take seconds to tens of seconds per file vs. milliseconds for C#.
- **PSI readiness matters**: C++ symbol resolution depends on include paths, compilation database, and UE5 generated headers being indexed. Analysis only produces meaningful results after Rider's initial project indexing completes (can take minutes for UE5).

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

**Important**: `ForceReHighlight` and `DaemonStateChanged` are part of the regular daemon, not SWEA. They work regardless of SWEA state, but **only for documents with active editor sessions (open files)**. See Dead End #2 above — this is why they don't solve our problem.

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

There is an unpredictable delay between "file saved" and "PSI reflects the new content." Running inspections too early returns results for the **old** file content. There is no simple public "flush and wait" API for this.

### New files must be in a project

`projectFile.ToSourceFile()` only works for files that belong to a `.csproj` / `.uproject`. A brand-new file not yet part of any project has no `IPsiSourceFile` and cannot be inspected. SDK-style C# projects auto-glob `**/*.cs`, but the project model still needs to reload. UE5 projects use `.uproject` + generated project files, so the situation is similar.

### Threading and concurrency

- `RunLocalInspections` uses daemon infrastructure — safe from background threads (our POC proves this)
- PSI access may require `ReadLockCookie.Create()` which blocks write operations while held
- Concurrent API requests could cause read lock contention
- A serial queue for inspection requests is the safest starting point

### MCP vs REST

Both would run inside the Rider process. MCP (Model Context Protocol) is purpose-built for LLM tool calling and avoids HTTP overhead. REST is more general. Either way, the API dies when the IDE closes and competes with the IDE for resources.

---

## UE5 Blueprint Derivation API (Decompiled from `JetBrains.ReSharper.Feature.Services.Cpp.dll`)

### Source: Closed-source, decompiled

The Unreal Engine support in ReSharper/Rider is **closed-source** — there is no `resharper-unreal` GitHub repo (unlike `resharper-unity` which is open-source). All types live in `JetBrains.ReSharper.Feature.Services.Cpp.dll`. The decompiled reference files are in `reference_files/ue_specific/`.

### The Golden API: `UE4AssetsCache.GetDerivedBlueprintClasses`

```csharp
// UE4AssetsCache is a [PsiComponent] — resolve via DI
var assetsCache = solution.GetComponent<UE4AssetsCache>();

// Direct children only:
ICollection<DerivedBlueprintClass> children = assetsCache.GetDerivedBlueprintClasses("AMyActor");

// All descendants (recursive BFS traversal):
IEnumerable<DerivedBlueprintClass> allDescendants = UE4SearchUtil.GetDerivedBlueprintClasses("AMyActor", assetsCache);
```

Each `DerivedBlueprintClass` is a readonly struct with:
- `Name` — the Blueprint class name (e.g., `"BP_MyActor_C"`)
- `ContainingFile` — `IPsiSourceFile` pointing to the `.uasset` file
- `Index` — index into the `.uasset` export map

### How the cache works

`UE4AssetsCache` extends `DeferredCacheWithCustomLockBase<UE4AssetData>`:
1. Scans all `.uasset` / `.umap` files (provided by `UE4AssetAdditionalFilesModuleFactory`)
2. Parses each with `UELinker` → `UE4AssetData.FromLinker(linker)`
3. `MergeData()` populates `myBaseTypesToInheritors` (`OneToListMap<string, DerivedBlueprintClass>`) from:
   - `BlueprintClassObject.SuperClassName` — class inheritance
   - `BlueprintClassObject.Interfaces[]` — interface implementations

The cache is **asynchronous/deferred**. It builds in the background after solution load.

### Cache readiness check

The daemon stages check readiness like this (from `UnrealBlueprintDaemonStageProcessBase`):
```csharp
DeferredCacheController component = solution.GetComponent<DeferredCacheController>();
bool isReady = component.CompletedOnce.Value && !component.HasDirtyFiles();
```

If the cache isn't ready, results will be incomplete (not wrong — just missing some Blueprints).

### Rich data model: `UE4AssetData`

Each parsed `.uasset` yields a `UE4AssetData` with:
- `BlueprintClasses[]` — `BlueprintClassObject` structs with `ObjectName`, `ClassName`, `SuperClassName`, `Interfaces[]`
- `K2VariableSets[]` — Blueprint graph nodes (variable get/set, function calls, delegate bindings)
- `OtherClasses[]` — non-Blueprint asset objects
- `WordHashes[]` — word index for fast text-based lookups

### Additional search capabilities via `UEAssetUsagesSearcher`

`UEAssetUsagesSearcher` is a `[SolutionComponent]` that provides higher-level queries:
- `GetFindUsagesResults(sourceFile, searchTarget, searchReadOccurrences)` — find usages of C++ classes/functions/properties in `.uasset` files
- `GetGoToInheritorsResults(searchTargets)` — find all Blueprint inheritors (classes and function overrides)
- `FindPossibleReadWriteResults(searchTargets, cache, searchReadOccurrences)` — find property read/write in Blueprints

Search targets are built via `UE4SearchUtil.BuildUESearchTargets(declaredElement)` which accounts for:
- Core Redirects (renamed classes/properties in `.ini` files)
- Class inheritance hierarchy (searches all derived C++ classes too)
- UE naming conventions (e.g., stripping prefixes)

### How Rider's Blueprint hints work (daemon stages)

The flow for the "N derived Blueprint classes" CodeVision hint:
1. `UnrealBlueprintClassesDaemonStage` creates a `UnrealBlueprintClassesDaemonStageProcess`
2. Process walks the C++ AST, finds symbols that look like `UCLASS`
3. For each, calls `UE4SearchUtil.BuildUESearchTargets(classEntity, solution, moduleName, withAllInheritors: true)`
4. Passes targets to `searcher.GetGoToInheritorsResults(targets)` via a lazy `Func<>`
5. Creates a `IHighlighting` via `CreateClassHighlighting()` that displays the count

Similarly, `UnrealBlueprintPropertiesDaemonStage` finds `UPROPERTY` members and looks up Blueprint read/write usages.

### Assembly reference requirement

All these types are in `JetBrains.ReSharper.Feature.Services.Cpp.dll`. To use them from our plugin, we need to add a reference to this assembly (NuGet package or direct DLL reference). The assembly is at:
```
C:\Program Files\JetBrains\JetBrains Rider 2024.3.5\lib\ReSharperHost\JetBrains.ReSharper.Feature.Services.Cpp.dll
```

### What we can expose via our API

A `/blueprints?class=AMyActor` endpoint could return:
- All derived Blueprint classes (recursive)
- The `.uasset` file path for each
- Whether the cache is complete or still building
- Optionally: interfaces implemented, function overrides, property usages

This data is NOT available through `InspectCodeDaemon` — it comes from a completely separate cache system that parses `.uasset` binary files independently of the C++ daemon stages.

---

## HTTP Inspection Server (`InspectionHttpServer.cs`)

### Architecture

A `[SolutionComponent]` running `System.Net.HttpListener` on `http://localhost:19876/`. Zero external dependencies.

### Endpoints

| Endpoint | Purpose |
|---|---|
| `/` | Help text |
| `/health` | Health check |
| `/files` | List all project source files |
| `/inspect?file=X&file=Y` | Run code inspections on specified files |

### Query parameters

- `&format=json` — JSON output (default is markdown)
- `&debug=true` — include per-file diagnostic info (PSI sync timing, retries, etc.)

### Reliability pipeline

The inspection endpoint implements a two-step reliability pipeline:

**Step A: PSI Content Sync** — Before inspecting, compares disk content with `sourceFile.Document.GetText()` (normalized line endings). Polls every 250ms, up to 15s timeout. This ensures the PSI reflects the latest file content.

**Step C: Retry on `OperationCanceledException`** — Up to 3 attempts with 1s delay. Handles the window where daemon stages get cancelled during re-indexing.

**Step B (DEAD END): `CommitAllDocuments`** — `IPsiServices.Files.CommitAllDocuments()` requires the main/UI thread. Cannot be called from `HttpListener`'s ThreadPool thread. Throws: "This action cannot be executed on the .NET TP Worker thread." Removed entirely; Steps A + C are sufficient.

### Parallelization

Both PSI sync (Step A) and inspection (Step C) phases use `Parallel.ForEach`. Results are reassembled in original request order. The daemon infrastructure natively supports concurrent `InspectCodeDaemon` instances.

### File not found handling

If the requestor provides multiple file paths, only fail early if ALL files are not found. Otherwise continue and report individual not-found files in the results.

---

## File Inventory

| File | Status | Purpose |
|---|---|---|
| `InspectionHttpServer.cs` | **Active** | **THE API** — HTTP server on port 19876 with `/inspect`, `/files`, `/health` endpoints |
| `InspectCodeDaemonExperiment.cs` | Disabled | Proved `InspectCodeDaemon` works for C++ (34 issues on 19 files). Replaced by `InspectionHttpServer` |
| `CppInspectionExperiment.cs` | Disabled | Superseded by InspectCodeDaemonExperiment. Proved C++ PSI is healthy but RunLocalInspections fails for C++ |
| `FullInspectionTestComponent.cs` | Disabled | POC proving RunLocalInspections works for C# (88 issues) |
| `RunFullInspectionsAction.cs` | Active | Action-based entry point (shortcut broken, SWEA path dead) |
| `RunInspectionsAction.cs` | Active | Legacy: markup-based inspection, open files only |
| `DumpActionsAction.cs` | Active | Utility: dumps registered actions |
| `ActionDumperComponent.cs` | Disabled | Utility: dumps actions at shell startup. Replaced by `InspectionHttpServer` |

### Reference files (decompiled)

| File | Source DLL | Purpose |
|---|---|---|
| `reference_files/RunInspection.cs` | `JetBrains.ReSharper.SolutionAnalysis.dll` | Entry point for "Inspect Code" feature |
| `reference_files/CollectInspectionResults.cs` | same | Contains private `InspectionDaemon` (the broken path) and `RunLocalInspections` |
| `reference_files/InspectCodeDaemon.cs` | same | **The working public class** — wraps in `FileImages.DisableCheckThread()` |
| `reference_files/DaemonProcessBase.cs` | `JetBrains.ReSharper.Daemon.Engine.dll` | Core `DoHighlighting()` that discovers and runs all daemon stages |

### UE-specific reference files (decompiled from `JetBrains.ReSharper.Feature.Services.Cpp.dll`)

| File | Namespace | Purpose |
|---|---|---|
| `ue_specific/UE4AssetsCache.cs` | `...UE4.UEAsset` | **Central cache** — `[PsiComponent]`, `GetDerivedBlueprintClasses()`, word index, deferred build |
| `ue_specific/UEAssetUsagesSearcher.cs` | `...UE4.UEAsset.Search` | **Search API** — `[SolutionComponent]`, find usages/inheritors/read-write in `.uasset` files |
| `ue_specific/UE4SearchUtil.cs` | `...UE4.UEAsset.Search` | **Search helpers** — recursive `GetDerivedBlueprintClasses()`, `BuildUESearchTargets()`, Core Redirects |
| `ue_specific/UE4AssetData.cs` | `...UE4.UEAsset` | **Parsed .uasset data** — `BlueprintClassObject` (with `SuperClassName`, `Interfaces[]`), `K2GraphNodeObject` |
| `ue_specific/DerivedBlueprintClass.cs` | `...UE4.UEAsset` | Result struct — `Name`, `ContainingFile`, `Index` |
| `ue_specific/UEBlueprintGeneratedClass.cs` | `...UE4.UEAsset.Reader` | `.uasset` binary reader — parses Blueprint class properties and interfaces |
| `ue_specific/UnrealBlueprintClassesDaemonStage.cs` | `...UE4.UEAsset.Daemon` | Daemon stage that produces "N derived Blueprint classes" hints |
| `ue_specific/UnrealBlueprintClassesDaemonStageProcess.cs` | `...UE4.UEAsset.Daemon` | Process that walks UCLASS symbols and queries `GetGoToInheritorsResults()` |
| `ue_specific/UnrealBlueprintPropertiesDaemonStage.cs` | `...UE4.UEAsset.Daemon` | Daemon stage for UPROPERTY Blueprint usage hints |
| `ue_specific/UnrealBlueprintPropertiesDameonStageProcess.cs` | `...UE4.UEAsset.Daemon` | Process that walks UPROPERTY symbols and queries read/write usages |
| `ue_specific/UnrealBlueprintDaemonStageProcessBase.cs` | `...UE4.UEAsset.Daemon` | Generic base class — cache readiness check, symbol walking, settings |
| `ue_specific/UnrealBlueprintHighlightingProvderBase.cs` | `...UE4.UEAsset.Daemon` | Abstract highlighting provider — class/property/function highlighting factories |
| `ue_specific/IUnrealAssetHighlighting.cs` | `...UE4.UEAsset.Daemon` | Highlighting interface with `OccurrencesCalculator` and `DeclaredElement` |
| `ue_specific/UnrealAssetOccurence.cs` | `...UE4.UEAsset.Search` | Occurrence wrapper — navigation (requires UnrealLink plugin), display text |
| `ue_specific/IUnrealOccurence.cs` | `...UE4.UEAsset.Search` | Interface for occurrence results |
| `ue_specific/IOccurrence.cs` | `...Feature.Services.Occurrences` | Base occurrence interface (from `Feature.Services.dll`, not Cpp-specific) |
