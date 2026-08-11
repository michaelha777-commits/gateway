using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeWatch3.Connectors.Opnsense;
using HomeWatch3.Data;
using HomeWatch3.Notifications;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

public sealed class AdultDnsMonitorOptions
{
    public const string SectionName = "AdultDnsMonitor";
    public bool Enabled { get; set; } = true;
    public int PollSeconds { get; set; } = 15;
    public int AlertCooldownMinutes { get; set; } = 10;
}

public sealed class AdultDnsMonitorStatus
{
    public bool Enabled { get; set; }
    public DateTime? LastPollUtc { get; set; }
    public DateTime? LastSuccessfulPollUtc { get; set; }
    public string? LastError { get; set; }
    public int LastRowsSeen { get; set; }
    public int AdultHitsDetected { get; set; }
    public DateTime? LastAlertUtc { get; set; }
}

public sealed class AdultDnsMonitor(
    IServiceScopeFactory scopeFactory,
    IOpnsenseClient opnsense,
    IAdultDomainClassifier classifier,
    INtfyService ntfy,
    IgnoredDeviceStore ignoredDevices,
    IgnoredDomainStore ignoredDomains,
    Microsoft.Extensions.Options.IOptions<AdultDnsMonitorOptions> options,
    ILogger<AdultDnsMonitor> logger) : BackgroundService
{
    private readonly AdultDnsMonitorOptions _options = options.Value;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastAlertByDevice = new(StringComparer.OrdinalIgnoreCase);
    private bool _primed;

    private sealed record PendingDnsRow(JsonElement Row, string? Domain, string? ClientIp, DateTime TimestampUtc);

    public AdultDnsMonitorStatus Status { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Status.Enabled = _options.Enabled;
        if (!_options.Enabled) { logger.LogInformation("Adult DNS monitor is disabled."); return; }
        var pollSeconds = Math.Clamp(_options.PollSeconds, 15, 300);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(pollSeconds));
        await PollAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await PollAsync(stoppingToken);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        Status.LastPollUtc = DateTime.UtcNow;
        try
        {
            var payload = await opnsense.GetUnboundQueriesAsync(cancellationToken);
            var rows = ExtractRows(payload).ToList();
            Status.LastRowsSeen = rows.Count;
            if (!_primed)
            {
                foreach (var row in rows) _seen.Add(Fingerprint(row));
                TrimSeen(); _primed = true; Status.LastSuccessfulPollUtc = DateTime.UtcNow; Status.LastError = null;
                logger.LogInformation("Adult DNS monitor primed with {Count} existing Unbound rows.", rows.Count); return;
            }

            var pending = new List<PendingDnsRow>();
            foreach (var row in rows.OrderBy(ParseTime))
            {
                var fingerprint = Fingerprint(row); if (!_seen.Add(fingerprint)) continue;
                var domain = GetString(row, "domain", "name", "qname", "query");
                if (ignoredDomains.IsIgnored(domain)) continue;
                var clientIp = GetString(row, "client", "client_ip", "source", "src", "ip");
                pending.Add(new PendingDnsRow(row, domain, clientIp, ParseTime(row)));
            }

            foreach (var candidate in CoalesceDuplicateRows(pending))
            {
                var classification = classifier.Classify(candidate.Domain); if (!classification.IsAdult) continue;
                var handled = await RecordAndNotifyAsync(candidate.ClientIp, candidate.Domain, classification, candidate.TimestampUtc, candidate.Row, cancellationToken);
                if (handled) Status.AdultHitsDetected++;
            }
            TrimSeen(); Status.LastSuccessfulPollUtc = DateTime.UtcNow; Status.LastError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { Status.LastError = ex.Message; logger.LogWarning(ex, "Adult DNS monitor poll failed."); }
    }

    private async Task<bool> RecordAndNotifyAsync(string? clientIp, string? domain, AdultDomainResult classification, DateTime timestampUtc, JsonElement row, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
        Device? device = null;
        if (!string.IsNullOrWhiteSpace(clientIp)) device = await db.Devices.FirstOrDefaultAsync(x => x.LastIpAddress == clientIp, cancellationToken);
        if (ignoredDevices.IsIgnored(device?.Id) || ignoredDomains.IsIgnored(domain)) return false;

        var now = DateTime.UtcNow;
        var eventUtc = timestampUtc == default ? now : DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc);
        if (eventUtc > now.AddMinutes(5)) eventUtc = now;
        var dnsType = GetString(row, "type", "qtype", "query_type") ?? "DNS";
        var action = GetString(row, "action") ?? "Pass";
        var dnsSource = GetString(row, "source") ?? "Unknown";
        var policy = GetString(row, "policy") ?? string.Empty;
        var blocklist = GetString(row, "blocklist") ?? string.Empty;
        var rcode = GetString(row, "rcode", "return_code") ?? string.Empty;
        var resolveMs = GetString(row, "resolve_time_ms") ?? string.Empty;
        var dnssec = GetString(row, "dnssec_status") ?? string.Empty;
        var blocked = action.Equals("Block", StringComparison.OrdinalIgnoreCase);

        db.TrafficEvents.Add(new TrafficEvent { TimestampUtc = eventUtc, DeviceId = device?.Id, SourceIp = clientIp, Domain = domain, Category = "Adult", Protocol = $"DNS/{dnsType}", Source = "opnsense-unbound", Confidence = classification.Confidence, Blocked = blocked });
        var deviceKey = device?.MacAddress ?? clientIp ?? "unknown";
        var cooldown = TimeSpan.FromMinutes(Math.Clamp(_options.AlertCooldownMinutes, 1, 1440));
        var shouldNotify = !_lastAlertByDevice.TryGetValue(deviceKey, out var lastAlert) || now - lastAlert >= cooldown;
        var name = !string.IsNullOrWhiteSpace(device?.Name) ? device!.Name! : clientIp ?? "Unknown device";
        var details = new List<string> { $"Device: {name}", $"IP: {clientIp ?? "unknown"}", $"Domain: {domain ?? "unknown"}", $"Detected UTC: {eventUtc:O}", $"DNS type: {dnsType}", $"Action: {action}", $"DNS source: {dnsSource}", $"Result: {(string.IsNullOrWhiteSpace(rcode) ? "unknown" : rcode)}", $"Confidence: {classification.Confidence}%", $"Evidence: {classification.Evidence}" };
        if (!string.IsNullOrWhiteSpace(policy)) details.Add($"Policy: {policy}");
        if (!string.IsNullOrWhiteSpace(blocklist)) details.Add($"Blocklist: {blocklist}");
        if (!string.IsNullOrWhiteSpace(resolveMs)) details.Add($"Resolve time: {resolveMs} ms");
        if (!string.IsNullOrWhiteSpace(dnssec)) details.Add($"DNSSEC: {dnssec}");
        var alert = new AlertRecord { CreatedUtc = eventUtc, Type = "adult-content", Severity = "high", DeviceId = device?.Id, Title = blocked ? "Adult domain blocked" : "Adult activity detected", Message = string.Join("\n", details), NotificationSent = false };
        db.Alerts.Add(alert); await db.SaveChangesAsync(cancellationToken);
        if (shouldNotify)
        {
            var notifyBody = $"Device: {name}\nIP: {clientIp ?? "unknown"}\nDomain: {domain ?? "unknown"}\nAction: {action}\nConfidence: {classification.Confidence}%";
            var sent = await ntfy.SendEventAsync(NtfyEventTypes.AdultContent, blocked ? "Adult domain blocked" : "Adult activity detected", notifyBody, cancellationToken);
            alert.NotificationSent = sent; alert.NotificationSentUtc = sent ? DateTime.UtcNow : null; await db.SaveChangesAsync(cancellationToken);
            if (sent) { _lastAlertByDevice[deviceKey] = DateTime.UtcNow; Status.LastAlertUtc = eventUtc; }
        }
        return true;
    }

    private static IEnumerable<JsonElement> ExtractRows(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array) return payload.EnumerateArray().Select(x => x.Clone());
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array) return rows.EnumerateArray().Select(x => x.Clone());
        return Array.Empty<JsonElement>();
    }
    private static string? GetString(JsonElement row, params string[] names) { if (row.ValueKind != JsonValueKind.Object) return null; foreach (var name in names) { if (!row.TryGetProperty(name, out var value)) continue; if (value.ValueKind == JsonValueKind.String) return value.GetString(); if (value.ValueKind == JsonValueKind.Number) return value.ToString(); } return null; }
    private static IEnumerable<PendingDnsRow> CoalesceDuplicateRows(IEnumerable<PendingDnsRow> rows)
    {
        var window = TimeSpan.FromSeconds(5);
        foreach (var domainRows in rows.GroupBy(x => NormalizeDomain(x.Domain), StringComparer.OrdinalIgnoreCase))
        {
            var ordered = domainRows.OrderBy(x => x.TimestampUtc).ToList();
            for (var index = 0; index < ordered.Count;)
            {
                var clusterStart = ordered[index].TimestampUtc;
                var cluster = new List<PendingDnsRow>();
                while (index < ordered.Count && ordered[index].TimestampUtc - clusterStart <= window) cluster.Add(ordered[index++]);
                var ipRows = cluster.Where(x => IPAddress.TryParse(x.ClientIp, out _)).ToList();
                var preferredRows = ipRows.Count > 0 ? ipRows : cluster;
                foreach (var candidate in preferredRows
                    .GroupBy(x => x.ClientIp ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.OrderByDescending(y => y.TimestampUtc).First()))
                    yield return candidate;
            }
        }
    }
    private static string NormalizeDomain(string? domain) => (domain ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
    private static DateTime ParseTime(JsonElement row)
    {
        var raw = GetString(row, "time", "timestamp", "created", "date"); if (string.IsNullOrWhiteSpace(raw)) return DateTime.UtcNow;
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)) { try { return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; } catch { } }
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : DateTime.UtcNow;
    }
    private static string Fingerprint(JsonElement row) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.GetRawText())));
    private void TrimSeen() { if (_seen.Count <= 5000) return; _seen.Clear(); _primed = false; }
}
