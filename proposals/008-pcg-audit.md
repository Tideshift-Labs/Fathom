# Proposal 008: PCG Graph Audit Support

**Status:** Implemented (2026-06-10)
**Created:** 2026-06-08

> **Implementation notes (supersede the text below where they differ):**
> - **No schema version bump.** Adding a new asset type does not change existing audit files, so there is nothing to cache-bust. `AuditSchemaVersion` stays at 15 in both repos; PCG audits backfill via the startup stale check (missing audit file = stale).
> - **No `PCG/` output subfolder.** The audit layout mirrors content paths, not asset types; PCG files go through `FAuditFileUtils::GetAuditOutputPath` unchanged. The `Type: PCG` header line is the discriminator, exactly like StateTree.
> - **No Rider directory-scanning change.** The scan is already recursive; only `InferAssetType` routing was added.
> - **Subgraphs are reference-only.** No recursive nested `SubGraphs` array: every project subgraph is itself a `UPCGGraph` asset with its own audit file, so nodes record `Subgraph:` (and `SubgraphInstance:`) asset paths and agents follow the reference. This also removes the subgraph fan-out risk entirely.
> - **`UPCGGraphInstance` assets are audited in v1** as their own top-level files (parent graph path, resolved base graph, parameter table with override markers).
> - **Settings type names come from `StaticEnum<EPCGSettingsType>()` reflection**, not a switch, so enumerators added in newer engine versions stringify automatically with no version guards. Pin `AllowedTypes` is likewise resolved by property name and exported via reflection, surviving the 5.5 enum to 5.7 `FPCGDataTypeIdentifier` type change without compile-time references.
> - All five extension callbacks are registered together; the phase split below ended up being a verification split only.
> - **Settings depth (Phase 3, tuned on the Electric Dreams sample):** depth B plus targeted expansion. Overridable params are emitted only when they differ from the class defaults; edit-visible `Instanced` subobjects (mesh selectors, instance data packers, Blueprint element instances) are expanded with CDO filtering, since they hold key node configuration that overridable params miss. Advanced and override-generated pins are omitted from pin lists.

## Problem

PCG (Procedural Content Generation) graphs are a primary authoring surface in UE 5.5+ for world population, scattering, terrain-aware placement, and rule-driven layout. A `UPCGGraph` is a node graph every bit as structural as a Blueprint event graph (sampler nodes, spatial operations, filters, spawners, subgraphs, control flow), but Fathom skips PCG assets entirely. Their internals are invisible to LLM agents today: an agent asked to reason about or modify procedural generation has no text representation of the graph to work from.

PCG uses a completely separate graph system from Blueprints. Where Blueprints use `UEdGraph` / `UK2Node` / `UEdGraphPin`, PCG uses its own UObject-based hierarchy (none of it derives from `UEdGraph`):

| PCG Class | Blueprint Equivalent | Purpose |
|---|---|---|
| `UPCGGraph` | `UEdGraph` | Graph container (`GetNodes()`, `ForEachNodeRecursively()`) |
| `UPCGNode` | `UEdGraphNode` | Node in the graph |
| `UPCGPin` | `UEdGraphPin` | Input/output connector (`InputPins` / `OutputPins`) |
| `UPCGEdge` | Wire | Explicit connection object (`InputPin` upstream, `OutputPin` downstream) |
| `UPCGSettings` | (node subclass internals) | Per-node configuration + node type (`GetType()`) |

So the existing Blueprint gather/serialize code cannot be reused directly. However, this is the third non-Blueprint graph type Fathom would support after ControlRig (RigVM) and StateTree, and the extension architecture for exactly this already exists. PCG is in fact **easier to traverse** than either: nodes, pins, and edges are all explicit arrays, subgraph recursion is built in, and node typing is a single enum query rather than a `dynamic_cast` ladder.

## Scope

This proposal covers `UPCGGraph` assets (and, transitively, the `UPCGGraphInstance` parameter overrides that reference them). Unlike ControlRig, a PCG graph is a standalone asset, not a `UBlueprint` subclass, so the `URigVMController::PostLoad` loading hazard that complicated Proposal 007 does not apply here.

PCG is an **optional** engine plugin that many projects do not enable. Therefore PCG auditing must follow the **StateTree optional-module pattern** (a separate sibling module gated at runtime), not the ControlRig pattern (in-core, hard dependency, compile-time `__has_include` shim). Core `FathomUELink` must not take a hard dependency on the PCG modules.

## Design

