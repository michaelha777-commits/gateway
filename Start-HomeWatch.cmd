@echo off
title HomeWatch
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0HomeWatch.ps1" -Port 8899
if errorlevel 1 pause
