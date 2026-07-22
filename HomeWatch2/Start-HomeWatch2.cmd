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

echo Starting HomeWatch 2...
start "HomeWatch 2" cmd /k dotnet run

timeout /t 4 /nobreak >nul
start http://127.0.0.1:8920
endlocal
