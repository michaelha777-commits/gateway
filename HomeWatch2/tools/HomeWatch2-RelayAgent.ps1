#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)),
    [string]$Branch = 'homewatch-v2'
)

$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $Root '..'))
$commandPath = Join-Path $Root 'agent-command.json'
$statusPath = Join-Path $Root 'agent-status.json'
$statePath = Join-Path $Root 'data\agent-state.json'
$healthUrl = 'http://127.0.0.1:8920/api/status'
New-Item -ItemType Directory -Force -Path (Split-Path $statePath -Parent) | Out-Null

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null }
}
function Test-Health {
    try { return Invoke-RestMethod -Uri $healthUrl -TimeoutSec 10 } catch { return $null }
}
function Stop-HomeWatch2 {
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'HomeWatch2|8920' } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force } catch {} }
    Start-Sleep -Seconds 2
}
function Start-HomeWatch2 {
    $launcher = Join-Path $Root 'Start-HomeWatch2.ps1'
    if (-not (Test-Path $launcher)) { throw 'Start-HomeWatch2.ps1 is missing.' }
    Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$launcher`"") -WorkingDirectory $Root
    for ($i=0; $i -lt 20; $i++) { Start-Sleep 2; $h=Test-Health; if ($h -and $h.ok) { return $h } }
    throw 'HomeWatch2 did not become healthy after restart.'
}
function Sync-Repository {
    & git -C $repoRoot fetch origin $Branch --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Git fetch failed.' }
    & git -C $repoRoot reset --hard "origin/$Branch" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Git update failed.' }
}
function Publish-Status($Status) {
    $Status | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $statusPath -Encoding UTF8
    & git -C $repoRoot add -- 'HomeWatch2/agent-status.json' | Out-Null
    & git -C $repoRoot commit -m "HomeWatch2 agent response $($Status.commandId)" | Out-Null
    if ($LASTEXITCODE -eq 0) { & git -C $repoRoot push origin $Branch --quiet | Out-Null }
}

$state = Read-JsonFile $statePath
$lastId = if ($state) { [string]$state.lastCommandId } else { '' }

while ($true) {
    try {
        & git -C $repoRoot pull --ff-only origin $Branch --quiet | Out-Null
        $command = Read-JsonFile $commandPath
        if ($command -and $command.id -and [string]$command.id -ne $lastId) {
            $id = [string]$command.id
            $action = ([string]$command.action).ToLowerInvariant()
            $started = [DateTime]::UtcNow
            $result = $null
            $ok = $true
            $errorMessage = $null
            try {
                switch ($action) {
                    'status' { $result = Test-Health }
                    'restart' { Stop-HomeWatch2; $result = Start-HomeWatch2 }
                    'update' { Sync-Repository; Stop-HomeWatch2; $result = Start-HomeWatch2 }
                    'diagnostics' {
                        $health = Test-Health
                        $result = [ordered]@{
                            health = $health
                            revision = (& git -C $repoRoot rev-parse HEAD).Trim()
                            listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object LocalPort -in @(8920) | Select-Object LocalAddress,LocalPort,OwningProcess)
                            dotnet = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue | Select-Object ProcessId,CommandLine)
                            diskFreeBytes = (Get-PSDrive -Name ([IO.Path]::GetPathRoot($Root).Substring(0,1))).Free
                        }
                    }
                    default { throw "Unsupported action '$action'. Allowed: status, restart, update, diagnostics." }
                }
            } catch { $ok = $false; $errorMessage = $_.Exception.Message }
            $status = [ordered]@{
                commandId = $id
                action = $action
                ok = $ok
                startedUtc = $started.ToString('o')
                completedUtc = [DateTime]::UtcNow.ToString('o')
                computer = $env:COMPUTERNAME
                result = $result
                error = $errorMessage
            }
            $lastId = $id
            @{ lastCommandId=$lastId; processedUtc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
            Publish-Status $status
        }
    } catch {
        Start-Sleep -Seconds 20
    }
    Start-Sleep -Seconds 15
}
