@echo off
setlocal
title Change HomeWatch access token

net session >nul 2>&1
if not "%errorlevel%"=="0" (
  echo Requesting administrator access...
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Set-HomeWatch-AccessToken.ps1" -HomeWatchDirectory "%~dp0"
if errorlevel 1 (
  echo.
  echo The access token was not changed.
)
echo.
pause
