using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HomeWatch3.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

public sealed record AdultHistoryExportFile(byte[] Content, string FileName);

public static class AdultHistoryExport
{
    private const int MaximumMinutes = 43_200;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<AdultHistoryExportFile> BuildAsync(
        int minutes,
        long? deviceId,
        string? requestedTimeZone,
        TrafficSessionMonitor monitor,
        HomeWatchDb db,
        IgnoredDeviceStore ignoredDevices,
        IgnoredDomainStore ignoredDomains,
        IAdultDomainClassifier adultClassifier,
        EvidenceDomainPolicy domainPolicy,
        CancellationToken cancellationToken)
    {
        minutes = Math.Clamp(minutes, 1, MaximumMinutes);
        var generatedUtc = DateTime.UtcNow;
        var sinceUtc = generatedUtc.AddMinutes(-minutes);
        var ignoredIds = ignoredDevices.GetIds().ToHashSet();
        var safeRoots = ((AdultDomainClassifier)adultClassifier).GetSafeDomains();
        var devices = await db.Devices.AsNoTracking().ToListAsync(cancellationToken);
        var deviceMap = devices.ToDictionary(x => x.Id);

        bool VisibleDevice(long id) =>
            (!deviceId.HasValue || id == deviceId.Value)
            && !ignoredIds.Contains(id)
            && (!deviceMap.TryGetValue(id, out var device) || !InfrastructureDeviceClassifier.IsInfrastructure(device));

        bool VisibleDomain(string? value)
        {
            var domain = value?.Trim().Trim('.').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(domain)) return true;
            if (domainPolicy.IsTrusted(domain)) return false;
            if (ignoredDomains.IsIgnored(domain)) return false;
            return !safeRoots.Any(root => domain.Equals(root, StringComparison.OrdinalIgnoreCase)
                || domain.EndsWith('.' + root, StringComparison.OrdinalIgnoreCase));
        }

        var sessions = monitor.GetSessions(minutes)
            .Where(x => x.Adult && VisibleDevice(x.DeviceId))
            .Where(x => x.Domains.Length == 0 || x.Domains.Any(VisibleDomain))
            .OrderBy(x => x.StartedUtc)
            .ToArray();

        var eventQuery = db.TrafficEvents.AsNoTracking()
            .Where(x => x.Category == "Adult" && (x.TimestampUtc >= sinceUtc || x.LastSeenUtc >= sinceUtc));
        if (deviceId.HasValue) eventQuery = eventQuery.Where(x => x.DeviceId == deviceId.Value);
        var events = (await eventQuery.OrderBy(x => x.TimestampUtc).ThenBy(x => x.Id).ToListAsync(cancellationToken))
            .Where(x => (!x.DeviceId.HasValue || VisibleDevice(x.DeviceId.Value)) && VisibleDomain(x.Domain))
            .ToArray();

        var alertQuery = db.Alerts.AsNoTracking()
            .Where(x => x.Type == "adult-content" && x.CreatedUtc >= sinceUtc);
        if (deviceId.HasValue) alertQuery = alertQuery.Where(x => x.DeviceId == deviceId.Value);
        var alerts = (await alertQuery.OrderBy(x => x.CreatedUtc).ThenBy(x => x.Id).ToListAsync(cancellationToken))
            .Where(x => (!x.DeviceId.HasValue || VisibleDevice(x.DeviceId.Value))
                && !domainPolicy.ContainsTrustedReference(x.Message))
            .ToArray();

        var timeZone = ResolveTimeZone(requestedTimeZone);
        var derived = sessions.Select(session => DeriveSession(session, events, timeZone)).ToArray();
        var behavior = BuildBehaviorSummary(derived, timeZone);
        var exportedDeviceIds = sessions.Select(x => x.DeviceId)
            .Concat(events.Where(x => x.DeviceId.HasValue).Select(x => x.DeviceId!.Value))
            .Distinct()
            .ToHashSet();
        var exportedDevices = devices.Where(x => exportedDeviceIds.Contains(x.Id)).Select(x => new
        {
            x.Id,
            x.Name,
            x.LastIpAddress,
            x.Vendor,
            x.FirstSeenUtc,
            x.LastSeenUtc
        }).ToArray();

