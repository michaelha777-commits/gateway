using System.Diagnostics;
using System.Globalization;
using HomeWatch3.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeWatch3.Monitoring;

public sealed class TlsInspectionOptions
{
    public const string SectionName = "TlsInspection";
    public bool EnableOfflineDecryption { get; set; } = true;
    public string TSharkPath { get; set; } = "tshark";
    public int MaximumCaptureMegabytes { get; set; } = 256;
    public int MaximumKeyLogMegabytes { get; set; } = 8;
    public int AnalysisTimeoutSeconds { get; set; } = 90;
}

public static class TlsInspectionModule
{
    public static IServiceCollection AddTlsInspectionModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TlsInspectionOptions>(configuration.GetSection(TlsInspectionOptions.SectionName));
        services.AddSingleton<TlsEvidenceAnalyzer>();
        return services;
    }

    public static void MapTlsInspectionModule(this WebApplication app)
    {
        app.MapGet("/api/tls-inspection/capabilities", (TlsEvidenceAnalyzer analyzer, NtopngFlowMonitor ntopng) =>
            Results.Ok(analyzer.GetCapabilities(ntopng.GetStatus())));

        app.MapGet("/api/tls-inspection/flows", async (
            int? minutes,
            long? deviceId,
            string? search,
            int? limit,
            HomeWatchDb db,
            IgnoredDeviceStore ignoredDevices,
            CancellationToken cancellationToken) =>
        {
            var selectedMinutes = Math.Clamp(minutes ?? 60, 1, 43_200);
            var selectedLimit = Math.Clamp(limit ?? 500, 1, 2_000);
            var since = DateTime.UtcNow.AddMinutes(-selectedMinutes);
            var ignoredIds = ignoredDevices.GetIds().ToHashSet();
            var query = db.TrafficEvents.AsNoTracking()
                .Where(x => x.Encrypted
                    && (x.TimestampUtc >= since || x.LastSeenUtc >= since)
                    && (!x.DeviceId.HasValue || !ignoredIds.Contains(x.DeviceId.Value)));
            if (deviceId.HasValue) query = query.Where(x => x.DeviceId == deviceId.Value);
            var events = await query.OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Id)
                .Take(5_000).ToListAsync(cancellationToken);
            var ids = events.Where(x => x.DeviceId.HasValue).Select(x => x.DeviceId!.Value).Distinct().ToArray();
            var devices = await db.Devices.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
            var needle = search?.Trim();
            var rows = events
                .Where(x => !x.DeviceId.HasValue || !devices.TryGetValue(x.DeviceId.Value, out var device) || !InfrastructureDeviceClassifier.IsInfrastructure(device))
                .Where(x => string.IsNullOrWhiteSpace(needle) || new[]
                {
                    x.Domain, x.TlsServerName, x.Application, x.Protocol, x.DestinationIp,
                    x.TlsVersion, x.TlsAlpn, x.TlsClientFingerprint, x.TlsServerFingerprint,
                    x.CertificateSubject, x.CertificateIssuer
                }.Any(value => value?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true))
                .Take(selectedLimit)
                .Select(x => new
                {
                    x.Id,
                    x.TimestampUtc,
                    startedUtc = x.StartedUtc ?? x.TimestampUtc,
                    lastSeenUtc = x.LastSeenUtc ?? x.TimestampUtc,
                    x.DeviceId,
                    device = x.DeviceId.HasValue && devices.TryGetValue(x.DeviceId.Value, out var device) ? device.Name ?? device.LastIpAddress : x.SourceIp,
                    ip = x.SourceIp,
                    hostname = x.TlsServerName ?? x.Domain,
                    x.Application,
                    x.Protocol,
                    remoteIp = x.DestinationIp,
                    remotePort = x.DestinationPort,
                    bytesDown = Math.Max(0, x.BytesDown ?? 0),
                    bytesUp = Math.Max(0, x.BytesUp ?? 0),
                    x.TlsVersion,
                    x.TlsCipher,
                    x.TlsAlpn,
                    x.TlsClientFingerprint,
                    x.TlsServerFingerprint,
                    x.CertificateSubject,
                    x.CertificateIssuer,
                    x.Visibility,
                    x.Confidence,
                    x.Source
                }).ToArray();

            return Results.Ok(new
            {
                generatedUtc = DateTime.UtcNow,
                filters = new { minutes = selectedMinutes, deviceId, search, limit = selectedLimit },
                summary = new
                {
                    encryptedFlows = rows.Length,
                    hostnamesVisible = rows.Count(x => !string.IsNullOrWhiteSpace(x.hostname)),
                    tlsVersionsVisible = rows.Count(x => !string.IsNullOrWhiteSpace(x.TlsVersion)),
                    fingerprintsVisible = rows.Count(x => !string.IsNullOrWhiteSpace(x.TlsClientFingerprint) || !string.IsNullOrWhiteSpace(x.TlsServerFingerprint)),
                    bytesDown = rows.Sum(x => x.bytesDown),
                    bytesUp = rows.Sum(x => x.bytesUp)
                },
                devices = ids.Where(devices.ContainsKey).Select(id => new { devices[id].Id, devices[id].Name, devices[id].LastIpAddress }).OrderBy(x => x.Name).ToArray(),
                flows = rows
            });
        });

        app.MapPost("/api/tls-inspection/analyze", async (
            HttpRequest request,
            TlsEvidenceAnalyzer analyzer,
            CancellationToken cancellationToken) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { error = "Upload a PCAP/PCAPNG capture and TLS key-log file as multipart form data." });
            var form = await request.ReadFormAsync(cancellationToken);
            var capture = form.Files.GetFile("capture");
            var keyLog = form.Files.GetFile("keyLog");
            if (capture is null || keyLog is null) return Results.BadRequest(new { error = "Both capture and keyLog files are required." });
            try { return Results.Ok(await analyzer.AnalyzeAsync(capture, keyLog, cancellationToken)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 503); }
        }).DisableAntiforgery();
    }
}

