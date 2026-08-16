using System.Net;
using System.Text.Json;
using HomeWatch3.Authentication;
using HomeWatch3.Connectors.Ntopng;
using HomeWatch3.Connectors.Opnsense;
using HomeWatch3.Data;
using HomeWatch3.Monitoring;
using HomeWatch3.Notifications;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(builder.Configuration["HomeWatch:ListenUrl"] ?? "http://0.0.0.0:8930");
builder.Services.Configure<NtopngOptions>(builder.Configuration.GetSection(NtopngOptions.SectionName));
builder.Services.Configure<OpnsenseOptions>(builder.Configuration.GetSection(OpnsenseOptions.SectionName));
builder.Services.Configure<NtfyOptions>(builder.Configuration.GetSection(NtfyOptions.SectionName));
builder.Services.Configure<AdultDnsMonitorOptions>(builder.Configuration.GetSection(AdultDnsMonitorOptions.SectionName));
builder.Services.Configure<HomeWatchAuthenticationOptions>(builder.Configuration.GetSection(HomeWatchAuthenticationOptions.SectionName));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});
builder.Services.AddMemoryCache();

var dataPath = builder.Configuration["HomeWatch:DataPath"];
if (string.IsNullOrWhiteSpace(dataPath)) dataPath = Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataPath);

builder.Services.AddDbContext<HomeWatchDb>(options => options.UseSqlite($"Data Source={Path.Combine(dataPath, "homewatch3.db")}"));
builder.Services.AddSingleton<IgnoredDeviceStore>();
builder.Services.AddSingleton<IgnoredDomainStore>();
builder.Services.AddSingleton<NtfyNotificationSettingsStore>();

builder.Services.AddHttpClient<IOpnsenseClient, OpnsenseClient>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpnsenseOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/'));
}).ConfigurePrimaryHttpMessageHandler(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OpnsenseOptions>>().Value;
    return new HttpClientHandler { ServerCertificateCustomValidationCallback = options.AllowInvalidCertificate ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator : null };
});

builder.Services.AddHttpClient<INtopngClient, NtopngClient>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NtopngOptions>>().Value;
    client.BaseAddress = Uri.TryCreate(options.BaseUrl.TrimEnd('/'), UriKind.Absolute, out var baseAddress)
        ? baseAddress
        : new Uri("http://127.0.0.1:3000");
    client.Timeout = TimeSpan.FromSeconds(10);
}).ConfigurePrimaryHttpMessageHandler(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NtopngOptions>>().Value;
    return new HttpClientHandler { ServerCertificateCustomValidationCallback = options.AllowInvalidCertificate ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator : null };
});

builder.Services.AddHttpClient<INtfyService, NtfyService>();
builder.Services.AddHttpClient<IMoneyPilotAuthenticationClient, MoneyPilotAuthenticationClient>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HomeWatchAuthenticationOptions>>().Value;
    client.BaseAddress = options.ResolveMoneyPilotBaseUri();
    client.Timeout = TimeSpan.FromSeconds(10);
}).ConfigurePrimaryHttpMessageHandler(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HomeWatchAuthenticationOptions>>().Value;
    return new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = options.AllowInvalidCertificate
            ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            : null
    };
});
builder.Services.AddSingleton<IAdultDomainClassifier, AdultDomainClassifier>();
builder.Services.AddSingleton<IZenarmorCategoryClassifier, ZenarmorCategoryClassifier>();
builder.Services.AddSingleton<ActivityCorrelationService>();
builder.Services.AddSingleton<AdultDnsMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AdultDnsMonitor>());
builder.Services.AddSingleton<NtopngFlowMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NtopngFlowMonitor>());
builder.Services.AddSingleton<TrafficSessionMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrafficSessionMonitor>());
builder.Services.AddHostedService<NewDeviceMonitor>();
builder.Services.AddV2Enhancements();

var app = builder.Build();
app.UseForwardedHeaders();
app.UseHomeWatchAuthentication();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // HomeWatch is updated in place on the NAS. Never let a browser combine a
        // newly deployed page with an older cached stylesheet or script.
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Context.Response.Headers.Pragma = "no-cache";
        context.Context.Response.Headers.Expires = "0";
    }
});
app.MapV2Enhancements();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
    await db.Database.EnsureCreatedAsync();
    await HomeWatchSchema.EnsureUpgradedAsync(db);
}

