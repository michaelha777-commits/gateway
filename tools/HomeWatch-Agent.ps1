#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)),
    [string]$BindAddress = '127.0.0.1',
    [int]$Port = 8911,
    [string]$Token = $env:HOMEWATCH_AGENT_TOKEN
)

$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "HomeWatch root not found: $Root" }
if ([string]::IsNullOrWhiteSpace($Token)) { throw 'HOMEWATCH_AGENT_TOKEN is not configured.' }

$allowedFiles = @(
    'HomeWatch.ps1',
    'Validate-HomeWatch.ps1',
    'web\app.js',
    'web\index.html',
    'web\styles.css',
    'config.json',
    'homewatch.log',
    'logs\homewatch.log',
    'logs\error.log'
)

function Send-Json($Context, [int]$Status, $Value) {
    $json = $Value | ConvertTo-Json -Depth 8 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $Context.Response.StatusCode = $Status
    $Context.Response.ContentType = 'application/json; charset=utf-8'
    $Context.Response.ContentLength64 = $bytes.Length
    $Context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
    $Context.Response.OutputStream.Close()
}

function Get-SafeFile([string]$RelativePath) {
    if ($allowedFiles -notcontains $RelativePath) { throw 'File is not in the diagnostics allow-list.' }
    $path = [IO.Path]::GetFullPath((Join-Path $Root $RelativePath))
    if (-not $path.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid path.' }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'File not found.' }
    $item = Get-Item -LiteralPath $path
    [ordered]@{
        path = $RelativePath
        length = $item.Length
        modifiedUtc = $item.LastWriteTimeUtc.ToString('o')
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        content = if ($item.Length -le 1048576) { Get-Content -LiteralPath $path -Raw } else { $null }
        truncated = ($item.Length -gt 1048576)
    }
}

function Get-Health {
    $homeWatch = Get-Process powershell,pwsh -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.CommandLine -match 'HomeWatch\.ps1' } catch { $false }
    }
    $listener = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
        Where-Object LocalPort -in @(8900,8910,8911) |
        Select-Object LocalAddress,LocalPort,OwningProcess
    [ordered]@{
        ok = $true
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        computer = $env:COMPUTERNAME
        root = $Root
        version = if (Test-Path (Join-Path $Root '.git')) { (& git -C $Root rev-parse HEAD 2>$null) } else { $null }
        powershell = $PSVersionTable.PSVersion.ToString()
        processes = @($homeWatch | Select-Object Id,ProcessName,StartTime)
        listeners = @($listener)
        freeDiskBytes = (Get-PSDrive -Name ([IO.Path]::GetPathRoot($Root).Substring(0,1))).Free
    }
}

function Invoke-Validation {
    $validator = Join-Path $Root 'Validate-HomeWatch.ps1'
    if (-not (Test-Path -LiteralPath $validator)) { throw 'Validate-HomeWatch.ps1 is missing.' }
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $validator 2>&1 | ForEach-Object ToString
    [ordered]@{ exitCode = $LASTEXITCODE; passed = ($LASTEXITCODE -eq 0); output = @($output) }
}

$prefix = "http://$BindAddress`:$Port/"
$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add($prefix)
$listener.Start()
Write-Host "HomeWatch diagnostics agent listening on $prefix"

try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        try {
            $auth = $ctx.Request.Headers['Authorization']
            if ($auth -ne "Bearer $Token") { Send-Json $ctx 401 @{ error = 'Unauthorized' }; continue }
            $path = $ctx.Request.Url.AbsolutePath.TrimEnd('/')
            switch ($path) {
                '/api/health' { Send-Json $ctx 200 (Get-Health); continue }
                '/api/test' { Send-Json $ctx 200 (Invoke-Validation); continue }
                '/api/file' {
                    $relative = [Uri]::UnescapeDataString($ctx.Request.QueryString['path'])
                    Send-Json $ctx 200 (Get-SafeFile $relative)
                    continue
                }
                '/api/manifest' {
                    $items = foreach ($file in $allowedFiles) {
                        $full = Join-Path $Root $file
                        if (Test-Path -LiteralPath $full -PathType Leaf) {
                            $item = Get-Item -LiteralPath $full
                            [ordered]@{ path=$file; length=$item.Length; modifiedUtc=$item.LastWriteTimeUtc.ToString('o'); sha256=(Get-FileHash $full -Algorithm SHA256).Hash }
                        }
                    }
                    Send-Json $ctx 200 @{ files=@($items) }
                    continue
                }
                default { Send-Json $ctx 404 @{ error = 'Not found' } }
            }
        } catch {
            try { Send-Json $ctx 500 @{ error = $_.Exception.Message } } catch {}
        }
    }
} finally {
    $listener.Stop()
    $listener.Close()
}