public sealed class TlsEvidenceAnalyzer(IOptions<TlsInspectionOptions> options, IConfiguration configuration)
{
    private static readonly string[] KeyLogPrefixes =
    [
        "CLIENT_RANDOM ",
        "CLIENT_EARLY_TRAFFIC_SECRET ",
        "CLIENT_HANDSHAKE_TRAFFIC_SECRET ",
        "SERVER_HANDSHAKE_TRAFFIC_SECRET ",
        "CLIENT_TRAFFIC_SECRET_0 ",
        "SERVER_TRAFFIC_SECRET_0 ",
        "EXPORTER_SECRET "
    ];

    private readonly TlsInspectionOptions _options = options.Value;
    private readonly string _analysisRoot = ResolveAnalysisRoot(configuration);

    public object GetCapabilities(NtopngFlowMonitorStatus ntopng)
    {
        var executable = ResolveTSharkPath();
        return new
        {
            passiveTlsIntelligence = new
            {
                available = true,
                ntopngAvailable = ntopng.Available,
                fields = new[] { "SNI/hostname", "TLS version", "ALPN", "JA3/JA3S when supplied by ntopng", "certificate metadata when supplied", "remote IP/port", "timing", "bytes" }
            },
            offlineAuthorizedDecryption = new
            {
                enabled = _options.EnableOfflineDecryption,
                available = _options.EnableOfflineDecryption && executable is not null,
                engine = executable is null ? null : "TShark / Wireshark",
                tsharkPath = executable,
                requires = new[] { "PCAP or PCAPNG captured from traffic you administer", "matching TLS session-key log" },
                output = new[] { "HTTP host", "redacted HTTPS path", "method", "SNI", "source/destination", "timestamp" },
                excluded = new[] { "Authorization headers", "cookies", "request/response bodies", "query strings", "credentials" }
            },
            captureSources = new[] { "OPNsense Diagnostics packet capture", "Wireshark/dumpcap capture", "pcapng with an embedded Decryption Secrets Block" }
        };
    }

