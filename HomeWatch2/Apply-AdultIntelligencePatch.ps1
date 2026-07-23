$ErrorActionPreference = 'Stop'
$programPath = Join-Path $PSScriptRoot 'Program.cs'
$text = Get-Content $programPath -Raw
$nl = [Environment]::NewLine

if ($text -notmatch 'AddSingleton<ExternalAdultDomainDatabase>') {
    $replacement = 'builder.Services.AddSingleton<NtfyNotifier>();' + $nl +
        'builder.Services.AddSingleton<ExternalAdultDomainDatabase>();' + $nl +
        'builder.Services.AddHostedService(sp => sp.GetRequiredService<ExternalAdultDomainDatabase>());' + $nl +
        'builder.Services.AddSingleton<AdultSessionMonitor>();' + $nl +
        'builder.Services.AddHostedService(sp => sp.GetRequiredService<AdultSessionMonitor>());' + $nl +
        'builder.Services.AddHostedService<AdGuardImportWorker>();'
    $text = [regex]::Replace($text,
        'builder\.Services\.AddSingleton<NtfyNotifier>\(\);\s*builder\.Services\.AddHostedService<AdGuardImportWorker>\(\);',
        [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement }, 1)
}

$text = $text.Replace('version = "2.0.0-alpha.9"', 'version = "2.0.0-alpha.10"')

if ($text -notmatch 'adultDatabase = new') {
    $statusReplacement = 'notifications = new { configured = !string.IsNullOrWhiteSpace(configuration["Ntfy:Topic"]) },' + $nl +
        '    adultDatabase = new { source = app.Services.GetRequiredService<ExternalAdultDomainDatabase>().Source, domainCount = app.Services.GetRequiredService<ExternalAdultDomainDatabase>().DomainCount, lastUpdated = UtcIso(app.Services.GetRequiredService<ExternalAdultDomainDatabase>().LastUpdatedUtc) },'
    $text = $text.Replace('notifications = new { configured = !string.IsNullOrWhiteSpace(configuration["Ntfy:Topic"]) },', $statusReplacement)
}

$text = $text.Replace(
    'public sealed class AdGuardImportWorker(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory, IConfiguration configuration, ImportState state, NtfyNotifier ntfy, ILogger<AdGuardImportWorker> logger) : BackgroundService',
    'public sealed class AdGuardImportWorker(IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory, IConfiguration configuration, ImportState state, ExternalAdultDomainDatabase adultDatabase, AdultSessionMonitor sessionMonitor, ILogger<AdGuardImportWorker> logger) : BackgroundService')

$text = [regex]::Replace($text, '\s*var notifications = new List<\(string Device, string Domain\)>\(\);', '')
$text = $text.Replace('var adult = AdultDomainClassifier.IsAdult(item.Domain);', 'var adult = adultDatabase.IsAdult(item.Domain);')

$adultPattern = '(?ms)^\s{12}if \(adult\)\s*\{.*?^\s{12}\}\s*(?=^\s{8}\})'
$adultReplacement = $nl + '            if (adult)' + $nl + '                await sessionMonitor.RecordHitAsync(device.Id, device.Name, item.Domain, timestamp, cancellationToken);' + $nl
$text = [regex]::Replace($text, $adultPattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $adultReplacement }, 1)

$text = [regex]::Replace($text,
    '(?ms)^\s{8}foreach \(var notification in notifications\)\s*\r?\n\s{12}await ntfy\.SendAsync\("Adult content detected".*?;\s*\r?\n',
    '')

if ($text -notmatch 'ExternalAdultDomainDatabase adultDatabase') { throw 'Could not patch the AdGuard importer constructor.' }
if ($text -notmatch 'sessionMonitor\.RecordHitAsync') { throw 'Could not replace per-domain adult alerts with session tracking.' }
if ($text -notmatch 'AddSingleton<ExternalAdultDomainDatabase>') { throw 'Could not register the adult intelligence services.' }

Set-Content -Path $programPath -Value $text -Encoding UTF8
Write-Host 'Adult intelligence/session patch applied.' -ForegroundColor Green
