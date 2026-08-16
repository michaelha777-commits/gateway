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
    string? CertificateIssuer = null);

public sealed record NtopngFlowSnapshot(
    bool Configured,
    bool Available,
    string? Error,
    int InterfaceId,
    int TotalRows,
    DateTime ObservedUtc,
    IReadOnlyList<NtopngActiveFlow> Flows);

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
            if (payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("data", out var rows)
                && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Object) continue;
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
                        ReadTextAny(row, "tls.server_name", "ssl.server_name", "server_name", "sni"),
                        ReadTextAny(row, "tls.version", "ssl.version", "tls_version"),
                        ReadTextAny(row, "tls.cipher", "ssl.cipher", "tls_cipher"),
                        ReadTextAny(row, "tls.alpn", "ssl.alpn", "alpn"),
                        ReadTextAny(row, "tls.ja3", "ssl.ja3", "client.ja3", "cli_ja3", "client_fingerprint"),
                        ReadTextAny(row, "tls.ja3s", "ssl.ja3s", "server.ja3", "srv_ja3", "server_fingerprint"),
                        ReadTextAny(row, "tls.certificate.subject", "ssl.certificate.subject", "certificate_subject"),
                        ReadTextAny(row, "tls.certificate.issuer", "ssl.certificate.issuer", "certificate_issuer")));
                }
            }

            if (totalRows == 0) totalRows = flows.Count;
            return new(true, true, null, _options.InterfaceId, totalRows, DateTime.UtcNow, flows);
        }
        catch (Exception ex)
        {
            return EmptyFlows(true, ex.Message);
        }
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
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

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
        if (!element.TryGetProperty(propertyName, out var value)) return 0;
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
