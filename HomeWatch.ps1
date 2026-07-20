#requires -Version 5.1
param([int]$Port = 8765)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$DataDir = Join-Path $env:LOCALAPPDATA 'HomeWatch'
$ConfigPath = Join-Path $DataDir 'config.json'
$EventsPath = Join-Path $DataDir 'events.jsonl'
$CategoriesPath = Join-Path $DataDir 'categories.json'
$MaintenancePath = Join-Path $DataDir 'maintenance.json'
$LastMaintenance = [DateTimeOffset]::MinValue
New-Item -ItemType Directory -Path $DataDir -Force | Out-Null

function Read-Config {
    if (-not (Test-Path $ConfigPath)) { return $null }
    $cfg = Get-Content $ConfigPath -Raw | ConvertFrom-Json
    if (-not ($cfg.psobject.Properties.Name -contains 'retentionDays')) { $cfg | Add-Member retentionDays 90 }
    if (-not ($cfg.psobject.Properties.Name -contains 'categoryListUrl')) { $cfg | Add-Member categoryListUrl '' }
    if (-not ($cfg.psobject.Properties.Name -contains 'autoUpdateCategories')) { $cfg | Add-Member autoUpdateCategories $true }
    if (-not ($cfg.psobject.Properties.Name -contains 'ignoredDomains')) { $cfg | Add-Member ignoredDomains @() }
    if (-not ($cfg.psobject.Properties.Name -contains 'discoveredNames')) { $cfg | Add-Member discoveredNames ([pscustomobject]@{}) }
    if (-not ($cfg.psobject.Properties.Name -contains 'discoveredMacs')) { $cfg | Add-Member discoveredMacs ([pscustomobject]@{}) }
    if (-not ($cfg.psobject.Properties.Name -contains 'macAliases')) { $cfg | Add-Member macAliases ([pscustomobject]@{}) }
    return $cfg
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

function Update-Categories([bool]$Force = $false) {
    $cfg = Read-Config
    if (-not $cfg -or (-not $cfg.autoUpdateCategories -and -not $Force) -or [string]::IsNullOrWhiteSpace($cfg.categoryListUrl)) { return $false }
    $last = [DateTimeOffset]::MinValue
    if (Test-Path $MaintenancePath) {
        try { $last = [DateTimeOffset]::Parse((Get-Content $MaintenancePath -Raw | ConvertFrom-Json).categoriesUpdatedAt) } catch {}
    }
    if (-not $Force -and ([DateTimeOffset]::Now - $last).TotalHours -lt 24) { return $false }
    $download = Invoke-RestMethod -Uri $cfg.categoryListUrl -Method Get -TimeoutSec 20
    foreach ($name in @('adultSites','adultCdns','bypassDomains')) {
        if (-not ($download.psobject.Properties.Name -contains $name) -or $download.$name -is [string]) { throw "Category feed is missing a valid $name list." }
    }
    $download | ConvertTo-Json -Depth 6 | Set-Content $CategoriesPath -Encoding UTF8
    @{categoriesUpdatedAt=[DateTimeOffset]::Now.ToString('o')} | ConvertTo-Json | Set-Content $MaintenancePath -Encoding UTF8
    return $true
}

function Import-Categories {
    if (-not (Test-Path $CategoriesPath)) { return }
    try {
        $custom = Get-Content $CategoriesPath -Raw | ConvertFrom-Json
        $script:AdultSites = @($script:AdultSites + @($custom.adultSites) | ForEach-Object {$_.ToString().Trim().ToLowerInvariant()} | Where-Object {$_} | Sort-Object -Unique)
        $script:AdultCdns = @($script:AdultCdns + @($custom.adultCdns) | ForEach-Object {$_.ToString().Trim().ToLowerInvariant()} | Where-Object {$_} | Sort-Object -Unique)
        $script:BypassDomains = @($script:BypassDomains + @($custom.bypassDomains) | ForEach-Object {$_.ToString().Trim().ToLowerInvariant()} | Where-Object {$_} | Sort-Object -Unique)
    } catch { Write-Warning "Could not load category list: $($_.Exception.Message)" }
}

function Invoke-Maintenance {
    $cfg = Read-Config
    if (-not $cfg) { return }
    try { [void](Update-Categories) } catch { Write-Warning "Category update failed: $($_.Exception.Message)" }
    Import-Categories
    if (-not (Test-Path $EventsPath)) { return }
    $days = [Math]::Max(1,[Math]::Min(3650,[int]$cfg.retentionDays))
    $cutoff = [DateTimeOffset]::Now.AddDays(-$days)
    $temp = $EventsPath + '.tmp'
    Get-Content $EventsPath | ForEach-Object {
        try { if ([DateTimeOffset]::Parse(($_ | ConvertFrom-Json).time) -ge $cutoff) { $_ } } catch {}
    } | Set-Content $temp -Encoding UTF8
    Move-Item $temp $EventsPath -Force
}

function Test-Suffix([string]$Domain, [string[]]$List) {
    foreach ($item in $List) {
        if ($Domain -eq $item -or $Domain.EndsWith('.' + $item)) { return $true }
    }
    return $false
}

function Get-DomainDescription([string]$Domain) {
    $d = $Domain.TrimEnd('.').ToLowerInvariant()
    $descriptions = [ordered]@{
        'aaplimg.com'='Apple content-delivery network for images, software, and service assets'
        'apple.com'='Apple website or device service'
        'icloud.com'='Apple iCloud service'
        'googleapis.com'='Google application programming interface or browser service'
        'gstatic.com'='Google static files such as scripts, fonts, or images'
        'google.com'='Google web or device service'
        'microsoft.com'='Microsoft software, account, or device service'
        'microsoftonline.com'='Microsoft account and sign-in service'
        'office.com'='Microsoft 365 or Office service'
        'office365.com'='Microsoft 365 service'
        'spotify.com'='Spotify music and account service'
        'spotifycdn.com'='Spotify media delivery network'
        'steamserver.net'='Steam game platform network service'
        'cloudflare-dns.com'='Cloudflare encrypted DNS resolver'
        'dns.google'='Google encrypted DNS resolver'
        'onetrust.com'='Website privacy and cookie-consent service'
        'sentry-cdn.com'='Application error-monitoring script delivery'
        'avast.com'='Avast security software service'
        'avg.com'='AVG security software service'
    }
    foreach ($suffix in $descriptions.Keys) {
        if ($d -eq $suffix -or $d.EndsWith('.' + $suffix)) { return $descriptions[$suffix] }
    }
    if (Test-Suffix $d $AdultSites) { return 'Adult-content website domain' }
    if (Test-Suffix $d $AdultCdns) { return 'Adult-content media or asset delivery domain' }
    return 'Domain contacted by an application or webpage; DNS alone does not identify which one'
}

function Invoke-ProcessText([string]$FileName, [string]$Arguments, [int]$TimeoutMs = 1500) {
    $p = New-Object Diagnostics.Process
    $p.StartInfo = New-Object Diagnostics.ProcessStartInfo
    $p.StartInfo.FileName = $FileName; $p.StartInfo.Arguments = $Arguments
    $p.StartInfo.UseShellExecute = $false; $p.StartInfo.CreateNoWindow = $true
    $p.StartInfo.RedirectStandardOutput = $true; $p.StartInfo.RedirectStandardError = $true
    try {
        [void]$p.Start()
        if (-not $p.WaitForExit($TimeoutMs)) { try {$p.Kill()} catch {}; return '' }
        return $p.StandardOutput.ReadToEnd()
    } catch { return '' } finally { $p.Dispose() }
}

function Get-ArpTable([string[]]$IPs) {
    foreach ($ip in $IPs) {
        if ($ip -match '^\d{1,3}(\.\d{1,3}){3}$') { [void](Invoke-ProcessText 'ping.exe' "-n 1 -w 200 $ip" 700) }
    }
    $table = @{}
    $text = Invoke-ProcessText 'arp.exe' '-a' 2500
    foreach ($line in ($text -split "\r?\n")) {
        if ($line -match '^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})\s+') {
            $table[$matches[1]] = $matches[2].ToUpperInvariant().Replace('-',':')
        }
    }
    return $table
}

function Resolve-DeviceName([string]$IP) {
    $name = ''
    try {
        $task = [Net.Dns]::GetHostEntryAsync($IP)
        if ($task.Wait(1200) -and $task.Result.HostName -and $task.Result.HostName -ne $IP) { $name = $task.Result.HostName.TrimEnd('.') }
    } catch {}
    if (-not $name -and $IP -match '^\d{1,3}(\.\d{1,3}){3}$') {
        $text = Invoke-ProcessText 'nbtstat.exe' "-A $IP" 1400
        $line = $text -split "\r?\n" | Where-Object {$_ -match '<00>\s+UNIQUE'} | Select-Object -First 1
        if ($line -match '^\s*([^\s<]+)') { $name = $matches[1] }
    }
    return $name
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
        description = Get-DomainDescription ([string]$domain)
    }
}

