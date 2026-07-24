using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class RuntimeSettingsEndpoints
{
    private static readonly SemaphoreSlim FileLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, (DateTime Expires, object Value)> IntelligenceCache = new(StringComparer.OrdinalIgnoreCase);

    public static void MapRuntimeSettingsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/runtime-settings", async (IWebHostEnvironment env) =>
        {
            var settings = await ReadSettingsAsync(env.ContentRootPath);
            return Results.Ok(ToPublic(settings));
        });

        app.MapPut("/api/runtime-settings", async (RuntimeSettingsRequest request, IWebHostEnvironment env) =>
        {
            var existing = await ReadSettingsAsync(env.ContentRootPath);
            var settings = new RuntimeSettingsData
            {
                VirusTotalApiKey = (request.VirusTotalApiKey ?? "").Trim(),
                UrlscanApiKey = (request.UrlscanApiKey ?? "").Trim(),
                NtfyBaseUrl = NormalizeBaseUrl(request.NtfyBaseUrl),
                NtfyTopic = (request.NtfyTopic ?? "").Trim(),
                NtfyUsername = (request.NtfyUsername ?? "").Trim(),
                NtfyPassword = request.NtfyPassword ?? "",
                IgnoredExactDomains = NormalizeDomainList(existing.IgnoredExactDomains),
                IgnoredDomainFamilies = NormalizeDomainList(existing.IgnoredDomainFamilies)
            };

            await WriteSettingsAsync(env.ContentRootPath, settings);
            await UpdateAppSettingsNtfyAsync(env.ContentRootPath, settings);
            return Results.Ok(ToPublic(settings));
        });

        app.MapGet("/api/ignored-domains", async (IWebHostEnvironment env) =>
        {
            var settings = await ReadSettingsAsync(env.ContentRootPath);
            return Results.Ok(new
            {
                exact = NormalizeDomainList(settings.IgnoredExactDomains),
                families = NormalizeDomainList(settings.IgnoredDomainFamilies)
            });
        });

        app.MapPost("/api/ignored-domains", async (IgnoredDomainRequest request, IWebHostEnvironment env) =>
        {
            var domain = NormalizeDomain(request.Domain ?? "");
            if (string.IsNullOrWhiteSpace(domain)) return Results.BadRequest(new { error = "A valid domain is required." });

            var mode = string.Equals(request.Mode, "family", StringComparison.OrdinalIgnoreCase) ? "family" : "exact";
            var value = mode == "family" ? DomainFamily(domain) : domain;
            var settings = await ReadSettingsAsync(env.ContentRootPath);
            var exact = NormalizeDomainList(settings.IgnoredExactDomains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var families = NormalizeDomainList(settings.IgnoredDomainFamilies).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (mode == "family") families.Add(value); else exact.Add(value);
            settings.IgnoredExactDomains = exact.OrderBy(x => x).ToList();
            settings.IgnoredDomainFamilies = families.OrderBy(x => x).ToList();
            await WriteSettingsAsync(env.ContentRootPath, settings);
            return Results.Ok(new { value, mode, exact = settings.IgnoredExactDomains, families = settings.IgnoredDomainFamilies });
        });

        app.MapDelete("/api/ignored-domains", async (string domain, string? mode, IWebHostEnvironment env) =>
        {
            var value = NormalizeDomain(domain);
            if (string.IsNullOrWhiteSpace(value)) return Results.BadRequest(new { error = "A valid domain is required." });
            var selectedMode = string.Equals(mode, "family", StringComparison.OrdinalIgnoreCase) ? "family" : "exact";
            var settings = await ReadSettingsAsync(env.ContentRootPath);
            var exact = NormalizeDomainList(settings.IgnoredExactDomains).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var families = NormalizeDomainList(settings.IgnoredDomainFamilies).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selectedMode == "family") families.Remove(value); else exact.Remove(value);
            settings.IgnoredExactDomains = exact.OrderBy(x => x).ToList();
            settings.IgnoredDomainFamilies = families.OrderBy(x => x).ToList();
            await WriteSettingsAsync(env.ContentRootPath, settings);
            return Results.Ok(new { exact = settings.IgnoredExactDomains, families = settings.IgnoredDomainFamilies });
        });

        app.MapPost("/api/runtime-settings/test/virustotal", async (RuntimeSettingsRequest request, IHttpClientFactory factory, CancellationToken ct) =>
        {
            var key = (request.VirusTotalApiKey ?? "").Trim();
            if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest(new { ok = false, error = "Enter a VirusTotal API key." });
            var client = factory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            using var message = new HttpRequestMessage(HttpMethod.Get, "https://www.virustotal.com/api/v3/domains/example.com");
            message.Headers.Add("x-apikey", key);
            using var response = await client.SendAsync(message, ct);
            return response.IsSuccessStatusCode
                ? Results.Ok(new { ok = true, status = "Connected" })
                : Results.BadRequest(new { ok = false, error = $"VirusTotal returned {(int)response.StatusCode} {response.ReasonPhrase}." });
        });

        app.MapPost("/api/runtime-settings/test/urlscan", async (RuntimeSettingsRequest request, IHttpClientFactory factory, CancellationToken ct) =>
        {
            var key = (request.UrlscanApiKey ?? "").Trim();
            if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest(new { ok = false, error = "Enter a urlscan API key." });
            var client = factory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            using var message = new HttpRequestMessage(HttpMethod.Get, "https://urlscan.io/api/v1/search/?q=domain:example.com&size=1");
            message.Headers.Add("api-key", key);
            using var response = await client.SendAsync(message, ct);
            return response.IsSuccessStatusCode
                ? Results.Ok(new { ok = true, status = "Connected" })
                : Results.BadRequest(new { ok = false, error = $"urlscan returned {(int)response.StatusCode} {response.ReasonPhrase}." });
        });

        app.MapPost("/api/runtime-settings/test/ntfy", async (RuntimeSettingsRequest request, IHttpClientFactory factory, CancellationToken ct) =>
        {
            var baseUrl = NormalizeBaseUrl(request.NtfyBaseUrl);
            var topic = (request.NtfyTopic ?? "").Trim();
            if (string.IsNullOrWhiteSpace(topic)) return Results.BadRequest(new { ok = false, error = "Enter an ntfy topic." });
            try
            {
                var client = factory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(12);
                using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{Uri.EscapeDataString(topic)}")
                {
                    Content = new StringContent("HomeWatch notification settings are working.", Encoding.UTF8, "text/plain")
                };
                message.Headers.TryAddWithoutValidation("Title", "HomeWatch test");
                message.Headers.TryAddWithoutValidation("Priority", "3");
                message.Headers.TryAddWithoutValidation("Tags", "bell");
                AddBasicAuth(message, request.NtfyUsername, request.NtfyPassword);
                using var response = await client.SendAsync(message, ct);
                response.EnsureSuccessStatusCode();
                return Results.Ok(new { ok = true, status = "Test notification sent" });
            }
            catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
        });

        app.MapGet("/api/domain-intelligence", async (string domain, IWebHostEnvironment env, IHttpClientFactory factory, CancellationToken ct) =>
        {
            domain = NormalizeDomain(domain);
            if (string.IsNullOrWhiteSpace(domain)) return Results.BadRequest(new { error = "A valid domain is required." });
            if (IntelligenceCache.TryGetValue(domain, out var cached) && cached.Expires > DateTime.UtcNow)
                return Results.Ok(cached.Value);

            var settings = await ReadSettingsAsync(env.ContentRootPath);
            var result = await LookupDomainAsync(domain, settings, factory, ct);
            IntelligenceCache[domain] = (DateTime.UtcNow.AddHours(24), result);
            return Results.Ok(result);
        });
    }

    private static async Task<object> LookupDomainAsync(string domain, RuntimeSettingsData settings, IHttpClientFactory factory, CancellationToken ct)
    {
        object? vt = null;
        object? urlscan = null;
        var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(12);

        if (!string.IsNullOrWhiteSpace(settings.VirusTotalApiKey))
        {
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, $"https://www.virustotal.com/api/v3/domains/{Uri.EscapeDataString(domain)}");
                message.Headers.Add("x-apikey", settings.VirusTotalApiKey);
                using var response = await client.SendAsync(message, ct);
                if (response.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
                    var attributes = json.RootElement.GetProperty("data").GetProperty("attributes");
                    var stats = attributes.TryGetProperty("last_analysis_stats", out var s) ? s : default;
                    vt = new
                    {
                        available = true,
                        harmless = GetInt(stats, "harmless"), malicious = GetInt(stats, "malicious"), suspicious = GetInt(stats, "suspicious"), undetected = GetInt(stats, "undetected"),
                        reputation = attributes.TryGetProperty("reputation", out var rep) && rep.TryGetInt32(out var r) ? r : 0,
                        categories = attributes.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Object
                            ? categories.EnumerateObject().Select(x => x.Value.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(6).ToArray()
                            : Array.Empty<string>()
                    };
                }
                else vt = new { available = false, error = $"HTTP {(int)response.StatusCode}" };
            }
            catch (Exception ex) { vt = new { available = false, error = ex.Message }; }
        }

        if (!string.IsNullOrWhiteSpace(settings.UrlscanApiKey))
        {
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, $"https://urlscan.io/api/v1/search/?q=domain:{Uri.EscapeDataString(domain)}&size=1");
                message.Headers.Add("api-key", settings.UrlscanApiKey);
                using var response = await client.SendAsync(message, ct);
                if (response.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
                    var first = json.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0 ? results[0] : default;
                    var page = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("page", out var p) ? p : default;
                    var task = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("task", out var t) ? t : default;
                    urlscan = new
                    {
                        available = first.ValueKind == JsonValueKind.Object,
                        title = GetString(page, "title"), country = GetString(page, "country"), server = GetString(page, "server"), ip = GetString(page, "ip"),
                        scanUrl = GetString(task, "reportURL"), screenshotUrl = GetString(task, "screenshotURL")
                    };
                }
                else urlscan = new { available = false, error = $"HTTP {(int)response.StatusCode}" };
            }
            catch (Exception ex) { urlscan = new { available = false, error = ex.Message }; }
        }

        return new { domain, virusTotal = vt, urlscan, lookedUpAt = DateTime.UtcNow.ToString("O") };
    }

    private static int GetInt(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
    private static string? GetString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string NormalizeDomain(string value) => (value ?? "").Trim().Trim('.').ToLowerInvariant();
    private static string DomainFamily(string domain)
    {
        var parts = NormalizeDomain(domain).Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? string.Join('.', parts[^2], parts[^1]) : NormalizeDomain(domain);
    }
    private static List<string> NormalizeDomainList(IEnumerable<string>? values) => (values ?? Array.Empty<string>()).Select(NormalizeDomain).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
    private static string NormalizeBaseUrl(string? value) => string.IsNullOrWhiteSpace(value) ? "https://ntfy.sh" : value.Trim().TrimEnd('/');
    private static void AddBasicAuth(HttpRequestMessage message, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password ?? ""}"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    private static object ToPublic(RuntimeSettingsData settings) => new
    {
        virusTotalApiKey = settings.VirusTotalApiKey,
        urlscanApiKey = settings.UrlscanApiKey,
        ntfyBaseUrl = settings.NtfyBaseUrl,
        ntfyTopic = settings.NtfyTopic,
        ntfyUsername = settings.NtfyUsername,
        ntfyPassword = settings.NtfyPassword,
        virusTotalConfigured = !string.IsNullOrWhiteSpace(settings.VirusTotalApiKey),
        urlscanConfigured = !string.IsNullOrWhiteSpace(settings.UrlscanApiKey),
        ntfyConfigured = !string.IsNullOrWhiteSpace(settings.NtfyTopic)
    };

    private static string SettingsPath(string root) => Path.Combine(root, "runtime-settings.json");

    private static async Task<RuntimeSettingsData> ReadSettingsAsync(string root)
    {
        await FileLock.WaitAsync();
        try
        {
            var path = SettingsPath(root);
            if (!File.Exists(path)) return new RuntimeSettingsData();
            return JsonSerializer.Deserialize<RuntimeSettingsData>(await File.ReadAllTextAsync(path), JsonOptions) ?? new RuntimeSettingsData();
        }
        catch { return new RuntimeSettingsData(); }
        finally { FileLock.Release(); }
    }

    private static async Task WriteSettingsAsync(string root, RuntimeSettingsData settings)
    {
        await FileLock.WaitAsync();
        try { await File.WriteAllTextAsync(SettingsPath(root), JsonSerializer.Serialize(settings, JsonOptions)); }
        finally { FileLock.Release(); }
    }

    private static async Task UpdateAppSettingsNtfyAsync(string root, RuntimeSettingsData settings)
    {
        var path = Path.Combine(root, "appsettings.json");
        await FileLock.WaitAsync();
        try
        {
            JsonObject json;
            try { json = File.Exists(path) ? JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsObject() ?? new JsonObject() : new JsonObject(); }
            catch { json = new JsonObject(); }
            json["Ntfy"] = new JsonObject { ["BaseUrl"] = settings.NtfyBaseUrl, ["Topic"] = settings.NtfyTopic };
            await File.WriteAllTextAsync(path, json.ToJsonString(JsonOptions));
        }
        finally { FileLock.Release(); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}

public sealed class RuntimeSettingsRequest
{
    public string? VirusTotalApiKey { get; set; }
    public string? UrlscanApiKey { get; set; }
    public string? NtfyBaseUrl { get; set; }
    public string? NtfyTopic { get; set; }
    public string? NtfyUsername { get; set; }
    public string? NtfyPassword { get; set; }
}

public sealed class IgnoredDomainRequest
{
    public string? Domain { get; set; }
    public string? Mode { get; set; }
}

public sealed class RuntimeSettingsData
{
    public string VirusTotalApiKey { get; set; } = "";
    public string UrlscanApiKey { get; set; } = "";
    public string NtfyBaseUrl { get; set; } = "https://ntfy.sh";
    public string NtfyTopic { get; set; } = "";
    public string NtfyUsername { get; set; } = "";
    public string NtfyPassword { get; set; } = "";
    public List<string> IgnoredExactDomains { get; set; } = new();
    public List<string> IgnoredDomainFamilies { get; set; } = new();
}