app.MapGet("/api/status", (Microsoft.Extensions.Options.IOptions<HomeWatchAuthenticationOptions> authentication) => Results.Ok(new
{
    application = "HomeWatch 3",
    version = "3.0.0-alpha.30",
    utc = DateTime.UtcNow,
    authentication = new { authentication.Value.Enabled, provider = "MoneyPilot" }
}));
app.MapPost("/api/auth/login", async (
    MoneyPilotLoginRequest request,
    HttpContext context,
    IMoneyPilotAuthenticationClient authentication,
    Microsoft.Extensions.Options.IOptions<HomeWatchAuthenticationOptions> configuredOptions,
    CancellationToken ct) =>
{
    var result = await authentication.LoginAsync(request, ct);
    context.Response.Headers.CacheControl = "no-store";
    if (!result.Success || result.Token is null || result.User is null)
    {
        return Results.Json(new { message = result.Message }, statusCode: result.StatusCode);
    }

    HomeWatchAuthenticationMiddleware.WriteSessionCookie(
        context.Response,
        configuredOptions.Value,
        result.Token,
        result.ExpiresInSeconds,
        result.RememberedDevice);

    return Results.Ok(new
    {
        token = result.Token,
        user = result.User,
        expiresInSeconds = result.ExpiresInSeconds,
        rememberedDevice = result.RememberedDevice
    });
});
app.MapPost("/api/auth/forgot-password", async (
    MoneyPilotPasswordResetRequest request,
    HttpContext context,
    IMoneyPilotAuthenticationClient authentication,
    CancellationToken ct) =>
{
    var result = await authentication.ResetPasswordAsync(request, ct);
    context.Response.Headers.CacheControl = "no-store";
    return result.Success
        ? Results.Ok(new { reset = true, message = result.Message })
        : Results.Json(new { message = result.Message }, statusCode: result.StatusCode);
});
app.MapPost("/api/auth/logout", (
    HttpContext context,
    Microsoft.Extensions.Options.IOptions<HomeWatchAuthenticationOptions> configuredOptions) =>
{
    context.Response.Cookies.Delete(configuredOptions.Value.CookieName, new CookieOptions { Path = "/" });
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { signedOut = true });
});
app.MapGet("/api/auth/me", (HttpContext context) => Results.Ok(new { user = context.GetAuthenticatedUser() }));
app.MapGet("/api/ntopng/status", async (INtopngClient client, CancellationToken ct) => Results.Ok(await client.GetHealthAsync(ct)));
app.MapGet("/api/ntopng/dashboard", async (INtopngClient client, CancellationToken ct) => Results.Ok(await client.GetDashboardAsync(ct)));
app.MapGet("/api/ntopng/flows", (NtopngFlowMonitor monitor) => Results.Ok(monitor.GetSnapshot()));
app.MapGet("/api/telemetry/status", (NtopngFlowMonitor monitor) => Results.Ok(monitor.GetStatus()));
app.MapGet("/api/opnsense/status", async (IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetHealthAsync(ct)));
app.MapGet("/api/opnsense/dhcp-leases", async (IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetDnsmasqLeasesAsync(ct)));
app.MapGet("/api/opnsense/unbound/queries", async (IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetUnboundQueriesAsync(ct)));
app.MapGet("/api/opnsense/firewall/states", async (IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetFirewallStatesAsync(ct)));
app.MapGet("/api/opnsense/firewall/log", async (IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetFirewallLogAsync(ct)));
app.MapGet("/api/opnsense/arp", async (IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetArpAsync(ct)));
app.MapGet("/api/opnsense/ndp", async (IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetNdpAsync(ct)));
app.MapGet("/api/opnsense/interfaces/statistics", async (IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetInterfaceStatisticsAsync(ct)));
app.MapGet("/api/opnsense/routes", async (IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetRoutesAsync(ct)));
app.MapGet("/api/opnsense/gateways", async (IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetGatewayStatusAsync(ct)));
app.MapGet("/api/opnsense/system/resources", async (IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetSystemResourcesAsync(ct)));
app.MapGet("/api/opnsense/traffic/top", async (string? interfaces, IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetTrafficTopAsync(interfaces ?? "lan", ct)));
app.MapGet("/api/opnsense/ids/status", async (IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetIdsStatusAsync(ct)));
app.MapGet("/api/opnsense/ids/alerts", async (int limit, string? search, IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetIdsAlertsAsync(limit <= 0 ? 250 : limit, search, ct)));
app.MapGet("/api/opnsense/etpro/status", async (IOpnsenseClient client, CancellationToken ct) => Results.Ok(await client.GetEtProTelemetryStatusAsync(ct)));
app.MapGet("/api/traffic/window", (int seconds, long? deviceId, TrafficSessionMonitor monitor) => Results.Ok(monitor.GetTrafficWindow(seconds, deviceId)));
app.MapGet("/api/traffic/timeline", (int seconds, TrafficSessionMonitor monitor) => Results.Ok(monitor.GetNetworkTrafficTimeline(seconds)));
app.MapGet("/api/video-sessions", async (int minutes, TrafficSessionMonitor monitor, HomeWatchDb db, IgnoredDeviceStore ignored, CancellationToken ct) =>
{
    var sessions = monitor.GetSessions(minutes <= 0 ? 1440 : minutes);
    var ids = sessions.Select(x => x.DeviceId).Distinct().ToArray();
    var devices = await db.Devices.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
    var ignoredIds = ignored.GetIds().ToHashSet();
    var filtered = sessions.Where(s =>
    {
        if (ignoredIds.Contains(s.DeviceId)) return false;
        return !devices.TryGetValue(s.DeviceId, out var d) || !InfrastructureDeviceClassifier.IsInfrastructure(d);
    }).ToArray();
    return Results.Ok(filtered);
});
app.MapGet("/api/exports/adult-history", async (
    int minutes,
    long? deviceId,
    string? timeZone,
    HttpContext context,
    TrafficSessionMonitor monitor,
    HomeWatchDb db,
    IgnoredDeviceStore ignoredDevices,
    IgnoredDomainStore ignoredDomains,
    IAdultDomainClassifier adultClassifier,
    CancellationToken ct) =>
{
    var export = await AdultHistoryExport.BuildAsync(
        minutes <= 0 ? 10080 : minutes,
        deviceId,
        timeZone,
        monitor,
        db,
        ignoredDevices,
        ignoredDomains,
        adultClassifier,
        ct);
    context.Response.Headers.CacheControl = "no-store";
    return Results.File(export.Content, "application/zip", export.FileName);
});
app.MapGet("/api/domains/ignored", (IgnoredDomainStore ignored) => Results.Ok(ignored.GetDomains()));
app.MapPost("/api/domains/ignored", (IgnoredDomainUpdate update, IgnoredDomainStore ignored) =>
{
    var domain = ignored.Add(update.Domain);
    return domain is null ? Results.BadRequest(new { error = "Enter a valid domain." }) : Results.Ok(new { domain });
});
app.MapDelete("/api/domains/ignored", (string domain, IgnoredDomainStore ignored) => { ignored.Remove(domain); return Results.Ok(new { domain }); });

