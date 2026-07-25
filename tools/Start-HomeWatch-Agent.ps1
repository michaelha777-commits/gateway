$ErrorActionPreference = 'Stop'
$env:HOMEWATCH_AGENT_TOKEN = (Get-Content -LiteralPath 'F:\gateway\data\agent-token.txt' -Raw).Trim()
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'F:\gateway\tools\HomeWatch-Agent.ps1' -Root 'F:\gateway' -Port 8911
