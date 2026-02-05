# RiderActionExplorer

A Rider/ReSharper plugin for extracting code inspection results from **all** solution files and writing them to a file on disk.

## Goal

Run ReSharper code inspections on every file in a solution — not just files currently open in the editor — and dump the results to a text file. This enables bulk inspection output without relying on external tooling.

## Background

ReSharper's **Solution-Wide Error Analysis (SWEA)** continuously analyzes all files in a solution in the background. The results are cached in memory. This plugin taps into those cached results (or falls back to per-file daemon analysis) using the same internal APIs that ReSharper's built-in "Inspect Code" feature uses.

## What's in the plugin

| File | Purpose |
|------|---------|
| `InspectionHttpServer.cs` | `[SolutionComponent]` HTTP server on `localhost:19876` — exposes `/inspect`, `/files`, `/health`, `/blueprints` endpoints for LLM and tool integration |
| `RunFullInspectionsAction.cs` | Action (`Ctrl+Alt+Shift+W`) that collects inspections from all solution files via SWEA or local daemon fallback, writes to `resharper-full-inspections-dump.txt` on Desktop |
| `FullInspectionTestComponent.cs` | `[SolutionComponent]` test harness that runs automatically on solution open — polls for SWEA completion then dumps results, no UI interaction needed |
| `RunInspectionsAction.cs` | Earlier approach (`Ctrl+Alt+Shift+I`) that reads `IDocumentMarkupManager` highlighters — only sees files with active document markup (effectively: open files) |
| `DumpActionsAction.cs` | Utility action (`Ctrl+Alt+Shift+D`) that dumps all registered IDE actions to a file |
| `ActionDumperComponent.cs` | `[ShellComponent]` that dumps registered actions at shell startup |

## Key APIs used

- **`SolutionAnalysisManager`** (`JetBrains.ReSharper.Daemon.SolutionAnalysis`) — facade for SWEA, provides `IssueSet` with cached analysis results
- **`SolutionAnalysisConfiguration`** (`JetBrains.ReSharper.Daemon`) — exposes `Paused` and `CompletedOnceAfterStart` properties to check SWEA status
- **`CollectInspectionResults.CollectIssuesFromSolutionAnalysis()`** (`JetBrains.ReSharper.Daemon.SolutionAnalysis.RunInspection`) — public static method that reads cached SWEA results per file
- **`IssuePointer`** — result type with `Message`, `GetSeverity()`, `Range`, `NavigationOffset`, `IsValid`

## Approaches we're deliberately avoiding

- **`IDocumentMarkupManager` / highlighter enumeration** — only works for files that have an active document model (opened in editor tabs). This is what `RunInspectionsAction.cs` does, and it misses the vast majority of files in a solution.

- **External `inspectcode.exe`** — JetBrains ships a standalone command-line inspection tool, but we want in-process access to the live analysis state with zero extra configuration or process spawning.

- **Custom daemon stages** — writing our own `IDaemonStage` would let us hook into the analysis pipeline, but it's unnecessary complexity when SWEA already caches exactly the results we need.

## Output format

```
=== Full Code Inspection Results (All Solution Files) ===
Scanned at: 2026-02-03 14:30:00
Solution: C:\path\to\solution
Mode: SWEA (cached solution-wide analysis)
Total files in scope: 247

Total issues: 42
  Errors:      3
  Warnings:    15
  Suggestions: 20
  Hints:       4
  Info:        0

[ERROR] src/Foo.cs:12 - Use of obsolete method 'Bar()'
[WARNING] src/Baz.cs:45 - Possible null reference exception
...
```

## HTTP API (`InspectionHttpServer`)

Starts automatically when a solution opens. Listens on `http://localhost:19876/`.

| Endpoint | Description |
|----------|-------------|
| `GET /` | List available endpoints |
| `GET /health` | Server and solution status |
| `GET /files` | List all user source files under the solution directory |
| `GET /inspect?file=path` | Run code inspection on file(s). Multiple: `&file=a&file=b`. Default markdown; `&format=json` for JSON. `&debug=true` for diagnostics. |
| `GET /blueprints?class=Name` | List UE5 Blueprint classes derived from a C++ or Blueprint class. `&format=json` for JSON. `&debug=true` for diagnostics. |
| `GET /blueprint-audit` | Get Blueprint audit data. Returns 409 if stale, 503 if not ready. `&format=json` for JSON. |
| `GET /blueprint-audit/refresh` | Trigger background refresh of Blueprint audit data via commandlet. |
| `GET /blueprint-audit/status` | Check status of Blueprint audit refresh (in progress, boot check result, last refresh time). |
| `GET /ue-project` | Diagnostic: show UE project detection info (.uproject path, engine path, etc). |

