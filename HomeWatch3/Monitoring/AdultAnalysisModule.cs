using HomeWatch3.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

public static class AdultAnalysisModule
{
    private static readonly TimeSpan FlowJoinMargin = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BurstGap = TimeSpan.FromMinutes(2);
    private const long SignificantMediaBytes = 10_000_000;

    public static IServiceCollection AddAdultAnalysisModule(this IServiceCollection services)
    {
        services.AddSingleton<AdultAnalysisSettingsStore>();
        return services;
    }

    public static void MapAdultAnalysisModule(this WebApplication app)
    {
        app.MapGet("/api/adult-analysis/settings", (AdultAnalysisSettingsStore settings) => Results.Ok(settings.GetSnapshot()));
        app.MapPut("/api/adult-analysis/settings", (AdultAnalysisDefaultsUpdate update, AdultAnalysisSettingsStore settings) =>
            Results.Ok(settings.SetDefaults(update.HideAdvertisingByDefault)));
        app.MapPost("/api/adult-analysis/advertising-domains", (AdultAdvertisingDomainUpdate update, AdultAnalysisSettingsStore settings) =>
        {
            var domain = settings.AddAdvertisingDomain(update.Domain);
            return domain is null
                ? Results.BadRequest(new { error = "Enter a valid advertising domain." })
                : Results.Ok(new { domain, settings = settings.GetSnapshot() });
        });
        app.MapDelete("/api/adult-analysis/advertising-domains", (string domain, AdultAnalysisSettingsStore settings) =>
        {
            settings.RemoveAdvertisingDomain(domain);
            return Results.Ok(new { domain, settings = settings.GetSnapshot() });
        });
        app.MapGet("/api/adult-analysis", (
            int? minutes,
            long? deviceId,
            bool? includeAds,
            string? timeZone,
            TrafficSessionMonitor monitor,
            HomeWatchDb db,
            IgnoredDeviceStore ignoredDevices,
            IgnoredDomainStore ignoredDomains,
            EvidenceDomainPolicy domainPolicy,
            AdultAnalysisSettingsStore settings,
            IAdultDomainClassifier adultClassifier,
            CancellationToken cancellationToken) =>
            BuildAsync(minutes, deviceId, includeAds, timeZone, monitor, db, ignoredDevices,
                ignoredDomains, domainPolicy, settings, adultClassifier, cancellationToken));
    }

