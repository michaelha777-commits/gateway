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
$EventViewCache = @{}
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
    if (-not ($cfg.psobject.Properties.Name -contains 'protectedVirusTotalApiKey')) { $cfg | Add-Member protectedVirusTotalApiKey '' }
    if (-not ($cfg.psobject.Properties.Name -contains 'protectedNtfyTopicUrl')) { $cfg | Add-Member protectedNtfyTopicUrl '' }
    if (-not ($cfg.psobject.Properties.Name -contains 'acknowledgedAlerts')) { $cfg | Add-Member acknowledgedAlerts @() }
    if (-not ($cfg.psobject.Properties.Name -contains 'notifiedSessions')) { $cfg | Add-Member notifiedSessions @() }
    if (-not ($cfg.psobject.Properties.Name -contains 'alertAuditLog')) { $cfg | Add-Member alertAuditLog @() }
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

function Invoke-AdGuard([string]$Path,[int]$TimeoutSec=15) {
    $cfg = Read-Config
    if (-not $cfg -or -not $cfg.baseUrl) { throw 'HomeWatch is not configured.' }
    $password = Unprotect-Password $cfg.protectedPassword
    $pair = '{0}:{1}' -f $cfg.username, $password
    $auth = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($pair))
    $headers = @{ Authorization = "Basic $auth" }
    Invoke-RestMethod -Uri ($cfg.baseUrl.TrimEnd('/') + '/control/' + $Path.TrimStart('/')) -Headers $headers -Method Get -TimeoutSec $TimeoutSec
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
    try { if(([Uri]$cfg.categoryListUrl).Host -eq 'example.com'){return $false} } catch { return $false }
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
        'amazonaws.com'='Amazon Web Services cloud infrastructure'
        'cloudfront.net'='Amazon CloudFront content-delivery network'
        'media-amazon.com'='Amazon retail media, images, and application assets'
        'amazon-adsystem.com'='Amazon advertising and measurement service'
        'amazon.com'='Amazon shopping, account, device, or application service'
        'amazon'='Amazon-owned service infrastructure under its restricted .amazon brand domain'
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
        'snapchat.com'='Snapchat application programming interface, messaging, media, or account service'
        'sc-cdn.net'='Snapchat content-delivery network for application media and assets'
        'snapkit.com'='Snapchat developer and sign-in integration service'
        'tiktok.com'='TikTok website, application, or account service'
        'tiktokv.com'='TikTok application and video-delivery infrastructure'
        'tiktokcdn.com'='TikTok content-delivery network for videos and application assets'
        'tiktokcdn-us.com'='TikTok content-delivery network for videos and application assets'
        'muscdn.com'='TikTok/ByteDance media content-delivery network'
        'byteoversea.com'='ByteDance infrastructure used by TikTok and related services'
        'ibytedtos.com'='ByteDance/TikTok data and content-delivery infrastructure'
        'ibyteimg.com'='ByteDance/TikTok image and asset delivery infrastructure'
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
    $inferred=Get-DomainIdentity $d
    return ($inferred.category + $(if ($inferred.confidence -lt 80) {' (inferred from hostname)'} else {''}))
}

