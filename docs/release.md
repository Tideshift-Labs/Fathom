# Releasing Fathom

This document covers versioning, the release workflow, and how Fathom and Fathom-UnrealEngine versions stay in sync.

## Versioning

Fathom uses [Semantic Versioning](https://semver.org/) (`MAJOR.MINOR.PATCH`).

The version is stored in two places:

| File | Field | Example |
|------|-------|---------|
| `gradle.properties` | `PluginVersion` | `0.1.0` |
| `../CoRider-UnrealEngine/FathomUELink.uplugin` | `VersionName` | `0.1.0` |

These must stay in sync. The Rider plugin reads the bundled `.uplugin` `VersionName` at runtime to detect whether the user's installed UE companion plugin is outdated (see `CompanionPluginService.Detect()`). A mismatch causes the plugin to prompt the user to update.

## Bumping a version

Use the helper script from the repo root:

```powershell
.\scripts\bump-version.ps1 -Version 0.2.0
```

This script:

1. Validates the version matches `MAJOR.MINOR.PATCH`
2. Updates `PluginVersion` in `gradle.properties`
3. Updates `CHANGELOG.md`: renames `## [Unreleased]` to `## [0.2.0] - YYYY-MM-DD` and adds a fresh `## [Unreleased]` section above it
4. Updates `VersionName` in `../CoRider-UnrealEngine/FathomUELink.uplugin`
5. Commits and tags `v0.2.0` in both repos

Before running the script, move your changelog entries from `## [Unreleased]` into bullet points under that heading so they get stamped with the version.

## Publishing a release

After bumping, push both repos with their tags. **Fathom-UnrealEngine must be pushed first** because the Fathom release workflow checks out Fathom-UnrealEngine by the same `v*` tag. If that tag does not exist on GitHub yet, the workflow will fail.

```powershell
git -C ..\CoRider-UnrealEngine push --follow-tags               # 1. UE plugin first
git push --follow-tags                                          # 2. Fathom second (triggers workflow)
```

Pushing the `v*` tag triggers the GitHub Actions release workflow (`.github/workflows/release.yml`), which:

1. Checks out Fathom
2. Checks out Fathom-UnrealEngine at the matching `v*` tag (as a sibling directory)
3. Sets up JDK 21
4. Runs `gradlew.bat buildPlugin` with the tag version and `Release` configuration
5. Extracts release notes from `CHANGELOG.md` for the matching version
6. Creates a GitHub Release with the built `.zip` attached

## Repository layout

Both repos must be sibling directories. The Gradle build (`build.gradle.kts`) references `../CoRider-UnrealEngine` directly to bundle the UE plugin source into the Rider plugin zip.

```
CoRider-All/
  CoRider/                    # Rider plugin (this repo)
  CoRider-UnrealEngine/       # UE companion plugin
```

## CHANGELOG format

The changelog follows [Keep a Changelog](https://keepachangelog.com/). Always add entries under `## [Unreleased]` during development:

```markdown
## [Unreleased]
- Added new inspection for Blueprint naming conventions

## [0.1.0]
- Initial version
```

Categories you can use under a version heading: `Added`, `Changed`, `Fixed`, `Removed`.

## Quick reference

| Task | Command |
|------|---------|
| Bump version | `.\scripts\bump-version.ps1 -Version X.Y.Z` |
| Push release | UE repo first, then Fathom (see above) |
| Build locally | `.\gradlew.bat buildPlugin -PBuildConfiguration=Release` |
| Check current version | `Select-String 'PluginVersion' .\gradle.properties` |
