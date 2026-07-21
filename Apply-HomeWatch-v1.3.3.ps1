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

# Use an array of pairs instead of a hashtable. This avoids duplicate-key parser
# failures when corrupted Unicode text is decoded inconsistently by PowerShell 5.1.
$ellipsis = [string][char]0x2026
$enDash = [string][char]0x2013
$emDash = [string][char]0x2014
$arrow = [string][char]0x2192
$middleDot = [string][char]0x00B7

$replacements = @(
    @('Refreshing' + $ellipsis, 'Refreshing...'),
    @('Connecting' + $ellipsis, 'Connecting...'),
    @('Looking up domain intelligence' + $ellipsis, 'Looking up domain intelligence...'),
    @('Scanning reverse DNS, NetBIOS, and ARP (usually under one minute)' + $ellipsis, 'Scanning reverse DNS, NetBIOS, and ARP (usually under one minute)...'),
    @('Saving' + $ellipsis, 'Saving...'),
    @('Sending test notification' + $ellipsis, 'Sending test notification...'),
    @($enDash, '-'),
    @($emDash, '-'),
    @($arrow, '->'),
    @($middleDot, ' - ')
)

foreach ($pair in $replacements) {
    $app = $app.Replace([string]$pair[0],[string]$pair[1])
    $index = $index.Replace([string]$pair[0],[string]$pair[1])
}

# Repair common mojibake without embedding corrupted characters in this script.
# Convert UTF-8 bytes that were misread as Windows-1252 back into Unicode where possible.
function Repair-Mojibake([string]$Text) {
    try {
        $win1252 = [Text.Encoding]::GetEncoding(1252)
        $utf8 = New-Object Text.UTF8Encoding($false,$true)
        $bytes = $win1252.GetBytes($Text)
        $candidate = $utf8.GetString($bytes)
        if ($candidate -match '[\u2026\u2013\u2014\u2192\u00B7]') { return $candidate }
    } catch {}
    return $Text
}

$app = Repair-Mojibake $app
$index = Repair-Mojibake $index
foreach ($pair in $replacements) {
    $app = $app.Replace([string]$pair[0],[string]$pair[1])
    $index = $index.Replace([string]$pair[0],[string]$pair[1])
}

$index = $index.Replace('v1.3.2','v1.3.3')
$index = [regex]::Replace($index,'/app\.js\?v=[^"'']+','/app.js?v=20260721-34')

Copy-Item $AppPath "$AppPath.v1.3.3-$stamp.bak" -Force
Copy-Item $IndexPath "$IndexPath.v1.3.3-$stamp.bak" -Force

Set-Content $AppPath $app -Encoding UTF8
Set-Content $IndexPath $index -Encoding UTF8

Write-Host 'HomeWatch v1.3.3 encoding cleanup applied.' -ForegroundColor Green
Write-Host 'Restart HomeWatch, then use Ctrl+F5 in the browser.' -ForegroundColor Cyan
