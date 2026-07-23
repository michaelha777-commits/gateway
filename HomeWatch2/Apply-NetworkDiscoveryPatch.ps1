param([switch]$Restore)
$ErrorActionPreference = 'Stop'
$program = Join-Path $PSScriptRoot 'Program.cs'
$backup = Join-Path $PSScriptRoot '.homewatch-program.backup'

if ($Restore) {
    if (Test-Path $backup) {
        Copy-Item $backup $program -Force
        Remove-Item $backup -Force
    }
    exit 0
}

if (-not (Test-Path $backup)) { Copy-Item $program $backup -Force }
$text = Get-Content $program -Raw
if ($text -notmatch 'AddHomeWatchNetworkDiscovery') {
    $text = $text.Replace('builder.Services.AddSingleton<ImportState>();', "builder.Services.AddSingleton<ImportState>();`r`nbuilder.Services.AddHomeWatchNetworkDiscovery();")
}
if ($text -notmatch 'MapHomeWatchNetworkDiscovery') {
    $text = $text.Replace('app.MapFallbackToFile("index.html");', "app.MapHomeWatchNetworkDiscovery();`r`napp.MapFallbackToFile(`"index.html`");")
}
$text = $text.Replace('version = "2.0.0-alpha.9"', 'version = "2.0.0-alpha.12"')
Set-Content -Path $program -Value $text -Encoding UTF8
