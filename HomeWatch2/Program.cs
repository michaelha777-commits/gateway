using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<HomeWatchDb>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("HomeWatch") ?? "Data Source=homewatch.db"));
builder.Services.Configure<AdGuardOptions>(builder.Configuration.GetSection("AdGuard"));
builder.Services.Configure<NtfyOptions>(builder.Configuration.GetSection("Ntfy"));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<NtfyNotifier>();
builder.Services.AddSingleton<ExternalAdultDomainDatabase>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ExternalAdultDomainDatabase>());
builder.Services.AddSingleton<AdultSessionMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AdultSessionMonitor>());
builder.Services.AddHostedService<AdGuardImportWorker>();
builder.Services.AddHostedService<ApplePrivateRelayBlocker>();
builder.Services.AddHostedService<ApplePrivateRelayBlocker>();
builder.Services.AddSingleton<ImportState>();
builder.Services.AddHomeWatchNetworkDiscovery();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true)));

var app = builder.Build();
AdultSafetyOverrides.Configure(app.Environment);
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
    db.Database.EnsureCreated();
    await RepairDuplicateDevicesAsync(db);
}

app.MapGet("/api/status", (ImportState import, IConfiguration configuration, ExternalAdultDomainDatabase adultDb) => Results.Ok(new
{
    ok = true,
    version = "2.0.0-alpha.15",
    importer = new { import.Connected, lastSuccess = UtcIso(import.LastSuccess), import.LastError, import.Imported },
    adultIntelligence = adultDb.GetStatus(),
    adultSafeOverrides = AdultSafetyOverrides.Status(),
    notifications = new { configured = !string.IsNullOrWhiteSpace(configuration["Ntfy:Topic"]) },
    generatedAt = UtcIso(DateTime.UtcNow)
}));

app.MapGet("/api/intelligence/adult", (ExternalAdultDomainDatabase adultDb) => Results.Ok(adultDb.GetStatus()));
app.MapPost("/api/intelligence/adult/refresh", async (ExternalAdultDomainDatabase adultDb, CancellationToken ct) =>
{
    await adultDb.RefreshAsync(ct);
    return Results.Ok(adultDb.GetStatus());
});
app.MapGet("/api/intelligence/adult/classify", (string domain, ExternalAdultDomainDatabase adultDb) =>
{
    if (AdultSafetyOverrides.IsSafe(domain))
        return Results.Ok(new AdultClassification(false, 100, RootDomain(domain), "Marked not adult by user", "user-safe-override"));
    return Results.Ok(adultDb.Classify(domain));
});

app.MapPost("/api/intelligence/adult/reclassify", async (HomeWatchDb db, ExternalAdultDomainDatabase adultDb, int days = 30) =>
{
    days = Math.Clamp(days, 1, 365);
    var cutoff = DateTime.UtcNow.AddDays(-days);
    var candidates = await db.Events.Where(x => x.Timestamp >= cutoff).ToListAsync();
    var changed = 0;
    foreach (var item in candidates)
    {
        var shouldBeAdult = !AdultSafetyOverrides.IsSafe(item.Domain) && adultDb.IsAdult(item.Domain);
        var desired = shouldBeAdult ? "adult" : item.Category == "adult" ? "dns" : item.Category;
        if (item.Category == desired) continue;
        item.Category = desired;
        item.Source = shouldBeAdult ? "adult-intelligence:reclassified" : "user-safe-override";
        changed++;
    }
    if (changed > 0) await db.SaveChangesAsync();
    return Results.Ok(new { changed, scanned = candidates.Count, days });
});