        var manifest = new
        {
            format = "homewatch-adult-history-export",
            formatVersion = 1,
            application = "HomeWatch 3",
            applicationVersion = "3.0.0-alpha.32",
            generatedUtc,
            filters = new
            {
                minutes,
                sinceUtc,
                untilUtc = generatedUtc,
                deviceId,
                timeZone = timeZone.Id,
                adultOnly = true,
                ignoredDevicesExcluded = true,
                infrastructureDevicesExcluded = true,
                ignoredAndUserSafeDomainsExcluded = true
            },
            counts = new
            {
                sessions = sessions.Length,
                rawAdultEvents = events.Length,
                adultAlerts = alerts.Length,
                devices = exportedDevices.Length
            },
            evidenceCapabilities = new
            {
                observedAdultService = true,
                observedHostnamesAndSni = true,
                correlatedDnsAndFlowTiming = true,
                correlatedTrafficVolume = true,
                exactHttpsUrl = events.Any(x => !string.IsNullOrWhiteSpace(x.ExactUrl)),
                exactVideoTitle = false,
                exactVideoCount = false,
                pornGenreOrCategory = false,
                definitivePlaybackDuration = false
            },
            security = new
            {
                containsPasswords = false,
                containsAuthenticationTokens = false,
                containsCookies = false,
                containsMoneyPilotSecrets = false
            }
        };

        var sessionDocument = new
        {
            explanation = "Each record separates observed facts from estimates and explicitly unsupported conclusions.",
            sessions = derived
        };
        var rawDocument = new
        {
            explanation = "Raw normalized events classified as Adult. ExactUrl is populated only when an upstream telemetry source actually supplied it.",
            events
        };
        var alertDocument = new
        {
            explanation = "Adult-content alerts emitted by HomeWatch in the selected window.",
            alerts
        };
        var behaviorDocument = new
        {
            explanation = "Aggregate patterns are based on detected adult-service sessions, not verified human viewing or playback.",
            behavior
        };

