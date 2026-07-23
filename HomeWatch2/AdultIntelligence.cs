using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

public sealed record AdultClassification(bool IsAdult, int Confidence, string RootDomain, string Evidence, string Source);

public sealed class ExternalAdultDomainDatabase(
    IHttpClientFactory httpClientFactory,
    IWebHostEnvironment environment,
    ILogger<ExternalAdultDomainDatabase> logger) : BackgroundService
{
    private static readonly (string Name, string Url)[] Sources =
    {
        ("BlocklistProject", "https://raw.githubusercontent.com/blocklistproject/Lists/master/porn.txt"),
        ("StevenBlack", "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/porn/hosts"),
        ("Sinfonietta", "https://raw.githubusercontent.com/Sinfonietta/hostfiles/master/pornography-hosts")
    };

    private static readonly string[] TrustedDomains =
    {
        "bing.com", "microsoft.com", "live.com", "office.com", "office365.com", "windows.com", "windowsupdate.com", "azure.com", "msftconnecttest.com", "msftncsi.com",
        "google.com", "gvt1.com", "gvt2.com", "googleapis.com", "gstatic.com", "googleusercontent.com", "googlevideo.com", "youtube.com", "youtu.be",
        "apple.com", "icloud.com", "mzstatic.com", "cdn-apple.com", "netflix.com", "nflxvideo.net", "nflximg.net", "nflxso.net", "nflxext.com",
        "amazon.com", "amazonaws.com", "cloudfront.net", "amazonvideo.com", "cloudflare.com", "cloudflare-dns.com", "github.com", "githubusercontent.com",
        "spotify.com", "facebook.com", "fbcdn.net", "instagram.com", "whatsapp.com", "reddit.com", "wikipedia.org", "ntfy.sh", "adguard.com", "adguard-dns.com",
        "plannedparenthood.org", "sexualhealthontario.ca", "sexeducationforum.org.uk", "mayoclinic.org", "nhs.uk"
    };

    private static readonly string[] AdultBrandTokens =
    {
        "xnxx", "xvideos", "pornhub", "xhamster", "redtube", "youporn", "spankbang", "tube8", "brazzers", "erome", "jerkmate",
        "chaturbate", "stripchat", "livejasmin", "bongacams", "myfreecams", "onlyfans", "nhentai", "hentaihaven", "rule34", "literotica",
        "anysex", "eporner", "hqporner", "tnaflix", "drtuber", "motherless", "fapello", "camsoda", "pussyspace"
    };

    private readonly HashSet<string> domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> evidenceSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> sourceCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string cachePath = Path.Combine(environment.ContentRootPath, "adult-intelligence-cache.json");
    private DateTime? lastUpdatedUtc;
    private string? lastError;

    public int DomainCount { get { lock (domains) return domains.Count; } }
    public DateTime? LastUpdatedUtc => lastUpdatedUtc;
    public string? LastError => lastError;
    public IReadOnlyDictionary<string, int> SourceCounts { get { lock (domains) return new Dictionary<string, int>(sourceCounts); } }

    public AdultClassification Classify(string domain)
    {
        var value = Normalize(domain);
        var root = GetRootDomain(value);
        if (string.IsNullOrWhiteSpace(value)) return new(false, 0, root, "Empty or invalid domain", "none");
        if (AdultSafetyOverrides.IsSafe(value)) return new(false, 100, root, "Marked not adult by user", "user-safe-override");
        if (MatchesAny(value, TrustedDomains)) return new(false, 100, root, "Trusted general-purpose or infrastructure domain", "safe-list");

        if (TryFindAdultBrandLabel(value, out var brand))
            return new(true, 98, root, $"Adult brand label in exact hostname: {brand}", "brand-intelligence");

        if (AdultDomainClassifier.IsAdult(value))
            return new(true, 94, root, "Known adult domain or strong adult term in exact hostname", "built-in-classifier");

        lock (domains)
        {
            if (TryFindListMatch(value, out var matched, out var sources))
            {
                if (sources.Count >= 2)
                    return new(true, 90, root,
                        $"Exact/parent entry '{matched}' independently appears in {sources.Count} adult lists: {string.Join(", ", sources.OrderBy(x => x))}",
                        "corroborated-blocklists");

                var source = sources.FirstOrDefault() ?? "cached-list";
                return new(false, 45, root,
                    $"Single uncorroborated adult-list entry '{matched}' from {source}; suppressed to prevent false positives",
                    "single-list-unconfirmed");
            }
        }

        return new(false, 20, root, "No reliable adult signal found", "local-intelligence");
    }

    public bool IsAdult(string domain) => Classify(domain).IsAdult;

    public object GetStatus() => new
    {
        domainCount = DomainCount,
        lastUpdatedUtc,
        lastError,
        sources = SourceCounts,
        policy = "Adult alerts require a known adult hostname/brand or corroboration by at least two independent lists.",
        refreshIntervalDays = 1
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadCacheAsync(stoppingToken);
        await RefreshAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await RefreshAsync(stoppingToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var combined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var provenance = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var failures = new List<string>();

            foreach (var source in Sources)
            {
                try
                {
                    var client = httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(60);
                    var text = await client.GetStringAsync(source.Url, cancellationToken);
                    var parsed = ParseDomains(text);
                    counts[source.Name] = parsed.Count;
                    foreach (var item in parsed)
                    {
                        combined.Add(item);
                        if (!provenance.TryGetValue(item, out var names)) provenance[item] = names = new(StringComparer.OrdinalIgnoreCase);
                        names.Add(source.Name);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{source.Name}: {ex.Message}");
                    logger.LogWarning(ex, "Could not refresh adult intelligence source {Source}", source.Name);
                }
            }

            foreach (var seed in AdultSeedDomains.All)
            {
                combined.Add(seed);
                provenance[seed] = new HashSet<string>(new[] { "HomeWatch curated seed", "curated verification" }, StringComparer.OrdinalIgnoreCase);
            }

            foreach (var trusted in TrustedDomains)
            {
                combined.RemoveWhere(x => MatchesAny(x, new[] { trusted }));
                foreach (var key in provenance.Keys.Where(x => MatchesAny(x, new[] { trusted })).ToList()) provenance.Remove(key);
            }

            if (combined.Count > 1000)
            {
                lock (domains)
                {
                    domains.Clear();
                    evidenceSources.Clear();
                    foreach (var item in combined) domains.Add(item);
                    foreach (var item in provenance) evidenceSources[item.Key] = item.Value;
                    sourceCounts.Clear();
                    foreach (var item in counts) sourceCounts[item.Key] = item.Value;
                }
                lastUpdatedUtc = DateTime.UtcNow;
                lastError = failures.Count == 0 ? null : string.Join(" | ", failures);
                await SaveCacheAsync(cancellationToken);
                logger.LogInformation("Adult intelligence refreshed with {Count} unique domains from {Sources} sources", combined.Count, counts.Count);
            }
            else
            {
                lastError = "Adult intelligence refresh returned too few domains; previous cache retained. " + string.Join(" | ", failures);
                logger.LogWarning("{Error}", lastError);
            }
        }
        finally { gate.Release(); }
    }

    private HashSet<string> ParseDomains(string text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;

            string candidate;
            if (line.StartsWith("||") && line.EndsWith('^')) candidate = line[2..^1];
            else
            {
                var parts = Regex.Split(line, "\\s+");
                candidate = parts.Length >= 2 && IsSinkholeIp(parts[0]) ? parts[1] : parts[0];
            }

            var domain = Normalize(candidate);
            if (!IsUsableDomain(domain) || MatchesAny(domain, TrustedDomains)) continue;
            result.Add(domain);
        }
        return result;
    }

    private async Task LoadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(cachePath)) return;
            await using var stream = File.OpenRead(cachePath);
            var cache = await JsonSerializer.DeserializeAsync<AdultCache>(stream, cancellationToken: cancellationToken);
            if (cache?.Domains is null) return;
            lock (domains)
            {
                domains.Clear();
                evidenceSources.Clear();
                foreach (var domain in cache.Domains.Where(IsUsableDomain).Where(x => !MatchesAny(x, TrustedDomains)))
                {
                    domains.Add(domain);
                    evidenceSources[domain] = new HashSet<string>(new[] { "legacy cache" }, StringComparer.OrdinalIgnoreCase);
                }
                sourceCounts.Clear();
                if (cache.SourceCounts is not null) foreach (var item in cache.SourceCounts) sourceCounts[item.Key] = item.Value;
            }
            lastUpdatedUtc = cache.UpdatedUtc;
            logger.LogInformation("Loaded {Count} cached adult domains; single cached entries are non-alerting until corroborated", DomainCount);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not load adult intelligence cache"); }
    }

    private async Task SaveCacheAsync(CancellationToken cancellationToken)
    {
        AdultCache cache;
        lock (domains) cache = new AdultCache(lastUpdatedUtc ?? DateTime.UtcNow, domains.OrderBy(x => x).ToArray(), new Dictionary<string, int>(sourceCounts));
        await using var stream = File.Create(cachePath);
        await JsonSerializer.SerializeAsync(stream, cache, new JsonSerializerOptions { WriteIndented = false }, cancellationToken);
    }

    private bool TryFindListMatch(string value, out string matched, out HashSet<string> sources)
    {
        var current = value;
        while (true)
        {
            if (domains.Contains(current))
            {
                matched = current;
                sources = evidenceSources.TryGetValue(current, out var found)
                    ? new HashSet<string>(found, StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(new[] { "unknown list" }, StringComparer.OrdinalIgnoreCase);
                return true;
            }
            var dot = current.IndexOf('.');
            if (dot < 0 || dot + 1 >= current.Length) break;
            current = current[(dot + 1)..];
        }
        matched = "";
        sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return false;
    }

    private static bool TryFindAdultBrandLabel(string domain, out string token)
    {
        foreach (var label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (var item in AdultBrandTokens)
        {
            if (label.Equals(item, StringComparison.OrdinalIgnoreCase) || label.StartsWith(item + "-", StringComparison.OrdinalIgnoreCase) || label.EndsWith("-" + item, StringComparison.OrdinalIgnoreCase))
            { token = item; return true; }
        }
        token = ""; return false;
    }

    private static bool MatchesAny(string value, IEnumerable<string> candidates) =>
        candidates.Any(candidate => value.Equals(candidate, StringComparison.OrdinalIgnoreCase) || value.EndsWith("." + candidate, StringComparison.OrdinalIgnoreCase));
    private static bool IsSinkholeIp(string value) => value is "0.0.0.0" or "127.0.0.1" or "::" or "::1";
    private static bool IsUsableDomain(string value) => value.Length is > 3 and < 254 && value.Contains('.') && !value.Contains('*') && !value.Contains('/') && value != "localhost";
    private static string Normalize(string value) => (value ?? "").Trim().Trim('.').ToLowerInvariant();
    private static string GetRootDomain(string domain)
    {
        var parts = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? string.Join('.', parts[^2], parts[^1]) : domain;
    }

    private sealed record AdultCache(DateTime UpdatedUtc, string[] Domains, Dictionary<string, int> SourceCounts);
}

public static class AdultSeedDomains
{
    public static readonly string[] All =
    {
        "pornhub.com", "xvideos.com", "xnxx.com", "redtube.com", "youporn.com", "xhamster.com", "spankbang.com", "tube8.com",
        "brazzers.com", "onlyfans.com", "chaturbate.com", "stripchat.com", "livejasmin.com", "cam4.com", "bongacams.com", "myfreecams.com",
        "pussyspace.com", "eporner.com", "hqporner.com", "pornpics.com", "pornhd.com", "pornone.com", "pornhat.com", "pornhits.com",
        "beeg.com", "tnaflix.com", "drtuber.com", "sunporno.com", "nuvid.com", "porndig.com", "porntrex.com", "pornmd.com",
        "erome.com", "motherless.com", "fapello.com", "fapality.com", "fapster.xxx", "theporndude.com", "sexvid.xxx", "sexu.com",
        "hclips.com", "txxx.com", "upornia.com", "vjav.com", "javhd.com", "javlibrary.com", "jav.guru", "thisvid.com",
        "xhamsterlive.com", "camsoda.com", "flirt4free.com", "streamate.com", "jerkmate.com", "imlive.com", "adulttime.com",
        "sex.com", "porn.com", "hentaihaven.xxx", "nhentai.net", "rule34.xxx", "literotica.com", "adultfriendfinder.com", "ashleymadison.com"
    };
}

public sealed class AdultSessionMonitor(
    IServiceScopeFactory scopeFactory,
    NtfyNotifier ntfy,
    ExternalAdultDomainDatabase adultIntelligence,
    ILogger<AdultSessionMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, SessionState> active = new();

    public async Task RecordHitAsync(Guid deviceId, string deviceName, string domain, DateTime timestamp, CancellationToken cancellationToken)
    {
        var exactDomain = (domain ?? "").Trim().Trim('.').ToLowerInvariant();
        var classification = adultIntelligence.Classify(exactDomain);
        if (!classification.IsAdult)
        {
            logger.LogWarning("Suppressed adult session for {Domain}: {Evidence}", exactDomain, classification.Evidence);
            return;
        }

        var created = false;
        var state = active.AddOrUpdate(deviceId,
            _ => { created = true; return new SessionState(Guid.NewGuid(), deviceId, deviceName, timestamp, timestamp, exactDomain, classification); },
            (_, current) =>
            {
                lock (current)
                {
                    if (timestamp - current.LastHit > IdleTimeout)
                    {
                        created = true;
                        return new SessionState(Guid.NewGuid(), deviceId, deviceName, timestamp, timestamp, exactDomain, classification);
                    }
                    current.LastHit = timestamp > current.LastHit ? timestamp : current.LastHit;
                    current.TotalAdultRequests++;
                    current.Domains[exactDomain] = current.Domains.GetValueOrDefault(exactDomain) + 1;
                    return current;
                }
            });
        if (!created) return;

        var detail = $"Exact triggering hostname: {exactDomain}. Root domain: {classification.RootDomain}. Confidence: {classification.Confidence}%. Evidence: {classification.Evidence}. Source: {classification.Source}";
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
        db.Sessions.Add(new ActivitySession { Id = state.SessionId, DeviceId = deviceId, StartedAt = timestamp, EndedAt = timestamp, Confidence = classification.Confidence, Assessment = detail });
        db.Alerts.Add(new Alert { Id = Guid.NewGuid(), SessionId = state.SessionId, DeviceId = deviceId, Severity = "critical", Title = "Adult viewing session started", Detail = detail, CreatedAt = timestamp });
        await db.SaveChangesAsync(cancellationToken);
        await ntfy.SendAsync("Adult viewing session started", $"Device: {deviceName}\nExact hostname: {exactDomain}\nConfidence: {classification.Confidence}%\nEvidence: {classification.Evidence}\nTime: {timestamp.ToLocalTime():g}", "rotating_light", 5, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        foreach (var pair in active.ToArray())
        {
            SessionState state; lock (pair.Value) state = pair.Value.Clone();
            if (DateTime.UtcNow - state.LastHit < IdleTimeout || !active.TryRemove(pair.Key, out _)) continue;
            try { await CloseSessionAsync(state, stoppingToken); } catch (Exception ex) { logger.LogWarning(ex, "Could not close adult session {SessionId}", state.SessionId); }
        }
    }

    private async Task CloseSessionAsync(SessionState state, CancellationToken cancellationToken)
    {
        var duration = state.LastHit - state.StartedAt;
        var mainSites = state.Domains.OrderByDescending(x => x.Value).Take(5).ToList();
        var primary = mainSites.FirstOrDefault().Key ?? state.FirstExactDomain;
        var sites = string.Join(", ", mainSites.Select(x => $"{x.Key} ({x.Value})"));
        var detail = $"Duration: {FormatDuration(duration)}. Primary exact hostname: {primary}. First exact hostname: {state.FirstExactDomain}. Adult requests: {state.TotalAdultRequests}. Main exact hostnames: {sites}. Trigger evidence: {state.Trigger.Evidence}. Source: {state.Trigger.Source}";
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
        var session = await db.Sessions.FirstOrDefaultAsync(x => x.Id == state.SessionId, cancellationToken);
        if (session is not null) { session.EndedAt = state.LastHit; session.Assessment = detail; }
        db.Alerts.Add(new Alert { Id = Guid.NewGuid(), SessionId = state.SessionId, DeviceId = state.DeviceId, Severity = "high", Title = "Adult viewing session ended", Detail = detail, CreatedAt = state.LastHit });
        await db.SaveChangesAsync(cancellationToken);
        await ntfy.SendAsync("Adult viewing session ended", $"Device: {state.DeviceName}\nDuration: {FormatDuration(duration)}\nPrimary exact hostname: {primary}\nAdult requests: {state.TotalAdultRequests}\nMain hostnames: {sites}", "bar_chart", 4, cancellationToken);
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalMinutes < 1 ? "less than 1 minute" : $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes))} minutes";

    private sealed class SessionState(Guid sessionId, Guid deviceId, string deviceName, DateTime startedAt, DateTime lastHit, string firstExactDomain, AdultClassification trigger)
    {
        public Guid SessionId { get; } = sessionId;
        public Guid DeviceId { get; } = deviceId;
        public string DeviceName { get; } = deviceName;
        public DateTime StartedAt { get; } = startedAt;
        public DateTime LastHit { get; set; } = lastHit;
        public string FirstExactDomain { get; } = firstExactDomain;
        public AdultClassification Trigger { get; } = trigger;
        public int TotalAdultRequests { get; set; } = 1;
        public Dictionary<string, int> Domains { get; } = new(StringComparer.OrdinalIgnoreCase) { [firstExactDomain] = 1 };
        public SessionState Clone()
        {
            var copy = new SessionState(SessionId, DeviceId, DeviceName, StartedAt, LastHit, FirstExactDomain, Trigger) { TotalAdultRequests = TotalAdultRequests };
            copy.Domains.Clear();
            foreach (var item in Domains) copy.Domains[item.Key] = item.Value;
            return copy;
        }
    }
}
