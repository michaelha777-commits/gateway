@echo off
setlocal
set "ROOT=%~dp0"
set "SCRIPT=%ROOT%HomeWatch.ps1"
set "BACKUP=%ROOT%HomeWatch.ps1.before-speed-fix.bak"

if not exist "%SCRIPT%" (
  echo HomeWatch.ps1 was not found in %ROOT%
  pause
  exit /b 1
)

if not exist "%BACKUP%" copy "%SCRIPT%" "%BACKUP%" >nul

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$p='%SCRIPT%'; $c=[IO.File]::ReadAllText($p);" ^
  "$original=$c;" ^
  "$c=$c.Replace('$nextBackgroundCheck=[DateTimeOffset]::Now.AddSeconds(2)','$nextBackgroundCheck=[DateTimeOffset]::Now.AddSeconds(60)');" ^
  "$c=$c.Replace('$nextBackgroundCheck=[DateTimeOffset]::Now.AddSeconds(3)','$nextBackgroundCheck=[DateTimeOffset]::Now.AddSeconds(60)');" ^
  "$old='$added=0; $events=@(Get-Events $hours); $sessions=@(Get-Sessions $events)';" ^
  "$new='$dashboardTimer=[Diagnostics.Stopwatch]::StartNew(); $eventTimer=[Diagnostics.Stopwatch]::StartNew(); $added=0; $events=@(Get-Events $hours); $eventTimer.Stop(); $sessionTimer=[Diagnostics.Stopwatch]::StartNew(); $sessions=@(Get-Sessions $events); $sessionTimer.Stop()';" ^
  "if($c.Contains($old)){$c=$c.Replace($old,$new)};" ^
  "$old2='Send-Json $ctx @{events=$events;sessions=$sessions;alerts=$alerts;alertAuditLog=$audit;clients=$clients;added=$added;eventReadErrors=$script:LastEventReadErrors;generatedAt=[DateTimeOffset]::Now.ToString(''o'')}; continue';" ^
  "$new2='$dashboardTimer.Stop(); Write-Host (''Dashboard: total {0:N1}s, events {1:N1}s, sessions {2:N1}s, {3} events'' -f $dashboardTimer.Elapsed.TotalSeconds,$eventTimer.Elapsed.TotalSeconds,$sessionTimer.Elapsed.TotalSeconds,$events.Count) -ForegroundColor Cyan; Send-Json $ctx @{events=$events;sessions=$sessions;alerts=$alerts;alertAuditLog=$audit;clients=$clients;added=$added;eventReadErrors=$script:LastEventReadErrors;generatedAt=[DateTimeOffset]::Now.ToString(''o'')}; continue';" ^
  "if($c.Contains($old2)){$c=$c.Replace($old2,$new2)};" ^
  "if($c -eq $original){Write-Error 'The speed fix could not find the expected code. No changes were made.'; exit 2};" ^
  "[IO.File]::WriteAllText($p,$c,[Text.UTF8Encoding]::new($false))"

if errorlevel 1 (
  echo.
  echo The automatic speed fix failed. HomeWatch.ps1 was not changed.
  pause
  exit /b 1
)

echo.
echo Slow-dashboard fix installed.
echo Background scanning now runs once per minute instead of every 3 seconds.
echo Dashboard timing will appear in the HomeWatch PowerShell window.
echo.
echo Close the current HomeWatch window, then start it again with Start-HomeWatch.cmd.
pause
