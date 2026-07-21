#requires -Version 5.1
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$CorePath = Join-Path $Root 'HomeWatch.ps1'
$AppPath = Join-Path $Root 'web\app.js'
$IndexPath = Join-Path $Root 'web\index.html'

foreach ($path in @($CorePath,$AppPath,$IndexPath)) {
    if (-not (Test-Path $path)) { throw "Required HomeWatch file not found: $path" }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$core = Get-Content $CorePath -Raw
$app = Get-Content $AppPath -Raw
$index = Get-Content $IndexPath -Raw

# All regex patterns use literal here-strings so JavaScript quotes and PowerShell variables
# are never interpreted by the installer parser.
$aliasPattern = @'
\$\('#aliases'\)\.innerHTML=model\.clients\.map\(c=>`<label class="alias">.*?</label>`\)\.join\(''\);
'@.Trim()

$aliasReplacement = @'
$('#aliases').innerHTML=model.clients.map(c=>`<label class="alias"><span>${esc(c.client)}</span><span class="mac">${esc(c.mac||'MAC not found')} <small>${c.mac?'Detected or saved':'Enter manually below'}</small></span><input class="deviceNameInput" data-ip="${esc(c.client)}" value="${esc(c.clientName===c.client?'':c.clientName)}" placeholder="Device name"><input class="manualMacInput" data-mac-ip="${esc(c.client)}" value="${esc(c.mac||'')}" placeholder="MAC address, e.g. AA:BB:CC:DD:EE:FF" inputmode="text" autocomplete="off"></label>`).join('');
'@.Trim()

if ($app -notmatch 'manualMacInput') {
    $updatedApp = [regex]::Replace($app,$aliasPattern,$aliasReplacement,[Text.RegularExpressions.RegexOptions]::Singleline)
    if ($updatedApp -eq $app) { throw 'Could not locate the device-row renderer. No files were overwritten.' }
    $app = $updatedApp
}

$savePattern = @'
\$\('#saveAliases'\)\.onclick=async\(\)=>\{.*?\};
'@.Trim()

$saveReplacement = @'
let savingAliases=false;
function normalizeMac(value){const raw=String(value||'').trim().replace(/[^0-9a-f]/gi,'').toUpperCase();if(!raw)return '';if(!/^[0-9A-F]{12}$/.test(raw))throw new Error('MAC addresses must contain exactly 12 hexadecimal characters.');return raw.match(/.{2}/g).join(':')}
$('#saveAliases').onclick=async()=>{if(savingAliases)return;const button=$('#saveAliases'),aliases={},manualMacs={};try{document.querySelectorAll('.deviceNameInput[data-ip]').forEach(i=>{const name=i.value.trim();if(name)aliases[i.dataset.ip]=name});document.querySelectorAll('.manualMacInput[data-mac-ip]').forEach(i=>{const mac=normalizeMac(i.value);if(mac){manualMacs[i.dataset.macIp]=mac;i.value=mac}});savingAliases=true;button.disabled=true;button.textContent='Saving...';$('#discoveryStatus').textContent='Saving device names and MAC addresses...';await api('/api/aliases',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({aliases,manualMacs})});model.clients.forEach(c=>{if(Object.prototype.hasOwnProperty.call(aliases,c.client))c.clientName=aliases[c.client];if(Object.prototype.hasOwnProperty.call(manualMacs,c.client))c.mac=manualMacs[c.client]});$('#discoveryStatus').textContent='Saved. The dashboard was not reloaded.';button.textContent='Saved';setTimeout(()=>{button.textContent='Save names';button.disabled=false;savingAliases=false},900)}catch(e){$('#discoveryStatus').textContent='Could not save: '+e.message;button.textContent='Save names';button.disabled=false;savingAliases=false}};
'@.Trim()

if ($app -notmatch 'savingAliases=false') {
    $updatedApp = [regex]::Replace($app,$savePattern,$saveReplacement,[Text.RegularExpressions.RegexOptions]::Singleline)
    if ($updatedApp -eq $app) { throw 'Could not locate the Save names handler. No files were overwritten.' }
    $app = $updatedApp
}

