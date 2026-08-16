using HomeWatch3.Data;

namespace HomeWatch3.Monitoring;

public sealed record CorrelatedActivity(
    long Id,
    DateTime TimestampUtc,
    DateTime StartedUtc,
    DateTime LastSeenUtc,
    long? DeviceId,
    string Device,
    string? Ip,
    string? ExactUrl,
    string? Domain,
    string Service,
    string? Application,
    string Category,
    string? Protocol,
    string? DestinationIp,
    int? DestinationPort,
    string? Country,
    int DurationSeconds,
    long BytesUp,
    long BytesDown,
    bool Blocked,
    bool Encrypted,
    string Visibility,
    int Confidence,
    bool Background,
    int EventCount,
    string[] Sources,
    string[] Domains,
    string[] Applications,
    string[] Protocols,
    string[] DestinationIps);

public sealed record CorrelatedActivitySummary(
    int EventCount,
    int RawSignalCount,
    int UniqueDomains,
    int ActiveDevices,
    int BlockedCount,
    int EncryptedCount,
    IReadOnlyDictionary<string, int> Categories,
    IReadOnlyDictionary<string, int> Visibility);

public sealed class ActivityCorrelationService
{
    private static readonly TimeSpan CorrelationWindow = TimeSpan.FromSeconds(90);

    public IReadOnlyList<CorrelatedActivity> Correlate(
        IEnumerable<TrafficEvent> source,
        IReadOnlyDictionary<long, Device> devices)
    {
        var latestByKey = new Dictionary<string, ActivityAccumulator>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<ActivityAccumulator>();

        foreach (var row in source.OrderBy(StartUtc).ThenBy(x => x.Id))
        {
            var start = StartUtc(row);
            var end = EndUtc(row);
            if (end < start) (start, end) = (end, start);
            var key = CorrelationKey(row);
            if (!latestByKey.TryGetValue(key, out var group)
                || start > group.LastSeenUtc + CorrelationWindow
                || end < group.StartedUtc - CorrelationWindow)
            {
                group = new ActivityAccumulator(row, start, end);
                latestByKey[key] = group;
                groups.Add(group);
            }
            else
            {
                group.Add(row, start, end);
            }
        }

        return groups
            .Select(x => x.ToRecord(devices))
            .OrderByDescending(x => x.LastSeenUtc)
            .ThenByDescending(x => x.Id)
            .ToArray();
    }

    public CorrelatedActivitySummary Summarize(IEnumerable<CorrelatedActivity> source)
    {
        var rows = source.ToArray();
        return new(
            rows.Length,
            rows.Sum(x => x.EventCount),
            rows.SelectMany(x => x.Domains).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            rows.Where(x => x.DeviceId.HasValue).Select(x => x.DeviceId).Distinct().Count(),
            rows.Count(x => x.Blocked),
            rows.Count(x => x.Encrypted),
            rows.GroupBy(x => x.Category ?? "Other", StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase),
            rows.GroupBy(x => x.Visibility, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase));
    }

    private static DateTime StartUtc(TrafficEvent row) => row.StartedUtc ?? row.TimestampUtc;
    private static DateTime EndUtc(TrafficEvent row) => row.LastSeenUtc ?? row.TimestampUtc;

    private static string CorrelationKey(TrafficEvent row)
    {
        var device = row.DeviceId.HasValue ? $"device:{row.DeviceId}" : $"ip:{row.SourceIp?.Trim().ToLowerInvariant()}";
        var hostname = TelemetryNaming.NormalizeHostname(row.Domain, row.DestinationIp);
        if (!string.IsNullOrWhiteSpace(hostname)) return $"{device}|host:{hostname}";
        var application = TelemetryNaming.NormalizeApplication(row.Application);
        if (!string.IsNullOrWhiteSpace(application)) return $"{device}|app:{application.ToLowerInvariant()}";
        if (!string.IsNullOrWhiteSpace(row.DestinationIp)) return $"{device}|remote:{row.DestinationIp}:{row.DestinationPort}";
        return $"{device}|signal:{row.Protocol}:{row.Category}";
    }

    private sealed class ActivityAccumulator
    {
        private readonly HashSet<string> _sources = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _applications = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _protocols = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _destinationIps = new(StringComparer.OrdinalIgnoreCase);
        private int _categoryRank;

        public ActivityAccumulator(TrafficEvent row, DateTime start, DateTime end)
        {
            StartedUtc = start;
            LastSeenUtc = end;
            Background = true;
            Add(row, start, end);
        }

        public long Id { get; private set; }
        public DateTime StartedUtc { get; private set; }
        public DateTime LastSeenUtc { get; private set; }
        public long? DeviceId { get; private set; }
        public string? SourceIp { get; private set; }
        public string? ExactUrl { get; private set; }
        public string? Domain { get; private set; }
        public string? Application { get; private set; }
        public string Category { get; private set; } = "Other";
        public int? DestinationPort { get; private set; }
        public string? Country { get; private set; }
        public int DurationSeconds { get; private set; }
        public long BytesUp { get; private set; }
        public long BytesDown { get; private set; }
        public bool Blocked { get; private set; }
        public bool Encrypted { get; private set; }
        public string Visibility { get; private set; } = "ip";
        public int Confidence { get; private set; }
        public bool Background { get; private set; }
        public int EventCount { get; private set; }