app.MapGet("/api/dashboard", async (HomeWatchDb db, int hours = 24) =>
{
    hours = Math.Clamp(hours, 1, 720);
    var cutoff = DateTime.UtcNow.AddHours(-hours);
    var rows = await db.Events.AsNoTracking().Where(x => x.Timestamp >= cutoff).OrderByDescending(x => x.Timestamp).Take(200)
        .Select(x => new
        {
            x.Id, x.Timestamp, x.Domain, x.Category, x.Action, x.DeviceId,
            DeviceName = x.Device != null ? x.Device.Name : "Unknown device",
            DeviceIp = x.Device != null ? x.Device.IpAddress : null
        }).ToListAsync();
    var events = rows.Select(x => new
    {
        x.Id, timestamp = UtcIso(x.Timestamp), x.Domain, x.Category, x.Action,
        deviceId = x.DeviceId, deviceName = x.DeviceName, deviceIp = x.DeviceIp
    });
    return Results.Ok(new
    {
        summary = new
        {
            deviceCount = await db.Devices.AsNoTracking().CountAsync(),
            activeDevices = await db.Devices.AsNoTracking().CountAsync(x => x.LastSeen >= cutoff),
            alertCount = await db.Alerts.AsNoTracking().CountAsync(x => !x.Acknowledged && x.CreatedAt >= cutoff),
            eventCount = rows.Count
        },
        events,
        generatedAt = UtcIso(DateTime.UtcNow)
    });
});

app.MapGet("/api/devices", async (HomeWatchDb db, int hours = 24) =>
{
    hours = Math.Clamp(hours, 1, 720);
    var cutoff = DateTime.UtcNow.AddHours(-hours);
    var devices = await db.Devices.AsNoTracking().OrderByDescending(x => x.LastSeen).ToListAsync();
    var ids = devices.Select(x => x.Id).ToList();
    var events = await db.Events.AsNoTracking().Where(x => ids.Contains(x.DeviceId) && x.Timestamp >= cutoff).ToListAsync();
    var result = devices.Select(device => new
    {
        device.Id, device.Name, device.IpAddress, device.MacAddress, device.Vendor,
        firstSeen = UtcIso(device.FirstSeen), lastSeen = UtcIso(device.LastSeen),
        online = device.LastSeen >= DateTime.UtcNow.AddMinutes(-5),
        eventCount = events.Count(x => x.DeviceId == device.Id),
        topDomains = events.Where(x => x.DeviceId == device.Id).GroupBy(x => x.Domain, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { domain = g.Key, count = g.Count() }).OrderByDescending(x => x.count).Take(5).ToList()
    });
    return Results.Ok(new { devices = result, generatedAt = UtcIso(DateTime.UtcNow) });
});

app.MapPut("/api/devices/{id:guid}", async (Guid id, DeviceUpdate request, HomeWatchDb db) =>
{
    var device = await db.Devices.FindAsync(id);
    if (device is null) return Results.NotFound(new { error = "Device not found." });
    var name = (request.Name ?? "").Trim();
    if (name.Length is < 1 or > 80) return Results.BadRequest(new { error = "Device name must be between 1 and 80 characters." });
    string? mac = null;
    if (!string.IsNullOrWhiteSpace(request.MacAddress))
    {
        mac = NormalizeMac(request.MacAddress);
        if (mac is null) return Results.BadRequest(new { error = "Enter a valid MAC address such as AA:BB:CC:DD:EE:FF." });
        if (await db.Devices.AnyAsync(x => x.Id != id && x.MacAddress == mac)) return Results.Conflict(new { error = "That MAC address is already assigned to another device." });
    }
    device.Name = name; device.MacAddress = mac;
    await db.SaveChangesAsync();
    return Results.Ok(new { device.Id, device.Name, device.IpAddress, device.MacAddress, device.Vendor, lastSeen = UtcIso(device.LastSeen) });
});

