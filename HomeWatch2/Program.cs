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
}

app.MapGet("/api/status", (ImportState import) => Results.Ok(new
{
    ok = true,
    version = "2.0.0-alpha.3",
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
            deviceName = x.Device != null ? x.Device.Name : "Unknown device"
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

app.MapGet("/api/devices", async (HomeWatchDb db) =>
    Results.Ok(await db.Devices.AsNoTracking().OrderByDescending(x => x.LastSeen).ToListAsync()));

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

        foreach (var row in rows.EnumerateArray().Reverse())
        {
            var domain = ReadString(row, "question", "name").TrimEnd('.').ToLowerInvariant();
            var clientIp = ReadString(row, "client");
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(clientIp)) continue;

            var timestampText = ReadString(row, "time");
            if (!DateTimeOffset.TryParse(timestampText, out var parsed)) continue;
            var timestamp = parsed.UtcDateTime;
            var exists = await db.Events.AnyAsync(x => x.Timestamp == timestamp && x.Domain == domain && x.Device.IpAddress == clientIp, cancellationToken);
            if (exists) continue;

            var device = await db.Devices.FirstOrDefaultAsync(x => x.IpAddress == clientIp, cancellationToken);
            if (device is null)
            {
                var clientName = ReadString(row, "client_info", "name");
                device = new Device
                {
                    Id = Guid.NewGuid(),
                    Name = string.IsNullOrWhiteSpace(clientName) ? clientIp : clientName,
                    IpAddress = clientIp,
                    FirstSeen = timestamp,
                    LastSeen = timestamp
                };
                db.Devices.Add(device);
            }
            else if (timestamp > device.LastSeen)
            {
                device.LastSeen = timestamp;
            }

            db.Events.Add(new ActivityEvent
            {
                Timestamp = timestamp,
                Device = device,
                Domain = domain,
                Category = "dns",
                Action = ReadString(row, "reason") is { Length: > 0 } reason ? reason : "observed",
                Source = "adguard"
            });
            imported++;
        }

        if (imported > 0) await db.SaveChangesAsync(cancellationToken);
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