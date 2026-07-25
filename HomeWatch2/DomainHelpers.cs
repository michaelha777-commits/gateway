global using static DomainHelpers;

// Shared domain helper used by both top-level endpoints and background services.
public static class DomainHelpers
{
    public static string RootDomain(string domain)
    {
        var normalized = (domain ?? string.Empty).Trim('.').ToLowerInvariant();
        var parts = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            ? string.Join('.', parts[^2], parts[^1])
            : normalized;
    }
}
