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

    public static bool IsInfrastructure(Device? device)
    {
        if (device is null) return false;
        return IsInfrastructure(device.Name, device.Vendor);
    }

    public static bool IsInfrastructure(string? name, string? vendor)
    {
        var n = (name ?? string.Empty).Trim().ToLowerInvariant();
        var v = (vendor ?? string.Empty).Trim().ToLowerInvariant();
        if (StrongNameTokens.Any(t => n.Contains(t, StringComparison.OrdinalIgnoreCase))) return true;

        // Vendor is only used when the name also looks network-oriented so ordinary
        // TP-Link smart-home devices are not accidentally hidden.
        if ((v.Contains("ubiquiti") || v.Contains("aruba") || v.Contains("netgear") || v.Contains("cisco")) &&
            (n.Contains("ap") || n.Contains("router") || n.Contains("switch") || n.Contains("mesh") || n.Contains("gateway"))) return true;

        if (v.Contains("tp-link") && (n.Contains("deco") || n.Contains("omada") || n.Contains("router") || n.Contains("access") || n.Contains("mesh"))) return true;

        return false;
    }
}
