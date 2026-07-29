using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
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
    await UpgradeSchemaAsync(db);
    await RepairDuplicateDevicesAsync(db);
}

app.MapGet("/api/status", (ImportState import, IConfiguration configuration, ExternalAdultDomainDatabase adultDb) => Results.Ok(new
{
    ok = true,
    version = "2.0.0-alpha.17",
    commit = BuildCommit(),
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

app.MapGet("/api/dashboard", async (HomeWatchDb db, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
{
    var range = ActivityEndpoints.ValidateRange(from, to);
    if (range.Error is not null) return Results.BadRequest(new { error = range.Error });
    var events = ActivityEndpoints.Filter(db.Events.AsNoTracking(), range.From, range.To);
    var aggregate = await events.GroupBy(_ => 1).Select(g => new
    {
        eventCount = g.Count(), activeDevices = g.Select(x => x.DeviceId).Distinct().Count()
    }).FirstOrDefaultAsync(ct);
    var alerts = db.Alerts.AsNoTracking();
    if (range.From is not null) alerts = alerts.Where(x => x.CreatedAt >= range.From);
    if (range.To is not null) alerts = alerts.Where(x => x.CreatedAt < range.To);
    return Results.Ok(new
    {
        summary = new { deviceCount = await db.Devices.CountAsync(ct), activeDevices = aggregate?.activeDevices ?? 0,
            alertCount = await alerts.CountAsync(x => !x.Acknowledged, ct), eventCount = aggregate?.eventCount ?? 0 },
        generatedAt = UtcIso(DateTime.UtcNow)
    });
});

app.MapGet("/api/devices", async (HomeWatchDb db, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
{
    var range = ActivityEndpoints.ValidateRange(from, to);
    if (range.Error is not null) return Results.BadRequest(new { error = range.Error });
    var filtered = ActivityEndpoints.Filter(db.Events.AsNoTracking(), range.From, range.To);
    var counts = await filtered.GroupBy(x => x.DeviceId).Select(g => new { deviceId = g.Key, count = g.Count() })
        .ToDictionaryAsync(x => x.deviceId, x => x.count, ct);
    var domainCounts = await filtered.GroupBy(x => new { x.DeviceId, x.Domain }).Select(g => new { g.Key.DeviceId, domain = g.Key.Domain, count = g.Count() })
        .OrderByDescending(x => x.count).ToListAsync(ct);
    var topDomains = domainCounts.GroupBy(x => x.DeviceId).ToDictionary(g => g.Key, g => g.Take(5).Select(x => new { x.domain, x.count }).ToList());
    var devices = await db.Devices.AsNoTracking().OrderByDescending(x => x.LastSeen).ToListAsync(ct);
    var result = devices.Select(device => new
    {
        device.Id, device.Name, device.IpAddress, device.MacAddress, device.Vendor,
        firstSeen = UtcIso(device.FirstSeen), lastSeen = UtcIso(device.LastSeen),
        online = device.LastSeen >= DateTime.UtcNow.AddMinutes(-5),
        eventCount = counts.GetValueOrDefault(device.Id),
        topDomains = topDomains.GetValueOrDefault(device.Id) ?? []
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

app.MapGet("/api/alerts", async (HomeWatchDb db) =>
{
    var alerts = await db.Alerts.AsNoTracking().OrderByDescending(x => x.CreatedAt).ToListAsync();
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
app.MapActivityEndpoints();
app.MapGet("/api/activity/history-status", async (HomeWatchDb db, CancellationToken ct) =>
{
    var bounds = await db.Events.AsNoTracking().GroupBy(_ => 1).Select(g => new
    { count = g.Count(), oldest = g.Min(x => (DateTime?)x.Timestamp), newest = g.Max(x => (DateTime?)x.Timestamp) }).FirstOrDefaultAsync(ct);
    var checkpoint = await db.ImportCheckpoints.AsNoTracking().SingleOrDefaultAsync(x => x.Source == "adguard-querylog", ct);
    return Results.Ok(new
    {
        eventCount = bounds?.count ?? 0, oldest = UtcIso(bounds?.oldest), newest = UtcIso(bounds?.newest),
        importer = checkpoint is null ? null : new { checkpoint.Source, highWaterTimestamp = UtcIso(checkpoint.HighWaterTimestamp),
            checkpoint.HighWaterFingerprint, checkpoint.LastBatchCount, checkpoint.RecoveryComplete,
            backfillBefore = UtcIso(checkpoint.BackfillBefore), updatedAt = UtcIso(checkpoint.UpdatedAt) },
        sourceLimitation = "HomeWatch can preserve only DNS events returned by AdGuard Home. If AdGuard has already purged query-log entries, HomeWatch cannot reconstruct them.",
        generatedAt = UtcIso(DateTime.UtcNow)
    });
});
app.MapEndpoints();
app.MapBackupDiagnosticsEndpoints();
// Never let an unknown API route fall through to the SPA shell. Besides giving API
// clients a useful error, this keeps a missing or renamed endpoint from surfacing as
// the misleading "<!doctype html> is not valid JSON" browser error.
app.MapFallback("/api/{**path}", (HttpContext context) => Results.Json(new
{
    error = "HomeWatch API endpoint not found.",
    path = context.Request.Path.Value
}, statusCode: StatusCodes.Status404NotFound));
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
    return parts.Length >= 2 ? string.Join('.', parts[^2], parts[^1]) : domain ?? "";
}
static string? ExtractAlertDomain(string detail)
{
    if (string.IsNullOrWhiteSpace(detail)) return null;
    var match = Regex.Match(detail, @"(?i)(?:first detected site|first site|primary site|domain):\s*([a-z0-9.-]+)");
    return match.Success ? match.Groups[1].Value.TrimEnd('.') : null;
}

static string BuildCommit()
{
    var configured = Environment.GetEnvironmentVariable("HOMEWATCH_COMMIT");
    if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
    try
    {
        var start = new System.Diagnostics.ProcessStartInfo("git", "rev-parse --short HEAD")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null) return "unknown";
        var value = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(2000);
        return process.ExitCode == 0 && value.Length > 0 ? value : "unknown";
    }
    catch { return "unknown"; }
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

static async Task UpgradeSchemaAsync(HomeWatchDb db)
{
    var connection = db.Database.GetDbConnection();
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "PRAGMA table_info('Events');";
    var hasFingerprint = false;
    await using (var reader = await command.ExecuteReaderAsync())
        while (await reader.ReadAsync())
            hasFingerprint |= string.Equals(reader.GetString(1), "Fingerprint", StringComparison.OrdinalIgnoreCase);
    if (!hasFingerprint) await db.Database.ExecuteSqlRawAsync("ALTER TABLE Events ADD COLUMN Fingerprint TEXT NULL;");
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS IX_Events_Fingerprint ON Events (Fingerprint) WHERE Fingerprint IS NOT NULL;");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Events_Timestamp_Id ON Events (Timestamp DESC, Id DESC);");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS IX_Events_DeviceId_Timestamp_Id ON Events (DeviceId, Timestamp DESC, Id DESC);");
    await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS ImportCheckpoints (Source TEXT NOT NULL PRIMARY KEY, HighWaterTimestamp TEXT NULL, HighWaterFingerprint TEXT NULL, UpdatedAt TEXT NOT NULL, LastBatchCount INTEGER NOT NULL, RecoveryComplete INTEGER NOT NULL, BackfillBefore TEXT NULL);");
    command.CommandText = "PRAGMA table_info('ImportCheckpoints');";
    var hasBackfillBefore = false;
    await using (var reader = await command.ExecuteReaderAsync())
        while (await reader.ReadAsync())
            hasBackfillBefore |= string.Equals(reader.GetString(1), "BackfillBefore", StringComparison.OrdinalIgnoreCase);
    if (!hasBackfillBefore) await db.Database.ExecuteSqlRawAsync("ALTER TABLE ImportCheckpoints ADD COLUMN BackfillBefore TEXT NULL;");
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
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
        var checkpoint = await db.ImportCheckpoints.FindAsync(["adguard-querylog"], cancellationToken);
        var batchSize = Math.Clamp(options.BatchSize, 50, 1000);
        var pending = new List<ImportRow>();
        var continuingBackfill = checkpoint is { RecoveryComplete: false, BackfillBefore: not null };
        string? olderThan = continuingBackfill ? checkpoint!.BackfillBefore!.Value.ToString("O") : null;
        var recoveryComplete = false;
        DateTime? oldestFetched = null;

        for (var page = 0; page < Math.Clamp(options.MaxRecoveryPages, 1, 1000); page++)
        {
            var pageRows = await FetchPageAsync(options, batchSize, olderThan, cancellationToken);
            if (pageRows.Count == 0) { recoveryComplete = true; break; }
            pending.AddRange(pageRows);
            var oldest = pageRows.Min(x => x.Timestamp);
            oldestFetched = oldestFetched is null || oldest < oldestFetched ? oldest : oldestFetched;
            if (!continuingBackfill && checkpoint?.HighWaterTimestamp is not null && oldest <= checkpoint.HighWaterTimestamp.Value)
            { recoveryComplete = true; break; }
            if (pageRows.Count < batchSize) { recoveryComplete = true; break; }
            olderThan = oldest.ToString("O");
        }

        var parsedRows = pending.GroupBy(x => x.Fingerprint, StringComparer.Ordinal).Select(x => x.First())
            .OrderBy(x => x.Timestamp).ToList();
        if (parsedRows.Count == 0)
        {
            state.Connected = true; state.LastSuccess = DateTime.UtcNow; state.LastError = null;
            return;
        }

        var clientIps = parsedRows.Select(x => x.ClientIp).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var knownDevices = await db.Devices.Where(x => x.IpAddress != null && clientIps.Contains(x.IpAddress)).ToListAsync(cancellationToken);
        var devicesByIp = knownDevices.Where(x => x.IpAddress != null).ToDictionary(x => x.IpAddress!, StringComparer.OrdinalIgnoreCase);
        var minTimestamp = parsedRows.Min(x => x.Timestamp);
        var maxTimestamp = parsedRows.Max(x => x.Timestamp);
        var existingFingerprints = await db.Events.AsNoTracking()
            .Where(x => x.Timestamp >= minTimestamp && x.Timestamp <= maxTimestamp && x.Fingerprint != null)
            .Select(x => x.Fingerprint!).ToListAsync(cancellationToken);
        var existingFingerprintSet = existingFingerprints.ToHashSet(StringComparer.Ordinal);
        var legacyKeys = await db.Events.AsNoTracking().Where(x => x.Timestamp >= minTimestamp && x.Timestamp <= maxTimestamp)
            .Select(x => new { x.Timestamp, x.DeviceId, x.Domain }).ToListAsync(cancellationToken);
        var existingLegacy = legacyKeys.Select(x => LegacyKey(x.Timestamp, x.DeviceId, x.Domain)).ToHashSet(StringComparer.Ordinal);
        var imported = 0;
        var adultHits = new List<(Guid DeviceId, string Device, string Domain, DateTime Timestamp)>();

        foreach (var item in parsedRows)
        {
            if (!devicesByIp.TryGetValue(item.ClientIp, out var device))
            {
                device = new Device { Id = Guid.NewGuid(), Name = string.IsNullOrWhiteSpace(item.ClientName) ? item.ClientIp : item.ClientName, IpAddress = item.ClientIp, FirstSeen = item.Timestamp, LastSeen = item.Timestamp };
                devicesByIp[item.ClientIp] = device; db.Devices.Add(device);
            }
            else
            {
                if (item.Timestamp > device.LastSeen) device.LastSeen = item.Timestamp;
                if (!string.IsNullOrWhiteSpace(item.ClientName) && device.Name == device.IpAddress) device.Name = item.ClientName;
            }
            if (existingFingerprintSet.Contains(item.Fingerprint) || existingLegacy.Contains(LegacyKey(item.Timestamp, device.Id, item.Domain))) continue;

            var safeOverride = AdultSafetyOverrides.IsSafe(item.Domain);
            var classification = safeOverride
                ? new AdultClassification(false, 100, DomainHelpers.RootDomain(item.Domain), "Marked not adult by user", "user-safe-override")
                : adultIntelligence.Classify(item.Domain);
            var privateRelay = ApplePrivateRelayBlocker.IsPrivateRelayDomain(item.Domain);
            var category = classification.IsAdult ? "adult" : privateRelay ? "privacy-proxy" : safeOverride && (item.Domain.Contains("adbutler") || item.Domain.Contains("scorecardresearch")) ? "advertising" : "dns";
            db.Events.Add(new ActivityEvent { Timestamp = item.Timestamp, DeviceId = device.Id, Device = device, Domain = item.Domain, Category = category, Action = item.Action, Fingerprint = item.Fingerprint, Source = classification.IsAdult ? $"adult-intelligence:{classification.Source}" : safeOverride ? "user-safe-override" : "adguard" });
            existingFingerprintSet.Add(item.Fingerprint); imported++;
            if (classification.IsAdult) adultHits.Add((device.Id, device.Name, item.Domain, item.Timestamp));
        }

        checkpoint ??= new ImportCheckpoint { Source = "adguard-querylog" };
        if (db.Entry(checkpoint).State == EntityState.Detached) db.ImportCheckpoints.Add(checkpoint);
        var newest = parsedRows[^1];
        if (checkpoint.HighWaterTimestamp is null || newest.Timestamp >= checkpoint.HighWaterTimestamp)
        { checkpoint.HighWaterTimestamp = newest.Timestamp; checkpoint.HighWaterFingerprint = newest.Fingerprint; }
        checkpoint.UpdatedAt = DateTime.UtcNow; checkpoint.LastBatchCount = pending.Count; checkpoint.RecoveryComplete = recoveryComplete;
        checkpoint.BackfillBefore = recoveryComplete ? null : oldestFetched;
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
        foreach (var hit in adultHits) await adultSessions.RecordHitAsync(hit.DeviceId, hit.Device, hit.Domain, hit.Timestamp, cancellationToken);
        state.Connected = true; state.LastSuccess = DateTime.UtcNow; state.LastError = recoveryComplete ? null : "Importer recovery page limit reached; backfill will continue."; state.Imported += imported;
    }

    private async Task<List<ImportRow>> FetchPageAsync(AdGuardOptions options, int limit, string? olderThan, CancellationToken cancellationToken)
    {
        var url = $"{options.BaseUrl.TrimEnd('/')}/control/querylog?limit={limit}";
        if (!string.IsNullOrWhiteSpace(olderThan)) url += $"&older_than={Uri.EscapeDataString(olderThan)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(options.Username))
        {
            var raw = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }
        var client = httpClientFactory.CreateClient(); client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (!json.RootElement.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("AdGuard returned no query-log data.");
        var result = new List<ImportRow>();
        foreach (var row in rows.EnumerateArray())
        {
            var domain = ReadString(row, "question", "name").TrimEnd('.').ToLowerInvariant();
            var clientIp = ReadString(row, "client");
            if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(clientIp)
                || !DateTimeOffset.TryParse(ReadString(row, "time"), out var parsed)) continue;
            var action = ReadString(row, "reason") is { Length: > 0 } reason ? reason : "observed";
            var timestamp = parsed.UtcDateTime;
            result.Add(new ImportRow(timestamp, domain, clientIp, ReadString(row, "client_info", "name"), action,
                Fingerprint(timestamp, clientIp, domain, action)));
        }
        return result;
    }

    private static string Fingerprint(DateTime timestamp, string clientIp, string domain, string action)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{timestamp.Ticks}|{clientIp}|{domain}|{action}"));
        return Convert.ToHexString(bytes);
    }

    private static string LegacyKey(DateTime timestamp, Guid deviceId, string domain) => $"{timestamp.Ticks}|{deviceId:N}|{domain}";
    private sealed record ImportRow(DateTime Timestamp, string Domain, string ClientIp, string ClientName, string Action, string Fingerprint);

    private static string RootDomainForWorker(string domain)
    {
        var parts = (domain ?? "").Trim('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? string.Join('.', parts[^2], parts[^1]) : domain ?? "";
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
public sealed class AdGuardOptions { public string BaseUrl { get; set; } = "http://127.0.0.1"; public string Username { get; set; } = ""; public string Password { get; set; } = ""; public int BatchSize { get; set; } = 500; public int MaxRecoveryPages { get; set; } = 100; }
public sealed class NtfyOptions { public string BaseUrl { get; set; } = "https://ntfy.sh"; public string Topic { get; set; } = ""; }

public sealed class HomeWatchDb(DbContextOptions<HomeWatchDb> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>(); public DbSet<ActivityEvent> Events => Set<ActivityEvent>(); public DbSet<ActivitySession> Sessions => Set<ActivitySession>(); public DbSet<Alert> Alerts => Set<Alert>(); public DbSet<ImportCheckpoint> ImportCheckpoints => Set<ImportCheckpoint>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>().HasIndex(x => x.MacAddress).IsUnique(); modelBuilder.Entity<Device>().HasIndex(x => x.IpAddress).IsUnique();
        modelBuilder.Entity<ActivityEvent>().HasIndex(x => x.Timestamp); modelBuilder.Entity<ActivityEvent>().HasIndex(x => new { x.Timestamp, x.Id }); modelBuilder.Entity<ActivityEvent>().HasIndex(x => new { x.DeviceId, x.Timestamp, x.Id }); modelBuilder.Entity<ActivityEvent>().HasIndex(x => x.Fingerprint).IsUnique();
        modelBuilder.Entity<Alert>().HasIndex(x => new { x.Acknowledged, x.CreatedAt }); modelBuilder.Entity<ImportCheckpoint>().HasKey(x => x.Source);
    }
}
public sealed class Device { public Guid Id { get; set; } public string Name { get; set; } = "Unknown device"; public string? IpAddress { get; set; } public string? MacAddress { get; set; } public string? Vendor { get; set; } public DateTime FirstSeen { get; set; } public DateTime LastSeen { get; set; } }
public sealed class ActivityEvent { public long Id { get; set; } public DateTime Timestamp { get; set; } public Guid DeviceId { get; set; } public Device? Device { get; set; } public string Domain { get; set; } = ""; public string Category { get; set; } = "unknown"; public string Action { get; set; } = "observed"; public string Source { get; set; } = "adguard"; public string? Fingerprint { get; set; } }
public sealed class ActivitySession { public Guid Id { get; set; } public Guid DeviceId { get; set; } public DateTime StartedAt { get; set; } public DateTime EndedAt { get; set; } public int Confidence { get; set; } public string Assessment { get; set; } = ""; }
public sealed class Alert { public Guid Id { get; set; } public Guid? SessionId { get; set; } public Guid DeviceId { get; set; } public string Severity { get; set; } = "medium"; public string Title { get; set; } = "Activity alert"; public string Detail { get; set; } = ""; public DateTime CreatedAt { get; set; } public bool Acknowledged { get; set; } public DateTime? AcknowledgedAt { get; set; } }

public sealed class ImportCheckpoint { public string Source { get; set; } = ""; public DateTime? HighWaterTimestamp { get; set; } public string? HighWaterFingerprint { get; set; } public DateTime UpdatedAt { get; set; } public int LastBatchCount { get; set; } public bool RecoveryComplete { get; set; } public DateTime? BackfillBefore { get; set; } }