app.MapGet("/api/opnsense/snapshot", async (IOpnsenseClient client, CancellationToken ct) =>
{
    var healthTask = client.GetHealthAsync(ct);
    var dnsTask = SafeOpnsense(() => client.GetUnboundQueriesAsync(ct));
    var leasesTask = SafeOpnsense(() => client.GetDnsmasqLeasesAsync(ct));
    var statesTask = SafeOpnsense(() => client.GetFirewallStatesAsync(ct));
    var logTask = SafeOpnsense(() => client.GetFirewallLogAsync(ct));
    var arpTask = SafeOpnsense(() => client.GetArpAsync(ct));
    var ndpTask = SafeOpnsense(() => client.GetNdpAsync(ct));
    var interfacesTask = SafeOpnsense(() => client.GetInterfaceStatisticsAsync(ct));
    var routesTask = SafeOpnsense(() => client.GetRoutesAsync(ct));
    var gatewaysTask = SafeOpnsense(() => client.GetGatewayStatusAsync(ct));
    var resourcesTask = SafeOpnsense(() => client.GetSystemResourcesAsync(ct));

    await Task.WhenAll(dnsTask, leasesTask, statesTask, logTask, arpTask, ndpTask, interfacesTask, routesTask, gatewaysTask, resourcesTask);
    return Results.Ok(new
    {
        utc = DateTime.UtcNow,
        health = await healthTask,
        unbound = await dnsTask,
        dhcpLeases = await leasesTask,
        firewallStates = await statesTask,
        firewallLog = await logTask,
        arp = await arpTask,
        ndp = await ndpTask,
        interfaces = await interfacesTask,
        routes = await routesTask,
        gateways = await gatewaysTask,
        systemResources = await resourcesTask
    });
});

