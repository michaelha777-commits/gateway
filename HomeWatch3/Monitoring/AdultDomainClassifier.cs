using System.Text.Json;
using System.Text.RegularExpressions;

namespace HomeWatch3.Monitoring;

public sealed record AdultDomainResult(bool IsAdult, int Confidence, string Evidence);

public interface IAdultDomainClassifier
{
    AdultDomainResult Classify(string? domain);
}

public sealed class AdultDomainClassifier(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<AdultDomainClassifier> logger) : BackgroundService, IAdultDomainClassifier
{
    private static readonly (string Name, string Url)[] Sources =
    {
        ("BlocklistProject", "https://raw.githubusercontent.com/blocklistproject/Lists/master/porn.txt"),
        ("StevenBlack", "https://raw.githubusercontent.com/StevenBlack/hosts/master/alternates/porn/hosts"),
        ("Sinfonietta", "https://raw.githubusercontent.com/Sinfonietta/hostfiles/master/pornography-hosts")
    };

    private static readonly string[] TrustedRoots =
    {
        "microsoft.com","live.com","office.com","office365.com","windows.com","azure.com",
        "google.com","googleapis.com","gstatic.com","googleusercontent.com","googlevideo.com","youtube.com","youtu.be",
        "apple.com","icloud.com","mzstatic.com","cdn-apple.com","amazon.com","amazonaws.com","cloudfront.net",
        "cloudflare.com","cloudflare-dns.com","github.com","githubusercontent.com","facebook.com","fbcdn.net","instagram.com","whatsapp.com",
        "netflix.com","nflxvideo.net","nflximg.net","nflxso.net","spotify.com","reddit.com","wikipedia.org","ntfy.sh"
    };

