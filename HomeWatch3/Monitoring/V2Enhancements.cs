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
            int minutes = 60, long? deviceId = null, string? category = null, string? search = null, int limit = 200, CancellationToken ct = default) =>
        {
            minutes = Math.Clamp(minutes, 1, 10080); limit = Math.Clamp(limit, 1, 500); var since = DateTime.UtcNow.AddMinutes(-minutes);
            var ignoredIds = ignoredDevices.GetIds().ToHashSet();
            var safeRoots = ((AdultDomainClassifier)adultClassifier).GetSafeDomains();
            var q = db.TrafficEvents.AsNoTracking().Where(x => x.TimestampUtc >= since && (!x.DeviceId.HasValue || !ignoredIds.Contains(x.DeviceId.Value)));
            if (deviceId.HasValue) q = q.Where(x => x.DeviceId == deviceId.Value);
            if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase)) q = q.Where(x => x.Category == category);
            if (!string.IsNullOrWhiteSpace(search)) { var s = search.Trim().ToLower(); q = q.Where(x => (x.Domain ?? "").ToLower().Contains(s) || (x.SourceIp ?? "").ToLower().Contains(s)); }
            var rows = await q.OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Id).Take(Math.Min(2500, limit * 5)).ToListAsync(ct);
            var visibleRows = rows.Where(x => IsHistoryVisible(x, ignoredDomains, safeRoots)).Take(limit).ToList();
            var deviceIds = visibleRows.Where(x => x.DeviceId.HasValue).Select(x => x.DeviceId!.Value).Distinct().ToArray();
            var devices = await db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            var filtered = visibleRows.Select(x =>
            {
                Device? device = null;
                if (x.DeviceId.HasValue) devices.TryGetValue(x.DeviceId.Value, out device);
                return new { x.Id, x.TimestampUtc, x.DeviceId, device = device?.Name ?? device?.LastIpAddress ?? x.SourceIp, ip = x.SourceIp, x.Domain, x.Category, x.Protocol, x.Source, x.Confidence, x.Blocked };
            }).ToArray();
            return Results.Ok(filtered);
        });

        app.MapGet("/api/history/summary", async (HomeWatchDb db, IgnoredDeviceStore ignoredDevices, IgnoredDomainStore ignoredDomains, IAdultDomainClassifier adultClassifier, int minutes = 60, CancellationToken ct = default) =>
        {
            minutes = Math.Clamp(minutes, 1, 10080); var since = DateTime.UtcNow.AddMinutes(-minutes); var ignoredIds = ignoredDevices.GetIds().ToHashSet();
            var safeRoots = ((AdultDomainClassifier)adultClassifier).GetSafeDomains();
            var rows = await db.TrafficEvents.AsNoTracking().Where(x => x.TimestampUtc >= since && (!x.DeviceId.HasValue || !ignoredIds.Contains(x.DeviceId.Value))).ToListAsync(ct);
            rows = rows.Where(x => IsHistoryVisible(x, ignoredDomains, safeRoots)).ToList();
            return Results.Ok(new { minutes, eventCount = rows.Count, uniqueDomains = rows.Select(x => x.Domain).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count(), activeDevices = rows.Select(x => x.DeviceId).Where(x => x.HasValue).Distinct().Count(), blockedCount = rows.Count(x => x.Blocked), adultCount = rows.Count(x => x.Category == "Adult"), categories = rows.GroupBy(x => x.Category).Select(g => new { category = g.Key, count = g.Count() }).OrderByDescending(x => x.count).ToArray() });
        });
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