        public void Add(TrafficEvent row, DateTime start, DateTime end)
        {
            Id = Math.Max(Id, row.Id);
            StartedUtc = start < StartedUtc ? start : StartedUtc;
            LastSeenUtc = end > LastSeenUtc ? end : LastSeenUtc;
            DeviceId ??= row.DeviceId;
            SourceIp ??= row.SourceIp;
            ExactUrl ??= string.IsNullOrWhiteSpace(row.ExactUrl) ? null : row.ExactUrl;
            var hostname = TelemetryNaming.NormalizeHostname(row.Domain, row.DestinationIp);
            if (!string.IsNullOrWhiteSpace(hostname))
            {
                _domains.Add(hostname);
                Domain ??= hostname;
            }
            var application = TelemetryNaming.NormalizeApplication(row.Application);
            if (!string.IsNullOrWhiteSpace(application))
            {
                _applications.Add(application);
                Application ??= application;
            }
            if (!string.IsNullOrWhiteSpace(row.Protocol)) _protocols.Add(row.Protocol);
            if (!string.IsNullOrWhiteSpace(row.DestinationIp)) _destinationIps.Add(row.DestinationIp);
            if (!string.IsNullOrWhiteSpace(row.Source)) _sources.Add(row.Source);
            DestinationPort ??= row.DestinationPort;
            Country ??= row.Country;
            DurationSeconds = Math.Max(DurationSeconds, row.DurationSeconds ?? 0);
            BytesUp += Math.Max(0, row.BytesUp ?? 0);
            BytesDown += Math.Max(0, row.BytesDown ?? 0);
            Blocked |= row.Blocked;
            Encrypted |= row.Encrypted;
            Confidence = Math.Max(Confidence, row.Confidence);
            EventCount++;

            var visibility = string.IsNullOrWhiteSpace(row.Visibility)
                ? TelemetryNaming.Visibility(row.ExactUrl, hostname, application, row.DestinationIp)
                : row.Visibility;
            if (TelemetryNaming.VisibilityRank(visibility) > TelemetryNaming.VisibilityRank(Visibility)) Visibility = visibility;

            var rank = CategoryRank(row.Category, row.Blocked);
            if (rank > _categoryRank)
            {
                _categoryRank = rank;
                Category = string.IsNullOrWhiteSpace(row.Category) ? "Other" : row.Category;
            }

            var backgroundSignal = TelemetryNaming.IsBackground(hostname, application)
                || (string.IsNullOrWhiteSpace(hostname) && string.IsNullOrWhiteSpace(application));
            Background &= backgroundSignal;
        }

        public CorrelatedActivity ToRecord(IReadOnlyDictionary<long, Device> devices)
        {
            Device? device = null;
            if (DeviceId.HasValue) devices.TryGetValue(DeviceId.Value, out device);
            var intervalSeconds = Math.Max(0, (int)(LastSeenUtc - StartedUtc).TotalSeconds);
            var application = Application ?? _applications.FirstOrDefault();
            var domain = Domain ?? _domains.FirstOrDefault();
            var destinationIp = _destinationIps.FirstOrDefault();
            return new CorrelatedActivity(
                Id,
                LastSeenUtc,
                StartedUtc,
                LastSeenUtc,
                DeviceId,
                device?.Name ?? device?.LastIpAddress ?? SourceIp ?? "Unknown device",
                SourceIp ?? device?.LastIpAddress,
                ExactUrl,
                domain,
                TelemetryNaming.Service(domain, application),
                application,
                Category,
                _protocols.FirstOrDefault(),
                destinationIp,
                DestinationPort,
                Country,
                Math.Max(DurationSeconds, intervalSeconds),
                BytesUp,
                BytesDown,
                Blocked,
                Encrypted,
                Visibility,
                Confidence,
                Background,
                EventCount,
                _sources.OrderBy(x => x).ToArray(),
                _domains.OrderBy(x => x).ToArray(),
                _applications.OrderBy(x => x).ToArray(),
                _protocols.OrderBy(x => x).ToArray(),
                _destinationIps.OrderBy(x => x).ToArray());
        }

        private static int CategoryRank(string? category, bool blocked)
        {
            if (category?.Equals("Adult", StringComparison.OrdinalIgnoreCase) == true) return 100;
            if (blocked || category?.Equals("Blocked", StringComparison.OrdinalIgnoreCase) == true) return 90;
            if (category?.Equals("DNS", StringComparison.OrdinalIgnoreCase) == true) return 10;
            return string.IsNullOrWhiteSpace(category) ? 0 : 50;
        }
    }
}
