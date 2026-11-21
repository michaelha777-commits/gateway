using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace HomeWatch3.Authentication;

public sealed class HomeWatchAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public bool Enabled { get; set; } = true;
    public string? MoneyPilotBaseUrl { get; set; }
    public string MoneyPilotEnvironmentFile { get; set; } = "/volume1/moneypilot-data/app/server/.env";
    public bool AllowInvalidCertificate { get; set; }
    public string CookieName { get; set; } = "homewatch.session";
    public int ValidationCacheSeconds { get; set; } = 120;

    public Uri ResolveMoneyPilotBaseUri()
    {
        if (Uri.TryCreate(MoneyPilotBaseUrl?.TrimEnd('/'), UriKind.Absolute, out var configured))
        {
            return configured;
        }

        var port = ReadEnvironmentValue(MoneyPilotEnvironmentFile, "PORT");
        if (!int.TryParse(port, out var parsedPort) || parsedPort is < 1 or > 65535)
        {
            parsedPort = 3000;
        }

        return new Uri($"http://127.0.0.1:{parsedPort}");
    }

    private static string? ReadEnvironmentValue(string? path, string key)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var separator = line.IndexOf('=');
                if (separator <= 0 || !line[..separator].Trim().Equals(key, StringComparison.Ordinal)) continue;

                var value = line[(separator + 1)..].Trim();
                if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                {
                    value = value[1..^1];
                }

                return value;
            }
        }
        catch (IOException)
        {
            // The configured URL or default port remains available when the
            // MoneyPilot runtime environment file cannot be read.
        }
        catch (UnauthorizedAccessException)
        {
            // Do not fail open. Authentication requests will return a clear
            // service-unavailable response if the resolved endpoint is wrong.
        }

        return null;
    }
}

public sealed record MoneyPilotLoginRequest(string Email, string Password, string Code, bool RememberDevice);
public sealed record MoneyPilotPasswordResetRequest(string Email, string Code, string NewPassword);
public sealed record HomeWatchAuthenticatedUser(string Email, string Role);

public enum TokenValidationState
{
    Valid,
    Invalid,
    ProviderUnavailable
}

public sealed record TokenValidationResult(TokenValidationState State, HomeWatchAuthenticatedUser? User = null)
{
    public static TokenValidationResult Valid(HomeWatchAuthenticatedUser user) => new(TokenValidationState.Valid, user);
    public static TokenValidationResult Invalid() => new(TokenValidationState.Invalid);
    public static TokenValidationResult ProviderUnavailable() => new(TokenValidationState.ProviderUnavailable);
}

public sealed record AuthenticationProxyResult(
    bool Success,
    int StatusCode,
    string Message,
    string? Token = null,
    HomeWatchAuthenticatedUser? User = null,
    int ExpiresInSeconds = 0,
    bool RememberedDevice = false);

public interface IMoneyPilotAuthenticationClient
{
    Task<AuthenticationProxyResult> LoginAsync(MoneyPilotLoginRequest request, CancellationToken cancellationToken);
    Task<AuthenticationProxyResult> ResetPasswordAsync(MoneyPilotPasswordResetRequest request, CancellationToken cancellationToken);
    Task<TokenValidationResult> ValidateTokenAsync(string token, CancellationToken cancellationToken);
}

