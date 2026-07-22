$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$credentialFile = Join-Path $PSScriptRoot '.homewatch-credentials.json'

function Convert-SecureStringToPlainText([Security.SecureString]$SecureString) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($SecureString)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

try {
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
        if ([string]::IsNullOrWhiteSpace($username)) {
            $username = 'admin'
        }

        $securePassword = Read-Host 'AdGuard password' -AsSecureString
        if ($securePassword.Length -eq 0) {
            throw 'An AdGuard password is required.'
        }

        [pscustomobject]@{
            Username = $username
            Password = ConvertFrom-SecureString $securePassword
        } | ConvertTo-Json | Set-Content -Path $credentialFile -Encoding UTF8
    }

    $env:AdGuard__BaseUrl = 'http://127.0.0.1'
    $env:AdGuard__Username = $username
    $env:AdGuard__Password = Convert-SecureStringToPlainText $securePassword

    Write-Host ''
    Write-Host 'Starting HomeWatch 2...'
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
