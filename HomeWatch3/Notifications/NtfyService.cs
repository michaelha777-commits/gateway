using System.Text;
using Microsoft.Extensions.Options;

namespace HomeWatch3.Notifications;

public sealed class NtfyOptions
{
    public const string SectionName = "Ntfy";
    public string TopicUrl { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public interface INtfyService
{
    Task<bool> SendEventAsync(string eventType, string title, string message, CancellationToken cancellationToken = default);
    Task<bool> SendTestAsync(string title, string message, string? priority = null, CancellationToken cancellationToken = default);
}

public sealed class NtfyService(HttpClient httpClient, IOptions<NtfyOptions> options, NtfyNotificationSettingsStore settings) : INtfyService
{
    private readonly NtfyOptions _options = options.Value;

    public Task<bool> SendEventAsync(string eventType, string title, string message, CancellationToken cancellationToken = default)
    {
        return settings.TryGetDelivery(eventType, out var priority)
            ? SendCoreAsync(title, message, priority, cancellationToken)
            : Task.FromResult(false);
    }

    public Task<bool> SendTestAsync(string title, string message, string? priority = null, CancellationToken cancellationToken = default) =>
        SendCoreAsync(title, message, priority, cancellationToken);

    private async Task<bool> SendCoreAsync(string title, string message, string? priority, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.TopicUrl))
            return false;

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TopicUrl)
        {
            Content = new StringContent(message, Encoding.UTF8, "text/plain")
        };
        request.Headers.TryAddWithoutValidation("Title", title);
        var normalizedPriority = new[] { "min", "low", "default", "high", "max" }
            .FirstOrDefault(x => x.Equals(priority, StringComparison.OrdinalIgnoreCase)) ?? "default";
        request.Headers.TryAddWithoutValidation("Priority", normalizedPriority);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
