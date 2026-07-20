#requires -Version 5.1
param([int]$Port = 8765)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$DataDir = Join-Path $env:LOCALAPPDATA 'HomeWatch'
$ConfigPath = Join-Path $DataDir 'config.json'
$EventsPath = Join-Path $DataDir 'events.jsonl'
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

function Read-Config {
    if (-not (Test-Path $ConfigPath)) { return $null }
    Get-Content $ConfigPath -Raw | ConvertFrom-Json
}

function Save-Config($Config) {
    $Config | ConvertTo-Json -Depth 8 | Set-Content $ConfigPath -Encoding UTF8
}

function Protect-Password([string]$Password) {
    ConvertTo-SecureString $Password -AsPlainText -Force | ConvertFrom-SecureString
}

function Unprotect-Password([string]$Protected) {
    $secure = ConvertTo-SecureString $Protected
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

function Invoke-AdGuard([string]$Path) {
    $cfg = Read-Config
    if (-not $cfg -or -not $cfg.baseUrl) { throw 'HomeWatch is not configured.' }
    $password = Unprotect-Password $cfg.protectedPassword
    $pair = '{0}:{1}' -f $cfg.username, $password
    $auth = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pair))
    $headers = @{ Authorization = "Basic $auth" }
    Invoke-RestMethod -Uri ($cfg.baseUrl.TrimEnd('/') + '/control/' + $Path.TrimStart('/')) -Headers $headers -Method Get -TimeoutSec 15
}

$AdultSites = @(
    'xvideos.com','xnxx.com','pornhub.com','youporn.com','redtube.com','tube8.com',
    'xhamster.com','spankbang.com','beeg.com','tnaflix.com','eporner.com','hqporner.com',
    'chaturbate.com','stripchat.com','livejasmin.com','myfreecams.com','cam4.com',
    'myteenwebcam.com','sexxxgif.com','txnhh.com','xvv1deos.com'
)
$AdultCdns = @(
    'xvideos-cdn.com','xnxx-cdn.com','phncdn.com','pornhub.org','xhcdn.com',
    'storageimagedisplay.com','sx.cdn.live'
)
$BypassDomains = @(
    'dns.google','cloudflare-dns.com','mozilla.cloudflare-dns.com','dns.quad9.net',
    'dns.nextdns.io','mask.icloud.com','mask-h2.icloud.com','mask-api.icloud.com'
)

function Test-Suffix([string]$Domain, [string[]]$List) {
    foreach ($item in $List) {
        if ($Domain -eq $item -or $Domain.EndsWith('.' + $item)) { return $true }
    }
    return $false
}

function Classify-Domain([string]$Domain) {
    $d = $Domain.TrimEnd('.').ToLowerInvariant()
    if (Test-Suffix $d $AdultSites) {
        $stream = $d -match '(^|\.)(mp4|hls|video|media)(-|\.)'
        return [ordered]@{ category='adult'; evidence= $(if ($stream) {'stream'} else {'direct'}); confidence=95; label='Direct adult site' }
    }
    if (Test-Suffix $d $AdultCdns) {
        if ($d -match '(^|\.)(mp4|hls|video|media)(-|\.)') {
            return [ordered]@{ category='adult'; evidence='stream'; confidence=88; label='Adult video delivery' }
        }
        if ($d -match '(^|\.)(thumb|thumbs|profile|assets)(-|\.)') {
            return [ordered]@{ category='adult'; evidence='asset'; confidence=62; label='Adult thumbnail/profile asset' }
        }
        return [ordered]@{ category='adult'; evidence='cdn'; confidence=55; label='Adult content server' }
    }
    if (Test-Suffix $d $BypassDomains) {
        return [ordered]@{ category='bypass'; evidence='dns'; confidence=70; label='Encrypted DNS or privacy relay' }
    }
    return [ordered]@{ category='other'; evidence='domain'; confidence=0; label='Other' }
}

