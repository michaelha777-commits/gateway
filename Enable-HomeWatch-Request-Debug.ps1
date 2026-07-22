#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$HomeWatchPath = (Join-Path $PSScriptRoot 'HomeWatch.ps1')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $HomeWatchPath)) {
    throw "HomeWatch.ps1 was not found at: $HomeWatchPath"
}

$backupPath = "$HomeWatchPath.before-request-debug"
Copy-Item -LiteralPath $HomeWatchPath -Destination $backupPath -Force

$content = Get-Content -LiteralPath $HomeWatchPath -Raw

$oldRequestLine = '$ctx = $listener.EndGetContext($pending); $path = $ctx.Request.Url.AbsolutePath'
$newRequestBlock = @'
$ctx = $listener.EndGetContext($pending)
$path = $ctx.Request.Url.AbsolutePath
$remote = if ($ctx.Request.RemoteEndPoint) { $ctx.Request.RemoteEndPoint.ToString() } else { 'unknown' }
$hostHeader = [string]$ctx.Request.Headers['Host']
Write-Host ("[HTTP] Accepted {0} {1} from {2}; Host={3}" -f $ctx.Request.HttpMethod,$path,$remote,$hostHeader) -ForegroundColor Cyan
'@

$oldIndexLine = "if (`$path -eq '/' -or `$path -eq '/index.html') { Send-File `$ctx (Join-Path `$Root 'web\index.html') 'text/html; charset=utf-8'; continue }"
$newIndexLine = "if (`$path -eq '/' -or `$path -eq '/index.html') { Write-Host '[HTTP] Serving index.html' -ForegroundColor DarkCyan; Send-File `$ctx (Join-Path `$Root 'web\index.html') 'text/html; charset=utf-8'; Write-Host '[HTTP] Completed index.html' -ForegroundColor Green; continue }"

$oldAppLine = "if (`$path -eq '/app.js') { Send-File `$ctx (Join-Path `$Root 'web\app.js') 'application/javascript; charset=utf-8'; continue }"
$newAppLine = "if (`$path -eq '/app.js') { Write-Host '[HTTP] Serving app.js' -ForegroundColor DarkCyan; Send-File `$ctx (Join-Path `$Root 'web\app.js') 'application/javascript; charset=utf-8'; Write-Host '[HTTP] Completed app.js' -ForegroundColor Green; continue }"

$replacements = @(
    @{ Name = 'request acceptance'; Old = $oldRequestLine; New = $newRequestBlock.TrimEnd() },
    @{ Name = 'index response'; Old = $oldIndexLine; New = $newIndexLine },
    @{ Name = 'app.js response'; Old = $oldAppLine; New = $newAppLine }
)

foreach ($replacement in $replacements) {
    $count = ([regex]::Matches($content, [regex]::Escape($replacement.Old))).Count
    if ($count -ne 1) {
        Copy-Item -LiteralPath $backupPath -Destination $HomeWatchPath -Force
        throw "Expected exactly one $($replacement.Name) line, but found $count. Original file restored."
    }
    $content = $content.Replace($replacement.Old, $replacement.New)
}

Set-Content -LiteralPath $HomeWatchPath -Value $content -Encoding UTF8

$tokens = $null
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $HomeWatchPath),
    [ref]$tokens,
    [ref]$errors
) | Out-Null

if ($errors.Count -gt 0) {
    Copy-Item -LiteralPath $backupPath -Destination $HomeWatchPath -Force
    $errors | Format-List
    throw 'PowerShell validation failed. Original file restored.'
}

Write-Host 'Request tracing enabled successfully.' -ForegroundColor Green
Write-Host "Backup: $backupPath"
Write-Host 'Restart HomeWatch and test http://192.168.1.112:8910 once.'