app.MapGet("/api/devices/{id:guid}/activity", async (Guid id, HomeWatchDb db, int hours = 24, int limit = 500, string? search = null) =>
{
    hours = Math.Clamp(hours, 1, 720); limit = Math.Clamp(limit, 10, 2000);
    var cutoff = DateTime.UtcNow.AddHours(-hours);
    var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (device is null) return Results.NotFound();
    var query = db.Events.AsNoTracking().Where(x => x.DeviceId == id && x.Timestamp >= cutoff);
    if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim().ToLower(); query = query.Where(x => x.Domain.ToLower().Contains(term)); }
    var rows = await query.OrderByDescending(x => x.Timestamp).Take(limit).Select(x => new { x.Id, x.Timestamp, x.Domain, x.Category, x.Action }).ToListAsync();
    var events = rows.Select(x => new { x.Id, timestamp = UtcIso(x.Timestamp), x.Domain, x.Category, x.Action }).ToList();
    var topDomains = rows.GroupBy(x => x.Domain, StringComparer.OrdinalIgnoreCase).Select(g => new { domain = g.Key, count = g.Count() }).OrderByDescending(x => x.count).Take(20).ToList();
    return Results.Ok(new
    {
        device = new { device.Id, device.Name, device.IpAddress, device.MacAddress, device.Vendor, firstSeen = UtcIso(device.FirstSeen), lastSeen = UtcIso(device.LastSeen), online = device.LastSeen >= DateTime.UtcNow.AddMinutes(-5) },
        summary = new { eventCount = rows.Count, uniqueDomains = rows.Select(x => x.Domain).Distinct(StringComparer.OrdinalIgnoreCase).Count() },
        topDomains, events, generatedAt = UtcIso(DateTime.UtcNow)
    });
});

app.MapGet("/api/alerts", async (HomeWatchDb db) =>
{
    var alerts = await db.Alerts.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(200).ToListAsync();
    var deviceIds = alerts.Select(x => x.DeviceId).Distinct().ToList();
    var devices = await db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
    return Results.Ok(new
    {
        alerts = alerts.Select(alert => new
        {
            alert.Id, alert.Severity, alert.Title, alert.Detail, createdAt = UtcIso(alert.CreatedAt), alert.Acknowledged,
            acknowledgedAt = UtcIso(alert.AcknowledgedAt), deviceId = alert.DeviceId,
            domain = ExtractAlertDomain(alert.Detail),
            deviceName = devices.TryGetValue(alert.DeviceId, out var d) ? d.Name : "Unknown device",
            deviceIp = devices.TryGetValue(alert.DeviceId, out var d2) ? d2.IpAddress : null
        }),
        generatedAt = UtcIso(DateTime.UtcNow)
    });
});

app.MapPost("/api/alerts/{id:guid}/acknowledge", async (Guid id, HomeWatchDb db, NtfyNotifier ntfy) =>
{
    var alert = await db.Alerts.FindAsync(id);
    if (alert is null) return Results.NotFound();
    alert.Acknowledged = true; alert.AcknowledgedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    await ntfy.SendAsync("HomeWatch alert acknowledged", alert.Title, "white_check_mark", 2, CancellationToken.None);
    return Results.Ok(new { alert.Id, alert.Acknowledged, acknowledgedAt = UtcIso(alert.AcknowledgedAt) });
});

app.MapPost("/api/notifications/test", async (NtfyNotifier ntfy) =>
{
    var sent = await ntfy.SendAsync("HomeWatch test", "Notifications are connected.", "bell", 3, CancellationToken.None);
    return sent ? Results.Ok(new { sent = true }) : Results.BadRequest(new { sent = false, error = "ntfy topic is not configured" });
});

app.MapRuntimeSettingsEndpoints();
app.MapHomeWatchNetworkDiscovery();
app.MapEndpoints();
app.MapBackupDiagnosticsEndpoints();
app.MapFallbackToFile("index.html");
app.Run("http://0.0.0.0:8920");

