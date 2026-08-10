using System.Globalization;
using System.Text.Json;
using HomeWatch3.Connectors.Opnsense;
using HomeWatch3.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

public sealed record TrafficSample(DateTime TimestampUtc, long DeviceId, string Ip, long BitsIn, long BitsOut);
public sealed record VideoSessionRecord(Guid Id, long DeviceId, string DeviceName, string Ip, string Service, bool Adult, DateTime StartedUtc, DateTime LastSeenUtc, DateTime? EndedUtc, long BytesDown, long BytesUp, long PeakBitsIn, long PeakBitsOut, string[] Domains, string[] Policies, int BlockedRequests, bool Active);

public sealed class TrafficSessionMonitor(
    IServiceScopeFactory scopeFactory,
    IOpnsenseClient opnsense,
    IgnoredDomainStore ignoredDomains,
    IWebHostEnvironment env,
    ILogger<TrafficSessionMonitor> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly List<TrafficSample> _samples = new();
    private readonly List<VideoSessionRecord> _sessions = new();
    private readonly HashSet<string> _seenDns = new(StringComparer.Ordinal);
    private readonly string _path = Path.Combine(env.ContentRootPath, "data", "video-sessions.json");
    private DateTime _lastPollUtc = DateTime.UtcNow;

    private static readonly (string Service, bool Adult, string[] Tokens)[] Services =
    [
        ("Pornhub", true, ["pornhub"]), ("XVideos", true, ["xvideos"]), ("XNXX", true, ["xnxx"]),
        ("YouPorn", true, ["youporn"]), ("RedTube", true, ["redtube"]), ("Tube8", true, ["tube8"]),
        ("YouTube", false, ["youtube.com","googlevideo.com","youtu.be"]), ("Netflix", false, ["netflix.com","nflxvideo.net","nflximg.net"]),
        ("TikTok", false, ["tiktok.com","tiktokcdn.com","byteoversea.com"]), ("Twitch", false, ["twitch.tv","ttvnw.net"]),
        ("Vimeo", false, ["vimeo.com","vimeocdn.com"]), ("Prime Video", false, ["primevideo.com","amazonvideo.com"]),
        ("Disney+", false, ["disneyplus.com","dssott.com"]), ("Dailymotion", false, ["dailymotion.com","dmcdn.net"])
    ];

    public IReadOnlyList<VideoSessionRecord> GetSessions(int minutes = 1440)
    {
        var since = DateTime.UtcNow.AddMinutes(-Math.Clamp(minutes, 1, 10080));
        lock (_gate) return _sessions.Where(x => x.LastSeenUtc >= since).OrderByDescending(x => x.LastSeenUtc).ToArray();
    }

    public object GetTrafficWindow(int seconds, long? deviceId = null)
    {
        seconds = Math.Clamp(seconds, 10, 3600);
        var since = DateTime.UtcNow.AddSeconds(-seconds);
        lock (_gate)
        {
            var rows = _samples.Where(x => x.TimestampUtc >= since && (!deviceId.HasValue || x.DeviceId == deviceId)).ToList();
            return rows.GroupBy(x => new { x.DeviceId, x.Ip }).Select(g => new
            {
                deviceId = g.Key.DeviceId, ip = g.Key.Ip, seconds,
                samples = g.Count(),
                averageBitsIn = (long)g.Average(x => x.BitsIn), averageBitsOut = (long)g.Average(x => x.BitsOut),
                peakBitsIn = g.Max(x => x.BitsIn), peakBitsOut = g.Max(x => x.BitsOut),
                estimatedBytesDown = (long)g.Sum(x => x.BitsIn / 8.0 * 5.0),
                estimatedBytesUp = (long)g.Sum(x => x.BitsOut / 8.0 * 5.0),
                firstSampleUtc = g.Min(x => x.TimestampUtc), lastSampleUtc = g.Max(x => x.TimestampUtc)
            }).ToArray();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Load();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        await Poll(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await Poll(stoppingToken);
    }

    private async Task Poll(CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow;
            var elapsed = Math.Clamp((now - _lastPollUtc).TotalSeconds, 1, 15);
            _lastPollUtc = now;
            var trafficTask = opnsense.GetTrafficTopAsync("lan", ct);
            var dnsTask = opnsense.GetUnboundQueriesAsync(ct);
            await Task.WhenAll(trafficTask, dnsTask);
            var traffic = await trafficTask; var dns = await dnsTask;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
            var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
            var byIp = devices.Where(x => !string.IsNullOrWhiteSpace(x.LastIpAddress)).ToDictionary(x => x.LastIpAddress!, StringComparer.OrdinalIgnoreCase);

            var trafficRows = ExtractTraffic(traffic).ToArray();
            lock (_gate)
            {
                foreach (var tr in trafficRows)
                {
                    var ip = GetString(tr, "address"); if (string.IsNullOrWhiteSpace(ip) || !byIp.TryGetValue(ip, out var device)) continue;
                    var bin = GetLong(tr, "rate_bits_in"); var bout = GetLong(tr, "rate_bits_out");
                    _samples.Add(new TrafficSample(now, device.Id, ip, bin, bout));
                    var active = _sessions.LastOrDefault(x => x.Active && x.DeviceId == device.Id);
                    if (active is not null)
                    {
                        ReplaceSession(active with { BytesDown = active.BytesDown + (long)(bin / 8.0 * elapsed), BytesUp = active.BytesUp + (long)(bout / 8.0 * elapsed), PeakBitsIn = Math.Max(active.PeakBitsIn, bin), PeakBitsOut = Math.Max(active.PeakBitsOut, bout) });
                    }
                }
                _samples.RemoveAll(x => x.TimestampUtc < now.AddHours(-24));
            }

            foreach (var row in ExtractDns(dns).OrderBy(ParseDnsTime))
            {
                var fp = row.GetRawText(); lock (_gate) { if (!_seenDns.Add(fp)) continue; if (_seenDns.Count > 10000) _seenDns.Clear(); }
                var domain = GetString(row, "domain", "name", "qname", "query")?.ToLowerInvariant();
                var ip = GetString(row, "client", "client_ip", "src", "ip");
                if (string.IsNullOrWhiteSpace(domain) || ignoredDomains.IsIgnored(domain) || string.IsNullOrWhiteSpace(ip) || !byIp.TryGetValue(ip, out var device)) continue;
                var service = Classify(domain); if (service is null) continue;
                var when = ParseDnsTime(row); var action = GetString(row, "action") ?? "Pass"; var policy = GetString(row, "policy");
                lock (_gate)
                {
                    var active = _sessions.LastOrDefault(x => x.Active && x.DeviceId == device.Id && x.Service == service.Value.Service);
                    if (active is null || when - active.LastSeenUtc > TimeSpan.FromMinutes(5))
                    {
                        if (active is not null) ReplaceSession(active with { Active = false, EndedUtc = active.LastSeenUtc });
                        _sessions.Add(new VideoSessionRecord(Guid.NewGuid(), device.Id, device.Name ?? ip, ip, service.Value.Service, service.Value.Adult, when, when, null, 0, 0, 0, 0, [domain], string.IsNullOrWhiteSpace(policy)?[]:[policy], action.Equals("Block", StringComparison.OrdinalIgnoreCase)?1:0, true));
                    }
                    else
                    {
                        ReplaceSession(active with { LastSeenUtc = when > active.LastSeenUtc ? when : active.LastSeenUtc, Domains = active.Domains.Append(domain).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray(), Policies = string.IsNullOrWhiteSpace(policy) ? active.Policies : active.Policies.Append(policy).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), BlockedRequests = active.BlockedRequests + (action.Equals("Block", StringComparison.OrdinalIgnoreCase) ? 1 : 0) });
                    }
                }
            }

            lock (_gate)
            {
                foreach (var s in _sessions.Where(x => x.Active && now - x.LastSeenUtc > TimeSpan.FromMinutes(5)).ToArray()) ReplaceSession(s with { Active = false, EndedUtc = s.LastSeenUtc });
                _sessions.RemoveAll(x => x.LastSeenUtc < now.AddDays(-30));
            }
            Save();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "Traffic/video session poll failed"); }
    }

    private void ReplaceSession(VideoSessionRecord value) { var i = _sessions.FindIndex(x => x.Id == value.Id); if (i >= 0) _sessions[i] = value; }
    private (string Service, bool Adult)? Classify(string domain) { foreach (var s in Services) if (s.Tokens.Any(t => domain.Contains(t, StringComparison.OrdinalIgnoreCase))) return (s.Service, s.Adult); return null; }
    private void Load() { try { if (!File.Exists(_path)) return; var data = JsonSerializer.Deserialize<List<VideoSessionRecord>>(File.ReadAllText(_path)); if (data is not null) lock (_gate) _sessions.AddRange(data.Select(x => x with { Active = false, EndedUtc = x.EndedUtc ?? x.LastSeenUtc })); } catch (Exception ex) { logger.LogWarning(ex, "Could not load video sessions"); } }
    private void Save() { try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); List<VideoSessionRecord> copy; lock (_gate) copy = _sessions.ToList(); File.WriteAllText(_path, JsonSerializer.Serialize(copy)); } catch (Exception ex) { logger.LogWarning(ex, "Could not save video sessions"); } }
    private static IEnumerable<JsonElement> ExtractTraffic(JsonElement payload) { if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("lan", out var lan) && lan.TryGetProperty("records", out var rec) && rec.ValueKind == JsonValueKind.Array) return rec.EnumerateArray().Select(x=>x.Clone()); return []; }
    private static IEnumerable<JsonElement> ExtractDns(JsonElement payload) { if (payload.ValueKind == JsonValueKind.Array) return payload.EnumerateArray().Select(x=>x.Clone()); if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array) return rows.EnumerateArray().Select(x=>x.Clone()); return []; }
    private static string? GetString(JsonElement row, params string[] names) { foreach (var n in names) if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(n, out var v)) return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString(); return null; }
    private static long GetLong(JsonElement row, string name) { if (!row.TryGetProperty(name, out var v)) return 0; if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n; return long.TryParse(v.ToString(), out n) ? n : 0; }
    private static DateTime ParseDnsTime(JsonElement row) { var s = GetString(row, "time", "timestamp", "created", "date"); if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)) try { return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; } catch {} return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal, out var dt) ? dt : DateTime.UtcNow; }
}
