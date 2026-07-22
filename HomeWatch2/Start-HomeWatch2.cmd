@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>&1
if errorlevel 1 (
  echo .NET 8 SDK is required before HomeWatch 2 can run.
  echo Install it, then run this file again.
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-HomeWatch2.ps1"
endlocal
