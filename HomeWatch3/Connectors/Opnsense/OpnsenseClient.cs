using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HomeWatch3.Connectors.Opnsense;

public sealed class OpnsenseOptions
{
    public const string SectionName = "Opnsense";
    public string BaseUrl { get; set; } = "https://192.168.1.1";
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public bool AllowInvalidCertificate { get; set; }
}

public interface IOpnsenseClient
{
    Task<OpnsenseHealth> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetDnsmasqLeasesAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetUnboundQueriesAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetFirewallStatesAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetFirewallLogAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetArpAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetNdpAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetInterfaceStatisticsAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetRoutesAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetGatewayStatusAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetSystemResourcesAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> GetTrafficTopAsync(string interfaces, CancellationToken cancellationToken = default);
}

public sealed record OpnsenseHealth(bool Reachable, int? StatusCode, string? Error);

public sealed class OpnsenseClient(HttpClient httpClient, IOptions<OpnsenseOptions> options) : IOpnsenseClient
{
    private readonly OpnsenseOptions _options = options.Value;

    public async Task<OpnsenseHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!HasCredentials()) return new(false, null, "OPNsense API credentials are not configured.");
        try
        {
            using var response = await SendAuthenticatedGetAsync("/api/core/system/status", cancellationToken);
            return new(response.IsSuccessStatusCode, (int)response.StatusCode,
                response.IsSuccessStatusCode ? null : $"OPNsense returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) { return new(false, null, ex.Message); }
    }

    public Task<JsonElement> GetDnsmasqLeasesAsync(CancellationToken ct = default) =>
        GetJsonAsync("/api/dnsmasq/leases/search", "dnsmasq leases", ct);

    public async Task<JsonElement> GetUnboundQueriesAsync(CancellationToken ct = default)
    {
        EnsureCredentials();
        using var response = await SendAuthenticatedPostAsync("/api/unbound/overview/search_queries", "{}", ct);
        return await ParseJsonResponseAsync(response, "Unbound query reporting", ct);
    }

    public Task<JsonElement> GetFirewallStatesAsync(CancellationToken ct = default) =>
        GetJsonAsync("/api/diagnostics/firewall/pf_states", "firewall states", ct);

    public Task<JsonElement> GetFirewallLogAsync(CancellationToken ct = default) =>
        GetJsonAsync("/api/diagnostics/firewall/log", "firewall log", ct);

    public Task<JsonElement> GetArpAsync(CancellationToken ct = default) =>
        GetJsonAsync("/api/diagnostics/interface/get_arp", "ARP table", ct);

    public Task<JsonElement> GetNdpAsync(CancellationToken ct = default) =>
        GetJsonAsync("/api/diagnostics/interface/get_ndp", "NDP table", ct);

    public Task<JsonElement> GetInterfaceStatisticsAsync(CancellationToken ct = default) =>
        GetJsonAsync("/api/diagnostics/interface/get_interface_statistics", "interface statistics", ct);

    public Task<JsonElement> GetRoutesAsync(CancellationToken ct = default) =>
        GetJsonAsync("/api/diagnostics/interface/get_routes", "routes", ct);

    public Task<JsonElement> GetGatewayStatusAsync(CancellationToken ct = default) =>
        GetJsonAsync("/api/routes/gateway/status", "gateway status", ct);

    public Task<JsonElement> GetSystemResourcesAsync(CancellationToken ct = default) =>
        GetJsonAsync("/api/diagnostics/system/system_resources", "system resources", ct);

    public Task<JsonElement> GetTrafficTopAsync(string interfaces, CancellationToken ct = default)
    {
        var safe = Uri.EscapeDataString(interfaces ?? string.Empty);
        return GetJsonAsync($"/api/diagnostics/traffic/_top/{safe}", "traffic top", ct);
    }

    private async Task<JsonElement> GetJsonAsync(string path, string source, CancellationToken ct)
    {
        EnsureCredentials();
        using var response = await SendAuthenticatedGetAsync(path, ct);
        return await ParseJsonResponseAsync(response, source, ct);
    }

    private bool HasCredentials() =>
        !string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.ApiSecret);

    private void EnsureCredentials()
    {
        if (!HasCredentials()) throw new InvalidOperationException("OPNsense API credentials are not configured.");
    }

    private async Task<HttpResponseMessage> SendAuthenticatedGetAsync(string path, CancellationToken ct)
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, path);
        return await httpClient.SendAsync(request, ct);
    }

    private async Task<HttpResponseMessage> SendAuthenticatedPostAsync(string path, string json, CancellationToken ct)
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, path);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await httpClient.SendAsync(request, ct);
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string path)
    {
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ApiKey}:{_options.ApiSecret}"));
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<JsonElement> ParseJsonResponseAsync(HttpResponseMessage response, string source, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OPNsense {source} API returned HTTP {(int)response.StatusCode}: {body}", null, response.StatusCode);
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return document.RootElement.Clone();
    }
}
