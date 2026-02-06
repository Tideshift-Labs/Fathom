# UE Companion Plugin: Blueprint Audit for LLMs

## Problem

The Rider/ReSharper plugin can inspect C++ source code and walk Blueprint derivation trees via reflection on `UE4AssetsCache`, but it has **no access to Blueprint internals**. `.uasset` is a binary format that only Unreal Engine itself can deserialize. Variables, graphs, nodes, CDO overrides, widget trees are all invisible from the Rider side.

## Solution

A companion Unreal Engine editor plugin that serializes Blueprint internals to JSON on disk. The Rider plugin reads those JSONs. The filesystem is the interface: no IPC, no sockets, no runtime coupling.

## What the UE plugin does

### Core auditor (`FBlueprintAuditor`)

Given a `UBlueprint*`, produces a JSON object containing:

| Section | Details |
|---------|---------|
| Metadata | Name, path, parent class, blueprint type |
| Source file hash | MD5 of the `.uasset` file, used for stale detection |
| Variables | Name, type (with container types: Array/Set/Map), category, `InstanceEditable`, `Replicated` |
| Property overrides | CDO diff against parent class defaults. Captures what the user changed in the Details panel |
| Interfaces | Implemented Blueprint interfaces |
| Components | Actor component hierarchy from `SimpleConstructionScript` |
| Widget tree | Recursive widget hierarchy (for Widget Blueprints) |
| Event graphs | Events, function calls (with target class), variable reads/writes, macro instances |
| Function graphs | Same detail as event graphs |
| Macro graphs | Name and node count |

### Three execution modes

1. **On-save subsystem (`UBlueprintAuditSubsystem`)**: `UEditorSubsystem` that hooks `PackageSavedWithContextEvent`. Every Blueprint save triggers an immediate re-audit. Also runs a deferred stale check on editor startup (compares `.uasset` MD5 hashes against stored hashes in audit JSONs).

2. **Batch commandlet (`UBlueprintAuditCommandlet`)**: Headless, single-run. Two modes:
   - Single asset: `-AssetPath=/Game/UI/WBP_Foo -Output=out.json`
   - All project assets: dumps every `/Game/` Blueprint to individual JSON files
   - Invocation: `UnrealEditor-Cmd.exe Project.uproject -run=BlueprintAudit`

3. **File watcher trigger from Rider**: The Rider plugin watches `Content/` for `.uasset` changes, compares timestamps against existing audit JSONs, and shells out to the commandlet for stale entries. This handles the "Rider open, editor closed" scenario (e.g. after a `git pull` brings in new Blueprint assets).

### Output location

```
{ProjectDir}/Saved/Audit/Blueprints/
```

Mirrors the `Content/` directory layout:
```
/Game/UI/Widgets/WBP_Foo  ->  Saved/Audit/Blueprints/UI/Widgets/WBP_Foo.json
```

## Architecture: how the two plugins couple

```
UE Companion Plugin                       Rider Plugin (InspectionHttpServer)
────────────────────                      ─────────────────────────────────────

 On-save subsystem ────writes──┐
                                ├──► Saved/Audit/Blueprints/*.json ◄──reads── /blueprint-audit endpoint
 Commandlet (headless) ──writes─┘              ▲
                                               │
                                          triggers when .uasset
                                          timestamps > JSON timestamps
```

### Coupling points

The contract between the two plugins is purely **filesystem conventions**:

1. **JSON output directory**: `{ProjectDir}/Saved/Audit/Blueprints/`. Both sides agree on this path.
2. **Commandlet name**: `BlueprintAudit`, the hardcoded convention the Rider plugin uses to invoke headless audits.
3. **Engine and project paths**: Rider already knows these from its Unreal Engine integration settings (needed for building/debugging).

No shared config files, no runtime communication protocol, no compile-time dependencies.

### Discovery: is the companion installed?

The Rider plugin doesn't need an explicit check. It:
1. Looks for `Saved/Audit/Blueprints/`. If JSONs exist, reads them.
2. If it needs to refresh, shells out to the commandlet. If it fails (commandlet not registered), the companion isn't installed; gracefully degrade.
3. Optionally: check for a `.uplugin` file in `Plugins/BlueprintAudit/` to proactively hint "companion not installed."

### What each side covers

| Concern | Rider plugin | UE companion plugin |
|---------|-------------|---------------------|
| C++ code inspections (SWEA) | Yes | - |
| C++ class hierarchy | Reflection on Cpp PSI | - |
| Blueprint derivation tree | `UE4AssetsCache` reflection | `AssetRegistry.GetAssetsByClass` |
| Blueprint internals (variables, graphs, nodes, CDO overrides, widget trees) | Cannot access | Full access via `UBlueprint*` |
| UPROPERTY/UFUNCTION metadata | Text-parse headers (Rider-only fallback) | `TFieldIterator<FProperty>` / `TFieldIterator<UFunction>` on loaded `UClass` |
| Trigger | On-demand HTTP | On-save + startup stale check + commandlet |

## POC status

A working proof-of-concept exists in a separate UE5 project:

```
E:\UE\Projects\Workspace\Source\Udemy_CUIEditor\
  Private/
    BlueprintAuditor.cpp        # Core audit logic
    BlueprintAuditCommandlet.cpp # Batch/headless commandlet
    BlueprintAuditSubsystem.cpp  # On-save editor subsystem
  Public/
    BlueprintAuditor.h
    BlueprintAuditCommandlet.h
    BlueprintAuditSubsystem.h
```

### To extract into a standalone plugin

- Move into a proper plugin structure: `Plugins/BlueprintAudit/Source/BlueprintAudit/`
- Module type: **Editor-only** (`Type: Editor`, `LoadingPhase: Default`) so it doesn't ship in cooked/packaged builds.
- Remove dependency on the game module (`Udemy_CUIEditor`); the auditor only uses engine/editor APIs.
- Add a `.uplugin` descriptor.

## Rider-side integration TODO

When integrating with the Rider plugin (`InspectionHttpServer`):

1. Add a `/blueprint-audit?class=WBP_Foo` endpoint that reads from `Saved/Audit/Blueprints/`. Pure file I/O, no engine coupling.
2. Add a file watcher on `Content/**/*.uasset` that compares modification timestamps against corresponding audit JSONs.
3. When stale entries are detected, shell out to the commandlet in the background: `{EngineDir}/Binaries/Win64/UnrealEditor-Cmd.exe {Project}.uproject -run=BlueprintAudit -AssetPath={PackagePath}`
4. Surface a staleness indicator in the response so LLM consumers know whether the data is fresh.
