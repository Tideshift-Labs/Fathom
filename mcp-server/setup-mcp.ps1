# CoRider MCP Setup Script
# This script automates the registration of the CoRider MCP server for Gemini CLI and Claude Desktop.

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$IndexPath = Join-Path $ScriptDir "index.js"
$NodePath = (Get-Command node).Source

if (-not $NodePath) {
    Write-Error "Node.js not found. Please install Node.js before running this script."
    exit 1
}

Write-Host "--- CoRider MCP Setup ---" -ForegroundColor Cyan

# 1. Gemini CLI Integration
Write-Host "`n[1/2] Registering with Gemini CLI..." -ForegroundColor Yellow
try {
    # We use & to execute the command. We wrap the path in quotes for safety.
    & gemini mcp add corider "$NodePath" "$IndexPath"
    Write-Host "Successfully registered with Gemini CLI!" -ForegroundColor Green
} catch {
    Write-Host "Gemini CLI not found or failed to register. Skipping..." -ForegroundColor Gray
}

# 2. Claude Desktop Integration
Write-Host "`n[2/2] Registering with Claude Desktop..." -ForegroundColor Yellow
$ClaudeConfigPath = Join-Path $env:APPDATA "Claude\claude_desktop_config.json"

if (Test-Path $ClaudeConfigPath) {
    $Config = Get-Content $ClaudeConfigPath | ConvertFrom-Json
    
    if (-not $Config.mcpServers) {
        $Config | Add-Member -MemberType NoteProperty -Name "mcpServers" -Value @{}
    }

    $Config.mcpServers | Add-Member -MemberType NoteProperty -Name "corider" -Value @{
        command = $NodePath
        args = @($IndexPath)
    } -Force

    $Config | ConvertTo-Json -Depth 10 | Set-Content $ClaudeConfigPath
    Write-Host "Successfully registered with Claude Desktop!" -ForegroundColor Green
} else {
    Write-Host "Claude Desktop config not found at $ClaudeConfigPath. Skipping..." -ForegroundColor Gray
}

Write-Host "`nSetup complete! Restart your AI client to see the new CoRider tools." -ForegroundColor Cyan