public sealed class MoneyPilotAuthenticationClient : IMoneyPilotAuthenticationClient
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly HomeWatchAuthenticationOptions _options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _validationLocks = new(StringComparer.Ordinal);

    public MoneyPilotAuthenticationClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<HomeWatchAuthenticationOptions> options)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
    }

    public async Task<AuthenticationProxyResult> LoginAsync(MoneyPilotLoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password) || !IsSixDigitCode(request.Code))
        {
            return new AuthenticationProxyResult(false, StatusCodes.Status400BadRequest, "Enter your email, password, and current 6-digit authenticator code.");
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/api/auth/login", new
            {
                email = request.Email.Trim(),
                password = request.Password,
                code = NormalizeCode(request.Code),
                rememberDevice = request.RememberDevice
            }, cancellationToken);

            using var payload = await ReadPayloadAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new AuthenticationProxyResult(false, (int)response.StatusCode, ReadMessage(payload, "Login failed"));
            }

            if (!TryReadLogin(payload, out var token, out var user, out var expiresInSeconds, out var rememberedDevice))
            {
                return new AuthenticationProxyResult(false, StatusCodes.Status502BadGateway, "MoneyPilot returned an invalid authentication response.");
            }

            CacheValidatedToken(token, user, expiresInSeconds);
            return new AuthenticationProxyResult(true, StatusCodes.Status200OK, "Signed in", token, user, expiresInSeconds, rememberedDevice);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderUnavailable();
        }
        catch (HttpRequestException)
        {
            return ProviderUnavailable();
        }
        catch (JsonException)
        {
            return new AuthenticationProxyResult(false, StatusCodes.Status502BadGateway, "MoneyPilot returned an invalid authentication response.");
        }
    }

    public async Task<AuthenticationProxyResult> ResetPasswordAsync(MoneyPilotPasswordResetRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || !IsSixDigitCode(request.Code) || string.IsNullOrEmpty(request.NewPassword) || request.NewPassword.Length < 12)
        {
            return new AuthenticationProxyResult(false, StatusCodes.Status400BadRequest, "Enter your email, current 6-digit authenticator code, and a new password of at least 12 characters.");
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/api/auth/forgot-password", new
            {
                email = request.Email.Trim(),
                code = NormalizeCode(request.Code),
                newPassword = request.NewPassword
            }, cancellationToken);

            using var payload = await ReadPayloadAsync(response, cancellationToken);
            var message = ReadMessage(payload, response.IsSuccessStatusCode ? "Password updated successfully" : "Password reset failed");
            return new AuthenticationProxyResult(response.IsSuccessStatusCode, (int)response.StatusCode, message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderUnavailable();
        }
        catch (HttpRequestException)
        {
            return ProviderUnavailable();
        }
        catch (JsonException)
        {
            return new AuthenticationProxyResult(false, StatusCodes.Status502BadGateway, "MoneyPilot returned an invalid authentication response.");
        }
    }

    public async Task<TokenValidationResult> ValidateTokenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return TokenValidationResult.Invalid();

        var cacheKey = GetCacheKey(token);
        if (_cache.TryGetValue(cacheKey, out HomeWatchAuthenticatedUser? cachedUser) && cachedUser is not null)
        {
            return TokenValidationResult.Valid(cachedUser);
        }

        var validationLock = _validationLocks.GetOrAdd(cacheKey, static _ => new SemaphoreSlim(1, 1));
        await validationLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(cacheKey, out cachedUser) && cachedUser is not null)
            {
                return TokenValidationResult.Valid(cachedUser);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return TokenValidationResult.Invalid();
                }

                if (!response.IsSuccessStatusCode)
                {
                    return TokenValidationResult.ProviderUnavailable();
                }

                using var payload = await ReadPayloadAsync(response, cancellationToken);
                if (!TryReadUser(payload, out var user)) return TokenValidationResult.Invalid();

                CacheValidatedToken(token, user, GetRemainingTokenLifetimeSeconds(token));
                return TokenValidationResult.Valid(user);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return TokenValidationResult.ProviderUnavailable();
            }
            catch (HttpRequestException)
            {
                return TokenValidationResult.ProviderUnavailable();
            }
            catch (JsonException)
            {
                return TokenValidationResult.ProviderUnavailable();
            }
        }
        finally
        {
            validationLock.Release();
            _validationLocks.TryRemove(cacheKey, out _);
        }
    }

    private void CacheValidatedToken(string token, HomeWatchAuthenticatedUser user, int tokenLifetimeSeconds)
    {
        var configuredSeconds = Math.Clamp(_options.ValidationCacheSeconds, 15, 300);
        var lifetimeSeconds = tokenLifetimeSeconds > 0 ? Math.Min(configuredSeconds, tokenLifetimeSeconds) : configuredSeconds;
        _cache.Set(GetCacheKey(token), user, TimeSpan.FromSeconds(Math.Max(1, lifetimeSeconds)));
    }

    private static string GetCacheKey(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return $"homewatch-auth:{Convert.ToHexString(hash)}";
    }

    private static int GetRemainingTokenLifetimeSeconds(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return 0;
            var payloadText = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var payload = JsonDocument.Parse(payloadText);
            if (!payload.RootElement.TryGetProperty("exp", out var expiration) || !expiration.TryGetInt64(out var expirationSeconds)) return 0;
            return (int)Math.Clamp(expirationSeconds - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0, int.MaxValue);
        }
        catch (Exception error) when (error is FormatException or JsonException or OverflowException)
        {
            return 0;
        }
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }

    private static async Task<JsonDocument> ReadPayloadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static bool TryReadLogin(
        JsonDocument payload,
        out string token,
        out HomeWatchAuthenticatedUser user,
        out int expiresInSeconds,
        out bool rememberedDevice)
    {
        token = string.Empty;
        user = new HomeWatchAuthenticatedUser(string.Empty, string.Empty);
        expiresInSeconds = 0;
        rememberedDevice = false;

        var root = payload.RootElement;
        if (!root.TryGetProperty("token", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String) return false;
        token = tokenElement.GetString()?.Trim() ?? string.Empty;
        if (token.Length == 0 || !TryReadUser(payload, out user)) return false;

        if (!root.TryGetProperty("expiresInSeconds", out var expiresElement) || !expiresElement.TryGetInt32(out expiresInSeconds)) return false;
        expiresInSeconds = Math.Clamp(expiresInSeconds, 60, 30 * 24 * 60 * 60);
        rememberedDevice = root.TryGetProperty("rememberedDevice", out var rememberedElement) && rememberedElement.ValueKind == JsonValueKind.True;
        return true;
    }

    private static bool TryReadUser(JsonDocument payload, out HomeWatchAuthenticatedUser user)
    {
        user = new HomeWatchAuthenticatedUser(string.Empty, string.Empty);
        var root = payload.RootElement;
        if (!root.TryGetProperty("user", out var userElement) || userElement.ValueKind != JsonValueKind.Object) return false;
        if (!userElement.TryGetProperty("email", out var emailElement) || emailElement.ValueKind != JsonValueKind.String) return false;
        if (!userElement.TryGetProperty("role", out var roleElement) || roleElement.ValueKind != JsonValueKind.String) return false;

        var email = emailElement.GetString()?.Trim() ?? string.Empty;
        var role = roleElement.GetString()?.Trim() ?? string.Empty;
        if (email.Length == 0 || !role.Equals("owner", StringComparison.Ordinal)) return false;

        user = new HomeWatchAuthenticatedUser(email, role);
        return true;
    }

    private static string ReadMessage(JsonDocument payload, string fallback)
    {
        if (!payload.RootElement.TryGetProperty("message", out var message)) return fallback;
        if (message.ValueKind == JsonValueKind.String) return message.GetString() ?? fallback;
        if (message.ValueKind == JsonValueKind.Array)
        {
            var values = message.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!);
            var combined = string.Join(" ", values);
            if (combined.Length > 0) return combined;
        }

        return fallback;
    }

    private static bool IsSixDigitCode(string? code) => NormalizeCode(code).Length == 6 && NormalizeCode(code).All(char.IsDigit);
    private static string NormalizeCode(string? code) => string.Concat((code ?? string.Empty).Where(static value => !char.IsWhiteSpace(value)));
    private static AuthenticationProxyResult ProviderUnavailable() => new(false, StatusCodes.Status503ServiceUnavailable, "MoneyPilot authentication is temporarily unavailable.");
}

