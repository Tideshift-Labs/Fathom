# CoRider

A Rider/ReSharper plugin for extracting code inspection results from **all** solution files and writing them to a file on disk.

## Goal

Run ReSharper code inspections on every file in a solution, including files not open in the editor, and dump the results to a text file. This enables bulk inspection output without relying on external tooling.

## Background

ReSharper's **Solution-Wide Error Analysis (SWEA)** continuously analyzes all files in a solution in the background. The results are cached in memory. This plugin taps into those cached results (or falls back to per-file daemon analysis) using the same internal APIs that ReSharper's built-in "Inspect Code" feature uses.

## What's in the plugin

| File | Purpose |
|------|---------|
| `InspectionHttpServer.cs` | `[SolutionComponent]` HTTP server on `localhost:19876` that exposes `/inspect`, `/files`, `/health`, `/blueprints` endpoints for LLM and tool integration |
| `RunFullInspectionsAction.cs` | Action (`Ctrl+Alt+Shift+W`) that collects inspections from all solution files via SWEA or local daemon fallback, writes to `resharper-full-inspections-dump.txt` on Desktop |
| `FullInspectionTestComponent.cs` | `[SolutionComponent]` test harness that runs automatically on solution open. Polls for SWEA completion then dumps results, no UI interaction needed |
| `RunInspectionsAction.cs` | Earlier approach (`Ctrl+Alt+Shift+I`) that reads `IDocumentMarkupManager` highlighters. Only sees files with active document markup (effectively: open files) |
| `DumpActionsAction.cs` | Utility action (`Ctrl+Alt+Shift+D`) that dumps all registered IDE actions to a file |
| `ActionDumperComponent.cs` | `[ShellComponent]` that dumps registered actions at shell startup |

## Key APIs used

- **`SolutionAnalysisManager`** (`JetBrains.ReSharper.Daemon.SolutionAnalysis`): facade for SWEA, provides `IssueSet` with cached analysis results
- **`SolutionAnalysisConfiguration`** (`JetBrains.ReSharper.Daemon`): exposes `Paused` and `CompletedOnceAfterStart` properties to check SWEA status
- **`CollectInspectionResults.CollectIssuesFromSolutionAnalysis()`** (`JetBrains.ReSharper.Daemon.SolutionAnalysis.RunInspection`): public static method that reads cached SWEA results per file
- **`IssuePointer`**: result type with `Message`, `GetSeverity()`, `Range`, `NavigationOffset`, `IsValid`

## Approaches we're deliberately avoiding

- **`IDocumentMarkupManager` / highlighter enumeration**: only works for files that have an active document model (opened in editor tabs). This is what `RunInspectionsAction.cs` does, and it misses the vast majority of files in a solution.

- **External `inspectcode.exe`**: JetBrains ships a standalone command-line inspection tool, but we want in-process access to the live analysis state with zero extra configuration or process spawning.

- **Custom daemon stages**: writing our own `IDaemonStage` would let us hook into the analysis pipeline, but it's unnecessary complexity when SWEA already caches exactly the results we need.

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

Uses **reflection** to access `UE4AssetsCache` and `UE4SearchUtil` from `JetBrains.ReSharper.Feature.Services.Cpp.dll` without needing a closed-source DLL reference. Performs recursive BFS to find Blueprint-to-Blueprint derivation chains.

**Known limitation:** Only discovers Blueprints that directly derive from the queried class or from other Blueprints in its hierarchy. Blueprints that derive from **C++ intermediate subclasses** are not found, because the asset cache has no C++ class hierarchy API. To match Rider's "Find Derived Symbols" behavior fully, we would need to walk the C++ PSI symbol tree first (see TODOs below).

### `/blueprint-audit` details

Reads Blueprint audit JSON files generated by the **CoRider-UnrealEngine** companion plugin. Features:

- **Staleness detection**: Compares `SourceFileHash` in each JSON file against the current `.uasset` file's MD5 hash
- **Never returns stale data**: Returns HTTP 409 (Conflict) if any Blueprint data is stale, with a list of stale entries
- **Background refresh**: Spawns `UnrealEditor-Cmd.exe -run=BlueprintAudit` to regenerate audit data
- **Boot check**: Automatically checks for staleness 5 seconds after solution load and triggers refresh if needed
- **Mutex protection**: Only one refresh can run at a time; concurrent requests get "already in progress" response