The audit infrastructure (extension registry, file I/O, MD5 staleness, background write dispatch, commandlet/subsystem/stale-check pipelines) is already generic. PCG support is a new self-contained module that registers callbacks; **core needs no changes** beyond a two-line optional-load registration and the shared schema-version bump.

### New optional module: `FathomUELinkPCG`

Mirror `FathomUELinkStateTree` exactly. Four wiring parts:

1. **`FathomUELink.uplugin` Plugins array** — declare PCG as a known optional plugin so UBT neither force-enables it nor errors when absent:
   ```json
   { "Name": "PCG", "Enabled": false, "Optional": true }
   ```
2. **`.uplugin` Modules array** — declare the sibling module with `LoadingPhase: None` so the plugin system never auto-loads it (its PCG dependencies can never fail to resolve in a project without PCG):
   ```json
   { "Name": "FathomUELinkPCG", "Type": "Editor", "LoadingPhase": "None" }
   ```
3. **`FathomUELinkPCG.Build.cs`** — depends on `FathomUELink`, `AssetRegistry`, and `PCG` (add `PCGEditor` only if editor-only graph metadata is needed). Core `FathomUELink.Build.cs` is untouched.
4. **Core `FFathomUELinkModule::LoadOptionalModules()`** — add a gated load using the existing generic helper:
   ```cpp
   const FName PCGRequiredPlugins[] = { TEXT("PCG") };
   LoadOptionalModuleIfPluginsPresent(TEXT("FathomUELinkPCG"), PCGRequiredPlugins);
   ```
   This loads `FathomUELinkPCG` only when the PCG runtime module is present, and the existing `OnModulesChanged` hook covers late lazy-loads. (Confirm the gating module name is literally `PCG` against `PCG.Build.cs` at implementation time.)

The sibling module's `StartupModule()` registers an `FAuditExtensionRegistry::FExtension` (the same five callbacks StateTree provides: `BatchAudit`, `TryAuditSavedObject`, `BuildStaleCheckList`, `ReAuditStaleEntry`, `IsHandledAsset`), keyed on `UPCGGraph::StaticClass()`.

### New POD structs

Gather on the game thread into plain structs (no UObject pointers); serialize on a background thread. Reuse `FGraphParamData` for parameters.

```cpp
struct FPCGPinAuditData
{
    FString Label;          // pin label (FName -> string)
    FString Direction;      // "Input" / "Output"
    FString AllowedTypes;   // human-readable from FPCGDataTypeIdentifier (e.g. "Point", "Spatial", "Any")
    bool bAllowMultipleData = true;
};

struct FPCGNodeAuditData
{
    int32 Id = 0;
    FString Type;           // EPCGSettingsType: "Sampler", "Spawner", "Spatial",
                            // "Filter", "Subgraph", "ControlFlow", "Param", etc.
    FString Title;          // GetNodeTitle() display text
    FString SettingsClass;  // GetSettings() class path, e.g. "/Script/PCG.PCGSurfaceSamplerSettings"
    bool bIsSubgraph = false;
    FString SubgraphPath;   // for subgraph nodes: referenced UPCGGraph path
    TArray<FPCGPinAuditData> Pins;
    TArray<FDefaultInputData> Settings;  // selected per-node settings values (see "Settings depth")
};

struct FPCGEdgeAuditData
{
    int32 SourceNodeId = 0;
    FString SourcePinLabel;
    int32 TargetNodeId = 0;
    FString TargetPinLabel;
};

struct FPCGGraphParamData
{
    FString Name;
    FString Type;
    FString DefaultValue;   // from the graph's UserParameters property bag
};

struct FPCGGraphAuditData
{
    FString Name;
    FString Path;
    FString PackageName;
    FString SourceFilePath;
    FString OutputPath;

    TArray<FPCGGraphParamData> Parameters;   // graph-level UserParameters
    TArray<FPCGNodeAuditData> Nodes;
    TArray<FPCGEdgeAuditData> Edges;
    TArray<FPCGGraphAuditData> SubGraphs;     // recursive, one per distinct referenced subgraph
};
```

### Gather implementation

`FPCGGraphAuditor::GatherData(const UPCGGraph*)`, structurally a near-copy of `ControlRigAuditor.cpp`:

1. Read metadata (name, path, package, source path, output path) via `FAuditFileUtils`.
2. Build the node list from `UPCGGraph::GetNodes()`:
   - Skip reroute nodes (`UPCGNode::GetSettings()->GetType() == EPCGSettingsType::Reroute`), assigning sequential IDs to the rest. Optionally include the Input/Output nodes (`GetInputNode()` / `GetOutputNode()`) as explicit endpoints.
   - Classify each node by `GetSettings()->GetType()` (one enum switch, no `dynamic_cast` ladder).
   - Capture title via `GetNodeTitle(EPCGNodeTitleType::FullTitle)` and settings class path via `GetSettings()->GetClass()->GetPathName()`.
   - Capture pins from `GetInputPins()` / `GetOutputPins()` (label, direction, allowed types, multi-data flag).
   - For subgraph nodes (`UPCGSubgraphNode` / `UPCGBaseSubgraphNode`), set `bIsSubgraph` and record `GetSubgraph()->GetPathName()`.
3. Build the edge list: for each node's output pins, walk `UPCGPin::Edges` (each `UPCGEdge` exposes `InputPin` upstream / `OutputPin` downstream), mapping pin owners to node IDs. Trace through reroute nodes to real endpoints using the existing reroute-tracing pattern (mirrors `TraceThroughReroutes` in `ControlRigAuditor.cpp`).
4. Read graph parameters from `UPCGGraph::UserParameters` (`FInstancedPropertyBag`).
5. Recurse into distinct referenced subgraphs (de-duplicated by path to avoid exponential blow-up on shared subgraphs). `ForEachNodeRecursively()` is available, but explicit per-subgraph recursion keeps the output structured as nested `FPCGGraphAuditData`.

### Serialize implementation

`FPCGGraphAuditor::SerializeToMarkdown(const FPCGGraphAuditData&)` producing the standard header block plus node/edge/parameter tables:

```markdown
# PCG_ForestScatter

Path: /Game/Procedural/PCG_ForestScatter.PCG_ForestScatter
Type: PCG
SourcePath: Content/Procedural/PCG_ForestScatter.uasset
Hash: a1b2c3d4e5f6...

## Parameters

| Name | Type | Default |
|------|------|---------|
| Density | float | 0.75 |
| TreeMesh | SoftObjectPath | /Game/Meshes/SM_Pine |

## Nodes

| Id | Type | Title | Settings | Details |
|----|------|-------|----------|---------|
| 0 | InputOutput | Input | | |
| 1 | Sampler | Surface Sampler | PCGSurfaceSamplerSettings | PointsPerSquaredMeter=0.1 |
| 2 | Filter | Density Filter | PCGDensityFilterSettings | LowerBound=0.5 |
| 3 | Spawner | Static Mesh Spawner | PCGStaticMeshSpawnerSettings | mesh=SM_Pine |
| 4 | InputOutput | Output | | |

## Edges

0.Out->1.In, 1.Out->2.In, 2.Out->3.In, 3.Out->4.In
```

The `Type: PCG` header field is the discriminator the Rider side uses to categorize the asset.

### Settings depth (the one real design decision)

Unlike Blueprint/RigVM, PCG graphs have **no exec flow** (a PCG graph is a pure dataflow DAG). The semantically interesting content is therefore concentrated in each node's `UPCGSettings` property values (a sampler's density, a filter's bounds, a transform's offsets), exposed via UE reflection. Three levels, in increasing cost:

- **A. Type + title only** — minimal, smallest output. Tells an agent the shape of the pipeline but not the tuning.
- **B. Type + title + key overridable params** (recommended for v1) — serialize the node's `CachedOverridableParams` and/or a shallow reflection dump of non-default `UPROPERTY` values via the existing `FathomAuditHelpers` formatters. This is where the actual procedural intent lives.
- **C. Full reflection dump** — every property on every settings object. Highest fidelity, largest output, most noise.

Recommend shipping **B**, reusing the shared property formatters in `AuditHelpers.h` (already exported for sibling modules). Level can be tuned after seeing real graphs.

### Output path

PCG audit files go under the versioned audit directory in their own subfolder, mirroring the existing per-type layout (Blueprints, ControlRigs, StateTrees, DataTables, ...):

```
Saved/Fathom/Audit/v<N>/PCG/<relative_path>.md
```

### Schema version

Bump `AuditSchemaVersion` from v15 to **v16**, kept in sync across both repos:
- UE: `FAuditFileUtils::AuditSchemaVersion` (`AuditFileUtils.h`)
- Rider: `BlueprintAuditService.AuditSchemaVersion` (`BlueprintAuditService.cs`)

## Rider-Side Changes

Minimal, since the audit architecture and Markdown parser are already generic.

### BlueprintAuditService.cs

