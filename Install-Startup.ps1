#requires -RunAsAdministrator
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$cmd = Join-Path $root 'Start-HomeWatch.cmd'
$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument ('/c "{0}"' -f $cmd)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries
Register-ScheduledTask -TaskName 'HomeWatch' -Action $action -Trigger $trigger -Settings $settings -Description 'Local AdGuard Home activity dashboard' -Force | Out-Null
Write-Host 'HomeWatch will now start automatically when you sign in.' -ForegroundColor Green
