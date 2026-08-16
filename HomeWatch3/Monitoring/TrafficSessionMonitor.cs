using System.Globalization;
using System.Net;
using System.Text.Json;
using HomeWatch3.Connectors.Ntopng;
using HomeWatch3.Connectors.Opnsense;
using HomeWatch3.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

public sealed record TrafficSample(DateTime TimestampUtc, long DeviceId, string Ip, long BitsIn, long BitsOut);
public sealed record SessionDnsEvidence(DateTime TimestampUtc, string Domain, string QueryType, string Action, string Source, string? Policy, string? Rcode, int Confidence, string Evidence);
public sealed record SessionFlowEvidence(
    string FlowId, DateTime StartedUtc, DateTime LastSeenUtc, string? Hostname, string? Application,
    string Protocol, string RemoteIp, int RemotePort, long BytesDown, long BytesUp,
    string? Country, string Visibility, string Source, int Confidence, bool Encrypted);
public sealed record VideoSessionRecord(
    Guid Id, long DeviceId, string DeviceName, string Ip, string Service, bool Adult,
    DateTime StartedUtc, DateTime LastSeenUtc, DateTime? EndedUtc,
    long BytesDown, long BytesUp, long PeakBitsIn, long PeakBitsOut,
    string[] Domains, string[] Policies, int BlockedRequests, bool Active,
    long AttributedBytesDown, long AttributedBytesUp, int AttributionConfidence,
    string[] ResolvedServiceIps, string[] MatchedRemoteIps, SessionDnsEvidence[] Evidence,
    string[] Applications, string[] Protocols, string Visibility, string[] TelemetrySources,
    SessionFlowEvidence[] FlowEvidence);

