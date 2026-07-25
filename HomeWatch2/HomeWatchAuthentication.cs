using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;

public static class HomeWatchAuthentication
{
    private const string Scheme = "HomeWatchCookie";
    private const string CookieName = "HomeWatch.Auth";

    public static IServiceCollection AddHomeWatchAuthentication(this IServiceCollection services)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownNetworks.Clear();
            options.KnownProxies.Clear();
        });

        services.AddAuthentication(Scheme)
            .AddCookie(Scheme, options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                options.SlidingExpiration = true;
                options.LoginPath = "/login.html";
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }
                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorization();
        services.AddSingleton<HomeWatchTokenStore>();
        return services;
    }

    public static WebApplication UseHomeWatchAuthentication(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseAuthentication();
        app.UseAuthorization();

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            var isPublic = path.StartsWithSegments("/login.html") ||
                           path.StartsWithSegments("/auth") ||
                           path.StartsWithSegments("/login-assets");

            if (isPublic || context.User.Identity?.IsAuthenticated == true)
            {
                await next();
                return;
            }

            if (path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "Authentication required." });
                return;
            }

            var returnUrl = Uri.EscapeDataString(context.Request.PathBase + context.Request.Path + context.Request.QueryString);
            context.Response.Redirect($"/login.html?returnUrl={returnUrl}");
        });

        return app;
    }

    public static WebApplication MapHomeWatchAuthentication(this WebApplication app)
    {
        app.MapGet("/auth/status", (HttpContext context, HomeWatchTokenStore store) => Results.Ok(new
        {
            authenticated = context.User.Identity?.IsAuthenticated == true,
            configured = store.IsConfigured
        }));

        app.MapPost("/auth/login", async (LoginRequest request, HttpContext context, HomeWatchTokenStore store) =>
        {
            await Task.Delay(Random.Shared.Next(150, 350));
            if (!store.IsConfigured)
                return Results.Json(new { error = "Remote access token has not been configured on the HomeWatch computer." }, statusCode: 503);

            if (string.IsNullOrWhiteSpace(request.Token) || !store.Validate(request.Token))
                return Results.Json(new { error = "Invalid access token." }, statusCode: 401);

            var claims = new[]
            {
                new Claim(ClaimTypes.Name, "HomeWatch administrator"),
                new Claim(ClaimTypes.Role, "Administrator")
            };
            var identity = new ClaimsIdentity(claims, Scheme);
            var properties = new AuthenticationProperties
            {
                IsPersistent = request.RememberDevice,
                AllowRefresh = true
            };
            if (request.RememberDevice)
                properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30);

            await context.SignInAsync(Scheme, new ClaimsPrincipal(identity), properties);
            return Results.Ok(new { authenticated = true, rememberedForDays = request.RememberDevice ? 30 : 0 });
        });

        app.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(Scheme);
            return Results.Ok(new { authenticated = false });
        });

        return app;
    }

    public sealed record LoginRequest(string? Token, bool RememberDevice);
}

public sealed class HomeWatchTokenStore
{
    private readonly byte[]? expectedHash;
    private readonly ILogger<HomeWatchTokenStore> logger;

    public HomeWatchTokenStore(IHostEnvironment environment, ILogger<HomeWatchTokenStore> logger)
    {
        this.logger = logger;
        var path = TokenPath(environment.ContentRootPath);
        try
        {
            if (!File.Exists(path)) return;
            var protectedBytes = File.ReadAllBytes(path);
            var tokenBytes = ProtectedData.Unprotect(protectedBytes, Encoding.UTF8.GetBytes("HomeWatch.RemoteAccess.v1"), DataProtectionScope.LocalMachine);
            expectedHash = SHA256.HashData(tokenBytes);
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read the HomeWatch remote-access token.");
        }
    }

    public bool IsConfigured => expectedHash is { Length: > 0 };

    public bool Validate(string token)
    {
        if (expectedHash is null) return false;
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        try { return CryptographicOperations.FixedTimeEquals(supplied, expectedHash); }
        finally { CryptographicOperations.ZeroMemory(supplied); }
    }

    public static string TokenPath(string contentRoot) => Path.Combine(contentRoot, "homewatch-access-token.dat");
}
