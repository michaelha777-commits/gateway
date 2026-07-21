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

# Remove the v1.3.4 MutationObserver guard, which can trigger an endless
# mutation loop by rewriting the same status text after every mutation.
$app = [regex]::Replace(
    $app,
    '(?s)\s*// HOMEWATCH_ASCII_STATUS_GUARD_V134.*?\}\)\(\);\s*',
    "`r`n"
)

# Force ASCII-only status assignments directly in the normal application flow.
$app = [regex]::Replace(
    $app,
    "if\(!silent\)\$\('#status'\)\.textContent='Refreshing[^']*'",
    "if(!silent)`$('#status').textContent='Refreshing...'"
)
$app = [regex]::Replace(
    $app,
    "out\.textContent='Looking up domain intelligence[^']*'",
    "out.textContent='Looking up domain intelligence...'"
)
$app = [regex]::Replace(
    $app,
    "\$\('#discoveryStatus'\)\.textContent='Scanning reverse DNS, NetBIOS, and ARP \(usually under one minute\)[^']*'",
    "`$('#discoveryStatus').textContent='Scanning reverse DNS, NetBIOS, and ARP (usually under one minute)...'"
)
$app = [regex]::Replace(
    $app,
    "\$\('#settingsStatus'\)\.textContent='Saving[^']*'",
    "`$('#settingsStatus').textContent='Saving...'"
)
$app = [regex]::Replace(
    $app,
    "\$\('#settingsStatus'\)\.textContent='Sending test notification[^']*'",
    "`$('#settingsStatus').textContent='Sending test notification...'"
)

# Replace the initial status text and bump cache/version.
$index = [regex]::Replace(
    $index,
    '<div class="status" id="status">.*?</div>',
    '<div class="status" id="status">Connecting...</div>',
    [Text.RegularExpressions.RegexOptions]::Singleline
)
$index = [regex]::Replace($index,'v1\.3\.[0-9]+','v1.3.5')
$index = [regex]::Replace($index,'/app\.js\?v=[^"'']+','/app.js?v=20260721-35')

Copy-Item $AppPath "$AppPath.v1.3.5-$stamp.bak" -Force
Copy-Item $IndexPath "$IndexPath.v1.3.5-$stamp.bak" -Force
Set-Content $AppPath $app -Encoding UTF8
Set-Content $IndexPath $index -Encoding UTF8

Write-Host 'HomeWatch v1.3.5 UI freeze fix applied.' -ForegroundColor Green
Write-Host 'Restart HomeWatch, close the frozen browser tab, and reopen the dashboard.' -ForegroundColor Cyan
