# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.14.0] - 2026-08-02

### Fixes & Changes
- [Rider] Bumped the target platform to Rider 2026.2 (was 2025.3). The HTTP/MCP server could fail to bind on newer Rider builds because engine-path detection falls back to regex-scraping an internal JetBrains type's debug string when the expected `Path` property isn't found; a shape change in that string on a new platform version could hand back a malformed path (e.g. a bare drive root) instead of failing cleanly. `UeProjectService` now validates any path recovered this way and discards it if implausible, rather than passing it downstream.
- [Rider] Bumped the Kotlin Gradle plugin to 2.4.10 (was 2.1.20); Rider 2026.2's platform jars are compiled with newer Kotlin metadata that the old compiler couldn't read. Also declared an explicit `bundledModule("intellij.rider.rdclient.dotnet")` dependency (plus the matching `<module>` entry in `plugin.xml`): 2026.2 split this module out of the previously-implicit `com.intellij.modules.rider` classpath, and it owns the `Project.solution`/protocol-scheduler access the Kotlin frontend code (`FathomHost`, `FathomSettings`, `FathomStatusBarWidget`) depends on.
- [UE5] Fixed the startup audit deadlocking against the editor's own asset compilation on large projects. The bulk re-audit ran every asset in one synchronous loop inside a progress dialog, and a slow task pumps Slate but does not run the engine tick, so `FAssetCompilingManager::ProcessAsyncTasks` never executed for the duration: compilation queued up but was never finalized. The audit's periodic garbage collection then tripped `FSkinnedAssetCompilingManager`'s pre-GC hook, which blocks the game thread until all queued skinned assets finish, and assets waiting on dependencies cannot be started from inside that wait. The result was a modal frozen part-way through, with the editor's own compile progress bar frozen behind it, both resuming only on cancel. The synchronous path is gone; there is now a single tick-driven path that returns to the engine loop between assets so compilation keeps draining.
- [UE5] The bulk re-audit no longer blocks the editor. Progress appears as a status-bar notification alongside the editor's own compilation progress instead of a modal dialog, and the editor stays usable throughout. The audit backs off when the engine's compile backlog gets large and resumes once it drains, so it stays out of the way of a startup compile storm without waiting on the small amount of compilation each audited asset triggers. Run `Fathom.CancelAudit` to defer the remainder to the next launch; anything already written is kept, since staleness is tracked per asset.
- [UE5] Audit-driven garbage collection is now time-gated as well as count-gated, and drains pending compilation first. Collecting every 50 assets was harmless while the bulk loop held the game thread, but with the tick-driven path it would have meant a full GC every few seconds while the user was working.
- [UE5] Capped the number of audit file writes in flight. These run on the same thread pool the editor uses for asset compilation, at a higher priority, so an uncapped bulk re-audit was starving the compilation it was waiting on.
- [UE5] Fixed a fatal editor crash during the startup audit on projects containing a Blueprint saved with `GeneratedClass = None`. `UBlueprint::PostLoad` asserts on such an asset (`check(nullptr != GeneratedClass)`), and because `LoadObject` dies rather than returning null, no guard inside the auditors could catch it. A new pre-load check reads the `GeneratedClass` asset registry tag and skips the asset before it is ever loaded. Assets that hard-reference a broken Blueprint are loaded together with it and die the same way, so the whole transitive hard-referencer closure is skipped too. Soft references are not followed: they resolve lazily and never crash a load.
- [UE5] Added a generic crash quarantine for fatal loads. The audit writes a breadcrumb file naming the package it is about to load and clears it afterwards. A breadcrumb that survives to the next editor launch means the process died on that package, so it is added to a persistent skip list in `Saved/Fathom/quarantine.txt` and logged by name. Any crash-on-load now costs one crash rather than an infinite crash loop on every launch, whatever the cause. Delete the relevant line from the quarantine file to retry an asset after repairing it.
- [UE5] All audit asset loads now go through a single choke point, `FAuditLoadGuard::TryLoadAuditableAsset`, which owns the safety checks and the breadcrumb. The startup stale-check list is filtered centrally after the extension auditors have contributed their entries, so new auditors inherit the protection instead of having to reimplement it.
- [UE5] Fixed a second fatal editor crash on the same assert, with a different cause: the startup stale check only waited for the asset registry to finish scanning, not for the editor's own startup loading. On a large project the asset registry finished while the startup map was still streaming, and the first audit load landed in that window, forcing `FlushAsyncLoading` and pulling a Blueprint through `PostLoad` before its generated class existed. The asset is fine on disk, so no registry check can predict this. Every audit load now gates on the editor being idle (no PIE session, no async loading, not loading a package, not collecting garbage) held continuously for five seconds, so we do not start in a brief gap between two of the engine's own loads. The gate gives up after two minutes and proceeds anyway rather than never auditing, and waits out a PIE session with no time limit. It is re-evaluated before every asset, so an editor that stops being idle part-way through pauses and resumes rather than pushing through.
- [UE5] The crash breadcrumb now covers the whole per-asset audit rather than just the load call, so a crash in a gather pass quarantines the asset too instead of repeating on every launch.
- [UE5] Fixed the plugin failing to compile on UE 5.5 at all. Two causes. First, `UE_VERSION_NEWER_THAN_OR_EQUAL` was only added in 5.6; on 5.5 it silently evaluates to `0`, taking the wrong branch, and then trips `-WarningsAsErrors`. Replaced with the equivalent `!UE_VERSION_OLDER_THAN` in the StateTree and PCG auditors. Second, the StateTree auditor uses five separate APIs that do not exist in 5.5 (the shared property-binding collection, `OnDelegate` transitions, state descriptions, task completion, custom tick rates), and because UBT compiles every module in a plugin unconditionally, that broke the build for all 5.5 users rather than only StateTree ones. The StateTree module is now compiled out on 5.5, logging a line explaining why when loaded. The plugin builds against 5.5, 5.6, 5.7, and 5.8; StateTree audits require 5.6 or newer, and every other auditor works on 5.5.
- [UE5] Skipped assets are now reported instead of silently missing. `Saved/Fathom/skipped.md` lists every excluded package grouped by reason, `audit-manifest.json` carries the same list as structured data, and `get_audit_status` surfaces it. The log names each unloadable seed asset and states how much of the project the referencer closure took out. Blueprints with `ParentClass = None` are reported as suspect: they still load and audit, but their output is likely incomplete.

