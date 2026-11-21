using System.Text.Json;

namespace HomeWatch3.Monitoring;

public sealed class IgnoredDomainStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private HashSet<string> _domains;

    public IgnoredDomainStore(IConfiguration configuration)
    {
        var dataPath = configuration["HomeWatch:DataPath"];
        if (string.IsNullOrWhiteSpace(dataPath)) dataPath = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataPath);
        _path = Path.Combine(dataPath, "ignored-domains.json");
        _domains = Load();
    }

    public bool IsIgnored(string? domain)
    {
        var value = Normalize(domain);
        if (value is null) return false;
        lock (_gate)
            return _domains.Any(d => value.Equals(d, StringComparison.OrdinalIgnoreCase) || value.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyCollection<string> GetDomains()
    {
        lock (_gate) return _domains.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string? Add(string? domain)
    {
        var value = Normalize(domain);
        if (value is null) return null;
        lock (_gate) { _domains.Add(value); Save(); }
        return value;
    }

    public void Remove(string? domain)
    {
        var value = Normalize(domain);
        if (value is null) return;
        lock (_gate) { _domains.Remove(value); Save(); }
    }

    private HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            var values = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path)) ?? Array.Empty<string>();
            return values.Select(Normalize).Where(x => x is not null).Select(x => x!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_domains.OrderBy(x => x).ToArray(), new JsonSerializerOptions { WriteIndented = true }));

    private static string? Normalize(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        var value = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (Uri.TryCreate(value.Contains("://") ? value : "https://" + value, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)) value = uri.Host.TrimEnd('.').ToLowerInvariant();
        return value.Length is > 0 and <= 253 ? value : null;
    }
}

public sealed record IgnoredDomainUpdate(string? Domain);
