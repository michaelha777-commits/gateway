using System.Globalization;
using System.Net;
using System.Text.Json;
using HomeWatch3.Connectors.Opnsense;
using HomeWatch3.Data;

namespace HomeWatch3.Monitoring;

internal static class SuricataTlsEnrichment
{
    public static async Task<HashSet<long>> EnrichAsync(
        IReadOnlyList<TrafficEvent> flows, IOpnsenseClient opnsense,
        IgnoredDomainStore ignoredDomains, EvidenceDomainPolicy domainPolicy, CancellationToken cancellationToken)
    {
        var candidates = flows
            .Where(x => x.Source == "ntopng-flow" && x.DeviceId.HasValue && x.StartedUtc.HasValue)
            .Select(x => (Flow: x, Key: Connection.Create(
                x.SourceIp, x.SourcePort, x.DestinationIp, x.DestinationPort, x.Protocol)))
            .Where(x => x.Key.HasValue)
            .ToLookup(x => x.Key!.Value, x => x.Flow);
        if (candidates.Count == 0) return [];

        JsonElement response;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { response = await opnsense.GetIdsAlertsAsync(500, null, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return []; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException) { return []; }

        return Apply(candidates, response, ignoredDomains, domainPolicy);
    }

    private static HashSet<long> Apply(ILookup<Connection, TrafficEvent> candidates, JsonElement response,
        IgnoredDomainStore ignoredDomains, EvidenceDomainPolicy domainPolicy)
    {
        var rows = response.ValueKind == JsonValueKind.Array ? response : Property(response, "rows");
        if (rows.ValueKind != JsonValueKind.Array) return [];

        HashSet<long> enriched = [];
        var alerts = rows.EnumerateArray()
            .Select(row => (Row: row, Time: ReadTime(row)))
            .Where(x => x.Time.HasValue)
            .OrderByDescending(x => x.Time);
        foreach (var (row, time) in alerts)
        {
            var observedUtc = time.GetValueOrDefault();
            var tls = Property(row, "tls");
            var quic = Property(row, "quic");
            if (tls.ValueKind != JsonValueKind.Object && quic.ValueKind != JsonValueKind.Object) continue;
            var key = Connection.Create(Read(row, "src_ip"), ReadPort(row, "src_port"),
                Read(row, "dest_ip"), ReadPort(row, "dest_port"), Read(row, "proto"));
            if (key is not { } connection) continue;

            // ntopng represents the flow from the monitored device's perspective.
            // Its timestamps may have second precision, so include the final observed second.
            var matches = candidates[connection].Concat(candidates[connection.Reverse()]).Distinct()
                .Where(x => observedUtc >= x.StartedUtc && observedUtc - (x.LastSeenUtc ?? x.TimestampUtc) < TimeSpan.FromSeconds(1))
                .Take(2).ToArray();
            if (matches.Length != 1) continue;
            var flow = matches[0];

            var sni = TelemetryNaming.NormalizeHostname(Read(tls, "sni") ?? Read(quic, "sni"), flow.DestinationIp);
            if (sni is not null && (ignoredDomains.IsIgnored(sni) || domainPolicy.IsTrusted(sni))) continue;
            var version = Read(tls, "version") ?? Read(quic, "version");
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
                enriched.Add(flow.Id);
                return observed;
            }
        }

        return enriched;
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
