# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.6.0] - 2026-02-23

## [0.5.0] - 2026-02-18

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