function Get-DomainIdentity([string]$Domain) {
    $d=$Domain.TrimEnd('.').ToLowerInvariant()
    if ($d -eq 'amazon' -or $d.EndsWith('.amazon')) { return [ordered]@{owner='Amazon';category='Amazon services / technology infrastructure';confidence=100;basis='Restricted brand domain'} }
    if (Test-Suffix $d @('amazon.com','media-amazon.com','amazon-adsystem.com')) { return [ordered]@{owner='Amazon';category='Shopping, devices, media, or advertising';confidence=95;basis='Recognized service domain'} }
    if (Test-Suffix $d @('amazonaws.com','cloudfront.net')) { return [ordered]@{owner='Amazon Web Services';category='Cloud hosting or content delivery';confidence=95;basis='Recognized service domain'} }
    if (Test-Suffix $d @('tiktok.com','tiktokv.com','tiktokcdn.com','tiktokcdn-us.com','muscdn.com','byteoversea.com','ibytedtos.com','ibyteimg.com')) { return [ordered]@{owner='TikTok / ByteDance';category='Social media, application, or video delivery';confidence=95;basis='Recognized service domain'} }
    if (Test-Suffix $d @('snapchat.com','sc-cdn.net','snapkit.com')) { return [ordered]@{owner='Snap';category='Social media, messaging, or media delivery';confidence=95;basis='Recognized service domain'} }
    if (Test-Suffix $d @('apple.com','icloud.com','aaplimg.com')) { return [ordered]@{owner='Apple';category='Device, cloud, or content-delivery service';confidence=95;basis='Recognized service domain'} }
    if (Test-Suffix $d @('google.com','googleapis.com','gstatic.com')) { return [ordered]@{owner='Google';category='Web, application, or device service';confidence=95;basis='Recognized service domain'} }
    if (Test-Suffix $d @('microsoft.com','microsoftonline.com','office.com','office365.com')) { return [ordered]@{owner='Microsoft';category='Software, account, or productivity service';confidence=95;basis='Recognized service domain'} }
    $category='Application or web service'; $basis='General hostname classification'; $confidence=45
    if ($d -match '(^|[.-])(ads?|adservice|analytics|metrics|telemetry|tracking|tracker|pixel)([.-]|$)') { $category='Advertising, analytics, or tracking';$confidence=70 }
    elseif ($d -match '(^|[.-])(cdn|edge|cache|static|assets?|images?|img)([0-9]*[.-]|[.-]|$)') { $category='Content delivery, images, or static assets';$confidence=65 }
    elseif ($d -match '(^|[.-])(video|vod|hls|stream|media|mp4)([0-9]*[.-]|[.-]|$)') { $category='Video or media delivery';$confidence=70 }
    elseif ($d -match '(^|[.-])(api|graph|gateway|service)([0-9]*[.-]|[.-]|$)') { $category='Application programming interface or backend service';$confidence=65 }
    elseif ($d -match '(^|[.-])(auth|login|account|identity|oauth|sso)([0-9]*[.-]|[.-]|$)') { $category='Authentication or account service';$confidence=70 }
    elseif ($d -match '(^|[.-])(push|notify|notification|messaging)([0-9]*[.-]|[.-]|$)') { $category='Push notification or messaging service';$confidence=65 }
    elseif ($d -match '(^|[.-])(update|updates|download)([0-9]*[.-]|[.-]|$)') { $category='Software update or download service';$confidence=65 }
    elseif ($d -match '(^|[.-])(mail|smtp|imap|email)([0-9]*[.-]|[.-]|$)') { $category='Email service';$confidence=70 }
    return [ordered]@{owner='Not established';category=$category;confidence=$confidence;basis=$basis}
}

