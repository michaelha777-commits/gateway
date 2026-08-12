using HomeWatch3.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

public static class V2Enhancements
{
    public static IServiceCollection AddV2Enhancements(this IServiceCollection services)
    {
        services.AddHostedService<AdultDomainClassifier>(sp => (AdultDomainClassifier)sp.GetRequiredService<IAdultDomainClassifier>());
        services.AddHostedService<DnsHistoryMonitor>();
        services.AddSingleton<DeviceDiscoveryService>();
        services.AddHostedService(sp => sp.GetRequiredService<DeviceDiscoveryService>());
        return services;
    }

    public static void MapV2Enhancements(this WebApplication app)
    {
        app.MapGet("/api/intelligence/adult/status", (IAdultDomainClassifier c) => Results.Ok(((AdultDomainClassifier)c).Status));
        app.MapGet("/api/intelligence/adult/safe", (IAdultDomainClassifier c) => Results.Ok(((AdultDomainClassifier)c).GetSafeDomains()));
        app.MapPost("/api/intelligence/adult/safe", async (SafeDomainUpdate u, IAdultDomainClassifier c, HomeWatchDb db, CancellationToken ct) =>
        {
            var classifier = (AdultDomainClassifier)c;
            var d = classifier.AddSafeDomain(u.Domain);
            if (d is null) return Results.BadRequest(new { error = "Enter a valid domain." });
            var events = await db.TrafficEvents.Where(x => x.Domain == d || (x.Domain != null && x.Domain.EndsWith("." + d))).ToListAsync(ct);
            var removedAdultEvents = events.Count(x => x.Category == "Adult");
            db.TrafficEvents.RemoveRange(events.Where(x => x.Category == "Adult"));
            var alerts = await db.Alerts.Where(x => x.Type == "adult-content" && !x.Acknowledged && x.Message.Contains(d)).ToListAsync(ct);
            foreach (var a in alerts) { a.Acknowledged = true; a.AcknowledgedUtc = DateTime.UtcNow; }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { domain = d, removedAdultEvents, acknowledgedAlerts = alerts.Count });
        });
        app.MapDelete("/api/intelligence/adult/safe", (string domain, IAdultDomainClassifier c) =>
        {
            ((AdultDomainClassifier)c).RemoveSafeDomain(domain);
            return Results.Ok(new { domain });
        });

        app.MapGet("/api/discovery/status", (DeviceDiscoveryService discovery) => Results.Ok(discovery.Status()));
        app.MapPost("/api/discovery/scan", (DeviceDiscoveryService discovery) =>
            discovery.TryStart() ? Results.Accepted(value: new { started = true }) : Results.Conflict(new { error = "A discovery scan is already running." }));
        app.MapGet("/api/devices/{id:long}/discovery", (long id, DeviceDiscoveryService discovery) =>
        {
            var result = discovery.Get(id);
            return result is null ? Results.NotFound(new { error = "No discovery data exists for this device yet." }) : Results.Ok(result);
        });

        app.MapPut("/api/devices/{id:long}/infrastructure", async (long id, InfrastructureOverrideUpdate update, HomeWatchDb db, CancellationToken ct) =>
        {
            var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (device is null) return Results.NotFound(new { error = "Device not found." });
            InfrastructureDeviceClassifier.SetOverride(id, update.Infrastructure);
            return Results.Ok(new
            {
                id,
                infrastructure = InfrastructureDeviceClassifier.IsInfrastructure(device),
                manualOverride = InfrastructureDeviceClassifier.GetOverride(id),
                automatic = InfrastructureDeviceClassifier.IsInfrastructure(device.Name, device.Vendor)
            });
        });

        app.MapGet("/api/history", async (HomeWatchDb db, IgnoredDeviceStore ignoredDevices, IgnoredDomainStore ignoredDomains, IAdultDomainClassifier adultClassifier,
            ActivityCorrelationService correlation, int minutes = 60, long? deviceId = null, string? category = null, string? search = null,
            string? visibility = null, string? source = null, string? activity = null, int limit = 200, CancellationToken ct = default) =>
        {
            minutes = Math.Clamp(minutes, 1, 43200);
            limit = Math.Clamp(limit, 1, 500);
            var rows = await LoadActivitiesAsync(db, ignoredDevices, ignoredDomains, adultClassifier, correlation, minutes, deviceId, ct);
            var filtered = ApplyHistoryFilters(rows, category, search, visibility, source, activity).Take(limit).ToArray();
            return Results.Ok(filtered);
        });