    private static async Task<IResult> BuildAsync(
        int? minutes,
        long? deviceId,
        bool? includeAds,
        string? timeZone,
        TrafficSessionMonitor monitor,
        HomeWatchDb db,
        IgnoredDeviceStore ignoredDevices,
        IgnoredDomainStore ignoredDomains,
        EvidenceDomainPolicy domainPolicy,
        AdultAnalysisSettingsStore settings,
        IAdultDomainClassifier adultClassifier,
        CancellationToken cancellationToken)
    {
        var selectedMinutes = Math.Clamp(minutes ?? 1440, 1, 43_200);
        var sinceUtc = DateTime.UtcNow.AddMinutes(-selectedMinutes);
        var configured = settings.GetSnapshot();
        var showAds = includeAds ?? !configured.HideAdvertisingByDefault;
        var ignoredIds = ignoredDevices.GetIds().ToHashSet();
        var devices = await db.Devices.AsNoTracking().ToListAsync(cancellationToken);
        var deviceMap = devices.ToDictionary(x => x.Id);

        bool VisibleDevice(long id) =>
            (!deviceId.HasValue || deviceId.Value == id)
            && !ignoredIds.Contains(id)
            && (!deviceMap.TryGetValue(id, out var device) || !InfrastructureDeviceClassifier.IsInfrastructure(device));

        bool VisibleDomain(string? value) =>
            !domainPolicy.IsTrusted(value) && !ignoredDomains.IsIgnored(value);

        var sessions = monitor.GetSessions(selectedMinutes)
            .Where(x => x.Adult && VisibleDevice(x.DeviceId))
            .Where(x => x.Domains.Length == 0 || x.Domains.Any(VisibleDomain))
            .OrderBy(x => x.StartedUtc)
            .ToArray();

        var eventQuery = db.TrafficEvents.AsNoTracking()
            .Where(x => x.Category == "Adult"
                && (x.TimestampUtc >= sinceUtc || x.LastSeenUtc >= sinceUtc));
        if (deviceId.HasValue) eventQuery = eventQuery.Where(x => x.DeviceId == deviceId.Value);
        var rawEvents = (await eventQuery.OrderBy(x => x.TimestampUtc).ThenBy(x => x.Id).ToListAsync(cancellationToken))
            .Where(x => (!x.DeviceId.HasValue || VisibleDevice(x.DeviceId.Value)) && VisibleDomain(x.Domain))
            .ToArray();

        var zone = ResolveTimeZone(timeZone);
        var eventAssignments = AssignEvents(sessions, rawEvents);
        var rows = sessions.Select(session => BuildSession(
            session,
            eventAssignments.TryGetValue(session.Id, out var assigned) ? assigned : Array.Empty<TrafficEvent>(),
            settings,
            adultClassifier,
            showAds,
            zone)).ToArray();

        var assignedEventIds = eventAssignments.Values.SelectMany(x => x).Select(x => x.Id).ToHashSet();
        var unassigned = rawEvents.Where(x => !assignedEventIds.Contains(x.Id))
            .GroupBy(x => NormalizeDomain(x.Domain) ?? x.DestinationIp ?? "unknown", StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                domain = group.Key,
                firstSeenUtc = group.Min(StartUtc),
                lastSeenUtc = group.Max(EndUtc),
                signals = group.Count(),
                bytesDown = group.Sum(x => Math.Max(0, x.BytesDown ?? 0)),
                bytesUp = group.Sum(x => Math.Max(0, x.BytesUp ?? 0)),
                advertising = settings.IsAdvertising(group.Key),
                confidence = group.Max(x => x.Confidence),
                devices = group.Where(x => x.DeviceId.HasValue).Select(x => x.DeviceId!.Value).Distinct().ToArray()
            })
            .Where(x => showAds || !x.advertising)
            .OrderByDescending(x => x.lastSeenUtc)
            .ToArray();

        var visibleDevices = sessions.Select(x => x.DeviceId)
            .Concat(rawEvents.Where(x => x.DeviceId.HasValue).Select(x => x.DeviceId!.Value))
            .Distinct()
            .Where(deviceMap.ContainsKey)
            .Select(id => new { deviceMap[id].Id, deviceMap[id].Name, deviceMap[id].LastIpAddress })
            .OrderBy(x => x.Name)
            .ToArray();

