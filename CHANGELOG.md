# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
