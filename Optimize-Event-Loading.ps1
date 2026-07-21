$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'HomeWatch.ps1'
if (-not (Test-Path $path)) { throw "HomeWatch.ps1 not found at $path" }

$content = [IO.File]::ReadAllText($path)
$old = @'
                    try {
                        $kind = Classify-Domain $domainKey
                        foreach ($property in @('category','evidence','confidence','label')) {
                            $e | Add-Member -NotePropertyName $property -NotePropertyValue $kind[$property] -Force
                        }
                        $e | Add-Member -NotePropertyName description -NotePropertyValue (Get-DomainDescription $domainKey) -Force
                        $identity=Get-DomainIdentity $domainKey
                        $e | Add-Member -NotePropertyName serviceOwner -NotePropertyValue $identity.owner -Force
                        $e | Add-Member -NotePropertyName serviceCategory -NotePropertyValue $identity.category -Force
                        $e | Add-Member -NotePropertyName serviceCategoryConfidence -NotePropertyValue $identity.confidence -Force
                    } catch {
                        if (-not $e.description) { $e | Add-Member description 'Domain description unavailable' -Force }
                    }
'@
$new = @'
                    try {
                        if (-not $e.category -or -not $e.evidence -or $null -eq $e.confidence -or -not $e.label) {
                            $kind = Classify-Domain $domainKey
                            foreach ($property in @('category','evidence','confidence','label')) {
                                $e | Add-Member -NotePropertyName $property -NotePropertyValue $kind[$property] -Force
                            }
                        }
                        if (-not $e.description) {
                            $e | Add-Member -NotePropertyName description -NotePropertyValue (Get-DomainDescription $domainKey) -Force
                        }
                        if (-not $e.serviceOwner -or -not $e.serviceCategory -or $null -eq $e.serviceCategoryConfidence) {
                            $identity=Get-DomainIdentity $domainKey
                            $e | Add-Member -NotePropertyName serviceOwner -NotePropertyValue $identity.owner -Force
                            $e | Add-Member -NotePropertyName serviceCategory -NotePropertyValue $identity.category -Force
                            $e | Add-Member -NotePropertyName serviceCategoryConfidence -NotePropertyValue $identity.confidence -Force
                        }
                    } catch {
                        if (-not $e.description) { $e | Add-Member description 'Domain description unavailable' -Force }
                    }
'@

if ($content.Contains($new)) {
    Write-Host 'Event-loading optimization is already installed.' -ForegroundColor Green
    exit 0
}
if (-not $content.Contains($old)) {
    throw 'Could not find the expected Get-Events block. No changes were made.'
}

$backup = "$path.before-event-optimization.bak"
if (-not (Test-Path $backup)) { Copy-Item $path $backup }
$content = $content.Replace($old, $new)
[IO.File]::WriteAllText($path, $content, [Text.UTF8Encoding]::new($false))
Write-Host 'Event-loading optimization installed successfully.' -ForegroundColor Green
