@echo off
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-HomeWatch2-Agent.ps1"
if %errorlevel% neq 0 (
  echo.
  echo Installation failed. Review the message above.
  pause
  exit /b 1
)
echo.
echo HomeWatch2 agent is connected through the private GitHub relay.
timeout /t 4 >nul
