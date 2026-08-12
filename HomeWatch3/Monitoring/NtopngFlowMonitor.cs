using System.Security.Cryptography;
using System.Text;
using HomeWatch3.Connectors.Ntopng;
using HomeWatch3.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeWatch3.Monitoring;

public sealed record NtopngFlowMonitorStatus(
    bool Configured,
    bool Available,
    DateTime? LastPollUtc,
    int ActiveFlows,
    int MatchedDeviceFlows,
    int PersistedFlowRecords,
    string? Error);

public sealed class NtopngFlowMonitor(
    IServiceScopeFactory scopeFactory,
    INtopngClient ntopng,
    IOptions<NtopngOptions> options,
    IAdultDomainClassifier adultClassifier,
    IgnoredDeviceStore ignoredDevices,
    IgnoredDomainStore ignoredDomains,
    ILogger<NtopngFlowMonitor> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly int _pollSeconds = Math.Clamp(options.Value.FlowPollSeconds, 5, 60);
    private NtopngFlowSnapshot _snapshot = new(false, false, "Waiting for the first ntopng flow poll.", options.Value.InterfaceId, 0, DateTime.UtcNow, []);
    private NtopngFlowMonitorStatus _status = new(false, false, null, 0, 0, 0, "Waiting for the first ntopng flow poll.");

    public NtopngFlowSnapshot GetSnapshot()
    {
        lock (_gate) return _snapshot;
    }

    public NtopngFlowMonitorStatus GetStatus()
    {
        lock (_gate) return _status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PollAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_pollSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await PollAsync(stoppingToken);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await ntopng.GetActiveFlowsAsync(cancellationToken);
            lock (_gate) _snapshot = snapshot;
            if (!snapshot.Available)
            {
                lock (_gate) _status = new(snapshot.Configured, false, snapshot.ObservedUtc, 0, 0, 0, snapshot.Error);
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
            var devices = await db.Devices.AsNoTracking().ToListAsync(cancellationToken);
            var byIp = devices
                .Where(x => !string.IsNullOrWhiteSpace(x.LastIpAddress))
                .GroupBy(x => x.LastIpAddress!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.OrderByDescending(d => d.LastSeenUtc).First(), StringComparer.OrdinalIgnoreCase);

            var normalized = snapshot.Flows
                .Select(flow => NormalizeFlow(snapshot.InterfaceId, flow, byIp))
                .Where(x => x is not null)
                .Select(x => x!)
                .Where(x => !ignoredDevices.IsIgnored(x.Device.Id))
                .Where(x => string.IsNullOrWhiteSpace(x.Hostname) || !ignoredDomains.IsIgnored(x.Hostname))
                .GroupBy(x => x.ExternalId, StringComparer.Ordinal)
                .Select(x => x.OrderByDescending(y => y.Flow.LastSeenUtc).First())
                .ToArray();

            var externalIds = normalized.Select(x => x.ExternalId).ToArray();
            var existing = externalIds.Length == 0
                ? new Dictionary<string, TrafficEvent>(StringComparer.Ordinal)
                : await db.TrafficEvents
                    .Where(x => x.ExternalId != null && externalIds.Contains(x.ExternalId))
                    .ToDictionaryAsync(x => x.ExternalId!, StringComparer.Ordinal, cancellationToken);

            var changed = 0;
            foreach (var item in normalized)
            {
                if (!existing.TryGetValue(item.ExternalId, out var trafficEvent))
                {
                    trafficEvent = new TrafficEvent { ExternalId = item.ExternalId, Source = "ntopng-flow" };
                    db.TrafficEvents.Add(trafficEvent);
                    existing[item.ExternalId] = trafficEvent;
                }

                Apply(item, trafficEvent);
                changed++;
            }

            if (changed > 0) await db.SaveChangesAsync(cancellationToken);
            lock (_gate)
            {
                _status = new(true, true, snapshot.ObservedUtc, snapshot.Flows.Count, normalized.Length, changed, null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ntopng flow telemetry poll failed");
            lock (_gate) _status = _status with { Available = false, LastPollUtc = DateTime.UtcNow, Error = ex.Message };
        }
    }

    private FlowObservation? NormalizeFlow(int interfaceId, NtopngActiveFlow flow, IReadOnlyDictionary<string, Device> byIp)
    {
        var clientIp = flow.Client.IpAddress?.Trim();
        var serverIp = flow.Server.IpAddress?.Trim();
        var localIsClient = !string.IsNullOrWhiteSpace(clientIp) && byIp.TryGetValue(clientIp, out var clientDevice);
        var localIsServer = !string.IsNullOrWhiteSpace(serverIp) && byIp.TryGetValue(serverIp, out var serverDevice);
        if (!localIsClient && !localIsServer) return null;

        var device = localIsClient ? clientDevice! : serverDevice!;
        var localIp = localIsClient ? clientIp! : serverIp!;
        var remote = localIsClient ? flow.Server : flow.Client;
        var hostname = TelemetryNaming.NormalizeHostname(remote.Name, remote.IpAddress);
        var application = TelemetryNaming.NormalizeApplication(flow.Application);
        var visibility = TelemetryNaming.Visibility(null, hostname, application, remote.IpAddress);
        var encrypted = TelemetryNaming.IsEncrypted(flow.Layer4Protocol, flow.Application, remote.Port);
        var classification = string.IsNullOrWhiteSpace(hostname) ? null : adultClassifier.Classify(hostname);
        var category = classification?.IsAdult == true ? "Adult" : TelemetryNaming.Category(hostname, application);
        var confidence = classification?.IsAdult == true
            ? classification.Confidence
            : visibility == "hostname" ? 88 : visibility == "application" ? 76 : 45;
        var externalId = ExternalId(interfaceId, flow, clientIp, serverIp);
        return new(flow, device, localIp, remote, hostname, application, visibility, encrypted, category, confidence, externalId, localIsClient);
    }

    private static void Apply(FlowObservation item, TrafficEvent target)
    {
        var flow = item.Flow;
        var clientToServerBytes = (long)Math.Round(flow.Bytes * item.Flow.ClientToServerPercent / 100d);
        var serverToClientBytes = Math.Max(0, flow.Bytes - clientToServerBytes);
        target.TimestampUtc = flow.LastSeenUtc;
        target.StartedUtc = flow.FirstSeenUtc;
        target.LastSeenUtc = flow.LastSeenUtc;
        target.DeviceId = item.Device.Id;
        target.SourceIp = item.LocalIp;
        target.DestinationIp = item.Remote.IpAddress;
        target.DestinationPort = item.Remote.Port == 0 ? null : item.Remote.Port;
        target.Domain = item.Hostname;
        target.ExactUrl = null;
        target.Application = item.Application;
        target.Category = item.Category;
        target.Protocol = string.Join("/", new[] { flow.Layer4Protocol, flow.Application }.Where(x => !string.IsNullOrWhiteSpace(x)));
        target.DurationSeconds = Math.Max(flow.DurationSeconds, (int)Math.Max(0, (flow.LastSeenUtc - flow.FirstSeenUtc).TotalSeconds));
        target.BytesUp = item.LocalIsClient ? clientToServerBytes : serverToClientBytes;
        target.BytesDown = item.LocalIsClient ? serverToClientBytes : clientToServerBytes;
        target.Country = item.Remote.Country;
        target.Visibility = item.Visibility;
        target.Encrypted = item.Encrypted;
        target.Confidence = item.Confidence;
        target.Blocked = false;
    }

    private static string ExternalId(int interfaceId, NtopngActiveFlow flow, string? clientIp, string? serverIp)
    {
        var key = string.IsNullOrWhiteSpace(flow.Key) ? flow.HashId : flow.Key;
        var started = new DateTimeOffset(DateTime.SpecifyKind(flow.FirstSeenUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        if (!string.IsNullOrWhiteSpace(key)) return $"ntopng:{interfaceId}:{key}:{started}";
        var raw = $"{interfaceId}|{clientIp}|{flow.Client.Port}|{serverIp}|{flow.Server.Port}|{flow.Layer4Protocol}|{flow.Application}|{started}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..20];
        return $"ntopng:{interfaceId}:{hash}:{started}";
    }

    private sealed record FlowObservation(
        NtopngActiveFlow Flow,
        Device Device,
        string LocalIp,
        NtopngFlowEndpoint Remote,
        string? Hostname,
        string? Application,
        string Visibility,
        bool Encrypted,
        string Category,
        int Confidence,
        string ExternalId,
        bool LocalIsClient);
}
