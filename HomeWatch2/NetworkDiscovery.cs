using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public static class NetworkDiscoveryRegistration
{
    public static IServiceCollection AddHomeWatchNetworkDiscovery(this IServiceCollection services)
    {
        services.AddSingleton<NetworkDiscoveryState>();
        services.AddSingleton<NetworkDiscoveryService>();
        services.AddHostedService<NetworkDiscoveryWorker>();
        return services;
    }

    public static void MapHomeWatchNetworkDiscovery(this WebApplication app)
    {
        app.MapGet("/api/discovery/status", (NetworkDiscoveryState state) => Results.Ok(new
        {
            state.Running,
            state.LastStarted,
            state.LastCompleted,
            state.LastError,
            state.DevicesScanned,
            state.HostsDiscovered,
            state.NmapAvailable,
            state.LastMode
        }));

        app.MapPost("/api/discovery/scan", async (bool full, NetworkDiscoveryService discovery, CancellationToken ct) =>
        {
            if (!discovery.TryStart(full)) return Results.Conflict(new { error = "A network scan is already running." });
            await Task.Yield();
            return Results.Accepted(value: new { started = true, mode = full ? "full" : "quick" });
        });

        app.MapGet("/api/devices/{id:guid}/discovery", async (Guid id, NetworkDiscoveryService discovery, CancellationToken ct) =>
        {
            var item = await discovery.GetDeviceDiscoveryAsync(id, ct);
            return item is null ? Results.NotFound(new { error = "No discovery data exists for this device yet." }) : Results.Ok(item);
        });
    }
}

public sealed class NetworkDiscoveryWorker(NetworkDiscoveryService discovery, ILogger<NetworkDiscoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (discovery.TryStart(full: true)) { }
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Network discovery scheduler failed");
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }
    }
}

public sealed class NetworkDiscoveryState
{
    public bool Running { get; set; }
    public DateTime? LastStarted { get; set; }
    public DateTime? LastCompleted { get; set; }
    public string? LastError { get; set; }
    public int DevicesScanned { get; set; }
    public int HostsDiscovered { get; set; }
    public bool NmapAvailable { get; set; }
    public string LastMode { get; set; } = "none";
}

