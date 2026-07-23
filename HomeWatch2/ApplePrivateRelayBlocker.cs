using System.Net;
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
        "mask-h2.icloud.com",
        "mask-api.icloud.com",
        "mask.apple-dns.net",
        "mask-api.fe2.apple-dns.net",
        "apple-native-relay.mask.apple-dns.net",
        "north-america-mask.wrr.me.apple-dns.net"
    };

    private static readonly string[] Rules = Domains.Select(domain => $"||{domain}^").ToArray();

    public static bool IsPrivateRelayDomain(string domain)
    {
        var value = (domain ?? "").Trim().Trim('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (Domains.Any(blocked =>
                value == blocked ||
                value.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Apple can introduce regional or service-specific relay hostnames.
        // Keep detection narrow: the hostname must be under Apple's relay-related
        // namespaces and contain a mask label/prefix.
        if (value.EndsWith(".icloud.com", StringComparison.OrdinalIgnoreCase))
        {
            var firstLabel = value.Split('.', 2)[0];
            return firstLabel.Equals("mask", StringComparison.OrdinalIgnoreCase) ||
                   firstLabel.StartsWith("mask-", StringComparison.OrdinalIgnoreCase);
        }

        if (value.EndsWith(".apple-dns.net", StringComparison.OrdinalIgnoreCase))
        {
            return value.Split('.').Any(label =>
                label.Equals("mask", StringComparison.OrdinalIgnoreCase) ||
                label.StartsWith("mask-", StringComparison.OrdinalIgnoreCase) ||
                label.EndsWith("-mask", StringComparison.OrdinalIgnoreCase));
        }

        return false;
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

        using var jsonResponse = await SendRulesAsync(
            client,
            baseUrl,
            options,
            new StringContent(JsonSerializer.Serialize(new { rules = currentRules }), Encoding.UTF8, "application/json"),
            cancellationToken);

        if (jsonResponse.StatusCode == HttpStatusCode.UnsupportedMediaType)
        {
            logger.LogInformation("AdGuard Home rejected JSON rules; retrying with its legacy text format.");
            using var legacyResponse = await SendRulesAsync(
                client,
                baseUrl,
                options,
                new StringContent(string.Join("\n", currentRules) + "\n", Encoding.UTF8, "text/plain"),
                cancellationToken);
            legacyResponse.EnsureSuccessStatusCode();
        }
        else
        {
            jsonResponse.EnsureSuccessStatusCode();
        }

        logger.LogInformation("Apple Private Relay is blocked in AdGuard Home: {Domains}", string.Join(", ", Domains));
    }

    private static async Task<HttpResponseMessage> SendRulesAsync(
        HttpClient client,
        string baseUrl,
        AdGuardOptions options,
        HttpContent content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/control/filtering/set_rules")
        {
            Content = content
        };
        AddAuthentication(request, options);
        return await client.SendAsync(request, cancellationToken);
    }

    private static void AddAuthentication(HttpRequestMessage request, AdGuardOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Username)) return;
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }
}
