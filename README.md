# RiderActionExplorer

A Rider/ReSharper plugin for extracting code inspection results from **all** solution files and writing them to a file on disk.

## Goal

Run ReSharper code inspections on every file in a solution — not just files currently open in the editor — and dump the results to a text file. This enables bulk inspection output without relying on external tooling.

## Background

ReSharper's **Solution-Wide Error Analysis (SWEA)** continuously analyzes all files in a solution in the background. The results are cached in memory. This plugin taps into those cached results (or falls back to per-file daemon analysis) using the same internal APIs that ReSharper's built-in "Inspect Code" feature uses.

## What's in the plugin

| File | Purpose |
|------|---------|
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

## Building and running

```bash
# Build the plugin
.\gradlew.bat :buildPlugin

# Launch a sandbox Rider instance with the plugin installed
.\gradlew.bat :runIde
```

Open a solution in the sandbox IDE. The `FullInspectionTestComponent` will automatically write results to `resharper-full-inspections-dump.txt` on the Desktop once SWEA finishes. Alternatively, use `Ctrl+Alt+Shift+W` or Find Action > "Run Full Inspections" to trigger manually.
