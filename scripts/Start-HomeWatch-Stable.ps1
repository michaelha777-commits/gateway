#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$SettingsPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'config\server.json')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$homeWatchPath = Join-Path $repoRoot 'HomeWatch.ps1'

if (-not (Test-Path $SettingsPath)) {
    throw "Server settings not found: $SettingsPath"
}
if (-not (Test-Path $homeWatchPath)) {
    throw "HomeWatch.ps1 not found: $homeWatchPath"
}

$settings = Get-Content $SettingsPath -Raw | ConvertFrom-Json
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
    throw "Port $port is already listening (PID: $($owners -join ', ')). Stop that process before starting HomeWatch."
}

Write-Host "Starting HomeWatch on http://127.0.0.1:$port" -ForegroundColor Cyan
& powershell.exe -NoExit -ExecutionPolicy Bypass -File $homeWatchPath -Port $port
