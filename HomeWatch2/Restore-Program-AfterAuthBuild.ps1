param([Parameter(Mandatory=$true)][string]$ProgramPath)
$ErrorActionPreference = 'Stop'
$backupPath = "$ProgramPath.auth-build-backup"
if (Test-Path $backupPath) {
    $original = [IO.File]::ReadAllText($backupPath)
    [IO.File]::WriteAllText($ProgramPath, $original, [Text.UTF8Encoding]::new($false))
    Remove-Item $backupPath -Force
}
