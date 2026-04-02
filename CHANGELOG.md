# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.10.0] - 2026-04-02

## [0.10.0] - 2026-04-02

### New Features
- **StateTree auditor**: full state hierarchy with tasks, transitions, enter conditions, considerations, property bindings (e.g. `[LeftTag <- Parameters.Motivation]`), Blueprint class resolution, enum display names, global evaluators/tasks, state metadata (Tag, Selection Behavior, Tasks Completion, Weight, Custom Tick Rate, Required Event with payload), linked state/asset references with parameter overrides. Uses optional module loading pattern for zero compile-time coupling.
- **Audit extension registry** (`AuditExtensionRegistry`): generic callback interface for optional auditors, wired into all subsystem/commandlet integration points. Enables future optional auditors without modifying core plugin code.
- **BehaviorTree auditor**: tree structure with blackboard keys, decorators (abort modes, logic expressions), services (intervals), task properties, and composite node types
- **Material and Material Instance auditing**: material domain, blend mode, shading model, expression graph with node connections, material instance parameter overrides
- Enriched status page with project info, audit stats by type, and endpoint documentation

### Fixes & Changes
- StateTree uses optional module loading: `FathomUELinkStateTree` with `LoadingPhase: None`, dynamically loaded when StateTree plugin is present. Verified to build against projects both with and without StateTree enabled.
- DataAsset duplicate filtering: extension-handled assets (e.g. StateTree, which inherits UDataAsset) filtered from DataAsset audit loops
- Fixed subsystem initialization during commandlet runs causing conflicts with batch audit
- Exported `LogFathomUELink` log category for cross-module access
- Widened log filter to capture BlueprintAudit and BootCheck log lines
- Fixed changelog formatting in JetBrains Marketplace releases
- Audit schema version bumped to v13

## [0.9.1] - 2026-03-30

### New Features
- [UE5] Per-component property overrides in audit files: each Blueprint component now lists non-default property values, with a tree view showing component hierarchy
- [Rider] `get_ue_project_info` MCP tool now returns useful project status instead of raw debug dump

### Fixes & Changes
- [UE5] Fixed ControlRig audit missing edges through reroute/redirect nodes. RigVM reroute nodes have a single IO pin, so the old link-based approach silently dropped connections through them.
- [UE5] Fixed IO pins in ControlRig graphs generating spurious reverse edges in audit files
- [UE5] Fixed crash in stale check during editor startup GC
- [UE5] Fixed AnimBlueprint audit docs to reflect partial support
- Audit schema version bumped to v11
- Updated Claude Desktop MCP docs to use mcp-remote bridge

## [0.9.0] - 2026-02-27

### New Features
- [UE5] Ability to audit nested/collapsed graphs in BPs

### Fixes & Changes
- Port fallback: if the configured port is already in use, the Rider-side HTTP server now tries up to 10 consecutive ports (e.g. 19876, 19877, ..., 19885) before giving up. Marker files, MCP configs, and RD notifications all reflect the actual bound port.
- [UE5] Audit file version bumped to v10.
- Added flowchart to README.md
- Fixed companion plugin notification balloons never appearing. The RD `sink` event fired before `FathomHost` (PostStartupActivity) registered its advise, so the notification was silently lost. Moved notification logic to `FathomStatusBarWidget.install()` which runs early enough to catch the event.
- Fixed boot-time companion plugin detection not triggering the status bar icon or notification bubble. The `BootCheckOrchestrator` was called before the RD scheduler was initialized, so the `companionPluginStatus` sink was never fired despite successful detection.
- Fixed "Install to Engine" leaving a stale Game copy that would shadow the Engine version. UE loads Game plugins before Engine plugins, so an outdated Game copy silently overrode a freshly installed Engine copy. The install logic now removes the Game copy when targeting Engine.

### Refactoring & Internals
- Renamed `InspectionHttpServer2` to `FathomRiderHttpServer` and split into three focused files: `CompanionPluginOrchestrator` (install/build workflows), `McpConfigWriter` (MCP config I/O), and the slimmed-down server class
- [UE5] Modularized `BlueprintAuditor` into domain-specific auditors: `FBlueprintGraphAuditor`, `FDataTableAuditor`, `FDataAssetAuditor`, `FUserDefinedStructAuditor`, `FControlRigAuditor`, `FAuditFileUtils`
- [UE5] Extracted all 23 POD audit data structs into `Public/Audit/AuditTypes.h`
- [UE5] Promoted `CleanExportedValue` helper into `FathomAuditHelpers` namespace (`Private/Audit/AuditHelpers.h/.cpp`)
- `FBlueprintAuditor` is now a thin facade delegating to domain auditors (backward-compatible, no consumer changes needed)
- [UE5] Canonical schema version constant lives in `FAuditFileUtils::AuditSchemaVersion`; `FBlueprintAuditor::AuditSchemaVersion` proxies it
- [UE5] Extracted `FathomHttp::SendJson`/`SendError` helpers to eliminate repeated JSON serialization boilerplate across all HTTP handlers, added `WrapHandler` safety wrapper for crash resilience, and replaced `LoadModuleChecked` with defensive `GetModulePtr` for AssetRegistry access
- [UE5] Introduced an audit version manifest: The UE plugin now writes a new file `audit-manifest.json` so Rider discovers the correct audit directory even when plugin versions differ. Rider shows an info note when a version mismatch is detected.
- Extracted `BootCheckOrchestrator` (boot-time audit + companion plugin detection) and `ServerMarkerWriter` (marker file I/O) from `FathomRiderHttpServer`, reducing it from ~405 to ~307 lines

