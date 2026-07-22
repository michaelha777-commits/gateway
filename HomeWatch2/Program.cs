using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<HomeWatchDb>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("HomeWatch") ?? "Data Source=homewatch.db"));
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

app.MapGet("/api/status", () => Results.Ok(new
{
    ok = true,
    version = "2.0.0-alpha.2",
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

app.MapPost("/api/devices", async (HomeWatchDb db, Device device) =>
{
    device.Id = Guid.NewGuid();
    device.FirstSeen = DateTime.UtcNow;
    device.LastSeen = device.FirstSeen;
    db.Devices.Add(device);
    await db.SaveChangesAsync();
    return Results.Created($"/api/devices/{device.Id}", device);
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
