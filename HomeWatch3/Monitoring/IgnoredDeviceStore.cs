using System.Text.Json;
using HomeWatch3.Data;

namespace HomeWatch3.Monitoring;

public sealed class IgnoredDeviceStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private HashSet<long> _ids;

    public IgnoredDeviceStore(IConfiguration configuration)
    {
        var dataPath = configuration["HomeWatch:DataPath"];
        if (string.IsNullOrWhiteSpace(dataPath))
            dataPath = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataPath);
        _path = Path.Combine(dataPath, "ignored-devices.json");
        _ids = Load();
    }

    public bool IsIgnored(long? deviceId)
    {
        if (!deviceId.HasValue) return false;
        lock (_gate) return _ids.Contains(deviceId.Value);
    }

    public IReadOnlyCollection<long> GetIds()
    {
        lock (_gate) return _ids.OrderBy(x => x).ToArray();
    }

    public void Set(long deviceId, bool ignored)
    {
        lock (_gate)
        {
            if (ignored) _ids.Add(deviceId);
            else _ids.Remove(deviceId);
            Save();
        }
    }

    private HashSet<long> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new HashSet<long>();
            var values = JsonSerializer.Deserialize<long[]>(File.ReadAllText(_path)) ?? Array.Empty<long>();
            return values.ToHashSet();
        }
        catch
        {
            return new HashSet<long>();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_ids.OrderBy(x => x).ToArray(), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}

public sealed record DeviceIgnoreUpdate(bool Ignored);