function Sync-Events {
    if (([DateTimeOffset]::Now - $script:LastMaintenance).TotalHours -ge 1) {
        Invoke-Maintenance
        $script:LastMaintenance = [DateTimeOffset]::Now
    }
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
    $macAliases = @{}
    if ($cfg -and $cfg.macAliases) { $cfg.macAliases.psobject.Properties | ForEach-Object { $macAliases[$_.Name.ToUpperInvariant()] = $_.Value } }
    $discovered = @{}
    if ($cfg -and $cfg.discoveredNames) { $cfg.discoveredNames.psobject.Properties | ForEach-Object { $discovered[$_.Name] = $_.Value } }
    $macs = @{}
    if ($cfg -and $cfg.discoveredMacs) { $cfg.discoveredMacs.psobject.Properties | ForEach-Object { $macs[$_.Name] = $_.Value } }
    $ignored = @{}
    if ($cfg -and $cfg.ignoredDomains) {
        @($cfg.ignoredDomains) | ForEach-Object {
            $ignored[([string]$_).Trim().TrimEnd('.').ToLowerInvariant()] = $true
        }
    }
    $items = New-Object Collections.Generic.List[object]
    $script:LastEventReadErrors = 0
    if (Test-Path $EventsPath) {
        Get-Content $EventsPath -Tail 50000 | ForEach-Object {
            try {
                $e = $_ | ConvertFrom-Json
                if ([DateTimeOffset]::Parse($e.time) -ge $cutoff) {
                    $domainKey = ([string]$e.domain).Trim().TrimEnd('.').ToLowerInvariant()
                    try {
                        $kind = Classify-Domain $domainKey
                        foreach ($property in @('category','evidence','confidence','label')) {
                            $e | Add-Member -NotePropertyName $property -NotePropertyValue $kind[$property] -Force
                        }
                        $e | Add-Member -NotePropertyName description -NotePropertyValue (Get-DomainDescription $domainKey) -Force
                    } catch {
                        if (-not $e.description) { $e | Add-Member description 'Domain description unavailable' -Force }
                    }
                    $e | Add-Member -NotePropertyName ignored -NotePropertyValue ([bool]$ignored.ContainsKey($domainKey)) -Force
                    $clientKey = [string]$e.client
                    $mac = if ($macs.ContainsKey($clientKey)) {[string]$macs[$clientKey]} else {''}
                    $macKey = $mac.ToUpperInvariant()
                    $displayName = if ($macKey -and $macAliases.ContainsKey($macKey)) {$macAliases[$macKey]} elseif ($aliases.ContainsKey($clientKey)) {$aliases[$clientKey]} elseif ($discovered.ContainsKey($clientKey)) {$discovered[$clientKey]} else {$clientKey}
                    $e | Add-Member -NotePropertyName clientName -NotePropertyValue $displayName -Force
                    $e | Add-Member -NotePropertyName mac -NotePropertyValue $mac -Force
                    $items.Add($e)
                }
            } catch { $script:LastEventReadErrors++ }
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

function Get-Alerts([object[]]$Events, [object[]]$Sessions) {
    $alerts = New-Object Collections.Generic.List[object]
    foreach ($s in ($Sessions | Sort-Object start -Descending | Select-Object -First 20)) {
        if ($s.confidence -ge 85) {
            $alerts.Add([ordered]@{time=$s.start;client=$s.client;clientName=$s.clientName;severity='high';kind='adult-session';title='Adult activity session';detail=$s.assessment;confidence=$s.confidence})
        }
    }
    return @($alerts | Sort-Object time -Descending | Select-Object -First 50)
}

function Send-Json($Context, $Object, [int]$Status=200) {
    $json = $Object | ConvertTo-Json -Depth 12
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $Context.Response.StatusCode = $Status
    $Context.Response.ContentType = 'application/json; charset=utf-8'
    $Context.Response.Headers['Cache-Control'] = 'no-store, no-cache, must-revalidate'
    $Context.Response.Headers['Pragma'] = 'no-cache'
    $Context.Response.ContentLength64 = $bytes.Length
    $Context.Response.OutputStream.Write($bytes,0,$bytes.Length)
    $Context.Response.Close()
}

function Send-File($Context, [string]$Path, [string]$Type) {
    if (-not (Test-Path $Path)) { $Context.Response.StatusCode=404; $Context.Response.Close(); return }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $Context.Response.ContentType=$Type
    $Context.Response.Headers['Cache-Control']='no-store, no-cache, must-revalidate'
    $Context.Response.Headers['Pragma']='no-cache'
    $Context.Response.ContentLength64=$bytes.Length
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
                $macMap=[ordered]@{}
                if ($body.macAliases) { $body.macAliases.psobject.Properties | ForEach-Object {$macMap[$_.Name.ToUpperInvariant()]=$_.Value} }
                $cfg.aliases=[pscustomobject]$map; $cfg.macAliases=[pscustomobject]$macMap
                Save-Config $cfg; Send-Json $ctx @{ok=$true}; continue
            }
            if ($path -eq '/api/settings' -and $ctx.Request.HttpMethod -eq 'GET') {
                $cfg=Read-Config; Send-Json $ctx @{retentionDays=$cfg.retentionDays;categoryListUrl=$cfg.categoryListUrl;autoUpdateCategories=$cfg.autoUpdateCategories;adGuardUrl=$cfg.baseUrl}; continue
            }
            if ($path -eq '/api/settings' -and $ctx.Request.HttpMethod -eq 'POST') {
                $body=Read-Body $ctx.Request; $cfg=Read-Config
                $cfg.retentionDays=[Math]::Max(1,[Math]::Min(3650,[int]$body.retentionDays))
                $cfg.categoryListUrl=[string]$body.categoryListUrl
                $cfg.autoUpdateCategories=[bool]$body.autoUpdateCategories
                Save-Config $cfg
                if ($body.updateNow) { [void](Update-Categories $true); Import-Categories }
                Send-Json $ctx @{ok=$true}; continue
            }
            if ($path -eq '/api/ignored' -and $ctx.Request.HttpMethod -eq 'POST') {
                $body=Read-Body $ctx.Request; $cfg=Read-Config
                $domain=([string]$body.domain).Trim().TrimEnd('.').ToLowerInvariant()
                if ([string]::IsNullOrWhiteSpace($domain)) { throw 'A domain is required.' }
                $list=@($cfg.ignoredDomains | ForEach-Object {([string]$_).Trim().TrimEnd('.').ToLowerInvariant()} | Where-Object {$_ -and $_ -ne $domain})
                if ([bool]$body.ignored) { $list=@($list + $domain) }
                $cfg.ignoredDomains=@($list | Sort-Object -Unique); Save-Config $cfg
                Send-Json $ctx @{ok=$true;ignoredDomains=$cfg.ignoredDomains}; continue
            }
            if ($path -eq '/api/discover' -and $ctx.Request.HttpMethod -eq 'POST') {
                $cfg=Read-Config; $map=[ordered]@{}; $macMap=[ordered]@{}
                if ($cfg.discoveredNames) { $cfg.discoveredNames.psobject.Properties | ForEach-Object {$map[$_.Name]=$_.Value} }
                if ($cfg.discoveredMacs) { $cfg.discoveredMacs.psobject.Properties | ForEach-Object {$macMap[$_.Name]=$_.Value} }
                $ips=@(Get-Events 720 | Select-Object -ExpandProperty client -Unique)
                $arp=Get-ArpTable $ips
                foreach ($ip in $ips) {
                    $found=Resolve-DeviceName ([string]$ip)
                    if (-not [string]::IsNullOrWhiteSpace($found)) { $map[[string]$ip]=$found }
                    if ($arp.ContainsKey([string]$ip)) { $macMap[[string]$ip]=$arp[[string]$ip] }
                }
                $cfg.discoveredNames=[pscustomobject]$map; $cfg.discoveredMacs=[pscustomobject]$macMap; Save-Config $cfg
                Send-Json $ctx @{ok=$true;discoveredNames=$cfg.discoveredNames;discoveredMacs=$cfg.discoveredMacs}; continue
            }
            if ($path -eq '/api/dashboard') {
                $hours=24; [void][int]::TryParse($ctx.Request.QueryString['hours'],[ref]$hours); if($hours -lt 1){$hours=24}
                $added=Sync-Events; $events=@(Get-Events $hours); $sessions=@(Get-Sessions $events)
                $clients=@($events | Select-Object client,clientName,mac -Unique | Sort-Object clientName); $alerts=@(Get-Alerts $events $sessions)
                Send-Json $ctx @{events=$events;sessions=$sessions;alerts=$alerts;clients=$clients;added=$added;eventReadErrors=$script:LastEventReadErrors;generatedAt=[DateTimeOffset]::Now.ToString('o')}; continue
            }
            $ctx.Response.StatusCode=404; $ctx.Response.Close()
        } catch {
            Send-Json $ctx @{error=$_.Exception.Message} 500
        }
    }
} finally { $listener.Stop(); $listener.Close() }