function Get-RegistrableDomain([string]$Domain) {
    $d=$Domain.Trim().TrimEnd('.').ToLowerInvariant(); $labels=@($d -split '\.')
    if ($labels.Count -le 2) { return $d }
    $twoLevelSuffixes=@('co.uk','org.uk','ac.uk','gov.uk','com.au','net.au','org.au','co.nz','co.jp','co.kr','co.in','com.br','com.mx','com.cn','com.sg','com.tr','co.za','com.ar','com.tw','com.hk')
    $lastTwo=$labels[-2]+'.'+$labels[-1]
    if ($twoLevelSuffixes -contains $lastTwo -and $labels.Count -ge 3) { return $labels[-3]+'.'+$lastTwo }
    return $lastTwo
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
        if ($d -match '(^|\.)(mp4|hls|video|media|stream|vod|fck-cl\d+)(-|\.)') { return [ordered]@{category='adult';evidence='stream';confidence=88;label='Adult video delivery'} }
        if ($d -match '(^|\.)(thumb|thumbs|profile|assets|asset|images|image|img|cdnl|static)(-|\.)') { return [ordered]@{category='adult';evidence='asset';confidence=68;label='Adult thumbnail or page asset'} }
        if ($d -match '(^|\.)(vast|ads?|adserver|promo|pop|crmkt|campaign|track|pixel)(-|\.)') { return [ordered]@{category='adult';evidence='ad';confidence=65;label='Adult advertising or marketing service'} }
        foreach($site in $AdultSites){if($d -eq $site -or $d -eq ('www.'+$site)){return [ordered]@{category='adult';evidence='direct';confidence=95;label='Direct adult site'}}}
        return [ordered]@{category='adult';evidence='cdn';confidence=58;label='Adult-site supporting service'}
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
    $identity = Get-DomainIdentity ([string]$domain)
    $answerValues=New-Object Collections.Generic.List[string];$answerTypes=New-Object Collections.Generic.List[string]
    foreach($answer in @($row.answer)){
        $value=if($answer.value){[string]$answer.value}elseif($answer.data){[string]$answer.data}elseif($answer.address){[string]$answer.address}else{''}
        if($value -and -not $answerValues.Contains($value)){$answerValues.Add($value)}
        if($answer.type){$type=[string]$answer.type;if(-not $answerTypes.Contains($type)){$answerTypes.Add($type)}}
    }
    [ordered]@{
        id = ('{0}|{1}|{2}|{3}' -f $when.ToString('o'),$client,$domain,$row.question.type)
        time = $when.ToString('o')
        client = [string]$client
        domain = ([string]$domain).TrimEnd('.').ToLowerInvariant()
        queryType = [string]$row.question.type
        responseTypes = @($answerTypes)
        responseValues = @($answerValues)
        status = if ($row.reason -and $row.reason -notmatch '^NotFiltered') { 'blocked' } else { 'processed' }
        category = $kind.category
        evidence = $kind.evidence
        confidence = $kind.confidence
        label = $kind.label
        description = Get-DomainDescription ([string]$domain)
        serviceOwner = $identity.owner
        serviceCategory = $identity.category
        serviceCategoryConfidence = $identity.confidence
    }
}

function Sync-Events([int]$TimeoutSec=5) {
    if (([DateTimeOffset]::Now - $script:LastMaintenance).TotalHours -ge 1) {
        Invoke-Maintenance
        $script:LastMaintenance = [DateTimeOffset]::Now
    }
    $result = Invoke-AdGuard 'querylog?limit=500' $TimeoutSec
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
    $eventStamp=if(Test-Path $EventsPath){(Get-Item $EventsPath).LastWriteTimeUtc.Ticks}else{0};$configStamp=if(Test-Path $ConfigPath){(Get-Item $ConfigPath).LastWriteTimeUtc.Ticks}else{0}
    $cacheKey=('{0}|{1}|{2}' -f $Hours,$eventStamp,$configStamp)
    if($script:EventViewCache.ContainsKey($cacheKey)){return @($script:EventViewCache[$cacheKey])}
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
                        $identity=Get-DomainIdentity $domainKey
                        $e | Add-Member -NotePropertyName serviceOwner -NotePropertyValue $identity.owner -Force
                        $e | Add-Member -NotePropertyName serviceCategory -NotePropertyValue $identity.category -Force
                        $e | Add-Member -NotePropertyName serviceCategoryConfidence -NotePropertyValue $identity.confidence -Force
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
    $result=@($items | Sort-Object time -Descending);$script:EventViewCache=@{$cacheKey=$result};return $result
}

