using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeWatch3.Data;
using HomeWatch3.Notifications;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

public sealed class AdGuardHistoryMonitor(IConfiguration config, IHttpClientFactory clients,
    IServiceScopeFactory scopes, INtfyService ntfy, ILogger<AdGuardHistoryMonitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("AdGuard:Enabled", false)) return;
        var unavailable = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(config.GetValue("AdGuard:PollSeconds", 30), 5, 3600)));
        do
        {
            try { await ImportAsync(ct); unavailable = false; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("AdGuard query log unavailable ({Type})", ex.GetType().Name);
                if (!unavailable) await ntfy.SendEventAsync("collector-failure", "AdGuard collection failed", "Review AdGuard query-log access in HomeWatch.", ct);
                unavailable = true;
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task ImportAsync(CancellationToken ct)
    {
        var url = new Uri((config["AdGuard:BaseUrl"] ?? "").TrimEnd('/') + "/control/querylog?limit=500");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var username = config["AdGuard:Username"];
        if (!string.IsNullOrWhiteSpace(username))
        {
            if (url.Scheme != "https") throw new InvalidOperationException("AdGuard credentials require HTTPS.");
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + config["AdGuard:Password"]));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
        using var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.MaxResponseContentBufferSize = 8 * 1024 * 1024;
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
        var ignoredDevices = scope.ServiceProvider.GetRequiredService<IgnoredDeviceStore>();
        var ignoredDomains = scope.ServiceProvider.GetRequiredService<IgnoredDomainStore>();
        var policy = scope.ServiceProvider.GetRequiredService<EvidenceDomainPolicy>();
        var observations = new Dictionary<string, TrafficEvent>();
        var inventory = await db.Devices.AsNoTracking().Where(d => d.LastIpAddress != null).ToListAsync(ct);
        var byIp = inventory.ToLookup(d => d.LastIpAddress);
        foreach (var row in json.RootElement.GetProperty("data").EnumerateArray().Take(500))
        {
            if (!row.TryGetProperty("question", out var question) || !question.TryGetProperty("name", out var host)
                || !row.TryGetProperty("client", out var ip) || !row.TryGetProperty("time", out var time)
                || !DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stamp)) continue;
            if (config.GetValue("History:EnableRawRetention",false) &&
                stamp.UtcDateTime < DateTime.UtcNow.AddDays(-Math.Clamp(config.GetValue("History:RawRetentionDays",90),1,3650))) continue;
            var domain = host.GetString()?.TrimEnd('.').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(domain) || policy.IsTrusted(domain) || ignoredDomains.IsIgnored(domain)) continue;
            var address = ip.GetString();
            var candidates = byIp[address].Where(d => d.FirstSeenUtc <= stamp.UtcDateTime && d.LastSeenUtc >= stamp.UtcDateTime.AddMinutes(-5)).Take(2).ToArray();
            var device = candidates.Length == 1 ? candidates[0] : null;
            if (ignoredDevices.IsIgnored(device?.Id)) continue;
            var type = question.TryGetProperty("type", out var qt) ? qt.GetString() : null;
            var reason = row.TryGetProperty("reason", out var action) ? action.GetString() : null;
            var blocked = reason?.StartsWith("Filtered", StringComparison.OrdinalIgnoreCase) == true && reason != "FilteredWhiteList";
            var key = "adguard:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] {time.GetString(),address,domain,type,reason})));
            observations.TryAdd(key, new TrafficEvent {ExternalId=key, TimestampUtc=stamp.UtcDateTime, StartedUtc=stamp.UtcDateTime,
                LastSeenUtc=stamp.UtcDateTime, DeviceId=device?.Id, SourceIp=address, Domain=domain, Protocol="DNS/"+type,
                Visibility="hostname", Category=blocked ? "Blocked" : "DNS", Blocked=blocked, Source="adguard-querylog"});
        }
        var keys = observations.Keys.ToArray();
        var existing = await db.TrafficEvents.Where(e => e.ExternalId != null && keys.Contains(e.ExternalId)).Select(e => e.ExternalId!).ToListAsync(ct);
        db.TrafficEvents.AddRange(observations.Where(x => !existing.Contains(x.Key)).Select(x => x.Value));
        await db.SaveChangesAsync(ct);
    }
}
