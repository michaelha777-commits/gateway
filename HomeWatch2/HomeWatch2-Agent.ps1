param(
    [switch]$Install,
    [switch]$RunOnce
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$branch = 'homewatch-v2'
$healthUrl = 'http://127.0.0.1:8920/api/status'
$logDir = Join-Path $root 'logs'
$agentLog = Join-Path $logDir 'agent.log'
$appOut = Join-Path $logDir 'homewatch.out.log'
$appErr = Join-Path $logDir 'homewatch.err.log'
$taskName = 'HomeWatch2 Auto Update and Health'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Write-AgentLog([string]$message) {
    $line = "$(Get-Date -Format o) $message"
    Add-Content -Path $agentLog -Value $line -Encoding UTF8
}

function Test-HomeWatchHealth {
    try {
        $response = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 10
        return [bool]$response.ok
    }
    catch { return $false }
}

function Stop-HomeWatch {
    Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match 'HomeWatch2|dotnet\s+run' } |
        ForEach-Object {
            try { Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop } catch { }
        }
    Start-Sleep -Seconds 2
}

function Start-HomeWatch {
    $process = Start-Process -FilePath 'dotnet.exe' `
        -ArgumentList @('run','--no-build','--urls','http://0.0.0.0:8920') `
        -WorkingDirectory $root `
        -WindowStyle Hidden `
        -RedirectStandardOutput $appOut `
        -RedirectStandardError $appErr `
        -PassThru

    for ($i = 0; $i -lt 18; $i++) {
        Start-Sleep -Seconds 2
        if (Test-HomeWatchHealth) {
            Write-AgentLog "HomeWatch healthy. PID=$($process.Id)"
            return $true
        }
        if ($process.HasExited) { break }
    }
    Write-AgentLog 'HomeWatch failed its startup health check.'
    return $false
}

function Invoke-AgentCycle {
    $mutex = New-Object Threading.Mutex($false, 'Global\HomeWatch2UpdateAgent')
    if (-not $mutex.WaitOne(0)) { return }
    try {
        Set-Location $root
        $previous = (& git rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Unable to read the current Git revision.' }

        & git fetch origin $branch --quiet
        if ($LASTEXITCODE -ne 0) { throw 'Git fetch failed.' }
        $remote = (& git rev-parse "origin/$branch").Trim()

        if ($previous -ne $remote) {
            Write-AgentLog "Deploying $remote (previous $previous)."
            Stop-HomeWatch
            & git reset --hard "origin/$branch" | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Git update failed.' }
            & dotnet build --configuration Release --nologo | Add-Content -Path $agentLog
            if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

            if (-not (Start-HomeWatch)) {
                Write-AgentLog "Rolling back to $previous."
                Stop-HomeWatch
                & git reset --hard $previous | Out-Null
                & dotnet build --configuration Release --nologo | Add-Content -Path $agentLog
                if (-not (Start-HomeWatch)) { throw 'Rollback also failed health validation.' }
                throw "Deployment $remote failed and was rolled back to $previous."
            }
            Write-AgentLog "Deployment $remote completed successfully."
        }
        elseif (-not (Test-HomeWatchHealth)) {
            Write-AgentLog 'Health check failed; restarting HomeWatch.'
            Stop-HomeWatch
            & dotnet build --configuration Release --nologo | Add-Content -Path $agentLog
            if (-not (Start-HomeWatch)) { throw 'Health recovery restart failed.' }
        }
    }
    catch {
        Write-AgentLog "ERROR: $($_.Exception.Message)"
    }
    finally {
        try { $mutex.ReleaseMutex() } catch { }
        $mutex.Dispose()
    }
}

if ($Install) {
    $powershell = (Get-Command powershell.exe).Source
    $arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`" -RunOnce"
    $action = New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $root
    $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Settings $settings -Description 'Automatically deploys, restarts, health-checks, and rolls back HomeWatch2.' -Force | Out-Null
    Write-AgentLog 'Scheduled update and health agent installed.'
    exit 0
}

Invoke-AgentCycle
