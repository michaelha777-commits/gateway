$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$program = Join-Path $PSScriptRoot 'Program.cs'
if (-not (Test-Path $program)) { throw 'Program.cs not found.' }

$text = Get-Content $program -Raw
if ($text -notmatch '"erome\.com"') {
    $text = $text.Replace('"ashleymadison.com"', '"ashleymadison.com", "erome.com", "jerkmate.com", "eporner.com", "motherless.com", "tnaflix.com", "drtuber.com", "beeg.com", "hqporner.com"')
}

# Reclassify already-imported matching events and create one recent alert per device if needed.
$startupMarker = 'await RepairDuplicateDevicesAsync(db);'
if ($text -notmatch 'ReclassifyKnownAdultEventsAsync') {
    $text = $text.Replace($startupMarker, $startupMarker + "`r`n    await ReclassifyKnownAdultEventsAsync(db);")

    $helper = @'

static async Task ReclassifyKnownAdultEventsAsync(HomeWatchDb db)
{
    var rows = await db.Events.Where(x => x.Category != "adult").ToListAsync();
    var changed = false;
    foreach (var row in rows)
    {
        if (!AdultDomainClassifier.IsAdult(row.Domain)) continue;
        row.Category = "adult";
        changed = true;
        var exists = await db.Alerts.AnyAsync(a => a.DeviceId == row.DeviceId && a.Title == "Adult content detected" && a.Detail.Contains(row.Domain));
        if (!exists)
        {
            db.Alerts.Add(new Alert
            {
                Id = Guid.NewGuid(), DeviceId = row.DeviceId, Severity = "critical",
                Title = "Adult content detected", Detail = $"Domain: {row.Domain}", CreatedAt = row.Timestamp
            });
        }
    }
    if (changed || db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();
}
'@
    $text = $text.Replace('public sealed class AdGuardImportWorker', $helper + "`r`npublic sealed class AdGuardImportWorker")
}

Set-Content -Path $program -Value $text -Encoding UTF8
Write-Host 'Adult-domain classifier updated for erome.com and jerkmate.com.' -ForegroundColor Green
