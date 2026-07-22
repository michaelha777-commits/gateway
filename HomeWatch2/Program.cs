using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<HomeWatchDb>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("HomeWatch") ?? "Data Source=homewatch.db"));
builder.Services.Configure<AdGuardOptions>(builder.Configuration.GetSection("AdGuard"));
builder.Services.AddHttpClient();
builder.Services.AddHostedService<AdGuardImportWorker>();
builder.Services.AddSingleton<ImportState>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyHeader().AllowAnyMethod().SetIsOriginAllowed(_ => true)));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
    db.Database.EnsureCreated();
    await RepairDuplicateDevicesAsync(db);
}

app.MapGet("/api/status", (ImportState import) => Results.Ok(new
{
    ok = true,
    version = "2.0.0-alpha.5",
    importer = new { import.Connected, import.LastSuccess, import.LastError, import.Imported },
    generatedAt = DateTime.UtcNow
}));

app.MapGet("/api/dashboard", async (HomeWatchDb db, int hours = 24) =>
{
    hours = Math.Clamp(hours, 1, 720);
    var cutoff = DateTime.UtcNow.AddHours(-hours);

    var events = await db.Events.AsNoTracking()
        .Where(x => x.Timestamp >= cutoff)
        .OrderByDescending(x => x.Timestamp)
        .Take(200)
        .Select(x => new
        {
            x.Id,
            x.Timestamp,
            x.Domain,
            x.Category,
            x.Action,
            deviceId = x.DeviceId,
            deviceName = x.Device != null ? x.Device.Name : "Unknown device",
            deviceIp = x.Device != null ? x.Device.IpAddress : null
        })
        .ToListAsync();

    var deviceCount = await db.Devices.AsNoTracking().CountAsync();
    var activeDevices = await db.Devices.AsNoTracking().CountAsync(x => x.LastSeen >= cutoff);
    var alertCount = await db.Alerts.AsNoTracking().CountAsync(x => !x.Acknowledged && x.CreatedAt >= cutoff);

    return Results.Ok(new
    {
        summary = new { deviceCount, activeDevices, alertCount, eventCount = events.Count },
        events,
        generatedAt = DateTime.UtcNow
    });
});

app.MapGet("/api/devices", async (HomeWatchDb db, int hours = 24) =>
{
    hours = Math.Clamp(hours, 1, 720);
    var cutoff = DateTime.UtcNow.AddHours(-hours);

    var devices = await db.Devices.AsNoTracking()
        .OrderByDescending(x => x.LastSeen)
        .Select(x => new
        {
            x.Id,
            x.Name,
            x.IpAddress,
            x.MacAddress,
            x.Vendor,
            x.FirstSeen,
            x.LastSeen,
            Online = x.LastSeen >= DateTime.UtcNow.AddMinutes(-5),
            EventCount = db.Events.Count(e => e.DeviceId == x.Id && e.Timestamp >= cutoff),
            TopDomains = db.Events
                .Where(e => e.DeviceId == x.Id && e.Timestamp >= cutoff)
                .GroupBy(e => e.Domain)
                .OrderByDescending(g => g.Count())
                .Select(g => new { Domain = g.Key, Count = g.Count() })
                .Take(5)
                .ToList()
        })
        .ToListAsync();

    return Results.Ok(new { devices, generatedAt = DateTime.UtcNow });
});

