using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public static class AdultSafetyOverrides
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> SafeRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "scorecardresearch.com",
        "servedbyadbutler.com"
    };
    private static string? storagePath;
    private static bool loaded;

    public static void Configure(IWebHostEnvironment environment)
    {
        lock (Gate)
        {
            storagePath = Path.Combine(environment.ContentRootPath, "adult-safe-overrides.json");
            LoadLocked();
            RepairKnownFalsePositivesLocked(environment.ContentRootPath);
        }
    }

    public static bool IsSafe(string domain)
    {
        var value = Normalize(domain);
        if (string.IsNullOrWhiteSpace(value)) return false;
        lock (Gate)
        {
            LoadLocked();
            return SafeRoots.Any(root => value == root || value.EndsWith("." + root, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static async Task<string> AddAsync(string domain, CancellationToken cancellationToken = default)
    {
        var root = RootDomain(Normalize(domain));
        if (string.IsNullOrWhiteSpace(root) || !root.Contains('.')) throw new ArgumentException("Enter a valid domain.", nameof(domain));
        string? path;
        string[] snapshot;
        lock (Gate)
        {
            LoadLocked();
            SafeRoots.Add(root);
            path = storagePath;
            snapshot = SafeRoots.OrderBy(x => x).ToArray();
        }
        if (!string.IsNullOrWhiteSpace(path))
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return root;
    }

    public static object Status()
    {
        lock (Gate)
        {
            LoadLocked();
            return new { safeDomainCount = SafeRoots.Count, domains = SafeRoots.OrderBy(x => x).ToArray() };
        }
    }

    public static void MapEndpoints(this WebApplication app)
    {
        app.MapGet("/api/intelligence/adult/safe-overrides", () => Results.Ok(Status()));
        app.MapPost("/api/intelligence/adult/false-positive", async (FalsePositiveRequest request, HomeWatchDb db, CancellationToken ct) =>
        {
            var root = await AddAsync(request.Domain, ct);
            var events = await db.Events.Where(x => x.Domain == root || x.Domain.EndsWith("." + root)).ToListAsync(ct);
            foreach (var item in events.Where(x => x.Category == "adult"))
            {
                item.Category = IsAdvertisingOrTracking(root) ? "advertising" : "dns";
                item.Source = "user-safe-override";
            }

            var alerts = await db.Alerts.Where(x => !x.Acknowledged && x.Detail.Contains(root)).ToListAsync(ct);
            foreach (var alert in alerts)
            {
                alert.Acknowledged = true;
                alert.AcknowledgedAt = DateTime.UtcNow;
                if (!alert.Detail.Contains("Marked not adult", StringComparison.OrdinalIgnoreCase))
                    alert.Detail += " | Marked not adult by user.";
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { domain = root, reclassifiedEvents = events.Count, acknowledgedAlerts = alerts.Count });
        });
    }

    public static string RootDomain(string domain)
    {
        var normalized = Normalize(domain);
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? string.Join('.', parts[^2], parts[^1]) : normalized;
    }

    private static bool IsAdvertisingOrTracking(string domain) =>
        domain.Contains("adbutler", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("scorecardresearch", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("analytics", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("tracking", StringComparison.OrdinalIgnoreCase);

    private static void RepairKnownFalsePositivesLocked(string contentRootPath)
    {
        try
        {
            var databasePath = Path.Combine(contentRootPath, "homewatch.db");
            if (!File.Exists(databasePath)) return;

            using var connection = new SqliteConnection($"Data Source={databasePath}");
            connection.Open();

            foreach (var root in SafeRoots)
            {
                using (var eventCommand = connection.CreateCommand())
                {
                    eventCommand.CommandText = """
                        UPDATE Events
                        SET Category = CASE WHEN $tracking = 1 THEN 'advertising' ELSE 'dns' END,
                            Source = 'safe-override:auto-repair'
                        WHERE Category = 'adult'
                          AND (lower(Domain) = $root OR lower(Domain) LIKE $suffix);
                        """;
                    eventCommand.Parameters.AddWithValue("$tracking", IsAdvertisingOrTracking(root) ? 1 : 0);
                    eventCommand.Parameters.AddWithValue("$root", root.ToLowerInvariant());
                    eventCommand.Parameters.AddWithValue("$suffix", "%." + root.ToLowerInvariant());
                    eventCommand.ExecuteNonQuery();
                }

                using var alertCommand = connection.CreateCommand();
                alertCommand.CommandText = """
                    UPDATE Alerts
                    SET Acknowledged = 1,
                        AcknowledgedAt = COALESCE(AcknowledgedAt, $now),
                        Detail = CASE
                            WHEN Detail LIKE '%Automatically removed: safe domain.%' THEN Detail
                            ELSE Detail || ' | Automatically removed: safe domain.'
                        END
                    WHERE Acknowledged = 0
                      AND lower(Detail) LIKE $contains;
                    """;
                alertCommand.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                alertCommand.Parameters.AddWithValue("$contains", "%" + root.ToLowerInvariant() + "%");
                alertCommand.ExecuteNonQuery();
            }
        }
        catch
        {
            // Startup must not fail if the database is missing, locked, or still being created.
        }
    }

    private static void LoadLocked()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (string.IsNullOrWhiteSpace(storagePath) || !File.Exists(storagePath)) return;
            var saved = JsonSerializer.Deserialize<string[]>(File.ReadAllText(storagePath)) ?? Array.Empty<string>();
            foreach (var item in saved.Select(Normalize).Where(x => !string.IsNullOrWhiteSpace(x))) SafeRoots.Add(item);
        }
        catch { }
    }

    private static string Normalize(string value)
    {
        var candidate = (value ?? "").Trim().Trim('.').ToLowerInvariant();
        if ((candidate.StartsWith("http://") || candidate.StartsWith("https://")) && Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) candidate = uri.Host;
        return candidate.Trim('.');
    }
}

public sealed record FalsePositiveRequest(string Domain);