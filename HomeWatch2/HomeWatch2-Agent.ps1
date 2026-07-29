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
$credentialFile = Join-Path $root '.homewatch-credentials.json'
$taskName = 'HomeWatch2 Auto Update and Health'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

function Write-AgentLog([string]$message) {
    $line = "$(Get-Date -Format o) $message"
    Add-Content -Path $agentLog -Value $line -Encoding UTF8
}

function Convert-SecureStringToPlainText([Security.SecureString]$SecureString) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Import-HomeWatchEnvironment {
    if (-not (Test-Path $credentialFile)) {
        throw 'HomeWatch credentials have not been saved yet. Start HomeWatch2 once interactively.'
    }

    $saved = Get-Content $credentialFile -Raw | ConvertFrom-Json
    $securePassword = [string]$saved.Password | ConvertTo-SecureString
    $env:AdGuard__BaseUrl = 'http://127.0.0.1'
    $env:AdGuard__Username = [string]$saved.Username
    $env:AdGuard__Password = Convert-SecureStringToPlainText $securePassword
    $env:Ntfy__BaseUrl = 'https://ntfy.sh'
    $env:Ntfy__Topic = [string]$saved.NtfyTopic
}

function Test-HomeWatchHealth {
    try {
        $status = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 10
        if (-not [bool]$status.ok) { return $false }

        # The dashboard, device list, and investigations all depend on these routes.
        # Checking them prevents a stale backend from being considered healthy merely
        # because its static SPA shell and the basic status endpoint still respond.
        $requiredApiUrls = @(
            'http://127.0.0.1:8920/api/dashboard',
            'http://127.0.0.1:8920/api/devices',
            'http://127.0.0.1:8920/api/activity?pageSize=1',
            'http://127.0.0.1:8920/api/activity/summary'
        )
        foreach ($url in $requiredApiUrls) {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 10
            $contentType = [string]$response.Headers['Content-Type']
            if ($response.StatusCode -ne 200 -or $contentType -notmatch '^application/json(?:;|$)') {
                Write-AgentLog "Health contract failed for $url (status=$($response.StatusCode), content-type=$contentType)."
                return $false
            }
            $null = $response.Content | ConvertFrom-Json
        }
        return $true
    }
    catch {
        Write-AgentLog "Health contract failed: $($_.Exception.Message)"
        return $false
    }
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
    Import-HomeWatchEnvironment
    $process = Start-Process -FilePath 'dotnet.exe' `
        -ArgumentList @('run','--no-build','--configuration','Release','--urls','http://0.0.0.0:8920') `
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
