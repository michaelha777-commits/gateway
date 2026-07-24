@echo off
setlocal
cd /d "%~dp0"

net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

echo Installing HomeWatch diagnostics agent...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-HomeWatch-Agent.ps1"
set "INSTALL_EXIT=%errorlevel%"

if not "%INSTALL_EXIT%"=="0" (
  echo.
  echo Installation failed with exit code %INSTALL_EXIT%.
  pause
  exit /b %INSTALL_EXIT%
)

echo.
echo HomeWatch diagnostics agent installed successfully.
echo You may close this window.
pause
