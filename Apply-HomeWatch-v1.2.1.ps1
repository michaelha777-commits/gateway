#requires -Version 5.1
$ErrorActionPreference='Stop'
$Root=Split-Path -Parent $MyInvocation.MyCommand.Path
$installer=Join-Path $Root 'Apply-HomeWatch-v1.2.0.ps1'
if(-not(Test-Path $installer)){throw 'Apply-HomeWatch-v1.2.0.ps1 must be in the same folder.'}

$text=Get-Content $installer -Raw
$start=$text.IndexOf('    $oldSession=')
$endMarker="    `$js=`$js.Replace(\"'Evidence confidence'"
$end=$text.IndexOf($endMarker,$start)
if($start -lt 0 -or $end -lt 0){throw 'Could not locate the JavaScript template patch in v1.2.0.'}

$safe=@'
    $sessionPattern='(?m)^\s*\$\(''#sessions''\)\.innerHTML=x\.sessions\.length\?.*$'
    $newSession=@'
 $('#sessions').innerHTML=x.sessions.length?x.sessions.map(s=>`<div class="session"><div><strong>${esc(s.clientName)}</strong><div class="when">${esc(s.client)}</div></div><div><strong>${new Date(s.start).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'})}–${new Date(s.end).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'})}</strong><div class="when">${new Date(s.start).toLocaleDateString()} · ${s.durationSeconds}s observed</div></div><div><strong>${esc(s.assessment)}</strong><div class="sessionNarrative">${esc(s.narrative)}</div><div class="sessionFacts">Video ${s.videoLikelihood??0}% · browsing ${s.browsingLikelihood??0}% · live cams ${s.liveCamLikelihood??0}% · popup/ads ${s.popupLikelihood??0}% · ${s.possiblePlaybackPhases||0} playback phases · ${s.popupCount||0} popup candidates</div>${(s.siteSequence||[]).length?`<div class="domains"><strong>Site path:</strong> ${(s.siteSequence||[]).map(esc).join(' → ')}</div>`:''}${(s.contentHints||[]).length?`<div class="domains"><strong>Hostname hints:</strong> ${(s.contentHints||[]).map(esc).join(', ')}</div>`:''}${(s.popupCandidates||[]).length?`<div class="domains"><strong>Possible popups:</strong> ${(s.popupCandidates||[]).map(p=>esc(p.origin)+' → '+esc(p.destination)+' ('+p.confidence+'%)').join(', ')}</div>`:''}<div class="domains">${s.domains.map(esc).join(', ')}</div></div><div><strong>${s.confidence}%</strong><div class="confidence"><i style="width:${s.confidence}%"></i></div></div></div>`).join(''):'<p>No adult activity detected during this period.</p>';
'@
    if([regex]::IsMatch($js,$sessionPattern)){$js=[regex]::Replace($js,$sessionPattern,[Text.RegularExpressions.MatchEvaluator]{param($m)$newSession.TrimEnd()},1)}else{Write-Warning 'The session card template was not patched because app.js differs from the expected version.'}
'@

$text=$text.Substring(0,$start)+$safe+"`r`n"+$text.Substring($end)
Set-Content $installer $text -Encoding UTF8

# Also make the event-property insertion tolerant of LF or CRLF line endings.
$text=Get-Content $installer -Raw
$text=$text.Replace('$text=$text.Replace("        label = `$kind.label`r`n        description", "        label = `$kind.label`r`n        role = `$kind.role`r`n        contentType = `$kind.contentType`r`n        roleConfidence = `$kind.roleConfidence`r`n        contentHints = @(`$kind.contentHints)`r`n        description")','$text=[regex]::Replace($text,''(?m)^(\s*)label = \$kind\.label\s*$'',{param($m) $i=$m.Groups[1].Value; $m.Value+"`r`n${i}role = `$kind.role`r`n${i}contentType = `$kind.contentType`r`n${i}roleConfidence = `$kind.roleConfidence`r`n${i}contentHints = @(`$kind.contentHints)"},1)')
Set-Content $installer $text -Encoding UTF8

& $installer
