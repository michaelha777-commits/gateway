#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'HomeWatch.ps1'
if (-not (Test-Path $path)) { throw 'HomeWatch.ps1 was not found.' }
$backup = $path + '.before-v1.1.2-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.bak'
Copy-Item $path $backup -Force
$text = [IO.File]::ReadAllText($path)
if ($text -notmatch '\$EventDiskCachePath') {
    $text = $text.Replace("`$EventsPath = Join-Path `$DataDir 'events.jsonl'", "`$EventsPath = Join-Path `$DataDir 'events.jsonl'`r`n`$EventDiskCachePath = Join-Path `$DataDir 'event-view-cache-v2.clixml'")
} else {
    $text = [regex]::Replace($text, "\$EventDiskCachePath\s*=\s*Join-Path\s+\$DataDir\s+'[^']+'", "`$EventDiskCachePath = Join-Path `$DataDir 'event-view-cache-v2.clixml'")
}
$newFunction = @'
function Get-Events([int]$Hours = 24) {
    $cutoff = [DateTimeOffset]::Now.AddHours(-$Hours)
    $fileLength = if (Test-Path $EventsPath) { (Get-Item $EventsPath).Length } else { 0 }
    $configStamp = if (Test-Path $ConfigPath) { (Get-Item $ConfigPath).LastWriteTimeUtc.Ticks } else { 0 }
    $cacheKey = ('{0}|{1}|{2}' -f $Hours,$fileLength,$configStamp)
    if ($script:EventViewCache.ContainsKey($cacheKey)) { return @($script:EventViewCache[$cacheKey]) }

    $cfg = Read-Config
    $aliases=@{}; if($cfg -and $cfg.aliases){$cfg.aliases.psobject.Properties|ForEach-Object{$aliases[$_.Name]=$_.Value}}
    $macAliases=@{}; if($cfg -and $cfg.macAliases){$cfg.macAliases.psobject.Properties|ForEach-Object{$macAliases[$_.Name.ToUpperInvariant()]=$_.Value}}
    $discovered=@{}; if($cfg -and $cfg.discoveredNames){$cfg.discoveredNames.psobject.Properties|ForEach-Object{$discovered[$_.Name]=$_.Value}}
    $macs=@{}; if($cfg -and $cfg.discoveredMacs){$cfg.discoveredMacs.psobject.Properties|ForEach-Object{$macs[$_.Name]=$_.Value}}
    $ignored=@{}; if($cfg -and $cfg.ignoredDomains){@($cfg.ignoredDomains)|ForEach-Object{$ignored[([string]$_).Trim().TrimEnd('.').ToLowerInvariant()]=$true}}

    $items = New-Object Collections.Generic.List[object]
    $known = @{}
    $readOffset = 0L
    $loaded = $false
    $script:LastEventReadErrors = 0

    if (Test-Path $EventDiskCachePath) {
        try {
            $disk = Import-Clixml -Path $EventDiskCachePath
            if ([int]$disk.hours -eq $Hours -and [int64]$disk.configStamp -eq [int64]$configStamp -and [int64]$disk.sourceLength -le [int64]$fileLength) {
                foreach($e in @($disk.events)) {
                    if([DateTimeOffset]::Parse($e.time) -ge $cutoff){$items.Add($e);if($e.id){$known[[string]$e.id]=$true}}
                }
                $readOffset=[int64]$disk.sourceLength
                $loaded=$true
                Write-Host ('Event cache: loaded {0} events from disk; reading appended data only' -f $items.Count)
            }
        } catch { Write-Warning ('Could not load incremental event cache: ' + $_.Exception.Message) }
    }

    $processLine = {
        param([string]$line)
        if([string]::IsNullOrWhiteSpace($line)){return}
        try {
            $e=$line|ConvertFrom-Json
            if($e.id -and $known.ContainsKey([string]$e.id)){return}
            if([DateTimeOffset]::Parse($e.time) -lt $cutoff){return}
            $domainKey=([string]$e.domain).Trim().TrimEnd('.').ToLowerInvariant()
            $kind=Classify-Domain $domainKey
            foreach($property in @('category','evidence','confidence','label')){$e|Add-Member -NotePropertyName $property -NotePropertyValue $kind[$property] -Force}
            $e|Add-Member description (Get-DomainDescription $domainKey) -Force
            $identity=Get-DomainIdentity $domainKey
            $e|Add-Member serviceOwner $identity.owner -Force
            $e|Add-Member serviceCategory $identity.category -Force
            $e|Add-Member serviceCategoryConfidence $identity.confidence -Force
            $e|Add-Member ignored ([bool]$ignored.ContainsKey($domainKey)) -Force
            $clientKey=[string]$e.client;$mac=if($macs.ContainsKey($clientKey)){[string]$macs[$clientKey]}else{''};$macKey=$mac.ToUpperInvariant()
            $displayName=if($macKey -and $macAliases.ContainsKey($macKey)){$macAliases[$macKey]}elseif($aliases.ContainsKey($clientKey)){$aliases[$clientKey]}elseif($discovered.ContainsKey($clientKey)){$discovered[$clientKey]}else{$clientKey}
            $e|Add-Member clientName $displayName -Force;$e|Add-Member mac $mac -Force
            $items.Add($e);if($e.id){$known[[string]$e.id]=$true}
        } catch { $script:LastEventReadErrors++ }
    }

    if (Test-Path $EventsPath) {
        if ($loaded -and $readOffset -lt $fileLength) {
            $stream=[IO.File]::Open($EventsPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
            try {
                [void]$stream.Seek($readOffset,[IO.SeekOrigin]::Begin)
                $reader=New-Object IO.StreamReader($stream,[Text.Encoding]::UTF8,$true,4096,$true)
                try { while(($line=$reader.ReadLine()) -ne $null){& $processLine $line} } finally { $reader.Dispose() }
            } finally { $stream.Dispose() }
        } elseif (-not $loaded) {
            Get-Content $EventsPath -Tail 50000 | ForEach-Object { & $processLine $_ }
        }
    }

    $result=@($items|Sort-Object time -Descending)
    $script:EventViewCache=@{$cacheKey=$result}
    try {
        $tmp=$EventDiskCachePath+'.tmp'
        [pscustomobject]@{version=2;hours=$Hours;configStamp=[int64]$configStamp;sourceLength=[int64]$fileLength;createdAt=[DateTimeOffset]::Now.ToString('o');events=$result}|Export-Clixml -Path $tmp -Depth 8
        Move-Item $tmp $EventDiskCachePath -Force
        Write-Host ('Event cache: saved {0} events to disk' -f $result.Count)
    } catch { Write-Warning ('Could not save incremental event cache: ' + $_.Exception.Message) }
    return $result
}

'@
$pattern = '(?s)function Get-Events\(\[int\]\$Hours = 24\) \{.*?\r?\n\}\r?\n\r?\n(?=function Get-Sessions)'
if (-not [regex]::IsMatch($text,$pattern)) { throw 'Could not locate Get-Events.' }
$text = [regex]::Replace($text,$pattern,$newFunction,1)
$text = $text.Replace('$listener.Prefixes.Add("http://127.0.0.1:$Port/")','$listener.Prefixes.Add("http://+:$Port/")')
$text = $text.Replace('$listener.Prefixes.Add("http://+:$Port/")','$listener.Prefixes.Add("http://+:$Port/")')
$text = $text.Replace('Write-Host "HomeWatch is running at http://127.0.0.1:$Port" -ForegroundColor Green', '$lanIp=([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()|Where-Object{$_.OperationalStatus -eq ''Up''}|ForEach-Object{$_.GetIPProperties().UnicastAddresses}|Where-Object{$_.Address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and -not $_.Address.ToString().StartsWith(''127.'')}|Select-Object -First 1).Address;Write-Host ("HomeWatch is running at http://{0}:{1} (local http://127.0.0.1:{1})" -f $lanIp,$Port) -ForegroundColor Green')
[IO.File]::WriteAllText($path,$text,(New-Object Text.UTF8Encoding($false)))
Write-Host ('HomeWatch v1.1.2 patch applied. Backup: '+$backup) -ForegroundColor Green
