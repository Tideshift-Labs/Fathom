# UE Companion Plugin: Blueprint Audit for LLMs

## Problem

The Rider/ReSharper plugin can inspect C++ source code and walk Blueprint derivation trees via reflection on `UE4AssetsCache`, but it has **no access to Blueprint internals**. `.uasset` is a binary format that only Unreal Engine itself can deserialize. Variables, graphs, nodes, CDO overrides, widget trees are all invisible from the Rider side.

## Solution

A companion Unreal Engine editor plugin ([Fathom-UnrealEngine](https://github.com/Tideshift-Labs/Fathom-UnrealEngine)) that serializes Blueprint internals to Markdown files on disk. The Rider plugin reads those files. The filesystem is the interface: no IPC, no sockets, no runtime coupling.

## What the UE plugin does

### Core auditor (modular domain auditors)

The audit system is split into domain-specific auditor structs (`FBlueprintGraphAuditor`, `FDataTableAuditor`, `FDataAssetAuditor`, `FUserDefinedStructAuditor`, `FControlRigAuditor`) under `Public/Audit/`. `FBlueprintAuditor` remains as a thin facade for backward compatibility.

Uses a two-phase architecture to avoid blocking the editor UI:

1. **Phase 1 (game thread):** `GatherData()` / `GatherBlueprintData()` reads UObject pointers and populates plain-old-data (POD) structs (defined in `Audit/AuditTypes.h`) that contain no UObject pointers.
2. **Phase 2 (background thread):** `SerializeToMarkdown()` + `FAuditFileUtils::WriteAuditFile()` converts POD structs to Markdown, computes the source file hash, and writes to disk.

Given a `UBlueprint*`, produces a Markdown file containing:

| Section | Details |
|---------|---------|
| Metadata | Name, path, parent class, blueprint type |
| Source file hash | MD5 of the `.uasset` file, used for stale detection |
| Variables | Name, type (with container types: Array/Set/Map), category, `InstanceEditable`, `Replicated` |
| Property overrides | CDO diff against parent class defaults. Captures what the user changed in the Details panel |
| Interfaces | Implemented Blueprint interfaces |
| Components | Actor component hierarchy from `SimpleConstructionScript` |
| Timelines | Name, length, loop/autoplay flags, track counts per type |
| Widget tree | Recursive widget hierarchy (for Widget Blueprints) |
| Event graphs | Full graph topology: typed node list, execution flow edges, data dependency edges |
| Function graphs | Same as event graphs, plus function signature (input/output parameter names and types) |
| Macro graphs | Same as function graphs |

### Three execution modes

1. **On-save subsystem (`UBlueprintAuditSubsystem`)**: `UEditorSubsystem` that hooks `PackageSavedWithContextEvent`. Every Blueprint save triggers an immediate re-audit (gather on game thread, serialize + write on background thread). Also hooks `OnAssetRemoved` and `OnAssetRenamed` to clean up audit files when Blueprints are deleted or moved. Includes in-flight dedup to prevent duplicate writes from rapid save-spam.

2. **Batch commandlet (`UBlueprintAuditCommandlet`)**: Headless, single-run. Two modes:
   - Single asset: `-AssetPath=/Game/UI/WBP_Foo -Output=out.md`
   - All project assets: dumps every `/Game/` Blueprint to individual `.md` files
   - Invocation: `UnrealEditor-Cmd.exe Project.uproject -run=BlueprintAudit`

3. **Startup stale check (subsystem state machine)**: On editor startup, runs a five-phase state machine that background-hashes all `.uasset` files, identifies stale entries, and re-audits them in batches of 5 per tick to avoid freezing the editor. After processing, sweeps orphaned audit files whose source `.uasset` no longer exists.

### Output location

```
{ProjectDir}/Saved/Fathom/Audit/v<N>/Blueprints/
```

The `v<N>` segment is the audit schema version (`FAuditFileUtils::AuditSchemaVersion`). When the version is bumped, all cached files are automatically invalidated because no files exist at the new path.

Mirrors the `Content/` directory layout:
```
/Game/UI/Widgets/WBP_Foo  ->  Saved/Fathom/Audit/v<N>/Blueprints/UI/Widgets/WBP_Foo.md
```

### Staleness detection

Staleness is detected by comparing MD5 hashes of `.uasset` files, not timestamps. Each audit file includes a `Hash:` header line containing the MD5 of the source `.uasset` at generation time. To check freshness:

```
Stored hash (in .md)  !=  Current hash (computed from .uasset)  =>  STALE
```

Both sides compute MD5 independently. The UE plugin uses `FMD5Hash::HashFile()`. The Rider plugin uses `System.Security.Cryptography.MD5`. The hex output format matches (lowercase, no separators).

## Architecture: how the two plugins couple

```
UE Editor Plugin                          Rider Plugin (.NET backend)
====================                      ===========================

 BlueprintAuditSubsystem (on-save)        BlueprintAuditService
 BlueprintAuditCommandlet (headless)      BlueprintAuditHandler (HTTP)
 Domain auditors (Audit/*.cpp)            AuditMarkdownFormatter
 FBlueprintAuditor facade
         |                                          |
         +---writes--->  Saved/Fathom/Audit/v<N>/  <---reads---+
                         Blueprints/*.md
```

The UE plugin writes Markdown files. The Rider plugin reads them. That is the entire interface.

### Coupling points

The contract between the two plugins is purely **filesystem conventions**:

1. **Audit output directory**: `{ProjectDir}/Saved/Fathom/Audit/v<N>/Blueprints/`. Both sides agree on this path.
2. **Schema version**: `FAuditFileUtils::AuditSchemaVersion` (C++, canonical constant; `FBlueprintAuditor::AuditSchemaVersion` proxies it) must match `BlueprintAuditService.AuditSchemaVersion` (C#). Divergence means the Rider plugin looks in the wrong directory.
3. **Commandlet name**: `BlueprintAudit`, the hardcoded convention the Rider plugin uses to invoke headless audits.
4. **Hash algorithm**: Both sides compute MD5 with lowercase hex output, no separators.
5. **Header field names**: The Rider-side `ParseAuditHeader()` reads `Name` (from H1), `Path`, `Hash`, `Parent`, `Type` from the Markdown header block.

No shared config files, no runtime communication protocol, no compile-time dependencies.

### Discovery: is the companion installed?

The Rider plugin detects the companion through two mechanisms:

1. Looks for `Saved/Fathom/Audit/v<N>/Blueprints/`. If `.md` files exist, reads them.
2. If it needs to refresh, shells out to the commandlet. If it fails with "unknown commandlet" or similar, sets a `_commandletMissing` flag. Subsequent requests return HTTP 501 with installation instructions rather than repeatedly failing.

### What each side covers

| Concern | Rider plugin | UE companion plugin |
|---------|-------------|---------------------|
| C++ code inspections (SWEA) | Yes | - |
| C++ class hierarchy | Reflection on Cpp PSI | - |
| Blueprint derivation tree | `UE4AssetsCache` reflection | `AssetRegistry.GetAssetsByClass` |
| Blueprint internals (variables, graphs, nodes, CDO overrides, widget trees) | Cannot access | Full access via `UBlueprint*` |
| Asset dependencies and referencers | Via audit HTTP endpoint | HTTP API (`FHttpServerModule`) on port 19900-19910 |
| Staleness detection | MD5 hash comparison on read | MD5 hash comparison on startup + on-save |
| Trigger | Boot check + on-demand HTTP | On-save + startup stale check + commandlet |

## Rider-side integration

The Rider plugin (.NET backend) provides HTTP endpoints for accessing audit data:

| Endpoint | Method | Purpose |
|----------|--------|---------|
| `/blueprint-audit` | GET | Returns audit data. 200 if fresh, 409 if stale, 503 if not ready, 501 if commandlet missing |
| `/blueprint-audit/refresh` | POST | Triggers commandlet (202 accepted, or already in progress) |
| `/blueprint-audit/status` | GET | Refresh progress, boot check status, last output/error |
| `/bp?file=/Game/Path` | GET | Composite: audit data + asset dependencies + referencers |

### Boot check

On solution open (after a configurable delay), the Rider plugin runs `CheckAndRefreshOnBoot()`:
1. Waits for engine path resolution (retries if Rider is still indexing)
2. Checks if the audit directory exists
3. If it exists, hashes all `.uasset` files and compares against stored hashes
4. If any entries are stale (or the directory is missing), triggers a commandlet refresh

### Commandlet detection

When the commandlet fails with output containing "unknown commandlet" or similar, the plugin sets a `_commandletMissing` flag and returns HTTP 501 with installation instructions.
