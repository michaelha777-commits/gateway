#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $MyInvocation.MyCommand.Path),
    [int]$Port = 8911,
    [string]$TaskName = 'HomeWatch Diagnostics Agent'
)

$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this installer as Administrator.'
}

$agent = Join-Path $Root 'tools\HomeWatch-Agent.ps1'
if (-not (Test-Path -LiteralPath $agent)) { throw "Agent not found: $agent" }

$tokenPath = Join-Path $Root 'data\agent-token.txt'
New-Item -ItemType Directory -Path (Split-Path -Parent $tokenPath) -Force | Out-Null
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$token = [Convert]::ToBase64String($bytes)
Set-Content -LiteralPath $tokenPath -Value $token -Encoding ASCII
& icacls.exe $tokenPath /inheritance:r /grant:r "${env:USERNAME}:(R,W)" 'SYSTEM:(F)' | Out-Null

$runner = Join-Path $Root 'tools\Start-HomeWatch-Agent.ps1'
$runnerContent = @"
`$ErrorActionPreference = 'Stop'
`$env:HOMEWATCH_AGENT_TOKEN = (Get-Content -LiteralPath '$($tokenPath.Replace("'","''"))' -Raw).Trim()
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File '$($agent.Replace("'","''"))' -Root '$($Root.Replace("'","''"))' -Port $Port
"@
Set-Content -LiteralPath $runner -Value $runnerContent -Encoding UTF8

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$runner`""
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -RestartCount 5 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName

$ruleName = 'HomeWatch Diagnostics Agent (Local Only)'
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Block -Protocol TCP -LocalPort $Port -Profile Any | Out-Null

Write-Host 'HomeWatch diagnostics agent installed.' -ForegroundColor Green
Write-Host "Local endpoint: http://127.0.0.1:$Port/api/health"
Write-Host "Token file: $tokenPath"
Write-Host 'The agent is restricted to localhost and an explicit file allow-list.'
