#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$SettingsPath
)

$ErrorActionPreference = 'Stop'

$scriptDirectory = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    throw 'Could not determine the launcher script directory.'
}

$repoRoot = Split-Path -Parent $scriptDirectory
if ([string]::IsNullOrWhiteSpace($SettingsPath)) {
    $SettingsPath = Join-Path $repoRoot 'config\server.json'
}
$homeWatchPath = Join-Path $repoRoot 'HomeWatch.ps1'

if (-not (Test-Path -LiteralPath $SettingsPath)) {
    throw "Server settings not found: $SettingsPath"
}
if (-not (Test-Path -LiteralPath $homeWatchPath)) {
    throw "HomeWatch.ps1 not found: $homeWatchPath"
}

$settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
$port = [int]$settings.port
if ($port -lt 1024 -or $port -gt 65535) {
    throw "Invalid configured port: $port"
}
if ([string]$settings.bindMode -ne 'localhost') {
    throw "This stabilization launcher currently supports bindMode 'localhost' only."
}

$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $homeWatchPath,
    [ref]$tokens,
    [ref]$parseErrors
) | Out-Null
if ($parseErrors.Count -gt 0) {
    $parseErrors | ForEach-Object { Write-Error $_.Message }
    throw 'HomeWatch.ps1 failed parser validation. Startup was blocked.'
}

$existing = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
if ($existing) {
    $owners = @($existing | Select-Object -ExpandProperty OwningProcess -Unique)
    throw "Port $port is already listening (PID: $($owners -join ', ')). Stop the existing HomeWatch window before starting the stabilization launcher."
}

Write-Host "Starting HomeWatch on http://127.0.0.1:$port" -ForegroundColor Cyan
& powershell.exe -NoExit -ExecutionPolicy Bypass -File $homeWatchPath -Port $port
