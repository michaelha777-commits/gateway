#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)),
    [string]$Branch = 'homewatch-v2',
    [switch]$Once,
    [switch]$NoGit
)

$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$repoRoot = [IO.Path]::GetFullPath((Join-Path $Root '..'))
$commandPath = Join-Path $Root 'agent-command.json'
$statusPath = Join-Path $Root 'agent-status.json'
$statePath = Join-Path $Root 'data\agent-state.json'
$credentialPath = Join-Path $Root '.homewatch-credentials.json'
$healthUrl = 'http://127.0.0.1:8920/api/status'
New-Item -ItemType Directory -Force -Path (Split-Path $statePath -Parent) | Out-Null

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null }
}
function Convert-SecureStringToPlainText([Security.SecureString]$SecureString) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
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
    $project = Join-Path $Root 'HomeWatch2.csproj'
    if (-not (Test-Path -LiteralPath $project)) { throw 'HomeWatch2.csproj is missing.' }

    $saved = Read-JsonFile $credentialPath
    if ($saved) {
        $env:AdGuard__BaseUrl = 'http://127.0.0.1'
        $env:AdGuard__Username = [string]$saved.Username
        if ($saved.Password) {
            $securePassword = [string]$saved.Password | ConvertTo-SecureString
            $env:AdGuard__Password = Convert-SecureStringToPlainText $securePassword
        }
        $env:Ntfy__BaseUrl = 'https://ntfy.sh'
        $env:Ntfy__Topic = [string]$saved.NtfyTopic
    }

    $stdout = Join-Path $Root 'logs\relay-homewatch.stdout.log'
    $stderr = Join-Path $Root 'logs\relay-homewatch.stderr.log'
    New-Item -ItemType Directory -Force -Path (Split-Path $stdout -Parent) | Out-Null
    Start-Process dotnet.exe -WindowStyle Hidden -WorkingDirectory $Root -ArgumentList @('run','--configuration','Release','--no-launch-profile') -RedirectStandardOutput $stdout -RedirectStandardError $stderr | Out-Null

    for ($i=0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 2
        $health = Test-Health
        if ($null -ne $health) { return $health }
    }
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
    if ($NoGit) { return }
    & git -C $repoRoot add -- 'HomeWatch2/agent-status.json' | Out-Null
    & git -C $repoRoot commit -m "HomeWatch2 agent response $($Status.commandId)" | Out-Null
    if ($LASTEXITCODE -eq 0) { & git -C $repoRoot push origin $Branch --quiet | Out-Null }
}
function Invoke-AgentCycle {
    if (-not $NoGit) { & git -C $repoRoot pull --ff-only origin $Branch --quiet | Out-Null }
    $state = Read-JsonFile $statePath
    $lastId = if ($state) { [string]$state.lastCommandId } else { '' }
    $command = Read-JsonFile $commandPath
    if (-not $command -or -not $command.id -or [string]$command.id -eq $lastId) { return }

    $id = [string]$command.id
    $action = ([string]$command.action).ToLowerInvariant()
    $started = [DateTime]::UtcNow
    $result = $null
    $ok = $true
    $errorMessage = $null
    try {
        switch ($action) {
            'status' { $result = Test-Health; if ($null -eq $result) { throw 'HomeWatch2 status endpoint is unreachable.' } }
            'restart' { Stop-HomeWatch2; $result = Start-HomeWatch2 }
            'update' { Sync-Repository; Stop-HomeWatch2; $result = Start-HomeWatch2 }
            'diagnostics' {
                $result = [ordered]@{
                    health = Test-Health
                    revision = if (-not $NoGit) { (& git -C $repoRoot rev-parse HEAD).Trim() } else { $null }
                    listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object LocalPort -eq 8920 | Select-Object LocalAddress,LocalPort,OwningProcess)
                    dotnet = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue | Select-Object ProcessId,CommandLine)
                    diskFreeBytes = (Get-PSDrive -Name ([IO.Path]::GetPathRoot($Root).Substring(0,1))).Free
                }
            }
            default { throw "Unsupported action '$action'. Allowed: status, restart, update, diagnostics." }
        }
    } catch { $ok = $false; $errorMessage = $_.Exception.Message }

    $status = [ordered]@{
        commandId = $id; action = $action; ok = $ok
        startedUtc = $started.ToString('o'); completedUtc = [DateTime]::UtcNow.ToString('o')
        computer = $env:COMPUTERNAME; result = $result; error = $errorMessage
    }
    @{ lastCommandId=$id; processedUtc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
    Publish-Status $status
}

do {
    try { Invoke-AgentCycle } catch { if ($Once) { throw }; Start-Sleep -Seconds 20 }
    if (-not $Once) { Start-Sleep -Seconds 15 }
} while (-not $Once)
