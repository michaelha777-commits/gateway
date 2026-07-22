#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateRange(1024,65535)]
    [int]$Port = 8900,
    [switch]$SkipValidation
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$CorePath = Join-Path $Root 'HomeWatch.ps1'
$ValidationPath = Join-Path $Root 'Validate-HomeWatch.ps1'

if (-not (Test-Path $CorePath)) {
    throw "HomeWatch.ps1 was not found in $Root"
}

if (-not $SkipValidation) {
    if (-not (Test-Path $ValidationPath)) {
        throw 'Validate-HomeWatch.ps1 is missing. Refusing to start without the validation gate.'
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ValidationPath
    if ($LASTEXITCODE -ne 0) {
        throw 'HomeWatch validation failed. The server was not started.'
    }
}

$existing = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($existing) {
    $owner = try { (Get-Process -Id $existing.OwningProcess -ErrorAction Stop).ProcessName } catch { "PID $($existing.OwningProcess)" }
    throw "Port $Port is already in use by $owner. Stop that process or choose another port."
}

Write-Host "Starting HomeWatch on http://127.0.0.1:$Port" -ForegroundColor Cyan
Write-Host 'Keep this window open while HomeWatch is running. Press Ctrl+C to stop it.' -ForegroundColor Yellow

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $CorePath -Port $Port
if ($LASTEXITCODE -ne 0) {
    throw "HomeWatch exited with code $LASTEXITCODE."
}