app.MapPost("/api/opnsense/devices/sync", async (IOpnsenseClient client, HomeWatchDb db, CancellationToken ct) =>
{
    var payload = await client.GetDnsmasqLeasesAsync(ct);
    if (!payload.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return Results.Problem("OPNsense DHCP response did not contain a rows array.", statusCode: 502);
    var now = DateTime.UtcNow; var created = 0; var updated = 0; var skipped = 0;
    foreach (var row in rows.EnumerateArray())
    {
        var mac = GetString(row, "hwaddr")?.Trim().ToLowerInvariant(); var ip = GetString(row, "address")?.Trim();
        var hostname = NormalizeValue(GetString(row, "hostname")); var vendor = NormalizeValue(GetString(row, "mac_info"));
        if (string.IsNullOrWhiteSpace(mac)) { skipped++; continue; }
        var device = await db.Devices.SingleOrDefaultAsync(x => x.MacAddress == mac, ct);
        if (device is null) { db.Devices.Add(new Device { MacAddress = mac, LastIpAddress = ip, Name = hostname, Vendor = vendor, FirstSeenUtc = now, LastSeenUtc = now }); created++; }
        else { device.LastIpAddress = ip; device.LastSeenUtc = now; if (string.IsNullOrWhiteSpace(device.Name) && !string.IsNullOrWhiteSpace(hostname)) device.Name = hostname; if (!string.IsNullOrWhiteSpace(vendor)) device.Vendor = vendor; updated++; }
    }
    await db.SaveChangesAsync(ct); return Results.Ok(new { created, updated, skipped, total = created + updated });
});

app.MapGet("/api/devices", async (HomeWatchDb db, CancellationToken ct) => Results.Ok(await db.Devices.AsNoTracking().OrderBy(x => x.LastIpAddress).ThenBy(x => x.Name).ToListAsync(ct)));
app.MapGet("/api/devices/management", async (HomeWatchDb db, IgnoredDeviceStore ignored, CancellationToken ct) =>
{
    var ignoredIds = ignored.GetIds().ToHashSet();
    var devices = await db.Devices.AsNoTracking().OrderBy(x => x.Name).ThenBy(x => x.LastIpAddress).ToListAsync(ct);
    return Results.Ok(devices.Select(d => new { device = d, ignored = ignoredIds.Contains(d.Id), infrastructure = InfrastructureDeviceClassifier.IsInfrastructure(d) }));
});
app.MapGet("/api/devices/ignored", async (HomeWatchDb db, IgnoredDeviceStore ignored, CancellationToken ct) =>
{
    var ids = ignored.GetIds().ToArray();
    var devices = await db.Devices.AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Name).ThenBy(x => x.LastIpAddress).ToListAsync(ct);
    return Results.Ok(devices);
});
app.MapPut("/api/devices/{id:long}/ignored", async (long id, DeviceIgnoreUpdate update, HomeWatchDb db, IgnoredDeviceStore ignored, CancellationToken ct) =>
{
    var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    if (device is null) return Results.NotFound(new { error = "Device not found" });
    ignored.Set(id, update.Ignored);
    return Results.Ok(new { id, ignored = update.Ignored, device });
});
app.MapPut("/api/devices/{id:long}/name", async (long id, DeviceNameUpdate update, HomeWatchDb db, CancellationToken ct) =>
{
    var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == id, ct); if (device is null) return Results.NotFound(new { error = "Device not found" });
    var name = update.Name?.Trim(); if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "Device name cannot be empty" }); if (name.Length > 80) return Results.BadRequest(new { error = "Device name must be 80 characters or fewer" });
    device.Name = name; await db.SaveChangesAsync(ct); return Results.Ok(device);
});
app.MapGet("/api/device-reviews", async (HomeWatchDb db, CancellationToken ct) =>
{
    var reviews = await db.Alerts.AsNoTracking().Where(x => (x.Type == "new-device" || x.Type == "new-ip") && !x.Acknowledged).OrderByDescending(x => x.CreatedUtc).ToListAsync(ct);
    var ids = reviews.Where(x => x.DeviceId.HasValue).Select(x => x.DeviceId!.Value).Distinct().ToArray();
    var devices = await db.Devices.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
    return Results.Ok(reviews.Select(x => new { review = x, device = x.DeviceId.HasValue && devices.TryGetValue(x.DeviceId.Value, out var d) ? d : null }));
});
app.MapPut("/api/device-reviews/{id:long}/reviewed", async (long id, HomeWatchDb db, CancellationToken ct) =>
{
    var review = await db.Alerts.SingleOrDefaultAsync(x => x.Id == id && (x.Type == "new-device" || x.Type == "new-ip"), ct);
    if (review is null) return Results.NotFound(new { error = "Review item not found" });
    review.Acknowledged = true; review.AcknowledgedUtc = DateTime.UtcNow; await db.SaveChangesAsync(ct); return Results.Ok(review);
});
app.MapGet("/api/devices/{id:long}/details", async (long id, HomeWatchDb db, CancellationToken ct) =>
{
    var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (device is null) return Results.NotFound(new { error = "Device not found" });
    var events = await db.TrafficEvents.AsNoTracking().Where(x => x.DeviceId == id).OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Id).Take(100).ToListAsync(ct);
    var alerts = await db.Alerts.AsNoTracking().Where(x => x.DeviceId == id).OrderByDescending(x => x.CreatedUtc).ThenByDescending(x => x.Id).Take(50).ToListAsync(ct);
    var adultEvents = events.Where(x => x.Category == "Adult").ToList();
    var uniqueDomains = adultEvents.Select(x => x.Domain).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    return Results.Ok(new { device, infrastructure = InfrastructureDeviceClassifier.IsInfrastructure(device), summary = new { recordedEvents = events.Count, adultSignals = adultEvents.Count, adultAlerts = alerts.Count(x => x.Type == "adult-content"), uniqueAdultDomains = uniqueDomains.Length, lastAdultSignalUtc = adultEvents.FirstOrDefault()?.TimestampUtc }, adultDomains = uniqueDomains.Take(25).ToArray(), events, alerts });
});
app.MapGet("/api/devices/{id:long}/ntopng", async (long id, HomeWatchDb db, INtopngClient client, CancellationToken ct) =>
{
    var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    if (device is null) return Results.NotFound(new { error = "Device not found" });
    return Results.Ok(await client.GetDeviceAsync(device.LastIpAddress, device.MacAddress, ct));
});

