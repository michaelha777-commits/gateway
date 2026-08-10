using System.Net;
using System.Net.Sockets;
using System.Text.Json;
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
    DateTime LastScannedUtc);

public sealed class DeviceDiscoveryService(
    IServiceScopeFactory scopeFactory,
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
            using var scope=scopeFactory.CreateScope();
            var db=scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
            var devices=await db.Devices.AsNoTracking().Where(x=>x.LastIpAddress!=null).OrderByDescending(x=>x.LastSeenUtc).Take(128).ToListAsync(ct);
            foreach(var d in devices)
            {
                ct.ThrowIfCancellationRequested();
                var ip=d.LastIpAddress!;
                var reverse=await ReverseDns(ip);
                var ports=await ScanPorts(ip,ct);
                var services=ports.Select(ServiceName).Distinct().ToArray();
                var inferred=Infer(d.Name,d.Vendor,reverse,ports);
                var sources=new List<string>{"HomeWatch","OPNsense identity"};
                if(!string.IsNullOrWhiteSpace(reverse))sources.Add("reverse DNS");
                if(ports.Length>0)sources.Add("TCP port probe");
                var confidence=Math.Clamp(45 + (!string.IsNullOrWhiteSpace(d.MacAddress)?15:0)+(!string.IsNullOrWhiteSpace(d.Vendor)?15:0)+(!string.IsNullOrWhiteSpace(reverse)?10:0)+(ports.Length>0?10:0),0,95);
                var rec=new DeviceDiscoveryRecord(d.Id,ip,reverse,inferred.Type,inferred.Os,ports,services,sources.ToArray(),confidence,DateTime.UtcNow);
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
            try{using var c=new TcpClient();await c.ConnectAsync(ip,port,token).WaitAsync(TimeSpan.FromMilliseconds(350),token);lock(open)open.Add(port);}catch{}
        });
        open.Sort();return open.ToArray();
    }
    private static string ServiceName(int p)=>p switch{22=>"SSH",53=>"DNS",80=>"HTTP",139=>"NetBIOS",443=>"HTTPS",445=>"SMB",515=>"LPD printer",554=>"RTSP",631=>"IPP printer",1883=>"MQTT",3000=>"Web app",3389=>"RDP",5000=>"Web/NAS",5353=>"mDNS",5900=>"VNC",8008=>"Cast/HTTP",8009=>"Cast",8080=>"HTTP-alt",8081=>"HTTP-alt",8443=>"HTTPS-alt",8883=>"MQTT TLS",9000=>"Web/service",9100=>"JetDirect printer",32400=>"Plex",_=>$"TCP/{p}"};
    private static (string? Type,string? Os) Infer(string? name,string? vendor,string? reverse,int[] ports)
    {
        var hay=$"{name} {vendor} {reverse}".ToLowerInvariant();
        if(hay.Contains("samsung")&&(hay.Contains("tv")||ports.Contains(8008)))return("Smart TV","Samsung/Tizen likely");
        if(hay.Contains("apple")||hay.Contains("iphone")||hay.Contains("ipad"))return("Apple device","iOS/iPadOS/macOS likely");
        if(hay.Contains("amazon")||hay.Contains("alexa")||hay.Contains("echo"))return("Smart speaker / Amazon device","Amazon Fire OS/Linux likely");
        if(hay.Contains("nest")||hay.Contains("google")&&ports.Contains(8008))return("Smart home / Cast device","Google embedded OS likely");
        if(hay.Contains("tp-link")||hay.Contains("deco"))return("Network / smart-home device","Embedded Linux likely");
        if(ports.Contains(9100)||ports.Contains(631)||ports.Contains(515))return("Printer",null);
        if(ports.Contains(554))return("Camera / media device","Embedded OS likely");
        if(ports.Contains(445)&&ports.Contains(3389))return("Windows computer","Windows likely");
        if(ports.Contains(22)&&ports.Contains(5000))return("NAS / server","Linux/Unix likely");
        if(ports.Contains(32400))return("Media server",null);
        return(null,null);
    }
    private void Load(){try{if(!File.Exists(_path))return;var rows=JsonSerializer.Deserialize<DeviceDiscoveryRecord[]>(File.ReadAllText(_path))??[];lock(_gate)foreach(var r in rows)_records[r.DeviceId]=r;}catch(Exception ex){logger.LogDebug(ex,"Could not load discovery cache");}}
    private void Save(){try{Directory.CreateDirectory(Path.GetDirectoryName(_path)!);DeviceDiscoveryRecord[] rows;lock(_gate)rows=_records.Values.OrderBy(x=>x.DeviceId).ToArray();File.WriteAllText(_path,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));}catch(Exception ex){logger.LogDebug(ex,"Could not save discovery cache");}}
}
