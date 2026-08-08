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
}

public sealed record OpnsenseHealth(bool Reachable, int? StatusCode, string? Error);

public sealed class OpnsenseClient(HttpClient httpClient, IOptions<OpnsenseOptions> options) : IOpnsenseClient
{
    private readonly OpnsenseOptions _options = options.Value;

    public async Task<OpnsenseHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (!HasCredentials())
            return new(false, null, "OPNsense API credentials are not configured.");

        try
        {
            using var response = await SendAuthenticatedGetAsync("/api/core/system/status", cancellationToken);
            return new(response.IsSuccessStatusCode, (int)response.StatusCode,
                response.IsSuccessStatusCode ? null : $"OPNsense returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new(false, null, ex.Message);
        }
    }

    public async Task<JsonElement> GetDnsmasqLeasesAsync(CancellationToken cancellationToken = default)
    {
        if (!HasCredentials())
            throw new InvalidOperationException("OPNsense API credentials are not configured.");

        using var response = await SendAuthenticatedGetAsync("/api/dnsmasq/leases/search", cancellationToken);
        return await ParseJsonResponseAsync(response, "dnsmasq leases", cancellationToken);
    }

    public async Task<JsonElement> GetUnboundQueriesAsync(CancellationToken cancellationToken = default)
    {
        if (!HasCredentials())
            throw new InvalidOperationException("OPNsense API credentials are not configured.");

        // OPNsense's Unbound reporting endpoint returns the most recent query rows.
        // An empty JSON search request is sufficient for the default newest-first page.
        using var response = await SendAuthenticatedPostAsync(
            "/api/unbound/overview/search_queries",
            "{}",
            cancellationToken);
        return await ParseJsonResponseAsync(response, "Unbound query reporting", cancellationToken);
    }

    private bool HasCredentials() =>
        !string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.ApiSecret);

    private async Task<HttpResponseMessage> SendAuthenticatedGetAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Get, path);
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthenticatedPostAsync(
        string path,
        string json,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthenticatedRequest(HttpMethod.Post, path);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private HttpRequestMessage CreateAuthenticatedRequest(HttpMethod method, string path)
    {
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ApiKey}:{_options.ApiSecret}"));
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<JsonElement> ParseJsonResponseAsync(
        HttpResponseMessage response,
        string source,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"OPNsense {source} API returned HTTP {(int)response.StatusCode}: {body}",
                null,
                response.StatusCode);

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return document.RootElement.Clone();
    }
}
