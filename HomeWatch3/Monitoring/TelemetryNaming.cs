using System.Net;

namespace HomeWatch3.Monitoring;

public static class TelemetryNaming
{
    private static readonly HashSet<string> GenericApplications = new(StringComparer.OrdinalIgnoreCase)
    {
        "TCP", "UDP", "TLS", "TLSV1.2", "TLSV1.3", "SSL", "QUIC", "HTTP", "HTTP/2", "HTTP/3",
        "HTTPS", "Unknown", "Unrated", "Generic"
    };

    private static readonly (string Service, string Category, string[] Tokens)[] KnownServices =
    [
        ("YouTube", "Streaming Media", ["youtube", "googlevideo", "youtu.be", "ytimg"]),
        ("Netflix", "Streaming Media", ["netflix", "nflxvideo", "nflximg", "nflxso"]),
        ("TikTok", "Social Media", ["tiktok", "tiktokcdn", "byteoversea"]),
        ("Instagram", "Social Media", ["instagram", "cdninstagram"]),
        ("Facebook", "Social Media", ["facebook", "fbcdn"]),
        ("Reddit", "Social Media", ["reddit", "redd.it", "redditmedia", "redditstatic"]),
        ("X / Twitter", "Social Media", ["twitter", "twimg", "x.com"]),
        ("Spotify", "Streaming Media", ["spotify", "scdn"]),
        ("Twitch", "Streaming Media", ["twitch", "ttvnw"]),
        ("Prime Video", "Streaming Media", ["primevideo", "amazonvideo", "aiv-cdn"]),
        ("Disney+", "Streaming Media", ["disneyplus", "dssott", "bamgrid"]),
        ("WhatsApp", "Messaging", ["whatsapp", "whatsapp.net"]),
        ("Microsoft 365", "Productivity", ["microsoftonline", "office365", "outlook", "hotmail"]),
        ("Apple", "Technology", ["apple", "icloud", "mzstatic"]),
        ("Amazon", "Shopping", ["amazon", "amazonaws"]),
        ("eBay", "Shopping", ["ebay", "ebayimg", "ebaystatic"]),
        ("Pornhub", "Adult", ["pornhub"]),
        ("XVideos", "Adult", ["xvideos"]),
        ("XNXX", "Adult", ["xnxx"]),
        ("YouPorn", "Adult", ["youporn"]),
        ("RedTube", "Adult", ["redtube"]),
        ("Tube8", "Adult", ["tube8"])
    ];

    private static readonly string[] BackgroundTokens =
    [
        "connectivitycheck", "captive.apple.com", "push.apple.com", "gstatic.com", "msftconnecttest",
        "windowsupdate", "time.apple.com", "pool.ntp.org", "clients3.google.com", "ocsp.", "crl."
    ];

    public static string? NormalizeHostname(string? candidate, string? remoteIp = null)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var value = candidate.Trim().TrimEnd('.');
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)) value = uri.Host;
        value = value.Trim().Trim('[', ']').TrimStart('*', '.').TrimEnd('.');
        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.Count(x => x == ':') == 1 && int.TryParse(value[(colon + 1)..], out _)) value = value[..colon];
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals(remoteIp, StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(value, out _)
            || value.Contains(' ')
            || value.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return null;
        return value.ToLowerInvariant();
    }

    public static string? NormalizeApplication(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(['.', '/', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var specific = parts.LastOrDefault(x => !GenericApplications.Contains(x));
        if (string.IsNullOrWhiteSpace(specific)) return null;
        return specific.Replace('_', ' ').Trim();
    }

    public static bool IsGenericApplication(string? value) =>
        string.IsNullOrWhiteSpace(value) || GenericApplications.Contains(value.Trim());

    public static string Service(string? hostname, string? application)
    {
        var haystack = $"{hostname} {application}";
        foreach (var known in KnownServices)
        {
            if (known.Tokens.Any(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase))) return known.Service;
        }
        return NormalizeApplication(application) ?? NormalizeHostname(hostname) ?? "Unknown service";
    }

    public static string Category(string? hostname, string? application)
    {
        var haystack = $"{hostname} {application}";
        foreach (var known in KnownServices)
        {
            if (known.Tokens.Any(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase))) return known.Category;
        }
        return NormalizeApplication(application) is null ? "Web" : "Application";
    }

    public static bool IsEncrypted(string? layer4, string? layer7, int remotePort)
    {
        var protocol = $"{layer4}/{layer7}";
        return remotePort == 443
            || protocol.Contains("TLS", StringComparison.OrdinalIgnoreCase)
            || protocol.Contains("SSL", StringComparison.OrdinalIgnoreCase)
            || protocol.Contains("QUIC", StringComparison.OrdinalIgnoreCase)
            || protocol.Contains("HTTPS", StringComparison.OrdinalIgnoreCase)
            || protocol.Contains("HTTP/3", StringComparison.OrdinalIgnoreCase);
    }

    public static string Visibility(string? exactUrl, string? hostname, string? application, string? remoteIp)
    {
        if (!string.IsNullOrWhiteSpace(exactUrl)) return "exact-url";
        if (!string.IsNullOrWhiteSpace(NormalizeHostname(hostname, remoteIp))) return "hostname";
        if (!string.IsNullOrWhiteSpace(NormalizeApplication(application))) return "application";
        return "ip";
    }

    public static int VisibilityRank(string? visibility) => visibility?.ToLowerInvariant() switch
    {
        "exact-url" => 4,
        "hostname" => 3,
        "application" => 2,
        _ => 1
    };

    public static bool IsBackground(string? hostname, string? application)
    {
        var haystack = $"{hostname} {application}";
        return BackgroundTokens.Any(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
