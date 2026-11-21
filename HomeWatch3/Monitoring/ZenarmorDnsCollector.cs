using System.Diagnostics;
using HomeWatch3.Data;
using HomeWatch3.Notifications;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HomeWatch3.Monitoring;

public sealed class ZenarmorDnsCollector(IConfiguration config, INtfyService ntfy, ILogger<ZenarmorDnsCollector> log) : BackgroundService
{
    private static readonly string[] Sources = ["dns","connections","security-tags","http","tls","alerts","sip"];
    private readonly Dictionary<string,(int Failures,DateTime Retry,string? Error)> _errors = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private string _state = "Waiting";
    private DateTime? _retry;
    private readonly string _directory = config["HomeWatch:DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data");
    private volatile bool _enabled;
    private bool Enabled => _enabled;
    private string SettingsPath => Path.Combine(_directory, "zenarmor-enabled.json");

    public override Task StartAsync(CancellationToken ct)
    {
        Initialize();
        _enabled = File.Exists(SettingsPath) ? JsonSerializer.Deserialize<bool>(File.ReadAllText(SettingsPath))
            : config.GetValue("Zenarmor:Enabled", false);
        return base.StartAsync(ct);
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            var pending=SettingsPath+".tmp";
            File.WriteAllText(pending,JsonSerializer.Serialize(enabled));
            File.Move(pending,SettingsPath,true);
            _enabled=enabled;
        }
        Wake();
    }

    private int Interval => Math.Clamp(config.GetValue("Zenarmor:PollSeconds", 60), 5, 3600);
    private int Batch => Math.Clamp(config.GetValue("Zenarmor:BatchSize", 250), 1, 500);
    private string DbPath => Path.Combine(_directory, "zenarmor.db");

    public void Wake()
    {
        lock (_gate) if (_wake.CurrentCount == 0) _wake.Release();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString());
        db.Open();
        return db;
    }

    private void Initialize()
    {
        Directory.CreateDirectory(_directory);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        if (version == 1) return;
        if (version != 0) throw new InvalidOperationException("Unsupported Zenarmor database version.");
        using var tx = db.BeginTransaction();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE checkpoint(source TEXT PRIMARY KEY, generation TEXT, epoch INTEGER NOT NULL DEFAULT 0,
                cursor INTEGER NOT NULL DEFAULT 0, anchor INTEGER, synced TEXT, imported INTEGER NOT NULL DEFAULT 0);
            INSERT INTO checkpoint(source) VALUES ('dns'),('connections'),('security-tags'),('http'),('tls'),('alerts'),('sip');
            CREATE TABLE dns(source TEXT NOT NULL,epoch INTEGER NOT NULL, id INTEGER NOT NULL, stamp INTEGER NOT NULL,
                domain TEXT NOT NULL, category TEXT, device TEXT, blocked INTEGER NOT NULL,
                raw TEXT NOT NULL, imported TEXT NOT NULL, PRIMARY KEY(source,epoch,id));
            CREATE INDEX dns_time ON dns(source,stamp DESC,epoch DESC,id DESC);
            CREATE INDEX dns_domain ON dns(domain,stamp);
            CREATE INDEX dns_device ON dns(device,stamp);
            CREATE INDEX evidence_uuid ON dns(json_extract(raw,'$.conn_uuid'),device);
            CREATE INDEX evidence_community ON dns(json_extract(raw,'$.community_id'),device,stamp);
            CREATE INDEX evidence_mac ON dns(lower(json_extract(raw,'$.src_hwaddr')),stamp);
            CREATE TABLE imported_keys(source TEXT,id INTEGER,stamp INTEGER,signature BLOB,PRIMARY KEY(source,id,stamp,signature)) WITHOUT ROWID;
            CREATE TABLE hourly(source TEXT,hour TEXT,category TEXT,observations INTEGER NOT NULL,blocked INTEGER NOT NULL,
                PRIMARY KEY(source,hour,category));
            CREATE TABLE daily(source TEXT,day TEXT,category TEXT,observations INTEGER NOT NULL,blocked INTEGER NOT NULL,
                PRIMARY KEY(source,day,category));
            CREATE TABLE investigation(sequence INTEGER PRIMARY KEY, source TEXT,epoch INTEGER,event INTEGER,
                time TEXT NOT NULL,actor TEXT NOT NULL,note TEXT NOT NULL,acknowledged INTEGER NOT NULL);
            CREATE INDEX investigation_evidence ON investigation(source,epoch,event);
            PRAGMA user_version=1;
            """;
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        var failures = 0;
        var storageLow = false;
        while (!ct.IsCancellationRequested)
        {
            var delay = Interval;
            try
            {
                var drive = DriveInfo.GetDrives().Where(d => Path.GetFullPath(_directory).StartsWith(d.RootDirectory.FullName,StringComparison.Ordinal))
                    .OrderByDescending(d=>d.RootDirectory.FullName.Length).FirstOrDefault();
                if (drive is { IsReady:true }) {
                    var low = drive.AvailableFreeSpace < Math.Max(1L,config.GetValue<long>("HomeWatch:FreeSpaceWarningMiB",512))*1048576L;
                    if (low && !storageLow) await ntfy.SendEventAsync("storage-warning","HomeWatch storage warning","The HomeWatch data volume is low on free space.",ct);
                    storageLow = low;
                }
                if (Enabled)
                {
                    lock (_gate) _state = "Synchronizing";
                    foreach(var source in Sources)
                    {
                        if (!Enabled) break;
                        lock(_gate) if (_errors.TryGetValue(source,out var prior) && prior.Retry>DateTime.UtcNow) continue;
                        try
                        {
                            if(await ImportAsync(source,ct)==Batch)delay=1;
                            lock(_gate)_errors.Remove(source);
                        }
                        catch(Exception ex) when(!ct.IsCancellationRequested)
                        {
                            bool firstFailure;
                            lock(_gate)
                            {
                                firstFailure=!_errors.ContainsKey(source);
                                var n=_errors.GetValueOrDefault(source).Failures+1;
                                _errors[source]=(n,DateTime.UtcNow.AddSeconds(Math.Min(3600,Interval*(1<<Math.Min(n,6)))),ex.GetType().Name);
                            }
                            if(firstFailure)await ntfy.SendEventAsync("zenarmor-unavailable","Zenarmor collector unavailable",source+" reporting synchronization failed.",ct);
                        }
                    }
                }
                failures = 0;
                lock (_gate) { _state = Enabled ? (_errors.Count==0?"Ready":"Some sources unavailable") : "Disabled"; _retry = null; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                delay = Math.Min(3600, Interval * (1 << Math.Min(6, failures++)));
                log.LogWarning("Zenarmor synchronization failed ({Type})", ex.GetType().Name);
                lock (_gate) { _state = "Sync failed: " + ex.GetType().Name; _retry = DateTime.UtcNow.AddSeconds(delay); }
            }
            await _wake.WaitAsync(TimeSpan.FromSeconds(delay), ct);
        }
    }

    private async Task<JsonDocument> Fetch(string source,long cursor, CancellationToken ct)
    {
        var key = config["Zenarmor:KeyPath"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "homewatch4_collector");
        var psi = new ProcessStartInfo(config["Zenarmor:SshPath"] ?? "ssh") { UseShellExecute=false, RedirectStandardInput=true, RedirectStandardOutput=true, RedirectStandardError=true };
        if (!OperatingSystem.IsWindows())
        {
            foreach (var option in new[] {"ControlMaster=auto","ControlPersist=60","ControlPath=~/.ssh/homewatch4-%C"})
            { psi.ArgumentList.Add("-o"); psi.ArgumentList.Add(option); }
        }
        foreach (var arg in new[] {"-T","-i",key,"-o","IdentitiesOnly=yes","-o","BatchMode=yes","-o","StrictHostKeyChecking=yes","-o","ClearAllForwardings=yes","-o","ConnectTimeout=8","HomeWatch4@192.168.1.1"})
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new IOException("Collector transport unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var stop = timeout.Token.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) {} });
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {source,operation="batch",cursor,limit=Batch}));
        process.StandardInput.Close();
        using var buffer = new MemoryStream();
        var block = new byte[16384];
        int length;
        while ((length = await process.StandardOutput.BaseStream.ReadAsync(block,timeout.Token)) > 0)
        {
            if (buffer.Length + length > 4*1024*1024+2) { process.Kill(true); throw new InvalidDataException("Oversized reply."); }
            buffer.Write(block,0,length);
        }
        await process.WaitForExitAsync(timeout.Token);
        await stderr;
        if (process.ExitCode != 0) throw new IOException("Collector SSH or read failure.");
        return JsonDocument.Parse(buffer.ToArray());
    }

    private async Task<int> ImportAsync(string source,CancellationToken ct)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT generation,epoch,cursor,anchor FROM checkpoint WHERE source=$source";
        cmd.Parameters.AddWithValue("$source",source);
        string? oldGeneration; long epoch,cursor; long? anchor;
        using (var reader=cmd.ExecuteReader())
        {
            reader.Read();
            oldGeneration=reader.IsDBNull(0)?null:reader.GetString(0);
            epoch=reader.GetInt64(1); cursor=reader.GetInt64(2); anchor=reader.IsDBNull(3)?null:reader.GetInt64(3);
        }
        using var doc=await Fetch(source,cursor,ct);
        var reply=doc.RootElement;
        var generation=reply.GetProperty("generation").GetString() ?? throw new InvalidDataException();
        var sourceAnchor=reply.GetProperty("anchor");
        bool reset=oldGeneration!=null && (oldGeneration!=generation || reply.GetProperty("maximum").GetInt64()<cursor ||
            (sourceAnchor.ValueKind!=JsonValueKind.Null && sourceAnchor.GetInt64()!=anchor));
        var now=DateTime.UtcNow.ToString("O");
        var cutoff=DateTimeOffset.UtcNow.AddDays(-Math.Clamp(config.GetValue("Zenarmor:RetentionDays",90),1,3650)).ToUnixTimeMilliseconds();
        using var tx=db.BeginTransaction();
        cmd.Transaction=tx;
        var count=0;
        var inserted=0;
        var notifyThreat=false;
        var notifyBlock=false;
        var recent=DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        if(reset){epoch++;cursor=0;anchor=null;}
        else foreach(var row in reply.GetProperty("records").EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var id=row.GetProperty("id").GetInt64();
            var stamp=row.GetProperty("start_time").GetInt64();
            if(id<=cursor || ++count>Batch)throw new InvalidDataException("Invalid source cursor.");
            var day=DateTimeOffset.FromUnixTimeMilliseconds(stamp).UtcDateTime.ToString("yyyy-MM-dd");
            string Field(string name)=>row.TryGetProperty(name,out var v)?v.ToString():"";
            var category=Field(source=="dns"?"domain_categories":source=="connections"?"app_category":"category");
            var domain=Field(source switch {"dns"=>"query","http"=>"host","tls" when row.TryGetProperty("sni",out _) => "sni",_=>"dst_hostname"});
            int blocked=Field("is_blocked").ToLowerInvariant() is "1" or "true"?1:0;
            cmd.Parameters.Clear();
            cmd.CommandText="INSERT OR IGNORE INTO imported_keys VALUES($source,$id,$stamp,$signature)";
            cmd.Parameters.AddWithValue("$source",source);
            cmd.Parameters.AddWithValue("$id",id);
            cmd.Parameters.AddWithValue("$stamp",stamp);
            cmd.Parameters.AddWithValue("$signature",System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(row.GetRawText())));
            cursor=id;anchor=stamp;
            if(cmd.ExecuteNonQuery()==0)continue;
            inserted++;
            if(stamp>=recent && stamp<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()){
                notifyThreat |= source=="alerts" && !string.IsNullOrWhiteSpace(Field("signature"));
                notifyBlock |= blocked==1;
            }
            cmd.Parameters.Clear();
            cmd.CommandText="INSERT OR IGNORE INTO dns VALUES ($source,$epoch,$id,$stamp,$domain,$category,$device,$blocked,$raw,$now)";
            foreach(var (name,value) in new (string,object)[]{("source",source),("epoch",epoch),("id",id),("stamp",stamp),("domain",domain.Trim().TrimEnd('.').ToLowerInvariant()),("category",category),("device",Field("device_id")),("blocked",blocked),("raw",row.GetRawText()),("now",now)})
                cmd.Parameters.AddWithValue("$"+name,value);
            if(cmd.ExecuteNonQuery()==1)
            {
                cmd.Parameters.AddWithValue("$hour",DateTimeOffset.FromUnixTimeMilliseconds(stamp).UtcDateTime.ToString("yyyy-MM-ddTHH"));
                cmd.CommandText="INSERT INTO hourly VALUES($source,$hour,$category,1,$blocked) ON CONFLICT(source,hour,category) DO UPDATE SET observations=observations+1,blocked=hourly.blocked+excluded.blocked";
                cmd.ExecuteNonQuery();
                cmd.Parameters.AddWithValue("$day",day);
                cmd.CommandText="INSERT INTO daily VALUES($source,$day,$category,1,$blocked) ON CONFLICT(source,day,category) DO UPDATE SET observations=observations+1,blocked=daily.blocked+excluded.blocked";
                cmd.ExecuteNonQuery();
            }
            cursor=id;anchor=stamp;
        }
        cmd.Parameters.Clear();
        cmd.CommandText="UPDATE checkpoint SET generation=$gen,epoch=$epoch,cursor=$cursor,anchor=$anchor,synced=$now,imported=imported+$count WHERE source=$source";
        cmd.Parameters.AddWithValue("$source",source);
        cmd.Parameters.AddWithValue("$gen",generation);cmd.Parameters.AddWithValue("$epoch",epoch);cmd.Parameters.AddWithValue("$cursor",cursor);
        cmd.Parameters.AddWithValue("$anchor",(object?)anchor??DBNull.Value);cmd.Parameters.AddWithValue("$now",now);cmd.Parameters.AddWithValue("$count",inserted);
        cmd.ExecuteNonQuery();
        cmd.CommandText="DELETE FROM dns WHERE stamp<$cutoff AND source NOT IN ('alerts','security-tags','suricata')";cmd.Parameters.AddWithValue("$cutoff",cutoff);cmd.ExecuteNonQuery();
        tx.Commit();
        if(notifyThreat) await ntfy.SendEventAsync("security-threat","Zenarmor alert","A recent reporting batch contains an alert with a signature. Review Zenarmor evidence.",ct);
        if(notifyBlock) await ntfy.SendEventAsync("policy-block","Zenarmor policy block","A recent reporting batch contains blocked activity. Review Zenarmor evidence.",ct);
        return reset?Batch:count;
    }

    internal Dictionary<long,ZenarmorHandshake> MatchHandshakes(IReadOnlyList<TrafficEvent> flows,
        IReadOnlyDictionary<long,Device> devices, DateTime since, int tolerance, IgnoredDomainStore ignores, EvidenceDomainPolicy policy)
    {
        var found = new Dictionary<long,ZenarmorHandshake>();
        var conflicting = new HashSet<long>();
        var candidates = flows.Where(f => f.DeviceId.HasValue && f.StartedUtc.HasValue)
            .ToLookup(f => SuricataTlsEnrichment.Connection.Create(f.SourceIp,f.SourcePort,f.DestinationIp,f.DestinationPort,f.Protocol));
        if (candidates.Count == 0) return found;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT stamp,raw FROM dns WHERE source='tls' AND stamp >= $since ORDER BY stamp DESC LIMIT 5000";
        cmd.Parameters.AddWithValue("$since", new DateTimeOffset(DateTime.SpecifyKind(since,DateTimeKind.Utc)).ToUnixTimeMilliseconds());
        var window = TimeSpan.FromSeconds(Math.Clamp(tolerance,0,30));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var time = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).UtcDateTime;
            using var doc = JsonDocument.Parse(reader.GetString(1));
            string? Field(string key) => doc.RootElement.TryGetProperty(key,out var v) && !string.IsNullOrWhiteSpace(v.ToString()) ? v.ToString() : null;
            int? Port(string key) => int.TryParse(Field(key),out var n) ? n : null;
            var tuple = SuricataTlsEnrichment.Connection.Create(Field("ip_src_saddr"),Port("ip_src_port"),Field("ip_dst_saddr"),Port("ip_dst_port"),Field("transport_proto"));
            var mac = Field("src_hwaddr")?.Replace('-',':');
            if (tuple == null || string.IsNullOrEmpty(mac)) continue;
            var matches = candidates[tuple].Where(f => time >= f.StartedUtc-window && time <= (f.LastSeenUtc??f.TimestampUtc)+window).Take(2).ToArray();
            if (matches.Length != 1) continue;
            var flow = matches[0];
            if (!devices.TryGetValue(flow.DeviceId!.Value,out var device) || !string.Equals(mac,device.MacAddress?.Replace('-',':'),StringComparison.OrdinalIgnoreCase)) continue;
            var host = Field("sni") ?? Field("dst_hostname") ?? Field("host");
            if (host!=null && (ignores.IsIgnored(host) || policy.IsTrusted(host))) continue;
            var observed = new ZenarmorHandshake(host,Field("tls_version")??Field("version"),Field("ja3"),Field("ja3s"),Field("ja4"));
            if (observed == new ZenarmorHandshake(null,null,null,null,null)) continue;
            if (found.TryGetValue(flow.Id,out var previous) && previous!=observed) conflicting.Add(flow.Id);
            else found[flow.Id]=observed;
        }
        foreach (var id in conflicting) found.Remove(id);
        return found;
    }

    public object SessionEvidence(string mac, DateTime start,DateTime end,string[] domains)
    {
        // Require identity, observed window and an exact session hostname. DNS alone is a lookup.
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT source,id,stamp,domain,category,raw FROM dns
            WHERE replace(lower(json_extract(raw,'$.src_hwaddr')),'-',':')=$mac AND stamp BETWEEN $start AND $end
            AND domain IN (SELECT value FROM json_each($domains)) ORDER BY stamp DESC LIMIT 250
            """;
        cmd.Parameters.AddWithValue("$mac",mac.Trim().Replace('-',':').ToLowerInvariant());
        cmd.Parameters.AddWithValue("$start",new DateTimeOffset(DateTime.SpecifyKind(start,DateTimeKind.Utc)).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$end",new DateTimeOffset(DateTime.SpecifyKind(end,DateTimeKind.Utc)).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$domains",JsonSerializer.Serialize(domains.Select(x=>x.Trim().TrimEnd('.').ToLowerInvariant())));
        using var r=cmd.ExecuteReader();var items=new List<object>();
        while(r.Read())items.Add(new {source=r.GetString(0),id=r.GetInt64(1),timeMs=r.GetInt64(2),domain=r.GetString(3),category=r.GetString(4),
            categoryType=r.GetString(0)=="connections"?"application":"domain"});
        return items;
    }

    public object SourceStatus()
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT source,cursor,synced,imported,anchor FROM checkpoint ORDER BY source";
        using var r=cmd.ExecuteReader();var result=new List<object>();
        lock(_gate)while(r.Read()){
            var source=r.GetString(0);var error=_errors.GetValueOrDefault(source);
            result.Add(new {source,cursor=r.GetInt64(1),synced=r.IsDBNull(2)?null:r.GetString(2),imported=r.GetInt64(3),
                latestSourceMs=r.IsDBNull(4)?(long?)null:r.GetInt64(4),error=error.Error,retry=error.Failures>0?(DateTime?)error.Retry:null});
        }
        return result;
    }

    public object Status()
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT cursor,synced,imported,anchor,(SELECT count(*) FROM dns) FROM checkpoint WHERE source='dns'";
        using var r=cmd.ExecuteReader();r.Read();
        lock(_gate)return new { enabled=Enabled,state=_state,nextRetryUtc=_retry,pollSeconds=Interval,cursor=r.GetInt64(0),
            lastSuccessUtc=r.IsDBNull(1)?null:r.GetString(1),imported=r.GetInt64(2),latestSourceMs=r.IsDBNull(3)?(long?)null:r.GetInt64(3),
            retained=r.GetInt64(4), sourceLagSeconds=r.IsDBNull(3)?(long?)null:Math.Max(0,(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-r.GetInt64(3))/1000),
            storageBytes=new FileInfo(DbPath).Length };
    }

    public void ArchiveIdsAlerts(JsonElement payload)
    {
        var rows = payload.ValueKind==JsonValueKind.Array ? payload :
            payload.ValueKind==JsonValueKind.Object && payload.TryGetProperty("rows",out var list) ? list : default;
        if(rows.ValueKind!=JsonValueKind.Array) return;
        using var db=Open(); using var tx=db.BeginTransaction(); using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="INSERT OR IGNORE INTO dns VALUES('suricata',$epoch,$id,$stamp,$domain,$category,'',$blocked,$raw,$now)";
        foreach(var row in rows.EnumerateArray().Take(500))
        {
            static string? Field(JsonElement obj,string key) =>
                obj.ValueKind==JsonValueKind.Object && obj.TryGetProperty(key,out var item) && item.ValueKind is JsonValueKind.String or JsonValueKind.Number ? item.ToString() : null;
            var timestamp=Field(row,"timestamp");
            if(!DateTimeOffset.TryParse(timestamp,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out var time)) continue;
            if(!row.TryGetProperty("alert",out var alert)) continue;
            var signature=Field(alert,"signature")??Field(row,"alert");
            if(string.IsNullOrWhiteSpace(signature)) continue;
            var category=Field(alert,"category")??Field(row,"alert_category")??"";
            var action=Field(alert,"action")??Field(row,"alert_action")??Field(row,"action");
            var host=new[]{("tls","sni"),("quic","sni"),("http","hostname"),("dns","rrname")}
                .Select(x=>row.TryGetProperty(x.Item1,out var nested)?Field(nested,x.Item2):null).FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x));
            var evidence=new {start_time=time.ToUnixTimeMilliseconds(),timestamp,signature,category,action,host,
                severity=Field(alert,"severity")??Field(row,"alert_severity"),sid=Field(alert,"signature_id")??Field(row,"alert_sid"),
                flow_id=Field(row,"flow_id"),ip_src_saddr=Field(row,"src_ip"),ip_src_port=Field(row,"src_port"),
                ip_dst_saddr=Field(row,"dest_ip"),ip_dst_port=Field(row,"dest_port"),transport_proto=Field(row,"proto")};
            var raw=JsonSerializer.Serialize(evidence);
            var hash=System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
            // Two independently stored 48-bit halves preserve exact identifiers in browser JSON numbers.
            long Key(int offset)=>System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(hash.AsSpan(offset,8)) & 0xffffffffffffL;
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id",Key(0));cmd.Parameters.AddWithValue("$epoch",Key(8));
            cmd.Parameters.AddWithValue("$stamp",evidence.start_time);cmd.Parameters.AddWithValue("$domain",host?.TrimEnd('.').ToLowerInvariant()??"");
            cmd.Parameters.AddWithValue("$category",category);cmd.Parameters.AddWithValue("$blocked",action?.ToLowerInvariant() is "blocked" or "drop" or "dropped");
            cmd.Parameters.AddWithValue("$raw",raw);cmd.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public object SecurityHistory(string? search)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT source,epoch,id,stamp,domain,category,blocked,raw,
                (SELECT acknowledged FROM investigation i WHERE i.source=d.source AND i.epoch=d.epoch AND i.event=d.id ORDER BY sequence DESC LIMIT 1)
            FROM dns d WHERE source IN ('alerts','security-tags','suricata')
            AND ($term='' OR instr(lower(domain),$term)>0 OR instr(lower(raw),$term)>0)
            ORDER BY stamp DESC,source,epoch DESC,id DESC LIMIT 200
            """;
        cmd.Parameters.AddWithValue("$term",search?.Trim().ToLowerInvariant()??"");
        using var r=cmd.ExecuteReader();var result=new List<object>();
        while(r.Read()) {
            using var document=JsonDocument.Parse(r.GetString(7));
            result.Add(new {source=r.GetString(0),epoch=r.GetInt64(1),id=r.GetInt64(2),time=r.GetInt64(3),domain=r.GetString(4),
                category=r.GetString(5),blocked=r.GetBoolean(6),evidence=document.RootElement.Clone(),acknowledged=!r.IsDBNull(8)&&r.GetBoolean(8)});
        }
        return result;
    }

    public bool Investigate(string source,long epoch,long id,string actor,string note,bool acknowledged)
    {
        if(source is not ("alerts" or "security-tags" or "suricata") || note.Length>1000)return false;
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            INSERT INTO investigation(source,epoch,event,time,actor,note,acknowledged)
            SELECT source,epoch,id,$time,$actor,$note,$ack FROM dns WHERE source=$source AND epoch=$epoch AND id=$id
            """;
        cmd.Parameters.AddWithValue("$time",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$actor",actor);
        cmd.Parameters.AddWithValue("$note",note);cmd.Parameters.AddWithValue("$ack",acknowledged);
        cmd.Parameters.AddWithValue("$source",source);cmd.Parameters.AddWithValue("$epoch",epoch);cmd.Parameters.AddWithValue("$id",id);
        return cmd.ExecuteNonQuery()==1;
    }

    public object Investigation(string source,long epoch,long id)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT time,actor,note,acknowledged FROM investigation WHERE source=$source AND epoch=$epoch AND event=$id ORDER BY sequence DESC LIMIT 100";
        cmd.Parameters.AddWithValue("$source",source);cmd.Parameters.AddWithValue("$epoch",epoch);cmd.Parameters.AddWithValue("$id",id);
        using var reader=cmd.ExecuteReader();var rows=new List<object>();
        while(reader.Read())rows.Add(new {time=reader.GetString(0),actor=reader.GetString(1),note=reader.GetString(2),acknowledged=reader.GetBoolean(3)});
        return rows;
    }

    public object Related(string source,long epoch,long id)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            WITH original AS (SELECT * FROM dns WHERE source=$source AND epoch=$epoch AND id=$id)
            SELECT b.source,b.stamp,b.domain,b.category,b.id,b.raw FROM original a JOIN dns b
            ON a.device<>'' AND b.device=a.device AND (
                (length(json_extract(a.raw,'$.conn_uuid'))>0 AND json_extract(a.raw,'$.conn_uuid')=json_extract(b.raw,'$.conn_uuid')) OR
                (length(json_extract(a.raw,'$.community_id'))>0 AND json_extract(a.raw,'$.community_id')=json_extract(b.raw,'$.community_id')
                 AND b.stamp BETWEEN a.stamp-2000 AND coalesce(json_extract(a.raw,'$.end_time'),a.stamp)+2000
                 AND json_extract(a.raw,'$.ip_src_saddr')=json_extract(b.raw,'$.ip_src_saddr')
                 AND json_extract(a.raw,'$.ip_dst_saddr')=json_extract(b.raw,'$.ip_dst_saddr')
                 AND json_extract(a.raw,'$.ip_src_port')=json_extract(b.raw,'$.ip_src_port')
                 AND json_extract(a.raw,'$.ip_dst_port')=json_extract(b.raw,'$.ip_dst_port')))
            ORDER BY b.stamp DESC,b.id DESC LIMIT 100
            """;
        cmd.Parameters.AddWithValue("$source",source);cmd.Parameters.AddWithValue("$epoch",epoch);cmd.Parameters.AddWithValue("$id",id);
        using var r=cmd.ExecuteReader();var evidence=new List<object>();
        while(r.Read())evidence.Add(new {source=r.GetString(0),timeMs=r.GetInt64(1),domain=r.GetString(2),category=r.GetString(3),id=r.GetInt64(4)});
        return evidence;
    }

    public object Read(string source,string? domain,string? device,string? category,bool? blocked,long? beforeMs,long epoch,long id,DateOnly? from=null,DateOnly? through=null)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT epoch,id,stamp,raw,imported FROM dns WHERE source=$source AND ($domain='' OR domain=$domain)
            AND ($device='' OR device=$device) AND ($category='' OR category=$category)
            AND stamp >= $start AND stamp < $end AND ($blocked IS NULL OR blocked=$blocked) AND (stamp,epoch,id)<($stamp,$epoch,$id)
            ORDER BY stamp DESC,epoch DESC,id DESC LIMIT 101
            """;
        cmd.Parameters.AddWithValue("$source",source);
        cmd.Parameters.AddWithValue("$domain",domain?.Trim().TrimEnd('.').ToLowerInvariant()??"");
        cmd.Parameters.AddWithValue("$device",device??"");cmd.Parameters.AddWithValue("$category",category??"");
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
        long Midnight(DateOnly date) => new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue),zone)).ToUnixTimeMilliseconds();
        cmd.Parameters.AddWithValue("$start", from.HasValue ? Midnight(from.Value) : long.MinValue);
        cmd.Parameters.AddWithValue("$end", through.HasValue && through.Value<DateOnly.MaxValue ? Midnight(through.Value.AddDays(1)) : long.MaxValue);
        cmd.Parameters.AddWithValue("$blocked",(object?)blocked??DBNull.Value);
        cmd.Parameters.AddWithValue("$stamp",beforeMs??long.MaxValue);cmd.Parameters.AddWithValue("$epoch",epoch);cmd.Parameters.AddWithValue("$id",id);
        using var reader=cmd.ExecuteReader();var rows=new List<object>();object? next=null;
        while(reader.Read())
        {
            if(rows.Count==100)return new {rows,next};
            using var raw=JsonDocument.Parse(reader.GetString(3));
            rows.Add(new { evidence=raw.RootElement.Clone(), epoch=reader.GetInt64(0), importedUtc=reader.GetString(4) });
            next=new {beforeMs=reader.GetInt64(2),epoch=reader.GetInt64(0),id=reader.GetInt64(1)};
        }
        return new {rows,next=(object?)null};
    }

    public object Overview()
    {
        using var db=Open();
        object[] Top(string sql)
        {
            using var cmd=db.CreateCommand();cmd.CommandText=sql;
            using var reader=cmd.ExecuteReader();var rows=new List<object>();
            while(reader.Read())rows.Add(new {label=reader.GetString(0),count=reader.GetInt64(1)});
            return rows.ToArray();
        }
        return new { domains=Top("SELECT domain,count(*) FROM dns WHERE source='dns' GROUP BY domain ORDER BY count(*) DESC,domain LIMIT 10"),
            devices=Top("SELECT device,count(*) FROM dns WHERE source='dns' AND device<>'' GROUP BY device ORDER BY count(*) DESC,device LIMIT 10"),
            blocked=Top("SELECT domain,count(*) FROM dns WHERE source='dns' AND blocked=1 GROUP BY domain ORDER BY count(*) DESC,domain LIMIT 10") };
    }

    public object Categories()
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT day,category,observations,blocked FROM daily WHERE source='dns' ORDER BY day DESC,observations DESC LIMIT 1000";
        using var reader=cmd.ExecuteReader();var rows=new List<object>();
        while(reader.Read())rows.Add(new {day=reader.GetString(0),category=reader.GetString(1),observations=reader.GetInt64(2),blocked=reader.GetInt64(3)});
        return rows;
    }
}

internal sealed record ZenarmorHandshake(string? Host,string? Version,string? Ja3,string? Ja3s,string? Ja4);