app.MapGet("/api/adult/activity", async (HomeWatchDb db, IgnoredDeviceStore ignored, int minutes = 30, CancellationToken ct = default) =>
{
    minutes = Math.Clamp(minutes, 1, 1440); var since = DateTime.UtcNow.AddMinutes(-minutes); var ignoredIds = ignored.GetIds().ToHashSet();
    var events = await db.TrafficEvents.AsNoTracking().Where(x => x.Category == "Adult" && x.TimestampUtc >= since && (!x.DeviceId.HasValue || !ignoredIds.Contains(x.DeviceId.Value))).OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Id).Take(250).ToListAsync(ct);
    var deviceIds = events.Where(x => x.DeviceId.HasValue).Select(x => x.DeviceId!.Value).Distinct().ToArray();
    var devices = await db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
    var grouped = events.GroupBy(x => x.DeviceId?.ToString() ?? $"ip:{x.SourceIp ?? "unknown"}").Select(g => { var latest = g.OrderByDescending(x => x.TimestampUtc).First(); Device? device = null; if (latest.DeviceId.HasValue) devices.TryGetValue(latest.DeviceId.Value, out device); return new { deviceId = latest.DeviceId, device = device?.Name ?? latest.SourceIp ?? "Unknown device", ip = latest.SourceIp ?? device?.LastIpAddress, lastSeenUtc = latest.TimestampUtc, domain = latest.Domain, confidence = g.Max(x => x.Confidence), hits = g.Count(), domains = g.Select(x => x.Domain).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(8).ToArray(), source = latest.Source }; }).Where(x => !x.deviceId.HasValue || !devices.TryGetValue(x.deviceId.Value, out var d) || !InfrastructureDeviceClassifier.IsInfrastructure(d)).OrderByDescending(x => x.lastSeenUtc).ToList();
    return Results.Ok(grouped);
});