function Normalize-Query($row) {
    $domain = if ($row.question.host) { $row.question.host } elseif ($row.question.name) { $row.question.name } else { $row.domain }
    $client = if ($row.client) { $row.client } elseif ($row.client_info.ip) { $row.client_info.ip } else { 'unknown' }
    $when = if ($row.time) { [DateTimeOffset]::Parse($row.time).ToLocalTime() } else { [DateTimeOffset]::Now }
    $kind = Classify-Domain ([string]$domain)
    [ordered]@{
        id = ('{0}|{1}|{2}|{3}' -f $when.ToString('o'),$client,$domain,$row.question.type)
        time = $when.ToString('o')
        client = [string]$client
        domain = ([string]$domain).TrimEnd('.').ToLowerInvariant()
        queryType = [string]$row.question.type
        status = if ($row.reason -and $row.reason -notmatch '^NotFiltered') { 'blocked' } else { 'processed' }
        category = $kind.category
        evidence = $kind.evidence
        confidence = $kind.confidence
        label = $kind.label
    }
}

function Sync-Events {
    $result = Invoke-AdGuard 'querylog?limit=500'
    $rows = if ($result.data) { @($result.data) } elseif ($result.entries) { @($result.entries) } else { @() }
    $existing = @{}
    if (Test-Path $EventsPath) {
        Get-Content $EventsPath -Tail 10000 | ForEach-Object {
            try { $x = $_ | ConvertFrom-Json; $existing[$x.id] = $true } catch {}
        }
    }
    $new = New-Object Collections.Generic.List[object]
    foreach ($row in $rows) {
        $event = Normalize-Query $row
        if (-not $existing.ContainsKey($event.id)) { $new.Add($event); $existing[$event.id] = $true }
    }
    if ($new.Count -gt 0) {
        $new | Sort-Object time | ForEach-Object { $_ | ConvertTo-Json -Compress } | Add-Content $EventsPath -Encoding UTF8
    }
    return $new.Count
}

function Get-Events([int]$Hours = 24) {
    $cutoff = [DateTimeOffset]::Now.AddHours(-$Hours)
    $cfg = Read-Config
    $aliases = @{}
    if ($cfg -and $cfg.aliases) { $cfg.aliases.psobject.Properties | ForEach-Object { $aliases[$_.Name] = $_.Value } }
    $items = New-Object Collections.Generic.List[object]
    if (Test-Path $EventsPath) {
        Get-Content $EventsPath -Tail 50000 | ForEach-Object {
            try {
                $e = $_ | ConvertFrom-Json
                if ([DateTimeOffset]::Parse($e.time) -ge $cutoff -and $e.category -ne 'other') {
                    $e | Add-Member -NotePropertyName clientName -NotePropertyValue $(if ($aliases.ContainsKey($e.client)) {$aliases[$e.client]} else {$e.client}) -Force
                    $items.Add($e)
                }
            } catch {}
        }
    }
    return @($items | Sort-Object time -Descending)
}

function Get-Sessions([object[]]$Events) {
    $adult = @($Events | Where-Object category -eq 'adult' | Sort-Object client,time)
    $sessions = New-Object Collections.Generic.List[object]
    foreach ($group in ($adult | Group-Object client)) {
        $current = $null
        foreach ($e in $group.Group) {
            $t = [DateTimeOffset]::Parse($e.time)
            if (-not $current -or ($t - $current.end).TotalMinutes -gt 20) {
                if ($current) { $sessions.Add($current) }
                $current = [pscustomobject]@{ client=$e.client; clientName=$e.clientName; start=$t; end=$t; events=New-Object Collections.Generic.List[object] }
            }
            $current.end = $t; $current.events.Add($e)
        }
        if ($current) { $sessions.Add($current) }
    }
    foreach ($s in $sessions) {
        $direct = @($s.events | Where-Object evidence -eq 'direct').Count
        $streams = @($s.events | Where-Object evidence -eq 'stream').Count
        $domains = @($s.events.domain | Sort-Object -Unique)
        $assessment = if ($streams -gt 0 -and $direct -gt 0) {'Confirmed browsing with video delivery'} elseif ($direct -gt 0) {'Confirmed adult-site visit'} else {'Adult assets only; may be incidental'}
        [ordered]@{
            client=$s.client; clientName=$s.clientName; start=$s.start.ToString('o'); end=$s.end.ToString('o')
            durationMinutes=[Math]::Max(1,[Math]::Round(($s.end-$s.start).TotalMinutes))
            requests=$s.events.Count; directRequests=$direct; streamRequests=$streams
            domains=$domains; assessment=$assessment
            confidence=$(if ($streams -gt 0 -and $direct -gt 0) {95} elseif ($direct -gt 0) {85} else {55})
        }
    }
}

