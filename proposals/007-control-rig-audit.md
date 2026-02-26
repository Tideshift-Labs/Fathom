# Proposal 007: Control Rig Audit Support

**Status:** Proposed
**Created:** 2026-02-26

## Problem

Control Rig assets are the primary way procedural animation and rigging logic is authored in UE5. They contain rich node graphs (bone transforms, math operations, IK solvers, control flow) that are just as complex as Blueprint event graphs, but the Fathom auditor currently skips them entirely.

The skip exists because `LoadObject<UBlueprint>(nullptr, ...)` triggers a fatal assertion inside `URigVMController::PostLoad`. The controller expects a loading context (valid Outer) that a bare `LoadObject` call does not provide. This means Control Rig internals are invisible to LLM agents today.

Control Rigs use a completely separate graph system from regular Blueprints. Where Blueprints use `UEdGraph` / `UK2Node` / `UEdGraphPin`, Control Rigs use:

| RigVM Class | Blueprint Equivalent | Purpose |
|---|---|---|
| `URigVMGraph` | `UEdGraph` | Graph container |
| `URigVMNode` | `UEdGraphNode` | Node in the graph |
| `URigVMPin` | `UEdGraphPin` | Input/output connector |
| `URigVMLink` | Wire | Connection between pins |
| `URigVMController` | (none) | Graph mutation manager |

This means existing `GatherGraphData()` / `SerializeGraphToMarkdown()` cannot be reused directly. The gather logic needs new code that walks the RigVM model.

## Scope

This proposal covers `UControlRigBlueprint` assets specifically. `URigVMBlueprint` is the base class, and in principle other RigVM-based asset types could appear in the future (Anim Next, third-party plugins). The implementation should be structured so that supporting additional `URigVMBlueprint` subclasses later is straightforward, but only Control Rigs need to ship in the first pass.

## Design

### New POD structs

Mirror the existing Blueprint audit pattern: gather on the game thread into plain structs, serialize on a background thread.

```cpp
struct FRigVMPinAuditData
{
    FString Name;
    FString CPPType;        // "float", "FVector", "FRigElementKey", etc.
    FString Direction;      // "Input", "Output", "IO", "Hidden"
    FString DefaultValue;   // serialized default (empty if not set)
};

struct FRigVMNodeAuditData
{
    int32 Id = 0;
    FString Type;           // "Unit", "Variable", "FunctionRef", "FunctionEntry",
                            // "FunctionReturn", "Collapse", "Other"
    FString Name;           // display title
    FString StructPath;     // for Unit nodes: "FRigUnit_SetBoneTransform"
    FString MethodName;     // for Unit nodes: "Execute"
    bool bIsMutable = false;
    bool bIsPure = false;
    bool bIsEvent = false;
    TArray<FRigVMPinAuditData> Pins;
};

struct FRigVMEdgeAuditData
{
    int32 SourceNodeId = 0;
    FString SourcePinPath;  // dot-separated: "Color.R"
    int32 TargetNodeId = 0;
    FString TargetPinPath;
};

struct FRigVMGraphAuditData
{
    FString Name;
    TArray<FGraphParamData> Inputs;   // reuse existing struct
    TArray<FGraphParamData> Outputs;
    TArray<FRigVMNodeAuditData> Nodes;
    TArray<FRigVMEdgeAuditData> Edges;
};

struct FControlRigAuditData
{
    FString Name;
    FString Path;
    FString PackageName;
    FString ParentClass;
    FString SourceFilePath;
    FString OutputPath;

    TArray<FVariableAuditData> Variables;    // reuse existing struct
    TArray<FRigVMGraphAuditData> Graphs;     // main graph + sub-graphs
};
```

### Gather implementation

A new static method `FBlueprintAuditor::GatherControlRigData(const UControlRigBlueprint*)` that:

1. Reads metadata (name, path, parent class) from the blueprint.
2. Calls `GetAllModels()` to get all `URigVMGraph` instances (main graph plus collapsed sub-graphs).
3. For each graph, iterates `GetNodes()`:
   - Skips `URigVMRerouteNode` and `URigVMCommentNode` (same policy as Blueprint knots/comments).
   - Assigns sequential IDs.
   - Classifies by node subclass (`URigVMUnitNode` -> "Unit", `URigVMVariableNode` -> "Variable", etc.).
   - For `URigVMUnitNode`, captures `GetScriptStruct()->GetPathName()` and `GetMethodName()`.
   - Captures top-level pins via `GetPins()` with type, direction, and default value.
4. Iterates `GetLinks()` to build the edge list, mapping pin owners to node IDs.
5. Reads graph variables via `GetVariableDescriptions()`.

### Serialize implementation

A new static method `FBlueprintAuditor::SerializeControlRigToMarkdown(const FControlRigAuditData&)` producing output like:

```markdown
# CR_Mannequin_BasicRig

Path: /Game/Animation/Rigs/CR_Mannequin_BasicRig
Parent: UControlRig
Type: ControlRig
Hash: a1b2c3d4e5f6...

## Variables

| Name | Type | Category |
|------|------|----------|
| Alpha | float | |
| IKEnabled | bool | Settings |

## Graph: RigGraph

### Nodes

| Id | Type | Name | Struct | Details |
|----|------|------|--------|---------|
| 0 | Unit | BeginExecution | FRigUnit_BeginExecution | event |
| 1 | Unit | SetBoneTransform | FRigUnit_SetBoneTransform | mutable |
| 2 | Unit | MathFloat_Multiply | FRigUnit_MathFloat_Multiply | pure |
| 3 | Variable | Alpha | | |

### Edges

0->1, 2.Result->1.Weight, 3.Value->2.A
```

The `Type: ControlRig` header field is the key discriminator that lets the Rider side categorize the asset.

### Loading strategy

**On-save path (safe, no changes to loading):**

In `UBlueprintAuditSubsystem::OnPackageSaved`, the Control Rig is already loaded with its graph model intact. Add a check:

```cpp
if (UControlRigBlueprint* CRBP = Cast<UControlRigBlueprint>(Object))
{
    FControlRigAuditData Data = FBlueprintAuditor::GatherControlRigData(CRBP);
    Data.SourceFilePath = FBlueprintAuditor::GetSourceFilePath(PackageName);
    Data.OutputPath = FBlueprintAuditor::GetAuditOutputPath(PackageName);
    DispatchBackgroundWrite(
        FBlueprintAuditor::SerializeControlRigToMarkdown(Data),
        Data.OutputPath);
}
```

Remove the `IsSupportedBlueprintClass` skip for Control Rig classes in the on-save path only.

**Stale-check path (needs investigation):**

The subsystem's `ProcessingStale` phase calls `LoadObject` to re-audit stale assets. Two options to try:

1. `LoadPackage(nullptr, *PkgPath, LOAD_None)` then `FindObject<UControlRigBlueprint>(Pkg, *AssetName)` to provide a valid Outer during load.
2. If that still asserts, defer stale Control Rig re-audits to the next editor session (on-save will catch them). Log a warning.

**Commandlet path (same investigation):**

The commandlet also uses `LoadObject`. Apply the same `LoadPackage` + `FindObject` fix. If it fails, exclude Control Rigs from batch mode and document the limitation.

### Module dependencies

Add to `FathomUELink.Build.cs` PrivateDependencyModuleNames:

```csharp
"RigVMDeveloper",       // URigVMGraph, URigVMNode, URigVMPin, URigVMLink
"ControlRigDeveloper",  // UControlRigBlueprint
```

Both are editor-only plugin modules. Since FathomUELink is already editor-only, this is safe. Wrap the includes in `#if WITH_EDITOR` guards if needed for any non-editor build configurations.

### Output path

Control Rig audit files go under the same versioned audit directory but in their own subfolder:

