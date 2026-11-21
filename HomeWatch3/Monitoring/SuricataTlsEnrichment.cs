using System.Globalization;
using System.Net;
using System.Text.Json;
using HomeWatch3.Connectors.Opnsense;
using HomeWatch3.Data;

namespace HomeWatch3.Monitoring;

internal sealed record FlowVerdict(string Status, string Reason, string? Source, string? Confidence);
internal sealed class CoverageReport
{
    public bool Available { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public int Attempts { get; set; }
    public int Matches { get; set; }
    public Dictionary<long, string> Failures { get; } = [];
    public HashSet<long> Filled { get; } = [];
    public HashSet<long> Native { get; } = [];

    public FlowVerdict Describe(TrafficEvent flow)
    {
        if (Filled.Contains(flow.Id))
            return new("enriched", "Unique device-attributed connection tuple within the observation window.", Native.Contains(flow.Id) ? "ntopng + Suricata" : "Suricata", "High");
        if (Native.Contains(flow.Id))
            return new("enriched", "TLS fields reported directly on this flow.", "ntopng", "Direct");
        if (Failures.TryGetValue(flow.Id, out var reason)) return new("unmatched", reason, null, null);
        if (!Available) return new("unknown", "Suricata observations unavailable for this request.", null, null);
        if (!SuricataTlsEnrichment.Eligible(flow)) return new("unknown", "Insufficient device, time or connection identifiers.", null, null);
        if (IsQuic(flow)) return new("quic", "QUIC uses TLS 1.3. TCP handshake inspection differs and no matching metadata was available.", null, null);
        return new("unobserved", "No matching handshake evidence in the available alert sample.", null, null);
    }

    public static bool IsQuic(TrafficEvent flow) =>
        System.Text.RegularExpressions.Regex.IsMatch($"{flow.Protocol} {flow.Application}", @"(?:^|[^a-z0-9])(?:quic|http/?3|h3)(?:$|[^a-z0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

internal sealed class SuricataTlsEnrichment
{
    private long _lastSuccessTicks;
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonElement> _observations = new();

    // Keep only correlation keys and supported handshake fields, never full alert records.
    private JsonElement[] Remember(JsonElement rows)
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var undated = new List<JsonElement>();
        lock (_gate)
        {
            foreach (var row in rows.ValueKind == JsonValueKind.Array ? rows.EnumerateArray().AsEnumerable() : Enumerable.Empty<JsonElement>())
            {
                var tls = Property(row, "tls");
                var quic = Property(row, "quic");
                if (tls.ValueKind != JsonValueKind.Object && quic.ValueKind != JsonValueKind.Object) continue;
                var detail = new { sni = Read(tls,"sni") ?? Read(quic,"sni"), version = Read(tls,"version"),
                    cipher = Read(tls,"cipher"), alpn = Read(tls,"alpn") ?? Read(quic,"alpn"),
                    ja3 = new { hash = Read(Property(tls,"ja3"),"hash") ?? Read(Property(quic,"ja3"),"hash") },
                    ja3s = new { hash = Read(Property(tls,"ja3s"),"hash") }, ja4 = Read(tls,"ja4") ?? Read(quic,"ja4"),
                    subject = Read(tls,"subject"), issuerdn = Read(tls,"issuerdn") };
                if (new[] {detail.sni,detail.version,detail.cipher,detail.alpn,detail.ja3.hash,detail.ja3s.hash,
                    detail.ja4,detail.subject,detail.issuerdn}.All(string.IsNullOrWhiteSpace)) continue;
                var observation = JsonSerializer.SerializeToElement(new { timestamp = Read(row,"timestamp"),
                    src_ip = Read(row,"src_ip"), dest_ip = Read(row,"dest_ip"), src_port = ReadPort(row,"src_port"),
                    dest_port = ReadPort(row,"dest_port"), proto = Read(row,"proto"), flow_id = Property(row,"flow_id").ToString(), tls = detail });
                if (ReadTime(observation) is null) undated.Add(observation);
                else _observations[observation.GetRawText()] = observation;
            }
            foreach (var key in _observations.Where(x => ReadTime(x.Value) < cutoff).Select(x => x.Key).ToArray())
                _observations.Remove(key);
            foreach (var key in _observations.Keys.Take(Math.Max(0, _observations.Count - 5000)).ToArray())
                _observations.Remove(key);
            return _observations.Values.Concat(undated).ToArray();
        }
    }

    internal static bool Eligible(TrafficEvent flow) => flow.Source == "ntopng-flow" && flow.DeviceId.HasValue
        && flow.StartedUtc.HasValue && Connection.Create(flow.SourceIp, flow.SourcePort, flow.DestinationIp, flow.DestinationPort, flow.Protocol).HasValue;

    public async Task<CoverageReport> EnrichAsync(
        IReadOnlyList<TrafficEvent> flows, IOpnsenseClient opnsense, int toleranceSeconds, DateTime lowerBound,
        IgnoredDomainStore ignoredDomains, EvidenceDomainPolicy domainPolicy, CancellationToken cancellationToken)
    {
        var report = new CoverageReport();
        var previous = Interlocked.Read(ref _lastSuccessTicks);
        if (previous > 0) report.LastSuccessUtc = new DateTime(previous, DateTimeKind.Utc);
        foreach (var flow in flows)
            if (new[] {flow.TlsServerName, flow.TlsVersion, flow.TlsCipher, flow.TlsAlpn, flow.TlsClientFingerprint,
                flow.TlsServerFingerprint, flow.CertificateSubject, flow.CertificateIssuer}.Any(x => !string.IsNullOrWhiteSpace(x))) report.Native.Add(flow.Id);
        var candidates = flows.Where(Eligible)
            .ToLookup(x => Connection.Create(x.SourceIp, x.SourcePort, x.DestinationIp, x.DestinationPort, x.Protocol)!.Value);
        JsonElement response = default;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { response = await opnsense.GetIdsAlertsAsync(500, null, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { /* Use retained observations. */ }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { /* Use retained observations. */ }

        var rows = response.ValueKind == JsonValueKind.Array ? response : Property(response, "rows");
        if (rows.ValueKind == JsonValueKind.Array)
        {
            report.Available = true;
            report.LastSuccessUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _lastSuccessTicks, report.LastSuccessUtc.Value.Ticks);
        }
        var tolerance = TimeSpan.FromSeconds(Math.Clamp(toleranceSeconds, 0, 30));
        var byPair = candidates.SelectMany(group => group.Select(flow => (group.Key, Flow: flow)))
            .ToLookup(x => (x.Key.Source, x.Key.Destination, x.Key.Transport), x => x.Flow);
        var partial = flows.Where(x => x.Source == "ntopng-flow" && !Eligible(x))
            .ToLookup(x => (x.SourceIp, x.DestinationIp));
        var alerts = Remember(rows)
            .Select(row => (Row: row, Time: ReadTime(row)))
            .OrderByDescending(x => x.Time);
        foreach (var (row, time) in alerts)
        {
            var observedUtc = time.GetValueOrDefault();
            var tls = Property(row, "tls");
            var quic = Property(row, "quic");
            if (tls.ValueKind != JsonValueKind.Object && quic.ValueKind != JsonValueKind.Object) continue;
            var key = Connection.Create(Read(row, "src_ip"), ReadPort(row, "src_port"),
                Read(row, "dest_ip"), ReadPort(row, "dest_port"), Read(row, "proto"));
            if (Read(tls,"sni") is { } host && (ignoredDomains.IsIgnored(host) || domainPolicy.IsTrusted(host))) continue;
            if (key is not { } connection)
            {
                if (IPAddress.TryParse(Read(row,"src_ip"), out var src) && IPAddress.TryParse(Read(row,"dest_ip"), out var dst))
                {
                    var transport = Read(row,"proto")?.ToUpperInvariant() ?? "";
                    foreach (var candidate in byPair[(src,dst,transport)].Concat(byPair[(dst,src,transport)]).Where(InWindow))
                        report.Failures.TryAdd(candidate.Id, "Alert has incomplete connection identifiers.");
                }
                continue;
            }

            var sni = TelemetryNaming.NormalizeHostname(Read(tls, "sni") ?? Read(quic, "sni"));
            var exact = candidates[connection].Concat(candidates[connection.Reverse()]).Distinct().ToArray();
            bool InWindow(TrafficEvent x) => time.HasValue && observedUtc >= x.StartedUtc - tolerance
                && observedUtc - (x.LastSeenUtc ?? x.TimestampUtc) < tolerance + TimeSpan.FromSeconds(1);
            var matches = exact.Where(InWindow).ToArray();
            report.Attempts++;
            if (matches.Length != 1 || observedUtc <= lowerBound + tolerance + TimeSpan.FromSeconds(1))
            {
                foreach (var candidate in matches.Length == 0 ? exact : matches)
                    report.Failures.TryAdd(candidate.Id, matches.Length > 1 ? "Multiple connections match the alert."
                        : !time.HasValue ? "Alert timestamp missing." : matches.Length == 0 ? "Tuple observed outside the connection time window." : "Candidate history truncated at the query boundary.");
                if (exact.Length == 0)
                    foreach (var candidate in byPair[(connection.Source,connection.Destination,connection.Transport)]
                        .Concat(byPair[(connection.Destination,connection.Source,connection.Transport)]).Where(InWindow)
                        .Where(x => x.SourcePort == connection.SourcePort || x.SourcePort == connection.DestinationPort))
                        report.Failures.TryAdd(candidate.Id, "Endpoint pair and time overlap, but ports differ.");
                continue;
            }
            var flow = matches[0];
            if (partial[(flow.SourceIp,flow.DestinationIp)].Concat(partial[(flow.DestinationIp,flow.SourceIp)])
                .Any(other => InWindow(other) && (!other.SourcePort.HasValue || other.SourcePort == flow.SourcePort)
                    && (!other.DestinationPort.HasValue || other.DestinationPort == flow.DestinationPort)
                    && string.Equals(other.Protocol?.Split('/')[0], flow.Protocol?.Split('/')[0], StringComparison.OrdinalIgnoreCase)))
            {
                report.Failures.TryAdd(flow.Id, "Overlapping flow with incomplete identifiers.");
                continue;
            }
            report.Matches++;
            var version = Read(tls, "version");
            var cipher = Read(tls, "cipher");
            var alpn = Read(tls, "alpn") ?? Read(quic, "alpn");
            var client = Read(Property(tls, "ja3"), "hash") ?? Read(Property(quic, "ja3"), "hash")
                ?? Read(tls, "ja4") ?? Read(quic, "ja4");
            var server = Read(Property(tls, "ja3s"), "hash");
            var subject = Read(tls, "subject");
            var issuer = Read(tls, "issuerdn");
            flow.TlsServerName = Fill(flow.TlsServerName, sni);
            flow.TlsVersion = Fill(flow.TlsVersion, version);
            flow.TlsCipher = Fill(flow.TlsCipher, cipher);
            flow.TlsAlpn = Fill(flow.TlsAlpn, alpn);
            flow.TlsClientFingerprint = Fill(flow.TlsClientFingerprint, client);
            flow.TlsServerFingerprint = Fill(flow.TlsServerFingerprint, server);
            flow.CertificateSubject = Fill(flow.CertificateSubject, subject);
            flow.CertificateIssuer = Fill(flow.CertificateIssuer, issuer);

            string? Fill(string? existing, string? observed)
            {
                if (!string.IsNullOrWhiteSpace(existing) || observed is null) return existing;
                report.Filled.Add(flow.Id);
                return observed;
            }
        }

        return report;
    }

    private static JsonElement Property(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) ? property : default;

    private static string? Read(JsonElement value, string key)
    {
        var property = Property(value, key);
        var text = property.ValueKind == JsonValueKind.String ? property.GetString()?.Trim() : null;
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static int? ReadPort(JsonElement row, string key)
    {
        var port = Property(row, key);
        if (port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out var number)) return number;
        return int.TryParse(Read(row, key), NumberStyles.None, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static DateTime? ReadTime(JsonElement row)
    {
        var text = Read(row, "timestamp");
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.UtcDateTime : null;
    }

    private readonly record struct Connection(IPAddress Source, int SourcePort, IPAddress Destination, int DestinationPort, string Transport)
    {
        public Connection Reverse() => new(Destination, DestinationPort, Source, SourcePort, Transport);

        public static Connection? Create(string? source, int? sourcePort, string? destination, int? destinationPort, string? protocol)
        {
            var transport = protocol?.Split('/', 2)[0].Trim().ToUpperInvariant() switch
            {
                "TCP" or "6" => "TCP",
                "UDP" or "17" => "UDP",
                _ => null
            };
            if (transport is null || sourcePort is not (>= 1 and <= 65535) || destinationPort is not (>= 1 and <= 65535)
                || !IPAddress.TryParse(source, out var from) || !IPAddress.TryParse(destination, out var to))
                return null;
            return new(Normalize(from), sourcePort.Value, Normalize(to), destinationPort.Value, transport);
        }

        private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}
