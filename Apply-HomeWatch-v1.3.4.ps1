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

# Repair the status assignment without depending on the current corrupted characters.
$app = [regex]::Replace(
    $app,
    "if\(!silent\)\$\('#status'\)\.textContent='Refreshing[^']*'",
    "if(!silent)`$('#status').textContent='Refreshing...'"
)

# Add a small runtime guard that normalizes any stale/corrupted status text.
if ($app -notmatch 'HOMEWATCH_ASCII_STATUS_GUARD_V134') {
    $guard = @'

// HOMEWATCH_ASCII_STATUS_GUARD_V134
(function(){
  const status = document.getElementById('status');
  if (!status) return;
  const normalize = () => {
    const text = String(status.textContent || '');
    if (/^Refreshing/i.test(text)) status.textContent = 'Refreshing...';
    else if (/^Connecting/i.test(text)) status.textContent = 'Connecting...';
  };
  normalize();
  new MutationObserver(normalize).observe(status,{childList:true,characterData:true,subtree:true});
})();
'@
    $app += $guard
}

# Replace the initial status element regardless of its current contents.
$index = [regex]::Replace(
    $index,
    '<div class="status" id="status">.*?</div>',
    '<div class="status" id="status">Connecting...</div>',
    [Text.RegularExpressions.RegexOptions]::Singleline
)

$index = [regex]::Replace($index,'v1\.3\.[0-9]+','v1.3.4')
$index = [regex]::Replace($index,'/app\.js\?v=[^"'']+','/app.js?v=20260721-34')

Copy-Item $AppPath "$AppPath.v1.3.4-$stamp.bak" -Force
Copy-Item $IndexPath "$IndexPath.v1.3.4-$stamp.bak" -Force
Set-Content $AppPath $app -Encoding UTF8
Set-Content $IndexPath $index -Encoding UTF8

Write-Host 'HomeWatch v1.3.4 status-text repair applied.' -ForegroundColor Green
Write-Host 'Restart HomeWatch and press Ctrl+F5 in the browser.' -ForegroundColor Cyan
