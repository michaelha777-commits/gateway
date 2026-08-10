using System.Text.Json;
using HomeWatch3.Connectors.Opnsense;
using HomeWatch3.Data;
using HomeWatch3.Notifications;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

public sealed class NewDeviceMonitor(IServiceScopeFactory scopeFactory, IOpnsenseClient opnsense, INtfyService ntfy, ILogger<NewDeviceMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Poll(stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; } catch (Exception ex) { logger.LogWarning(ex, "New-device check failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch { break; }
        }
    }

    private async Task Poll(CancellationToken ct)
    {
        var payload = await opnsense.GetDnsmasqLeasesAsync(ct);
        var leases = Rows(payload).Select(Parse).Where(x => x is not null).Cast<Lease>().GroupBy(x => x.Mac).Select(g => g.OrderByDescending(Score).First()).ToArray();
        using var scope = scopeFactory.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
        var known = (await db.Devices.ToListAsync(ct)).Where(x => NormalizeMac(x.MacAddress) is not null).GroupBy(x => NormalizeMac(x.MacAddress)!).ToDictionary(x => x.Key, x => x.OrderByDescending(d => d.LastSeenUtc).First());
        foreach (var lease in leases)
        {
            known.TryGetValue(lease.Mac, out var device);
            if (device is null)
            {
                device = new Device { MacAddress = lease.Mac, LastIpAddress = lease.Ip, Name = lease.Hostname, Vendor = lease.Vendor, FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow };
                db.Devices.Add(device); await db.SaveChangesAsync(ct); known[lease.Mac] = device;
                await AddReview(db, device, "new-device", "New device on your network", $"{lease.Hostname ?? lease.Vendor ?? "Unknown device"} • {lease.Ip ?? "No IP"} • {lease.Mac}", "high", ct);
            }
            else
            {
                var oldIp = device.LastIpAddress; device.LastSeenUtc = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(lease.Hostname) && string.IsNullOrWhiteSpace(device.Name)) device.Name = lease.Hostname;
                if (!string.IsNullOrWhiteSpace(lease.Vendor)) device.Vendor = lease.Vendor;
                if (!string.IsNullOrWhiteSpace(lease.Ip) && !string.Equals(oldIp, lease.Ip, StringComparison.OrdinalIgnoreCase))
                {
                    device.LastIpAddress = lease.Ip; await db.SaveChangesAsync(ct);
                    await AddReview(db, device, "new-ip", "Known device has a new IP", $"{device.Name ?? device.MacAddress} • {oldIp ?? "No previous IP"} → {lease.Ip}", "default", ct);
                }
                else await db.SaveChangesAsync(ct);
            }
        }
    }

    private async Task AddReview(HomeWatchDb db, Device device, string type, string title, string message, string priority, CancellationToken ct)
    {
        var alert = new AlertRecord { Type = type, Severity = type == "new-device" ? "warning" : "info", DeviceId = device.Id, Title = title, Message = message };
        db.Alerts.Add(alert); await db.SaveChangesAsync(ct); var sent = await ntfy.SendAsync(title, message, priority, ct);
        alert.NotificationSent = sent; alert.NotificationSentUtc = sent ? DateTime.UtcNow : null; await db.SaveChangesAsync(ct);
    }

    private static int Score(Lease x) => (!string.IsNullOrWhiteSpace(x.Hostname) ? 10 : 0) + (x.Active ? 20 : 0) + (!string.IsNullOrWhiteSpace(x.Ip) ? 5 : 0);
    private static Lease? Parse(JsonElement row)
    {
        var mac = NormalizeMac(Pick(row, "hwaddr", "mac", "macaddress", "mac_address")); if (mac is null) return null; var state = Pick(row, "state", "status");
        return new(mac, Pick(row, "address", "ip", "ipaddress"), Clean(Pick(row, "hostname", "host", "name", "client-hostname", "client_hostname")), Clean(Pick(row, "mac_info", "manufacturer", "vendor")), state?.Equals("active", StringComparison.OrdinalIgnoreCase) == true || state?.Equals("online", StringComparison.OrdinalIgnoreCase) == true);
    }
    private static IEnumerable<JsonElement> Rows(JsonElement p) { if (p.ValueKind == JsonValueKind.Array) return p.EnumerateArray().Select(x => x.Clone()); if (p.ValueKind == JsonValueKind.Object) foreach (var x in p.EnumerateObject()) if ((x.Name.Equals("rows", StringComparison.OrdinalIgnoreCase) || x.Name.Equals("data", StringComparison.OrdinalIgnoreCase)) && x.Value.ValueKind == JsonValueKind.Array) return x.Value.EnumerateArray().Select(v => v.Clone()); return []; }
    private static string? Pick(JsonElement row, params string[] names) { foreach (var p in row.EnumerateObject()) if (names.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString(); return null; }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) || value == "*" ? null : value.Trim();
    private static string? NormalizeMac(string? value) { var hex = string.Concat((value ?? "").Where(Uri.IsHexDigit)).ToLowerInvariant(); return hex.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : null; }
    private sealed record Lease(string Mac, string? Ip, string? Hostname, string? Vendor, bool Active);
}
