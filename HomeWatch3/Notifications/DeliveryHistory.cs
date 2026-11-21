using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace HomeWatch3.Notifications;

public sealed class DeliveryHistory(IConfiguration configuration)
{
    private readonly string _path = Path.Combine(configuration["HomeWatch:DataPath"]??Path.Combine(AppContext.BaseDirectory,"data"),"ntfy-delivery.db");
    private readonly object _gate=new();
    private SqliteConnection Open()
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=_path}.ToString());db.Open();
        using var cmd=db.CreateCommand();cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS delivery(id INTEGER PRIMARY KEY, stamp INTEGER NOT NULL,event TEXT,priority TEXT,fingerprint TEXT,outcome TEXT);
            CREATE INDEX IF NOT EXISTS delivery_stamp ON delivery(stamp);
            CREATE INDEX IF NOT EXISTS delivery_repeat ON delivery(fingerprint,stamp);
            """;cmd.ExecuteNonQuery();return db;
    }
    public long Begin(string type,string title,string message,string priority)
    {
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(type+"\n"+title+"\n"+message)));
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var cmd=db.CreateCommand();cmd.Transaction=tx;
            var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            cmd.Parameters.AddWithValue("$now",now);cmd.Parameters.AddWithValue("$key",key);
            cmd.CommandText="SELECT count(*) FROM delivery WHERE fingerprint=$key AND stamp>=$now-300 AND outcome IN ('pending','delivered')";
            if(type!="test" && Convert.ToInt64(cmd.ExecuteScalar())>0)return 0;
            cmd.Parameters.AddWithValue("$type",type);cmd.Parameters.AddWithValue("$priority",priority);
            cmd.CommandText="DELETE FROM delivery WHERE stamp<$now-7776000; INSERT INTO delivery(stamp,event,priority,fingerprint,outcome) VALUES($now,$type,$priority,$key,'pending'); SELECT last_insert_rowid();";
            var id=Convert.ToInt64(cmd.ExecuteScalar());tx.Commit();return id;
        }
    }
    public void Finish(long id,string outcome)
    {
        lock(_gate){
            using var db=Open();using var cmd=db.CreateCommand();
            cmd.CommandText="UPDATE delivery SET outcome=$outcome WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$outcome",outcome);cmd.ExecuteNonQuery();
        }
    }
    public object Recent()
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT stamp,event,priority,outcome FROM delivery ORDER BY id DESC LIMIT 100";
        using var r=cmd.ExecuteReader();var rows=new List<object>();
        while(r.Read())rows.Add(new {utc=DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)),type=r.GetString(1),priority=r.GetString(2),outcome=r.GetString(3)});
        return rows;
    }
}
