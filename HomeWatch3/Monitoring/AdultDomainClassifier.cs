namespace HomeWatch3.Monitoring;

public sealed record AdultDomainResult(bool IsAdult, int Confidence, string Evidence);

public interface IAdultDomainClassifier
{
    AdultDomainResult Classify(string? domain);
}

public sealed class AdultDomainClassifier : IAdultDomainClassifier
{
    // Phase-one detector: only high-confidence brand labels. This deliberately
    // favors avoiding false alerts over broad classification. Larger curated
    // intelligence feeds can be layered in after the live path is proven.
    private static readonly string[] AdultBrandTokens =
    {
        "xnxx", "xvideos", "pornhub", "xhamster", "redtube", "youporn",
        "spankbang", "tube8", "brazzers", "erome", "jerkmate", "chaturbate",
        "stripchat", "livejasmin", "bongacams", "myfreecams", "onlyfans",
        "nhentai", "hentaihaven", "rule34", "literotica", "anysex", "eporner",
        "hqporner", "tnaflix", "drtuber", "motherless", "fapello", "camsoda"
    };

    public AdultDomainResult Classify(string? domain)
    {
        var normalized = Normalize(domain);
        if (normalized.Length == 0)
            return new(false, 0, "No domain");

        foreach (var label in normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var token in AdultBrandTokens)
            {
                if (label.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                    label.StartsWith(token + "-", StringComparison.OrdinalIgnoreCase) ||
                    label.EndsWith("-" + token, StringComparison.OrdinalIgnoreCase))
                {
                    return new(true, 98, $"Known adult brand label: {token}");
                }
            }
        }

        return new(false, 10, "No high-confidence adult signal");
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var result = value.Trim().Trim('.').ToLowerInvariant();
        if (result.StartsWith("http://", StringComparison.Ordinal)) result = result[7..];
        if (result.StartsWith("https://", StringComparison.Ordinal)) result = result[8..];
        var slash = result.IndexOf('/');
        if (slash >= 0) result = result[..slash];
        var colon = result.IndexOf(':');
        if (colon >= 0) result = result[..colon];
        return result;
    }
}