1. **`InferAssetType()`**: add a branch returning `"PCG"` when the header contains `Type: PCG`.
2. **`GetAuditData()`**: add a `pcgGraphs` list alongside the existing per-type lists; route `"PCG"` entries into it.
3. **Directory scanning**: add `PCG/` to the set of subdirectories scanned under `Saved/Fathom/Audit/v<N>/`.

### Models / formatter / MCP

- Add `List<...> PcgGraphs` and `int PcgGraphCount` to the audit result model.
- Add a `## PCG Graphs` section to `AuditMarkdownFormatter` following the existing per-type sections.
- Update the `get_blueprint_audit` MCP tool description to include PCG graphs.

## Implementation Plan

### Phase 1: Module scaffold + on-save auditing (core value)

1. Create the `FathomUELinkPCG` module (copy the `FathomUELinkStateTree` scaffold: `Build.cs`, `*Module.cpp/.h`, `*Auditor.cpp/.h`, `*AuditTypes.h`).
2. Wire the four optional-load parts (`.uplugin` Plugins + Modules, sibling `Build.cs`, core `LoadOptionalModules()` line).
3. Implement `FPCGGraphAuditor::GatherData()` and `SerializeToMarkdown()` at settings depth **B**.
4. Register the `TryAuditSavedObject` (on-save) and `IsHandledAsset` callbacks.
5. Verify: save a PCG graph in editor, confirm the `.md` appears at `Saved/Fathom/Audit/v<N>/PCG/<path>.md` with nodes, edges, and parameters.

### Phase 2: Batch + stale-check + Rider integration

1. Register `BatchAudit`, `BuildStaleCheckList`, `ReAuditStaleEntry` (PCG loads cleanly via `LoadObject<UPCGGraph>` — no ControlRig-style loading hazard, so the full pipeline is available immediately).
2. Bump schema version to v16 in both repos.
3. Implement the Rider-side `InferAssetType`/`GetAuditData`/model/formatter/MCP changes.
4. Verify the `/blueprint-audit` endpoint returns a `pcgGraphs` array.

### Phase 3: Settings-depth tuning (stretch)

1. Review real production graphs; decide whether to keep depth **B**, trim toward **A**, or selectively expand toward **C** for high-value node types (samplers, spawners).

## Risks

### Module availability

`PCG` is an optional plugin module. The separate-module + `LoadingPhase: None` + runtime gated-load pattern (Proposal-008 Design step 4) is specifically designed to make a PCG-disabled project a no-op with no link or load errors. This is the proven StateTree mechanism; the main implementation risk is using the wrong gating module name (verify against `PCG.Build.cs`).

### PCG API stability

The PCG framework reached production in UE 5.3-5.4 and continues to evolve (GPU nodes, dynamic mesh data, data layers added in later 5.x). Pin to the read-only API surface available in the project's target UE version (`GetNodes`, `GetInputPins`/`GetOutputPins`, `UPCGPin::Edges`, `GetSettings`/`GetType`/`GetNodeTitle`, `GetSubgraph`, `UserParameters`). Avoid any mutation API. Minimum target is UE 5.5 per project policy.

### Subgraph fan-out

Shared subgraphs referenced by many nodes (or recursive/cyclic graph references) could blow up nested output. De-duplicate recursion by subgraph asset path and guard against cycles with a visited set, the same way reroute tracing guards against knot cycles.

### Output size from settings

Depth **C** (full reflection) can produce very large files for graphs with many heavily-configured nodes. Ship depth **B** and rely on the existing background-write/chunking to keep the editor responsive; revisit only if agents need finer granularity.

### Node typing coverage

`EPCGSettingsType` has ~20 values and new ones appear across versions. The classifier should map known types and fall back to a `"Generic"`/`"Other"` label (carrying the settings class path) for unrecognized ones, so newer node types degrade gracefully rather than being dropped.

## Verification

### Phase 1

1. Open a project with the PCG plugin enabled and a `UPCGGraph` asset.
2. Save the graph.
3. Confirm a `.md` file appears at `Saved/Fathom/Audit/v<N>/PCG/<path>.md`.
4. Inspect: header metadata, parameter table, node table (with type/title/settings), and edge list; subgraph references resolved and nested.
5. Open a project **without** the PCG plugin; confirm the editor loads cleanly with no `FathomUELinkPCG` load error and no PCG audit output.

### Phase 2

```bash
# With the Rider plugin running
curl "http://localhost:19876/blueprint-audit?format=json"
# Response should include a "pcgGraphs" array with the saved PCG graph entry
```
