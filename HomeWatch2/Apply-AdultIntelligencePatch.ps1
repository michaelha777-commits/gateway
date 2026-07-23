$ErrorActionPreference = 'Stop'
$programPath = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $programPath -Raw

if ($text -notmatch 'AddSingleton<ExternalAdultDomainDatabase>') {
    $text = $text.Replace(
        'builder.Services.AddSingleton<NtfyNotifier>();' + [Environment]::NewLine + 'builder.Services.AddHostedService<AdGuardImportWorker>();',
        'builder.Services.AddSingleton<NtfyNotifier>();' + [Environment]::NewLine +
        'builder.Services.AddSingleton<ExternalAdultDomainDatabase>();' + [Environment]::NewLine +
        'builder.Services.AddHostedService(sp => sp.GetRequiredService<ExternalAdultDomainDatabase>());' + [Environment]::NewLine +
        'builder.Services.AddSingleton<AdultSessionMonitor>();' + [Environment]::NewLine +
        'builder.Services.AddHostedService(sp => sp.GetRequiredService<AdultSessionMonitor>());' + [Environment]::NewLine +
        'builder.Services.AddHostedService<AdGuardImportWorker>();')
}

$text = $text.Replace('version = "2.0.0-alpha.9"', 'version = "2.0.0-alpha.10"')

if ($text -notmatch 'adultDatabase = new') {
    $text = $text.Replace(
        'notifications = new { configured = !string.IsNullOrWhiteSpace(configuration["Ntfy:Topic"]) },',
        'notifications = new { configured = !string.IsNullOrWhiteSpace(configuration["Ntfy:Topic"]) },' + [Environment]::NewLine +
        '    adultDatabase = new { source = app.Services.GetRequiredService<ExternalAdultDomainDatabase>().Source, domainCount = app.Services.GetRequiredService<ExternalAdultDomainDatabase>().DomainCount, lastUpdated = UtcIso(app.Services.GetRequiredService<ExternalAdultDomainDatabase>().LastUpdatedUtc) },')
}

$text = $text.Replace(
    'public sealed class AdGuardImportWorker(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory, IConfiguration configuration, ImportState state, NtfyNotifier ntfy, ILogger<AdGuardImportWorker> logger) : BackgroundService',
    'public sealed class AdGuardImportWorker(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory, IConfiguration configuration, ImportState state, ExternalAdultDomainDatabase adultDatabase, AdultSessionMonitor sessionMonitor, ILogger<AdGuardImportWorker> logger) : BackgroundService')

$text = $text.Replace('        var notifications = new List<(string Device, string Domain)>();' + [Environment]::NewLine, '')
$text = $text.Replace('            var adult = AdultDomainClassifier.IsAdult(item.Domain);', '            var adult = adultDatabase.IsAdult(item.Domain);')

$oldAdultBlock = @'
            if (adult)
            {
                var since = timestamp.AddMinutes(-30);
                var duplicateAlert = await db.Alerts.AnyAsync(a => a.DeviceId == device.Id && a.Title == "Adult content detected" && a.Detail.Contains(item.Domain) && a.CreatedAt >= since, cancellationToken)
                    || db.Alerts.Local.Any(a => a.DeviceId == device.Id && a.Title == "Adult content detected" && a.Detail.Contains(item.Domain) && a.CreatedAt >= since);
                if (!duplicateAlert)
                {
                    db.Alerts.Add(new Alert { Id = Guid.NewGuid(), DeviceId = device.Id, Severity = "critical", Title = "Adult content detected", Detail = $"Domain: {item.Domain}", CreatedAt = timestamp });
                    notifications.Add((device.Name, item.Domain));
                }
            }
'@
$newAdultBlock = @'
            if (adult)
                await sessionMonitor.RecordHitAsync(device.Id, device.Name, item.Domain, timestamp, cancellationToken);
'@
$text = $text.Replace($oldAdultBlock, $newAdultBlock)

$oldNotifications = @'
        foreach (var notification in notifications)
            await ntfy.SendAsync("Adult content detected", $"Device: {notification.Device}\nWebsite: {notification.Domain}\nTime: {DateTime.Now:g}", "rotating_light,no_entry", 5, cancellationToken);
'@
$text = $text.Replace($oldNotifications, '')

Set-Content -Path $programPath -Value $text -Encoding UTF8
Write-Host 'Adult intelligence/session patch applied.' -ForegroundColor Green