Requires the [CoRider-UnrealEngine](https://github.com/kvirani/CoRider-UnrealEngine) plugin to be installed in the UE project.

## TODOs

- [ ] **`/blueprints`: Walk C++ class hierarchy for full recursive Blueprint discovery.** Currently `GetDerivedBlueprintClasses` only returns Blueprints whose direct parent matches the queried name. If `ClassA (C++) -> ClassB (C++) -> BP_Foo (Blueprint)`, querying `ClassA` will miss `BP_Foo`. Need to find a ReSharper C++ PSI API (e.g. `CppInheritorSearcher`, `ICppClassHierarchy`, or similar) to enumerate C++ subclasses, then pass all names to `GetDerivedBlueprintClasses(IEnumerable<string>, UE4AssetsCache, bool)`.
- [ ] **Clean up diagnostic/debug code in `/blueprints`.** The debug dumps of all methods on `UE4SearchUtil` and `UE4AssetsCache` were useful during development but can be trimmed once the API is stable.
- [x] ~~**UE companion plugin for Blueprint audit.**~~ Implemented as [CoRider-UnrealEngine](https://github.com/kvirani/CoRider-UnrealEngine). The Rider plugin reads JSONs via `/blueprint-audit` endpoint and triggers commandlet runs when data is stale.
- [ ] **`/uclass?class=ClassName`: UPROPERTY/UFUNCTION reflection endpoint.** Add an endpoint that, given a C++ class name, finds its header file and parses `UPROPERTY(...)` and `UFUNCTION(...)` macro declarations. Should return specifiers (e.g. `EditAnywhere`, `BlueprintCallable`, `Category="Foo"`), property/function types, and names. Approach: text-parse the header file directly (regex over macro blocks) rather than using the C++ PSI. This is the simplest strategy and captures the actual macro specifiers as written. Class-name-to-header lookup can reuse the `/files` endpoint's file list or the Unreal naming convention (`ClassName.h`).
- [ ] Get error `ArgumentException: An item with the same key has already been added. Key: <pathtofile>` if same file is added twice to /inspect
- [ ] Add a root route that lists all the routes and their descriptions.
- [ ] **Notification balloons on server start/failure.** The RD protocol model (`CoRiderModel`) already has a `serverStatus` signal, and the C# backend already fires it on success/failure in `StartServer()`. The Kotlin frontend just needs to `advise` on it and show `NotificationGroupManager` balloons. The blocker is getting a `Lifetime` scoped to the solution. `project.solution.lifetime` and `project.solutionLifetime` (from `com.jetbrains.rider.projectView`) both failed to resolve against Rider SDK 2025.3. The `notificationGroup` XML registration and `NotificationGroupManager` code are straightforward once the lifetime is sorted. Possible leads: check if `RdExtBase` exposes a lifetime through its protocol, or check JetBrains/resharper-unity for how they advise on RD signals from `ProjectActivity`.
- [ ] The /inspect endpoint is no longer working for .cs (C#) files. It doesn't error, just reports 0 issues when there are indeed issues to report. 

## Prerequisites

- **JDK 17+** (for Gradle / IntelliJ Platform plugin)
- **Visual Studio** or **.NET SDK** (for compiling the ReSharper backend)
- **Windows** (currently the only supported platform)

### First-time setup

The build requires `vswhere.exe` and `nuget.exe` in the `tools/` directory. These are gitignored, so after a fresh clone run:

```powershell
.\scripts\setup.ps1
```

This downloads both tools automatically.

## Building and running

```powershell
.\gradlew.bat :compileDotNet    # Compile only
.\gradlew.bat :buildPlugin      # Build distributable
.\gradlew.bat :runIde           # Launch sandbox Rider with plugin
```

Open a solution in the sandbox IDE. The `FullInspectionTestComponent` will automatically write results to `resharper-full-inspections-dump.txt` on the Desktop once SWEA finishes. Alternatively, use `Ctrl+Alt+Shift+W` or Find Action > "Run Full Inspections" to trigger manually.

## Architecture

```
CoRider/
├── src/dotnet/ReSharperPlugin.CoRider/
│   ├── InspectionHttpServer2.cs          # HTTP server entry point ([SolutionComponent])
│   ├── ServerConfiguration.cs            # Server config (port, etc.)
│   ├── ICoRiderZone.cs                   # Plugin zone marker
│   ├── Handlers/
│   │   ├── IRequestHandler.cs            # Handler interface
│   │   ├── IndexHandler.cs               # GET /  (endpoint listing)
│   │   ├── InspectHandler.cs             # GET /inspect  (code inspection)
│   │   ├── FilesHandler.cs               # GET /files  (source file listing)
│   │   ├── BlueprintsHandler.cs          # GET /blueprints  (Blueprint derivation)
│   │   ├── BlueprintAuditHandler.cs      # GET /blueprint-audit/*  (audit data)
│   │   └── UeProjectHandler.cs           # GET /ue-project  (UE detection info)
│   ├── Services/
│   │   ├── InspectionService.cs          # InspectCodeDaemon wrapper
│   │   ├── PsiSyncService.cs             # PSI content sync (disk → PSI)
│   │   ├── FileIndexService.cs           # Solution file enumeration
│   │   ├── BlueprintQueryService.cs      # UE4AssetsCache reflection
│   │   ├── BlueprintAuditService.cs      # Audit JSON reader + staleness
│   │   ├── UeProjectService.cs           # UE project detection
│   │   └── ReflectionService.cs          # JetBrains API reflection helpers
│   ├── Formatting/
│   │   ├── InspectMarkdownFormatter.cs   # Inspection → markdown
│   │   ├── BlueprintMarkdownFormatter.cs # Blueprint results → markdown
│   │   ├── AuditMarkdownFormatter.cs     # Audit data → markdown
│   │   └── HttpHelpers.cs               # HTTP response utilities
│   ├── Models/
│   │   ├── InspectionModels.cs           # Inspection DTOs
│   │   ├── BlueprintModels.cs            # Blueprint DTOs
│   │   ├── FilesResponse.cs              # File list DTO
│   │   └── UEProjectInfo.cs              # UE project info DTO
│   └── Serialization/
│       └── Json.cs                       # JSON serialization helpers
├── src/rider/main/
│   ├── kotlin/com/jetbrains/rider/plugins/corider/
│   │   ├── CoRiderHost.kt                  # ProjectActivity: pushes settings port to backend via RD
│   │   └── CoRiderSettings.kt              # Settings page (Tools > CoRider) + persistent state
│   └── resources/META-INF/
│       └── plugin.xml                      # IntelliJ plugin descriptor
├── protocol/src/main/kotlin/model/rider/
│   └── CoRiderModel.kt                    # RD protocol model (generates C# + Kotlin)
├── scripts/
│   ├── setup.ps1                         # First-time tool download
│   ├── settings.ps1                      # Shared build variables
│   ├── publishPlugin.ps1                 # Publish to JetBrains marketplace
│   └── runVisualStudio.ps1               # ReSharper hive setup
├── docs/
│   ├── ue-companion-plugin.md            # Blueprint audit architecture doc
│   ├── LEARNINGS.md                      # Dead ends and hard-won lessons
│   └── reference_files/                  # Decompiled JetBrains API references
└── gradlew.bat                           # Gradle wrapper
```

## Development Workflow

### Testing the plugin

1. Build and launch the sandbox IDE:
   ```powershell
   .\gradlew.bat :runIde
   ```
2. Open a UE project (or any C++/C# solution) in the sandbox Rider instance.
3. Verify the server started:
   ```powershell
   curl http://localhost:19876/health
   ```
4. Test inspection:
   ```powershell
   curl "http://localhost:19876/inspect?file=Source/MyFile.cpp"
   ```
5. Test Blueprint endpoints (UE projects only):
   ```powershell
   curl "http://localhost:19876/blueprints?class=AMyActor&debug=true"
   curl http://localhost:19876/blueprint-audit/status
   ```

### Debugging

- **Backend logs**: `build/idea-sandbox/RD-{version}/log/backend.{date}.{pid}.log`. Search for your class name; verbose entries use `|V|` prefix.
- **Debug query param**: Append `&debug=true` to `/inspect` or `/blueprints` for per-file diagnostics (PSI sync timing, retries, reflection method signatures).
- **Marker file**: On startup the server writes `resharper-http-server.txt` to the Desktop to confirm the component loaded.

## Cross-Repo Coordination

This plugin works with the companion [CoRider-UnrealEngine](https://github.com/kvirani/CoRider-UnrealEngine) UE editor plugin. When modifying the Blueprint audit system, keep these in sync:

- **Audit schema version**: `BlueprintAuditService.AuditSchemaVersion` (this repo) must match `FBlueprintAuditor::AuditSchemaVersion` in `BlueprintAuditor.h` (UE repo). Bump both together when the JSON schema changes.
- **Audit output path**: `Saved/Audit/v<N>/Blueprints/...`. The `v<N>` version segment invalidates cached JSON automatically. Both sides must agree on this path structure.
- **Commandlet name**: The Rider plugin invokes `UnrealEditor-Cmd.exe -run=BlueprintAudit`. This name is hardcoded on both sides.

## Proposals

- [001 - Delegate Binding Map](proposals/001-delegate-binding-map.md)
- [002 - Asset Reference Graph](proposals/002-asset-reference-graph.md)

## Important Notes

- **Windows-only** currently due to hardcoded paths like `Win64`, `UnrealEditor-Cmd.exe`.
- **Port** defaults to 19876. Configurable via Settings > Tools > CoRider, or env var `RIDER_INSPECTOR_PORT` (takes priority over settings).
- **Desktop marker file**: `resharper-http-server.txt` is written on startup to confirm the server component loaded.
- **Symlinks on Windows**: Prefer `New-Item -ItemType Junction` over `mklink /D` for directory symlinks. Junctions don't require admin or Developer Mode, and `mklink` is a `cmd.exe` built-in that doesn't work directly in PowerShell.
