using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Data;

internal static class IdentityMigration
{
    public static void Apply(HomeWatchDb db)
    {
        var path=Path.GetFullPath(db.Database.GetDbConnection().DataSource);
        using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWrite}.ToString());
        connection.Open();
        using var command=connection.CreateCommand();
        command.CommandText="PRAGMA user_version";
        var version=Convert.ToInt32(command.ExecuteScalar());
        if(version==1)return;
        if(version!=0)throw new InvalidDataException("Unexpected HomeWatch schema version.");
        var folder=Path.Combine(Path.GetDirectoryName(path)!,"backups");
        Directory.CreateDirectory(folder);
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(folder,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var backup=Path.Combine(folder,"pre-identity-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")+".db");
        using(var snapshot=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=backup}.ToString()))
        {
            snapshot.Open();connection.BackupDatabase(snapshot);
            using var check=snapshot.CreateCommand();check.CommandText="PRAGMA integrity_check";
            if(!Equals(check.ExecuteScalar(),"ok"))throw new InvalidDataException("Pre-migration backup failed integrity verification.");
        }
        using var tx=connection.BeginTransaction();command.Transaction=tx;
        command.CommandText="""
            CREATE TABLE DeviceProfile(device INTEGER PRIMARY KEY REFERENCES Devices(Id), owner TEXT,labels TEXT);
            CREATE TABLE DeviceGroupMember(device INTEGER PRIMARY KEY REFERENCES Devices(Id), canonical INTEGER NOT NULL REFERENCES Devices(Id),CHECK(device<>canonical));
            CREATE INDEX DeviceGroupCanonical ON DeviceGroupMember(canonical);
            CREATE TABLE IdentityObservation(id INTEGER PRIMARY KEY,device INTEGER,stamp TEXT,source TEXT,mac TEXT,ip TEXT,name TEXT);
            CREATE INDEX IdentityDeviceTime ON IdentityObservation(device,stamp);
            CREATE TABLE IdentityAudit(id INTEGER PRIMARY KEY,device INTEGER,stamp TEXT,actor TEXT,action TEXT,detail TEXT);
            CREATE INDEX IdentityAuditDevice ON IdentityAudit(device,id);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();tx.Commit();
    }
}
