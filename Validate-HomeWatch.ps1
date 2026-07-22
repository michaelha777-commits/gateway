#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$Message) {
    $failures.Add($Message)
    Write-Host "[FAIL] $Message" -ForegroundColor Red
}

function Add-Pass([string]$Message) {
    Write-Host "[PASS] $Message" -ForegroundColor Green
}

$requiredFiles = @(
    'HomeWatch.ps1',
    'web\app.js',
    'web\index.html',
    'web\styles.css'
)

foreach ($relativePath in $requiredFiles) {
    $path = Join-Path $Root $relativePath
    if (Test-Path $path) { Add-Pass "$relativePath exists" }
    else { Add-Failure "$relativePath is missing" }
}

$corePath = Join-Path $Root 'HomeWatch.ps1'
if (Test-Path $corePath) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($corePath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -eq 0) {
        Add-Pass 'HomeWatch.ps1 parses successfully in Windows PowerShell'
    } else {
        foreach ($errorItem in $parseErrors) {
            Add-Failure ("HomeWatch.ps1 parser error at line {0}, column {1}: {2}" -f $errorItem.Extent.StartLineNumber,$errorItem.Extent.StartColumnNumber,$errorItem.Message)
        }
    }
}

$jsPath = Join-Path $Root 'web\app.js'
if (Test-Path $jsPath) {
    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($node) {
        $output = & $node.Source --check $jsPath 2>&1
        if ($LASTEXITCODE -eq 0) { Add-Pass 'web/app.js passes node --check' }
        else { Add-Failure ("web/app.js syntax check failed: " + ($output -join ' ')) }
    } else {
        Write-Host '[WARN] Node.js is not installed; JavaScript syntax check skipped.' -ForegroundColor Yellow
    }
}

$textFiles = @('HomeWatch.ps1','web\app.js','web\index.html','web\styles.css')
$badSequences = @('â€¦','â€“','â€”','â†’','Â·','ï»¿')
foreach ($relativePath in $textFiles) {
    $path = Join-Path $Root $relativePath
    if (-not (Test-Path $path)) { continue }
    $text = Get-Content $path -Raw
    foreach ($sequence in $badSequences) {
        if ($text.Contains($sequence)) { Add-Failure "$relativePath contains corrupted encoding sequence: $sequence" }
    }
}
if ($failures.Count -eq 0) { Add-Pass 'No known mojibake sequences found' }

$indexPath = Join-Path $Root 'web\index.html'
if (Test-Path $indexPath) {
    $index = Get-Content $indexPath -Raw
    if ($index -match '<meta\s+charset=["'']?utf-8["'']?\s*/?>') { Add-Pass 'index.html declares UTF-8' }
    else { Add-Failure 'index.html does not declare UTF-8 with a meta charset tag' }

    foreach ($asset in @('/app.js','/styles.css')) {
        if ($index.Contains($asset)) { Add-Pass "index.html references $asset" }
        else { Add-Failure "index.html does not reference $asset" }
    }
}

$installerFiles = Get-ChildItem $Root -Filter 'Apply-HomeWatch-v*.ps1' -File -ErrorAction SilentlyContinue
if ($installerFiles.Count -gt 0) {
    Write-Host ("[WARN] {0} legacy patch installer(s) remain in the repository. Do not use them for new changes." -f $installerFiles.Count) -ForegroundColor Yellow
}

if ($failures.Count -gt 0) {
    Write-Host "`nHomeWatch validation failed with $($failures.Count) issue(s)." -ForegroundColor Red
    exit 1
}

Write-Host "`nHomeWatch validation passed." -ForegroundColor Green
exit 0