static string? UtcIso(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("O");
static string? NormalizeMac(string input)
{
    var hex = Regex.Replace(input, "[^0-9A-Fa-f]", "").ToUpperInvariant();
    return hex.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : null;
}
static string RootDomain(string domain)
{
    var parts = (domain ?? "").Trim('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
    return parts.Length >= 2 ? string.Join('.', parts[^2], parts[^1]) : domain;
}
static string? ExtractAlertDomain(string detail)
{
    if (string.IsNullOrWhiteSpace(detail)) return null;
    var match = Regex.Match(detail, @"(?i)(?:first detected site|first site|primary site|domain):\s*([a-z0-9.-]+)");
    return match.Success ? match.Groups[1].Value.TrimEnd('.') : null;
}

static async Task RepairDuplicateDevicesAsync(HomeWatchDb db)
{
    var devices = await db.Devices.OrderBy(x => x.FirstSeen).ToListAsync();
    var duplicateGroups = devices.Where(x => !string.IsNullOrWhiteSpace(x.IpAddress)).GroupBy(x => x.IpAddress!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
    foreach (var group in duplicateGroups)
    {
        var canonical = group.First(); var duplicates = group.Skip(1).ToList(); var ids = duplicates.Select(x => x.Id).ToList();
        foreach (var e in await db.Events.Where(x => ids.Contains(x.DeviceId)).ToListAsync()) e.DeviceId = canonical.Id;
        foreach (var a in await db.Alerts.Where(x => ids.Contains(x.DeviceId)).ToListAsync()) a.DeviceId = canonical.Id;
        canonical.FirstSeen = group.Min(x => x.FirstSeen); canonical.LastSeen = group.Max(x => x.LastSeen);
        var bestName = group.Select(x => x.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && n != canonical.IpAddress);
        if (!string.IsNullOrWhiteSpace(bestName)) canonical.Name = bestName;
        db.Devices.RemoveRange(duplicates);
    }
    if (duplicateGroups.Count > 0) await db.SaveChangesAsync();
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_Devices_IpAddress ON Devices (IpAddress) WHERE IpAddress IS NOT NULL;");
}

public sealed class AdGuardImportWorker(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ImportState state,
    ExternalAdultDomainDatabase adultIntelligence,
    AdultSessionMonitor adultSessions,
    ILogger<AdGuardImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ImportAsync(stoppingToken); await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { state.Connected = false; state.LastError = ex.Message; logger.LogWarning(ex, "AdGuard import failed"); await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
        }
    }

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        var options = configuration.GetSection("AdGuard").Get<AdGuardOptions>() ?? new();
        var request = new HttpRequestMessage(HttpMethod.Get, $"{options.BaseUrl.TrimEnd('/')}/control/querylog?limit={Math.Clamp(options.BatchSize, 10, 500)}");
        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }
        var client = httpClientFactory.CreateClient(); client.Timeout = TimeSpan.FromSeconds(8);
        using var response = await client.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("AdGuard returned no query-log data.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
        var imported = 0;
        var adultHits = new List<(Guid DeviceId, string Device, string Domain, DateTime Timestamp)>();
        var parsedRows = rows.EnumerateArray().Reverse().Select(row => new
        {
            Row = row,
            Domain = ReadString(row, "question", "name").TrimEnd('.').ToLowerInvariant(),
            ClientIp = ReadString(row, "client"),
            TimestampText = ReadString(row, "time")
        }).Where(x => !string.IsNullOrWhiteSpace(x.Domain) && !string.IsNullOrWhiteSpace(x.ClientIp)).ToList();

        var clientIps = parsedRows.Select(x => x.ClientIp).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var knownDevices = await db.Devices.Where(x => x.IpAddress != null && clientIps.Contains(x.IpAddress)).ToListAsync(cancellationToken);
        var devicesByIp = knownDevices.Where(x => x.IpAddress != null).ToDictionary(x => x.IpAddress!, StringComparer.OrdinalIgnoreCase);

        foreach (var item in parsedRows)
        {
            if (!DateTimeOffset.TryParse(item.TimestampText, out var parsed)) continue;
            var timestamp = parsed.UtcDateTime;
            if (!devicesByIp.TryGetValue(item.ClientIp, out var device))
            {
                var clientName = ReadString(item.Row, "client_info", "name");
                device = new Device { Id = Guid.NewGuid(), Name = string.IsNullOrWhiteSpace(clientName) ? item.ClientIp : clientName, IpAddress = item.ClientIp, FirstSeen = timestamp, LastSeen = timestamp };
                devicesByIp[item.ClientIp] = device; db.Devices.Add(device);
            }
            else
            {
                if (timestamp > device.LastSeen) device.LastSeen = timestamp;
                var clientName = ReadString(item.Row, "client_info", "name");
                if (!string.IsNullOrWhiteSpace(clientName) && device.Name == device.IpAddress) device.Name = clientName;
            }

            var exists = await db.Events.AnyAsync(x => x.Timestamp == timestamp && x.Domain == item.Domain && x.DeviceId == device.Id, cancellationToken);
            if (exists || db.Events.Local.Any(x => x.Timestamp == timestamp && x.Domain == item.Domain && x.DeviceId == device.Id)) continue;

            var safeOverride = AdultSafetyOverrides.IsSafe(item.Domain);
            var classification = safeOverride
                ? new AdultClassification(false, 100, RootDomain(item.Domain), "Marked not adult by user", "user-safe-override")
                : adultIntelligence.Classify(item.Domain);
            var privateRelay = ApplePrivateRelayBlocker.IsPrivateRelayDomain(item.Domain);
            var category = classification.IsAdult ? "adult" : privateRelay ? "privacy-proxy" : safeOverride && (item.Domain.Contains("adbutler") || item.Domain.Contains("scorecardresearch")) ? "advertising" : "dns";
            var action = ReadString(item.Row, "reason") is { Length: > 0 } reason ? reason : "observed";
            db.Events.Add(new ActivityEvent { Timestamp = timestamp, DeviceId = device.Id, Device = device, Domain = item.Domain, Category = category, Action = action, Source = classification.IsAdult ? $"adult-intelligence:{classification.Source}" : safeOverride ? "user-safe-override" : "adguard" });
            imported++;
            if (classification.IsAdult) adultHits.Add((device.Id, device.Name, item.Domain, timestamp));
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
        foreach (var hit in adultHits) await adultSessions.RecordHitAsync(hit.DeviceId, hit.Device, hit.Domain, hit.Timestamp, cancellationToken);
        state.Connected = true; state.LastSuccess = DateTime.UtcNow; state.LastError = null; state.Imported += imported;
    }

    private static string ReadString(JsonElement element, params string[] path)
    {
        foreach (var name in path) if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return "";
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
    }
}

public static class AdultDomainClassifier
{
    private static readonly HashSet<string> KnownDomains = new(AdultSeedDomains.All, StringComparer.OrdinalIgnoreCase);
    private static readonly string[] StrongKeywords =
    {
        "porn", "porno", "pornstar", "xxx", "hentai", "nsfw", "pussy", "blowjob", "gangbang", "hardcore", "sexcam", "sexcams",
        "sexvideo", "sexvideos", "adultvideo", "adultvideos", "nudevideo", "nudevideos", "camgirl", "camgirls", "milfporn", "teenporn",
        "analporn", "gayporn", "lesbianporn", "fapello", "fapster", "erome", "nhentai", "rule34"
    };
    private static readonly HashSet<string> SafeDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "sexeducationforum.org.uk", "sexualhealthontario.ca", "plannedparenthood.org", "nhs.uk", "mayoclinic.org", "wikipedia.org", "reddit.com",
        "x.com", "twitter.com", "instagram.com", "facebook.com", "youtube.com", "scorecardresearch.com", "servedbyadbutler.com"
    };

    public static bool IsAdult(string domain)
    {
        var value = Normalize(domain);
        if (string.IsNullOrWhiteSpace(value) || AdultSafetyOverrides.IsSafe(value) || SafeDomains.Any(root => value == root || value.EndsWith("." + root, StringComparison.OrdinalIgnoreCase))) return false;
        if (KnownDomains.Any(root => value == root || value.EndsWith("." + root, StringComparison.OrdinalIgnoreCase))) return true;
        var labels = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var searchable = string.Join('-', labels.Take(Math.Max(1, labels.Length - 1)));
        return StrongKeywords.Any(keyword => searchable.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
    private static string Normalize(string domain)
    {
        var value = (domain ?? "").Trim().Trim('.').ToLowerInvariant();
        if ((value.StartsWith("http://") || value.StartsWith("https://")) && Uri.TryCreate(value, UriKind.Absolute, out var uri)) value = uri.Host;
        return value.Trim('.');
    }
}

public sealed class NtfyNotifier(IHttpClientFactory factory, IConfiguration configuration, ILogger<NtfyNotifier> logger)
{
    public async Task<bool> SendAsync(string title, string message, string tags, int priority, CancellationToken cancellationToken)
    {
        var options = configuration.GetSection("Ntfy").Get<NtfyOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(options.Topic)) return false;
        try
        {
            var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(options.Topic)}") { Content = new StringContent(message, Encoding.UTF8, "text/plain") };
            request.Headers.TryAddWithoutValidation("Title", title); request.Headers.TryAddWithoutValidation("Priority", Math.Clamp(priority, 1, 5).ToString()); request.Headers.TryAddWithoutValidation("Tags", tags);
            using var response = await client.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode(); return true;
        }
        catch (Exception ex) { logger.LogWarning(ex, "ntfy notification failed"); return false; }
    }
}

public sealed record DeviceUpdate(string? Name, string? MacAddress);
public sealed class ImportState { public bool Connected { get; set; } public DateTime? LastSuccess { get; set; } public string? LastError { get; set; } public long Imported { get; set; } }
public sealed class AdGuardOptions { public string BaseUrl { get; set; } = "http://127.0.0.1"; public string Username { get; set; } = ""; public string Password { get; set; } = ""; public int BatchSize { get; set; } = 200; }
public sealed class NtfyOptions { public string BaseUrl { get; set; } = "https://ntfy.sh"; public string Topic { get; set; } = ""; }

public sealed class HomeWatchDb(DbContextOptions<HomeWatchDb> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>(); public DbSet<ActivityEvent> Events => Set<ActivityEvent>(); public DbSet<ActivitySession> Sessions => Set<ActivitySession>(); public DbSet<Alert> Alerts => Set<Alert>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>().HasIndex(x => x.MacAddress).IsUnique(); modelBuilder.Entity<Device>().HasIndex(x => x.IpAddress).IsUnique();
        modelBuilder.Entity<ActivityEvent>().HasIndex(x => x.Timestamp); modelBuilder.Entity<ActivityEvent>().HasIndex(x => new { x.DeviceId, x.Timestamp });
        modelBuilder.Entity<Alert>().HasIndex(x => new { x.Acknowledged, x.CreatedAt });
    }
}
public sealed class Device { public Guid Id { get; set; } public string Name { get; set; } = "Unknown device"; public string? IpAddress { get; set; } public string? MacAddress { get; set; } public string? Vendor { get; set; } public DateTime FirstSeen { get; set; } public DateTime LastSeen { get; set; } }
public sealed class ActivityEvent { public long Id { get; set; } public DateTime Timestamp { get; set; } public Guid DeviceId { get; set; } public Device? Device { get; set; } public string Domain { get; set; } = ""; public string Category { get; set; } = "unknown"; public string Action { get; set; } = "observed"; public string Source { get; set; } = "adguard"; }
public sealed class ActivitySession { public Guid Id { get; set; } public Guid DeviceId { get; set; } public DateTime StartedAt { get; set; } public DateTime EndedAt { get; set; } public int Confidence { get; set; } public string Assessment { get; set; } = ""; }
public sealed class Alert { public Guid Id { get; set; } public Guid? SessionId { get; set; } public Guid DeviceId { get; set; } public string Severity { get; set; } = "medium"; public string Title { get; set; } = "Activity alert"; public string Detail { get; set; } = ""; public DateTime CreatedAt { get; set; } public bool Acknowledged { get; set; } public DateTime? AcknowledgedAt { get; set; } }