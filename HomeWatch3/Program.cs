using System.Text.Json;
using HomeWatch3.Connectors.Opnsense;
using HomeWatch3.Data;
using HomeWatch3.Notifications;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(builder.Configuration["HomeWatch:ListenUrl"] ?? "http://0.0.0.0:8930");

builder.Services.Configure<OpnsenseOptions>(builder.Configuration.GetSection(OpnsenseOptions.SectionName));
builder.Services.Configure<NtfyOptions>(builder.Configuration.GetSection(NtfyOptions.SectionName));

var dataPath = builder.Configuration["HomeWatch:DataPath"];
if (string.IsNullOrWhiteSpace(dataPath))
    dataPath = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataPath);

builder.Services.AddDbContext<HomeWatchDb>(options =>
    options.UseSqlite($"Data Source={Path.Combine(dataPath, "homewatch3.db")}"));

builder.Services.AddHttpClient<IOpnsenseClient, OpnsenseClient>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpnsenseOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/'));
}).ConfigurePrimaryHttpMessageHandler(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpnsenseOptions>>().Value;
    return new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = options.AllowInvalidCertificate
            ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            : null
    };
});

builder.Services.AddHttpClient<INtfyService, NtfyService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
    await db.Database.EnsureCreatedAsync();
}

app.MapGet("/api/status", () => Results.Ok(new
{
    application = "HomeWatch 3",
    version = "3.0.0-alpha.4",
    utc = DateTime.UtcNow
}));

app.MapGet("/api/opnsense/status", async (IOpnsenseClient client, CancellationToken ct) =>
    Results.Ok(await client.GetHealthAsync(ct)));

app.MapGet("/api/opnsense/dhcp-leases", async (IOpnsenseClient client, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await client.GetDnsmasqLeasesAsync(ct));
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(ex.Message, statusCode: ex.StatusCode is null ? 502 : (int)ex.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

app.MapGet("/api/opnsense/unbound/queries", async (IOpnsenseClient client, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await client.GetUnboundQueriesAsync(ct));
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(ex.Message, statusCode: ex.StatusCode is null ? 502 : (int)ex.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/opnsense/devices/sync", async (IOpnsenseClient client, HomeWatchDb db, CancellationToken ct) =>
{
    try
    {
        var payload = await client.GetDnsmasqLeasesAsync(ct);
        if (!payload.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return Results.Problem("OPNsense DHCP response did not contain a rows array.", statusCode: 502);

        var now = DateTime.UtcNow;
        var created = 0;
        var updated = 0;
        var skipped = 0;

        foreach (var row in rows.EnumerateArray())
        {
            var mac = GetString(row, "hwaddr")?.Trim().ToLowerInvariant();
            var ip = GetString(row, "address")?.Trim();
            var hostname = NormalizeValue(GetString(row, "hostname"));
            var vendor = NormalizeValue(GetString(row, "mac_info"));

            if (string.IsNullOrWhiteSpace(mac))
            {
                skipped++;
                continue;
            }

            var device = await db.Devices.SingleOrDefaultAsync(x => x.MacAddress == mac, ct);
            if (device is null)
            {
                db.Devices.Add(new Device
                {
                    MacAddress = mac,
                    LastIpAddress = ip,
                    Name = hostname,
                    Vendor = vendor,
                    FirstSeenUtc = now,
                    LastSeenUtc = now
                });
                created++;
            }
            else
            {
                device.LastIpAddress = ip;
                device.LastSeenUtc = now;
                if (string.IsNullOrWhiteSpace(device.Name) && !string.IsNullOrWhiteSpace(hostname))
                    device.Name = hostname;
                if (!string.IsNullOrWhiteSpace(vendor))
                    device.Vendor = vendor;
                updated++;
            }
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { created, updated, skipped, total = created + updated });
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem(ex.Message, statusCode: ex.StatusCode is null ? 502 : (int)ex.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

app.MapGet("/api/devices", async (HomeWatchDb db, CancellationToken ct) =>
{
    var devices = await db.Devices.AsNoTracking()
        .OrderBy(x => x.LastIpAddress)
        .ThenBy(x => x.Name)
        .ToListAsync(ct);
    return Results.Ok(devices);
});

app.MapPost("/api/notifications/test", async (INtfyService ntfy, CancellationToken ct) =>
{
    var sent = await ntfy.SendAsync("HomeWatch 3", "HomeWatch 3 ntfy test notification", "default", ct);
    return sent ? Results.Ok(new { sent = true }) : Results.BadRequest(new { sent = false });
});

app.MapGet("/api/events", async (HomeWatchDb db, int limit = 100, CancellationToken ct = default) =>
{
    limit = Math.Clamp(limit, 1, 500);
    var events = await db.TrafficEvents.AsNoTracking()
        .OrderByDescending(x => x.TimestampUtc)
        .ThenByDescending(x => x.Id)
        .Take(limit)
        .ToListAsync(ct);
    return Results.Ok(events);
});

app.Run();

static string? GetString(JsonElement element, string property)
{
    if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        return null;
    return value.GetString();
}

static string? NormalizeValue(string? value)
{
    if (string.IsNullOrWhiteSpace(value) || value == "*")
        return null;
    return value.Trim();
}
