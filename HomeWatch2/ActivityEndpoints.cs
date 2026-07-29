using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;

public static class ActivityEndpoints
{
    private const int DefaultPageSize = 100;
    private const int MaximumPageSize = 500;

    public static void MapActivityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/activity", GetActivityAsync);
        app.MapGet("/api/activity/summary", GetSummaryAsync);
    }

    private static async Task<IResult> GetActivityAsync(
        HomeWatchDb db, DateTimeOffset? from, DateTimeOffset? to, Guid? deviceId,
        string? category, string? search, string? cursor, int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var range = ValidateRange(from, to);
        if (range.Error is not null) return Results.BadRequest(new { error = range.Error });
        if (!TryDecodeCursor(cursor, out var beforeTimestamp, out var beforeId))
            return Results.BadRequest(new { error = "The activity cursor is invalid." });

        pageSize = Math.Clamp(pageSize, 1, MaximumPageSize);
        var query = Filter(db.Events.AsNoTracking(), range.From, range.To, deviceId, category, search);
        if (beforeTimestamp is not null)
            query = query.Where(x => x.Timestamp < beforeTimestamp || (x.Timestamp == beforeTimestamp && x.Id < beforeId));

        var rows = await query.OrderByDescending(x => x.Timestamp).ThenByDescending(x => x.Id)
            .Select(x => new ActivityRow(x.Id, x.Timestamp, x.Domain, x.Category, x.Action, x.Source,
                x.DeviceId, x.Device != null ? x.Device.Name : "Unknown device", x.Device != null ? x.Device.IpAddress : null))
            .Take(pageSize + 1).ToListAsync(cancellationToken);
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var last = rows.LastOrDefault();

        return Results.Ok(new
        {
            events = rows.Select(ToResponse),
            page = new { pageSize, hasMore, nextCursor = hasMore && last is not null ? EncodeCursor(last.Timestamp, last.Id) : null },
            range = new { from = Iso(range.From), to = Iso(range.To), allHistory = range.From is null },
            generatedAt = Iso(DateTime.UtcNow)
        });
    }

    private static async Task<IResult> GetSummaryAsync(
        HomeWatchDb db, DateTimeOffset? from, DateTimeOffset? to, Guid? deviceId,
        string? category, string? search, CancellationToken cancellationToken = default)
    {
        var range = ValidateRange(from, to);
        if (range.Error is not null) return Results.BadRequest(new { error = range.Error });
        var query = Filter(db.Events.AsNoTracking(), range.From, range.To, deviceId, category, search);

        var totals = await query.GroupBy(_ => 1).Select(g => new
        {
            eventCount = g.Count(), uniqueDomains = g.Select(x => x.Domain).Distinct().Count(),
            activeDevices = g.Select(x => x.DeviceId).Distinct().Count(),
            blockedCount = g.Count(x => x.Action.ToLower().Contains("block")),
            oldest = g.Min(x => (DateTime?)x.Timestamp), newest = g.Max(x => (DateTime?)x.Timestamp)
        }).FirstOrDefaultAsync(cancellationToken);
        var categories = await query.GroupBy(x => x.Category).Select(g => new { category = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count).ToListAsync(cancellationToken);

        return Results.Ok(new
        {
            eventCount = totals?.eventCount ?? 0, uniqueDomains = totals?.uniqueDomains ?? 0,
            activeDevices = totals?.activeDevices ?? 0, blockedCount = totals?.blockedCount ?? 0,
            oldest = Iso(totals?.oldest), newest = Iso(totals?.newest), categories,
            range = new { from = Iso(range.From), to = Iso(range.To), allHistory = range.From is null },
            generatedAt = Iso(DateTime.UtcNow)
        });
    }

    public static IQueryable<ActivityEvent> Filter(IQueryable<ActivityEvent> query, DateTime? from, DateTime? to,
        Guid? deviceId = null, string? category = null, string? search = null)
    {
        if (from is not null) query = query.Where(x => x.Timestamp >= from);
        if (to is not null) query = query.Where(x => x.Timestamp < to);
        if (deviceId is not null) query = query.Where(x => x.DeviceId == deviceId);
        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.Category == category.Trim().ToLower());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLike(search.Trim().ToLowerInvariant())}%";
            query = query.Where(x => EF.Functions.Like(x.Domain.ToLower(), pattern, "\\"));
        }
        return query;
    }

    public static (DateTime? From, DateTime? To, string? Error) ValidateRange(DateTimeOffset? from, DateTimeOffset? to)
    {
        var utcFrom = from?.UtcDateTime;
        var utcTo = to?.UtcDateTime;
        return utcFrom is not null && utcTo is not null && utcFrom >= utcTo
            ? (utcFrom, utcTo, "'from' must be earlier than 'to'.")
            : (utcFrom, utcTo, null);
    }

    private static object ToResponse(ActivityRow x) => new
    {
        x.Id, timestamp = Iso(x.Timestamp), x.Domain, x.Category, x.Action, x.Source,
        deviceId = x.DeviceId, deviceName = x.DeviceName, deviceIp = x.DeviceIp
    };

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    private static string? Iso(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc).ToString("O");
    private static string EncodeCursor(DateTime timestamp, long id) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{timestamp.Ticks}:{id}"));
    private static bool TryDecodeCursor(string? cursor, out DateTime? timestamp, out long id)
    {
        timestamp = null; id = 0;
        if (string.IsNullOrWhiteSpace(cursor)) return true;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id)) return false;
            timestamp = new DateTime(ticks, DateTimeKind.Utc);
            return id > 0;
        }
        catch (FormatException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private sealed record ActivityRow(long Id, DateTime Timestamp, string Domain, string Category, string Action,
        string Source, Guid DeviceId, string DeviceName, string? DeviceIp);
}
