@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Optimize-Event-Loading.ps1"
if errorlevel 1 (
  echo.
  echo Optimization failed.
  pause
  exit /b 1
)
echo.
echo Optimization complete. Close any running HomeWatch window and start HomeWatch again.
pause
