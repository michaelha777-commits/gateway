[CmdletBinding()]
param(
    [string]$Token,
    [string]$HomeWatchDirectory = $PSScriptRoot,
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an Administrator PowerShell window.'
}

if ([string]::IsNullOrWhiteSpace($Token)) {
    $secure = Read-Host 'Enter a strong HomeWatch remote-access token' -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $Token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}

if ($Token.Length -lt 16) {
    throw 'The access token must be at least 16 characters long. A long passphrase is recommended.'
}

Add-Type -AssemblyName System.Security
$plain = [Text.Encoding]::UTF8.GetBytes($Token.Trim())
$entropy = [Text.Encoding]::UTF8.GetBytes('HomeWatch.RemoteAccess.v1')
try {
    $encrypted = [Security.Cryptography.ProtectedData]::Protect(
        $plain,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    $path = Join-Path $HomeWatchDirectory 'homewatch-access-token.dat'
    [IO.File]::WriteAllBytes($path, $encrypted)
    $acl = Get-Acl $path
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('SYSTEM','FullControl','Allow')))
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule('Administrators','FullControl','Allow')))
    Set-Acl -Path $path -AclObject $acl
}
finally {
    # CryptographicOperations.ZeroMemory is unavailable in Windows PowerShell 5.1.
    # Array.Clear provides compatible cleanup for the temporary plaintext byte array.
    if ($null -ne $plain) { [Array]::Clear($plain, 0, $plain.Length) }
    $Token = $null
}

Write-Host "HomeWatch access token saved securely to $path" -ForegroundColor Green
if (-not $NoRestart -and (Get-Service HomeWatch -ErrorAction SilentlyContinue)) {
    Restart-Service HomeWatch -Force
    Write-Host 'HomeWatch service restarted.' -ForegroundColor Green
}
