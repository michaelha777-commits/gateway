using System.Text.Json;
using HomeWatch3.Data;

namespace HomeWatch3.Monitoring;

public static class InfrastructureDeviceClassifier
{
    private static readonly string[] StrongNameTokens =
    [
        "deco", "access point", "access-point", "wireless ap", "wifi ap",
        "router", "gateway", "firewall", "opnsense", "pfsense",
        "unifi", "omada", "eero", "orbi", "mesh node", "mesh-node",
        "switch", "managed switch"
    ];

    private static readonly object Gate = new();
    private static Dictionary<long,bool>? _overrides;
    private static string OverridePath => Path.Combine(Directory.GetCurrentDirectory(), "data", "infrastructure-device-overrides.json");

    public static bool IsInfrastructure(Device? device)
    {
        if (device is null) return false;
        var manual = GetOverride(device.Id);
        if (manual.HasValue) return manual.Value;
        return IsInfrastructure(device.Name, device.Vendor);
    }

    public static bool IsInfrastructure(string? name, string? vendor)
    {
        var n = (name ?? string.Empty).Trim().ToLowerInvariant();
        var v = (vendor ?? string.Empty).Trim().ToLowerInvariant();
        if (StrongNameTokens.Any(t => n.Contains(t, StringComparison.OrdinalIgnoreCase))) return true;

        if ((v.Contains("ubiquiti") || v.Contains("aruba") || v.Contains("netgear") || v.Contains("cisco")) &&
            (n.Contains("ap") || n.Contains("router") || n.Contains("switch") || n.Contains("mesh") || n.Contains("gateway"))) return true;

        if (v.Contains("tp-link") && (n.Contains("deco") || n.Contains("omada") || n.Contains("router") || n.Contains("access") || n.Contains("mesh"))) return true;
        return false;
    }

    public static bool? GetOverride(long deviceId)
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _overrides!.TryGetValue(deviceId, out var value) ? value : null;
        }
    }

    public static void SetOverride(long deviceId, bool? infrastructure)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (infrastructure.HasValue) _overrides![deviceId] = infrastructure.Value;
            else _overrides!.Remove(deviceId);
            Save();
        }
    }

    private static void EnsureLoaded()
    {
        if (_overrides is not null) return;
        try
        {
            if (File.Exists(OverridePath))
                _overrides = JsonSerializer.Deserialize<Dictionary<long,bool>>(File.ReadAllText(OverridePath)) ?? new();
            else _overrides = new();
        }
        catch { _overrides = new(); }
    }

    private static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OverridePath)!);
        File.WriteAllText(OverridePath, JsonSerializer.Serialize(_overrides, new JsonSerializerOptions { WriteIndented = true }));
    }
}
