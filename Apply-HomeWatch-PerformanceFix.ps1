#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Root
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = (Get-Location).Path
}

$homeWatchPath = Join-Path $Root 'HomeWatch.ps1'
$indexPath = Join-Path $Root 'web\index.html'

if (-not (Test-Path -LiteralPath $homeWatchPath -PathType Leaf)) {
    throw "HomeWatch.ps1 was not found at $homeWatchPath"
}

$content = [IO.File]::ReadAllText($homeWatchPath)
$original = $content

$replacements = [ordered]@{
    'Get-Content $EventsPath -Tail 10000' = 'Get-Content $EventsPath -Tail 5000'
    'Get-Content $EventsPath -Tail 50000' = 'Get-Content $EventsPath -Tail 20000'
    '$nextBackgroundCheck=[DateTimeOffset]::Now.AddSeconds(2)' = '$nextBackgroundCheck=[DateTimeOffset]::Now.AddSeconds(10)'
    '$nextBackgroundCheck=[DateTimeOffset]::Now.AddSeconds(3)' = '$nextBackgroundCheck=[DateTimeOffset]::Now.AddSeconds(15)'
    '$pending.AsyncWaitHandle.WaitOne(1000)' = '$pending.AsyncWaitHandle.WaitOne(250)'
}

foreach ($entry in $replacements.GetEnumerator()) {
    if (-not $content.Contains($entry.Key)) {
        Write-Warning "Pattern not found or already patched: $($entry.Key)"
        continue
    }
    $content = $content.Replace($entry.Key, $entry.Value)
}

if ($content -eq $original) {
    Write-Host 'No changes were required. The performance patch may already be installed.' -ForegroundColor Yellow
    exit 0
}

$backup = "$homeWatchPath.performance-backup-$(Get-Date -Format yyyyMMdd-HHmmss)"
[IO.File]::WriteAllText($backup, $original, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($homeWatchPath, $content, [Text.UTF8Encoding]::new($false))

$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile($homeWatchPath, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    Copy-Item -LiteralPath $backup -Destination $homeWatchPath -Force
    throw ('The patched script failed PowerShell syntax validation and was restored. ' + ($parseErrors | ForEach-Object Message -join '; '))
}

if (Test-Path -LiteralPath $indexPath -PathType Leaf) {
    $index = [IO.File]::ReadAllText($indexPath)
    $index = $index.Replace('v1.3.1', 'v1.3.2')
    [IO.File]::WriteAllText($indexPath, $index, [Text.UTF8Encoding]::new($false))
}

Write-Host 'HomeWatch performance patch installed successfully.' -ForegroundColor Green
Write-Host "Backup: $backup"
Write-Host 'Changes: 15-second background polling, faster request detection, and smaller event-processing windows.'