app.MapGet("/api/devices/{id:guid}/activity", async (
    Guid id,
    HomeWatchDb db,
    int hours = 24,
    int limit = 500,
    string? search = null) =>
{
    hours = Math.Clamp(hours, 1, 720);
    limit = Math.Clamp(limit, 10, 2000);
    var cutoff = DateTime.UtcNow.AddHours(-hours);

    var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    if (device is null) return Results.NotFound();

    var query = db.Events.AsNoTracking()
        .Where(x => x.DeviceId == id && x.Timestamp >= cutoff);

    if (!string.IsNullOrWhiteSpace(search))
    {
        var term = search.Trim().ToLower();
        query = query.Where(x => x.Domain.ToLower().Contains(term));
    }

    var events = await query
        .OrderByDescending(x => x.Timestamp)
        .Take(limit)
        .Select(x => new { x.Id, x.Timestamp, x.Domain, x.Category, x.Action })
        .ToListAsync();

    var topDomains = events
        .GroupBy(x => x.Domain, StringComparer.OrdinalIgnoreCase)
        .Select(group => new { domain = group.Key, count = group.Count() })
        .OrderByDescending(x => x.count)
        .ThenBy(x => x.domain)
        .Take(20)
        .ToList();

    return Results.Ok(new
    {
        device = new
        {
            device.Id,
            device.Name,
            device.IpAddress,
            device.MacAddress,
            device.Vendor,
            device.FirstSeen,
            device.LastSeen,
            online = device.LastSeen >= DateTime.UtcNow.AddMinutes(-5)
        },
        summary = new { eventCount = events.Count, uniqueDomains = events.Select(x => x.Domain).Distinct(StringComparer.OrdinalIgnoreCase).Count() },
        topDomains,
        events,
        generatedAt = DateTime.UtcNow
    });
});

app.MapGet("/api/alerts", async (HomeWatchDb db) =>
    Results.Ok(await db.Alerts.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync()));

app.MapPost("/api/alerts/{id:guid}/acknowledge", async (Guid id, HomeWatchDb db) =>
{
    var alert = await db.Alerts.FindAsync(id);
    if (alert is null) return Results.NotFound();
    alert.Acknowledged = true;
    alert.AcknowledgedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(alert);
});

app.MapFallbackToFile("index.html");
app.Run("http://0.0.0.0:8920");

static async Task RepairDuplicateDevicesAsync(HomeWatchDb db)
{
    var devices = await db.Devices.OrderBy(x => x.FirstSeen).ToListAsync();
    var duplicateGroups = devices
        .Where(x => !string.IsNullOrWhiteSpace(x.IpAddress))
        .GroupBy(x => x.IpAddress!, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() > 1)
        .ToList();

    foreach (var group in duplicateGroups)
    {
        var canonical = group.First();
        var duplicates = group.Skip(1).ToList();
        var duplicateIds = duplicates.Select(x => x.Id).ToList();

        var events = await db.Events.Where(x => duplicateIds.Contains(x.DeviceId)).ToListAsync();
        foreach (var activity in events) activity.DeviceId = canonical.Id;

        var alerts = await db.Alerts.Where(x => duplicateIds.Contains(x.DeviceId)).ToListAsync();
        foreach (var alert in alerts) alert.DeviceId = canonical.Id;

        canonical.FirstSeen = group.Min(x => x.FirstSeen);
        canonical.LastSeen = group.Max(x => x.LastSeen);
        var bestName = group.Select(x => x.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && name != canonical.IpAddress);
        if (!string.IsNullOrWhiteSpace(bestName)) canonical.Name = bestName;

        db.Devices.RemoveRange(duplicates);
    }

    if (duplicateGroups.Count > 0) await db.SaveChangesAsync();

    await db.Database.ExecuteSqlRawAsync(
        "CREATE UNIQUE INDEX IF NOT EXISTS IX_Devices_IpAddress ON Devices (IpAddress) WHERE IpAddress IS NOT NULL;");
}