        app.MapGet("/api/history/summary", async (HomeWatchDb db, IgnoredDeviceStore ignoredDevices, IgnoredDomainStore ignoredDomains, IAdultDomainClassifier adultClassifier,
            ActivityCorrelationService correlation, int minutes = 60, long? deviceId = null, string? category = null, string? search = null,
            string? visibility = null, string? source = null, string? activity = null, CancellationToken ct = default) =>
        {
            minutes = Math.Clamp(minutes, 1, 43200);
            var rows = await LoadActivitiesAsync(db, ignoredDevices, ignoredDomains, adultClassifier, correlation, minutes, deviceId, ct);
            var filtered = ApplyHistoryFilters(rows, category, search, visibility, source, activity).ToArray();
            var summary = correlation.Summarize(filtered);
            return Results.Ok(new { minutes, summary.EventCount, summary.RawSignalCount, summary.UniqueDomains, summary.ActiveDevices, summary.BlockedCount, summary.EncryptedCount, summary.Categories, summary.Visibility });
        });
    }

    private static async Task<IReadOnlyList<CorrelatedActivity>> LoadActivitiesAsync(
        HomeWatchDb db,
        IgnoredDeviceStore ignoredDevices,
        IgnoredDomainStore ignoredDomains,
        IAdultDomainClassifier adultClassifier,
        ActivityCorrelationService correlation,
        int minutes,
        long? deviceId,
        CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddMinutes(-minutes);
        var ignoredIds = ignoredDevices.GetIds().ToHashSet();
        var safeRoots = ((AdultDomainClassifier)adultClassifier).GetSafeDomains();
        var query = db.TrafficEvents.AsNoTracking()
            .Where(x => (x.TimestampUtc >= since || x.LastSeenUtc >= since)
                && (!x.DeviceId.HasValue || !ignoredIds.Contains(x.DeviceId.Value)));
        if (deviceId.HasValue) query = query.Where(x => x.DeviceId == deviceId.Value);
        var raw = await query.OrderBy(x => x.TimestampUtc).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        raw = raw.Where(x => IsHistoryVisible(x, ignoredDomains, safeRoots)).ToList();
        var deviceIds = raw.Where(x => x.DeviceId.HasValue).Select(x => x.DeviceId!.Value).Distinct().ToArray();
        var devices = await db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        return correlation.Correlate(raw, devices);
    }

    private static IEnumerable<CorrelatedActivity> ApplyHistoryFilters(
        IEnumerable<CorrelatedActivity> source,
        string? category,
        string? search,
        string? visibility,
        string? telemetrySource,
        string? activity)
    {
        var query = source;
        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(visibility) && !visibility.Equals("all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.Visibility.Equals(visibility, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(telemetrySource) && !telemetrySource.Equals("all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.Sources.Any(s => s.Equals(telemetrySource, StringComparison.OrdinalIgnoreCase)));
        if (activity?.Equals("background", StringComparison.OrdinalIgnoreCase) == true) query = query.Where(x => x.Background);
        if (activity?.Equals("foreground", StringComparison.OrdinalIgnoreCase) == true) query = query.Where(x => !x.Background);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim();
            query = query.Where(x => new[]
            {
                x.ExactUrl, x.Domain, x.Service, x.Application, x.Device, x.Ip, x.DestinationIp, x.Protocol, x.Country
            }.Where(v => !string.IsNullOrWhiteSpace(v)).Any(v => v!.Contains(needle, StringComparison.OrdinalIgnoreCase))
            || x.Sources.Any(v => v.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }
        return query;
    }

    private static bool IsHistoryVisible(TrafficEvent x, IgnoredDomainStore ignoredDomains, string[] safeRoots)
    {
        var domain = x.Domain?.Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(domain)) return true;
        if (ignoredDomains.IsIgnored(domain)) return false;
        if (safeRoots.Any(root => domain.Equals(root, StringComparison.OrdinalIgnoreCase) || domain.EndsWith("." + root, StringComparison.OrdinalIgnoreCase))) return false;
        if (domain.Equals("localhost", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (domain.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".ip6.arpa", StringComparison.OrdinalIgnoreCase)) return false;
        if ((x.Protocol ?? string.Empty).Contains("PTR", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(x.SourceIp, "localhost", StringComparison.OrdinalIgnoreCase) || x.SourceIp is "127.0.0.1" or "::1") return false;
        return true;
    }
}

public sealed record SafeDomainUpdate(string? Domain);
public sealed record InfrastructureOverrideUpdate(bool? Infrastructure);