    private static readonly string[] AdultBrandTokens =
    {
        "xnxx","xvideos","pornhub","xhamster","redtube","youporn","spankbang","tube8","brazzers","erome","jerkmate",
        "chaturbate","stripchat","livejasmin","bongacams","myfreecams","onlyfans","nhentai","hentaihaven","rule34","literotica",
        "anysex","eporner","hqporner","tnaflix","drtuber","motherless","fapello","camsoda","pornpics","pornhd","pornone"
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _sourcesByDomain = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _safeRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dataPath = ResolveDataPath(configuration);
    private DateTime? _lastUpdatedUtc;
    private string? _lastError;

    public object Status
    {
        get
        {
            lock (_gate)
                return new { domainCount = _sourcesByDomain.Count, safeDomainCount = _safeRoots.Count, lastUpdatedUtc = _lastUpdatedUtc, lastError = _lastError, feeds = Sources.Select(x => x.Name).ToArray() };
        }
    }

    public AdultDomainResult Classify(string? domain)
    {
        var value = Normalize(domain);
        if (value.Length == 0) return new(false, 0, "No domain");
        lock (_gate)
        {
            if (MatchesAny(value, _safeRoots)) return new(false, 100, "Marked not adult by user");
        }
        if (MatchesAny(value, TrustedRoots)) return new(false, 100, "Trusted infrastructure/general-purpose domain");

        foreach (var label in value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var token in AdultBrandTokens)
                if (label.Equals(token, StringComparison.OrdinalIgnoreCase) || label.StartsWith(token + "-", StringComparison.OrdinalIgnoreCase) || label.EndsWith("-" + token, StringComparison.OrdinalIgnoreCase))
                    return new(true, 98, $"Known adult brand label: {token}");

        lock (_gate)
        {
            var current = value;
            while (true)
            {
                if (_sourcesByDomain.TryGetValue(current, out var sources))
                {
                    if (sources.Count >= 2) return new(true, 90, $"Corroborated by {sources.Count} adult-domain feeds: {string.Join(", ", sources.OrderBy(x => x))}");
                    return new(false, 45, $"Only one adult-domain feed matched ({sources.First()}); suppressed to reduce false positives");
                }
                var dot = current.IndexOf('.'); if (dot < 0) break; current = current[(dot + 1)..];
            }
        }
        return new(false, 10, "No high-confidence adult signal");
    }

    public string? AddSafeDomain(string? domain)
    {
        var root = RootDomain(Normalize(domain));
        if (string.IsNullOrWhiteSpace(root) || !root.Contains('.')) return null;
        lock (_gate) { _safeRoots.Add(root); SaveSafe(); }
        return root;
    }

    public void RemoveSafeDomain(string? domain)
    {
        var root = RootDomain(Normalize(domain));
        lock (_gate) { _safeRoots.Remove(root); SaveSafe(); }
    }

    public string[] GetSafeDomains() { lock (_gate) return _safeRoots.OrderBy(x => x).ToArray(); }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_dataPath); LoadSafe(); LoadCache();
        await RefreshAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await RefreshAsync(stoppingToken);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var merged = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase); var failures = new List<string>();
        foreach (var source in Sources)
        {
            try
            {
                var client = httpClientFactory.CreateClient(); client.Timeout = TimeSpan.FromSeconds(45);
                var text = await client.GetStringAsync(source.Url, ct);
                foreach (var domain in ParseDomains(text))
                {
                    if (MatchesAny(domain, TrustedRoots)) continue;
                    if (!merged.TryGetValue(domain, out var set)) merged[domain] = set = new(StringComparer.OrdinalIgnoreCase);
                    set.Add(source.Name);
                }
            }
            catch (Exception ex) { failures.Add($"{source.Name}: {ex.Message}"); }
        }
        if (merged.Count > 1000)
        {
            lock (_gate) { _sourcesByDomain.Clear(); foreach (var x in merged) _sourcesByDomain[x.Key] = x.Value; _lastUpdatedUtc = DateTime.UtcNow; _lastError = failures.Count == 0 ? null : string.Join(" | ", failures); SaveCache(); }
        }
        else if (failures.Count > 0) { lock (_gate) _lastError = string.Join(" | ", failures); logger.LogWarning("Adult intelligence refresh incomplete: {Error}", _lastError); }
    }

    private IEnumerable<string> ParseDomains(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;
            var parts = Regex.Split(line, "\\s+"); var candidate = line.StartsWith("||") && line.EndsWith('^') ? line[2..^1] : parts.Length >= 2 && IsSinkhole(parts[0]) ? parts[1] : parts[0];
            var d = Normalize(candidate); if (d.Length > 3 && d.Length < 254 && d.Contains('.') && !d.Contains('*') && !d.Contains('/')) yield return d;
        }
    }

    private void LoadSafe() { try { var p = Path.Combine(_dataPath,"safe-domains.json"); if (!File.Exists(p)) return; foreach (var x in JsonSerializer.Deserialize<string[]>(File.ReadAllText(p)) ?? []) _safeRoots.Add(Normalize(x)); } catch { } }
    private void SaveSafe() { File.WriteAllText(Path.Combine(_dataPath,"safe-domains.json"), JsonSerializer.Serialize(_safeRoots.OrderBy(x=>x).ToArray(), new JsonSerializerOptions{WriteIndented=true})); }
    private void LoadCache() { try { var p=Path.Combine(_dataPath,"adult-intelligence-cache.json"); if(!File.Exists(p))return; var cache=JsonSerializer.Deserialize<Dictionary<string,string[]>>(File.ReadAllText(p)); if(cache is null)return; foreach(var x in cache)_sourcesByDomain[x.Key]=x.Value.ToHashSet(StringComparer.OrdinalIgnoreCase); } catch { } }
    private void SaveCache() { File.WriteAllText(Path.Combine(_dataPath,"adult-intelligence-cache.json"), JsonSerializer.Serialize(_sourcesByDomain.ToDictionary(x=>x.Key,x=>x.Value.ToArray()))); }
    private static string ResolveDataPath(IConfiguration c) { var p=c["HomeWatch:DataPath"]; return string.IsNullOrWhiteSpace(p)?Path.Combine(AppContext.BaseDirectory,"data"):p; }
    private static bool IsSinkhole(string s)=>s is "0.0.0.0" or "127.0.0.1" or "::" or "::1";
    private static bool MatchesAny(string value,IEnumerable<string> roots)=>roots.Any(r=>value.Equals(r,StringComparison.OrdinalIgnoreCase)||value.EndsWith("."+r,StringComparison.OrdinalIgnoreCase));
    private static string RootDomain(string d){var p=d.Split('.',StringSplitOptions.RemoveEmptyEntries);return p.Length>=2?$"{p[^2]}.{p[^1]}":d;}
    private static string Normalize(string? value){if(string.IsNullOrWhiteSpace(value))return string.Empty;var s=value.Trim().Trim('.').ToLowerInvariant();if(Uri.TryCreate(s,UriKind.Absolute,out var u))s=u.Host;var slash=s.IndexOf('/');if(slash>=0)s=s[..slash];var colon=s.IndexOf(':');if(colon>=0)s=s[..colon];return s.Trim('.');}
}
