#requires -Version 5.1
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$HomeWatchPath = Join-Path $Root 'HomeWatch.ps1'
$AppPath = Join-Path $Root 'web\app.js'
$IndexPath = Join-Path $Root 'web\index.html'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

if (-not (Test-Path $HomeWatchPath)) { throw "HomeWatch.ps1 was not found in $Root" }
if (-not (Test-Path $AppPath)) { throw "web\app.js was not found in $Root" }

Copy-Item $HomeWatchPath "$HomeWatchPath.before-v1.2.0-$stamp.bak" -Force
Copy-Item $AppPath "$AppPath.before-v1.2.0-$stamp.bak" -Force
if (Test-Path $IndexPath) { Copy-Item $IndexPath "$IndexPath.before-v1.2.0-$stamp.bak" -Force }

function Replace-PowerShellFunction {
    param([string]$Text,[string]$Name,[string]$Replacement)
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseInput($Text,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw "Cannot parse HomeWatch.ps1 before patching: $($errors[0].Message)"}
    $fn=$ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name},$true)|Select-Object -First 1
    if(-not $fn){throw "Function $Name was not found."}
    return $Text.Substring(0,$fn.Extent.StartOffset)+$Replacement+$Text.Substring($fn.Extent.EndOffset)
}

$hostnameFunction = @'
function Get-HostnameIntelligence([string]$Domain) {
    $d=$Domain.Trim().TrimEnd('.').ToLowerInvariant()
    $role='Unknown supporting service';$contentType='Unknown';$confidence=45;$hints=New-Object Collections.Generic.List[string]
    $tokens=@($d -split '[.\-_]')

    if(Test-Suffix $d $AdultSites){$role='Main adult site or first-party service';$contentType='Adult website';$confidence=88}
    if(Test-Suffix $d $AdultCdns){$role='Adult media/content delivery';$contentType='Adult media';$confidence=82}

    if($d -match '(^|[.\-_])(ads?|adserver|adservice|vast|promo|campaign|sponsor)([.\-_]|$)'){$role='Advertising';$contentType='Advertisement';$confidence=[Math]::Max($confidence,82)}
    if($d -match '(^|[.\-_])(pop|popup|popunder|redirect|click|onclick|landing|offer|trk|track|tracking)([.\-_]|$)'){$role='Popup, redirect, or click tracking';$contentType='Advertising redirect';$confidence=[Math]::Max($confidence,85)}
    if($d -match '(^|[.\-_])(analytics|metrics|telemetry|pixel|beacon|measure)([.\-_]|$)'){$role='Analytics or tracking';$contentType='Tracking';$confidence=[Math]::Max($confidence,78)}
    if($d -match '(^|[.\-_])(thumb|thumbs|thumbnail|preview|poster|gallery|images?|img|photo|pics?)([0-9]*[.\-_]|[.\-_]|$)'){$role='Thumbnail, preview, or image delivery';$contentType='Images';$confidence=[Math]::Max($confidence,78)}
    if($d -match '(^|[.\-_])(video|vod|hls|dash|stream|streaming|media|mp4|m3u8|manifest|player)([0-9]*[.\-_]|[.\-_]|$)'){$role='Video player or streaming delivery';$contentType='Video';$confidence=[Math]::Max($confidence,86)}
    if($d -match '(^|[.\-_])(live|cam|cams|webcam|webcams)([0-9]*[.\-_]|[.\-_]|$)'){$role='Live camera or live-stream service';$contentType='Live cams';$confidence=[Math]::Max($confidence,88);$hints.Add('live cams')}
    if($d -match '(^|[.\-_])(chat|messaging|message|socket|websocket)([0-9]*[.\-_]|[.\-_]|$)'){$role='Chat or real-time messaging';$contentType='Chat';$confidence=[Math]::Max($confidence,76)}
    if($d -match '(^|[.\-_])(search|find|query|autocomplete)([0-9]*[.\-_]|[.\-_]|$)'){$role='Search or discovery service';$contentType='Search';$confidence=[Math]::Max($confidence,72)}
    if($d -match '(^|[.\-_])(api|graphql|graph|gateway|service)([0-9]*[.\-_]|[.\-_]|$)'){$role='Application API or backend';$contentType='API';$confidence=[Math]::Max($confidence,68)}
    if($d -match '(^|[.\-_])(cdn|edge|cache|static|assets?)([0-9]*[.\-_]|[.\-_]|$)' -and $contentType -eq 'Unknown'){$role='Static asset or CDN delivery';$contentType='Assets';$confidence=[Math]::Max($confidence,66)}

    $categoryMap=[ordered]@{
        'vr'='VR';'virtualreality'='VR';'hentai'='hentai/anime';'anime'='hentai/anime';'cartoon'='animated content';'3d'='3D/animated content';
        'gay'='gay content';'lesbian'='lesbian content';'trans'='trans content';'milf'='MILF-labelled content';'teen'='teen-labelled content';
        'mature'='mature-labelled content';'cougar'='cougar-labelled content';'dating'='dating';'stories'='stories';'story'='stories';'live'='live content';'cams'='live cams';'cam'='live cams'
    }
    foreach($key in $categoryMap.Keys){if($tokens -contains $key -and -not $hints.Contains($categoryMap[$key])){$hints.Add($categoryMap[$key])}}

    [ordered]@{role=$role;contentType=$contentType;confidence=$confidence;contentHints=@($hints);basis='Hostname keywords and recognized domain lists'}
}
'@

