#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)),
    [string]$CommandPath = 'HomeWatch2/agent-command.json',
    [string]$StatusPath = 'HomeWatch2/agent-status.json',
    [string]$HeartbeatPath = 'HomeWatch2/agent-heartbeat.json',
    [int]$PollSeconds = 15
)

$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$lastCommandId = $null

function Write-JsonFile([string]$RelativePath, $Value) {
    $full = Join-Path $RepoRoot $RelativePath
    $dir = Split-Path -Parent $full
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $json = $Value | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($full, $json, [Text.UTF8Encoding]::new($false))
}

function Publish-Files([string]$Message, [string[]]$Paths) {
    & git -C $RepoRoot add -- $Paths
    $pending = & git -C $RepoRoot diff --cached --name-only
    if (-not $pending) { return }
    & git -C $RepoRoot commit -m $Message | Out-Null
    & git -C $RepoRoot push | Out-Null
}

function Invoke-LocalAgent([string]$Endpoint) {
    $token = $env:HOMEWATCH_AGENT_TOKEN
    if ([string]::IsNullOrWhiteSpace($token)) { throw 'HOMEWATCH_AGENT_TOKEN is not configured.' }
    Invoke-RestMethod -Uri "http://127.0.0.1:8911$Endpoint" -Headers @{ Authorization = "Bearer $token" } -TimeoutSec 30
}

function Invoke-Command($Command) {
    switch ([string]$Command.action) {
        'diagnostics' { return Invoke-LocalAgent '/api/health' }
        'validate' { return Invoke-LocalAgent '/api/test' }
        'manifest' { return Invoke-LocalAgent '/api/manifest' }
        'dashboard' { return Invoke-RestMethod -Uri 'http://127.0.0.1:8920/api/dashboard?hours=24' -TimeoutSec 30 }
        'devices' { return Invoke-RestMethod -Uri 'http://127.0.0.1:8920/api/devices?hours=24' -TimeoutSec 30 }
        'status' { return Invoke-RestMethod -Uri 'http://127.0.0.1:8920/api/status' -TimeoutSec 30 }
        default { throw "Unsupported relay action: $($Command.action)" }
    }
}

while ($true) {
    try {
        & git -C $RepoRoot pull --ff-only | Out-Null

        $heartbeat = [ordered]@{
            online = $true
            computer = $env:COMPUTERNAME
            generatedUtc = [DateTime]::UtcNow.ToString('o')
            relayVersion = '1.0.0'
            lastCommandId = $lastCommandId
        }
        Write-JsonFile $HeartbeatPath $heartbeat
        Publish-Files 'Relay heartbeat' @($HeartbeatPath)

        $commandFile = Join-Path $RepoRoot $CommandPath
        if (Test-Path $commandFile) {
            $command = Get-Content -LiteralPath $commandFile -Raw | ConvertFrom-Json
            if ($command.id -and $command.id -ne $lastCommandId) {
                $started = [DateTime]::UtcNow
                try {
                    $result = Invoke-Command $command
                    $status = [ordered]@{
                        id = $command.id
                        action = $command.action
                        success = $true
                        startedUtc = $started.ToString('o')
                        completedUtc = [DateTime]::UtcNow.ToString('o')
                        computer = $env:COMPUTERNAME
                        result = $result
                    }
                } catch {
                    $status = [ordered]@{
                        id = $command.id
                        action = $command.action
                        success = $false
                        startedUtc = $started.ToString('o')
                        completedUtc = [DateTime]::UtcNow.ToString('o')
                        computer = $env:COMPUTERNAME
                        error = $_.Exception.Message
                    }
                }
                Write-JsonFile $StatusPath $status
                Publish-Files "Relay response: $($command.id)" @($StatusPath)
                $lastCommandId = $command.id
            }
        }
    } catch {
        Write-Warning $_.Exception.Message
    }
    Start-Sleep -Seconds ([Math]::Max(10, $PollSeconds))
}
