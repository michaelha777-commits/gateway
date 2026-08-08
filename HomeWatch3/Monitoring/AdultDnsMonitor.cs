using System.Globalization;
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
    Microsoft.Extensions.Options.IOptions<AdultDnsMonitorOptions> options,
    ILogger<AdultDnsMonitor> logger) : BackgroundService
{
    private readonly AdultDnsMonitorOptions _options = options.Value;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastAlertByDevice = new(StringComparer.OrdinalIgnoreCase);
    private bool _primed;

    public AdultDnsMonitorStatus Status { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Status.Enabled = _options.Enabled;
        if (!_options.Enabled)
        {
            logger.LogInformation("Adult DNS monitor is disabled.");
            return;
        }

        var pollSeconds = Math.Clamp(_options.PollSeconds, 15, 300);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(pollSeconds));

        await PollAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await PollAsync(stoppingToken);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        Status.LastPollUtc = DateTime.UtcNow;
        try
        {
            var payload = await opnsense.GetUnboundQueriesAsync(cancellationToken);
            var rows = ExtractRows(payload).ToList();
            Status.LastRowsSeen = rows.Count;

            // The first successful read establishes a baseline so a restart does not
            // alert on old rows already present in OPNsense's rolling report.
            if (!_primed)
            {
                foreach (var row in rows)
                    _seen.Add(Fingerprint(row));
                TrimSeen();
                _primed = true;
                Status.LastSuccessfulPollUtc = DateTime.UtcNow;
                Status.LastError = null;
                logger.LogInformation("Adult DNS monitor primed with {Count} existing Unbound rows.", rows.Count);
                return;
            }

            foreach (var row in rows.OrderBy(x => ParseTime(x)))
            {
                var fingerprint = Fingerprint(row);
                if (!_seen.Add(fingerprint)) continue;

                var domain = GetString(row, "domain", "name", "qname", "query");
                var clientIp = GetString(row, "client", "client_ip", "source", "src", "ip");
                var classification = classifier.Classify(domain);
                if (!classification.IsAdult) continue;

                Status.AdultHitsDetected++;
                await RecordAndNotifyAsync(
                    clientIp,
                    domain,
                    classification,
                    ParseTime(row),
                    cancellationToken);
            }

            TrimSeen();
            Status.LastSuccessfulPollUtc = DateTime.UtcNow;
            Status.LastError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Status.LastError = ex.Message;
            logger.LogWarning(ex, "Adult DNS monitor poll failed.");
        }
    }

    private async Task RecordAndNotifyAsync(
        string? clientIp,
        string? domain,
        AdultDomainResult classification,
        DateTime timestampUtc,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();

        Device? device = null;
        if (!string.IsNullOrWhiteSpace(clientIp))
            device = await db.Devices.FirstOrDefaultAsync(x => x.LastIpAddress == clientIp, cancellationToken);

        var now = DateTime.UtcNow;
        var eventRow = new TrafficEvent
        {
            TimestampUtc = timestampUtc == default ? now : timestampUtc,
            DeviceId = device?.Id,
            SourceIp = clientIp,
            Domain = domain,
            Category = "Adult",
            Protocol = "DNS",
            Source = "opnsense-unbound",
            Confidence = classification.Confidence,
            Blocked = false
        };
        db.TrafficEvents.Add(eventRow);

        var deviceKey = device?.MacAddress ?? clientIp ?? "unknown";
        var cooldown = TimeSpan.FromMinutes(Math.Clamp(_options.AlertCooldownMinutes, 1, 1440));
        var shouldNotify = !_lastAlertByDevice.TryGetValue(deviceKey, out var lastAlert) || now - lastAlert >= cooldown;

        var name = !string.IsNullOrWhiteSpace(device?.Name)
            ? device!.Name!
            : clientIp ?? "Unknown device";

        var alert = new AlertRecord
        {
            CreatedUtc = now,
            Type = "adult-content",
            Severity = "high",
            DeviceId = device?.Id,
            Title = "Adult activity detected",
            Message = $"Device: {name}\nIP: {clientIp ?? "unknown"}\nDomain: {domain ?? "unknown"}\nConfidence: {classification.Confidence}%\nEvidence: {classification.Evidence}",
            NotificationSent = false
        };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync(cancellationToken);

        if (shouldNotify)
        {
            var sent = await ntfy.SendAsync(
                "Adult activity detected",
                $"Device: {name}\nIP: {clientIp ?? "unknown"}\nDomain: {domain ?? "unknown"}\nConfidence: {classification.Confidence}%",
                "high",
                cancellationToken);

            alert.NotificationSent = sent;
            alert.NotificationSentUtc = sent ? DateTime.UtcNow : null;
            await db.SaveChangesAsync(cancellationToken);

            if (sent)
            {
                _lastAlertByDevice[deviceKey] = DateTime.UtcNow;
                Status.LastAlertUtc = DateTime.UtcNow;
            }
        }
    }

    private static IEnumerable<JsonElement> ExtractRows(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray().Select(x => x.Clone());

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            return rows.EnumerateArray().Select(x => x.Clone());

        return Array.Empty<JsonElement>();
    }

    private static string? GetString(JsonElement row, params string[] names)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!row.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Number) return value.ToString();
        }
        return null;
    }

    private static DateTime ParseTime(JsonElement row)
    {
        var raw = GetString(row, "time", "timestamp", "created", "date");
        if (string.IsNullOrWhiteSpace(raw)) return DateTime.UtcNow;

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; }
            catch { }
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTime.UtcNow;
    }

    private static string Fingerprint(JsonElement row)
    {
        var raw = row.GetRawText();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private void TrimSeen()
    {
        // OPNsense returns at most a bounded recent window. Keep a modest in-memory
        // dedup set; clearing after growth is safe because current rows are immediately
        // re-added on subsequent polls and alert cooldown prevents notification storms.
        if (_seen.Count <= 5000) return;
        _seen.Clear();
        _primed = false;
    }
}