    public async Task<object> AnalyzeAsync(IFormFile capture, IFormFile keyLog, CancellationToken cancellationToken)
    {
        if (!_options.EnableOfflineDecryption) throw new InvalidOperationException("Offline TLS analysis is disabled in HomeWatch configuration.");
        var executable = ResolveTSharkPath();
        if (executable is null) throw new InvalidOperationException("TShark is not installed or TlsInspection:TSharkPath is not configured.");

        var maximumCaptureBytes = Math.Clamp(_options.MaximumCaptureMegabytes, 1, 2_048) * 1024L * 1024L;
        var maximumKeyLogBytes = Math.Clamp(_options.MaximumKeyLogMegabytes, 1, 64) * 1024L * 1024L;
        if (capture.Length <= 0 || capture.Length > maximumCaptureBytes) throw new ArgumentException($"Capture must be between 1 byte and {_options.MaximumCaptureMegabytes} MB.");
        if (keyLog.Length <= 0 || keyLog.Length > maximumKeyLogBytes) throw new ArgumentException($"TLS key log must be between 1 byte and {_options.MaximumKeyLogMegabytes} MB.");
        if (!new[] { ".pcap", ".pcapng", ".cap" }.Contains(Path.GetExtension(capture.FileName), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Capture must use the .pcap, .pcapng, or .cap extension.");

        Directory.CreateDirectory(_analysisRoot);
        var jobId = Guid.NewGuid().ToString("N");
        var jobDirectory = Path.Combine(_analysisRoot, jobId);
        Directory.CreateDirectory(jobDirectory);
        var capturePath = Path.Combine(jobDirectory, "capture" + Path.GetExtension(capture.FileName).ToLowerInvariant());
        var keyLogPath = Path.Combine(jobDirectory, "tls.keys");

        try
        {
            await SaveAsync(capture, capturePath, cancellationToken);
            await SaveAsync(keyLog, keyLogPath, cancellationToken);
            if (!File.ReadLines(keyLogPath).Take(200).Any(line => KeyLogPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal))))
                throw new ArgumentException("The key-log file does not contain recognized TLS session-key entries.");

            var result = await RunTSharkAsync(executable, capturePath, keyLogPath, cancellationToken);
            return new
            {
                analyzedUtc = DateTime.UtcNow,
                capture = new { name = Path.GetFileName(capture.FileName), bytes = capture.Length },
                engine = result.Engine,
                decryptedHttpRequests = result.Entries.Count(x => !string.IsNullOrWhiteSpace(x.Method)),
                tlsHandshakes = result.Entries.Count(x => string.IsNullOrWhiteSpace(x.Method) && !string.IsNullOrWhiteSpace(x.Hostname)),
                entries = result.Entries,
                privacy = "Input files were deleted after analysis. Query strings, cookies, authorization headers, bodies, and credentials were not extracted."
            };
        }
        finally
        {
            if (Directory.Exists(jobDirectory)
                && Path.GetDirectoryName(jobDirectory)?.Equals(_analysisRoot, StringComparison.Ordinal) == true)
            {
                try { Directory.Delete(jobDirectory, recursive: true); }
                catch { }
            }
        }
    }

