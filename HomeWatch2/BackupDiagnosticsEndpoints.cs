using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public static class BackupDiagnosticsEndpoints
{
    private const string BackupFormat = "homewatch-backup-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void MapBackupDiagnosticsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/exports/activity", async (HomeWatchDb db, IWebHostEnvironment env, bool includeIgnored = true, CancellationToken ct = default) =>
        {
            var ignoredDeviceIds = includeIgnored ? new HashSet<Guid>() : ReadIgnoredDeviceIds(env.ContentRootPath);
            var package = await BuildExportAsync(db, ignoredDeviceIds, ct);
            var json = JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
            var suffix = includeIgnored ? "all" : "non-ignored";
            return Results.File(json, "application/json", $"HomeWatch-activity-{suffix}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        });

        app.MapGet("/api/exports/sessions", async (HomeWatchDb db, IWebHostEnvironment env, bool includeIgnored = true, CancellationToken ct = default) =>
        {
            var ignoredDeviceIds = includeIgnored ? new HashSet<Guid>() : ReadIgnoredDeviceIds(env.ContentRootPath);
            var sessions = await db.Sessions.AsNoTracking()
                .Where(x => !ignoredDeviceIds.Contains(x.DeviceId))
                .OrderByDescending(x => x.StartedAt)
                .Select(x => new
                {
                    x.Id,
                    x.DeviceId,
                    x.StartedAt,
                    x.EndedAt,
                    x.Confidence,
                    x.Assessment
                }).ToListAsync(ct);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                exportFormat = "homewatch-sessions-v1",
                generatedUtc = DateTime.UtcNow,
                includeIgnored,
                sessions
            }, JsonOptions);
            return Results.File(bytes, "application/json", $"HomeWatch-sessions-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        });

        app.MapGet("/api/backup/full", async (HomeWatchDb db, IWebHostEnvironment env, IConfiguration config, CancellationToken ct) =>
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "HomeWatch", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                var dbFile = Path.Combine(tempRoot, "homewatch.db");
                await CreateDatabaseSnapshotAsync(db, dbFile, ct);

                var manifest = new
                {
                    format = BackupFormat,
                    createdUtc = DateTime.UtcNow,
                    homeWatchVersion = "2.0.0-alpha.17",
                    databaseProvider = "SQLite",
                    databaseSha256 = Sha256(dbFile),
                    machine = Environment.MachineName,
                    operatingSystem = Environment.OSVersion.ToString()
                };
                await File.WriteAllTextAsync(Path.Combine(tempRoot, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions), ct);

                var dataDir = Path.Combine(tempRoot, "data");
                Directory.CreateDirectory(dataDir);
                foreach (var file in FindBackupFiles(env.ContentRootPath))
                {
                    var relative = Path.GetRelativePath(env.ContentRootPath, file);
                    var destination = Path.Combine(dataDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, true);
                }

                var zipPath = Path.Combine(Path.GetTempPath(), $"HomeWatch-full-backup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.hwbackup");
                ZipFile.CreateFromDirectory(tempRoot, zipPath, CompressionLevel.Optimal, false);
                var bytes = await File.ReadAllBytesAsync(zipPath, ct);
                File.Delete(zipPath);
                return Results.File(bytes, "application/zip", $"HomeWatch-full-backup-{DateTime.Now:yyyyMMdd-HHmmss}.hwbackup");
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        });

        app.MapPost("/api/backup/restore", async (HttpRequest request, HomeWatchDb db, IWebHostEnvironment env, CancellationToken ct) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Upload a HomeWatch .hwbackup file." });
            var form = await request.ReadFormAsync(ct);
            var upload = form.Files.GetFile("backup");
            if (upload is null || upload.Length == 0) return Results.BadRequest(new { error = "No backup file was selected." });
            if (upload.Length > 2L * 1024 * 1024 * 1024) return Results.BadRequest(new { error = "Backup is too large." });

            var tempRoot = Path.Combine(Path.GetTempPath(), "HomeWatchRestore", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                var archivePath = Path.Combine(tempRoot, "restore.hwbackup");
                await using (var target = File.Create(archivePath)) await upload.CopyToAsync(target, ct);
                var extractPath = Path.Combine(tempRoot, "extracted");
                Directory.CreateDirectory(extractPath);
                SafeExtract(archivePath, extractPath);

                var manifestPath = Path.Combine(extractPath, "manifest.json");
                if (!File.Exists(manifestPath)) return Results.BadRequest(new { error = "This is not a valid HomeWatch backup: manifest.json is missing." });
                using var manifestDoc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct));
                var format = manifestDoc.RootElement.TryGetProperty("format", out var formatNode) ? formatNode.GetString() : null;
                if (!string.Equals(format, BackupFormat, StringComparison.Ordinal)) return Results.BadRequest(new { error = "Unsupported HomeWatch backup format." });

                var backupDb = Path.Combine(extractPath, "homewatch.db");
                if (!File.Exists(backupDb)) return Results.BadRequest(new { error = "The backup database is missing." });
                if (manifestDoc.RootElement.TryGetProperty("databaseSha256", out var checksumNode))
                {
                    var expected = checksumNode.GetString();
                    if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(expected, Sha256(backupDb), StringComparison.OrdinalIgnoreCase))
                        return Results.BadRequest(new { error = "Backup database checksum validation failed." });
                }

                await RestoreDatabaseSnapshotAsync(db, backupDb, ct);

                var dataPath = Path.Combine(extractPath, "data");
                if (Directory.Exists(dataPath))
                {
                    foreach (var source in Directory.EnumerateFiles(dataPath, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(dataPath, source);
                        if (!IsSafeRelativePath(relative)) continue;
                        var destination = Path.Combine(env.ContentRootPath, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(source, destination, true);
                    }
                }

                return Results.Ok(new
                {
                    restored = true,
                    restartRequired = true,
                    message = "Full HomeWatch data and settings were restored. Restart HomeWatch now."
                });
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }).DisableAntiforgery();
    }

    private static async Task<object> BuildExportAsync(HomeWatchDb db, HashSet<Guid> ignoredDeviceIds, CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().Where(x => !ignoredDeviceIds.Contains(x.Id)).OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.IpAddress, x.MacAddress, x.Vendor, x.FirstSeen, x.LastSeen }).ToListAsync(ct);
        var ids = devices.Select(x => x.Id).ToHashSet();
        var events = await db.Events.AsNoTracking().Where(x => ids.Contains(x.DeviceId)).OrderByDescending(x => x.Timestamp)
            .Select(x => new { x.Id, x.Timestamp, x.DeviceId, x.Domain, x.Category, x.Action, x.Source }).ToListAsync(ct);
        var sessions = await db.Sessions.AsNoTracking().Where(x => ids.Contains(x.DeviceId)).OrderByDescending(x => x.StartedAt)
            .Select(x => new { x.Id, x.DeviceId, x.StartedAt, x.EndedAt, x.Confidence, x.Assessment }).ToListAsync(ct);
        var alerts = await db.Alerts.AsNoTracking().Where(x => ids.Contains(x.DeviceId)).OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.SessionId, x.DeviceId, x.Severity, x.Title, x.Detail, x.CreatedAt, x.Acknowledged, x.AcknowledgedAt }).ToListAsync(ct);
        return new
        {
            exportFormat = "homewatch-complete-activity-v1",
            generatedUtc = DateTime.UtcNow,
            ignoredDevicesExcluded = ignoredDeviceIds.Count > 0,
            ignoredDeviceIds,
            totals = new { devices = devices.Count, events = events.Count, sessions = sessions.Count, alerts = alerts.Count },
            devices,
            events,
            sessions,
            alerts
        };
    }

    private static HashSet<Guid> ReadIgnoredDeviceIds(string root)
    {
        var result = new HashSet<Guid>();
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var text = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"device-([0-9a-fA-F-]{36})\.homewatch-device\.local"))
                    if (Guid.TryParse(match.Groups[1].Value, out var id)) result.Add(id);
            }
            catch { }
        }
        return result;
    }

    private static IEnumerable<string> FindBackupFiles(string root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "appsettings.json", "appsettings.Production.json", "runtime-settings.json", "adult-intelligence-cache.json",
            "adult-safe-overrides.json", "ignored-domains.json", "network-discovery.json"
        };
        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(file => names.Contains(Path.GetFileName(file)) || Path.GetFileName(file).StartsWith("homewatch-", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task CreateDatabaseSnapshotAsync(HomeWatchDb db, string destination, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var source = (SqliteConnection)db.Database.GetDbConnection();
            await using var target = new SqliteConnection($"Data Source={destination}");
            await target.OpenAsync(ct);
            source.BackupDatabase(target);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task RestoreDatabaseSnapshotAsync(HomeWatchDb db, string sourcePath, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var destination = (SqliteConnection)db.Database.GetDbConnection();
            await using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly");
            await source.OpenAsync(ct);
            source.BackupDatabase(destination);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void SafeExtract(string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup contains an unsafe path.");
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static bool IsSafeRelativePath(string path) =>
        !Path.IsPathRooted(path) && !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("..");
}
