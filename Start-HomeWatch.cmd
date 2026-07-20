@echo off
title HomeWatch
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0HomeWatch.ps1"
if errorlevel 1 pause