$classifyFunction = @'
function Classify-Domain([string]$Domain) {
    $d=$Domain.TrimEnd('.').ToLowerInvariant();$intel=Get-HostnameIntelligence $d
    if(Test-Suffix $d $AdultSites){
        if($intel.contentType -eq 'Video'){return [ordered]@{category='adult';evidence='stream';confidence=88;label='Adult video delivery';role=$intel.role;contentType=$intel.contentType;roleConfidence=$intel.confidence;contentHints=$intel.contentHints}}
        if($intel.contentType -eq 'Images'){return [ordered]@{category='adult';evidence='asset';confidence=70;label='Adult thumbnail or image asset';role=$intel.role;contentType=$intel.contentType;roleConfidence=$intel.confidence;contentHints=$intel.contentHints}}
        if($intel.contentType -in @('Advertisement','Advertising redirect','Tracking')){return [ordered]@{category='adult';evidence='ad';confidence=72;label=$intel.role;role=$intel.role;contentType=$intel.contentType;roleConfidence=$intel.confidence;contentHints=$intel.contentHints}}
        foreach($site in $AdultSites){if($d -eq $site -or $d -eq ('www.'+$site)){return [ordered]@{category='adult';evidence='direct';confidence=95;label='Direct adult site';role='Main adult website';contentType='Adult website';roleConfidence=98;contentHints=$intel.contentHints}}}
        return [ordered]@{category='adult';evidence='cdn';confidence=60;label='Adult-site supporting service';role=$intel.role;contentType=$intel.contentType;roleConfidence=$intel.confidence;contentHints=$intel.contentHints}
    }
    if(Test-Suffix $d $AdultCdns){
        $evidence=if($intel.contentType -eq 'Video'){'stream'}elseif($intel.contentType -eq 'Images'){'asset'}elseif($intel.contentType -in @('Advertisement','Advertising redirect','Tracking')){'ad'}else{'cdn'}
        $label=if($evidence -eq 'stream'){'Adult video delivery'}elseif($evidence -eq 'asset'){'Adult thumbnail or image asset'}elseif($evidence -eq 'ad'){$intel.role}else{'Adult content server'}
        return [ordered]@{category='adult';evidence=$evidence;confidence=$(if($evidence -eq 'stream'){88}elseif($evidence -eq 'ad'){72}else{62});label=$label;role=$intel.role;contentType=$intel.contentType;roleConfidence=$intel.confidence;contentHints=$intel.contentHints}
    }
    if(Test-Suffix $d $BypassDomains){return [ordered]@{category='bypass';evidence='dns';confidence=70;label='Encrypted DNS or privacy relay';role='Encrypted DNS or privacy relay';contentType='Privacy service';roleConfidence=90;contentHints=@()}}
    return [ordered]@{category='other';evidence='domain';confidence=0;label='Other';role=$intel.role;contentType=$intel.contentType;roleConfidence=$intel.confidence;contentHints=$intel.contentHints}
}
'@