app.MapGet("/api/monitoring/adult/status", (AdultDnsMonitor monitor) => Results.Ok(monitor.Status));
app.MapGet("/api/notifications/settings", (NtfyNotificationSettingsStore store, Microsoft.Extensions.Options.IOptions<NtfyOptions> configuredOptions) =>
{
    var snapshot = store.GetSnapshot();
    var options = configuredOptions.Value;
    return Results.Ok(new
    {
        configured = options.Enabled && !string.IsNullOrWhiteSpace(options.TopicUrl),
        transportEnabled = options.Enabled,
        snapshot.Enabled,
        snapshot.Events,
        snapshot.Priorities
    });
});
app.MapPut("/api/notifications/settings", (NtfySettingsUpdate update, NtfyNotificationSettingsStore store) =>
{
    try { return Results.Ok(store.Update(update)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPost("/api/notifications/test", async (string? priority, INtfyService ntfy, CancellationToken ct) =>
{
    var allowed = new[] { "min", "low", "default", "high", "max" };
    var selectedPriority = allowed.FirstOrDefault(x => x.Equals(priority, StringComparison.OrdinalIgnoreCase)) ?? "default";
    return (await ntfy.SendTestAsync("HomeWatch 3", $"ntfy test notification • {selectedPriority} priority", selectedPriority, ct))
        ? Results.Ok(new { sent = true, priority = selectedPriority })
        : Results.BadRequest(new { sent = false, error = "ntfy is not configured or did not accept the notification." });
});
app.MapGet("/api/alerts", async (HomeWatchDb db, IgnoredDeviceStore ignored, int limit = 50, CancellationToken ct = default) => { limit = Math.Clamp(limit, 1, 250); var ignoredIds = ignored.GetIds().ToHashSet(); var alerts = await db.Alerts.AsNoTracking().Where(x => x.Type == "adult-content" && (!x.DeviceId.HasValue || !ignoredIds.Contains(x.DeviceId.Value))).OrderByDescending(x => x.CreatedUtc).ThenByDescending(x => x.Id).Take(limit).ToListAsync(ct); return Results.Ok(alerts); });
app.MapGet("/api/events", async (HomeWatchDb db, int limit = 100, CancellationToken ct = default) => { limit = Math.Clamp(limit, 1, 500); return Results.Ok(await db.TrafficEvents.AsNoTracking().OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Id).Take(limit).ToListAsync(ct)); });

app.Run();

static async Task<OpnsenseSnapshotPart> SafeOpnsense(Func<Task<JsonElement>> action)
{
    try { return new(true, await action(), null); }
    catch (Exception ex) { return new(false, null, ex.Message); }
}
static string? GetString(JsonElement element, string property) { if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null; return value.GetString(); }
static string? NormalizeValue(string? value) => string.IsNullOrWhiteSpace(value) || value == "*" ? null : value.Trim();
public sealed record DeviceNameUpdate(string? Name);
public sealed record OpnsenseSnapshotPart(bool Ok, JsonElement? Data, string? Error);