        await using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJson(archive, "manifest.json", manifest);
            WriteJson(archive, "adult-sessions.json", sessionDocument);
            WriteJson(archive, "behavior-summary.json", behaviorDocument);
            WriteJson(archive, "raw-adult-events.json", rawDocument);
            WriteJson(archive, "adult-alerts.json", alertDocument);
            WriteJson(archive, "devices.json", exportedDevices);
            WriteText(archive, "sessions.csv", BuildSessionsCsv(derived));
            WriteText(archive, "README.txt", BuildReadme(timeZone.Id));
        }

        var fileName = $"homewatch-adult-history-{generatedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}Z.zip";
        return new AdultHistoryExportFile(output.ToArray(), fileName);
    }

    private static object DeriveSession(VideoSessionRecord session, TrafficEvent[] events, TimeZoneInfo timeZone)
    {
        var observedWindowSeconds = Math.Max(0, (int)Math.Round((session.LastSeenUtc - session.StartedUtc).TotalSeconds));
        var flowActiveSeconds = UnionSeconds(session.FlowEvidence.Select(x => (x.StartedUtc, x.LastSeenUtc)));
        var trafficBursts = CountTrafficBursts(session.FlowEvidence, TimeSpan.FromMinutes(2));
        var activityEstimateSeconds = flowActiveSeconds > 0
            ? flowActiveSeconds
            : session.Evidence.Length > 1 ? observedWindowSeconds : 0;
        var activityEstimateMethod = flowActiveSeconds > 0
            ? "Union of observed ntopng flow windows"
            : session.Evidence.Length > 1 ? "First-to-last relevant DNS signal" : "Insufficient duration evidence";
        var activityEstimateConfidence = flowActiveSeconds > 0 ? "medium" : session.Evidence.Length > 1 ? "low" : "insufficient";
        var exactUrls = events.Where(x => x.DeviceId == session.DeviceId
                && !string.IsNullOrWhiteSpace(x.ExactUrl)
                && (x.LastSeenUtc ?? x.TimestampUtc) >= session.StartedUtc
                && (x.StartedUtc ?? x.TimestampUtc) <= session.LastSeenUtc)
            .Select(x => x.ExactUrl!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var localStart = ConvertUtc(session.StartedUtc, timeZone);
        var localEnd = ConvertUtc(session.LastSeenUtc, timeZone);
        var hasFlowEvidence = session.FlowEvidence.Length > 0;
        var correlatedBytesDown = hasFlowEvidence
            ? SumUniqueFlowBytes(session.FlowEvidence, x => x.BytesDown)
            : Math.Max(0, session.AttributedBytesDown);
        var correlatedBytesUp = hasFlowEvidence
            ? SumUniqueFlowBytes(session.FlowEvidence, x => x.BytesUp)
            : Math.Max(0, session.AttributedBytesUp);

        return new
        {
            sessionId = session.Id,
            session.DeviceId,
            session.DeviceName,
            session.Ip,
            session.Service,
            classification = "Adult service",
            session.StartedUtc,
            session.LastSeenUtc,
            session.EndedUtc,
            localStarted = localStart,
            localLastSeen = localEnd,
            timeZone = timeZone.Id,
            observations = new
            {
                observedWindowSeconds,
                flowActiveSeconds,
                trafficBursts,
                dnsSignalCount = session.Evidence.Length,
                flowSignalCount = session.FlowEvidence.Length,
                session.Domains,
                exactUrls,
                session.Applications,
                session.Protocols,
                session.Visibility,
                session.TelemetrySources,
                session.BlockedRequests,
                correlatedBytesDown,
                correlatedBytesUp,
                session.AttributionConfidence
            },
            activityEstimate = new
            {
                seconds = activityEstimateSeconds,
                method = activityEstimateMethod,
                confidence = activityEstimateConfidence,
                caveat = "This is estimated network activity, not verified continuous playback or time spent watching."
            },
            videoEstimate = new
            {
                supported = false,
                count = (int?)null,
                titles = Array.Empty<string>(),
                reason = "DNS, SNI and flow telemetry do not expose a reliable media item identifier or player events. Traffic bursts are not video counts."
            },
            contentGenreEstimate = new
            {
                supported = false,
                categories = Array.Empty<string>(),
                reason = "The observed service/domain can establish a broad Adult classification, but not the genre or category of encrypted page or video content."
            },
            rawEvidence = new
            {
                dns = session.Evidence,
                flows = session.FlowEvidence,
                session.ResolvedServiceIps,
                session.MatchedRemoteIps,
                session.Policies
            }
        };
    }

    private static object BuildBehaviorSummary(object[] derived, TimeZoneInfo timeZone)
    {
        var rows = derived.Select(value => JsonSerializer.SerializeToElement(value, JsonOptions)).ToArray();
        var sessionRows = rows.Select(row => new
        {
            Device = row.GetProperty("deviceName").GetString() ?? "Unknown device",
            Service = row.GetProperty("service").GetString() ?? "Unknown service",
            LocalStart = row.GetProperty("localStarted").GetDateTime(),
            ObservedSeconds = row.GetProperty("observations").GetProperty("observedWindowSeconds").GetInt32(),
            EstimatedSeconds = row.GetProperty("activityEstimate").GetProperty("seconds").GetInt32(),
            BytesDown = row.GetProperty("observations").GetProperty("correlatedBytesDown").GetInt64(),
            BytesUp = row.GetProperty("observations").GetProperty("correlatedBytesUp").GetInt64()
        }).ToArray();
        var observed = sessionRows.Select(x => x.ObservedSeconds).OrderBy(x => x).ToArray();
        var activeDays = sessionRows.Select(x => DateOnly.FromDateTime(x.LocalStart)).Distinct().Count();

        return new
        {
            timeZone = timeZone.Id,
            sessionCount = sessionRows.Length,
            activeDays,
            sessionsPerActiveDay = activeDays == 0 ? 0 : Math.Round((double)sessionRows.Length / activeDays, 2),
            totalObservedWindowSeconds = sessionRows.Sum(x => (long)x.ObservedSeconds),
            totalEstimatedNetworkActivitySeconds = sessionRows.Sum(x => (long)x.EstimatedSeconds),
            medianObservedWindowSeconds = Median(observed),
            longestObservedWindowSeconds = observed.DefaultIfEmpty().Max(),
            correlatedBytesDown = sessionRows.Sum(x => x.BytesDown),
            correlatedBytesUp = sessionRows.Sum(x => x.BytesUp),
            lateNightSessionCount = sessionRows.Count(x => x.LocalStart.Hour < 5),
            byService = sessionRows.GroupBy(x => x.Service, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count()).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase),
            byDevice = sessionRows.GroupBy(x => x.Device, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count()).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase),
            byDayOfWeek = sessionRows.GroupBy(x => x.LocalStart.DayOfWeek.ToString())
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase),
            byHourOfDay = sessionRows.GroupBy(x => x.LocalStart.Hour).OrderBy(x => x.Key)
                .ToDictionary(x => x.Key.ToString("00", CultureInfo.InvariantCulture), x => x.Count()),
            byDate = sessionRows.GroupBy(x => DateOnly.FromDateTime(x.LocalStart)).OrderBy(x => x.Key)
                .ToDictionary(x => x.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), x => x.Count()),
            caveat = "Patterns describe detected adult-service network sessions. They do not establish who used the device, what was watched, or whether playback was continuous."
        };
    }

    private static int UnionSeconds(IEnumerable<(DateTime Start, DateTime End)> source)
    {
        var intervals = source.Select(x => x.End < x.Start ? (x.End, x.Start) : x)
            .OrderBy(x => x.Item1)
            .ToArray();
        if (intervals.Length == 0) return 0;
        var total = TimeSpan.Zero;
        var currentStart = intervals[0].Item1;
        var currentEnd = intervals[0].Item2;
        foreach (var interval in intervals.Skip(1))
        {
            if (interval.Item1 <= currentEnd)
            {
                if (interval.Item2 > currentEnd) currentEnd = interval.Item2;
                continue;
            }
            total += currentEnd - currentStart;
            currentStart = interval.Item1;
            currentEnd = interval.Item2;
        }
        total += currentEnd - currentStart;
        return Math.Max(0, (int)Math.Round(total.TotalSeconds));
    }

    private static int CountTrafficBursts(IEnumerable<SessionFlowEvidence> flows, TimeSpan maximumGap)
    {
        var intervals = flows.OrderBy(x => x.StartedUtc).ToArray();
        if (intervals.Length == 0) return 0;
        var count = 1;
        var end = intervals[0].LastSeenUtc;
        foreach (var flow in intervals.Skip(1))
        {
            if (flow.StartedUtc > end + maximumGap) count++;
            if (flow.LastSeenUtc > end) end = flow.LastSeenUtc;
        }
        return count;
    }

    private static long SumUniqueFlowBytes(IEnumerable<SessionFlowEvidence> flows, Func<SessionFlowEvidence, long> selector) =>
        flows.GroupBy(x => x.FlowId, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(x => x.LastSeenUtc).First())
            .Sum(flow => Math.Max(0, selector(flow)));

    private static double Median(int[] values)
    {
        if (values.Length == 0) return 0;
        var middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2d;
    }

    private static DateTime ConvertUtc(DateTime value, TimeZoneInfo timeZone)
    {
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
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

    private static string BuildSessionsCsv(object[] derived)
    {
        var builder = new StringBuilder();
        builder.AppendLine("session_id,device,ip,service,started_utc,last_seen_utc,observed_window_seconds,estimated_network_activity_seconds,activity_estimate_confidence,correlated_bytes_down,correlated_bytes_up,domains,video_count,title_or_genre");
        foreach (var value in derived)
        {
            var row = JsonSerializer.SerializeToElement(value, JsonOptions);
            var observations = row.GetProperty("observations");
            var estimate = row.GetProperty("activityEstimate");
            var domains = string.Join(" | ", observations.GetProperty("domains").EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)));
            var values = new[]
            {
                row.GetProperty("sessionId").ToString(),
                row.GetProperty("deviceName").GetString(),
                row.GetProperty("ip").GetString(),
                row.GetProperty("service").GetString(),
                row.GetProperty("startedUtc").GetDateTime().ToString("O", CultureInfo.InvariantCulture),
                row.GetProperty("lastSeenUtc").GetDateTime().ToString("O", CultureInfo.InvariantCulture),
                observations.GetProperty("observedWindowSeconds").GetInt32().ToString(CultureInfo.InvariantCulture),
                estimate.GetProperty("seconds").GetInt32().ToString(CultureInfo.InvariantCulture),
                estimate.GetProperty("confidence").GetString(),
                observations.GetProperty("correlatedBytesDown").GetInt64().ToString(CultureInfo.InvariantCulture),
                observations.GetProperty("correlatedBytesUp").GetInt64().ToString(CultureInfo.InvariantCulture),
                domains,
                "unknown",
                "unknown"
            };
            builder.AppendLine(string.Join(",", values.Select(Csv)));
        }
        return builder.ToString();
    }

    private static string Csv(string? value)
    {
        var safe = value ?? string.Empty;
        if (safe.Length > 0 && safe[0] is '=' or '+' or '-' or '@') safe = '\'' + safe;
        return '"' + safe.Replace("\"", "\"\"") + '"';
    }

    private static string BuildReadme(string timeZone) => $"""
        HomeWatch adult-history analysis export
        ======================================

        Upload this entire ZIP when requesting an analysis. The most useful files are:
        - adult-sessions.json: session-level observations, estimates, and DNS/flow evidence
        - behavior-summary.json: frequency, time-of-day, service, device, and duration-window aggregates
        - raw-adult-events.json: normalized adult-classified telemetry
        - sessions.csv: compact spreadsheet-friendly session summary

        Times are UTC unless a field starts with "local". Local aggregates use {timeZone}.

        Evidence limits:
        - A service/domain, SNI, application, remote address, timing window, and correlated bytes may be observed.
        - Estimated network-active time is not definitive watch time and may include buffering, page assets, or background traffic.
        - A traffic burst is not a video and must not be counted as one.
        - DNS/SNI/flow evidence does not reveal an encrypted HTTPS path, search term, page title, exact video, or porn genre.
        - Exact title, genre, per-video duration, and video count remain unknown unless a separate upstream source supplies URLs/page titles, player events, or actual media.

        The bundle intentionally contains no passwords, authentication tokens, cookies, or MoneyPilot secrets.
        """;

    private static void WriteJson(ZipArchive archive, string name, object value) =>
        WriteText(archive, name, JsonSerializer.Serialize(value, JsonOptions));

    private static void WriteText(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }
}
