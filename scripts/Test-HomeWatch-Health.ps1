#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:8920',
    [int]$TimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'
$base = $BaseUrl.TrimEnd('/')

function Invoke-HealthRequest {
    param([Parameter(Mandatory)][string]$Path)

    $uri = $base + $Path
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $result = Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec $TimeoutSeconds
        $watch.Stop()
        [pscustomobject]@{
            Endpoint = $Path
            Healthy = $true
            DurationMs = $watch.ElapsedMilliseconds
            Error = ''
            Result = $result
        }
    }
    catch {
        $watch.Stop()
        [pscustomobject]@{
            Endpoint = $Path
            Healthy = $false
            DurationMs = $watch.ElapsedMilliseconds
            Error = $_.Exception.Message
            Result = $null
        }
    }
}

$results = @(
    Invoke-HealthRequest -Path '/api/status'
    Invoke-HealthRequest -Path '/api/dashboard?hours=1'
)

$results | Select-Object Endpoint,Healthy,DurationMs,Error | Format-Table -AutoSize

if ($results.Healthy -contains $false) {
    throw 'One or more HomeWatch health checks failed.'
}
