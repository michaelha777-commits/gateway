#requires -Version 5.1
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$CorePath = Join-Path $Root 'HomeWatch.ps1'
$AppPath = Join-Path $Root 'web\app.js'
$IndexPath = Join-Path $Root 'web\index.html'

foreach ($path in @($CorePath, $AppPath, $IndexPath)) {
    if (-not (Test-Path $path)) { throw "Required HomeWatch file not found: $path" }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
Copy-Item $CorePath "$CorePath.v1.3.2-$stamp.bak" -Force
Copy-Item $AppPath "$AppPath.v1.3.2-$stamp.bak" -Force
Copy-Item $IndexPath "$IndexPath.v1.3.2-$stamp.bak" -Force

$core = (Get-Content $CorePath -Raw) -replace "`r`n", "`n"
$app = (Get-Content $AppPath -Raw) -replace "`r`n", "`n"
$index = (Get-Content $IndexPath -Raw) -replace "`r`n", "`n"

function Replace-Required([string]$Text, [string]$Old, [string]$New, [string]$Description) {
    if (-not $Text.Contains($Old)) { throw "Could not locate $Description. No files were overwritten." }
    return $Text.Replace($Old, $New)
}

if ($core -notmatch 'HOMEWATCH_ONLINE_ADULT_INTELLIGENCE_V132') {
    $oldCdns = "    'storageimagedisplay.com','sx.cdn.live'"
    $newCdns = "    'storageimagedisplay.com','sx.cdn.live','xxxjmp.com','pvvstream.pro','pvvstream.com'"
    $core = Replace-Required $core $oldCdns $newCdns 'the adult CDN list'

    $onlineCode = @'
# HOMEWATCH_ONLINE_ADULT_INTELLIGENCE_V132
$OnlineAdultDomainsPath = Join-Path $DataDir 'adult-domains-online.txt'
$OnlineAdultMaintenancePath = Join-Path $DataDir 'adult-domains-online-maintenance.json'
$OnlineAdultFeedUrl = 'https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/porn-only/hosts'
$OnlineAdultDomains = @{}

function Import-OnlineAdultDomains {
    $script:OnlineAdultDomains = @{}
    if (-not (Test-Path $OnlineAdultDomainsPath)) { return 0 }
    foreach ($line in Get-Content $OnlineAdultDomainsPath) {
        $domain = ([string]$line).Trim().TrimEnd('.').ToLowerInvariant()
        if ($domain -match '^[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])$' -and $domain.Contains('.')) {
            $script:OnlineAdultDomains[$domain] = $true
        }
    }
    return $script:OnlineAdultDomains.Count
}

function Test-OnlineAdultDomain([string]$Domain) {
    if (-not $script:OnlineAdultDomains -or $script:OnlineAdultDomains.Count -eq 0) { return $false }
    $d = $Domain.Trim().TrimEnd('.').ToLowerInvariant()
    if ($script:OnlineAdultDomains.ContainsKey($d)) { return $true }
    $labels = @($d -split '\.')
    for ($i = 1; $i -lt ($labels.Count - 1); $i++) {
        $suffix = ($labels[$i..($labels.Count - 1)] -join '.')
        if ($script:OnlineAdultDomains.ContainsKey($suffix)) { return $true }
    }
    return $false
}

function Update-OnlineAdultDomains([bool]$Force = $false) {
    $last = [DateTimeOffset]::MinValue
    if (Test-Path $OnlineAdultMaintenancePath) {
        try { $last = [DateTimeOffset]::Parse((Get-Content $OnlineAdultMaintenancePath -Raw | ConvertFrom-Json).updatedAt) } catch {}
    }
    if (-not $Force -and (Test-Path $OnlineAdultDomainsPath) -and ([DateTimeOffset]::Now - $last).TotalHours -lt 24) {
        [void](Import-OnlineAdultDomains)
        return $false
    }
    try {
        $raw = (Invoke-WebRequest -UseBasicParsing -Uri $OnlineAdultFeedUrl -TimeoutSec 30).Content
        $set = @{}
        foreach ($line in ($raw -split "`r?`n")) {
            $clean = ($line -replace '#.*$', '').Trim()
            if (-not $clean) { continue }
            $parts = @($clean -split '\s+' | Where-Object { $_ })
            if ($parts.Count -lt 2) { continue }
            foreach ($candidate in $parts[1..($parts.Count - 1)]) {
                $domain = ([string]$candidate).Trim().TrimEnd('.').ToLowerInvariant()
                if ($domain -in @('localhost','localhost.localdomain','local','broadcasthost','ip6-localhost','ip6-loopback')) { continue }
                if ($domain -match '^[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])$' -and $domain.Contains('.')) { $set[$domain] = $true }
            }
        }
        if ($set.Count -lt 1000) { throw "Online adult-domain feed returned only $($set.Count) valid domains." }
        @($set.Keys | Sort-Object) | Set-Content $OnlineAdultDomainsPath -Encoding UTF8
        @{ updatedAt = [DateTimeOffset]::Now.ToString('o'); source = $OnlineAdultFeedUrl; domainCount = $set.Count } |
            ConvertTo-Json | Set-Content $OnlineAdultMaintenancePath -Encoding UTF8
        [void](Import-OnlineAdultDomains)
        return $true
    } catch {
        Write-Warning "Online adult-domain update failed: $($_.Exception.Message)"
        [void](Import-OnlineAdultDomains)
        return $false
    }
}

'@
    $anchor = 'function Update-Categories([bool]$Force = $false) {'
    $core = Replace-Required $core $anchor ($onlineCode + $anchor) 'the Update-Categories function'

    $oldMaintenance = @'
    Import-Categories
    if (-not (Test-Path $EventsPath)) { return }
'@
    $newMaintenance = @'
    try { [void](Update-OnlineAdultDomains) } catch { Write-Warning "Online adult-domain update failed: $($_.Exception.Message)" }
    Import-Categories
    [void](Import-OnlineAdultDomains)
    if (-not (Test-Path $EventsPath)) { return }
'@
    $core = Replace-Required $core $oldMaintenance $newMaintenance 'the maintenance category import block'

    $classificationInsert = @'
    if (Test-OnlineAdultDomain $d) {
        if ($d -match '(^|\.)(video|videos|vod|hls|stream|media|mp4|cdn[0-9-]*)(-|\.)' -or $d -match '(^|\.)video\.') {
            return [ordered]@{ category='adult'; evidence='stream'; confidence=92; label='Adult video delivery (online database)' }
        }
        return [ordered]@{ category='adult'; evidence='direct'; confidence=93; label='Adult domain (online database)' }
    }
    if ($d -match '(^|[.-])(xxx[a-z0-9-]*|porn[a-z0-9-]*|hentai[a-z0-9-]*)([.-]|$)') {
        $evidence = if ($d -match '(^|\.)(video|videos|vod|hls|stream|media|mp4|cdn[0-9-]*)(-|\.)') { 'stream' } else { 'direct' }
        $label = if ($evidence -eq 'stream') { 'Adult video delivery (hostname signal)' } else { 'Likely adult domain (hostname signal)' }
        return [ordered]@{ category='adult'; evidence=$evidence; confidence=90; label=$label }
    }
'@
    $classAnchor = '    if (Test-Suffix $d $BypassDomains) {'
    $core = Replace-Required $core $classAnchor ($classificationInsert + $classAnchor) 'the bypass classification block'

    $identityOld = "    `$category='Application or web service'; `$basis='General hostname classification'; `$confidence=45"
    $identityNew = @'
    if ((Test-Suffix $d $AdultSites) -or (Test-Suffix $d $AdultCdns) -or (Test-OnlineAdultDomain $d) -or $d -match '(^|[.-])(xxx[a-z0-9-]*|porn[a-z0-9-]*|hentai[a-z0-9-]*)([.-]|$)') {
        return [ordered]@{ owner='Adult-content service'; category='Adult website or media infrastructure'; confidence=92; basis='HomeWatch adult-domain intelligence' }
    }
    $category='Application or web service'; $basis='General hostname classification'; $confidence=45
'@
    $core = Replace-Required $core $identityOld $identityNew 'the generic domain identity fallback'

    $settingsOld = '                if ($body.updateNow) { [void](Update-Categories $true); Import-Categories }'
    $settingsNew = '                if ($body.updateNow) { [void](Update-Categories $true); Import-Categories; [void](Update-OnlineAdultDomains $true); [void](Import-OnlineAdultDomains); $script:EventViewCache=@{} }'
    $core = Replace-Required $core $settingsOld $settingsNew 'the Update categories now handler'

    $startupAnchor = '$listener = New-Object Net.HttpListener'
    $startupCode = @'
try {
    [void](Update-OnlineAdultDomains)
    [void](Import-OnlineAdultDomains)
} catch {
    Write-Warning "Initial online adult-domain load failed: $($_.Exception.Message)"
}

$listener = New-Object Net.HttpListener
'@
    $core = Replace-Required $core $startupAnchor $startupCode 'the HomeWatch listener startup'
}

if ($app -notmatch 'Adult video delivery \(database\)') {
    $oldApp = "function enrichEvents(events){return events.map(e=>{const hi=hostnameIntel(e.domain);return{...e,hostnameRole:hi.role,hostnameRoleLabel:hi.label,hostnameRoleConfidence:hi.confidence,contentHints:contentHints(e.domain)}})}"
    $newApp = "function enrichEvents(events){return events.map(e=>{let hi=hostnameIntel(e.domain);if(e.category==='adult'){const role=e.evidence==='stream'?'stream':e.evidence==='asset'?'image':e.evidence==='ad'?'ad':e.evidence==='direct'?'direct':hi.role;const labels={stream:'Adult video delivery (database)',image:'Adult images/assets (database)',ad:'Adult advertising (database)',direct:'Direct adult domain (database)',cdn:'Adult content server (database)'};hi={role,label:labels[role]||labels[e.evidence]||'Adult supporting service (database)',confidence:Math.max(Number(e.confidence||0),88)}}return{...e,hostnameRole:hi.role,hostnameRoleLabel:hi.label,hostnameRoleConfidence:hi.confidence,contentHints:contentHints(e.domain)}})}"
    $app = Replace-Required $app $oldApp $newApp 'the enrichEvents function in web/app.js'
}

$index = $index.Replace('v1.3.1', 'v1.3.2')
$index = $index.Replace('/app.js?v=20260720-25', '/app.js?v=20260721-32')
$index = $index.Replace(
    'A specialist website-categorization provider can add broad site-level categories, but exact video-level categories require browser or decrypted URL visibility.',
    'HomeWatch now checks a maintained online adult-domain feed in addition to its local intelligence. This improves site and infrastructure recognition, but DNS still cannot reveal exact page titles, searches, or video categories.'
)

Set-Content $CorePath $core -Encoding UTF8
Set-Content $AppPath $app -Encoding UTF8
Set-Content $IndexPath $index -Encoding UTF8

Write-Host 'HomeWatch v1.3.2 patch applied.' -ForegroundColor Green
Write-Host 'Restart HomeWatch. On startup it will download and cache the maintained online adult-domain feed.' -ForegroundColor Cyan
Write-Host 'The feed refreshes once per day; Update categories now also forces an immediate refresh.' -ForegroundColor Cyan
