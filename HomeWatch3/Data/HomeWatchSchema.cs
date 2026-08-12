using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Data;

public static class HomeWatchSchema
{
    private static readonly (string Name, string Definition)[] TrafficEventColumns =
    [
        ("StartedUtc", "TEXT NULL"),
        ("LastSeenUtc", "TEXT NULL"),
        ("DestinationPort", "INTEGER NULL"),
        ("ExactUrl", "TEXT NULL"),
        ("DurationSeconds", "INTEGER NULL"),
        ("Country", "TEXT NULL"),
        ("Visibility", "TEXT NOT NULL DEFAULT 'hostname'"),
        ("Encrypted", "INTEGER NOT NULL DEFAULT 0"),
        ("ExternalId", "TEXT NULL")
    ];

    public static async Task EnsureUpgradedAsync(HomeWatchDb db, CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var existingColumns = await ReadColumnsAsync(db.Database.GetDbConnection(), cancellationToken);
            foreach (var (name, definition) in TrafficEventColumns)
            {
                if (existingColumns.Contains(name)) continue;
                await db.Database.ExecuteSqlRawAsync(
                    $"ALTER TABLE \"TrafficEvents\" ADD COLUMN \"{name}\" {definition};",
                    cancellationToken);
            }

            await db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TrafficEvents_ExternalId\" ON \"TrafficEvents\" (\"ExternalId\") WHERE \"ExternalId\" IS NOT NULL;",
                cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"TrafficEvents\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(1)) columns.Add(reader.GetString(1));
        }
        return columns;
    }
}
