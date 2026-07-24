#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)),
    [string]$TaskName = 'HomeWatch GitHub Relay'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$relay = Join-Path $RepoRoot 'tools\HomeWatch-GitHub-Relay.ps1'
if (-not (Test-Path -LiteralPath $relay)) { throw "Relay script not found: $relay" }
if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot '.git'))) { throw "Not a Git repository: $RepoRoot" }

# Confirm this Windows account can push before installing an unattended task.
& git -C $RepoRoot fetch | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Git authentication failed. Sign in to GitHub for this repository first.' }

$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$relay`" -RepoRoot `"$RepoRoot`""
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Host "Installed and started scheduled task: $TaskName"