    private async Task<TlsRunResult> RunTSharkAsync(string executable, string capturePath, string keyLogPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-r", capturePath,
            "-o", $"tls.keylog_file:{keyLogPath}",
            "-Y", "http.request || http2 || tls.handshake.extensions_server_name",
            "-T", "fields",
            "-E", "separator=/t",
            "-E", "quote=n",
            "-E", "occurrence=f",
            "-e", "frame.time_epoch",
            "-e", "ip.src",
            "-e", "ipv6.src",
            "-e", "ip.dst",
            "-e", "ipv6.dst",
            "-e", "tcp.dstport",
            "-e", "udp.dstport",
            "-e", "tls.handshake.extensions_server_name",
            "-e", "http.host",
            "-e", "http.request.method",
            "-e", "http.request.uri",
            "-e", "http2.headers.authority",
            "-e", "http2.headers.method",
            "-e", "http2.headers.path"
        }) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("TShark could not be started.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.AnalysisTimeoutSeconds, 10, 600)));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("TLS analysis exceeded the configured timeout.");
        }
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("TShark could not analyze the supplied files: " + Truncate(error, 1_000));

        var entries = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseLine)
            .Where(x => x is not null)
            .Select(x => x!)
            .GroupBy(x => $"{x.TimestampUtc:O}|{x.SourceIp}|{x.DestinationIp}|{x.Method}|{x.Hostname}|{x.Path}", StringComparer.Ordinal)
            .Select(x => x.First())
            .OrderBy(x => x.TimestampUtc)
            .Take(2_000)
            .ToArray();
        var version = await ReadVersionAsync(executable, cancellationToken);
        return new(version, entries);
    }

    private static TlsDecryptedEntry? ParseLine(string line)
    {
        var fields = line.TrimEnd('\r').Split('\t');
        if (fields.Length < 14) Array.Resize(ref fields, 14);
        if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch)) return null;
        DateTime timestamp;
        try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(epoch * 1_000)).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
        var source = First(fields[1], fields[2]);
        var destination = First(fields[3], fields[4]);
        var port = First(fields[5], fields[6]);
        var sni = NormalizeHost(fields[7]);
        var httpHost = NormalizeHost(fields[8]);
        var httpMethod = Clean(fields[9]);
        var httpPath = RedactPath(fields[10]);
        var h2Host = NormalizeHost(fields[11]);
        var h2Method = Clean(fields[12]);
        var h2Path = RedactPath(fields[13]);
        var hostname = First(httpHost, h2Host, sni);
        var method = First(httpMethod, h2Method);
        var path = First(httpPath, h2Path);
        var url = !string.IsNullOrWhiteSpace(hostname) && !string.IsNullOrWhiteSpace(path)
            ? "https://" + hostname + (path.StartsWith('/') ? path : "/" + path)
            : null;
        return new(timestamp, source, destination, int.TryParse(port, out var parsedPort) ? parsedPort : null,
            hostname, sni, method, path, url, string.IsNullOrWhiteSpace(method) ? "TLS handshake" : "Decrypted HTTP request metadata");
    }

    private static async Task SaveAsync(IFormFile file, string path, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous);
        await file.CopyToAsync(output, cancellationToken);
    }

    private async Task<string> ReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var info = new ProcessStartInfo { FileName = executable, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            info.ArgumentList.Add("--version");
            using var process = Process.Start(info);
            if (process is null) return "TShark";
            var output = await process.StandardOutput.ReadLineAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(output) ? "TShark" : output.Trim();
        }
        catch { return "TShark"; }
    }

    private string? ResolveTSharkPath()
    {
        if (string.IsNullOrWhiteSpace(_options.TSharkPath)) return null;
        if (Path.IsPathRooted(_options.TSharkPath)) return File.Exists(_options.TSharkPath) ? _options.TSharkPath : null;
        var names = OperatingSystem.IsWindows() ? new[] { _options.TSharkPath, _options.TSharkPath + ".exe" } : new[] { _options.TSharkPath };
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    private static string ResolveAnalysisRoot(IConfiguration configuration)
    {
        var dataPath = configuration["HomeWatch:DataPath"];
        if (string.IsNullOrWhiteSpace(dataPath)) dataPath = Path.Combine(AppContext.BaseDirectory, "data");
        return Path.GetFullPath(Path.Combine(dataPath, "tls-analysis"));
    }

    private static string? NormalizeHost(string? value)
    {
        var text = Clean(value);
        if (text is null) return null;
        if (Uri.TryCreate("https://" + text, UriKind.Absolute, out var uri)) return uri.Host.ToLowerInvariant();
        return text.Trim().Trim('.').ToLowerInvariant();
    }

    private static string? RedactPath(string? value)
    {
        var text = Clean(value);
        if (text is null) return null;
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute)) text = absolute.AbsolutePath;
        var end = text.IndexOfAny(['?', '#']);
        if (end >= 0) text = text[..end];
        var segments = text.Split('/', StringSplitOptions.None);
        var redactNext = false;
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (redactNext || LooksLikeSecret(segment)) segments[index] = "[redacted]";
            redactNext = new[] { "token", "access_token", "auth", "authorization", "password", "passwd", "session", "secret", "signature", "sig", "key" }
                .Contains(segment, StringComparer.OrdinalIgnoreCase);
        }
        var redacted = string.Join('/', segments);
        return redacted.Length <= 512 ? redacted : redacted[..512];
    }

    private static bool LooksLikeSecret(string value)
    {
        if (value.Length < 40) return false;
        if (value.Count(ch => ch == '.') == 2 && value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')) return true;
        return value.Length >= 48 && value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '=' or '%');
    }

    private static string? First(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

    private sealed record TlsRunResult(string Engine, TlsDecryptedEntry[] Entries);
    private sealed record TlsDecryptedEntry(
        DateTime TimestampUtc,
        string? SourceIp,
        string? DestinationIp,
        int? DestinationPort,
        string? Hostname,
        string? Sni,
        string? Method,
        string? Path,
        string? RedactedUrl,
        string Evidence);
}
