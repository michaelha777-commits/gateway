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
}

public sealed record OpnsenseHealth(bool Reachable, int? StatusCode, string? Error);

public sealed class OpnsenseClient(HttpClient httpClient, IOptions<OpnsenseOptions> options) : IOpnsenseClient
{
    private readonly OpnsenseOptions _options = options.Value;

    public async Task<OpnsenseHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.ApiSecret))
            return new(false, null, "OPNsense API credentials are not configured.");

        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ApiKey}:{_options.ApiSecret}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/core/system/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return new(response.IsSuccessStatusCode, (int)response.StatusCode,
                response.IsSuccessStatusCode ? null : $"OPNsense returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new(false, null, ex.Message);
        }
    }
}
