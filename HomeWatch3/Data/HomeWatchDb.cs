using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Data;

public sealed class HomeWatchDb(DbContextOptions<HomeWatchDb> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<TrafficEvent> TrafficEvents => Set<TrafficEvent>();
    public DbSet<AlertRecord> Alerts => Set<AlertRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>().HasIndex(x => x.MacAddress).IsUnique();
        modelBuilder.Entity<Device>().HasIndex(x => x.LastIpAddress);
        modelBuilder.Entity<TrafficEvent>().HasIndex(x => new { x.TimestampUtc, x.Id });
        modelBuilder.Entity<TrafficEvent>().HasIndex(x => new { x.DeviceId, x.TimestampUtc, x.Id });
        modelBuilder.Entity<TrafficEvent>().HasIndex(x => x.Domain);
        modelBuilder.Entity<TrafficEvent>().HasIndex(x => x.ExternalId).IsUnique();
        modelBuilder.Entity<AlertRecord>().HasIndex(x => new { x.CreatedUtc, x.Id });
    }
}

public sealed class Device
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? MacAddress { get; set; }
    public string? LastIpAddress { get; set; }
    public string? Vendor { get; set; }
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TrafficEvent
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public long? DeviceId { get; set; }
    public string? SourceIp { get; set; }
    public string? DestinationIp { get; set; }
    public int? DestinationPort { get; set; }
    public string? Domain { get; set; }
    public string? ExactUrl { get; set; }
    public string? Application { get; set; }
    public string? Category { get; set; }
    public string? Protocol { get; set; }
    public int? DurationSeconds { get; set; }
    public long? BytesUp { get; set; }
    public long? BytesDown { get; set; }
    public string? Country { get; set; }
    public string Visibility { get; set; } = "hostname";
    public bool Encrypted { get; set; }
    public string? ExternalId { get; set; }
    public string Source { get; set; } = "unknown";
    public int Confidence { get; set; }
    public bool Blocked { get; set; }
}

public sealed class AlertRecord
{
    public long Id { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "info";
    public string Severity { get; set; } = "info";
    public long? DeviceId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool NotificationSent { get; set; }
    public DateTime? NotificationSentUtc { get; set; }
    public bool Acknowledged { get; set; }
    public DateTime? AcknowledgedUtc { get; set; }
}