function Get-Sessions([object[]]$Events) {
    $adult = @($Events | Where-Object {$_.category -eq 'adult' -and -not $_.ignored} | Sort-Object client,time)
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
        $ordered=@($s.events | Sort-Object time)
        $directEvents=@($ordered | Where-Object evidence -eq 'direct'); $streamEvents=@($ordered | Where-Object evidence -eq 'stream'); $assetEvents=@($ordered | Where-Object {$_.evidence -in @('asset','cdn')});$adEvents=@($ordered|Where-Object evidence -eq 'ad')
        $direct=$directEvents.Count; $streams=$streamEvents.Count; $domains=@($ordered.domain | Sort-Object -Unique)
        $logical=@{}; foreach ($e in $ordered) { $t=[DateTimeOffset]::Parse($e.time);$bucket=[Math]::Floor($t.ToUnixTimeSeconds()/2);$logical[($e.domain+'|'+$bucket)]=$true }
        $firstDirect=if($directEvents.Count){[DateTimeOffset]::Parse($directEvents[0].time)}else{$null}
        $firstStream=if($streamEvents.Count){[DateTimeOffset]::Parse($streamEvents[0].time)}else{$null}
        $lastStream=if($streamEvents.Count){[DateTimeOffset]::Parse($streamEvents[-1].time)}else{$null}
        $playbackEpisodes=0;$priorStream=$null
        foreach($e in $streamEvents){$t=[DateTimeOffset]::Parse($e.time);if(-not $priorStream -or ($t-$priorStream).TotalSeconds -gt 120){$playbackEpisodes++};$priorStream=$t}
        $assetBursts=0;$priorAsset=$null
        foreach($e in $assetEvents){$t=[DateTimeOffset]::Parse($e.time);if(-not $priorAsset -or ($t-$priorAsset).TotalSeconds -gt 10){$assetBursts++};$priorAsset=$t}
        $streamWindowSeconds=if($firstStream){[Math]::Max(0,[Math]::Round(($lastStream-$firstStream).TotalSeconds))}else{0}
        $timeToStreamSeconds=if($firstDirect -and $firstStream){[Math]::Max(0,[Math]::Round(($firstStream-$firstDirect).TotalSeconds))}else{$null}
        $directDomains=@($directEvents.domain | Sort-Object -Unique);$streamDomains=@($streamEvents.domain | Sort-Object -Unique);$assetDomains=@($assetEvents.domain | Sort-Object -Unique);$adDomains=@($adEvents.domain|Sort-Object -Unique)
        $assessment = if ($streams -gt 0 -and $direct -gt 0) {'Confirmed browsing with video delivery'} elseif ($direct -gt 0) {'Confirmed adult-site visit'} else {'Adult assets only; may be incidental'}
        $narrative=if($firstDirect -and $firstStream){('A direct visit to {0} was followed by media-delivery DNS about {1} seconds later. Media-related DNS continued across a {2}-second window. {3} possible playback phase(s) and {4} asset-loading burst(s) were detected.' -f ($directDomains -join ', '),$timeToStreamSeconds,$streamWindowSeconds,$playbackEpisodes,$assetBursts)}elseif($firstDirect){('A direct adult-site visit was detected for {0}, without a recognized video-delivery hostname in this export.' -f ($directDomains -join ', '))}else{'Only adult-related asset or delivery domains were detected; the initiating page is not present in this export.'}
        [ordered]@{
            client=$s.client; clientName=$s.clientName; start=$s.start.ToString('o'); end=$s.end.ToString('o')
            durationMinutes=[Math]::Max(1,[Math]::Ceiling(($s.end-$s.start).TotalMinutes));durationSeconds=[Math]::Round(($s.end-$s.start).TotalSeconds)
            requests=$s.events.Count;logicalContacts=$logical.Count;directRequests=$direct;streamRequests=$streams;assetRequests=$assetEvents.Count;advertisingRequests=$adEvents.Count
            domains=$domains;directDomains=$directDomains;streamDomains=$streamDomains;assetDomains=$assetDomains;advertisingDomains=$adDomains
            firstDirectAt=$(if($firstDirect){$firstDirect.ToString('o')}else{$null});firstStreamAt=$(if($firstStream){$firstStream.ToString('o')}else{$null});lastStreamAt=$(if($lastStream){$lastStream.ToString('o')}else{$null})
            timeToFirstStreamSeconds=$timeToStreamSeconds;streamWindowSeconds=$streamWindowSeconds;possiblePlaybackPhases=$playbackEpisodes;assetLoadingBursts=$assetBursts;possibleNavigationChanges=[Math]::Max(0,$assetBursts-1)
            assessment=$assessment;narrative=$narrative
            confidence=$(if ($streams -gt 0 -and $direct -gt 0) {95} elseif ($direct -gt 0) {85} else {55})
        }
    }
}

