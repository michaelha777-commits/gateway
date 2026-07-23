$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$credentialFile = Join-Path $PSScriptRoot '.homewatch-credentials.json'

function Convert-SecureStringToPlainText([Security.SecureString]$SecureString) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
}

function Find-NmapExecutable {
    $command = Get-Command nmap.exe -ErrorAction SilentlyContinue
    if (-not $command) { $command = Get-Command nmap -ErrorAction SilentlyContinue }
    if ($command) { return $command.Source }

    $candidates = @(
        (Join-Path $env:ProgramFiles 'Nmap\nmap.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Nmap\nmap.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Nmap\nmap.exe'),
        'C:\Program Files\Nmap\nmap.exe',
        'C:\Program Files (x86)\Nmap\nmap.exe'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    return $candidates | Where-Object { Test-Path $_ -PathType Leaf } | Select-Object -First 1
}

try {
    $networkPatch = Join-Path $PSScriptRoot 'Apply-NetworkDiscoveryPatch.ps1'
    if (Test-Path $networkPatch) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $networkPatch
    }

    $adultPatch = Join-Path $PSScriptRoot 'Apply-AdultClassifierPatch.ps1'
    if (Test-Path $adultPatch) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $adultPatch
    }

    $saved = $null
    if (Test-Path $credentialFile) {
        $saved = Get-Content $credentialFile -Raw | ConvertFrom-Json
        $username = [string]$saved.Username
        $securePassword = [string]$saved.Password | ConvertTo-SecureString
    }
    else {
        Write-Host ''
        Write-Host 'HomeWatch needs your AdGuard Home login once.'
        Write-Host 'The password will be encrypted for this Windows user and will not be saved in GitHub.'
        Write-Host ''
        $username = Read-Host 'AdGuard username [admin]'
        if ([string]::IsNullOrWhiteSpace($username)) { $username = 'admin' }
        $securePassword = Read-Host 'AdGuard password' -AsSecureString
        if ($securePassword.Length -eq 0) { throw 'An AdGuard password is required.' }
    }

    $ntfyTopic = if ($saved -and $saved.PSObject.Properties.Name -contains 'NtfyTopic') { [string]$saved.NtfyTopic } else { '' }
    if ([string]::IsNullOrWhiteSpace($ntfyTopic)) {
        Write-Host ''
        Write-Host 'HomeWatch will send adult-content alerts to ntfy on your phone.'
        $ntfyTopic = Read-Host 'Enter the ntfy topic subscribed on your phone'
        if ([string]::IsNullOrWhiteSpace($ntfyTopic)) {
            Write-Host 'No ntfy topic entered. Alerts will still appear inside HomeWatch.' -ForegroundColor Yellow
        }
    }

    [pscustomobject]@{
        Username = $username
        Password = ConvertFrom-SecureString $securePassword
        NtfyTopic = $ntfyTopic
    } | ConvertTo-Json | Set-Content -Path $credentialFile -Encoding UTF8

    $env:AdGuard__BaseUrl = 'http://127.0.0.1'
    $env:AdGuard__Username = $username
    $env:AdGuard__Password = Convert-SecureStringToPlainText $securePassword
    $env:Ntfy__BaseUrl = 'https://ntfy.sh'
    $env:Ntfy__Topic = $ntfyTopic

    $nmapPath = Find-NmapExecutable
    if ($nmapPath) {
        $nmapFolder = Split-Path $nmapPath -Parent
        if (($env:PATH -split ';') -notcontains $nmapFolder) {
            $env:PATH = "$nmapFolder;$env:PATH"
        }
        $env:HomeWatch__NmapPath = $nmapPath
        Write-Host "Nmap detected: $nmapPath" -ForegroundColor Green
    }
    else {
        Write-Host 'Nmap was not found in PATH or its standard Windows installation folders. HomeWatch will use its built-in scanner.' -ForegroundColor Yellow
    }

    Write-Host ''
    Write-Host 'Starting HomeWatch 2 with network discovery enabled...'
    Start-Process -FilePath 'cmd.exe' -ArgumentList '/k', 'dotnet run' -WorkingDirectory $PSScriptRoot
    Start-Sleep -Seconds 4
    Start-Process 'http://127.0.0.1:8920'
}
catch {
    Write-Host ''
    Write-Host "HomeWatch could not start: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}
