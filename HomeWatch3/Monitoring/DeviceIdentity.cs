using HomeWatch3.Authentication;
using HomeWatch3.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

internal static class DeviceIdentity
{
    private static SqliteConnection Open(HomeWatchDb db)
    {
        var connection=new SqliteConnection(db.Database.GetDbConnection().ConnectionString);
        connection.Open();return connection;
    }
    public static void Observe(HomeWatchDb db,long device,string? mac,string? ip,string? name,string source)
    {
        using var conn=Open(db);using var cmd=conn.CreateCommand();
        cmd.CommandText="""
            INSERT INTO IdentityObservation(device,stamp,source,mac,ip,name)
            SELECT $device,$stamp,$source,$mac,$ip,$name WHERE NOT EXISTS (
              SELECT 1 FROM (SELECT mac,ip,name FROM IdentityObservation WHERE device=$device AND source=$source ORDER BY id DESC LIMIT 1)
              WHERE mac IS $mac AND ip IS $ip AND name IS $name)
            """;
        cmd.Parameters.AddWithValue("$device",device);cmd.Parameters.AddWithValue("$stamp",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$source",source);
        cmd.Parameters.AddWithValue("$mac",(object?)mac??DBNull.Value);cmd.Parameters.AddWithValue("$ip",(object?)ip??DBNull.Value);cmd.Parameters.AddWithValue("$name",(object?)name??DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static long[] Members(HomeWatchDb db,long id)
    {
        using var conn=Open(db);using var cmd=conn.CreateCommand();
        cmd.CommandText="SELECT device FROM DeviceGroupMember WHERE canonical=$id UNION SELECT $id";
        cmd.Parameters.AddWithValue("$id",id);using var rows=cmd.ExecuteReader();var members=new List<long>();
        while(rows.Read())members.Add(rows.GetInt64(0));return members.ToArray();
    }

    public static void MapDeviceIdentity(this WebApplication app)
    {
        app.MapGet("/api/devices/{id:long}/identity",async(long id,HomeWatchDb db,CancellationToken ct)=>{
            var device=await db.Devices.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==id,ct);if(device==null)return Results.NotFound();
            using var conn=Open(db);using var cmd=conn.CreateCommand();
            cmd.Parameters.AddWithValue("$id",id);
            List<Dictionary<string,object?>> Rows(string sql){
                cmd.CommandText=sql;using var r=cmd.ExecuteReader();var result=new List<Dictionary<string,object?>>();
                while(r.Read()){var row=new Dictionary<string,object?>();for(var i=0;i<r.FieldCount;i++)row[r.GetName(i)]=r.IsDBNull(i)?null:r.GetValue(i);result.Add(row);}return result;
            }
            var prefix=device.MacAddress?.Split(':','-').FirstOrDefault();
            var local=byte.TryParse(prefix,System.Globalization.NumberStyles.HexNumber,null,out var octet) && (octet&2)!=0;
            return Results.Ok(new {profile=Rows("SELECT owner,labels FROM DeviceProfile WHERE device=$id").FirstOrDefault(),
                group=Rows("SELECT device,canonical FROM DeviceGroupMember WHERE device=$id OR canonical=$id"),
                observations=Rows("SELECT stamp,source,mac,ip,name FROM IdentityObservation WHERE device=$id ORDER BY id DESC LIMIT 100"),
                audit=Rows("SELECT stamp,actor,action,detail FROM IdentityAudit WHERE device=$id ORDER BY id DESC LIMIT 100"),
                locallyAdministeredMac=local});
        });
        app.MapPut("/api/devices/{id:long}/identity",async(long id,IdentityChange change,HttpContext context,HomeWatchDb db,CancellationToken ct)=>{
            if(!await db.Devices.AnyAsync(x=>x.Id==id,ct))return Results.NotFound();
            if(change.Action=="merge" && (change.Target==null || change.Target==id || !await db.Devices.AnyAsync(x=>x.Id==change.Target,ct)))return Results.BadRequest();
            using var conn=Open(db);using var tx=conn.BeginTransaction();using var cmd=conn.CreateCommand();cmd.Transaction=tx;
            cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$target",(object?)change.Target??DBNull.Value);
            string detail;
            switch(change.Action){
                case "profile":
                    if((change.Owner?.Length??0)>120 || (change.Labels?.Length??0)>250)return Results.BadRequest();
                    cmd.Parameters.AddWithValue("$owner",change.Owner?.Trim()??"");cmd.Parameters.AddWithValue("$labels",change.Labels?.Trim()??"");
                    cmd.CommandText="INSERT INTO DeviceProfile VALUES($id,$owner,$labels) ON CONFLICT(device) DO UPDATE SET owner=$owner,labels=$labels";
                    detail=(change.Owner+" / "+change.Labels).Trim();break;
                case "merge":
                    cmd.CommandText="SELECT count(*) FROM DeviceGroupMember WHERE canonical=$id OR device IN ($id,$target)";
                    if(Convert.ToInt64(cmd.ExecuteScalar())!=0)return Results.Conflict(new {error="Split an existing membership before regrouping."});
                    cmd.CommandText="INSERT INTO DeviceGroupMember VALUES($id,$target)";detail="Grouped with "+change.Target;break;
                case "split":
                    cmd.CommandText="SELECT canonical FROM DeviceGroupMember WHERE device=$id";
                    var previous=cmd.ExecuteScalar();if(previous==null)return Results.NotFound();detail="Split from "+previous;
                    cmd.CommandText="DELETE FROM DeviceGroupMember WHERE device=$id";break;
                default:return Results.BadRequest();
            }
            cmd.ExecuteNonQuery();
            cmd.CommandText="INSERT INTO IdentityAudit(device,stamp,actor,action,detail) VALUES($id,$stamp,$actor,$action,$detail)";
            cmd.Parameters.AddWithValue("$stamp",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$actor",context.GetAuthenticatedUser()?.Email??"local");
            cmd.Parameters.AddWithValue("$action",change.Action);cmd.Parameters.AddWithValue("$detail",detail);cmd.ExecuteNonQuery();tx.Commit();
            return Results.Ok(new {saved=true});
        });
    }
}
public sealed record IdentityChange(string Action,long? Target,string? Owner,string? Labels);
