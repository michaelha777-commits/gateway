#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$files = @(
    'Install-HomeWatch2-Agent.ps1',
    'tools\HomeWatch2-RelayAgent.ps1'
)

foreach ($relative in $files) {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing required file: $relative" }
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
    if ($errors.Count -gt 0) {
        $message = ($errors | ForEach-Object { "line $($_.Extent.StartLineNumber): $($_.Message)" }) -join '; '
        throw "$relative failed PowerShell parsing: $message"
    }
}

$cmdPath = Join-Path $root 'Install-HomeWatch2-Agent.cmd'
if (-not (Test-Path -LiteralPath $cmdPath)) { throw 'Install-HomeWatch2-Agent.cmd is missing.' }
$cmd = Get-Content -LiteralPath $cmdPath -Raw
foreach ($required in @('ExecutionPolicy Bypass','Install-HomeWatch2-Agent.ps1','Start-Process','-Verb RunAs')) {
    if (-not $cmd.Contains($required)) { throw "Installer command is missing: $required" }
}

$agent = Get-Content -LiteralPath (Join-Path $root 'tools\HomeWatch2-RelayAgent.ps1') -Raw
foreach ($required in @("'status'","'diagnostics'","'restart'","'update'",'--configuration','Release','--no-launch-profile','-RedirectStandardOutput','-RedirectStandardError')) {
    if (-not $agent.Contains($required)) { throw "Relay agent is missing required behavior: $required" }
}
foreach ($forbidden in @('Invoke-Expression','iex ','cmd.exe /c','ScriptBlock.Create')) {
    if ($agent -match [regex]::Escape($forbidden)) { throw "Relay agent contains forbidden arbitrary execution pattern: $forbidden" }
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('homewatch2-agent-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    Copy-Item -Path (Join-Path $root 'tools') -Destination $temp -Recurse
    Copy-Item -Path $cmdPath -Destination $temp
    Copy-Item -Path (Join-Path $root 'Install-HomeWatch2-Agent.ps1') -Destination $temp
    Set-Content -LiteralPath (Join-Path $temp 'agent-command.json') -Encoding UTF8 -Value '{"id":"ci-unsupported","action":"not-allowed"}'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $temp 'tools\HomeWatch2-RelayAgent.ps1') -Root $temp -Once -NoGit
    if ($LASTEXITCODE -ne 0) { throw "Relay smoke test exited with code $LASTEXITCODE" }
    $status = Get-Content -LiteralPath (Join-Path $temp 'agent-status.json') -Raw | ConvertFrom-Json
    if ($status.commandId -ne 'ci-unsupported' -or $status.ok -ne $false -or $status.error -notmatch 'Unsupported action') {
        throw 'Relay allow-list smoke test did not return the expected rejection.'
    }
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'HomeWatch2 agent validation passed.' -ForegroundColor Green