### `/blueprints` details

Uses **reflection** to access `UE4AssetsCache` and `UE4SearchUtil` from `JetBrains.ReSharper.Feature.Services.Cpp.dll` — no closed-source DLL reference needed. Performs recursive BFS to find Blueprint-to-Blueprint derivation chains.

**Known limitation:** Only discovers Blueprints that directly derive from the queried class or from other Blueprints in its hierarchy. Blueprints that derive from **C++ intermediate subclasses** are not found, because the asset cache has no C++ class hierarchy API. To match Rider's "Find Derived Symbols" behavior fully, we would need to walk the C++ PSI symbol tree first (see TODOs below).

### `/blueprint-audit` details

Reads Blueprint audit JSON files generated by the **UnrealBlueprintAudit** companion plugin. Features:

- **Staleness detection**: Compares `SourceFileHash` in each JSON file against the current `.uasset` file's MD5 hash
- **Never returns stale data**: Returns HTTP 409 (Conflict) if any Blueprint data is stale, with a list of stale entries
- **Background refresh**: Spawns `UnrealEditor-Cmd.exe -run=BlueprintAudit` to regenerate audit data
- **Boot check**: Automatically checks for staleness 5 seconds after solution load and triggers refresh if needed
- **Mutex protection**: Only one refresh can run at a time; concurrent requests get "already in progress" response

Requires the [UnrealBlueprintAudit](https://github.com/[your-username]/UnrealBlueprintAudit) plugin to be installed in the UE project.

## TODOs

- [ ] **`/blueprints`: Walk C++ class hierarchy for full recursive Blueprint discovery.** Currently `GetDerivedBlueprintClasses` only returns Blueprints whose direct parent matches the queried name. If `ClassA (C++) -> ClassB (C++) -> BP_Foo (Blueprint)`, querying `ClassA` will miss `BP_Foo`. Need to find a ReSharper C++ PSI API (e.g. `CppInheritorSearcher`, `ICppClassHierarchy`, or similar) to enumerate C++ subclasses, then pass all names to `GetDerivedBlueprintClasses(IEnumerable<string>, UE4AssetsCache, bool)`.
- [ ] **Clean up diagnostic/debug code in `/blueprints`.** The debug dumps of all methods on `UE4SearchUtil` and `UE4AssetsCache` were useful during development but can be trimmed once the API is stable.
- [x] ~~**UE companion plugin for Blueprint audit.**~~ Implemented as [UnrealBlueprintAudit](https://github.com/[your-username]/UnrealBlueprintAudit). The Rider plugin reads JSONs via `/blueprint-audit` endpoint and triggers commandlet runs when data is stale.
- [ ] **`/uclass?class=ClassName`: UPROPERTY/UFUNCTION reflection endpoint.** Add an endpoint that, given a C++ class name, finds its header file and parses `UPROPERTY(...)` and `UFUNCTION(...)` macro declarations. Should return specifiers (e.g. `EditAnywhere`, `BlueprintCallable`, `Category="Foo"`), property/function types, and names. Approach: text-parse the header file directly (regex over macro blocks) rather than using the C++ PSI — this is the simplest strategy and captures the actual macro specifiers as written. Class-name-to-header lookup can reuse the `/files` endpoint's file list or the Unreal naming convention (`ClassName.h`).

## Building and running

```bash
# Build the plugin
.\gradlew.bat :buildPlugin

# Launch a sandbox Rider instance with the plugin installed
.\gradlew.bat :runIde
```

Open a solution in the sandbox IDE. The `FullInspectionTestComponent` will automatically write results to `resharper-full-inspections-dump.txt` on the Desktop once SWEA finishes. Alternatively, use `Ctrl+Alt+Shift+W` or Find Action > "Run Full Inspections" to trigger manually.
