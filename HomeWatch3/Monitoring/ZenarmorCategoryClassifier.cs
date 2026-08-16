namespace HomeWatch3.Monitoring;

public sealed record ZenarmorCategoryResult(bool IsAdult, int Confidence, string Category, string Evidence);

public interface IZenarmorCategoryClassifier
{
    ZenarmorCategoryResult Classify(string? category);
}

public sealed class ZenarmorCategoryClassifier : IZenarmorCategoryClassifier
{
    private static readonly string[] AdultCategories =
    {
        "adult",
        "pornography",
        "porn"
    };

    public ZenarmorCategoryResult Classify(string? category)
    {
        var normalized = (category ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return new(false, 0, string.Empty, "No Zenarmor web category supplied");

        foreach (var item in AdultCategories)
        {
            if (normalized.Equals(item, StringComparison.OrdinalIgnoreCase))
            {
                return new(true, 100, normalized,
                    $"Zenarmor classified the traffic as '{normalized}'");
            }
        }

        return new(false, 20, normalized,
            $"Zenarmor category '{normalized}' is not an adult category");
    }
}