$sessionsFunction = @'
function Get-Sessions([object[]]$Events) {
    $adult=@($Events|Where-Object{$_.category -eq 'adult' -and -not $_.ignored}|Sort-Object client,time);$sessions=New-Object Collections.Generic.List[object]
    foreach($group in ($adult|Group-Object client)){
        $current=$null
        foreach($e in $group.Group){$t=[DateTimeOffset]::Parse($e.time);if(-not $current -or ($t-$current.end).TotalMinutes -gt 20){if($current){$sessions.Add($current)};$current=[pscustomobject]@{client=$e.client;clientName=$e.clientName;start=$t;end=$t;events=New-Object Collections.Generic.List[object]}};$current.end=$t;$current.events.Add($e)}
        if($current){$sessions.Add($current)}
    }
    foreach($s in $sessions){
        $ordered=@($s.events|Sort-Object time);$directEvents=@($ordered|Where-Object evidence -eq 'direct');$streamEvents=@($ordered|Where-Object evidence -eq 'stream');$assetEvents=@($ordered|Where-Object{$_.evidence -in @('asset','cdn')});$adEvents=@($ordered|Where-Object evidence -eq 'ad')
        $logical=@{};foreach($e in $ordered){$t=[DateTimeOffset]::Parse($e.time);$logical[($e.domain+'|'+[Math]::Floor($t.ToUnixTimeSeconds()/2))]=$true}
        $roleCounts=[ordered]@{};$contentTypes=[ordered]@{};$hintSet=[ordered]@{};$timeline=New-Object Collections.Generic.List[object]
        foreach($e in $ordered){$intel=Get-HostnameIntelligence ([string]$e.domain);$role=[string]$intel.role;$type=[string]$intel.contentType;if(-not $roleCounts.Contains($role)){$roleCounts[$role]=0};$roleCounts[$role]++;if(-not $contentTypes.Contains($type)){$contentTypes[$type]=0};$contentTypes[$type]++;foreach($h in @($intel.contentHints)){$hintSet[$h]=$true};$timeline.Add([ordered]@{time=$e.time;domain=$e.domain;role=$role;contentType=$type;confidence=$intel.confidence;evidence=$e.evidence})}
        $directDomains=@($directEvents.domain|Sort-Object -Unique);$streamDomains=@($streamEvents.domain|Sort-Object -Unique);$assetDomains=@($assetEvents.domain|Sort-Object -Unique);$adDomains=@($adEvents.domain|Sort-Object -Unique)
        $siteSequence=New-Object Collections.Generic.List[string];foreach($e in $directEvents){$site=Get-RegistrableDomain ([string]$e.domain);if($siteSequence.Count -eq 0 -or $siteSequence[$siteSequence.Count-1] -ne $site){$siteSequence.Add($site)}}
        $popupCandidates=New-Object Collections.Generic.List[object]
        foreach($ad in $adEvents){$at=[DateTimeOffset]::Parse($ad.time);$prior=@($ordered|Where-Object{[DateTimeOffset]::Parse($_.time) -le $at -and $_.evidence -in @('direct','asset','cdn')}|Select-Object -Last 1);$origin=if($prior.Count){Get-RegistrableDomain ([string]$prior[0].domain)}elseif($siteSequence.Count){$siteSequence[$siteSequence.Count-1]}else{'Unknown'};$intel=Get-HostnameIntelligence ([string]$ad.domain);if($intel.contentType -eq 'Advertising redirect' -or $intel.role -match 'Popup|redirect|click'){$popupCandidates.Add([ordered]@{time=$ad.time;origin=$origin;destination=$ad.domain;confidence=$intel.confidence;reason=$intel.role})}}
        $firstDirect=if($directEvents.Count){[DateTimeOffset]::Parse($directEvents[0].time)}else{$null};$firstStream=if($streamEvents.Count){[DateTimeOffset]::Parse($streamEvents[0].time)}else{$null};$lastStream=if($streamEvents.Count){[DateTimeOffset]::Parse($streamEvents[-1].time)}else{$null}
        $playbackEpisodes=0;$priorStream=$null;foreach($e in $streamEvents){$t=[DateTimeOffset]::Parse($e.time);if(-not $priorStream -or ($t-$priorStream).TotalSeconds -gt 120){$playbackEpisodes++};$priorStream=$t}
        $assetBursts=0;$priorAsset=$null;foreach($e in $assetEvents){$t=[DateTimeOffset]::Parse($e.time);if(-not $priorAsset -or ($t-$priorAsset).TotalSeconds -gt 10){$assetBursts++};$priorAsset=$t}
        $total=[Math]::Max(1,$ordered.Count);$videoScore=[Math]::Min(99,[Math]::Round(($streamEvents.Count/$total)*100+$(if($streamEvents.Count){45}else{0})+$(if($playbackEpisodes -gt 1){8}else{0})));$browseScore=[Math]::Min(99,[Math]::Round(($assetEvents.Count/$total)*100+$(if($directEvents.Count){25}else{0})));$adScore=[Math]::Min(99,[Math]::Round(($adEvents.Count/$total)*100+$(if($popupCandidates.Count){40}else{0})));$liveScore=$(if($hintSet.Contains('live cams')){90}elseif(@($ordered|Where-Object{(Get-HostnameIntelligence $_.domain).contentType -eq 'Live cams'}).Count){85}else{5})
        $assessment=if($streamEvents.Count -and $directEvents.Count){'Confirmed adult-site browsing with likely video delivery'}elseif($directEvents.Count){'Confirmed adult-site visit; playback not established'}else{'Adult supporting infrastructure only; initiating page not observed'}
        $narrative=('Observed {0} DNS requests across {1} logical contacts. Sites: {2}. Video likelihood {3}%, browsing likelihood {4}%, popup/ad likelihood {5}%. {6}' -f $ordered.Count,$logical.Count,$(if($siteSequence.Count){$siteSequence -join ' → '}else{'not established'}),$videoScore,$browseScore,$adScore,$(if($hintSet.Count){'Hostname hints: '+(($hintSet.Keys)-join ', ')+'.'}else{'No reliable content-category keyword was exposed in the hostnames.'}))
        [ordered]@{
            client=$s.client;clientName=$s.clientName;start=$s.start.ToString('o');end=$s.end.ToString('o');durationMinutes=[Math]::Max(1,[Math]::Ceiling(($s.end-$s.start).TotalMinutes));durationSeconds=[Math]::Round(($s.end-$s.start).TotalSeconds)
            requests=$ordered.Count;logicalContacts=$logical.Count;directRequests=$directEvents.Count;streamRequests=$streamEvents.Count;assetRequests=$assetEvents.Count;advertisingRequests=$adEvents.Count
            domains=@($ordered.domain|Sort-Object -Unique);directDomains=$directDomains;streamDomains=$streamDomains;assetDomains=$assetDomains;advertisingDomains=$adDomains
            siteSequence=@($siteSequence);siteTransitions=[Math]::Max(0,$siteSequence.Count-1);popupCandidates=@($popupCandidates);popupCount=$popupCandidates.Count;roleCounts=$roleCounts;contentTypeCounts=$contentTypes;contentHints=@($hintSet.Keys);timeline=@($timeline)
            firstDirectAt=$(if($firstDirect){$firstDirect.ToString('o')}else{$null});firstStreamAt=$(if($firstStream){$firstStream.ToString('o')}else{$null});lastStreamAt=$(if($lastStream){$lastStream.ToString('o')}else{$null});timeToFirstStreamSeconds=$(if($firstDirect -and $firstStream){[Math]::Max(0,[Math]::Round(($firstStream-$firstDirect).TotalSeconds))}else{$null});streamWindowSeconds=$(if($firstStream){[Math]::Max(0,[Math]::Round(($lastStream-$firstStream).TotalSeconds))}else{0})
            possiblePlaybackPhases=$playbackEpisodes;assetLoadingBursts=$assetBursts;possibleNavigationChanges=[Math]::Max(0,$assetBursts-1);videoLikelihood=$videoScore;browsingLikelihood=$browseScore;liveCamLikelihood=$liveScore;popupLikelihood=$adScore;assessment=$assessment;narrative=$narrative;confidence=$(if($streamEvents.Count -and $directEvents.Count){95}elseif($directEvents.Count){85}else{55})
        }
    }
}
'@

