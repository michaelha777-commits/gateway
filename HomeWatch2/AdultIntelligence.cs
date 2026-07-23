using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

public sealed class ExternalAdultDomainDatabase(
    IHttpClientFactory httpClientFactory,
    ILogger<ExternalAdultDomainDatabase> logger) : BackgroundService
{
    private const string SourceUrl = "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/porn/hosts";
    private readonly HashSet<string> domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTime? lastUpdatedUtc;

    public int DomainCount { get { lock (domains) return domains.Count; } }
    public DateTime? LastUpdatedUtc => lastUpdatedUtc;
    public string Source => "StevenBlack hosts porn list";

    public bool IsAdult(string domain)
    {
        var value = Normalize(domain);
        if (string.IsNullOrWhiteSpace(value)) return false;
        lock (domains)
        {
            if (domains.Contains(value)) return true;
            var dot = value.IndexOf('.');
            while (dot >= 0 && dot + 1 < value.Length)
            {
                value = value[(dot + 1)..];
                if (domains.Contains(value)) return true;
                dot = value.IndexOf('.');
            }
        }
        return AdultDomainClassifier.IsAdult(domain);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(7));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await RefreshAsync(stoppingToken);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(45);
            var text = await client.GetStringAsync(SourceUrl, cancellationToken);
            var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var parts = Regex.Split(line, "\\s+");
                if (parts.Length < 2 || (parts[0] != "0.0.0.0" && parts[0] != "127.0.0.1")) continue;
                var domain = Normalize(parts[1]);
                if (!string.IsNullOrWhiteSpace(domain) && domain != "localhost" && !domain.Contains('*')) updated.Add(domain);
            }
            updated.Add("erome.com");
            updated.Add("jerkmate.com");
            lock (domains)
            {
                domains.Clear();
                foreach (var domain in updated) domains.Add(domain);
            }
            lastUpdatedUtc = DateTime.UtcNow;
            logger.LogInformation("Adult domain database refreshed with {Count} domains", updated.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not refresh external adult domain database; built-in domains remain active");
        }
        finally { gate.Release(); }
    }

    private static string Normalize(string value) => value.Trim().Trim('.').ToLowerInvariant();
}

public sealed class AdultSessionMonitor(
    IServiceScopeFactory scopeFactory,
    NtfyNotifier ntfy,
    ILogger<AdultSessionMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, SessionState> active = new();

    public async Task RecordHitAsync(Guid deviceId, string deviceName, string domain, DateTime timestamp, CancellationToken cancellationToken)
    {
        var rootDomain = GetRootDomain(domain);
        var created = false;
        var state = active.AddOrUpdate(deviceId,
            _ => { created = true; return new SessionState(Guid.NewGuid(), deviceId, deviceName, timestamp, timestamp, rootDomain); },
            (_, current) =>
            {
                lock (current)
                {
                    if (timestamp - current.LastHit > IdleTimeout)
                    {
                        created = true;
                        return new SessionState(Guid.NewGuid(), deviceId, deviceName, timestamp, timestamp, rootDomain);
                    }
                    current.LastHit = timestamp > current.LastHit ? timestamp : current.LastHit;
                    current.TotalAdultRequests++;
                    current.Domains[rootDomain] = current.Domains.GetValueOrDefault(rootDomain) + 1;
                    return current;
                }
            });

        if (!created) return;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
        db.Sessions.Add(new ActivitySession { Id = state.SessionId, DeviceId = deviceId, StartedAt = timestamp, EndedAt = timestamp, Confidence = 100, Assessment = $"Adult viewing session; first site: {rootDomain}" });
        db.Alerts.Add(new Alert { Id = Guid.NewGuid(), SessionId = state.SessionId, DeviceId = deviceId, Severity = "critical", Title = "Adult viewing session started", Detail = $"First detected site: {rootDomain}", CreatedAt = timestamp });
        await db.SaveChangesAsync(cancellationToken);
        await ntfy.SendAsync("Adult viewing session started", $"Device: {deviceName}\nFirst site: {rootDomain}\nTime: {timestamp.ToLocalTime():g}", "rotating_light", 5, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTime.UtcNow;
            foreach (var pair in active.ToArray())
            {
                SessionState state;
                lock (pair.Value) state = pair.Value.Clone();
                if (now - state.LastHit < IdleTimeout || !active.TryRemove(pair.Key, out _)) continue;
                try { await CloseSessionAsync(state, stoppingToken); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not close adult session {SessionId}", state.SessionId); }
            }
        }
    }

    private async Task CloseSessionAsync(SessionState state, CancellationToken cancellationToken)
    {
        var endedAt = state.LastHit;
        var duration = endedAt - state.StartedAt;
        var mainSites = state.Domains.OrderByDescending(x => x.Value).Take(5).ToList();
        var primary = mainSites.FirstOrDefault().Key ?? "unknown";
        var sites = string.Join(", ", mainSites.Select(x => $"{x.Key} ({x.Value})"));
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
        var session = await db.Sessions.FirstOrDefaultAsync(x => x.Id == state.SessionId, cancellationToken);
        if (session is not null)
        {
            session.EndedAt = endedAt;
            session.Assessment = $"Adult viewing session. Primary site: {primary}. Adult DNS requests: {state.TotalAdultRequests}. Main sites: {sites}";
        }
        db.Alerts.Add(new Alert { Id = Guid.NewGuid(), SessionId = state.SessionId, DeviceId = state.DeviceId, Severity = "high", Title = "Adult viewing session ended", Detail = $"Duration: {FormatDuration(duration)}. Primary site: {primary}. Adult requests: {state.TotalAdultRequests}. Main sites: {sites}", CreatedAt = endedAt });
        await db.SaveChangesAsync(cancellationToken);
        await ntfy.SendAsync("Adult viewing session ended", $"Device: {state.DeviceName}\nDuration: {FormatDuration(duration)}\nPrimary site: {primary}\nAdult requests: {state.TotalAdultRequests}\nMain sites: {sites}", "bar_chart", 4, cancellationToken);
    }

    private static string GetRootDomain(string domain)
    {
        var parts = domain.Trim('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? string.Join('.', parts[^2], parts[^1]) : domain;
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalMinutes < 1 ? "less than 1 minute" : $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes))} minutes";

    private sealed class SessionState(Guid sessionId, Guid deviceId, string deviceName, DateTime startedAt, DateTime lastHit, string firstDomain)
    {
        public Guid SessionId { get; } = sessionId;
        public Guid DeviceId { get; } = deviceId;
        public string DeviceName { get; } = deviceName;
        public DateTime StartedAt { get; } = startedAt;
        public DateTime LastHit { get; set; } = lastHit;
        public int TotalAdultRequests { get; set; } = 1;
        public Dictionary<string, int> Domains { get; } = new(StringComparer.OrdinalIgnoreCase) { [firstDomain] = 1 };
        public SessionState Clone()
        {
            var copy = new SessionState(SessionId, DeviceId, DeviceName, StartedAt, LastHit, Domains.Keys.First()) { TotalAdultRequests = TotalAdultRequests };
            copy.Domains.Clear(); foreach (var item in Domains) copy.Domains[item.Key] = item.Value;
            return copy;
        }
    }
}