public sealed class NetworkDiscoveryService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    NetworkDiscoveryState state,
    ILogger<NetworkDiscoveryService> logger)
{
    private readonly object gate = new();
    private static readonly int[] CommonPorts =
    {
        21,22,23,25,53,80,110,139,143,443,445,515,554,631,993,995,1883,2869,3000,3389,
        5000,5353,5900,5985,5986,62078,8008,8009,8080,8081,8443,8883,9000,9100,32400
    };

    public bool TryStart(bool full)
    {
        lock (gate)
        {
            if (state.Running) return false;
            state.Running = true;
            state.LastStarted = DateTime.UtcNow;
            state.LastError = null;
            state.LastMode = full ? "full" : "quick";
            _ = Task.Run(() => ScanAsync(full, CancellationToken.None));
            return true;
        }
    }

    public async Task<object?> GetDeviceDiscoveryAsync(Guid deviceId, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        await using var connection = NewConnection();
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Hostname,NetBiosName,DeviceType,OperatingSystem,Manufacturer,OpenPortsJson,ServicesJson,WebInterfacesJson,DiscoverySources,LastScanned FROM DeviceDiscovery WHERE DeviceId=$id";
        command.Parameters.AddWithValue("$id", deviceId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new
        {
            hostname = Null(reader, 0), netBiosName = Null(reader, 1), deviceType = Null(reader, 2), operatingSystem = Null(reader, 3), manufacturer = Null(reader, 4),
            openPorts = ParseJson(reader.GetString(5)), services = ParseJson(reader.GetString(6)), webInterfaces = ParseJson(reader.GetString(7)),
            discoverySources = Null(reader, 8), lastScanned = Null(reader, 9)
        };
    }

    private async Task ScanAsync(bool full, CancellationToken ct)
    {
        try
        {
            await EnsureTableAsync(ct);
            var nmap = FindExecutable("nmap.exe") ?? FindExecutable("nmap");
            state.NmapAvailable = nmap is not null;
            var arp = await ReadArpTableAsync();
            var hosts = await DiscoverSubnetHostsAsync(ct);
            state.HostsDiscovered = hosts.Count;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
            var devices = await db.Devices.ToListAsync(ct);
            var byIp = devices.Where(x => !string.IsNullOrWhiteSpace(x.IpAddress)).ToDictionary(x => x.IpAddress!, StringComparer.OrdinalIgnoreCase);

            foreach (var ip in hosts)
            {
                if (!byIp.TryGetValue(ip, out var device))
                {
                    device = new Device { Id = Guid.NewGuid(), Name = ip, IpAddress = ip, FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow };
                    db.Devices.Add(device);
                    byIp[ip] = device;
                }
                else device.LastSeen = DateTime.UtcNow;
                if (string.IsNullOrWhiteSpace(device.MacAddress) && arp.TryGetValue(ip, out var mac)) device.MacAddress = mac;
            }
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);

            var targets = byIp.Values.Where(x => !string.IsNullOrWhiteSpace(x.IpAddress)).OrderByDescending(x => x.LastSeen).Take(128).ToList();
            state.DevicesScanned = 0;
            foreach (var device in targets)
            {
                ct.ThrowIfCancellationRequested();
                var result = await FingerprintAsync(device.IpAddress!, full, nmap, ct);
                if (arp.TryGetValue(device.IpAddress!, out var mac) && string.IsNullOrWhiteSpace(device.MacAddress)) device.MacAddress = mac;
                if (string.IsNullOrWhiteSpace(device.Vendor) && !string.IsNullOrWhiteSpace(result.Manufacturer)) device.Vendor = result.Manufacturer;
                if (device.Name == device.IpAddress && !string.IsNullOrWhiteSpace(result.BestName)) device.Name = result.BestName!;
                await SaveDiscoveryAsync(device.Id, result, ct);
                state.DevicesScanned++;
            }
            if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
            state.LastCompleted = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            state.LastError = ex.Message;
            logger.LogWarning(ex, "Network discovery failed");
        }
        finally
        {
            lock (gate) state.Running = false;
        }
    }

    private async Task<DiscoveryResult> FingerprintAsync(string ip, bool full, string? nmap, CancellationToken ct)
    {
        var result = new DiscoveryResult();
        result.Hostname = await ReverseDnsAsync(ip);
        result.NetBiosName = await NetBiosNameAsync(ip);
        result.Sources.Add("DNS/LLMNR/NetBIOS");

        if (full && nmap is not null)
        {
            try
            {
                var nmapResult = await RunNmapAsync(nmap, ip, ct);
                result.Merge(nmapResult);
                result.Sources.Add("Nmap");
            }
            catch (Exception ex) { logger.LogDebug(ex, "Nmap fingerprint failed for {Ip}", ip); }
        }

        if (result.OpenPorts.Count == 0)
        {
            result.OpenPorts = await ScanCommonPortsAsync(ip, ct);
            result.Sources.Add("TCP connect scan");
        }

        foreach (var port in result.OpenPorts.Where(p => p is 80 or 443 or 3000 or 5000 or 8008 or 8080 or 8081 or 8443 or 9000))
        {
            var web = await ProbeWebAsync(ip, port, ct);
            if (web is not null) result.WebInterfaces.Add(web);
        }
        InferDevice(result);
        return result;
    }

    private async Task<HashSet<string>> DiscoverSubnetHostsAsync(CancellationToken ct)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localIps = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address).Distinct().ToList();

        foreach (var local in localIps)
        {
            var bytes = local.GetAddressBytes();
            var tasks = Enumerable.Range(1, 254).Select(async last =>
            {
                var ip = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.{last}";
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(ip, 250);
                    if (reply.Status == IPStatus.Success) lock (hosts) hosts.Add(ip);
                }
                catch { }
            });
            await Task.WhenAll(tasks);
            hosts.Add(local.ToString());
        }
        return hosts;
    }

    private static async Task<Dictionary<string,string>> ReadArpTableAsync()
    {
        var output = await RunProcessAsync("arp", "-a", 8000, CancellationToken.None);
        var map = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(output, @"(?m)^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9a-fA-F-]{17})\s+"))
            map[match.Groups[1].Value] = match.Groups[2].Value.Replace('-', ':').ToUpperInvariant();
        return map;
    }

    private static async Task<string?> ReverseDnsAsync(string ip)
    {
        try { var entry = await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromSeconds(2)); return entry.HostName; } catch { return null; }
    }

    private static async Task<string?> NetBiosNameAsync(string ip)
    {
        try
        {
            var output = await RunProcessAsync("nbtstat", $"-A {ip}", 4000, CancellationToken.None);
            var match = Regex.Match(output, @"(?m)^\s*([^\s<]{1,15})\s+<00>\s+UNIQUE");
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        catch { return null; }
    }

    private static async Task<List<int>> ScanCommonPortsAsync(string ip, CancellationToken ct)
    {
        var open = new List<int>();
        await Parallel.ForEachAsync(CommonPorts, new ParallelOptions { MaxDegreeOfParallelism = 20, CancellationToken = ct }, async (port, token) =>
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(ip, port, token).WaitAsync(TimeSpan.FromMilliseconds(500), token);
                lock (open) open.Add(port);
            }
            catch { }
        });
        open.Sort();
        return open;
    }

    private async Task<WebInterface?> ProbeWebAsync(string ip, int port, CancellationToken ct)
    {
        try
        {
            var scheme = port is 443 or 8443 ? "https" : "http";
            var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_,_,_,_) => true, AllowAutoRedirect = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync($"{scheme}://{ip}:{port}/", ct);
            var html = await response.Content.ReadAsStringAsync(ct);
            var title = Regex.Match(html, @"(?is)<title[^>]*>(.*?)</title>").Groups[1].Value;
            title = Regex.Replace(WebUtility.HtmlDecode(title), @"\s+", " ").Trim();
            return new WebInterface(port, scheme, title, response.Headers.Server?.ToString(), (int)response.StatusCode);
        }
        catch { return null; }
    }

    private static async Task<DiscoveryResult> RunNmapAsync(string nmap, string ip, CancellationToken ct)
    {
        var xml = await RunProcessAsync(nmap, $"-Pn -sT -sV --version-light --top-ports 100 -O --osscan-limit -T4 -oX - {ip}", 90000, ct);
        var doc = XDocument.Parse(xml);
        var host = doc.Descendants("host").FirstOrDefault();
        var result = new DiscoveryResult();
        if (host is null) return result;
        var address = host.Elements("address").FirstOrDefault(x => (string?)x.Attribute("addrtype") == "mac");
        result.Manufacturer = (string?)address?.Attribute("vendor");
        result.Hostname = (string?)host.Descendants("hostname").FirstOrDefault()?.Attribute("name");
        result.OperatingSystem = (string?)host.Descendants("osmatch").OrderByDescending(x => (int?)x.Attribute("accuracy") ?? 0).FirstOrDefault()?.Attribute("name");
        foreach (var port in host.Descendants("port"))
        {
            if ((string?)port.Element("state")?.Attribute("state") != "open") continue;
            var number = (int?)port.Attribute("portid");
            if (number is null) continue;
            result.OpenPorts.Add(number.Value);
            var service = port.Element("service");
            result.Services.Add(new ServiceInfo(number.Value, (string?)service?.Attribute("name"), (string?)service?.Attribute("product"), (string?)service?.Attribute("version"), (string?)service?.Attribute("extrainfo")));
        }
        return result;
    }

    private static void InferDevice(DiscoveryResult result)
    {
        var text = string.Join(' ', new[] { result.Hostname, result.NetBiosName, result.OperatingSystem, result.Manufacturer }.Where(x => !string.IsNullOrWhiteSpace(x))).ToLowerInvariant();
        var ports = result.OpenPorts;
        result.DeviceType = text.Contains("iphone") || text.Contains("ipad") ? "Apple mobile device" :
            text.Contains("android") ? "Android device" :
            text.Contains("printer") || ports.Contains(9100) || ports.Contains(631) || ports.Contains(515) ? "Printer" :
            text.Contains("roku") || text.Contains("chromecast") || ports.Contains(8008) || ports.Contains(8009) ? "Streaming device" :
            text.Contains("tv") || text.Contains("bravia") || text.Contains("webos") ? "Smart TV" :
            ports.Contains(554) ? "Camera / media device" :
            ports.Contains(445) || ports.Contains(3389) || text.Contains("windows") ? "Windows computer" :
            ports.Contains(22) && (ports.Contains(80) || ports.Contains(443)) ? "Server / NAS / network appliance" :
            ports.Contains(53) ? "Router / DNS appliance" : "Network device";
    }

    private async Task SaveDiscoveryAsync(Guid deviceId, DiscoveryResult result, CancellationToken ct)
    {
        await using var connection = NewConnection();
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = @"INSERT INTO DeviceDiscovery(DeviceId,Hostname,NetBiosName,DeviceType,OperatingSystem,Manufacturer,OpenPortsJson,ServicesJson,WebInterfacesJson,DiscoverySources,LastScanned)
VALUES($id,$hostname,$netbios,$type,$os,$manufacturer,$ports,$services,$web,$sources,$scanned)
ON CONFLICT(DeviceId) DO UPDATE SET Hostname=excluded.Hostname,NetBiosName=excluded.NetBiosName,DeviceType=excluded.DeviceType,OperatingSystem=excluded.OperatingSystem,Manufacturer=excluded.Manufacturer,OpenPortsJson=excluded.OpenPortsJson,ServicesJson=excluded.ServicesJson,WebInterfacesJson=excluded.WebInterfacesJson,DiscoverySources=excluded.DiscoverySources,LastScanned=excluded.LastScanned";
        command.Parameters.AddWithValue("$id", deviceId.ToString());
        command.Parameters.AddWithValue("$hostname", (object?)result.Hostname ?? DBNull.Value);
        command.Parameters.AddWithValue("$netbios", (object?)result.NetBiosName ?? DBNull.Value);
        command.Parameters.AddWithValue("$type", (object?)result.DeviceType ?? DBNull.Value);
        command.Parameters.AddWithValue("$os", (object?)result.OperatingSystem ?? DBNull.Value);
        command.Parameters.AddWithValue("$manufacturer", (object?)result.Manufacturer ?? DBNull.Value);
        command.Parameters.AddWithValue("$ports", JsonSerializer.Serialize(result.OpenPorts.Distinct().OrderBy(x => x)));
        command.Parameters.AddWithValue("$services", JsonSerializer.Serialize(result.Services));
        command.Parameters.AddWithValue("$web", JsonSerializer.Serialize(result.WebInterfaces));
        command.Parameters.AddWithValue("$sources", string.Join(", ", result.Sources.Distinct()));
        command.Parameters.AddWithValue("$scanned", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        await using var connection = NewConnection();
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = @"CREATE TABLE IF NOT EXISTS DeviceDiscovery(
DeviceId TEXT PRIMARY KEY, Hostname TEXT NULL, NetBiosName TEXT NULL, DeviceType TEXT NULL, OperatingSystem TEXT NULL, Manufacturer TEXT NULL,
OpenPortsJson TEXT NOT NULL DEFAULT '[]', ServicesJson TEXT NOT NULL DEFAULT '[]', WebInterfacesJson TEXT NOT NULL DEFAULT '[]',
DiscoverySources TEXT NULL, LastScanned TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(ct);
    }

    private SqliteConnection NewConnection()
    {
        var raw = configuration.GetConnectionString("HomeWatch") ?? "Data Source=homewatch.db";
        return new SqliteConnection(raw);
    }

    private static object ParseJson(string json)
    {
        try { return JsonSerializer.Deserialize<object>(json) ?? Array.Empty<object>(); } catch { return Array.Empty<object>(); }
    }
    private static string? Null(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Select(p => Path.Combine(p.Trim(), name)).FirstOrDefault(File.Exists);
    }
    private static async Task<string> RunProcessAsync(string file, string arguments, int timeoutMs, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct);
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException(error.Trim());
        return output;
    }
}

public sealed class DiscoveryResult
{
    public string? Hostname { get; set; }
    public string? NetBiosName { get; set; }
    public string? DeviceType { get; set; }
    public string? OperatingSystem { get; set; }
    public string? Manufacturer { get; set; }
    public string? BestName => Hostname ?? NetBiosName;
    public List<int> OpenPorts { get; set; } = new();
    public List<ServiceInfo> Services { get; set; } = new();
    public List<WebInterface> WebInterfaces { get; set; } = new();
    public List<string> Sources { get; set; } = new();
    public void Merge(DiscoveryResult other)
    {
        Hostname ??= other.Hostname; NetBiosName ??= other.NetBiosName; OperatingSystem ??= other.OperatingSystem; Manufacturer ??= other.Manufacturer;
        OpenPorts = OpenPorts.Concat(other.OpenPorts).Distinct().OrderBy(x => x).ToList();
        Services.AddRange(other.Services); WebInterfaces.AddRange(other.WebInterfaces); Sources.AddRange(other.Sources);
    }
}
public sealed record ServiceInfo(int Port, string? Name, string? Product, string? Version, string? ExtraInfo);
public sealed record WebInterface(int Port, string Scheme, string? Title, string? Server, int StatusCode);