function Get-SessionId($Session) {
    $raw=('{0}|{1}' -f $Session.client,$Session.start)
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $hash=$sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($raw)); return (($hash | ForEach-Object {$_.ToString('x2')}) -join '').Substring(0,20) }
    finally { $sha.Dispose() }
}

function Get-Alerts([object[]]$Events, [object[]]$Sessions) {
    $alerts = New-Object Collections.Generic.List[object]
    $cfg=Read-Config;$ack=@{};$sent=@{}
    if($cfg){@($cfg.acknowledgedAlerts)|ForEach-Object{if($_.id){$ack[[string]$_.id]=[string]$_.acknowledgedAt}};@($cfg.notifiedSessions)|ForEach-Object{if($_.id){$sent[[string]$_.id]=[string]$_.sentAt}}}
    foreach ($s in ($Sessions | Sort-Object start -Descending | Select-Object -First 20)) {
        if ($s.confidence -ge 85) {
            $id=Get-SessionId $s
            $alerts.Add([ordered]@{id=$id;time=$s.start;client=$s.client;clientName=$s.clientName;severity='high';kind='adult-session';title='Adult activity session';detail=$s.assessment;confidence=$s.confidence;acknowledged=$ack.ContainsKey($id);acknowledgedAt=$(if($ack.ContainsKey($id)){$ack[$id]}else{$null});notificationSent=$sent.ContainsKey($id);notificationSentAt=$(if($sent.ContainsKey($id)){$sent[$id]}else{$null})})
        }
    }
    return @($alerts | Sort-Object time -Descending | Select-Object -First 50)
}

function Send-SessionNotifications([object[]]$Sessions) {
    $cfg=Read-Config
    if(-not $cfg -or [string]::IsNullOrWhiteSpace([string]$cfg.protectedNtfyTopicUrl)){return}
    $url=Unprotect-Password ([string]$cfg.protectedNtfyTopicUrl);$sent=@{};@($cfg.notifiedSessions)|ForEach-Object{if($_.id){$sent[[string]$_.id]=$true}}
    $records=New-Object Collections.Generic.List[object];@($cfg.notifiedSessions)|ForEach-Object{$records.Add($_)};$changed=$false
    $cutoff=[DateTimeOffset]::Now.AddMinutes(-10)
    foreach($s in ($Sessions|Where-Object{$_.confidence -ge 85 -and [DateTimeOffset]::Parse($_.start) -ge $cutoff})){
        $id=Get-SessionId $s;if($sent.ContainsKey($id)){continue}
        $message=('{0} began an adult-content session at {1}. {2}% confidence. {3}' -f $s.clientName,([DateTimeOffset]::Parse($s.start).ToString('h:mm:ss tt')),$s.confidence,$s.assessment)
        Invoke-RestMethod -Uri $url -Method Post -ContentType 'text/plain; charset=utf-8' -Headers @{Title='HomeWatch adult-session alert';Priority='high';Tags='warning'} -Body $message -TimeoutSec 5|Out-Null
        $now=[DateTimeOffset]::Now.ToString('o');$records.Add([pscustomobject]@{id=$id;sentAt=$now});$sent[$id]=$true;$changed=$true
    }
    if($changed){$cfg.notifiedSessions=@($records|Select-Object -Last 500);Save-Config $cfg}
}

function Invoke-BackgroundCheck {
    try {$added=Sync-Events 3;if($added -gt 0){$events=@(Get-Events 24);$sessions=@(Get-Sessions $events);Send-SessionNotifications $sessions}}
    catch {Write-Warning "Background alert check failed: $($_.Exception.Message)"}
}

function Send-Json($Context, $Object, [int]$Status=200) {
    try {
        $json = $Object | ConvertTo-Json -Depth 12
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        $Context.Response.StatusCode = $Status
        $Context.Response.ContentType = 'application/json; charset=utf-8'
        $Context.Response.Headers['Cache-Control'] = 'no-store, no-cache, must-revalidate'
        $Context.Response.Headers['Pragma'] = 'no-cache'
        $Context.Response.ContentLength64 = $bytes.Length
        $Context.Response.OutputStream.Write($bytes,0,$bytes.Length)
        $Context.Response.Close()
    } catch {
        try {$Context.Response.Abort()} catch {}
    }
}