```
Saved/Fathom/Audit/v<N>/ControlRigs/<relative_path>.md
```

This mirrors the existing pattern where Blueprints, DataTables, DataAssets, and Structures each get their own subdirectory.

## Rider-Side Changes

The Rider plugin changes are minimal since the audit architecture is already generic.

### BlueprintAuditService.cs

1. **`InferAssetType()`**: Add a condition to detect `Type: ControlRig` in the audit header and return `"ControlRig"` as the asset type.
2. **`GetAuditData()`**: Add a `controlRigs` list alongside the existing `blueprints`, `dataTables`, `dataAssets`, `structures` lists. Route entries with type `"ControlRig"` into it.
3. **Directory scanning**: Add `ControlRigs/` to the set of subdirectories scanned under `Saved/Fathom/Audit/v<N>/`.

### BlueprintModels.cs

Add to `BlueprintAuditResult`:
- `List<BlueprintAuditEntry> ControlRigs`
- `int ControlRigCount`

### AuditMarkdownFormatter.cs

Add a `## Control Rigs` section in `FormatAuditResult()`, following the same pattern as the existing DataTable/DataAsset sections.

### FathomMcpServer.cs

Update the `get_blueprint_audit` tool description from "Blueprints, DataTables, and DataAssets" to include "Control Rigs".

### Schema version

Bump `AuditSchemaVersion` in both repos (UE: `BlueprintAuditor.h`, Rider: `BlueprintAuditService.cs`).

## Implementation Plan

### Phase 1: On-save auditing (core value)

1. Add `RigVMDeveloper` and `ControlRigDeveloper` module dependencies
2. Define the new POD audit structs in `BlueprintAuditor.h`
3. Implement `GatherControlRigData()` and `SerializeControlRigToMarkdown()`
4. Hook into `OnPackageSaved` to detect and audit `UControlRigBlueprint`
5. Write unit-style verification: save a Control Rig in editor, confirm `.md` file appears

### Phase 2: Rider integration

1. Update `InferAssetType()`, `GetAuditData()`, models, and formatter
2. Bump schema version in both repos
3. Verify `get_blueprint_audit` MCP tool returns Control Rig entries

### Phase 3: Commandlet and stale-check (stretch)

1. Test `LoadPackage` + `FindObject` approach for Control Rig loading
2. If it works, enable batch auditing and stale re-auditing
3. If it fails, document the limitation and rely on on-save only

## Risks

### RigVM API stability

The RigVM graph model API has changed across UE versions (the `URigVMBlueprint` intermediate class was introduced in 5.2). Pin to the API surface available in whatever UE version the project targets. Avoid using `URigVMController` (the mutation manager) since we only need read access.

### Module availability

`ControlRigDeveloper` is a plugin module, not an engine module. If a project does not have the Control Rig plugin enabled, the module will not exist. Guard the include and the `Cast<UControlRigBlueprint>` behind a plugin availability check or `#if` preprocessor block to avoid link errors.

### Graph complexity

Control Rig graphs can be large (hundreds of nodes for full-body IK setups). The same chunked/background-write pattern used for Blueprint audits should keep editor responsiveness acceptable, but test with a non-trivial production rig.

### Pin sub-pins

`URigVMPin` supports hierarchical sub-pins (e.g., a `FVector` pin has X/Y/Z children). Phase 1 captures only top-level pins. Sub-pin expansion could be added later if agents need that granularity, but it significantly increases output size.

## Verification

### Phase 1

1. Open a project with a Control Rig asset in the editor
2. Save the Control Rig
3. Confirm a `.md` file appears at `Saved/Fathom/Audit/v<N>/ControlRigs/<path>.md`
4. Inspect the file: should contain header metadata, variable table, node table, and edge list

### Phase 2

```bash
# With the Rider plugin running
curl "http://localhost:19876/blueprint-audit?format=json"
# Response should include "controlRigs" array with the saved Control Rig entry
```
