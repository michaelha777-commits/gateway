using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HomeWatch3.Connectors.Ntopng;

public sealed class NtopngOptions
{
    public const string SectionName = "Ntopng";
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://192.168.1.1:3000";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int InterfaceId { get; set; }
    public bool AllowInvalidCertificate { get; set; }
    public bool EnableFlowTelemetry { get; set; } = true;
    public int FlowPollSeconds { get; set; } = 10;
    public int FlowPageSize { get; set; } = 500;
}

public interface INtopngClient
{
    Task<NtopngHealth> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<NtopngDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<NtopngFlowSnapshot> GetActiveFlowsAsync(CancellationToken cancellationToken = default);
    Task<NtopngLiveTlsProbe> ProbeLiveTlsAsync(IReadOnlyList<NtopngActiveFlow> flows, CancellationToken cancellationToken = default);
    Task<NtopngDeviceSnapshot> GetDeviceAsync(
        string? ipAddress,
        string? macAddress,
        CancellationToken cancellationToken = default);
}

public sealed record NtopngHealth(
    bool Configured,
    bool Reachable,
    int? StatusCode,
    int InterfaceId,
    string? InterfaceName,
    string? Error);

public sealed record NtopngFlowEndpoint(
    string? IpAddress,
    string? Name,
    int Port,
    string? Country);

public sealed record NtopngActiveFlow(
    string Key,
    string HashId,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    NtopngFlowEndpoint Client,
    NtopngFlowEndpoint Server,
    string? Layer4Protocol,
    string? Application,
    int DurationSeconds,
    long Bytes,
    int ClientToServerPercent,
    long ThroughputBitsPerSecond,
    int RiskScore,
    string? TlsServerName = null,
    string? TlsVersion = null,
    string? TlsCipher = null,
    string? TlsAlpn = null,
    string? TlsClientFingerprint = null,
    string? TlsServerFingerprint = null,
    string? CertificateSubject = null,
    string? CertificateIssuer = null,
    string? ObservedUrl = null);

public sealed record NtopngFlowSnapshot(
    bool Configured,
    bool Available,
    string? Error,
    int InterfaceId,
    int TotalRows,
    DateTime ObservedUtc,
    IReadOnlyList<NtopngActiveFlow> Flows,
    NtopngTlsDiagnostics? TlsDiagnostics = null);

// Aggregated schema information only: no hosts, IPs, domains, TLS values or credentials.
public sealed record NtopngTlsDiagnostics(
    int SampledFlows,
    int ParsedVersions,
    int ParsedFingerprints,
    IReadOnlyDictionary<string, int> SourceFieldCounts,
    IReadOnlyDictionary<string, int>? StructuralFieldCounts = null);

public sealed record NtopngLiveTlsProbe(int Attempted, int Successful, int WithTlsMetadata, int WithTlsVersions, int WithFingerprints, IReadOnlyDictionary<string, int> TlsFieldNames, IReadOnlyDictionary<string, int> ResponseStructure, string? Error);

public sealed record NtopngApplication(
    string Name,
    string DisplayName,
    string Kind,
    long BytesSent,
    long BytesReceived,
    long TotalBytes,
    int DurationSeconds,
    int FlowCount,
    string? Breed,
    string? Category,
    double Percentage);

public sealed record NtopngCategory(
    string Name,
    long BytesSent,
    long BytesReceived,
    long TotalBytes,
    int DurationSeconds,
    double Percentage);

public sealed record NtopngDashboardSnapshot(
    bool Configured,
    bool Available,
    string? Error,
    int InterfaceId,
    string? InterfaceName,
    int ActiveHosts,
    int LocalHosts,
    int AlertCount,
    int RiskScore,
    long BytesSent,
    long BytesReceived,
    long TotalBytes,
    IReadOnlyList<NtopngApplication> Applications);

public sealed record NtopngDeviceSnapshot(
    bool Configured,
    bool Available,
    string? Error,
    string? IpAddress,
    int InterfaceId,
    string? InterfaceName,
    string? Name,
    string? MacAddress,
    string? OperatingSystem,
    string? DeviceType,
    DateTime? FirstSeenUtc,
    DateTime? LastSeenUtc,
    int ActiveFlows,
    int AlertCount,
    int RiskScore,
    long BytesSent,
    long BytesReceived,
    long TotalBytes,
    IReadOnlyList<NtopngApplication> Applications,
    IReadOnlyList<NtopngCategory> Categories);

public sealed class NtopngClient(HttpClient httpClient, IOptions<NtopngOptions> options) : INtopngClient
{
    private readonly NtopngOptions _options = options.Value;

