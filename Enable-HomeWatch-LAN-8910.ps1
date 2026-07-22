#requires -Version 5.1
#requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$HomeWatchPath = Join-Path $Root 'HomeWatch.ps1'
$BackupPath = Join-Path $Root 'HomeWatch.ps1.before-lan-8910.bak'
$Port = 8910

if (-not (Test-Path $HomeWatchPath)) {
    throw "HomeWatch.ps1 was not found at $HomeWatchPath"
}

# Stop only running HomeWatch instances, never every PowerShell process.
Get-CimInstance Win32_Process |
    Where-Object {
        $_.ProcessId -ne $PID -and
        $_.Name -match '^(powershell|pwsh)(\.exe)?$' -and
        $_.CommandLine -match 'HomeWatch\.ps1'
    } |
    ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }

Start-Sleep -Seconds 2

$content = Get-Content $HomeWatchPath -Raw

$oldBlock = @'
$listener = New-Object Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Write-Host "HomeWatch is running at http://127.0.0.1:$Port" -ForegroundColor Green
Start-Process "http://127.0.0.1:$Port"
'@

$newBlock = @'
$listener = New-Object Net.HttpListener
$listener.Prefixes.Add("http://+:$Port/")
$listener.Start()
Write-Host "HomeWatch is running on this PC at http://127.0.0.1:$Port and on your LAN at http://<PC-IP>:$Port" -ForegroundColor Green
Start-Process "http://127.0.0.1:$Port"
'@

if ($content.Contains($oldBlock)) {
    Copy-Item $HomeWatchPath $BackupPath -Force
    $content = $content.Replace($oldBlock, $newBlock)
    Set-Content $HomeWatchPath -Value $content -Encoding UTF8
}
elseif (-not $content.Contains('$listener.Prefixes.Add("http://+:$Port/")')) {
    throw 'The expected listener block was not found. No file was changed.'
}

# Validate PowerShell syntax before proceeding. Restore automatically on failure.
$tokens = $null
$parseErrors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    $HomeWatchPath,
    [ref]$tokens,
    [ref]$parseErrors
) | Out-Null

if ($parseErrors.Count -gt 0) {
    if (Test-Path $BackupPath) {
        Copy-Item $BackupPath $HomeWatchPath -Force
    }
    $parseErrors | Format-List
    throw 'PowerShell validation failed. The original HomeWatch.ps1 was restored.'
}

# Remove old port-proxy rules that can occupy the same port.
& netsh interface portproxy delete v4tov4 listenaddress=0.0.0.0 listenport=$Port 2>$null | Out-Null
& netsh interface portproxy delete v4tov4 listenaddress=127.0.0.1 listenport=$Port 2>$null | Out-Null

# Reserve the wildcard HttpListener URL for the current Windows account.
& netsh http delete urlacl url="http://+:$Port/" 2>$null | Out-Null
& netsh http add urlacl url="http://+:$Port/" user="$env:USERDOMAIN\$env:USERNAME" | Out-Host

# Allow LAN traffic only on Private Windows networks and only from the local subnet.
Get-NetFirewallRule -DisplayName "HomeWatch LAN $Port" -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule

New-NetFirewallRule `
    -DisplayName "HomeWatch LAN $Port" `
    -Direction Inbound `
    -Protocol TCP `
    -LocalPort $Port `
    -RemoteAddress LocalSubnet `
    -Action Allow `
    -Profile Private | Out-Host

# Ensure the standard launcher always uses 8910.
$launcher = @"
@echo off
cd /d "$Root"
powershell -NoExit -ExecutionPolicy Bypass -File ".\HomeWatch.ps1" -Port $Port
"@
Set-Content (Join-Path $Root 'Start-HomeWatch.cmd') -Value $launcher -Encoding ASCII

Write-Host ''
Write-Host 'HomeWatch LAN configuration completed successfully.' -ForegroundColor Green
Write-Host "Starting HomeWatch on port $Port..." -ForegroundColor Green
Write-Host ''

& powershell.exe -NoExit -ExecutionPolicy Bypass -File $HomeWatchPath -Port $Port
