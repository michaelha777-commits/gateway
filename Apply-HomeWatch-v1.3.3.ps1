#requires -Version 5.1
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppPath = Join-Path $Root 'web\app.js'
$IndexPath = Join-Path $Root 'web\index.html'

foreach ($path in @($AppPath,$IndexPath)) {
    if (-not (Test-Path $path)) { throw "Required HomeWatch file not found: $path" }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$app = Get-Content $AppPath -Raw
$index = Get-Content $IndexPath -Raw

# Replace typography and common mojibake with ASCII-safe UI text.
$replacements = [ordered]@{
    'Refreshing…' = 'Refreshing...'
    'Refreshingâ€¦' = 'Refreshing...'
    'Connecting…' = 'Connecting...'
    'Connectingâ€¦' = 'Connecting...'
    'Looking up domain intelligence…' = 'Looking up domain intelligence...'
    'Looking up domain intelligenceâ€¦' = 'Looking up domain intelligence...'
    'Scanning reverse DNS, NetBIOS, and ARP (usually under one minute)…' = 'Scanning reverse DNS, NetBIOS, and ARP (usually under one minute)...'
    'Scanning reverse DNS, NetBIOS, and ARP (usually under one minute)â€¦' = 'Scanning reverse DNS, NetBIOS, and ARP (usually under one minute)...'
    'Saving…' = 'Saving...'
    'Savingâ€¦' = 'Saving...'
    'Sending test notification…' = 'Sending test notification...'
    'Sending test notificationâ€¦' = 'Sending test notification...'
    '–' = '-'
    'â€“' = '-'
    '→' = '->'
    'â†’' = '->'
    '·' = ' - '
    'Â·' = ' - '
    '—' = '-'
    'â€”' = '-'
}

foreach ($entry in $replacements.GetEnumerator()) {
    $app = $app.Replace([string]$entry.Key,[string]$entry.Value)
    $index = $index.Replace([string]$entry.Key,[string]$entry.Value)
}

# Bump the visible version and force browsers to load the corrected script.
$index = $index.Replace('v1.3.2','v1.3.3')
$index = [regex]::Replace($index,'/app\.js\?v=[^"'']+','/app.js?v=20260721-33')

Copy-Item $AppPath "$AppPath.v1.3.3-$stamp.bak" -Force
Copy-Item $IndexPath "$IndexPath.v1.3.3-$stamp.bak" -Force

# PowerShell 5.1 writes UTF-8 with BOM; the HTTP response declares UTF-8.
Set-Content $AppPath $app -Encoding UTF8
Set-Content $IndexPath $index -Encoding UTF8

Write-Host 'HomeWatch v1.3.3 encoding cleanup applied.' -ForegroundColor Green
Write-Host 'Restart HomeWatch, then refresh the browser. The UI now uses ASCII-safe status text.' -ForegroundColor Cyan
