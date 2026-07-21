#requires -Version 5.1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$path = Join-Path $root 'HomeWatch.ps1'
if (-not (Test-Path $path)) { throw 'HomeWatch.ps1 was not found.' }

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = "$path.before-v1.1.3-$timestamp.bak"
Copy-Item $path $backup -Force

try {
    $text = [IO.File]::ReadAllText($path)

    # Refuse to patch a damaged file. The restored source should parse cleanly first.
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw ('HomeWatch.ps1 already contains PowerShell syntax errors. Restore a known-good backup first. First error: ' + $parseErrors[0].Message)
    }

    if ($text -notmatch '\$EventDiskCachePath\s*=') {
        $eventsLine = '$EventsPath = Join-Path $DataDir ''events.jsonl'''
        if (-not $text.Contains($eventsLine)) { throw 'Could not locate the EventsPath definition.' }
        $text = $text.Replace($eventsLine, $eventsLine + [Environment]::NewLine + '$EventDiskCachePath = Join-Path $DataDir ''event-view-cache''')
    }

    # Restore LAN listening without changing the browser URL opened on the local PC.
    $text = $text.Replace('$listener.Prefixes.Add("http://127.0.0.1:$Port/")', '$listener.Prefixes.Add("http://+:$Port/")')

    # Parse again after the small edits and replace exactly the Get-Events AST extent.
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw ('Pre-replacement validation failed: ' + $parseErrors[0].Message) }
    $functionAst = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-Events' }, $true)
    if (-not $functionAst) { throw 'Could not locate the Get-Events function.' }

    $replacement = @'
