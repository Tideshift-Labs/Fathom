<#
.SYNOPSIS
    Sets up the development environment for CoRider.

.DESCRIPTION
    Downloads required build tools (vswhere.exe, nuget.exe) to the tools/ directory.
    These are gitignored and must be present before building.

.EXAMPLE
    .\setup.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ToolsDir = "$PSScriptRoot\tools"

if (-not (Test-Path $ToolsDir)) {
    New-Item -ItemType Directory -Path $ToolsDir | Out-Null
    Write-Host "Created tools/ directory"
}

# vswhere.exe — locates Visual Studio / MSBuild installations
$VsWherePath = "$ToolsDir\vswhere.exe"
if (-not (Test-Path $VsWherePath)) {
    Write-Host "Downloading vswhere.exe..."
    Invoke-WebRequest -Uri "https://github.com/microsoft/vswhere/releases/latest/download/vswhere.exe" -OutFile $VsWherePath
    Write-Host "  -> $VsWherePath"
} else {
    Write-Host "vswhere.exe already present"
}

# nuget.exe — used by runVisualStudio.ps1 for ReSharper hive setup
$NuGetPath = "$ToolsDir\nuget.exe"
if (-not (Test-Path $NuGetPath)) {
    Write-Host "Downloading nuget.exe..."
    Invoke-WebRequest -Uri "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe" -OutFile $NuGetPath
    Write-Host "  -> $NuGetPath"
} else {
    Write-Host "nuget.exe already present"
}

Write-Host ""
Write-Host "Setup complete. You can now build with:"
Write-Host "  .\gradlew.bat :compileDotNet"
Write-Host "  .\gradlew.bat :buildPlugin"
Write-Host "  .\gradlew.bat :runIde"