    public async Task<NtopngHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
        {
            return new(false, false, null, _options.InterfaceId, null,
                "ntopng is disabled or its username/password are not configured.");
        }

        try
        {
            using var response = await SendAsync("/lua/rest/v2/get/ntopng/interfaces.lua", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new(true, false, (int)response.StatusCode, _options.InterfaceId, null,
                    HttpError(response.StatusCode));
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!TryGetSuccessfulPayload(root, out var payload, out var error))
            {
                return new(true, false, (int)response.StatusCode, _options.InterfaceId, null, error);
            }

            string? interfaceName = null;
            if (payload.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in payload.EnumerateArray())
                {
                    if (ReadInt(item, "ifid") != _options.InterfaceId) continue;
                    interfaceName = ReadText(item, "ifname");
                    break;
                }
            }

            return new(true, true, (int)response.StatusCode, _options.InterfaceId, interfaceName, null);
        }
        catch (Exception ex)
        {
            return new(true, false, null, _options.InterfaceId, null, ex.Message);
        }
    }

    public async Task<NtopngDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured()) return EmptyDashboard(false, "ntopng is not configured in HomeWatch.");

        try
        {
            var health = await GetHealthAsync(cancellationToken);
            if (!health.Reachable) return EmptyDashboard(true, health.Error, health.InterfaceName);

            var interfacePath = $"/lua/rest/v2/get/interface/data.lua?ifid={_options.InterfaceId}";
            var applicationsPath = $"/lua/rest/v2/get/interface/l7/stats.lua?ifid={_options.InterfaceId}&ndpistats_mode=sinceStartup&breed=true&ndpi_category=true&all_values=true&max_values=12&collapse_stats=false";
            var interfaceTask = ReadPayloadAsync(interfacePath, cancellationToken);
            var applicationsTask = ReadPayloadAsync(applicationsPath, cancellationToken);
            await Task.WhenAll(interfaceTask, applicationsTask);

            var interfaceResult = await interfaceTask;
            var applicationsResult = await applicationsTask;
            var interfaceData = interfaceResult.Payload;
            var rawApplications = new List<RawApplication>();
            if (applicationsResult.Payload.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            {
                CollectApplications(applicationsResult.Payload, rawApplications);
            }

            var applications = NormalizeApplications(rawApplications).Take(12).ToArray();
            var sent = ReadLongAny(interfaceData, "bytes.sent", "bytes_sent", "sent_bytes", "bytes_upload", "local2remote");
            var received = ReadLongAny(interfaceData, "bytes.rcvd", "bytes.received", "bytes_received", "rcvd_bytes", "bytes_download", "remote2local");
            var total = Math.Max(ReadLongAny(interfaceData, "bytes", "total_bytes"), sent + received);
            if (total == 0) total = applications.Sum(x => x.TotalBytes);

            var error = interfaceResult.Error ?? applicationsResult.Error;
            return new(
                true,
                interfaceResult.Success || applicationsResult.Success,
                error,
                _options.InterfaceId,
                health.InterfaceName,
                ReadIntAny(interfaceData, "num_hosts", "hosts", "active_hosts"),
                ReadIntAny(interfaceData, "num_local_hosts", "local_hosts"),
                ReadIntAny(interfaceData, "num_alerts", "alerts", "total_alerts", "engaged_alerts"),
                ReadIntAny(interfaceData, "score", "risk_score"),
                sent,
                received,
                total,
                applications);
        }
        catch (Exception ex)
        {
            return EmptyDashboard(true, ex.Message);
        }
    }

    public async Task<NtopngFlowSnapshot> GetActiveFlowsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured()) return EmptyFlows(false, "ntopng is not configured in HomeWatch.");
        if (!_options.EnableFlowTelemetry) return EmptyFlows(true, "ntopng flow telemetry is disabled in HomeWatch.");

        try
        {
            var pageSize = Math.Clamp(_options.FlowPageSize, 50, 1000);
            var path = $"/lua/rest/v2/get/flow/active.lua?ifid={_options.InterfaceId}&currentPage=1&perPage={pageSize}&sortColumn=last_seen&sortOrder=desc&verbose=true";
            var result = await ReadPayloadAsync(path, cancellationToken);
            if (!result.Success) return EmptyFlows(true, result.Error);

            var payload = result.Payload;
            var totalRows = ReadInt(payload, "totalRows");
            var flows = new List<NtopngActiveFlow>();
            var sourceFields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var structuralFields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var sampledFlows = 0;
            if (payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("data", out var rows)
                && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
                    if (sampledFlows++ < 100)
                    {
                        CollectTlsFieldNames(row, sourceFields);
                        CollectStructuralFieldNames(row, structuralFields);
                    }
                    var client = ReadFlowEndpoint(row, "client");
                    var server = ReadFlowEndpoint(row, "server");
                    var protocol = ReadObject(row, "protocol");
                    var breakdown = ReadObject(row, "breakdown");
                    var throughput = ReadObject(row, "thpt");
                    var firstSeen = ReadEpoch(row, "first_seen") ?? DateTime.UtcNow;
                    var lastSeen = ReadEpoch(row, "last_seen") ?? firstSeen;
                    var clientToServer = breakdown.ValueKind == JsonValueKind.Object
                        ? Math.Clamp(ReadInt(breakdown, "cli2srv"), 0, 100)
                        : 50;

                    flows.Add(new NtopngActiveFlow(
                        ReadText(row, "key") ?? string.Empty,
                        ReadText(row, "hash_id") ?? string.Empty,
                        firstSeen,
                        lastSeen,
                        client,
                        server,
                        ReadText(protocol, "l4"),
                        ReadText(protocol, "l7"),
                        Math.Max(0, ReadInt(row, "duration")),
                        Math.Max(0, ReadLong(row, "bytes")),
                        clientToServer,
                        Math.Max(0, ReadLong(throughput, "bps")),
                        Math.Max(0, ReadInt(row, "score")),
                        ReadTlsText(row, protocol, "tls.server_name", "ssl.server_name", "server_name", "sni"),
                        ReadTlsText(row, protocol, "tls.version", "ssl.version", "tls_version"),
                        ReadTlsText(row, protocol, "tls.cipher", "ssl.cipher", "tls_cipher"),
                        ReadTlsText(row, protocol, "tls.alpn", "ssl.alpn", "alpn"),
                        ReadTlsText(row, protocol, "tls.ja4", "ssl.ja4", "ja4", "client.ja4", "cli_ja4", "client_ja4", "tls.ja3", "ssl.ja3", "client.ja3", "cli_ja3", "client_fingerprint"),
                        ReadTlsText(row, protocol, "tls.ja4s", "ssl.ja4s", "ja4s", "server.ja4", "srv_ja4", "server_ja4", "tls.ja3s", "ssl.ja3s", "server.ja3", "srv_ja3", "server_fingerprint"),
                        ReadTlsText(row, protocol, "tls.certificate.subject", "ssl.certificate.subject", "certificate_subject"),
                        ReadTlsText(row, protocol, "tls.certificate.issuer", "ssl.certificate.issuer", "certificate_issuer"),
                        HomeWatch3.Monitoring.TlsEvidenceAnalyzer.RedactObservedUrl(ReadTextAny(row, "http.url", "http_url", "HTTP_URL"))));
                }
            }

            if (totalRows == 0) totalRows = flows.Count;
            var diagnostic = new NtopngTlsDiagnostics(
                Math.Min(sampledFlows, 100),
                flows.Count(x => !string.IsNullOrWhiteSpace(x.TlsVersion)),
                flows.Count(x => !string.IsNullOrWhiteSpace(x.TlsClientFingerprint) || !string.IsNullOrWhiteSpace(x.TlsServerFingerprint)),
                sourceFields,
                structuralFields);
            return new(true, true, null, _options.InterfaceId, totalRows, DateTime.UtcNow, flows, diagnostic);
        }
        catch (Exception ex)
        {
            return EmptyFlows(true, ex.Message);
        }
    }

    // On-demand read-only probe. Never returns raw flow data, IPs, domains or TLS values.
    public async Task<NtopngLiveTlsProbe> ProbeLiveTlsAsync(
        IReadOnlyList<NtopngActiveFlow> flows, CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var structure = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var attempted = 0;
        var successful = 0;
        var withTls = 0;
        var versions = 0;
        var fingerprints = 0;
        string? error = null;
        if (!IsConfigured()) return new(0, 0, 0, 0, 0, fields, structure, "ntopng is not configured.");
        foreach (var flow in flows.Where(x => x.Server.Port is 443 or 8443 or 853 or 993 or 995 || 
            string.Equals(x.Application, "TLS", StringComparison.OrdinalIgnoreCase)).Take(5))
        {
            var id = long.TryParse(flow.Key, out var key) && key > 0
                ? "flow_key=" + key
                : long.TryParse(flow.HashId, out var hash) && hash > 0
                    ? "flow_hash_id=" + hash
                    : null;
            if (id is null) continue;
            attempted++;
            try
            {
                var path = $"/lua/rest/v2/get/flow/live/overview.lua?ifid={_options.InterfaceId}&{id}";
                // Distinguish a missing endpoint, login redirect, HTML response, and invalid JSON.
                // Never inspect or expose response bodies, URLs, credentials, or flow values.
                using var response = await SendAsync(path, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    error = $"Detailed-flow endpoint returned HTTP {(int)response.StatusCode}.";
                    continue;
                }
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is not null &&
                    !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    error = mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                        ? "Detailed-flow endpoint returned HTML (possibly a login redirect or unsupported route)."
                        : "Detailed-flow endpoint returned non-JSON content.";
                    continue;
                }
                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                if (!TryGetSuccessfulPayload(document.RootElement, out var payload, out var apiError))
                {
                    error = apiError is null ? "Detailed-flow API rejected the request." :
                        apiError.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase)
                            ? "Detailed-flow API did not find the requested flow."
                            : "Detailed-flow API rejected the request.";
                    continue;
                }
                var raw = ReadObject(payload, "flow");
                if (raw.ValueKind != JsonValueKind.Object)
                {
                    error = "Live-flow endpoint returned no flow object.";
                    continue;
                }
                successful++;
                var foundTls = false;
                var foundVersion = false;
                var foundFingerprint = false;
                foreach (var property in raw.EnumerateObject())
                {
                    var name = property.Name;
                    // Only allowlisted schema names, never arbitrary field values.
                    if (name is "protos.tls.client_requested_server_name" or
                        "protos.tls.ja3" or "protos.tls.ja3s" or
                        "protos.tls.ja4" or "protos.tls.ja4s" or
                        "protos.tls.version" or "protos.tls.cipher" or
                        "protos.tls.alpn" or "protos.tls.cert_subject" or
                        "protos.tls.cert_issuer" ||
                        name.StartsWith("protos.tls.", StringComparison.OrdinalIgnoreCase) &&
                        new[] { "version", "ja3", "ja4", "cipher", "alpn", "cert" }
                            .Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Report category instead of untrusted raw key text.
                        var category = name.Contains("ja3", StringComparison.OrdinalIgnoreCase) ||
                                       name.Contains("ja4", StringComparison.OrdinalIgnoreCase) ? "fingerprint" :
                                       name.Contains("version", StringComparison.OrdinalIgnoreCase) ? "version" :
                                       name.Contains("cipher", StringComparison.OrdinalIgnoreCase) ? "cipher" :
                                       name.Contains("alpn", StringComparison.OrdinalIgnoreCase) ? "alpn" :
                                       name.Contains("cert", StringComparison.OrdinalIgnoreCase) ? "certificate" : "sni";
                        fields[category] = fields.GetValueOrDefault(category) + 1;
                        foundTls = true;
                        if (category == "version") foundVersion = true;
                        if (category == "fingerprint") foundFingerprint = true;
                    }
                }
                if (foundTls) withTls++;
                if (foundVersion) versions++;
                if (foundFingerprint) fingerprints++;
                foreach (var name in new[] { "flow", "ext", "now" })
                    if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out _))
                        structure[name] = structure.GetValueOrDefault(name) + 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (JsonException) { error = "Detailed-flow endpoint returned invalid JSON (possibly an HTML login page)."; }
            catch (HttpRequestException) { error = "ntopng detailed-flow network request failed."; }
            catch (Exception) { error = "Detailed-flow response could not be processed."; }
        }
        if (attempted == 0) error = "No eligible active TLS flow with a numeric key was found. Generate HTTPS traffic and try again.";
        return new(attempted, successful, withTls, versions, fingerprints, fields, structure, error);
    }

    public async Task<NtopngDeviceSnapshot> GetDeviceAsync(
        string? ipAddress,
        string? macAddress,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured()) return Empty(false, ipAddress, "ntopng is not configured in HomeWatch.");
        if (string.IsNullOrWhiteSpace(ipAddress) && string.IsNullOrWhiteSpace(macAddress))
        {
            return Empty(true, ipAddress, "This HomeWatch device does not have an IP or MAC address.");
        }

        try
        {
            var health = await GetHealthAsync(cancellationToken);
            if (!health.Reachable) return Empty(true, ipAddress, health.Error, health.InterfaceName);

            var resolvedIpAddress = ipAddress;
            HostLookupResult lookup;
            if (string.IsNullOrWhiteSpace(resolvedIpAddress))
            {
                resolvedIpAddress = await FindActiveIpByMacAsync(macAddress, cancellationToken);
                lookup = string.IsNullOrWhiteSpace(resolvedIpAddress)
                    ? HostLookupResult.NotFoundResult()
                    : await ReadHostAsync(resolvedIpAddress, cancellationToken);
            }
            else
            {
                lookup = await ReadHostAsync(resolvedIpAddress, cancellationToken);
            }

            if (lookup.NotFound && !string.IsNullOrWhiteSpace(macAddress))
            {
                var activeIpAddress = await FindActiveIpByMacAsync(macAddress, cancellationToken);
                if (!string.IsNullOrWhiteSpace(activeIpAddress)
                    && !string.Equals(activeIpAddress, resolvedIpAddress, StringComparison.OrdinalIgnoreCase))
                {
                    resolvedIpAddress = activeIpAddress;
                    lookup = await ReadHostAsync(resolvedIpAddress, cancellationToken);
                }
            }

            if (!lookup.Found)
            {
                var error = lookup.NotFound
                    ? "ntopng no longer has this device in live memory. Wake or use it, then refresh."
                    : lookup.Error;
                return Empty(true, resolvedIpAddress ?? ipAddress, error, health.InterfaceName);
            }

            var host = lookup.Host;
            var applications = ReadApplications(host);
            if (applications.Count == 0)
            {
                applications = await ReadL7FallbackAsync(resolvedIpAddress!, cancellationToken);
            }
            var categories = ReadCategories(host);
            var bytesSent = ReadLong(host, "bytes.sent");
            var bytesReceived = ReadLong(host, "bytes.rcvd");
            var totalBytes = Math.Max(ReadLong(host, "bytes"), bytesSent + bytesReceived);
            if (totalBytes == 0) totalBytes = applications.Sum(x => x.TotalBytes);

            var name = FirstText(
                ReadText(host, "name"),
                FirstArrayText(host, "names"));
            var operatingSystem = FirstText(
                ReadText(host, "os_detail"),
                ReadNonNumericText(host, "os"));
            var deviceType = FirstText(
                ReadText(host, "device_type_name"),
                ReadText(host, "device_type"),
                ReadNonNumericText(host, "devtype"));

            return new(
                true,
                true,
                null,
                resolvedIpAddress,
                _options.InterfaceId,
                health.InterfaceName,
                name,
                ReadText(host, "mac"),
                operatingSystem,
                deviceType,
                ReadEpoch(host, "seen.first"),
                ReadEpoch(host, "seen.last"),
                ReadInt(host, "active_flows.as_client") + ReadInt(host, "active_flows.as_server"),
                Math.Max(ReadInt(host, "num_alerts"), ReadInt(host, "total_alerts")),
                ReadInt(host, "score"),
                bytesSent,
                bytesReceived,
                totalBytes,
                applications,
                categories);
        }
        catch (Exception ex)
        {
            return Empty(true, ipAddress, ex.Message);
        }
    }

    private async Task<HostLookupResult> ReadHostAsync(string ipAddress, CancellationToken cancellationToken)
    {
        var path = $"/lua/rest/v2/get/host/data.lua?ifid={_options.InterfaceId}&host={Uri.EscapeDataString(ipAddress)}";
        using var response = await SendAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return HostLookupResult.NotFoundResult();
        if (!response.IsSuccessStatusCode) return HostLookupResult.ErrorResult(HttpError(response.StatusCode));

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!TryGetSuccessfulPayload(document.RootElement, out var host, out var error))
        {
            return error?.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase) == true
                ? HostLookupResult.NotFoundResult()
                : HostLookupResult.ErrorResult(FriendlyApiError(error));
        }
        return host.ValueKind == JsonValueKind.Object
            ? HostLookupResult.Success(host.Clone())
            : HostLookupResult.ErrorResult("ntopng did not return host data for this address.");
    }

    private async Task<string?> FindActiveIpByMacAsync(string? macAddress, CancellationToken cancellationToken)
    {
        var normalizedMacAddress = NormalizeMac(macAddress);
        if (normalizedMacAddress is null) return null;

        var path = $"/lua/rest/v2/get/host/active.lua?ifid={_options.InterfaceId}&all=true&mac={Uri.EscapeDataString(macAddress!)}";
        using var response = await SendAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!TryGetSuccessfulPayload(document.RootElement, out var payload, out _)
            || payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("data", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (!string.Equals(NormalizeMac(ReadText(row, "mac")), normalizedMacAddress, StringComparison.Ordinal)) continue;
            var activeIpAddress = ReadText(row, "ip");
            if (!string.IsNullOrWhiteSpace(activeIpAddress)) return activeIpAddress;
        }
        return null;
    }

    private async Task<List<NtopngApplication>> ReadL7FallbackAsync(string ipAddress, CancellationToken cancellationToken)
    {
        var path = $"/lua/rest/v2/get/host/l7/stats.lua?ifid={_options.InterfaceId}&host={Uri.EscapeDataString(ipAddress)}&breed=true&ndpi_category=true&collapse_stats=false";
        using var response = await SendAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode) return [];
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        if (!TryGetSuccessfulPayload(document.RootElement, out var rows, out _) || rows.ValueKind != JsonValueKind.Array) return [];

        var raw = new List<RawApplication>();
        foreach (var row in rows.EnumerateArray())
        {
            var name = ReadText(row, "label") ?? ReadText(row, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var total = ReadLong(row, "value");
            raw.Add(new(name, 0, 0, total, ReadInt(row, "duration"), ReadInt(row, "num_flows"),
                ReadText(row, "breed"), ReadText(row, "category")));
        }
        return NormalizeApplications(raw);
    }

    private async Task<PayloadResult> ReadPayloadAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode) return new(false, default, HttpError(response.StatusCode));
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return TryGetSuccessfulPayload(document.RootElement, out var payload, out var error)
            ? new(true, payload.Clone(), null)
            : new(false, default, FriendlyApiError(error));
    }

    private static void CollectApplications(JsonElement element, List<RawApplication> rows, string? suggestedName = null)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) CollectApplications(item, rows);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        var name = FirstText(ReadText(element, "label"), ReadText(element, "name"), ReadText(element, "protocol"), suggestedName);
        var sent = ReadLongAny(element, "bytes.sent", "bytes_sent", "sent");
        var received = ReadLongAny(element, "bytes.rcvd", "bytes.received", "bytes_received", "received");
        var total = Math.Max(ReadLongAny(element, "value", "bytes", "total_bytes"), sent + received);
        if (!string.IsNullOrWhiteSpace(name) && total > 0)
        {
            rows.Add(new(name, sent, received, total,
                ReadIntAny(element, "duration", "duration_seconds"),
                ReadIntAny(element, "num_flows", "flows"),
                ReadText(element, "breed"),
                FirstText(ReadText(element, "category"), ReadText(element, "ndpi_category"))));
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("metadata")) continue;
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var number) && number > 0)
            {
                rows.Add(new(property.Name, 0, 0, number, 0, 0, null, null));
            }
            else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                CollectApplications(property.Value, rows, property.Name);
            }
        }
    }

    private static List<NtopngApplication> ReadApplications(JsonElement host)
    {
        if (!host.TryGetProperty("ndpi", out var ndpi) || ndpi.ValueKind != JsonValueKind.Object) return [];
        var raw = new List<RawApplication>();
        foreach (var application in ndpi.EnumerateObject())
        {
            if (application.Value.ValueKind != JsonValueKind.Object) continue;
            var sent = ReadLong(application.Value, "bytes.sent");
            var received = ReadLong(application.Value, "bytes.rcvd");
            var total = Math.Max(ReadLong(application.Value, "bytes"), sent + received);
            raw.Add(new(application.Name, sent, received, total,
                ReadInt(application.Value, "duration"), ReadInt(application.Value, "num_flows"),
                ReadText(application.Value, "breed"), ReadText(application.Value, "category")));
        }
        return NormalizeApplications(raw);
    }

    private static List<NtopngApplication> NormalizeApplications(IEnumerable<RawApplication> source)
    {
        var rows = source.Where(x => x.TotalBytes > 0 && !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RawApplication(
                group.First().Name,
                group.Sum(x => x.BytesSent),
                group.Sum(x => x.BytesReceived),
                group.Sum(x => x.TotalBytes),
                group.Max(x => x.DurationSeconds),
                group.Sum(x => x.FlowCount),
                group.Select(x => x.Breed).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                group.Select(x => x.Category).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))))
            .OrderByDescending(x => x.TotalBytes)
            .ToList();
        var grandTotal = rows.Sum(x => x.TotalBytes);
        return rows.Select(x => new NtopngApplication(
            x.Name,
            DisplayName(x.Name),
            ApplicationKind(x.Name),
            x.BytesSent,
            x.BytesReceived,
            x.TotalBytes,
            x.DurationSeconds,
            x.FlowCount,
            x.Breed,
            x.Category,
            grandTotal == 0 ? 0 : Math.Round(x.TotalBytes * 100d / grandTotal, 1)))
            .ToList();
    }

    private static List<NtopngCategory> ReadCategories(JsonElement host)
    {
        if (!host.TryGetProperty("ndpi_categories", out var categories) || categories.ValueKind != JsonValueKind.Object) return [];
        var raw = new List<(string Name, long Sent, long Received, long Total, int Duration)>();
        foreach (var category in categories.EnumerateObject())
        {
            if (category.Value.ValueKind != JsonValueKind.Object) continue;
            var sent = ReadLong(category.Value, "bytes.sent");
            var received = ReadLong(category.Value, "bytes.rcvd");
            raw.Add((category.Name, sent, received,
                Math.Max(ReadLong(category.Value, "bytes"), sent + received),
                ReadInt(category.Value, "duration")));
        }

        raw = raw.Where(x => x.Total > 0).OrderByDescending(x => x.Total).ToList();
        var grandTotal = raw.Sum(x => x.Total);
        return raw.Select(x => new NtopngCategory(x.Name, x.Sent, x.Received, x.Total, x.Duration,
            grandTotal == 0 ? 0 : Math.Round(x.Total * 100d / grandTotal, 1))).ToList();
    }

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}")));
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private bool IsConfigured() => _options.Enabled
        && !string.IsNullOrWhiteSpace(_options.BaseUrl)
        && !string.IsNullOrWhiteSpace(_options.Username)
        && !string.IsNullOrWhiteSpace(_options.Password);

    private NtopngDeviceSnapshot Empty(bool configured, string? ipAddress, string? error, string? interfaceName = null) =>
        new(configured, false, error, ipAddress, _options.InterfaceId, interfaceName, null, null, null, null,
            null, null, 0, 0, 0, 0, 0, 0, [], []);

    private NtopngDashboardSnapshot EmptyDashboard(bool configured, string? error, string? interfaceName = null) =>
        new(configured, false, error, _options.InterfaceId, interfaceName, 0, 0, 0, 0, 0, 0, 0, []);

    private NtopngFlowSnapshot EmptyFlows(bool configured, string? error) =>
        new(configured, false, error, _options.InterfaceId, 0, DateTime.UtcNow, []);

    private static NtopngFlowEndpoint ReadFlowEndpoint(JsonElement row, string propertyName)
    {
        var endpoint = ReadObject(row, propertyName);
        return endpoint.ValueKind == JsonValueKind.Object
            ? new NtopngFlowEndpoint(
                ReadText(endpoint, "ip"),
                ReadText(endpoint, "name"),
                Math.Max(0, ReadInt(endpoint, "port")),
                ReadText(endpoint, "country"))
            : new NtopngFlowEndpoint(null, null, 0, null);
    }

    private static JsonElement ReadObject(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static bool TryGetSuccessfulPayload(JsonElement root, out JsonElement payload, out string? error)
    {
        payload = default;
        error = null;
        var rc = ReadInt(root, "rc");
        if (rc != 0)
        {
            error = $"ntopng API returned {ReadText(root, "rc_str") ?? $"error {rc}"}.";
            return false;
        }
        if (!root.TryGetProperty("rsp", out payload))
        {
            error = "ntopng response did not include an rsp payload.";
            return false;
        }
        return true;
    }

    private static string FriendlyApiError(string? error) =>
        error?.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase) == true
            ? "ntopng does not currently have this host in memory. Wake the device and generate traffic, then refresh."
            : error ?? "ntopng did not return host data.";

    private static string? NormalizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Where(Uri.IsHexDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized.Length == 12 ? normalized : null;
    }

    private static string HttpError(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            "ntopng rejected the configured username/password or the account lacks API access.",
        HttpStatusCode.NotFound => "The configured ntopng REST endpoint was not found.",
        _ => $"ntopng returned HTTP {(int)statusCode} ({statusCode})."
    };

    private static string DisplayName(string name) => name.ToUpperInvariant() switch
    {
        "QUIC" => "Encrypted QUIC traffic",
        "TLS" or "TLSV1.2" or "TLSV1.3" => "Encrypted TLS traffic",
        _ => name
    };

    private static string ApplicationKind(string name) => name.ToUpperInvariant() switch
    {
        "QUIC" or "TLS" or "TLSV1.2" or "TLSV1.3" or "HTTP" or "HTTP/2" or "HTTP/3" or "TCP" or "UDP" => "Transport",
        "DNS" or "DHCP" or "NTP" or "ICMP" or "MDNS" or "SSDP" or "SMB" or "SSH" => "Network service",
        _ => "Application"
    };

    private static string? FirstText(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x != "0");

    private static string? FirstArrayText(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())) return item.GetString();
            if (item.ValueKind == JsonValueKind.Object)
            {
                var name = ReadText(item, "name");
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        return null;
    }

    private static string? ReadNonNumericText(JsonElement element, string propertyName)
    {
        var value = ReadText(element, propertyName);
        return value is not null && !double.TryParse(value, out _) ? value : null;
    }

    private static string? ReadText(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    // ntopng versions may expose TLS metadata on the flow or its protocol object.
    private static void CollectStructuralFieldNames(JsonElement flow, Dictionary<string, int> counts)
    {
        // Allowlisted schema names only. Never expose values, hostnames, IPs or arbitrary JSON keys.
        foreach (var property in flow.EnumerateObject())
        {
            if (new[] { "protocol", "client", "server", "metadata", "moreinfo", "additional_info", "info", "tls", "ssl", "ndpi" }
                .Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                Count(property.Name);
            if (property.Value.ValueKind != JsonValueKind.Object ||
                !new[] { "protocol", "metadata", "moreinfo", "additional_info", "info", "ndpi" }
                    .Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var child in property.Value.EnumerateObject())
            {
                if (new[] { "l4", "l7", "name", "id", "tls", "ssl", "http", "ja3", "ja4", "sni", "moreinfo", "metadata", "protocol" }
                    .Contains(child.Name, StringComparer.OrdinalIgnoreCase))
                    Count(property.Name + "." + child.Name);
            }
        }
        void Count(string name) => counts[name] = counts.GetValueOrDefault(name) + 1;
    }

    private static void CollectTlsFieldNames(JsonElement flow, Dictionary<string, int> counts)
    {
        // Inspect only schema names. Never include field values or arbitrary nested payloads.
        foreach (var property in flow.EnumerateObject())
        {
            var key = property.Name;
            if (IsTlsFieldName(key)) Count(key);
            if (property.Value.ValueKind != JsonValueKind.Object ||
                !new[] { "protocol", "tls", "ssl" }.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            foreach (var nested in property.Value.EnumerateObject())
            {
                if (IsTlsFieldName(nested.Name)) Count(key + "." + nested.Name);
                if (nested.Value.ValueKind != JsonValueKind.Object ||
                    !new[] { "tls", "ssl" }.Contains(nested.Name, StringComparer.OrdinalIgnoreCase)) continue;
                foreach (var leaf in nested.Value.EnumerateObject())
                    if (IsTlsFieldName(leaf.Name)) Count(key + "." + nested.Name + "." + leaf.Name);
            }
        }
        void Count(string key) { if (counts.Count < 80 || counts.ContainsKey(key)) counts[key] = counts.GetValueOrDefault(key) + 1; }
    }

    private static bool IsTlsFieldName(string name) =>
        new[] { "tls", "ssl", "ja3", "ja4", "sni", "alpn", "cipher", "cert", "fingerprint", "server_name", "version" }
            .Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string? ReadTlsText(JsonElement flow, JsonElement protocol, params string[] paths) =>
        ReadTextAny(flow, paths) ?? ReadTextAny(protocol, paths);

    private static string? ReadTextAny(JsonElement element, params string[] propertyPaths)
    {
        foreach (var path in propertyPaths)
        {
            var direct = ReadText(element, path);
            if (!string.IsNullOrWhiteSpace(direct)) return direct;

            var current = element;
            var found = true;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    found = false;
                    break;
                }
            }
            if (!found) continue;
            if (current.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(current.GetString())) return current.GetString();
            if (current.ValueKind == JsonValueKind.Number) return current.GetRawText();
        }
        return null;
    }

    private static long ReadLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer)) return integer;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return (long)Math.Max(0, number);
        return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out integer) ? integer : 0;
    }

    private static long ReadLongAny(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        foreach (var propertyName in propertyNames)
        {
            var value = ReadLong(element, propertyName);
            if (value != 0) return value;
        }
        return 0;
    }

    private static int ReadIntAny(JsonElement element, params string[] propertyNames)
    {
        var value = ReadLongAny(element, propertyNames);
        return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        var value = ReadLong(element, propertyName);
        return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }

    private static DateTime? ReadEpoch(JsonElement element, string propertyName)
    {
        var seconds = ReadLong(element, propertyName);
        if (seconds <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private sealed record RawApplication(
        string Name,
        long BytesSent,
        long BytesReceived,
        long TotalBytes,
        int DurationSeconds,
        int FlowCount,
        string? Breed,
        string? Category);

    private sealed record PayloadResult(bool Success, JsonElement Payload, string? Error);

    private sealed record HostLookupResult(bool Found, bool NotFound, JsonElement Host, string? Error)
    {
        public static HostLookupResult Success(JsonElement host) => new(true, false, host, null);
        public static HostLookupResult NotFoundResult() => new(false, true, default, null);
        public static HostLookupResult ErrorResult(string? error) => new(false, false, default, error);
    }
}
