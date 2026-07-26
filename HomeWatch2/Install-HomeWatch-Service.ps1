#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$ServiceName = "HomeWatch",
    [string]$ProjectDirectory,
    [string]$NssmPath,
    [string]$Configuration = "Release",
    [string]$Framework = "net8.0"
)

$ErrorActionPreference = "Stop"

# Windows PowerShell 5.1 can evaluate parameter defaults before $PSScriptRoot is populated.
# Resolve script-relative paths after parameter binding instead.
$scriptDirectory = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($scriptDirectory)) {
    throw "Unable to determine the installer script directory."
}

if ([string]::IsNullOrWhiteSpace($ProjectDirectory)) {
    $ProjectDirectory = $scriptDirectory
}
if ([string]::IsNullOrWhiteSpace($NssmPath)) {
    $repositoryDirectory = Split-Path -Parent $ProjectDirectory
    $NssmPath = Join-Path $repositoryDirectory "nssm\win64\nssm.exe"
}

$projectFile = Join-Path $ProjectDirectory "HomeWatch2.csproj"
$application = Join-Path $ProjectDirectory "bin\$Configuration\$Framework\HomeWatch2.exe"

if (-not (Test-Path $projectFile)) {
    throw "HomeWatch project file was not found: $projectFile"
}

if (-not (Test-Path $NssmPath)) {
    throw "NSSM was not found: $NssmPath"
}

Write-Host "Building HomeWatch..."
& dotnet build $projectFile --configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "HomeWatch build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $application)) {
    throw "HomeWatch executable was not produced: $application"
}

$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Stopping existing $ServiceName service..."
    if ($existingService.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force
        $existingService.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }

    Write-Host "Updating existing service configuration..."
    & $NssmPath set $ServiceName Application $application
    & $NssmPath set $ServiceName AppDirectory $ProjectDirectory
    & $NssmPath reset $ServiceName AppParameters
}
else {
    Write-Host "Installing $ServiceName service..."
    & $NssmPath install $ServiceName $application
    & $NssmPath set $ServiceName AppDirectory $ProjectDirectory
}

& $NssmPath set $ServiceName DisplayName "HomeWatch"
& $NssmPath set $ServiceName Description "HomeWatch network monitoring service"
& $NssmPath set $ServiceName Start SERVICE_AUTO_START
& $NssmPath set $ServiceName ObjectName LocalSystem
& $NssmPath set $ServiceName AppExit Default Restart
& $NssmPath set $ServiceName AppRestartDelay 5000

Write-Host "Starting $ServiceName service..."
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))

$listener = Get-NetTCPConnection -LocalPort 8920 -State Listen -ErrorAction SilentlyContinue
if (-not $listener) {
    Write-Warning "$ServiceName is running, but port 8920 is not listening yet. Check the service logs or retry in a few seconds."
}
else {
    Write-Host "HomeWatch is running and listening on port 8920."
}

Write-Host "Open http://127.0.0.1:8920"
