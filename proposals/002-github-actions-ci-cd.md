# Proposal 002: GitHub Actions CI/CD and Tag-Driven Releases

## Problem

CoRider has no CI/CD. Releases to both GitHub Releases and JetBrains Marketplace are entirely manual, requiring the developer to:

1. Remember to update `CHANGELOG.md`
2. Manually set `PluginVersion` in `gradle.properties`
3. Build with Release configuration
4. Run tests
5. Manually run `publishPlugin` with the correct token
6. Manually create a GitHub Release and upload the ZIP

This is error-prone and discourages frequent releases.

## Goal

Push a git tag, everything else happens automatically:

```
git tag v1.2.3 && git push origin main --tags
```

Results in:
- CI builds and tests the plugin (Release config)
- A GitHub Release appears on the Releases tab with the plugin ZIP attached
- The plugin is published to JetBrains Marketplace
- Changelog notes are extracted automatically

## Design

### Two Workflow Files

**`.github/workflows/ci.yml`** runs on every push and PR:
- Builds the plugin (Debug config)
- Runs .NET tests
- Catches regressions before they reach a release

**`.github/workflows/release.yml`** runs when a `v*` tag is pushed:
- Extracts version from the tag (`v1.2.3` -> `1.2.3`)
- Builds in Release configuration with the correct version
- Runs tests
- Extracts the matching changelog section from `CHANGELOG.md`
- Creates a GitHub Release with the ZIP artifact and changelog body
- Publishes to JetBrains Marketplace via `publishPlugin` Gradle task

### Build Environment (Windows Runner)

Both workflows run on `windows-latest` because the build depends on:
- **MSBuild** via Visual Studio Build Tools (pre-installed on GitHub Windows runners)
- **vswhere.exe** (downloaded by `scripts/setup.ps1`) to locate MSBuild
- **JDK 21** (installed via `actions/setup-java` with temurin distribution)
- **.NET SDK 8.x** (installed via `actions/setup-dotnet`)

The `gradle.properties` hardcodes `org.gradle.java.home` to a local dev path. CI overrides this with `-Dorg.gradle.java.home="%JAVA_HOME%"` (system property takes precedence over properties file).

### Sibling Repo Checkout

`prepareSandbox` depends on `packageUePlugin`, which zips `${rootDir}/../CoRider-UnrealEngine`. Both workflows check out `Tideshift-Labs/Fathom-UnrealEngine` (public) as a sibling directory:

```yaml
- uses: actions/checkout@v4
  with:
    path: CoRider

- uses: actions/checkout@v4
  with:
    repository: Tideshift-Labs/Fathom-UnrealEngine
    path: CoRider-UnrealEngine
```

### Version Injection

`gradle.properties` keeps `PluginVersion=9999.0.0` as a dev placeholder. The release workflow overrides it at build time:

```
gradlew.bat :buildPlugin -PPluginVersion=1.2.3 -PBuildConfiguration=Release
```

No source file edits needed for releases. The tag is the single source of truth for the version number.

### Changelog Extraction

The release workflow uses PowerShell to parse `CHANGELOG.md` (Keep a Changelog format). It matches the `## X.Y.Z` heading corresponding to the tag version and extracts everything until the next heading. This becomes the GitHub Release body. Fallback: if no matching heading is found, the first changelog section is used.

### Secrets

| Secret | Purpose | How to obtain |
|--------|---------|---------------|
| `PUBLISH_TOKEN` | JetBrains Marketplace upload | https://plugins.jetbrains.com/author/me/tokens after registering |

`GITHUB_TOKEN` is provided automatically by GitHub Actions.

## Developer Release Workflow (After Setup)

```
1. Update CHANGELOG.md with a new ## X.Y.Z section
2. git add . && git commit -m "Release X.Y.Z"
3. git tag vX.Y.Z
4. git push origin main --tags
5. Monitor GitHub Actions tab for green build
6. Verify: GitHub Releases tab has the ZIP, Marketplace shows the new version
```

## Files to Create

- `.github/workflows/ci.yml`
- `.github/workflows/release.yml`

## Files NOT Modified

- `build.gradle.kts` (already supports `-P` overrides for all properties)
- `gradle.properties` (keeps `9999.0.0` dev placeholder; CI overrides via CLI)

## JetBrains Marketplace Prerequisites

Before the release workflow's `publishPlugin` step will work, the developer must:

1. Register at https://plugins.jetbrains.com
2. Accept the Developer Agreement
3. Create a vendor profile and declare non-trader status
4. Manually upload the first plugin version (one-time, triggers manual review)
5. After approval, generate an API token and add it as `PUBLISH_TOKEN` secret in GitHub repo settings

Subsequent versions published via CI will go through faster automated verification.

## Verification

1. **CI**: Push any commit to a branch. Check the Actions tab for a passing build.
2. **Release**: Push a `v0.1.0` tag. Verify:
   - Actions tab shows a green release workflow run
   - Releases tab shows "CoRider 0.1.0" with the ZIP attached
   - JetBrains Marketplace shows the new version (after initial registration)
