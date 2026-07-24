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

# The task must run as the currently authenticated Windows user because GitHub
# credentials are normally stored in that user's credential manager.
& git -C $RepoRoot fetch | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Git authentication failed. Sign in to GitHub for this repository first.' }

$userId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$relay`" -RepoRoot `"$RepoRoot`""
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
$principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Highest

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Host "Installed and started scheduled task: $TaskName"
