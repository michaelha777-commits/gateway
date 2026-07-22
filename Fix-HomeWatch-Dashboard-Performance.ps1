#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$HomeWatchPath = 'F:\gateway\HomeWatch.ps1',
    [string]$AppJsPath = 'F:\gateway\web\app.js',
    [int]$ClientEventLimit = 5000
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $HomeWatchPath)) { throw "HomeWatch.ps1 not found: $HomeWatchPath" }
if (-not (Test-Path -LiteralPath $AppJsPath)) { throw "app.js not found: $AppJsPath" }
if ($ClientEventLimit -lt 1000 -or $ClientEventLimit -gt 20000) { throw 'ClientEventLimit must be between 1000 and 20000.' }

$psBackup = "$HomeWatchPath.before-dashboard-performance"
$jsBackup = "$AppJsPath.before-dashboard-performance"
Copy-Item -LiteralPath $HomeWatchPath -Destination $psBackup -Force
Copy-Item -LiteralPath $AppJsPath -Destination $jsBackup -Force

try {
    $ps = Get-Content -LiteralPath $HomeWatchPath -Raw

    $oldDashboard = @'
                $added=0; $events=@(Get-Events $hours); $sessions=@(Get-Sessions $events)
                $clients=@($events | Select-Object client,clientName,mac -Unique | Sort-Object clientName); $alerts=@(Get-Alerts $events $sessions)
                $cfg=Read-Config;$audit=@($cfg.alertAuditLog|Sort-Object at -Descending|Select-Object -First 100)
                Send-Json $ctx @{events=$events;sessions=$sessions;alerts=$alerts;alertAuditLog=$audit;clients=$clients;added=$added;eventReadErrors=$script:LastEventReadErrors;generatedAt=[DateTimeOffset]::Now.ToString('o')}; continue
'@

    $newDashboard = @"
                `$added=0; `$events=@(Get-Events `$hours); `$sessions=@(Get-Sessions `$events)
                `$clients=@(`$events | Select-Object client,clientName,mac -Unique | Sort-Object clientName); `$alerts=@(Get-Alerts `$events `$sessions)
                `$cfg=Read-Config;`$audit=@(`$cfg.alertAuditLog|Sort-Object at -Descending|Select-Object -First 100)
                `$summary=[ordered]@{
                    adultSessions=@(`$sessions).Count
                    directVisits=@(`$events|Where-Object evidence -eq 'direct').Count
                    streamingSignals=@(`$events|Where-Object evidence -eq 'stream').Count
                    bypassSignals=@(`$events|Where-Object category -eq 'bypass').Count
                    activeDevices=@(`$events.client|Sort-Object -Unique).Count
                    uniqueDomains=@(`$events.domain|Sort-Object -Unique).Count
                    blockedRequests=@(`$events|Where-Object status -eq 'blocked').Count
                    totalRequests=@(`$events).Count
                }
                `$eventsForClient=@(`$events|Select-Object -First $ClientEventLimit)
                Send-Json `$ctx @{events=`$eventsForClient;sessions=`$sessions;alerts=`$alerts;alertAuditLog=`$audit;clients=`$clients;summary=`$summary;eventsLimited=(`$events.Count -gt `$eventsForClient.Count);totalEventCount=`$events.Count;added=`$added;eventReadErrors=`$script:LastEventReadErrors;generatedAt=[DateTimeOffset]::Now.ToString('o')}; continue
"@

    if (-not $ps.Contains($oldDashboard)) {
        throw 'The expected dashboard handler was not found. No files were changed.'
    }
    $ps = $ps.Replace($oldDashboard, $newDashboard)

    $tokens = $null
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseInput($ps, [ref]$tokens, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) {
        $parseErrors | ForEach-Object { Write-Error $_.Message }
        throw 'PowerShell validation failed.'
    }

    $js = Get-Content -LiteralPath $AppJsPath -Raw
    $oldApi = "async function api(path,options){const r=await fetch(path,options),x=await r.json();if(!r.ok)throw new Error(x.error||'Request failed');return x}"
    $newApi = "async function api(path,options={}){const c=new AbortController(),t=setTimeout(()=>c.abort(),30000);try{const r=await fetch(path,{...options,signal:c.signal}),x=await r.json();if(!r.ok)throw new Error(x.error||'Request failed');return x}catch(e){if(e.name==='AbortError')throw new Error('HomeWatch request timed out');throw e}finally{clearTimeout(t)}}"
    if (-not $js.Contains($oldApi)) { throw 'The expected API helper was not found in app.js.' }
    $js = $js.Replace($oldApi, $newApi)

    $oldCounters = "$('#sessionCount').textContent=x.sessions.length;$('#directCount').textContent=all.filter(e=>e.evidence==='direct').length;$('#streamCount').textContent=all.filter(e=>e.evidence==='stream'||e.hostnameRole==='stream').length;$('#bypassCount').textContent=all.filter(e=>e.category==='bypass').length;`n $('#deviceCount').textContent=new Set(all.map(e=>e.client)).size;$('#domainCount').textContent=new Set(all.map(e=>e.domain)).size;$('#blockedCount').textContent=all.filter(e=>e.status==='blocked').length;$('#requestCount').textContent=all.length;"
    $newCounters = "const summary=!$('#client').value&&model.summary?model.summary:null;$('#sessionCount').textContent=summary?summary.adultSessions:x.sessions.length;$('#directCount').textContent=summary?summary.directVisits:all.filter(e=>e.evidence==='direct').length;$('#streamCount').textContent=summary?summary.streamingSignals:all.filter(e=>e.evidence==='stream'||e.hostnameRole==='stream').length;$('#bypassCount').textContent=summary?summary.bypassSignals:all.filter(e=>e.category==='bypass').length;`n $('#deviceCount').textContent=summary?summary.activeDevices:new Set(all.map(e=>e.client)).size;$('#domainCount').textContent=summary?summary.uniqueDomains:new Set(all.map(e=>e.domain)).size;$('#blockedCount').textContent=summary?summary.blockedRequests:all.filter(e=>e.status==='blocked').length;$('#requestCount').textContent=summary?summary.totalRequests:all.length;"
    if (-not $js.Contains($oldCounters)) { throw 'The expected dashboard counter block was not found in app.js.' }
    $js = $js.Replace($oldCounters, $newCounters)

    $oldSummary = "$('#resultSummary').textContent=`Showing ${Math.min(x.events.length,1000).toLocaleString()} of ${x.events.length.toLocaleString()} matching requests${x.events.length>1000?' (display limited to 1,000)':''}.`;"
    $newSummary = "const sourceTotal=!$('#client').value&&model.totalEventCount?model.totalEventCount:x.events.length;$('#resultSummary').textContent=`Showing ${Math.min(x.events.length,1000).toLocaleString()} of ${sourceTotal.toLocaleString()} requests${model.eventsLimited&&!$('#client').value?' (browser data limited for performance)':''}.`;"
    if (-not $js.Contains($oldSummary)) { throw 'The expected result summary line was not found in app.js.' }
    $js = $js.Replace($oldSummary, $newSummary)

    Set-Content -LiteralPath $HomeWatchPath -Value $ps -Encoding UTF8
    Set-Content -LiteralPath $AppJsPath -Value $js -Encoding UTF8

    Write-Host 'Dashboard performance fix installed.' -ForegroundColor Green
    Write-Host "HomeWatch backup: $psBackup"
    Write-Host "app.js backup: $jsBackup"
    Write-Host 'Restart HomeWatch, then test both localhost and the LAN address.'
}
catch {
    Copy-Item -LiteralPath $psBackup -Destination $HomeWatchPath -Force
    Copy-Item -LiteralPath $jsBackup -Destination $AppJsPath -Force
    throw "Fix failed and backups were restored. $($_.Exception.Message)"
}