$endpointPattern = @'
if \(\$path -eq '/api/aliases' -and \$ctx\.Request\.HttpMethod -eq 'POST'\) \{.*?Send-Json \$ctx @\{ok=\$true\}; continue\s*\}
'@.Trim()

$endpointReplacement = @'
if ($path -eq '/api/aliases' -and $ctx.Request.HttpMethod -eq 'POST') {
                $body=Read-Body $ctx.Request; $cfg=Read-Config
                $map=[ordered]@{}
                if ($body.aliases) { $body.aliases.psobject.Properties | ForEach-Object { if (-not [string]::IsNullOrWhiteSpace([string]$_.Value)) { $map[$_.Name]=([string]$_.Value).Trim() } } }
                $cfg.aliases=[pscustomobject]$map

                $savedMacs=[ordered]@{}
                if ($cfg.discoveredMacs) { $cfg.discoveredMacs.psobject.Properties | ForEach-Object { $savedMacs[$_.Name]=([string]$_.Value).ToUpperInvariant() } }
                if ($body.manualMacs) {
                    $body.manualMacs.psobject.Properties | ForEach-Object {
                        $ip=[string]$_.Name
                        $raw=([string]$_.Value).ToUpperInvariant() -replace '[^0-9A-F]',''
                        if ($raw -notmatch '^[0-9A-F]{12}$') { throw "Invalid MAC address for $ip." }
                        $normalized=($raw -replace '(.{2})(?!$)','$1:')
                        $savedMacs[$ip]=$normalized
                    }
                }
                $cfg.discoveredMacs=[pscustomobject]$savedMacs
                Save-Config $cfg
                $script:EventViewCache=@{}
                Send-Json $ctx @{ok=$true;aliases=$cfg.aliases;discoveredMacs=$cfg.discoveredMacs}; continue
            }
'@.Trim()

if ($core -notmatch 'manualMacs') {
    $updatedCore = [regex]::Replace($core,$endpointPattern,$endpointReplacement,[Text.RegularExpressions.RegexOptions]::Singleline)
    if ($updatedCore -eq $core) { throw 'Could not locate the aliases API endpoint. No files were overwritten.' }
    $core = $updatedCore
}

if ($index -notmatch 'manualMacInput') {
    $index = $index.Replace('</style></head>', '.alias .manualMacInput{margin-top:6px;font-family:Consolas,monospace}.alias .mac small{font-family:inherit;color:#7b8d94;margin-left:5px}</style></head>')
}
$index = [regex]::Replace($index,'v1\.3\.[0-9]+','v1.3.6')
$index = [regex]::Replace($index,'/app\.js\?v=[^"'']+','/app.js?v=20260721-36b')

# Validate the generated JavaScript structure before any file is overwritten.
if ($app -notmatch 'manualMacInput' -or $app -notmatch 'normalizeMac' -or $app -notmatch 'savingAliases') {
    throw 'Generated device editor failed validation. No files were overwritten.'
}
if ($core -notmatch 'manualMacs' -or $core -notmatch 'Invalid MAC address') {
    throw 'Generated aliases endpoint failed validation. No files were overwritten.'
}

Copy-Item $CorePath "$CorePath.v1.3.6-$stamp.bak" -Force
Copy-Item $AppPath "$AppPath.v1.3.6-$stamp.bak" -Force
Copy-Item $IndexPath "$IndexPath.v1.3.6-$stamp.bak" -Force
Set-Content $CorePath $core -Encoding UTF8
Set-Content $AppPath $app -Encoding UTF8
Set-Content $IndexPath $index -Encoding UTF8

Write-Host 'HomeWatch v1.3.6 device editor applied.' -ForegroundColor Green
Write-Host 'Device names now save without a full dashboard reload, and missing MAC addresses can be entered manually.' -ForegroundColor Cyan
Write-Host 'Restart HomeWatch, open Devices, and press Ctrl+F5 once.' -ForegroundColor Cyan