## [0.8.0] - 2026-02-26

### Added
- Control Rig audit support: on-save, stale check, and commandlet paths now gather and serialize Control Rig node graphs (URigVMGraph/URigVMNode/URigVMPin/URigVMLink)
- New POD structs for Control Rig audit data: FRigVMPinAuditData, FRigVMNodeAuditData, FRigVMEdgeAuditData, FRigVMGraphAuditData, FControlRigAuditData
- Rider plugin: ControlRig categorization in audit results, InferAssetType, formatter, and MCP tool descriptions
- ControlRig and RigVM plugin dependencies in FathomUELink.uplugin and Build.cs

### Changed
- Audit schema version bumped to v9 (both UE and Rider sides)
- Removed ControlRig/RigVM exclusion from IsSupportedBlueprintClass (loading is now safe with modules linked)

## [0.7.0] - 2026-02-24

## [0.6.2] - 2026-02-23

- UE5 Plugin compile error (oops)

## [0.6.1] - 2026-02-23

### Fixed
- UE5 Plugin Crash fix for when we attempt to audit uassets with compile errors

## [0.6.0] - 2026-02-23

### Added
- Blueprint staleness indicators: `IsStale` and `EditorAvailable` fields in blueprint info JSON responses
- Markdown banners for editor-offline state and stale audit data warnings in blueprint info output

### Changed
- Live Coding responses now surface compiler errors in a "Compiler Errors" markdown section when a patch fails
- Shortened "no editor" messages for dependencies/referencers to "Requires live editor connection."

### Fixed
- Fixed JetBrains Marketplace upload endpoint in release workflow (switched to `/api/updates/upload` with explicit `pluginId`)

## [0.5.0] - 2026-02-18

### Added
- Live Coding (Hot Reload) via MCP and HTTP: `/live-coding/compile` and `/live-coding/status` endpoints let AI agents and HTTP clients trigger and monitor UE Live Coding compiles
- `live_coding_compile` and `live_coding_status` MCP tools with per-tool timeout support (130s for compile)
- Branded HTML home page at `/` with live status, feature cards, API endpoint table, and MCP config (replaces old markdown listing)

### Changed
- Build output switched from IntelliJ Build tool window to Run console to avoid plugin verification issues with `@ApiStatus.Internal` classes
- `AssetRefProxyService` gained a `ProxyGetWithStatus` overload accepting a custom `HttpClient` for long-running requests
- MCP server internals refactored from `WebClient` to `HttpWebRequest` to support per-tool timeouts

### Removed
- Deleted custom `BuildEvents.kt` (custom `OutputBuildEvent`/`FinishBuildEvent` implementations no longer needed)

## [0.4.0] - 2026-02-17

### Changed
- MCP tool responses no longer include hyperlinks (navigation URLs are still returned via HTTP endpoints)
- Updated plugin icon and status bar logos

### Fixed
- Removed custom `FathomStartBuildEvent` in favor of the platform `StartBuildEventImpl` (attempt to fix plugin verifier warning for `@NonExtendable` `StartBuildEvent`)
- Removed deprecated `isStdOut()` override from `FathomOutputBuildEvent`
- Fixed release workflow changelog extraction regex and switched Marketplace upload from NuGet push to REST API

## [0.3.0] - 2026-02-16

### Added
- C++ symbol navigation: `/symbols` search, `/symbols/declaration` go-to-definition, and `/symbols/inheritors` class hierarchy endpoints (HTTP + MCP tools)
- Symbol search with kind, scope, and limit filters; declarations include source code snippets
- RiderLink conflict detection: companion plugin install/build actions are blocked while RiderLink installation is in progress
- `.runide-project` file support for auto-opening a project/sln in the sandboxed Rider during development

### Changed
- Custom build event implementations to avoid depending on internal platform classes
- Cleaned up stale reference files, completed proposals, and outdated docs

## [0.2.0] - 2026-02-14

### Added
- DataTable, DataAsset, and UserDefinedStruct audit support (audit schema v5-v7)
- Engine-level install location for UE companion plugin (in addition to Game/project-level)
- Engine plugin builds via RunUAT.bat BuildPlugin
- Streaming build output to Rider's Build tool window in real time
- Concurrent action guard preventing duplicate install/build operations
- Install hash tracking (`.fathom-install-hash`) to detect plugin content changes during local dev
- MCP auto-config support for OpenCode (`opencode.json`)

### Changed
- Moved all Fathom metadata under `Saved/Fathom/` (audit output, server markers)
- Status bar widget shows install location context and disables actions while operations run
- Permission-denied errors on Engine install now suggest running Rider as Administrator or using Game install

## [0.1.1] - 2026-02-12
- Renamed plugin from CoRider to Fathom (by Tideshift Labs) for JetBrains Marketplace submission

## [0.1.0]
- Initial version
