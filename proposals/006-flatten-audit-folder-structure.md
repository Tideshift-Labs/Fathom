# 006: Flatten Audit Folder Structure

## Status
Proposed

## Summary
Remove the `Blueprints/`, `DataTables/`, `DataAssets/` subdirectories under `Saved/Fathom/Audit/v<N>/` and place all audit `.md` files in a single flat directory tree that mirrors the UE Content/ layout.

## Current State
```
Saved/Fathom/Audit/v5/
  Blueprints/
    UI/Widgets/WBP_MainMenu.md
    Characters/BP_PlayerCharacter.md
  DataTables/
    Data/DT_WeaponStats.md
  DataAssets/
    Abilities/DA_AbilityConfig.md
```

## Proposed State
```
Saved/Fathom/Audit/v5/
  UI/Widgets/WBP_MainMenu.md
  Characters/BP_PlayerCharacter.md
  Data/DT_WeaponStats.md
  Abilities/DA_AbilityConfig.md
```

This mirrors the UE Content/ directory structure directly.

## Motivation
- The UE Content/ folder is flat. A DataTable and Blueprint in the same Content folder should produce audit files in the same audit folder.
- The asset type is already identifiable from the `.md` header: Blueprints have `Parent:`, DataTables have `RowStruct:`, DataAssets have `ClassPath:`.
- Three folders add branching in every path-related code path (save, remove, rename, sweep, stale check, Rider scan) for no functional benefit.

## Changes Required

### UE Plugin
- **BlueprintAuditor.h**: Remove `GetAuditBaseDir(const FString& AssetTypeFolder)` and `GetAuditOutputPath(const FString&, const FString&)` overloads. The existing parameterless `GetAuditBaseDir()` returns `v<N>/` directly (no subfolder).
- **BlueprintAuditor.cpp**: Delete the parameterized overloads. Existing `GetAuditOutputPath(PackageName)` and `GetAuditBaseDir()` point to the version root.
- **BlueprintAuditSubsystem.cpp**: `OnAssetRemoved` and `OnAssetRenamed` no longer branch on asset type for path calculation; all types use the same `GetAuditOutputPath(PackageName)`. `SweepOrphanedAuditFilesInDir` helper is removed; `SweepOrphanedAuditFiles` scans one directory. `EAuditAssetType` stays (needed for `LoadObject<T>` dispatch in Phase 3).
- **BlueprintAuditCommandlet.cpp**: All types use the same `GetAuditOutputPath`.

### Rider Plugin
- **BlueprintAuditService.cs**: `GetAuditData()`, `CheckAndRefreshOnBoot()`, `FindAuditEntry()` scan one directory. Asset type is derived from parsed header fields (`RowStruct` present = DataTable, `ClassPath` present = DataAsset, else Blueprint).
- No model or MCP changes needed.

### Schema Version
Bump from 5 to 6 since the on-disk layout changes.

## Estimated Scope
Net deletion of code. Removes the parameterized path overloads and collapses three-way branching into single paths throughout. The Rider service adds a small type-inference helper based on header fields.
