#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$files = @(
    'tools\HomeWatch-Agent.ps1',
    'Install-HomeWatch-Agent.ps1'
)

$failed = $false
foreach ($relative in $files) {
    $path = Join-Path $Root $relative
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Error "$relative is missing"
        $failed = $true
        continue
    }

    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors.Count) {
        foreach ($item in $errors) { Write-Error "$relative line $($item.Extent.StartLineNumber): $($item.Message)" }
        $failed = $true
    } else {
        Write-Host "[PASS] $relative parses successfully" -ForegroundColor Green
    }
}

$agentText = Get-Content -LiteralPath (Join-Path $Root 'tools\HomeWatch-Agent.ps1') -Raw
$requiredProtections = @(
    "BindAddress = '127.0.0.1'",
    'Authorization',
    '$allowedFiles',
    'StartsWith($Root',
    'HOMEWATCH_AGENT_TOKEN'
)
foreach ($protection in $requiredProtections) {
    if (-not $agentText.Contains($protection)) {
        Write-Error "Agent safety control missing: $protection"
        $failed = $true
    } else {
        Write-Host "[PASS] Agent safety control present: $protection" -ForegroundColor Green
    }
}

$forbidden = @('Invoke-Expression','iex ','cmd.exe /c','Start-Process powershell')
foreach ($pattern in $forbidden) {
    if ($agentText -match [regex]::Escape($pattern)) {
        Write-Error "Forbidden arbitrary-execution pattern found: $pattern"
        $failed = $true
    }
}

if ($failed) { exit 1 }
Write-Host 'HomeWatch agent validation passed.' -ForegroundColor Green
exit 0