        return Results.Ok(new
        {
            generatedUtc = DateTime.UtcNow,
            filters = new { minutes = selectedMinutes, deviceId, includeAds = showAds, timeZone = zone.Id },
            summary = new
            {
                sessionCount = rows.Length,
                activeDevices = rows.Select(x => x.DeviceId).Distinct().Count(),
                totalObservedWindowSeconds = rows.Sum(x => x.ObservedWindowSeconds),
                totalNetworkActiveSeconds = rows.Sum(x => x.NetworkActiveSeconds),
                totalBytesDown = rows.Sum(x => x.BytesDown),
                totalBytesUp = rows.Sum(x => x.BytesUp),
                mediaDeliveryBytes = rows.Sum(x => x.MediaDeliveryBytes),
                significantMediaTransfers = rows.Sum(x => x.SignificantMediaTransfers),
                mediaDeliveryPhases = rows.Sum(x => x.MediaDeliveryPhases),
                filteredAdvertisingFlows = rows.Sum(x => x.FilteredAdvertisingFlows),
                filteredAdvertisingBytes = rows.Sum(x => x.FilteredAdvertisingBytes),
                unassignedSignals = unassigned.Length
            },
            evidenceLimits = new
            {
                exactUrlsAvailable = rawEvents.Any(x => !string.IsNullOrWhiteSpace(x.ExactUrl)),
                exactVideoCountAvailable = false,
                exactVideoTitlesAvailable = false,
                pornCategoryAvailable = false,
                definitiveWatchDurationAvailable = false,
                explanation = "Media delivery, browsing assets, advertising, timing, and byte volume are observable. A delivery phase is not a video count."
            },
            devices = visibleDevices,
            sessions = rows,
            unassignedSignals = unassigned,
            advertising = configured
        });
    }

    private static AdultSessionAnalysis BuildSession(
        VideoSessionRecord session,
        IEnumerable<TrafficEvent> relatedEvents,
        AdultAnalysisSettingsStore settings,
        IAdultDomainClassifier adultClassifier,
        bool includeAds,
        TimeZoneInfo timeZone)
    {
        var flows = relatedEvents
            .Where(x => "ntopng-flow".Equals(x.Source, StringComparison.OrdinalIgnoreCase))
            .Select(x => FromTrafficEvent(x, session.Service, settings, adultClassifier))
            .Concat(session.FlowEvidence.Select(x => FromSessionFlow(x, session.Service, settings, adultClassifier)))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(x => x.LastSeenUtc).ThenByDescending(x => x.BytesDown).First())
            .OrderBy(x => x.StartedUtc)
            .ToArray();
        var visibleFlows = flows.Where(x => includeAds || !x.Advertising).ToArray();
        var hiddenAds = flows.Where(x => x.Advertising && !includeAds).ToArray();
        var mediaFlows = visibleFlows.Where(x => x.MediaDelivery).ToArray();
        var significantMedia = mediaFlows.Where(x => x.BytesDown >= SignificantMediaBytes).ToArray();
        var visibleDomains = visibleFlows.Select(x => x.Hostname)
            .Concat(session.Domains.Where(domain => includeAds || !settings.IsAdvertising(domain)))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var advertisingDomains = hiddenAds.Select(x => x.Hostname)
            .Concat(session.Domains.Where(settings.IsAdvertising))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var bytesDown = flows.Length == 0 ? Math.Max(0, session.AttributedBytesDown) : visibleFlows.Sum(x => x.BytesDown);
        var bytesUp = flows.Length == 0 ? Math.Max(0, session.AttributedBytesUp) : visibleFlows.Sum(x => x.BytesUp);
        var localStarted = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(session.StartedUtc, DateTimeKind.Utc), timeZone);
        var localLastSeen = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(session.LastSeenUtc, DateTimeKind.Utc), timeZone);

        return new AdultSessionAnalysis(
            session.Id,
            session.DeviceId,
            session.DeviceName,
            session.Ip,
            session.Service,
            session.StartedUtc,
            session.LastSeenUtc,
            localStarted,
            localLastSeen,
            timeZone.Id,
            Math.Max(0, (int)Math.Round((session.LastSeenUtc - session.StartedUtc).TotalSeconds)),
            UnionSeconds(visibleFlows.Select(x => (x.StartedUtc, x.LastSeenUtc))),
            CountBursts(visibleFlows),
            bytesDown,
            bytesUp,
            mediaFlows.Sum(x => x.BytesDown),
            mediaFlows.Length,
            significantMedia.Length,
            CountBursts(significantMedia),
            hiddenAds.Length,
            hiddenAds.Sum(x => x.BytesDown + x.BytesUp),
            session.AttributionConfidence,
            visibleDomains,
            advertisingDomains,
            visibleFlows,
            "Strong media-delivery evidence can establish streaming traffic, but it cannot identify a reliable video count, title, genre, or continuous watch duration.");
    }

    private static Dictionary<Guid, TrafficEvent[]> AssignEvents(VideoSessionRecord[] sessions, TrafficEvent[] events)
    {
        var assignments = sessions.ToDictionary(x => x.Id, _ => new List<TrafficEvent>());
        foreach (var row in events)
        {
            var normalizedFlowId = NormalizeFlowKey(row.ExternalId);
            var target = sessions
                .Where(session => row.DeviceId == session.DeviceId
                    && EndUtc(row) >= session.StartedUtc - FlowJoinMargin
                    && StartUtc(row) <= session.LastSeenUtc + FlowJoinMargin)
                .OrderByDescending(session => normalizedFlowId is not null
                    && session.FlowEvidence.Any(flow => NormalizeFlowKey(flow.FlowId)?.Equals(normalizedFlowId, StringComparison.Ordinal) == true))
                .ThenByDescending(session => MatchesService(row.Domain, session.Service) || MatchesService(row.Application, session.Service))
                .ThenBy(session => DistanceFromSession(row, session))
                .FirstOrDefault();
            if (target is not null) assignments[target.Id].Add(row);
        }
        return assignments.ToDictionary(x => x.Key, x => x.Value.ToArray());
    }

    private static long DistanceFromSession(TrafficEvent row, VideoSessionRecord session)
    {
        if (EndUtc(row) < session.StartedUtc) return (session.StartedUtc - EndUtc(row)).Ticks;
        if (StartUtc(row) > session.LastSeenUtc) return (StartUtc(row) - session.LastSeenUtc).Ticks;
        return 0;
    }

    private static AdultFlowAnalysis FromTrafficEvent(
        TrafficEvent row,
        string service,
        AdultAnalysisSettingsStore settings,
        IAdultDomainClassifier adultClassifier)
    {
        var hostname = NormalizeDomain(row.Domain);
        var started = StartUtc(row);
        var ended = EndUtc(row);
        var key = NormalizeFlowKey(row.ExternalId) ?? $"event:{row.Id}";
        var bytesDown = Math.Max(0, row.BytesDown ?? 0);
        var bytesUp = Math.Max(0, row.BytesUp ?? 0);
        return BuildFlow(key, started, ended, hostname, row.Application, row.Protocol, row.DestinationIp,
            row.DestinationPort, bytesDown, bytesUp, row.Encrypted, row.Confidence, row.Source,
            RedactQuery(row.ExactUrl), service, settings, adultClassifier);
    }

    private static AdultFlowAnalysis FromSessionFlow(
        SessionFlowEvidence row,
        string service,
        AdultAnalysisSettingsStore settings,
        IAdultDomainClassifier adultClassifier) =>
        BuildFlow(NormalizeFlowKey(row.FlowId) ?? row.FlowId, row.StartedUtc, row.LastSeenUtc,
            NormalizeDomain(row.Hostname), row.Application, row.Protocol, row.RemoteIp, row.RemotePort,
            Math.Max(0, row.BytesDown), Math.Max(0, row.BytesUp), row.Encrypted, row.Confidence,
            row.Source, null, service, settings, adultClassifier);

    private static AdultFlowAnalysis BuildFlow(
        string key,
        DateTime started,
        DateTime ended,
        string? hostname,
        string? application,
        string? protocol,
        string? remoteIp,
        int? remotePort,
        long bytesDown,
        long bytesUp,
        bool encrypted,
        int confidence,
        string source,
        string? exactUrl,
        string service,
        AdultAnalysisSettingsStore settings,
        IAdultDomainClassifier adultClassifier)
    {
        var advertising = settings.IsAdvertising(hostname);
        var media = IsMediaDelivery(hostname, application);
        var role = advertising ? "Advertising / redirect"
            : media ? "Media delivery"
            : IsBrowsingAsset(hostname) ? "Browsing assets"
            : MatchesService(hostname, service) ? "Adult service"
            : adultClassifier.Classify(hostname).IsAdult ? "Adjacent adult service"
            : "Supporting traffic";
        return new(key, started, ended, hostname, application, protocol, remoteIp, remotePort,
            bytesDown, bytesUp, encrypted, confidence, source, exactUrl, role, advertising, media);
    }

    private static bool IsMediaDelivery(string? hostname, string? application)
    {
        var signal = $"{hostname} {application}";
        return new[] { "hls-", "mp4-", "video", "stream", "mpeg", "dash" }
            .Any(token => signal.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsBrowsingAsset(string? hostname) =>
        new[] { "thumb", "profile", "asset", "image", "static", "preview" }
            .Any(token => (hostname ?? string.Empty).Contains(token, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesService(string? hostname, string service)
    {
        var compact = new string(service.Where(char.IsLetterOrDigit).ToArray());
        var host = new string((hostname ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        return compact.Length > 2 && host.Contains(compact, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountBursts(IEnumerable<AdultFlowAnalysis> source)
    {
        var rows = source.OrderBy(x => x.StartedUtc).ToArray();
        if (rows.Length == 0) return 0;
        var count = 1;
        var end = rows[0].LastSeenUtc;
        foreach (var row in rows.Skip(1))
        {
            if (row.StartedUtc > end + BurstGap) count++;
            if (row.LastSeenUtc > end) end = row.LastSeenUtc;
        }
        return count;
    }

    private static int UnionSeconds(IEnumerable<(DateTime Start, DateTime End)> source)
    {
        var rows = source
            .Select(x => x.End < x.Start ? (Start: x.End, End: x.Start) : x)
            .OrderBy(x => x.Start)
            .ToArray();
        if (rows.Length == 0) return 0;
        var start = rows[0].Start;
        var end = rows[0].End;
        var total = TimeSpan.Zero;
        foreach (var row in rows.Skip(1))
        {
            if (row.Start <= end)
            {
                if (row.End > end) end = row.End;
                continue;
            }
            total += end - start;
            start = row.Start;
            end = row.End;
        }
        total += end - start;
        return Math.Max(0, (int)Math.Round(total.TotalSeconds));
    }

    private static DateTime StartUtc(TrafficEvent row) => row.StartedUtc ?? row.TimestampUtc;
    private static DateTime EndUtc(TrafficEvent row) => row.LastSeenUtc ?? row.TimestampUtc;
    private static string? NormalizeDomain(string? value) => TelemetryNaming.NormalizeHostname(value);
    private static string? NormalizeFlowKey(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.StartsWith("ntopng:", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;

    private static string? RedactQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value.Split('?', '#')[0];
        return uri.GetLeftPart(UriPartial.Path);
    }

    private static TimeZoneInfo ResolveTimeZone(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && requested.Length <= 100)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(requested); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    private sealed record AdultFlowAnalysis(
        string Key,
        DateTime StartedUtc,
        DateTime LastSeenUtc,
        string? Hostname,
        string? Application,
        string? Protocol,
        string? RemoteIp,
        int? RemotePort,
        long BytesDown,
        long BytesUp,
        bool Encrypted,
        int Confidence,
        string Source,
        string? ExactUrl,
        string Role,
        bool Advertising,
        bool MediaDelivery);

    private sealed record AdultSessionAnalysis(
        Guid SessionId,
        long DeviceId,
        string DeviceName,
        string Ip,
        string Service,
        DateTime StartedUtc,
        DateTime LastSeenUtc,
        DateTime LocalStarted,
        DateTime LocalLastSeen,
        string TimeZone,
        int ObservedWindowSeconds,
        int NetworkActiveSeconds,
        int TrafficBursts,
        long BytesDown,
        long BytesUp,
        long MediaDeliveryBytes,
        int MediaFlowCount,
        int SignificantMediaTransfers,
        int MediaDeliveryPhases,
        int FilteredAdvertisingFlows,
        long FilteredAdvertisingBytes,
        int Confidence,
        string?[] Domains,
        string?[] AdvertisingDomains,
        AdultFlowAnalysis[] Flows,
        string Caveat);
}
