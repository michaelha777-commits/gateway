$ErrorActionPreference = 'Stop'

function Replace-Once([string]$Text, [string]$Old, [string]$New, [string]$Label) {
    $index = $Text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($index -lt 0) { throw "Expected text not found: $Label" }
    return $Text.Substring(0, $index) + $New + $Text.Substring($index + $Old.Length)
}

$programPath = 'HomeWatch2/Program.cs'
$program = [IO.File]::ReadAllText($programPath)
$program = $program.Replace("builder.Services.AddHostedService<ApplePrivateRelayBlocker>();`r`nbuilder.Services.AddHostedService<ApplePrivateRelayBlocker>();", "builder.Services.AddHostedService<ApplePrivateRelayBlocker>();")
$program = $program.Replace("builder.Services.AddHostedService<ApplePrivateRelayBlocker>();`nbuilder.Services.AddHostedService<ApplePrivateRelayBlocker>();", "builder.Services.AddHostedService<ApplePrivateRelayBlocker>();")
$program = Replace-Once $program 'version = "2.0.0-alpha.15",' 'version = "2.0.0-alpha.16",`r`n    commit = BuildCommit(),' 'status version'
$program = Replace-Once $program 'RootDomain(item.Domain)' 'RootDomainForWorker(item.Domain)' 'worker root-domain call'
$program = Replace-Once $program "return parts.Length >= 2 ? string.Join('.', parts[^2], parts[^1]) : domain;" "return parts.Length >= 2 ? string.Join('.', parts[^2], parts[^1]) : domain ?? \"\";" 'RootDomain nullability'
$workerMarker = '    private static string ReadString(JsonElement element, params string[] path)'
$workerHelper = @'
    private static string RootDomainForWorker(string domain)
    {
        var parts = (domain ?? "").Trim('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? string.Join('.', parts[^2], parts[^1]) : domain ?? "";
    }

    private static string ReadString(JsonElement element, params string[] path)
'@
$program = Replace-Once $program $workerMarker $workerHelper 'worker helper'
$commitMarker = 'static async Task RepairDuplicateDevicesAsync(HomeWatchDb db)'
$commitHelper = @'
static string BuildCommit()
{
    var configured = Environment.GetEnvironmentVariable("HOMEWATCH_COMMIT");
    if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();
    try
    {
        var start = new System.Diagnostics.ProcessStartInfo("git", "rev-parse --short HEAD")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null) return "unknown";
        var value = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(2000);
        return process.ExitCode == 0 && value.Length > 0 ? value : "unknown";
    }
    catch { return "unknown"; }
}

static async Task RepairDuplicateDevicesAsync(HomeWatchDb db)
'@
$program = Replace-Once $program $commitMarker $commitHelper 'commit helper'
[IO.File]::WriteAllText($programPath, $program, [Text.UTF8Encoding]::new($false))

$networkPath = 'HomeWatch2/NetworkDiscovery.cs'
$network = [IO.File]::ReadAllText($networkPath)
$network = $network.Replace("    IHttpClientFactory httpClientFactory,`r`n    IConfiguration configuration,", "    IConfiguration configuration,")
$network = $network.Replace("    IHttpClientFactory httpClientFactory,`n    IConfiguration configuration,", "    IConfiguration configuration,")
[IO.File]::WriteAllText($networkPath, $network, [Text.UTF8Encoding]::new($false))

$indexPath = 'HomeWatch2/wwwroot/index.html'
$index = [IO.File]::ReadAllText($indexPath).Replace('2.0.0-alpha.15','2.0.0-alpha.16')
$index = $index.Replace('LIVE ADULT SESSIONS','NETWORK SESSIONS').Replace('Grouped session timeline','Grouped activity timeline').Replace('Adult-related activity is grouped by device and updated live as additional domains are detected.','All network activity is grouped by device and service. Adult activity remains clearly highlighted.').Replace('Loading live sessions…','Loading sessions…')
if ($index -notmatch 'id="sessionCategory"') {
    $index = $index.Replace('<select id="sessionHours">','<select id="sessionCategory"><option value="all">All session types</option><option value="adult">Adult</option><option value="social">Social media</option><option value="streaming">Streaming</option><option value="messaging">Messaging</option><option value="shopping">Shopping</option><option value="gaming">Gaming</option><option value="productivity">Productivity</option><option value="development">Development</option><option value="advertising">Advertising/tracking</option><option value="privacy-proxy">VPN/proxy/encrypted DNS</option><option value="system">Network/system</option><option value="other">Other</option></select><select id="sessionHours">')
}
$index = [regex]::Replace($index, '\s*<script src="/live-session-timeline\.js\?v=[^"]+"></script>', '', 1)
[IO.File]::WriteAllText($indexPath, $index, [Text.UTF8Encoding]::new($false))

$appPath = 'HomeWatch2/wwwroot/app.js'
$app = [IO.File]::ReadAllText($appPath).Replace("const HOMEWATCH_VERSION = '2.0.0-alpha.10';", "const HOMEWATCH_VERSION = '2.0.0-alpha.16';")
$app = $app.Replace("byId('version').textContent = `v`${status.version || HOMEWATCH_VERSION}`;", "byId('version').textContent = `v`${status.version || HOMEWATCH_VERSION}`${status.commit ? ` · `${String(status.commit).slice(0,7)}` : ''}`;")
$app = $app.Replace("byId('sessionHours').addEventListener('change', loadSessions);", "byId('sessionHours').addEventListener('change', loadSessions);`r`nbyId('sessionCategory')?.addEventListener('change', loadSessions);")
[IO.File]::WriteAllText($appPath, $app, [Text.UTF8Encoding]::new($false))
