using HomeWatch3.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

public sealed class EvidenceDomainPolicy
{
    private readonly HashSet<string> _trustedRoots = new(StringComparer.OrdinalIgnoreCase);

    public EvidenceDomainPolicy(IConfiguration configuration)
    {
        Add("teamelevation.synology.me");
        Add(configuration["HomeWatch:PublicUrl"]);
        Add(configuration["Authentication:MoneyPilotBaseUrl"]);

        foreach (var child in configuration.GetSection("HomeWatch:TrustedDomains").GetChildren())
            Add(child.Value);
    }

    public bool IsTrusted(string? domain)
    {
        var value = Normalize(domain);
        return value is not null && _trustedRoots.Any(root =>
            value.Equals(root, StringComparison.OrdinalIgnoreCase)
            || value.EndsWith('.' + root, StringComparison.OrdinalIgnoreCase));
    }

    public bool ContainsTrustedReference(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && _trustedRoots.Any(root => text.Contains(root, StringComparison.OrdinalIgnoreCase));

    public string[] GetTrustedDomains() => _trustedRoots.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<object> RemoveHistoricalArtifactsAsync(HomeWatchDb db, CancellationToken cancellationToken = default)
    {
        var removeEvents = new Dictionary<long, TrafficEvent>();
        foreach (var root in _trustedRoots)
        {
            var matches = await db.TrafficEvents
                .Where(x => x.Domain != null && (x.Domain == root || x.Domain.EndsWith("." + root)))
                .ToListAsync(cancellationToken);
            foreach (var match in matches) removeEvents[match.Id] = match;
        }

        var alerts = await db.Alerts
            .Where(x => x.Type == "adult-content")
            .ToListAsync(cancellationToken);
        var removeAlerts = alerts.Where(x => ContainsTrustedReference(x.Message)).ToArray();

        if (removeEvents.Count > 0) db.TrafficEvents.RemoveRange(removeEvents.Values);
        if (removeAlerts.Length > 0) db.Alerts.RemoveRange(removeAlerts);
        if (removeEvents.Count > 0 || removeAlerts.Length > 0) await db.SaveChangesAsync(cancellationToken);

        return new { removedEvents = removeEvents.Count, removedAlerts = removeAlerts.Length };
    }

    private void Add(string? candidate)
    {
        var domain = Normalize(candidate);
        if (domain is not null && domain.Contains('.')) _trustedRoots.Add(domain);
    }

    private static string? Normalize(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        var value = candidate.Trim();
        if (Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
            value = uri.Host;
        value = value.Trim().Trim('.').ToLowerInvariant();
        return value.Length is > 0 and <= 253 ? value : null;
    }
}
