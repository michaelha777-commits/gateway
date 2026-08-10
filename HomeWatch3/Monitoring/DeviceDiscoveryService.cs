using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using HomeWatch3.Connectors.Opnsense;
using HomeWatch3.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

public sealed record DeviceDiscoveryRecord(
    long DeviceId,
    string Ip,
    string? ReverseDns,
    string? DeviceType,
    string? OperatingSystem,
    int[] OpenPorts,
    string[] Services,
    string[] Sources,
    int IdentityConfidence,
    DateTime LastScannedUtc,
    string? DhcpHostname = null,
    string? OpnsenseVendor = null,
    string? InterfaceName = null,
    bool RandomizedMac = false,
    string? IdentityReason = null);

public sealed class DeviceDiscoveryService(
    IServiceScopeFactory scopeFactory,
    IOpnsenseClient opnsense,
    IWebHostEnvironment env,
    ILogger<DeviceDiscoveryService> logger) : BackgroundService
{
    private static readonly int[] CommonPorts = [22,53,80,139,443,445,515,554,631,1883,3000,3389,5000,5353,5900,8008,8009,8080,8081,8443,8883,9000,9100,32400];
    private readonly object _gate = new();
    private readonly Dictionary<long,DeviceDiscoveryRecord> _records = new();
    private readonly string _path = Path.Combine(env.ContentRootPath,"data","device-discovery.json");
    public bool Running { get; private set; }
    public DateTime? LastCompletedUtc { get; private set; }
    public string? LastError { get; private set; }

    public DeviceDiscoveryRecord? Get(long id){lock(_gate)return _records.TryGetValue(id,out var r)?r:null;}
    public object Status(){lock(_gate)return new{running=Running,lastCompletedUtc=LastCompletedUtc,lastError=LastError,deviceCount=_records.Count};}
    public bool TryStart(){if(Running)return false;_ = Task.Run(()=>ScanAll(CancellationToken.None));return true;}

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Load();
        try{await Task.Delay(TimeSpan.FromSeconds(20),stoppingToken);}catch{return;}
        while(!stoppingToken.IsCancellationRequested)
        {
            await ScanAll(stoppingToken);
            try{await Task.Delay(TimeSpan.FromMinutes(30),stoppingToken);}catch{break;}
        }
    }

    private async Task ScanAll(CancellationToken ct)
    {
        if(Running)return; Running=true; LastError=null;
        try
        {
            JsonElement leases = default, arp = default;
            try { leases = await opnsense.GetDnsmasqLeasesAsync(ct); } catch(Exception ex) { logger.LogDebug(ex,"Could not read OPNsense DHCP leases during discovery"); }
            try { arp = await opnsense.GetArpAsync(ct); } catch(Exception ex) { logger.LogDebug(ex,"Could not read OPNsense ARP table during discovery"); }
            var leaseRows = Rows(leases).ToArray();
            var arpRows = Rows(arp).ToArray();

            using var scope=scopeFactory.CreateScope();
            var db=scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
            var devices=await db.Devices.AsNoTracking().Where(x=>x.LastIpAddress!=null).OrderByDescending(x=>x.LastSeenUtc).Take(128).ToListAsync(ct);
            foreach(var d in devices)
            {
                ct.ThrowIfCancellationRequested();
                var ip=d.LastIpAddress!;
                var lease = FindRow(leaseRows,d.MacAddress,ip);
                var ar = FindRow(arpRows,d.MacAddress,ip);
                var dhcpHostname = Pick(lease,"hostname","host","name");
                var opnVendor = Pick(ar,"manufacturer","vendor","mac_info") ?? Pick(lease,"mac_info","manufacturer","vendor");
                var iface = Pick(ar,"interface_name","ifname","interface") ?? Pick(lease,"interface","ifname");
                var reverse=await ReverseDns(ip);
                var ports=await ScanPorts(ip,ct);
                var services=ports.Select(ServiceName).Distinct().ToArray();
                var randomized = IsLocallyAdministeredMac(d.MacAddress);
                var inferred=Infer(d.Name,d.Vendor,dhcpHostname,opnVendor,reverse,d.MacAddress,ports);
                var sources=new List<string>{"HomeWatch","OPNsense ARP"};
                if(!string.IsNullOrWhiteSpace(dhcpHostname))sources.Add("OPNsense DHCP");
                if(!string.IsNullOrWhiteSpace(opnVendor))sources.Add("MAC/OUI");
                if(!string.IsNullOrWhiteSpace(reverse))sources.Add("reverse DNS");
                if(ports.Length>0)sources.Add("TCP port probe");
                var confidence=45;
                if(!string.IsNullOrWhiteSpace(d.MacAddress))confidence+=10;
                if(!string.IsNullOrWhiteSpace(dhcpHostname))confidence+=20;
                if(!string.IsNullOrWhiteSpace(opnVendor)||!string.IsNullOrWhiteSpace(d.Vendor))confidence+=10;
                if(!string.IsNullOrWhiteSpace(reverse))confidence+=5;
                if(ports.Length>0)confidence+=5;
                if(!string.IsNullOrWhiteSpace(inferred.Type))confidence+=10;
                if(randomized && string.IsNullOrWhiteSpace(opnVendor)) confidence-=5;
                confidence=Math.Clamp(confidence,0,98);
                var rec=new DeviceDiscoveryRecord(d.Id,ip,reverse,inferred.Type,inferred.Os,ports,services,sources.Distinct().ToArray(),confidence,DateTime.UtcNow,dhcpHostname,opnVendor,iface,randomized,inferred.Reason);
                lock(_gate)_records[d.Id]=rec;
            }
            LastCompletedUtc=DateTime.UtcNow; Save();
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){}
        catch(Exception ex){LastError=ex.GetBaseException().Message;logger.LogWarning(ex,"Device discovery failed");}
        finally{Running=false;}
    }

    private static async Task<string?> ReverseDns(string ip)
    {
        try{return (await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromSeconds(2))).HostName;}catch{return null;}
    }
    private static async Task<int[]> ScanPorts(string ip,CancellationToken ct)
    {
        var open=new List<int>();
        await Parallel.ForEachAsync(CommonPorts,new ParallelOptions{MaxDegreeOfParallelism=12,CancellationToken=ct},async(port,token)=>
        {
            try
            {
                using var c=new TcpClient();
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(350));
                await c.ConnectAsync(ip,port,timeout.Token);
                lock(open)open.Add(port);
            }
            catch{}
        });
        open.Sort();return open.ToArray();
    }
    private static string ServiceName(int p)=>p switch{22=>"SSH",53=>"DNS",80=>"HTTP",139=>"NetBIOS",443=>"HTTPS",445=>"SMB",515=>"LPD printer",554=>"RTSP",631=>"IPP printer",1883=>"MQTT",3000=>"Web app",3389=>"RDP",5000=>"Web/NAS",5353=>"mDNS",5900=>"VNC",8008=>"Cast/HTTP",8009=>"Cast",8080=>"HTTP-alt",8081=>"HTTP-alt",8443=>"HTTPS-alt",8883=>"MQTT TLS",9000=>"Web/service",9100=>"JetDirect printer",32400=>"Plex",_=>$"TCP/{p}"};
    private static (string? Type,string? Os,string? Reason) Infer(string? name,string? vendor,string? dhcp,string? opnVendor,string? reverse,string? mac,int[] ports)
    {
        var hay=$"{name} {vendor} {dhcp} {opnVendor} {reverse}".ToLowerInvariant();
        if(hay.Contains("iphone"))return("iPhone","iOS","OPNsense DHCP/reverse-DNS identity contains iPhone");
        if(hay.Contains("ipad"))return("iPad","iPadOS","OPNsense DHCP/reverse-DNS identity contains iPad");
        if(hay.Contains("macbook")||hay.Contains("imac"))return("Mac","macOS","Hostname identifies an Apple computer");
        if(hay.Contains("pixel"))return("Android phone","Android","Hostname identifies a Google Pixel device");
        if(hay.Contains("galaxy")||hay.Contains("samsung")&&hay.Contains("phone"))return("Android phone","Android / Samsung One UI likely","Hostname/vendor indicates Samsung mobile device");
        if(hay.Contains("android"))return("Android device","Android","Hostname advertises Android");
        if(hay.Contains("samsung")&&(hay.Contains("tv")||ports.Contains(8008)))return("Smart TV","Samsung/Tizen likely","Samsung identity plus TV/Cast service evidence");
        if(hay.Contains("apple")||hay.Contains("iphone")||hay.Contains("ipad"))return("Apple device","iOS/iPadOS/macOS likely","Apple identity signal");
        if(hay.Contains("amazon")||hay.Contains("alexa")||hay.Contains("echo"))return("Smart speaker / Amazon device","Amazon Fire OS/Linux likely","Amazon/Alexa hostname or vendor");
        if(hay.Contains("nest")||(hay.Contains("google")&&ports.Contains(8008)))return("Smart home / Cast device","Google embedded OS likely","Google/Nest identity plus Cast service");
        if(hay.Contains("tp-link")||hay.Contains("deco"))return("Network / smart-home device","Embedded Linux likely","TP-Link/Deco identity");
        if(hay.Contains("windows")||hay.Contains("desktop-")||hay.Contains("laptop-"))return("Windows computer","Windows likely","Windows-style hostname");
        if(ports.Contains(9100)||ports.Contains(631)||ports.Contains(515))return("Printer",null,"Printer service ports detected");
        if(ports.Contains(554))return("Camera / media device","Embedded OS likely","RTSP service detected");
        if(ports.Contains(445)&&ports.Contains(3389))return("Windows computer","Windows likely","SMB and RDP services detected");
        if(ports.Contains(22)&&ports.Contains(5000))return("NAS / server","Linux/Unix likely","SSH and NAS/web service ports detected");
        if(ports.Contains(32400))return("Media server",null,"Plex service detected");
        if(IsLocallyAdministeredMac(mac)&&!string.IsNullOrWhiteSpace(dhcp))return("Personal/mobile device",null,"Private/randomized MAC prevents vendor lookup; DHCP hostname is the strongest identity signal");
        return(null,null,"Insufficient identity signals from DHCP, ARP/OUI, reverse DNS and local service probes");
    }
    private static bool IsLocallyAdministeredMac(string? mac)
    {
        if(string.IsNullOrWhiteSpace(mac))return false;
        var first=mac.Split(':','-').FirstOrDefault();
        return byte.TryParse(first,System.Globalization.NumberStyles.HexNumber,null,out var b)&&(b&0x02)!=0;
    }
    private static IEnumerable<JsonElement> Rows(JsonElement payload)
    {
        if(payload.ValueKind==JsonValueKind.Array)return payload.EnumerateArray().Select(x=>x.Clone());
        if(payload.ValueKind==JsonValueKind.Object&&payload.TryGetProperty("rows",out var rows)&&rows.ValueKind==JsonValueKind.Array)return rows.EnumerateArray().Select(x=>x.Clone());
        return [];
    }
    private static JsonElement? FindRow(IEnumerable<JsonElement> rows,string? mac,string? ip)
    {
        var m=(mac??"").Trim().ToLowerInvariant();
        foreach(var r in rows)
        {
            var rm=(Pick(r,"hwaddr","mac","macaddress","mac_address")??"").Trim().ToLowerInvariant();
            var ri=Pick(r,"address","ip","ipaddress");
            if((m.Length>0&&rm==m)||(!string.IsNullOrWhiteSpace(ip)&&ri==ip))return r;
        }
        return null;
    }
    private static string? Pick(JsonElement? row,params string[] names)
    {
        if(row is null||row.Value.ValueKind!=JsonValueKind.Object)return null;
        foreach(var n in names)if(row.Value.TryGetProperty(n,out var v)){var s=v.ValueKind==JsonValueKind.String?v.GetString():v.ToString();if(!string.IsNullOrWhiteSpace(s)&&s!="*")return s.Trim();}
        return null;
    }
    private void Load(){try{if(!File.Exists(_path))return;var rows=JsonSerializer.Deserialize<DeviceDiscoveryRecord[]>(File.ReadAllText(_path))??[];lock(_gate)foreach(var r in rows)_records[r.DeviceId]=r;}catch(Exception ex){logger.LogDebug(ex,"Could not load discovery cache");}}
    private void Save(){try{Directory.CreateDirectory(Path.GetDirectoryName(_path)!);DeviceDiscoveryRecord[] rows;lock(_gate)rows=_records.Values.OrderBy(x=>x.DeviceId).ToArray();File.WriteAllText(_path,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));}catch(Exception ex){logger.LogDebug(ex,"Could not save discovery cache");}}
}