public sealed class TrafficSessionMonitor(
    IServiceScopeFactory scopeFactory,
    IOpnsenseClient opnsense,
    NtopngFlowMonitor ntopngFlows,
    IgnoredDomainStore ignoredDomains,
    IAdultDomainClassifier adultClassifier,
    IWebHostEnvironment env,
    ILogger<TrafficSessionMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(10);
    // DNS is only a session signal. Browsers and video apps can keep an encrypted
    // stream alive for much longer without resolving another recognizable host.
    // Keep the close timeout short for the UI, but stitch a later signal back into
    // the same session when the quiet gap is still plausibly one viewing period.
    private static readonly TimeSpan SessionMergeWindow = TimeSpan.FromMinutes(30);
    private readonly object _gate = new();
    private readonly List<TrafficSample> _samples = new();
    private readonly List<VideoSessionRecord> _sessions = new();
    private readonly HashSet<string> _seenDns = new(StringComparer.Ordinal);
    private readonly Dictionary<string,(DateTime ExpiresUtc,string[] Ips)> _dnsCache = new(StringComparer.OrdinalIgnoreCase);
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
        var since = DateTime.UtcNow.AddMinutes(-Math.Clamp(minutes, 1, 43200));
        lock (_gate)
            return CoalesceSessions(_sessions.Where(x => x.LastSeenUtc >= since))
                .OrderByDescending(x => x.LastSeenUtc)
                .ToArray();
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
                deviceId = g.Key.DeviceId, ip = g.Key.Ip, seconds, samples = g.Count(),
                averageBitsIn = (long)g.Average(x => x.BitsIn), averageBitsOut = (long)g.Average(x => x.BitsOut),
                peakBitsIn = g.Max(x => x.BitsIn), peakBitsOut = g.Max(x => x.BitsOut),
                estimatedBytesDown = (long)g.Sum(x => x.BitsIn / 8.0 * 5.0), estimatedBytesUp = (long)g.Sum(x => x.BitsOut / 8.0 * 5.0),
                firstSampleUtc = g.Min(x => x.TimestampUtc), lastSampleUtc = g.Max(x => x.TimestampUtc)
            }).ToArray();
        }
    }

    public object GetNetworkTrafficTimeline(int seconds)
    {
        seconds = Math.Clamp(seconds, 30, 3600);
        var since = DateTime.UtcNow.AddSeconds(-seconds);
        lock (_gate)
        {
            return _samples
                .Where(x => x.TimestampUtc >= since)
                .GroupBy(x => new DateTime(
                    x.TimestampUtc.Year,
                    x.TimestampUtc.Month,
                    x.TimestampUtc.Day,
                    x.TimestampUtc.Hour,
                    x.TimestampUtc.Minute,
                    x.TimestampUtc.Second - x.TimestampUtc.Second % 5,
                    DateTimeKind.Utc))
                .OrderBy(x => x.Key)
                .Select(group => new
                {
                    timestampUtc = group.Key,
                    bitsIn = group.Sum(x => x.BitsIn),
                    bitsOut = group.Sum(x => x.BitsOut),
                    activeDevices = group.Select(x => x.DeviceId).Distinct().Count()
                })
                .ToArray();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Load(); using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5)); await Poll(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await Poll(stoppingToken);
    }

    private async Task Poll(CancellationToken ct)
    {
        try
        {
            var now = DateTime.UtcNow; var elapsed = Math.Clamp((now - _lastPollUtc).TotalSeconds, 1, 15); _lastPollUtc = now;
            var trafficTask = opnsense.GetTrafficTopAsync("lan", ct); var dnsTask = opnsense.GetUnboundQueriesAsync(ct); await Task.WhenAll(trafficTask, dnsTask);
            var traffic = await trafficTask; var dns = await dnsTask;
            using var scope = scopeFactory.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
            var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
            var byIp = devices.Where(x => !string.IsNullOrWhiteSpace(x.LastIpAddress))
                .GroupBy(x => x.LastIpAddress!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.OrderByDescending(d => d.LastSeenUtc).First(), StringComparer.OrdinalIgnoreCase);

            lock (_gate)
            {
                foreach (var tr in ExtractTraffic(traffic))
                {
                    var ip = GetString(tr, "address"); if (string.IsNullOrWhiteSpace(ip) || !byIp.TryGetValue(ip, out var device)) continue;
                    var bin = GetLong(tr, "rate_bits_in"); var bout = GetLong(tr, "rate_bits_out"); _samples.Add(new TrafficSample(now, device.Id, ip, bin, bout));
                    var active = _sessions.LastOrDefault(x => x.Active && x.DeviceId == device.Id);
                    if (active is null) continue;

                    var details = ExtractTrafficDetails(tr).ToArray();
                    var totalDetailBits = details.Sum(x => GetLong(x, "rate_bits"));
                    var resolved = new HashSet<string>(active.ResolvedServiceIps ?? [], StringComparer.OrdinalIgnoreCase);
                    var matched = details.Where(x => resolved.Contains(GetString(x, "address") ?? string.Empty)).ToArray();
                    var matchedBits = matched.Sum(x => GetLong(x, "rate_bits"));
                    var share = totalDetailBits > 0 ? Math.Clamp((double)matchedBits / totalDetailBits, 0, 1) : 0;
                    var attrDown = active.AttributedBytesDown + (long)(bin / 8.0 * elapsed * share);
                    var attrUp = active.AttributedBytesUp + (long)(bout / 8.0 * elapsed * share);
                    var matchedIps = active.MatchedRemoteIps.Concat(matched.Select(x => GetString(x, "address") ?? "")).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
                    var confidence = matched.Length == 0 ? active.AttributionConfidence : Math.Max(active.AttributionConfidence, matched.Length == details.Length && details.Length > 0 ? 85 : 65);
                    ReplaceSession(active with
                    {
                        BytesDown = active.BytesDown + (long)(bin / 8.0 * elapsed), BytesUp = active.BytesUp + (long)(bout / 8.0 * elapsed),
                        PeakBitsIn = Math.Max(active.PeakBitsIn, bin), PeakBitsOut = Math.Max(active.PeakBitsOut, bout),
                        AttributedBytesDown = attrDown, AttributedBytesUp = attrUp, AttributionConfidence = confidence, MatchedRemoteIps = matchedIps
                    });
                }
                _samples.RemoveAll(x => x.TimestampUtc < now.AddHours(-24));
            }

            foreach (var row in ExtractDns(dns).OrderBy(ParseDnsTime))
            {
                var fp = row.GetRawText(); lock (_gate) { if (!_seenDns.Add(fp)) continue; if (_seenDns.Count > 10000) _seenDns.Clear(); }
                var domain = GetString(row, "domain", "name", "qname", "query")?.Trim().Trim('.').ToLowerInvariant(); var ip = GetString(row, "client", "client_ip", "src", "ip");
                if (string.IsNullOrWhiteSpace(domain) || ignoredDomains.IsIgnored(domain) || string.IsNullOrWhiteSpace(ip) || !byIp.TryGetValue(ip, out var device)) continue;
                var service = Classify(domain); if (service is null) continue;
                var adultResult = adultClassifier.Classify(domain); if (service.Value.Adult && !adultResult.IsAdult) continue;
                var resolvedIps = await ResolveDomainIps(domain, ct);
                var when = ParseDnsTime(row); var action = GetString(row, "action") ?? "Pass"; var policy = GetString(row, "policy");
                var queryType = GetString(row, "type", "qtype", "query_type") ?? "DNS"; var source = GetString(row, "source") ?? "OPNsense Unbound"; var rcode = GetString(row, "rcode");
                var evidence = new SessionDnsEvidence(when, domain, queryType, action, source, policy, rcode,
                    service.Value.Adult ? adultResult.Confidence : 90,
                    service.Value.Adult ? adultResult.Evidence : $"Recognized {service.Value.Service} service domain");

                lock (_gate)
                {
                    // Reuse the most recent same-device/same-service session when the new signal is
                    // within the stitch window, even if that session was already marked ended. This
                    // prevents CDN hostname churn or a brief DNS gap from creating duplicate cards.
                    var existing = _sessions
                        .Where(x => SameDevice(x, device.Id, ip) && x.Service.Equals(service.Value.Service, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(x => x.LastSeenUtc)
                        .FirstOrDefault();
                    var canContinue = existing is not null && when >= existing.StartedUtc.AddMinutes(-1) && when - existing.LastSeenUtc <= SessionMergeWindow;

                    if (!canContinue)
                    {
                        _sessions.Add(new VideoSessionRecord(Guid.NewGuid(), device.Id, device.Name ?? ip, ip, service.Value.Service, service.Value.Adult,
                            when, when, null, 0, 0, 0, 0, [domain], string.IsNullOrWhiteSpace(policy) ? [] : [policy],
                            action.Equals("Block", StringComparison.OrdinalIgnoreCase) ? 1 : 0, true,
                            0, 0, 0, resolvedIps, [], [evidence], [], ["DNS"], "hostname", ["opnsense-unbound"], []));
                    }
                    else
                    {
                        ReplaceSession(existing! with
                        {
                            Active = true,
                            EndedUtc = null,
                            StartedUtc = when < existing!.StartedUtc ? when : existing.StartedUtc,
                            LastSeenUtc = when > existing.LastSeenUtc ? when : existing.LastSeenUtc,
                            Domains = existing.Domains.Append(domain).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray(),
                            Policies = string.IsNullOrWhiteSpace(policy) ? existing.Policies : existing.Policies.Append(policy).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                            BlockedRequests = existing.BlockedRequests + (action.Equals("Block", StringComparison.OrdinalIgnoreCase) ? 1 : 0),
                            ResolvedServiceIps = existing.ResolvedServiceIps.Concat(resolvedIps).Distinct(StringComparer.OrdinalIgnoreCase).Take(250).ToArray(),
                            Evidence = existing.Evidence.Append(evidence).GroupBy(x => new { x.TimestampUtc, x.Domain, x.QueryType, x.Action }).Select(g => g.First()).OrderBy(x => x.TimestampUtc).TakeLast(500).ToArray(),
                            Visibility = TelemetryNaming.VisibilityRank(existing.Visibility) >= TelemetryNaming.VisibilityRank("hostname") ? existing.Visibility : "hostname",
                            TelemetrySources = existing.TelemetrySources.Append("opnsense-unbound").Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                        });
                    }
                }
            }

            ProcessNtopngFlows(ntopngFlows.GetSnapshot(), byIp);

            lock (_gate)
            {
                foreach (var s in _sessions.Where(x => x.Active && now - x.LastSeenUtc > SessionIdleTimeout).ToArray()) ReplaceSession(s with { Active = false, EndedUtc = s.LastSeenUtc });
                MergeDuplicatesInPlace();
                _sessions.RemoveAll(x => x.LastSeenUtc < now.AddDays(-30));
            }
            Save();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "Traffic/video session poll failed"); }
    }

    private async Task<string[]> ResolveDomainIps(string domain, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_dnsCache.TryGetValue(domain, out var cached) && cached.ExpiresUtc > DateTime.UtcNow) return cached.Ips;
        }
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain, ct).WaitAsync(TimeSpan.FromSeconds(2), ct);
            var ips = addresses.Select(x => x.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToArray();
            lock (_gate) _dnsCache[domain] = (DateTime.UtcNow.AddMinutes(10), ips);
            return ips;
        }
        catch
        {
            lock (_gate) _dnsCache[domain] = (DateTime.UtcNow.AddMinutes(2), []);
            return [];
        }
    }

    private void ProcessNtopngFlows(NtopngFlowSnapshot snapshot, IReadOnlyDictionary<string, Device> byIp)
    {
        if (!snapshot.Available) return;
        foreach (var flow in snapshot.Flows)
        {
            var clientIp = flow.Client.IpAddress?.Trim();
            var serverIp = flow.Server.IpAddress?.Trim();
            Device? clientDevice = null;
            Device? serverDevice = null;
            var localIsClient = !string.IsNullOrWhiteSpace(clientIp) && byIp.TryGetValue(clientIp, out clientDevice);
            var localIsServer = !string.IsNullOrWhiteSpace(serverIp) && byIp.TryGetValue(serverIp, out serverDevice);
            if (!localIsClient && !localIsServer) continue;

            var device = localIsClient ? clientDevice! : serverDevice!;
            var localIp = localIsClient ? clientIp! : serverIp!;
            var remote = localIsClient ? flow.Server : flow.Client;
            var hostname = TelemetryNaming.NormalizeHostname(remote.Name, remote.IpAddress);
            if (!string.IsNullOrWhiteSpace(hostname) && ignoredDomains.IsIgnored(hostname)) continue;
            var application = TelemetryNaming.NormalizeApplication(flow.Application);
            var service = ClassifySignal(hostname, application);
            if (service is null) continue;

            var adult = string.IsNullOrWhiteSpace(hostname) ? null : adultClassifier.Classify(hostname);
            if (service.Value.Adult && adult?.IsAdult != true) continue;
            var visibility = TelemetryNaming.Visibility(null, hostname, application, remote.IpAddress);
            var encrypted = TelemetryNaming.IsEncrypted(flow.Layer4Protocol, flow.Application, remote.Port);
            var confidence = service.Value.Adult ? adult!.Confidence : visibility == "hostname" ? 90 : 80;
            var protocol = string.Join("/", new[] { flow.Layer4Protocol, flow.Application }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var clientToServerBytes = (long)Math.Round(flow.Bytes * flow.ClientToServerPercent / 100d);
            var serverToClientBytes = Math.Max(0, flow.Bytes - clientToServerBytes);
            var bytesUp = localIsClient ? clientToServerBytes : serverToClientBytes;
            var bytesDown = localIsClient ? serverToClientBytes : clientToServerBytes;
            var remoteIp = remote.IpAddress ?? string.Empty;
            var flowId = SessionFlowId(snapshot.InterfaceId, flow);
            var evidence = new SessionFlowEvidence(
                flowId, flow.FirstSeenUtc, flow.LastSeenUtc, hostname, application, protocol, remoteIp,
                remote.Port, bytesDown, bytesUp, remote.Country, visibility, "ntopng-flow", confidence, encrypted);

            lock (_gate)
            {
                var existing = _sessions
                    .Where(x => SameDevice(x, device.Id, localIp) && x.Service.Equals(service.Value.Service, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.LastSeenUtc)
                    .FirstOrDefault();
                var canContinue = existing is not null
                    && flow.FirstSeenUtc <= existing.LastSeenUtc + SessionMergeWindow
                    && flow.LastSeenUtc >= existing.StartedUtc - TimeSpan.FromMinutes(1);
                var domains = string.IsNullOrWhiteSpace(hostname) ? Array.Empty<string>() : new[] { hostname };
                var applications = string.IsNullOrWhiteSpace(application) ? Array.Empty<string>() : new[] { application };
                var protocols = string.IsNullOrWhiteSpace(protocol) ? Array.Empty<string>() : new[] { protocol };
                var remoteIps = string.IsNullOrWhiteSpace(remoteIp) ? Array.Empty<string>() : new[] { remoteIp };

                if (!canContinue)
                {
                    _sessions.Add(new VideoSessionRecord(
                        Guid.NewGuid(), device.Id, device.Name ?? localIp, localIp, service.Value.Service, service.Value.Adult,
                        flow.FirstSeenUtc, flow.LastSeenUtc, null, 0, 0, 0, 0, domains, [], 0, true,
                        bytesDown, bytesUp, confidence, remoteIps, remoteIps, [], applications, protocols,
                        visibility, ["ntopng-flow"], [evidence]));
                    continue;
                }

                var flowEvidence = existing!.FlowEvidence
                    .Where(x => !x.FlowId.Equals(flowId, StringComparison.Ordinal))
                    .Append(evidence)
                    .OrderBy(x => x.StartedUtc)
                    .TakeLast(500)
                    .ToArray();
                var bestVisibility = TelemetryNaming.VisibilityRank(existing.Visibility) >= TelemetryNaming.VisibilityRank(visibility)
                    ? existing.Visibility
                    : visibility;
                ReplaceSession(existing with
                {
                    DeviceId = device.Id,
                    DeviceName = device.Name ?? existing.DeviceName,
                    Ip = localIp,
                    Active = true,
                    EndedUtc = null,
                    StartedUtc = flow.FirstSeenUtc < existing.StartedUtc ? flow.FirstSeenUtc : existing.StartedUtc,
                    LastSeenUtc = flow.LastSeenUtc > existing.LastSeenUtc ? flow.LastSeenUtc : existing.LastSeenUtc,
                    Domains = existing.Domains.Concat(domains).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray(),
                    Applications = existing.Applications.Concat(applications).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Protocols = existing.Protocols.Concat(protocols).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    Visibility = bestVisibility,
                    TelemetrySources = existing.TelemetrySources.Append("ntopng-flow").Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    ResolvedServiceIps = existing.ResolvedServiceIps.Concat(remoteIps).Distinct(StringComparer.OrdinalIgnoreCase).Take(250).ToArray(),
                    MatchedRemoteIps = existing.MatchedRemoteIps.Concat(remoteIps).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray(),
                    AttributedBytesDown = Math.Max(existing.AttributedBytesDown, flowEvidence.Sum(x => x.BytesDown)),
                    AttributedBytesUp = Math.Max(existing.AttributedBytesUp, flowEvidence.Sum(x => x.BytesUp)),
                    AttributionConfidence = Math.Max(existing.AttributionConfidence, confidence),
                    FlowEvidence = flowEvidence
                });
            }
        }
    }

    private static string SessionFlowId(int interfaceId, NtopngActiveFlow flow)
    {
        var key = string.IsNullOrWhiteSpace(flow.Key) ? flow.HashId : flow.Key;
        var started = new DateTimeOffset(DateTime.SpecifyKind(flow.FirstSeenUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return $"{interfaceId}:{key}:{started}";
    }

    private void MergeDuplicatesInPlace()
    {
        var merged = CoalesceSessions(_sessions).ToList();
        _sessions.Clear();
        _sessions.AddRange(merged);
    }

    private static IEnumerable<VideoSessionRecord> CoalesceSessions(IEnumerable<VideoSessionRecord> source)
    {
        // First discard exact persisted copies without adding their byte totals a
        // second time. Then repair near fragments by stable database ID or by the
        // same LAN address. The time condition prevents unrelated DHCP occupants
        // from being combined simply because they used the same address later.
        var unique = source
            .GroupBy(x => x.Id)
            .Select(g => g.OrderByDescending(x => x.LastSeenUtc).ThenByDescending(x => x.Evidence?.Length ?? 0).First())
            .OrderBy(x => x.StartedUtc)
            .ThenBy(x => x.LastSeenUtc);
        var merged = new List<VideoSessionRecord>();

        foreach (var next in unique)
        {
            var matches = merged.Where(current =>
                    current.Service.Equals(next.Service, StringComparison.OrdinalIgnoreCase) &&
                    SameDevice(current, next.DeviceId, next.Ip) &&
                    next.StartedUtc <= current.LastSeenUtc + SessionMergeWindow &&
                    next.LastSeenUtc >= current.StartedUtc - SessionMergeWindow)
                .ToArray();

            var combined = next;
            foreach (var match in matches)
            {
                merged.Remove(match);
                combined = MergePair(match, combined);
            }
            merged.Add(combined);
        }

        return merged;
    }

    private static VideoSessionRecord MergePair(VideoSessionRecord a, VideoSessionRecord b)
    {
        var active = a.Active || b.Active;
        var latestEnd = new[] { a.EndedUtc, b.EndedUtc }.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty().Max();
        var latest = a.LastSeenUtc >= b.LastSeenUtc ? a : b;
        var preferredName = PreferredDeviceName(a, b);
        return a with
        {
            DeviceId = latest.DeviceId,
            DeviceName = preferredName,
            Ip = string.IsNullOrWhiteSpace(latest.Ip) ? (string.IsNullOrWhiteSpace(a.Ip) ? b.Ip : a.Ip) : latest.Ip,
            Adult = a.Adult || b.Adult,
            StartedUtc = a.StartedUtc <= b.StartedUtc ? a.StartedUtc : b.StartedUtc,
            LastSeenUtc = a.LastSeenUtc >= b.LastSeenUtc ? a.LastSeenUtc : b.LastSeenUtc,
            EndedUtc = active ? null : latestEnd == default ? (a.LastSeenUtc >= b.LastSeenUtc ? a.LastSeenUtc : b.LastSeenUtc) : latestEnd,
            BytesDown = a.BytesDown + b.BytesDown,
            BytesUp = a.BytesUp + b.BytesUp,
            PeakBitsIn = Math.Max(a.PeakBitsIn, b.PeakBitsIn),
            PeakBitsOut = Math.Max(a.PeakBitsOut, b.PeakBitsOut),
            Domains = a.Domains.Concat(b.Domains).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray(),
            Policies = a.Policies.Concat(b.Policies).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            BlockedRequests = a.BlockedRequests + b.BlockedRequests,
            Active = active,
            AttributedBytesDown = a.AttributedBytesDown + b.AttributedBytesDown,
            AttributedBytesUp = a.AttributedBytesUp + b.AttributedBytesUp,
            AttributionConfidence = Math.Max(a.AttributionConfidence, b.AttributionConfidence),
            ResolvedServiceIps = a.ResolvedServiceIps.Concat(b.ResolvedServiceIps).Distinct(StringComparer.OrdinalIgnoreCase).Take(250).ToArray(),
            MatchedRemoteIps = a.MatchedRemoteIps.Concat(b.MatchedRemoteIps).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray(),
            Evidence = a.Evidence.Concat(b.Evidence)
                .GroupBy(x => new { x.TimestampUtc, x.Domain, x.QueryType, x.Action })
                .Select(g => g.First())
                .OrderBy(x => x.TimestampUtc)
                .TakeLast(500)
                .ToArray(),
            Applications = a.Applications.Concat(b.Applications).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Protocols = a.Protocols.Concat(b.Protocols).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Visibility = TelemetryNaming.VisibilityRank(a.Visibility) >= TelemetryNaming.VisibilityRank(b.Visibility) ? a.Visibility : b.Visibility,
            TelemetrySources = a.TelemetrySources.Concat(b.TelemetrySources).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            FlowEvidence = a.FlowEvidence.Concat(b.FlowEvidence)
                .GroupBy(x => x.FlowId, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(x => x.LastSeenUtc).First())
                .OrderBy(x => x.StartedUtc)
                .TakeLast(500)
                .ToArray()
        };
    }

    private static bool SameDevice(VideoSessionRecord session, long deviceId, string ip) =>
        session.DeviceId == deviceId ||
        (!string.IsNullOrWhiteSpace(session.Ip) && !string.IsNullOrWhiteSpace(ip) &&
         session.Ip.Equals(ip, StringComparison.OrdinalIgnoreCase));

    private static string PreferredDeviceName(VideoSessionRecord a, VideoSessionRecord b)
    {
        static bool IsUseful(string? name, string? ip) =>
            !string.IsNullOrWhiteSpace(name) &&
            !name.Equals(ip, StringComparison.OrdinalIgnoreCase) &&
            !IPAddress.TryParse(name, out _);

        if (IsUseful(b.DeviceName, b.Ip) && (!IsUseful(a.DeviceName, a.Ip) || b.LastSeenUtc >= a.LastSeenUtc)) return b.DeviceName;
        if (IsUseful(a.DeviceName, a.Ip)) return a.DeviceName;
        return string.IsNullOrWhiteSpace(b.DeviceName) ? a.DeviceName : b.DeviceName;
    }

    private void ReplaceSession(VideoSessionRecord value) { var i = _sessions.FindIndex(x => x.Id == value.Id); if (i >= 0) _sessions[i] = value; }
    private (string Service, bool Adult)? Classify(string domain) { foreach (var s in Services) if (s.Tokens.Any(t => domain.Contains(t, StringComparison.OrdinalIgnoreCase))) return (s.Service, s.Adult); return null; }
    private (string Service, bool Adult)? ClassifySignal(string? hostname, string? application)
    {
        var signal = $"{hostname} {application}";
        foreach (var service in Services)
            if (signal.Contains(service.Service, StringComparison.OrdinalIgnoreCase)
                || service.Tokens.Any(token => signal.Contains(token, StringComparison.OrdinalIgnoreCase)))
                return (service.Service, service.Adult);
        return null;
    }
    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var data = JsonSerializer.Deserialize<List<VideoSessionRecord>>(File.ReadAllText(_path));
            if (data is null) return;
            lock (_gate)
            {
                _sessions.AddRange(data.Select(x => x with
                {
                    Active = false, EndedUtc = x.EndedUtc ?? x.LastSeenUtc,
                    Domains = x.Domains ?? [], Policies = x.Policies ?? [], ResolvedServiceIps = x.ResolvedServiceIps ?? [], MatchedRemoteIps = x.MatchedRemoteIps ?? [], Evidence = x.Evidence ?? [],
                    Applications = x.Applications ?? [], Protocols = x.Protocols ?? [], Visibility = string.IsNullOrWhiteSpace(x.Visibility) ? "hostname" : x.Visibility,
                    TelemetrySources = x.TelemetrySources ?? [], FlowEvidence = x.FlowEvidence ?? []
                }));
                MergeDuplicatesInPlace();
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not load video sessions"); }
    }
    private void Save() { try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); List<VideoSessionRecord> copy; lock (_gate) copy = _sessions.ToList(); File.WriteAllText(_path, JsonSerializer.Serialize(copy)); } catch (Exception ex) { logger.LogWarning(ex, "Could not save video sessions"); } }
    private static IEnumerable<JsonElement> ExtractTraffic(JsonElement payload) { if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("lan", out var lan) && lan.TryGetProperty("records", out var rec) && rec.ValueKind == JsonValueKind.Array) return rec.EnumerateArray().Select(x => x.Clone()); return []; }
    private static IEnumerable<JsonElement> ExtractTrafficDetails(JsonElement row) { if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array) return details.EnumerateArray().Select(x => x.Clone()); return []; }
    private static IEnumerable<JsonElement> ExtractDns(JsonElement payload) { if (payload.ValueKind == JsonValueKind.Array) return payload.EnumerateArray().Select(x => x.Clone()); if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array) return rows.EnumerateArray().Select(x => x.Clone()); return []; }
    private static string? GetString(JsonElement row, params string[] names) { foreach (var n in names) if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(n, out var v)) return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString(); return null; }
    private static long GetLong(JsonElement row, string name) { if (!row.TryGetProperty(name, out var v)) return 0; if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n; return long.TryParse(v.ToString(), out n) ? n : 0; }
    private static DateTime ParseDnsTime(JsonElement row) { var s = GetString(row, "time", "timestamp", "created", "date"); if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)) try { return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime; } catch { } return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : DateTime.UtcNow; }
}
