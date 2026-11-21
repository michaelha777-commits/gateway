using HomeWatch3.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

public sealed class TrafficRetention(IServiceScopeFactory scopes, IConfiguration config, ILogger<TrafficRetention> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!config.GetValue("History:EnableRawRetention", false)) return;
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HomeWatchDb>();
                var days = Math.Clamp(config.GetValue("History:RawRetentionDays", 90), 1, 3650);
                processed = ArchiveBatch(db, DateTime.UtcNow.AddDays(-days));
            }
            catch (SqliteException ex) { logger.LogWarning("Traffic retention deferred (SQLite {Code})", ex.SqliteErrorCode); }
            await Task.Delay(processed == 1000 ? TimeSpan.FromSeconds(2) : TimeSpan.FromHours(1),ct);
        }
    }

    private static int ArchiveBatch(HomeWatchDb db, DateTime cutoff)
    {
        using var connection = new SqliteConnection(db.Database.GetDbConnection().ConnectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS ArchivedTrafficHour(
                hour TEXT, device INTEGER, source TEXT, category TEXT, observations INTEGER NOT NULL,
                blocked INTEGER NOT NULL, uploaded INTEGER NOT NULL, downloaded INTEGER NOT NULL,
                PRIMARY KEY(hour,device,source,category));
            CREATE TEMP TABLE IF NOT EXISTS expiring(id INTEGER PRIMARY KEY);
            DELETE FROM expiring;
            INSERT INTO expiring SELECT Id FROM TrafficEvents
                WHERE TimestampUtc < $cutoff AND (LastSeenUtc IS NULL OR LastSeenUtc < $cutoff)
                ORDER BY TimestampUtc,Id LIMIT 1000;
            INSERT INTO ArchivedTrafficHour
                SELECT substr(TimestampUtc,1,13),coalesce(DeviceId,0),coalesce(Source,''),coalesce(Category,''),
                       count(*),sum(Blocked),sum(max(0,coalesce(BytesUp,0))),sum(max(0,coalesce(BytesDown,0)))
                FROM TrafficEvents WHERE Id IN (SELECT id FROM expiring)
                GROUP BY 1,2,3,4
                ON CONFLICT(hour,device,source,category) DO UPDATE SET
                    observations=observations+excluded.observations,blocked=blocked+excluded.blocked,
                    uploaded=uploaded+excluded.uploaded,downloaded=downloaded+excluded.downloaded;
            DELETE FROM TrafficEvents WHERE Id IN (SELECT id FROM expiring);
            SELECT count(*) FROM expiring;
            """;
        cmd.Parameters.AddWithValue("$cutoff",cutoff);
        var processed = Convert.ToInt32(cmd.ExecuteScalar());
        tx.Commit();
        return processed;
    }
}