## [0.13.2] - 2026-06-10

### Added
- [UE5] PCG graph audit support. `UPCGGraph` assets are audited via a new optional `FathomUELinkPCG` module (StateTree-style, loaded only when the PCG plugin is present): graph-level user parameters, node table (type, title, settings class), per-node non-default overridable settings values, instanced subobject configuration (mesh selector entries, instance data packers, Blueprint element properties), pin types, and an edge list traced through reroute nodes (including named reroute declaration/usage pairs). Subgraph nodes record the referenced graph asset path instead of inlining it. `UPCGGraphInstance` assets get their own audit file listing the parent graph and parameter values with override markers. The Rider plugin routes `Type: PCG` audit files into a `pcgGraphs` list on `/blueprint-audit` and includes PCG in the MCP tool descriptions and project status output. No schema bump: existing audit files are unchanged, and PCG audits backfill via the startup stale check.

### Fixes & Changes
- [UE5] Fixed an access violation in the Blueprint graph audit when a pin's `LinkedTo` array contained null or orphaned entries (seen in corrupted or partially-loaded graphs). The knot-trace and edge-building passes now skip null pin links, null owning nodes, null pins, and null graph/node list entries instead of dereferencing them.
- [UE5] Hardened all auditors against the same class of broken references. Properties whose backing type asset was deleted (a `TSubclassOf`/`TSoftClassPtr` with null MetaClass, object refs with null PropertyClass, null enum or struct types) crash inside the engine's `GetCPPType`, `ExportTextItem`, and `Identical`; a shared `HasBrokenTypeMetadata` check now skips them in every property gather loop, DataTable columns/rows render `Unknown`/`(unavailable)` placeholders instead of crashing, the ControlRig reroute trace skips null pin links like the Blueprint knot trace, and the recursive BehaviorTree, Blackboard parent-chain, and StateTree hierarchy walks guard against cyclic references.

## [0.13.1] - 2026-05-10

### Fixes & Changes
- [UE5] Fixed UE 5.6 compile break in StateTree auditor: `EStateTreeExpressionOperand::Multiply` was added in 5.7. The case label is now guarded with `UE_VERSION_NEWER_THAN_OR_EQUAL(5,7,0)`, so 5.5 and 5.6 builds compile cleanly while 5.7 still emits `MUL` for the new operator.

## [0.13.0] - 2026-05-08