function Send-File($Context, [string]$Path, [string]$Type) {
    try {
        if (-not (Test-Path $Path)) { $Context.Response.StatusCode=404; $Context.Response.Close(); return }
        $bytes = [IO.File]::ReadAllBytes($Path)
        $Context.Response.ContentType=$Type
        $Context.Response.Headers['Cache-Control']='no-store, no-cache, must-revalidate'
        $Context.Response.Headers['Pragma']='no-cache'
        $Context.Response.ContentLength64=$bytes.Length
        $Context.Response.OutputStream.Write($bytes,0,$bytes.Length); $Context.Response.Close()
    } catch { try {$Context.Response.Abort()} catch {} }
}

function Read-Body($Request) {
    $reader = New-Object IO.StreamReader($Request.InputStream,$Request.ContentEncoding)
    try { $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Close() }
}

function Get-ExternalDomainInfo([string]$Domain) {
    $domain = $Domain.Trim().TrimEnd('.').ToLowerInvariant()
    if ($domain -notmatch '^[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?$') { throw 'Invalid domain name.' }
    $localDescription=Get-DomainDescription $domain; $identity=Get-DomainIdentity $domain; $parentDomain=Get-RegistrableDomain $domain
    $info = [ordered]@{domain=$domain;parentDomain=$parentDomain;description=$localDescription;owner=$identity.owner;localCategory=$identity.category;categoryConfidence=$identity.confidence;categoryBasis=$identity.basis;source='urlscan.io';observed=$false;summary='No historical public scan was found for this domain.'}
    try {
        $query = [Uri]::EscapeDataString('domain:' + $domain)
        $response = Invoke-RestMethod -Uri ('https://urlscan.io/api/v1/search/?size=1&q=' + $query) -Method Get -TimeoutSec 15 -Headers @{'User-Agent'='HomeWatch/1.0'}
        $result = @($response.results) | Select-Object -First 1
        if ($result) {
            $page = $result.page; $verdict = $result.verdicts.overall
            $info.observed=$true; $info.title=[string]$page.title; $info.ip=[string]$page.ip
            $info.country=[string]$page.country; $info.server=[string]$page.server
            $info.scannedAt=[string]$result.task.time; $info.malicious=[bool]$verdict.malicious
            $info.summary=$(if ($verdict.malicious) {'urlscan has flagged at least one public scan as potentially malicious.'} else {'No malicious verdict was reported for the latest public scan.'})
        }
    } catch { $info.summary='urlscan information is temporarily unavailable.' }

    $cfg = Read-Config
    if ($cfg -and -not [string]::IsNullOrWhiteSpace([string]$cfg.protectedVirusTotalApiKey)) {
        $apiKey = Unprotect-Password ([string]$cfg.protectedVirusTotalApiKey)
        $categories = New-Object Collections.Generic.List[string]; $a=$null; $lastStatus=0; $categoryLookupDomain=''
        $targets=@($domain); if ($parentDomain -ne $domain) { $targets+= $parentDomain }
        foreach ($target in $targets) {
            try {
                $vt = Invoke-RestMethod -Uri ('https://www.virustotal.com/api/v3/domains/' + [Uri]::EscapeDataString($target)) -Headers @{'x-apikey'=$apiKey;'User-Agent'='HomeWatch/1.0'} -Method Get -TimeoutSec 20
                if (-not $a) { $a=$vt.data.attributes; $info.virusTotalLookupDomain=$target }
                if ($vt.data.attributes.categories) {
                    $vt.data.attributes.categories.psobject.Properties | ForEach-Object {
                        $value=([string]$_.Value).Trim(); if ($value -and -not $categories.Contains($value)) { $categories.Add($value) }
                    }
                    if ($categories.Count -gt 0) { $categoryLookupDomain=$target }
                }
                if ($categories.Count -gt 0) { break }
            } catch { try { $lastStatus=[int]$_.Exception.Response.StatusCode } catch {} }
        }
        if ($a) {
            $stats=$a.last_analysis_stats
            $info.source='VirusTotal + urlscan.io'; $info.virusTotal=$true
            $info.categories=@($categories | Select-Object -First 12)
            if ($categories.Count -gt 0) {
                $info.inferredCategory=$info.localCategory
                $info.localCategory=(@($categories | Select-Object -First 4) -join ', ')
                $info.categoryConfidence=85; $info.categoryBasis=('VirusTotal provider reports for ' + $categoryLookupDomain)
            }
            $info.reputation=[int]$a.reputation; $info.registrar=[string]$a.registrar
            $info.analysis=[ordered]@{malicious=[int]$stats.malicious;suspicious=[int]$stats.suspicious;harmless=[int]$stats.harmless;undetected=[int]$stats.undetected}
        } else {
            $info.virusTotal=$false
            $info.virusTotalError=$(if ($lastStatus -eq 401) {'VirusTotal rejected the saved API key.'} elseif ($lastStatus -eq 429) {'VirusTotal free-tier quota is temporarily exhausted.'} else {'VirusTotal has no report for this hostname or its parent domain.'})
        }
    } else { $info.virusTotal=$false; $info.virusTotalError='Add a VirusTotal API key in Data settings for category details.' }
    return $info
}

