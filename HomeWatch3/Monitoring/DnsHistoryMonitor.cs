using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeWatch3.Connectors.Opnsense;
using HomeWatch3.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

public sealed class DnsHistoryMonitor(
    IServiceScopeFactory scopeFactory,
    IOpnsenseClient opnsense,
    IAdultDomainClassifier classifier,
    IgnoredDeviceStore ignoredDevices,
    IgnoredDomainStore ignoredDomains,
    ILogger<DnsHistoryMonitor> logger) : BackgroundService
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _primed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        await Poll(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await Poll(stoppingToken);
    }

    private async Task Poll(CancellationToken ct)
    {
        try
        {
            var payload = await opnsense.GetUnboundQueriesAsync(ct);
            var rows = ExtractRows(payload).ToList();
            if (!_primed)
            {
                foreach (var row in rows) _seen.Add(Fingerprint(row));
                _primed = true; return;
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
            var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
            var byIp = devices.Where(x => !string.IsNullOrWhiteSpace(x.LastIpAddress))
                .GroupBy(x => x.LastIpAddress!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.OrderByDescending(d => d.LastSeenUtc).First(), StringComparer.OrdinalIgnoreCase);
            var added = 0;

            foreach (var row in rows.OrderBy(ParseTime))
            {
                if (!_seen.Add(Fingerprint(row))) continue;
                var domain = GetString(row,"domain","name","qname","query")?.Trim().Trim('.').ToLowerInvariant();
                var ip = GetString(row,"client","client_ip","source","src","ip");
                var type = GetString(row,"type","qtype","query_type") ?? "DNS";
                if (string.IsNullOrWhiteSpace(domain) || ignoredDomains.IsIgnored(domain) || IsInfrastructure(domain, ip, type)) continue;
                byIp.TryGetValue(ip ?? string.Empty, out var device);
                if (ignoredDevices.IsIgnored(device?.Id)) continue;

                var classification = classifier.Classify(domain);
                // Adult events are persisted by AdultDnsMonitor, so avoid duplicates here.
                if (classification.IsAdult) continue;

                var action = GetString(row,"action") ?? "Pass";
                var when = ParseTime(row);
                db.TrafficEvents.Add(new TrafficEvent
                {
                    TimestampUtc = when, StartedUtc = when, LastSeenUtc = when,
                    DeviceId = device?.Id, SourceIp = ip, Domain = domain, Visibility = "hostname",
                    Category = action.Equals("Block",StringComparison.OrdinalIgnoreCase) ? "Blocked" : "DNS",
                    Protocol = $"DNS/{type}", Source = "opnsense-unbound", Confidence = classification.Confidence,
                    Blocked = action.Equals("Block",StringComparison.OrdinalIgnoreCase)
                });
                added++;
            }
            if (added > 0) await db.SaveChangesAsync(ct);
            if (_seen.Count > 20000) { _seen.Clear(); _primed = false; }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex,"DNS history poll failed"); }
    }

    private static bool IsInfrastructure(string domain, string? ip, string type)
    {
        if (type.Equals("PTR", StringComparison.OrdinalIgnoreCase)) return true;
        if (domain.Equals("localhost", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (domain.EndsWith(".in-addr.arpa", StringComparison.OrdinalIgnoreCase) || domain.EndsWith(".ip6.arpa", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(ip, "localhost", StringComparison.OrdinalIgnoreCase) || ip is "127.0.0.1" or "::1") return true;
        return false;
    }

    private static IEnumerable<JsonElement> ExtractRows(JsonElement p)
    {
        if(p.ValueKind==JsonValueKind.Array)return p.EnumerateArray().Select(x=>x.Clone());
        if(p.ValueKind==JsonValueKind.Object&&p.TryGetProperty("rows",out var r)&&r.ValueKind==JsonValueKind.Array)return r.EnumerateArray().Select(x=>x.Clone());
        return [];
    }
    private static string Fingerprint(JsonElement row)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.GetRawText())));
    private static string? GetString(JsonElement row,params string[] names){foreach(var n in names)if(row.ValueKind==JsonValueKind.Object&&row.TryGetProperty(n,out var v))return v.ValueKind==JsonValueKind.String?v.GetString():v.ToString();return null;}
    private static DateTime ParseTime(JsonElement row){var s=GetString(row,"time","timestamp","created","date");if(long.TryParse(s,NumberStyles.Integer,CultureInfo.InvariantCulture,out var unix))try{return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;}catch{}return DateTime.TryParse(s,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out var dt)?DateTime.SpecifyKind(dt,DateTimeKind.Utc):DateTime.UtcNow;}
}
