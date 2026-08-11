using System.Text.Json;

namespace HomeWatch3.Notifications;

public static class NtfyEventTypes
{
    public const string AdultContent = "adult-content";
    public const string NewDevice = "new-device";
    public const string NewIp = "new-ip";
}

public sealed record NtfyEventDefinition(
    string Type,
    string Name,
    string Description,
    bool DefaultEnabled,
    string DefaultPriority);

public sealed record NtfyEventPreference(
    string Type,
    string Name,
    string Description,
    bool Enabled,
    string Priority);

public sealed record NtfySettingsSnapshot(
    bool Enabled,
    IReadOnlyList<NtfyEventPreference> Events,
    IReadOnlyList<string> Priorities);

public sealed record NtfyEventPreferenceUpdate(string Type, bool Enabled, string Priority);
public sealed record NtfySettingsUpdate(bool Enabled, List<NtfyEventPreferenceUpdate>? Events);

public sealed class NtfyNotificationSettingsStore
{
    private static readonly string[] AllowedPriorities = ["min", "low", "default", "high", "max"];
    private static readonly NtfyEventDefinition[] Definitions =
    [
        new(NtfyEventTypes.AdultContent, "Adult activity", "An adult domain is detected or blocked.", true, "high"),
        new(NtfyEventTypes.NewDevice, "New device", "A MAC address appears on the network for the first time.", false, "high"),
        new(NtfyEventTypes.NewIp, "Device IP change", "A known device receives a different IP address.", true, "default")
    ];

    private readonly string _path;
    private readonly object _gate = new();
    private StoredSettings _settings;

    public NtfyNotificationSettingsStore(IConfiguration configuration)
    {
        var dataPath = configuration["HomeWatch:DataPath"];
        if (string.IsNullOrWhiteSpace(dataPath)) dataPath = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataPath);
        _path = Path.Combine(dataPath, "ntfy-notifications.json");
        _settings = Load();
    }

    public NtfySettingsSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var events = Definitions.Select(definition =>
            {
                var preference = GetPreference(definition);
                return new NtfyEventPreference(
                    definition.Type,
                    definition.Name,
                    definition.Description,
                    preference.Enabled,
                    preference.Priority);
            }).ToArray();
            return new NtfySettingsSnapshot(_settings.Enabled, events, AllowedPriorities);
        }
    }

    public bool TryGetDelivery(string eventType, out string priority)
    {
        lock (_gate)
        {
            priority = "default";
            if (!_settings.Enabled) return false;
            var definition = Definitions.FirstOrDefault(x => x.Type.Equals(eventType, StringComparison.OrdinalIgnoreCase));
            if (definition is null) return false;
            var preference = GetPreference(definition);
            priority = preference.Priority;
            return preference.Enabled;
        }
    }

    public NtfySettingsSnapshot Update(NtfySettingsUpdate update)
    {
        lock (_gate)
        {
            var supplied = update.Events ?? [];
            foreach (var preference in supplied)
            {
                if (!Definitions.Any(x => x.Type.Equals(preference.Type, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"Unknown ntfy event type: {preference.Type}");
                if (!AllowedPriorities.Contains(preference.Priority, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException($"Unsupported ntfy priority: {preference.Priority}");
            }

            _settings.Enabled = update.Enabled;
            foreach (var preference in supplied)
            {
                var type = Definitions.First(x => x.Type.Equals(preference.Type, StringComparison.OrdinalIgnoreCase)).Type;
                _settings.Events[type] = new StoredEventPreference
                {
                    Enabled = preference.Enabled,
                    Priority = NormalizePriority(preference.Priority, "default")
                };
            }
            Save();
            return GetSnapshotUnsafe();
        }
    }

    private NtfySettingsSnapshot GetSnapshotUnsafe()
    {
        var events = Definitions.Select(definition =>
        {
            var preference = GetPreference(definition);
            return new NtfyEventPreference(definition.Type, definition.Name, definition.Description, preference.Enabled, preference.Priority);
        }).ToArray();
        return new NtfySettingsSnapshot(_settings.Enabled, events, AllowedPriorities);
    }

    private StoredEventPreference GetPreference(NtfyEventDefinition definition)
    {
        if (!_settings.Events.TryGetValue(definition.Type, out var preference))
            return new StoredEventPreference { Enabled = definition.DefaultEnabled, Priority = definition.DefaultPriority };
        return new StoredEventPreference
        {
            Enabled = preference.Enabled,
            Priority = NormalizePriority(preference.Priority, definition.DefaultPriority)
        };
    }

    private StoredSettings Load()
    {
        var defaults = CreateDefaults();
        try
        {
            if (!File.Exists(_path)) return defaults;
            var loaded = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(_path));
            if (loaded is null) return defaults;
            loaded.Events = new Dictionary<string, StoredEventPreference>(loaded.Events ?? new(), StringComparer.OrdinalIgnoreCase);
            foreach (var definition in Definitions)
            {
                if (!loaded.Events.TryGetValue(definition.Type, out var preference))
                    loaded.Events[definition.Type] = new StoredEventPreference { Enabled = definition.DefaultEnabled, Priority = definition.DefaultPriority };
                else
                    preference.Priority = NormalizePriority(preference.Priority, definition.DefaultPriority);
            }
            return loaded;
        }
        catch
        {
            return defaults;
        }
    }

    private static StoredSettings CreateDefaults() => new()
    {
        Enabled = true,
        Events = Definitions.ToDictionary(
            x => x.Type,
            x => new StoredEventPreference { Enabled = x.DefaultEnabled, Priority = x.DefaultPriority },
            StringComparer.OrdinalIgnoreCase)
    };

    private void Save()
    {
        var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _path, true);
    }

    private static string NormalizePriority(string? priority, string fallback) =>
        AllowedPriorities.FirstOrDefault(x => x.Equals(priority, StringComparison.OrdinalIgnoreCase)) ?? fallback;

    public sealed class StoredSettings
    {
        public bool Enabled { get; set; } = true;
        public Dictionary<string, StoredEventPreference> Events { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class StoredEventPreference
    {
        public bool Enabled { get; set; }
        public string Priority { get; set; } = "default";
    }
}
