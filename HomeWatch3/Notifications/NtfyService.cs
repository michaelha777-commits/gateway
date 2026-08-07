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
    Task<bool> SendAsync(string title, string message, string? priority = null, CancellationToken cancellationToken = default);
}

public sealed class NtfyService(HttpClient httpClient, IOptions<NtfyOptions> options) : INtfyService
{
    private readonly NtfyOptions _options = options.Value;

    public async Task<bool> SendAsync(string title, string message, string? priority = null, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.TopicUrl))
            return false;

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TopicUrl)
        {
            Content = new StringContent(message, Encoding.UTF8, "text/plain")
        };
        request.Headers.TryAddWithoutValidation("Title", title);
        if (!string.IsNullOrWhiteSpace(priority))
            request.Headers.TryAddWithoutValidation("Priority", priority);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
