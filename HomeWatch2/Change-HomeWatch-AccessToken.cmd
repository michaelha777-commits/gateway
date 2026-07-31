@echo off
setlocal
title Change HomeWatch access token

net session >nul 2>&1
if not "%errorlevel%"=="0" (
  echo Requesting administrator access...
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

rem Normalize the script directory to a full path without a trailing backslash.
rem This prevents CMD quoting from introducing a literal quote into the token path.
for %%I in ("%~dp0.") do set "HOMEWATCH_DIR=%%~fI"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Set-HomeWatch-AccessToken.ps1" -HomeWatchDirectory "%HOMEWATCH_DIR%"
if errorlevel 1 (
  echo.
  echo The access token was not changed.
)
echo.
pause