try {
    $text=Get-Content $HomeWatchPath -Raw
    if($text -notmatch 'function Get-HostnameIntelligence'){
        $insertAt=$text.IndexOf('function Classify-Domain')
        if($insertAt -lt 0){throw 'Classify-Domain was not found.'}
        $text=$text.Substring(0,$insertAt)+$hostnameFunction+"`r`n`r`n"+$text.Substring($insertAt)
    } else {
        $text=Replace-PowerShellFunction $text 'Get-HostnameIntelligence' $hostnameFunction
    }
    $text=Replace-PowerShellFunction $text 'Classify-Domain' $classifyFunction
    $text=Replace-PowerShellFunction $text 'Get-Sessions' $sessionsFunction

    $text=$text.Replace("foreach (`$property in @('category','evidence','confidence','label'))", "foreach (`$property in @('category','evidence','confidence','label','role','contentType','roleConfidence','contentHints'))")
    $text=$text.Replace("        label = `$kind.label`r`n        description", "        label = `$kind.label`r`n        role = `$kind.role`r`n        contentType = `$kind.contentType`r`n        roleConfidence = `$kind.roleConfidence`r`n        contentHints = @(`$kind.contentHints)`r`n        description")

    $tokens=$null;$errors=$null
    [void][Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw "Patched HomeWatch.ps1 failed validation: $($errors[0].Message)"}
    Set-Content $HomeWatchPath $text -Encoding UTF8

    $js=Get-Content $AppPath -Raw
    $oldSession="$('#sessions').innerHTML=x.sessions.length?x.sessions.map(s=>`<div class=\"session\"><div><strong>${esc(s.clientName)}</strong><div class=\"when\">${esc(s.client)}</div></div><div><strong>${new Date(s.start).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'})}–${new Date(s.end).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'})}</strong><div class=\"when\">${new Date(s.start).toLocaleDateString()} · ${s.durationSeconds}s observed</div></div><div><strong>${esc(s.assessment)}</strong><div class=\"sessionNarrative\">${esc(s.narrative)}</div><div class=\"sessionFacts\">${s.logicalContacts} logical contacts · ${s.possiblePlaybackPhases} possible playback phase${s.possiblePlaybackPhases===1?'':'s'} · ${s.assetLoadingBursts} asset-loading burst${s.assetLoadingBursts===1?'':'s'} · ${s.advertisingRequests||0} advertising requests${s.timeToFirstStreamSeconds!=null?' · media after '+s.timeToFirstStreamSeconds+'s':''}</div><div class=\"domains\">${s.domains.map(esc).join(', ')}</div></div><div><strong>${s.confidence}%</strong><div class=\"confidence\"><i style=\"width:${s.confidence}%\"></i></div></div></div>`).join(''):'<p>No adult activity detected during this period.</p>';"
    $newSession="$('#sessions').innerHTML=x.sessions.length?x.sessions.map(s=>`<div class=\"session\"><div><strong>${esc(s.clientName)}</strong><div class=\"when\">${esc(s.client)}</div></div><div><strong>${new Date(s.start).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'})}–${new Date(s.end).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'})}</strong><div class=\"when\">${new Date(s.start).toLocaleDateString()} · ${s.durationSeconds}s observed</div></div><div><strong>${esc(s.assessment)}</strong><div class=\"sessionNarrative\">${esc(s.narrative)}</div><div class=\"sessionFacts\">Video ${s.videoLikelihood??0}% · browsing ${s.browsingLikelihood??0}% · live cams ${s.liveCamLikelihood??0}% · popup/ads ${s.popupLikelihood??0}% · ${s.possiblePlaybackPhases||0} playback phases · ${s.popupCount||0} popup candidates</div>${(s.siteSequence||[]).length?`<div class=\"domains\"><strong>Site path:</strong> ${(s.siteSequence||[]).map(esc).join(' → ')}</div>`:''}${(s.contentHints||[]).length?`<div class=\"domains\"><strong>Hostname hints:</strong> ${(s.contentHints||[]).map(esc).join(', ')}</div>`:''}${(s.popupCandidates||[]).length?`<div class=\"domains\"><strong>Possible popups:</strong> ${(s.popupCandidates||[]).map(p=>esc(p.origin)+' → '+esc(p.destination)+' ('+p.confidence+'%)').join(', ')}</div>`:''}<div class=\"domains\">${s.domains.map(esc).join(', ')}</div></div><div><strong>${s.confidence}%</strong><div class=\"confidence\"><i style=\"width:${s.confidence}%\"></i></div></div></div>`).join(''):'<p>No adult activity detected during this period.</p>';"
    if($js.Contains($oldSession)){$js=$js.Replace($oldSession,$newSession)}else{Write-Warning 'The session card template was not patched because app.js differs from the expected version.'}
    $js=$js.Replace("'Evidence confidence'],...x.map(e=>[e.time,e.clientName,e.client,e.domain,e.queryType,(e.responseTypes||[]).join(' | '),(e.responseValues||[]).join(' | '),e.serviceOwner,e.serviceCategory,e.serviceCategoryConfidence,e.description,e.status,e.label,e.confidence])", "'Evidence confidence','Hostname role','Content type','Role confidence','Content hints'],...x.map(e=>[e.time,e.clientName,e.client,e.domain,e.queryType,(e.responseTypes||[]).join(' | '),(e.responseValues||[]).join(' | '),e.serviceOwner,e.serviceCategory,e.serviceCategoryConfidence,e.description,e.status,e.label,e.confidence,e.role,e.contentType,e.roleConfidence,(e.contentHints||[]).join(' | ')])")
    Set-Content $AppPath $js -Encoding UTF8

    if(Test-Path $IndexPath){$html=Get-Content $IndexPath -Raw;$html=[regex]::Replace($html,'HomeWatch v\d+\.\d+\.\d+','HomeWatch v1.2.0');Set-Content $IndexPath $html -Encoding UTF8}

    Write-Host 'HomeWatch v1.2.0 content intelligence installed successfully.' -ForegroundColor Green
    Write-Host 'Restart HomeWatch, run a test, then refresh the dashboard.' -ForegroundColor Cyan
} catch {
    Copy-Item "$HomeWatchPath.before-v1.2.0-$stamp.bak" $HomeWatchPath -Force -ErrorAction SilentlyContinue
    Copy-Item "$AppPath.before-v1.2.0-$stamp.bak" $AppPath -Force -ErrorAction SilentlyContinue
    if(Test-Path "$IndexPath.before-v1.2.0-$stamp.bak"){Copy-Item "$IndexPath.before-v1.2.0-$stamp.bak" $IndexPath -Force -ErrorAction SilentlyContinue}
    throw
}