### Fixes & Changes
- [UE5] Property overrides now expand `Instanced` UObject subobjects inline instead of emitting a single-line object path. Affects GAS Gameplay Effects (`GEComponents` and the per-component config arrays such as `GrantAbilityConfigs`) and any other UPROPERTY declared with the `Instanced` specifier. Each subobject is labelled with its `EditorFriendlyName` (when present) plus the class name; child fields are filtered against the class CDO so only overrides appear. Cycle detection and an 8-level depth cap prevent runaway recursion. Non-instanced object refs (e.g. `UTexture2D*`) keep the existing asset-name behavior.
- [UE5] Bulk stale re-audit (e.g. after a Fathom audit-format update) now surfaces a cancelable `FScopedSlowTask` progress dialog when 25 or more assets need re-auditing, instead of freezing the editor for minutes with no feedback. Below that threshold, the existing ticker paces one entry every three frames for perceptual smoothness. The dialog text makes it clear the operation is one-time, not per-launch.
- [UE5] Audit now decodes `FInstancedPropertyBag` and `FInstancedStruct` contents wherever they appear (DataAsset, Blueprint, StateTree, BehaviorTree, Material auditors) by unwrapping their dynamic `UScriptStruct` schema. Previously these wrappers rendered as empty `()` because static `FProperty` walking only sees their internal handle members. Also fixes an editor crash on Blueprint audit when a class contained an empty `FInstancedPropertyBag` (caused by `UPropertyBag::InitializeStruct` asserting on a null `Dest` from a zero-size buffer).
- [UE5] StateTree `OnDelegate` transitions now render the bound dispatcher identity, e.g. `OnDelegate (BindWidgetEvent.OnTriggered) -> Target` instead of just `OnDelegate -> Target`. `OnEvent` transitions similarly render their event tag and optional payload struct. Previously a fan-out of multiple `OnDelegate` transitions all rendered as identical-looking lines distinguishable only by target state, making the actual flow logic unreadable.
- [UE5] Fixed Blueprint graph audit emitting self-edges through knot/reroute nodes (e.g. a Branch's `else` output passing through a reroute would render as `1-[else]->1`, suggesting an infinite loop). The knot trace direction was inverted, walking back upstream to the source rather than forward through the knot's output pin. (Tideshift-Labs/Fathom#45)
- Audit schema version bumped to v15.

## [0.12.0] - 2026-05-07

### Fixes & Changes
- [UE5] Fixed Blueprint function audits duplicating output parameters when the function had multiple return nodes. Outputs are now collected from a single result node since they share the function signature. (Tideshift-Labs/Fathom#36)
- [UE5] DataAsset, Blueprint, BehaviorTree, Material, and StateTree audits now serialize array, set, map, and struct properties as structured indented Markdown instead of single-line `(...)` blobs. Object/soft-object references are stripped to just the asset name. Nested struct fields that match defaults are filtered out. (Tideshift-Labs/Fathom#31)
- [UE5] Blueprint EventGraph audits now partition nodes by exec entry point, rendering one sub-section per Event / CustomEvent / input-action instead of a single flat table. Each section lists only the nodes reachable from that entry plus their data dependencies. Nodes shared across multiple entries are duplicated and annotated `[shared with: ...]`. The same partitioning applies recursively inside collapsed sub-graphs. Function and macro graphs render unchanged. (Tideshift-Labs/Fathom#38)
- [UE5] Enum properties (including those nested in arrays, sets, maps, and struct fields) now resolve to their display-name metadata in audit output instead of internal names like `NewEnumeratorN`.
- Audit schema version bumped to v14

## [0.11.0] - 2026-05-06

### New Features
- [UE5] Audit Blueprints, DataAssets, etc. that live in **project plugins** (`EPluginType::Project`). Plugin audits write to `Saved/Fathom/Audit/v<N>/_Plugins/<PluginName>/...` and are discoverable through the existing `/blueprint-audit` and asset-ref endpoints. Engine/Enterprise/External/Mod plugins remain excluded. (Tideshift-Labs/Fathom#39)

### Fixes & Changes
- [UE5] Skip One-File-Per-Actor packages under `__ExternalActors__/` and `__ExternalObjects__/`. These are level data, not first-class assets, and were producing noisy `Failed to compute hash` warnings during map edits. (Tideshift-Labs/Fathom#40)
- [UE5] Audit files now include a `SourcePath:` header containing the project-relative `.uasset` path (or absolute, if the asset lives outside the project). The Rider-side staleness check prefers this over deriving the path from the package name, which is what makes plugin staleness work. Older audits without the field still load via the legacy `/Game/` derivation. No schema-version bump.
- [UE5] The boot-time sweep also deletes audit files whose package is no longer auditable (e.g. pre-existing `__ExternalActors__` audits, or audits for a project plugin that was disabled).
- [UE5] Skip subsystem startup during cook and unattended runs. UAT packaging launches the editor with `-unattended`, where the existing `IsRunningCommandlet()` guard alone was not enough: `AssetRefSubsystem` produced `HttpListener unable to bind` errors when packaging while another editor was open, and `BlueprintAuditSubsystem` wrote a manifest and registered save hooks during cook. Both now also check `IsRunningCookCommandlet()` and `FApp::IsUnattended()`.

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