public sealed class AdGuardImportWorker(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ImportState state,
    ILogger<AdGuardImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ImportAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                state.Connected = false;
                state.LastError = ex.Message;
                logger.LogWarning(ex, "AdGuard import failed");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task ImportAsync(CancellationToken cancellationToken)
    {
        var options = configuration.GetSection("AdGuard").Get<AdGuardOptions>() ?? new();
        var baseUrl = options.BaseUrl.TrimEnd('/');
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/control/querylog?limit={Math.Clamp(options.BatchSize, 10, 500)}");
        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }

        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("AdGuard returned no query-log data.");

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
        var imported = 0;

        var parsedRows = rows.EnumerateArray().Reverse()
            .Select(row => new
            {
                Row = row,
                Domain = ReadString(row, "question", "name").TrimEnd('.').ToLowerInvariant(),
                ClientIp = ReadString(row, "client"),
                TimestampText = ReadString(row, "time")
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Domain) && !string.IsNullOrWhiteSpace(x.ClientIp))
            .ToList();

        var clientIps = parsedRows.Select(x => x.ClientIp).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var knownDevices = await db.Devices.Where(x => x.IpAddress != null && clientIps.Contains(x.IpAddress)).ToListAsync(cancellationToken);
        var devicesByIp = knownDevices
            .Where(x => x.IpAddress != null)
            .ToDictionary(x => x.IpAddress!, StringComparer.OrdinalIgnoreCase);

        foreach (var item in parsedRows)
        {
            if (!DateTimeOffset.TryParse(item.TimestampText, out var parsed)) continue;
            var timestamp = parsed.UtcDateTime;

            if (!devicesByIp.TryGetValue(item.ClientIp, out var device))
            {
                var clientName = ReadString(item.Row, "client_info", "name");
                device = new Device
                {
                    Id = Guid.NewGuid(),
                    Name = string.IsNullOrWhiteSpace(clientName) ? item.ClientIp : clientName,
                    IpAddress = item.ClientIp,
                    FirstSeen = timestamp,
                    LastSeen = timestamp
                };
                devicesByIp[item.ClientIp] = device;
                db.Devices.Add(device);
            }
            else
            {
                if (timestamp > device.LastSeen) device.LastSeen = timestamp;
                var clientName = ReadString(item.Row, "client_info", "name");
                if (!string.IsNullOrWhiteSpace(clientName) && device.Name == device.IpAddress)
                    device.Name = clientName;
            }

            var exists = await db.Events.AnyAsync(
                x => x.Timestamp == timestamp && x.Domain == item.Domain && x.DeviceId == device.Id,
                cancellationToken);
            if (exists || db.Events.Local.Any(x => x.Timestamp == timestamp && x.Domain == item.Domain && x.DeviceId == device.Id))
                continue;

            db.Events.Add(new ActivityEvent
            {
                Timestamp = timestamp,
                DeviceId = device.Id,
                Device = device,
                Domain = item.Domain,
                Category = "dns",
                Action = ReadString(item.Row, "reason") is { Length: > 0 } reason ? reason : "observed",
                Source = "adguard"
            });
            imported++;
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
        state.Connected = true;
        state.LastSuccess = DateTime.UtcNow;
        state.LastError = null;
        state.Imported += imported;
    }

    private static string ReadString(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return "";
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
    }
}

public sealed class ImportState
{
    public bool Connected { get; set; }
    public DateTime? LastSuccess { get; set; }
    public string? LastError { get; set; }
    public long Imported { get; set; }
}

public sealed class AdGuardOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public int BatchSize { get; set; } = 200;
}

public sealed class HomeWatchDb(DbContextOptions<HomeWatchDb> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<ActivityEvent> Events => Set<ActivityEvent>();
    public DbSet<ActivitySession> Sessions => Set<ActivitySession>();
    public DbSet<Alert> Alerts => Set<Alert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>().HasIndex(x => x.MacAddress).IsUnique();
        modelBuilder.Entity<Device>().HasIndex(x => x.IpAddress).IsUnique();
        modelBuilder.Entity<ActivityEvent>().HasIndex(x => x.Timestamp);
        modelBuilder.Entity<ActivityEvent>().HasIndex(x => new { x.DeviceId, x.Timestamp });
        modelBuilder.Entity<Alert>().HasIndex(x => new { x.Acknowledged, x.CreatedAt });
    }
}

public sealed class Device
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "Unknown device";
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }
    public string? Vendor { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
}

public sealed class ActivityEvent
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }
    public string Domain { get; set; } = "";
    public string Category { get; set; } = "unknown";
    public string Action { get; set; } = "observed";
    public string Source { get; set; } = "adguard";
}

public sealed class ActivitySession
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public int Confidence { get; set; }
    public string Assessment { get; set; } = "";
}

public sealed class Alert
{
    public Guid Id { get; set; }
    public Guid? SessionId { get; set; }
    public Guid DeviceId { get; set; }
    public string Severity { get; set; } = "medium";
    public string Title { get; set; } = "Activity alert";
    public string Detail { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool Acknowledged { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
}