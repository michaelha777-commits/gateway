#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failed = $false

function Write-CheckResult {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    if ($Passed) {
        Write-Host "[PASS] $Name" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] $Name $Detail" -ForegroundColor Red
        $script:failed = $true
    }
}

$homeWatchPath = Join-Path $root 'HomeWatch.ps1'
$appJsPath = Join-Path $root 'web\app.js'
$indexPath = Join-Path $root 'web\index.html'
$stylesPath = Join-Path $root 'web\styles.css'

foreach ($path in @($homeWatchPath, $appJsPath, $indexPath, $stylesPath)) {
    Write-CheckResult "Exists: $([IO.Path]::GetFileName($path))" (Test-Path $path)
}

if (Test-Path $homeWatchPath) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $homeWatchPath,
        [ref]$tokens,
        [ref]$errors
    ) | Out-Null

    if ($errors.Count -eq 0) {
        Write-CheckResult 'PowerShell parser' $true
    } else {
        foreach ($error in $errors) {
            Write-Host ("  Line {0}: {1}" -f $error.Extent.StartLineNumber, $error.Message) -ForegroundColor Red
        }
        Write-CheckResult 'PowerShell parser' $false
    }
}

if (Test-Path $appJsPath) {
    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($node) {
        & $node.Source --check $appJsPath
        Write-CheckResult 'JavaScript syntax' ($LASTEXITCODE -eq 0)
    } else {
        Write-Host '[SKIP] JavaScript syntax: Node.js is not installed.' -ForegroundColor Yellow
    }
}

if (Test-Path $homeWatchPath) {
    $source = Get-Content $homeWatchPath -Raw
    Write-CheckResult 'No generated patch execution in startup' ($source -notmatch 'Apply-HomeWatch|before-v1\.|-replace\s+.*HomeWatch')
    Write-CheckResult 'HttpListener present' ($source -match 'Net\.HttpListener')
    Write-CheckResult 'Dashboard endpoint present' ($source -match "'/api/dashboard'")
}

if ($failed) {
    throw 'HomeWatch source validation failed.'
}

Write-Host 'HomeWatch source validation completed successfully.' -ForegroundColor Cyan
