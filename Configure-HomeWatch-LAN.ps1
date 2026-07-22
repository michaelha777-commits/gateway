#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Enable','Disable','Status')]
    [string]$Action = 'Enable',

    [ValidateRange(1024,65535)]
    [int]$Port = 8900
)

$ErrorActionPreference = 'Stop'
$ruleName = "HomeWatch LAN TCP $Port"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from PowerShell as Administrator.'
    }
}

function Show-Status {
    Write-Host "`nFirewall rule:" -ForegroundColor Cyan
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue |
        Select-Object DisplayName,Enabled,Profile,Direction,Action |
        Format-Table -AutoSize

    Write-Host "Port proxy:" -ForegroundColor Cyan
    netsh interface portproxy show v4tov4

    Write-Host "`nHomeWatch must still be running locally on http://127.0.0.1:$Port" -ForegroundColor Yellow
    Write-Host 'From another device, browse to http://<THIS-PC-LAN-IP>:'$Port -ForegroundColor Yellow
}

Assert-Administrator

switch ($Action) {
    'Enable' {
        Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue

        New-NetFirewallRule `
            -DisplayName $ruleName `
            -Direction Inbound `
            -Action Allow `
            -Protocol TCP `
            -LocalPort $Port `
            -Profile Private `
            -RemoteAddress LocalSubnet | Out-Null

        netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=$Port 2>$null | Out-Null
        netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=$Port connectaddress=127.0.0.1 connectport=$Port | Out-Null

        Set-Service iphlpsvc -StartupType Automatic
        Start-Service iphlpsvc

        Write-Host "HomeWatch LAN access enabled on TCP port $Port for the Private network profile and LocalSubnet only." -ForegroundColor Green
        Show-Status
    }
    'Disable' {
        Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue |
            Remove-NetFirewallRule -ErrorAction SilentlyContinue
        netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=$Port 2>$null | Out-Null
        Write-Host "HomeWatch LAN access disabled on TCP port $Port." -ForegroundColor Green
        Show-Status
    }
    'Status' {
        Show-Status
    }
}