function Get-Events([int]$Hours = 24) {
    $cutoff = [DateTimeOffset]::Now.AddHours(-$Hours)
    $eventStamp = if (Test-Path $EventsPath) { (Get-Item $EventsPath).LastWriteTimeUtc.Ticks } else { 0 }
    $configStamp = if (Test-Path $ConfigPath) { (Get-Item $ConfigPath).LastWriteTimeUtc.Ticks } else { 0 }
    $cacheKey = ('{0}|{1}|{2}' -f $Hours,$eventStamp,$configStamp)
    if ($script:EventViewCache.ContainsKey($cacheKey)) { return @($script:EventViewCache[$cacheKey]) }

    $cfg = Read-Config
    $aliases = @{}
    if ($cfg -and $cfg.aliases) { $cfg.aliases.psobject.Properties | ForEach-Object { $aliases[$_.Name] = $_.Value } }
    $macAliases = @{}
    if ($cfg -and $cfg.macAliases) { $cfg.macAliases.psobject.Properties | ForEach-Object { $macAliases[$_.Name.ToUpperInvariant()] = $_.Value } }
    $discovered = @{}
    if ($cfg -and $cfg.discoveredNames) { $cfg.discoveredNames.psobject.Properties | ForEach-Object { $discovered[$_.Name] = $_.Value } }
    $macs = @{}
    if ($cfg -and $cfg.discoveredMacs) { $cfg.discoveredMacs.psobject.Properties | ForEach-Object { $macs[$_.Name] = $_.Value } }
    $ignored = @{}
    if ($cfg -and $cfg.ignoredDomains) {
        @($cfg.ignoredDomains) | ForEach-Object { $ignored[([string]$_).Trim().TrimEnd('.').ToLowerInvariant()] = $true }
    }

    $decorateEvent = {
        param($e)
        $domainKey = ([string]$e.domain).Trim().TrimEnd('.').ToLowerInvariant()
        $kind = Classify-Domain $domainKey
        foreach ($property in @('category','evidence','confidence','label')) {
            $e | Add-Member -NotePropertyName $property -NotePropertyValue $kind[$property] -Force
        }
        $e | Add-Member -NotePropertyName description -NotePropertyValue (Get-DomainDescription $domainKey) -Force
        $identity = Get-DomainIdentity $domainKey
        $e | Add-Member -NotePropertyName serviceOwner -NotePropertyValue $identity.owner -Force
        $e | Add-Member -NotePropertyName serviceCategory -NotePropertyValue $identity.category -Force
        $e | Add-Member -NotePropertyName serviceCategoryConfidence -NotePropertyValue $identity.confidence -Force
        $e | Add-Member -NotePropertyName ignored -NotePropertyValue ([bool]$ignored.ContainsKey($domainKey)) -Force
        $clientKey = [string]$e.client
        $mac = if ($macs.ContainsKey($clientKey)) { [string]$macs[$clientKey] } else { '' }
        $macKey = $mac.ToUpperInvariant()
        $displayName = if ($macKey -and $macAliases.ContainsKey($macKey)) { $macAliases[$macKey] } elseif ($aliases.ContainsKey($clientKey)) { $aliases[$clientKey] } elseif ($discovered.ContainsKey($clientKey)) { $discovered[$clientKey] } else { $clientKey }
        $e | Add-Member -NotePropertyName clientName -NotePropertyValue $displayName -Force
        $e | Add-Member -NotePropertyName mac -NotePropertyValue $mac -Force
        return $e
    }

    $diskPath = ('{0}-{1}.clixml' -f $EventDiskCachePath,$Hours)
    $items = New-Object Collections.Generic.List[object]
    $script:LastEventReadErrors = 0
    $totalLines = 0
    if (Test-Path $EventsPath) {
        foreach ($unused in [IO.File]::ReadLines($EventsPath)) { $totalLines++ }
    }

    $startLine = 0
    $loadedDiskCache = $false
    if (Test-Path $diskPath) {
        try {
            $diskCache = Import-Clixml -Path $diskPath
            $cachedLineCount = [int64]$diskCache.sourceLineCount
            if ([int64]$diskCache.configStamp -eq [int64]$configStamp -and $cachedLineCount -ge 0 -and $cachedLineCount -le $totalLines) {
                foreach ($cachedEvent in @($diskCache.events)) {
                    try {
                        if ([DateTimeOffset]::Parse($cachedEvent.time) -ge $cutoff) { $items.Add($cachedEvent) }
                    } catch { $script:LastEventReadErrors++ }
                }
                $startLine = [int]$cachedLineCount
                $loadedDiskCache = $true
                Write-Host ('Event cache: loaded {0} events from disk; reading {1} appended record(s)' -f $items.Count,($totalLines-$startLine))
            }
        } catch {
            Write-Warning ('Could not load incremental event cache: ' + $_.Exception.Message)
        }
    }

    if (Test-Path $EventsPath) {
        $lines = if ($loadedDiskCache) { Get-Content $EventsPath | Select-Object -Skip $startLine } else { Get-Content $EventsPath -Tail 50000 }
        foreach ($line in $lines) {
            try {
                $e = $line | ConvertFrom-Json
                if ([DateTimeOffset]::Parse($e.time) -ge $cutoff) { $items.Add((& $decorateEvent $e)) }
            } catch { $script:LastEventReadErrors++ }
        }
    }

    $result = @($items | Sort-Object time -Descending)
    $script:EventViewCache = @{$cacheKey=$result}

    try {
        $cacheTemp = $diskPath + '.tmp'
        [pscustomobject]@{
            version = 2
            hours = $Hours
            configStamp = [int64]$configStamp
            sourceLineCount = [int64]$totalLines
            createdAt = [DateTimeOffset]::Now.ToString('o')
            events = $result
        } | Export-Clixml -Path $cacheTemp -Depth 8
        Move-Item $cacheTemp $diskPath -Force
        if (-not $loadedDiskCache -or $totalLines -gt $startLine) {
            Write-Host ('Event cache: saved {0} events to disk' -f $result.Count)
        }
    } catch {
        Write-Warning ('Could not save incremental event cache: ' + $_.Exception.Message)
    }

    return $result
}
'@

    $start = $functionAst.Extent.StartOffset
    $length = $functionAst.Extent.EndOffset - $start
    $text = $text.Remove($start, $length).Insert($start, $replacement)

    # Final parser validation. Nothing is written unless the complete result is valid PowerShell.
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw ('Generated HomeWatch.ps1 failed validation: ' + $parseErrors[0].Message)
    }

    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
    Write-Host ('HomeWatch v1.1.3 performance patch applied. Backup: ' + $backup) -ForegroundColor Green
}
catch {
    Copy-Item $backup $path -Force
    Write-Host 'The update failed validation. The original HomeWatch.ps1 was restored automatically.' -ForegroundColor Yellow
    throw
}
