@echo off
setlocal
net session >nul 2>&1
if errorlevel 1 (
  echo This setup must be run as Administrator.
  echo Right-click Enable-Phone-Access.cmd and choose Run as administrator.
  pause
  exit /b 1
)

set "ROOT=%~dp0"
set "SCRIPT=%ROOT%HomeWatch.ps1"
set "BACKUP=%ROOT%HomeWatch.ps1.before-phone-access.bak"

if not exist "%SCRIPT%" (
  echo HomeWatch.ps1 was not found in %ROOT%
  pause
  exit /b 1
)

if not exist "%BACKUP%" copy "%SCRIPT%" "%BACKUP%" >nul

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$p='%SCRIPT%'; $c=[IO.File]::ReadAllText($p); $old='$listener.Prefixes.Add(\"http://127.0.0.1:$Port/\")'; $new='$listener.Prefixes.Add(\"http://+:$Port/\")'; if($c.Contains($old)){$c=$c.Replace($old,$new); [IO.File]::WriteAllText($p,$c,[Text.UTF8Encoding]::new($false)); exit 0} elseif($c.Contains($new)){exit 0} else {Write-Error 'Could not find the HomeWatch listener line.'; exit 2}"
if errorlevel 1 (
  echo Could not update HomeWatch automatically.
  pause
  exit /b 1
)

netsh http delete urlacl url=http://+:8899/ >nul 2>&1
netsh http add urlacl url=http://+:8899/ user="%USERDOMAIN%\%USERNAME%"
if errorlevel 1 (
  echo Could not create the Windows URL permission.
  pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$r=Get-NetFirewallRule -DisplayName 'HomeWatch LAN 8899' -ErrorAction SilentlyContinue; if($r){$r | Remove-NetFirewallRule}; New-NetFirewallRule -DisplayName 'HomeWatch LAN 8899' -Direction Inbound -Protocol TCP -LocalPort 8899 -RemoteAddress LocalSubnet -Action Allow | Out-Null"
if errorlevel 1 (
  echo Could not create the Windows Firewall rule.
  pause
  exit /b 1
)

echo.
echo Phone access is enabled on port 8899 for devices on your local network only.
echo Start HomeWatch with Start-HomeWatch.cmd, then use the computer's IPv4 address from your phone.
echo No router port forwarding is required.
pause
