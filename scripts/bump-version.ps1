Param(
    [Parameter(Mandatory=$true)]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path $PSScriptRoot -Parent

# Validate semver pattern
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "Version '$Version' does not match semver pattern (e.g. 0.2.0)"
    exit 1
}

# Update gradle.properties
$gradleProps = Join-Path $RepoRoot "gradle.properties"
$content = Get-Content $gradleProps -Raw
if ($content -notmatch 'PluginVersion=\d+\.\d+\.\d+') {
    Write-Error "Could not find PluginVersion in $gradleProps"
    exit 1
}
$content = $content -replace 'PluginVersion=\d+\.\d+\.\d+', "PluginVersion=$Version"
Set-Content $gradleProps $content -NoNewline
Write-Host "Updated gradle.properties to PluginVersion=$Version"

# Update CHANGELOG.md
$changelogPath = Join-Path $RepoRoot "CHANGELOG.md"
$changelog = Get-Content $changelogPath -Raw
if ($changelog -notmatch '## \[Unreleased\]') {
    Write-Error "Could not find '## [Unreleased]' section in CHANGELOG.md"
    exit 1
}
$today = Get-Date -Format "yyyy-MM-dd"
$changelog = $changelog -replace '## \[Unreleased\]', "## [Unreleased]`n`n## [$Version] - $today"
Set-Content $changelogPath $changelog -NoNewline
Write-Host "Updated CHANGELOG.md with version $Version"

# Update Fathom-UnrealEngine .uplugin VersionName (sibling repo)
$upluginPath = Join-Path $RepoRoot "..\CoRider-UnrealEngine\FathomUELink.uplugin"
if (Test-Path $upluginPath) {
    $uplugin = Get-Content $upluginPath -Raw
    $uplugin = $uplugin -replace '"VersionName"\s*:\s*"[^"]*"', "`"VersionName`": `"$Version`""
    Set-Content $upluginPath $uplugin -NoNewline
    Write-Host "Updated FathomUELink.uplugin VersionName to $Version"
} else {
    Write-Warning "FathomUELink.uplugin not found at $upluginPath - skipping"
}

# Git commit and tag in Fathom repo
Push-Location $RepoRoot
try {
    git add gradle.properties CHANGELOG.md
    git commit -m "Bump version to $Version"
    git tag "v$Version"
    Write-Host "Created commit and tag v$Version in Fathom"
} finally {
    Pop-Location
}

# Git commit in CoRider-UnrealEngine repo (sibling, separate repo)
if (Test-Path $upluginPath) {
    $ueRoot = Split-Path $upluginPath -Parent
    Push-Location $ueRoot
    try {
        git add FathomUELink.uplugin
        git commit -m "Bump version to $Version"
        git tag "v$Version"
        Write-Host "Created commit and tag v$Version in Fathom-UnrealEngine"
    } finally {
        Pop-Location
    }
}

Write-Host ""
Write-Host "To publish, push Fathom-UnrealEngine FIRST (its tag must exist on GitHub"
Write-Host "before the Fathom workflow tries to check it out):"
if (Test-Path $upluginPath) {
    Write-Host "  git -C '$(Split-Path $upluginPath -Parent)' push --follow-tags"
}
Write-Host "  git -C '$RepoRoot' push --follow-tags"
