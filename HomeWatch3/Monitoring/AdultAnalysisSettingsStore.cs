using System.Text.Json;

namespace HomeWatch3.Monitoring;

public sealed record AdultAnalysisSettingsSnapshot(bool HideAdvertisingByDefault, string[] AdvertisingDomains);
public sealed record AdultAdvertisingDomainUpdate(string? Domain);
public sealed record AdultAnalysisDefaultsUpdate(bool HideAdvertisingByDefault);

public sealed class AdultAnalysisSettingsStore
{
    private static readonly string[] DefaultAdvertisingDomains =
    [
        "pemsrv.com",
        "trafficjunky.com",
        "exoclick.com",
        "exosrv.com",
        "popads.net",
        "juicyads.com",
        "doubleclick.net",
        "googlesyndication.com"
    ];

    private readonly object _gate = new();
    private readonly string _path;
    private bool _hideAdvertisingByDefault = true;
    private HashSet<string> _advertisingDomains = new(DefaultAdvertisingDomains, StringComparer.OrdinalIgnoreCase);

    public AdultAnalysisSettingsStore(IConfiguration configuration)
    {
        var dataPath = configuration["HomeWatch:DataPath"];
        if (string.IsNullOrWhiteSpace(dataPath)) dataPath = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataPath);
        _path = Path.Combine(dataPath, "adult-analysis-settings.json");
        Load();
    }

    public AdultAnalysisSettingsSnapshot GetSnapshot()
    {
        lock (_gate)
            return new(_hideAdvertisingByDefault, _advertisingDomains.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public bool IsAdvertising(string? hostname)
    {
        var value = Normalize(hostname);
        if (value is null) return false;
        lock (_gate)
            return _advertisingDomains.Any(root => value.Equals(root, StringComparison.OrdinalIgnoreCase)
                || value.EndsWith('.' + root, StringComparison.OrdinalIgnoreCase));
    }

    public AdultAnalysisSettingsSnapshot SetDefaults(bool hideAdvertisingByDefault)
    {
        lock (_gate)
        {
            _hideAdvertisingByDefault = hideAdvertisingByDefault;
            Save();
            return GetSnapshotUnsafe();
        }
    }

    public string? AddAdvertisingDomain(string? domain)
    {
        var value = Normalize(domain);
        if (value is null || !value.Contains('.')) return null;
        lock (_gate)
        {
            _advertisingDomains.Add(value);
            Save();
        }
        return value;
    }

    public void RemoveAdvertisingDomain(string? domain)
    {
        var value = Normalize(domain);
        if (value is null) return;
        lock (_gate)
        {
            _advertisingDomains.Remove(value);
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var saved = JsonSerializer.Deserialize<AdultAnalysisSettingsSnapshot>(File.ReadAllText(_path));
            if (saved is null) return;
            _hideAdvertisingByDefault = saved.HideAdvertisingByDefault;
            _advertisingDomains = saved.AdvertisingDomains
                .Select(Normalize)
                .Where(x => x is not null)
                .Select(x => x!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
    }

    private AdultAnalysisSettingsSnapshot GetSnapshotUnsafe() =>
        new(_hideAdvertisingByDefault, _advertisingDomains.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());

    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(GetSnapshotUnsafe(), new JsonSerializerOptions { WriteIndented = true }));

    private static string? Normalize(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        var value = domain.Trim();
        if (Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host)) value = uri.Host;
        value = value.Trim().Trim('.').ToLowerInvariant();
        return value.Length is > 0 and <= 253 ? value : null;
    }
}