function Send-Json($Context, $Object, [int]$Status=200) {
    $json = $Object | ConvertTo-Json -Depth 12
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $Context.Response.StatusCode = $Status
    $Context.Response.ContentType = 'application/json; charset=utf-8'
    $Context.Response.ContentLength64 = $bytes.Length
    $Context.Response.OutputStream.Write($bytes,0,$bytes.Length)
    $Context.Response.Close()
}

function Send-File($Context, [string]$Path, [string]$Type) {
    if (-not (Test-Path $Path)) { $Context.Response.StatusCode=404; $Context.Response.Close(); return }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $Context.Response.ContentType=$Type; $Context.Response.ContentLength64=$bytes.Length
    $Context.Response.OutputStream.Write($bytes,0,$bytes.Length); $Context.Response.Close()
}

function Read-Body($Request) {
    $reader = New-Object IO.StreamReader($Request.InputStream,$Request.ContentEncoding)
    try { $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Close() }
}

$listener = New-Object Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Write-Host "HomeWatch is running at http://127.0.0.1:$Port" -ForegroundColor Green
Start-Process "http://127.0.0.1:$Port"

try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext(); $path = $ctx.Request.Url.AbsolutePath
        try {
            if ($path -eq '/' -or $path -eq '/index.html') { Send-File $ctx (Join-Path $Root 'web\index.html') 'text/html; charset=utf-8'; continue }
            if ($path -eq '/app.js') { Send-File $ctx (Join-Path $Root 'web\app.js') 'application/javascript; charset=utf-8'; continue }
            if ($path -eq '/styles.css') { Send-File $ctx (Join-Path $Root 'web\styles.css') 'text/css; charset=utf-8'; continue }
            if ($path -eq '/api/status') {
                $cfg=Read-Config; Send-Json $ctx @{ configured=[bool]$cfg; baseUrl=$(if($cfg){$cfg.baseUrl}else{''}) }; continue
            }
            if ($path -eq '/api/config' -and $ctx.Request.HttpMethod -eq 'POST') {
                $body=Read-Body $ctx.Request; $old=Read-Config
                $aliases = if ($old -and $old.aliases) {$old.aliases} else {[pscustomobject]@{}}
                $cfg=[ordered]@{baseUrl=$body.baseUrl.TrimEnd('/');username=$body.username;protectedPassword=(Protect-Password $body.password);aliases=$aliases}
                Save-Config $cfg
                try { [void](Invoke-AdGuard 'status') }
                catch { Remove-Item $ConfigPath -Force -ErrorAction SilentlyContinue; throw }
                Send-Json $ctx @{ok=$true}; continue
            }
            if ($path -eq '/api/aliases' -and $ctx.Request.HttpMethod -eq 'POST') {
                $body=Read-Body $ctx.Request; $cfg=Read-Config
                $map=[ordered]@{}; $body.aliases.psobject.Properties | ForEach-Object {$map[$_.Name]=$_.Value}
                $cfg.aliases=[pscustomobject]$map; Save-Config $cfg; Send-Json $ctx @{ok=$true}; continue
            }
            if ($path -eq '/api/dashboard') {
                $hours=24; [void][int]::TryParse($ctx.Request.QueryString['hours'],[ref]$hours); if($hours -lt 1){$hours=24}
                $added=Sync-Events; $events=@(Get-Events $hours); $sessions=@(Get-Sessions $events)
                $clients=@($events | Select-Object client,clientName -Unique | Sort-Object clientName)
                Send-Json $ctx @{events=$events;sessions=$sessions;clients=$clients;added=$added;generatedAt=[DateTimeOffset]::Now.ToString('o')}; continue
            }
            $ctx.Response.StatusCode=404; $ctx.Response.Close()
        } catch {
            Send-Json $ctx @{error=$_.Exception.Message} 500
        }
    }
} finally { $listener.Stop(); $listener.Close() }