public static class HomeWatchAuthenticationMiddleware
{
    public const string UserItemKey = "HomeWatch.AuthenticatedUser";

    public static IApplicationBuilder UseHomeWatchAuthentication(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<IOptions<HomeWatchAuthenticationOptions>>().Value;
            if (!options.Enabled || IsPublicPath(context.Request.Path))
            {
                await next();
                return;
            }

            var token = ReadBearerToken(context.Request) ?? context.Request.Cookies[options.CookieName];
            if (string.IsNullOrWhiteSpace(token))
            {
                await RejectAsync(context, providerUnavailable: false);
                return;
            }

            var authentication = context.RequestServices.GetRequiredService<IMoneyPilotAuthenticationClient>();
            var validation = await authentication.ValidateTokenAsync(token, context.RequestAborted);
            if (validation.State != TokenValidationState.Valid || validation.User is null)
            {
                if (validation.State == TokenValidationState.Invalid)
                {
                    context.Response.Cookies.Delete(options.CookieName, new CookieOptions { Path = "/" });
                }

                await RejectAsync(context, validation.State == TokenValidationState.ProviderUnavailable);
                return;
            }

            context.Items[UserItemKey] = validation.User;
            await next();
        });
    }

    public static HomeWatchAuthenticatedUser? GetAuthenticatedUser(this HttpContext context) =>
        context.Items.TryGetValue(UserItemKey, out var value) ? value as HomeWatchAuthenticatedUser : null;

    public static string? ReadRequestToken(HttpRequest request)
    {
        var options = request.HttpContext.RequestServices.GetRequiredService<IOptions<HomeWatchAuthenticationOptions>>().Value;
        return ReadBearerToken(request) ?? request.Cookies[options.CookieName];
    }

    public static void WriteSessionCookie(HttpResponse response, HomeWatchAuthenticationOptions options, string token, int expiresInSeconds, bool rememberedDevice)
    {
        var cookie = new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Strict,
            Secure = response.HttpContext.Request.IsHttps,
            Path = "/"
        };

        if (rememberedDevice)
        {
            cookie.MaxAge = TimeSpan.FromSeconds(expiresInSeconds);
            cookie.Expires = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds);
        }

        response.Cookies.Append(options.CookieName, token, cookie);
    }

    private static string? ReadBearerToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim()
            : null;
    }

    private static bool IsPublicPath(PathString path)
    {
        var value = path.Value ?? string.Empty;
        return value.Equals("/api/status", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("/api/auth/forgot-password", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("/login.html", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("/login.js", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("/auth.css", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RejectAsync(HttpContext context, bool providerUnavailable)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = providerUnavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status401Unauthorized;
            if (!providerUnavailable) context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new
            {
                error = providerUnavailable
                    ? "MoneyPilot authentication is temporarily unavailable."
                    : "Authentication is required."
            });
            return;
        }

        if (providerUnavailable)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("HomeWatch cannot verify your session because MoneyPilot authentication is temporarily unavailable.");
            return;
        }

        var returnUrl = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
        context.Response.Redirect($"/login.html?returnUrl={Uri.EscapeDataString(returnUrl)}");
    }
}