$listener = New-Object Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Write-Host "HomeWatch is running at http://127.0.0.1:$Port" -ForegroundColor Green
Start-Process "http://127.0.0.1:$Port"

try {
    $nextBackgroundCheck=[DateTimeOffset]::Now.AddSeconds(2)
    while ($listener.IsListening) {
        if([DateTimeOffset]::Now -ge $nextBackgroundCheck){Invoke-BackgroundCheck;$nextBackgroundCheck=[DateTimeOffset]::Now.AddSeconds(3)}
        $pending=$listener.BeginGetContext($null,$null)
        while(-not $pending.AsyncWaitHandle.WaitOne(1000)){
            if([DateTimeOffset]::Now -ge $nextBackgroundCheck){Invoke-BackgroundCheck;$nextBackgroundCheck=[DateTimeOffset]::Now.AddSeconds(3)}
        }
        $ctx = $listener.EndGetContext($pending); $path = $ctx.Request.Url.AbsolutePath
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
                $cfg=Read-Config; Send-Json $ctx @{retentionDays=$cfg.retentionDays;categoryListUrl=$cfg.categoryListUrl;autoUpdateCategories=$cfg.autoUpdateCategories;adGuardUrl=$cfg.baseUrl;virusTotalConfigured=(-not [string]::IsNullOrWhiteSpace([string]$cfg.protectedVirusTotalApiKey));ntfyConfigured=(-not [string]::IsNullOrWhiteSpace([string]$cfg.protectedNtfyTopicUrl))}; continue
            }
            if ($path -eq '/api/settings' -and $ctx.Request.HttpMethod -eq 'POST') {
                $body=Read-Body $ctx.Request; $cfg=Read-Config
                $cfg.retentionDays=[Math]::Max(1,[Math]::Min(3650,[int]$body.retentionDays))
                $cfg.categoryListUrl=[string]$body.categoryListUrl
                $cfg.autoUpdateCategories=[bool]$body.autoUpdateCategories
                if ([bool]$body.removeVirusTotalApiKey) { $cfg.protectedVirusTotalApiKey='' }
                elseif (-not [string]::IsNullOrWhiteSpace([string]$body.virusTotalApiKey)) { $cfg.protectedVirusTotalApiKey=Protect-Password (([string]$body.virusTotalApiKey).Trim()) }
                if ([bool]$body.removeNtfyTopicUrl) { $cfg.protectedNtfyTopicUrl='' }
                elseif (-not [string]::IsNullOrWhiteSpace([string]$body.ntfyTopicUrl)) {
                    $ntfyUri=[Uri](([string]$body.ntfyTopicUrl).Trim())
                    if($ntfyUri.Scheme -ne 'https' -or [string]::IsNullOrWhiteSpace($ntfyUri.Host)){throw 'The ntfy topic must be a complete HTTPS URL.'}
                    $cfg.protectedNtfyTopicUrl=Protect-Password ($ntfyUri.AbsoluteUri.TrimEnd('/'))
                }
                Save-Config $cfg
                if ($body.updateNow) { [void](Update-Categories $true); Import-Categories }
                Send-Json $ctx @{ok=$true}; continue
            }
            if ($path -eq '/api/notifications/test' -and $ctx.Request.HttpMethod -eq 'POST') {
                $cfg=Read-Config;if([string]::IsNullOrWhiteSpace([string]$cfg.protectedNtfyTopicUrl)){throw 'Save an ntfy topic URL first.'}
                $url=Unprotect-Password ([string]$cfg.protectedNtfyTopicUrl)
                Invoke-RestMethod -Uri $url -Method Post -ContentType 'text/plain; charset=utf-8' -Headers @{Title='HomeWatch test notification';Priority='default';Tags='white_check_mark'} -Body 'HomeWatch phone notifications are working.' -TimeoutSec 15|Out-Null
                Send-Json $ctx @{ok=$true};continue
            }
            if ($path -eq '/api/alerts/acknowledge' -and $ctx.Request.HttpMethod -eq 'POST') {
                $body=Read-Body $ctx.Request;$cfg=Read-Config;$id=([string]$body.id).Trim();if($id -notmatch '^[a-f0-9]{20}$'){throw 'Invalid alert identifier.'}
                $records=New-Object Collections.Generic.List[object];@($cfg.acknowledgedAlerts)|ForEach-Object{if($_.id -ne $id){$records.Add($_)}}
                $at=[DateTimeOffset]::Now.ToString('o');if([bool]$body.acknowledged){$records.Add([pscustomobject]@{id=$id;acknowledgedAt=$at})}
                $audit=New-Object Collections.Generic.List[object];@($cfg.alertAuditLog)|ForEach-Object{$audit.Add($_)}
                $audit.Add([pscustomobject]@{id=$id;action=$(if([bool]$body.acknowledged){'acknowledged'}else{'reopened'});at=$at;client=[string]$body.client;clientName=[string]$body.clientName;alertTime=[string]$body.alertTime;title=[string]$body.title;detail=[string]$body.detail})
                $cfg.acknowledgedAlerts=@($records|Select-Object -Last 500);$cfg.alertAuditLog=@($audit|Select-Object -Last 1000);Save-Config $cfg;Send-Json $ctx @{ok=$true;acknowledgedAt=$at};continue
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
            if ($path -eq '/api/domain-info' -and $ctx.Request.HttpMethod -eq 'GET') {
                $domain=[string]$ctx.Request.QueryString['domain']
                Send-Json $ctx (Get-ExternalDomainInfo $domain); continue
            }
            if ($path -eq '/api/dashboard') {
                $hours=24; [void][int]::TryParse($ctx.Request.QueryString['hours'],[ref]$hours); if($hours -lt 1){$hours=24}
                $added=0; $events=@(Get-Events $hours); $sessions=@(Get-Sessions $events)
                $clients=@($events | Select-Object client,clientName,mac -Unique | Sort-Object clientName); $alerts=@(Get-Alerts $events $sessions)
                $cfg=Read-Config;$audit=@($cfg.alertAuditLog|Sort-Object at -Descending|Select-Object -First 100)
                Send-Json $ctx @{events=$events;sessions=$sessions;alerts=$alerts;alertAuditLog=$audit;clients=$clients;added=$added;eventReadErrors=$script:LastEventReadErrors;generatedAt=[DateTimeOffset]::Now.ToString('o')}; continue
            }
            $ctx.Response.StatusCode=404; $ctx.Response.Close()
        } catch {
            try { Send-Json $ctx @{error=$_.Exception.Message} 500 } catch { try {$ctx.Response.Abort()} catch {} }
        }
    }
} finally { $listener.Stop(); $listener.Close() }
