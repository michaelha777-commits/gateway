using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public sealed class ApplePrivateRelayBlocker(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<ApplePrivateRelayBlocker> logger) : BackgroundService
{
    private static readonly string[] Domains =
    {
        "mask.icloud.com",
        "mask-h2.icloud.com"
    };

    private static readonly string[] Rules = Domains.Select(domain => $"||{domain}^").ToArray();

    public static bool IsPrivateRelayDomain(string domain)
    {
        var value = (domain ?? "").Trim().Trim('.').ToLowerInvariant();
        return Domains.Any(blocked => value == blocked || value.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureBlockedAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not enforce Apple Private Relay blocking in AdGuard Home");
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }
    }

    private async Task EnsureBlockedAsync(CancellationToken cancellationToken)
    {
        var options = configuration.GetSection("AdGuard").Get<AdGuardOptions>() ?? new();
        var baseUrl = options.BaseUrl.TrimEnd('/');
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/control/filtering/status");
        AddAuthentication(statusRequest, options);
        using var statusResponse = await client.SendAsync(statusRequest, cancellationToken);
        statusResponse.EnsureSuccessStatusCode();

        using var statusJson = JsonDocument.Parse(await statusResponse.Content.ReadAsStreamAsync(cancellationToken));
        var currentRules = new List<string>();
        if (statusJson.RootElement.TryGetProperty("user_rules", out var userRules) && userRules.ValueKind == JsonValueKind.Array)
        {
            currentRules.AddRange(userRules.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))!);
        }

        var changed = false;
        foreach (var rule in Rules)
        {
            if (currentRules.Contains(rule, StringComparer.OrdinalIgnoreCase)) continue;
            currentRules.Add(rule);
            changed = true;
        }
        if (!changed) return;

        var payload = JsonSerializer.Serialize(new { rules = currentRules });
        using var setRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/control/filtering/set_rules")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        AddAuthentication(setRequest, options);
        using var setResponse = await client.SendAsync(setRequest, cancellationToken);
        setResponse.EnsureSuccessStatusCode();
        logger.LogInformation("Apple Private Relay is blocked in AdGuard Home: {Domains}", string.Join(", ", Domains));
    }

    private static void AddAuthentication(HttpRequestMessage request, AdGuardOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Username)) return;
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }
}
