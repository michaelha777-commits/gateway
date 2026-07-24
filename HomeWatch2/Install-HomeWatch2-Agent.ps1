#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Root,
    [string]$TaskName = 'HomeWatch2 Private Relay Agent'
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Root)) {
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $Root = $PSScriptRoot
    }
    elseif ($MyInvocation.MyCommand.Path) {
        $Root = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    else {
        $Root = (Get-Location).Path
    }
}
$Root = [IO.Path]::GetFullPath($Root)

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw 'Administrator rights are required.' }

$agent = Join-Path $Root 'tools\HomeWatch2-RelayAgent.ps1'
if (-not (Test-Path -LiteralPath $agent)) { throw "Agent not found: $agent" }

$runner = Join-Path $Root 'tools\Start-HomeWatch2-RelayAgent.ps1'
@"
`$ErrorActionPreference = 'Stop'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File '$($agent.Replace("'","''"))' -Root '$($Root.Replace("'","''"))'
"@ | Set-Content -LiteralPath $runner -Encoding UTF8

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$runner`"" -WorkingDirectory $Root
$trigger1 = New-ScheduledTaskTrigger -AtStartup
$trigger2 = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($trigger1,$trigger2) -Settings $settings -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName

Start-Sleep -Seconds 2
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
if ($task.State -eq 'Disabled') { throw 'The scheduled task was created but is disabled.' }

Write-Host ''
Write-Host 'HomeWatch2 private relay agent installed and started.' -ForegroundColor Green
Write-Host "Root: $Root"
Write-Host 'It accepts only: status, diagnostics, restart, and update.'
Write-Host 'No inbound firewall port or public tunnel is used.'
