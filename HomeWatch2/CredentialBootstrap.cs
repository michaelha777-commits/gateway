using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class CredentialBootstrap
{
    private const string LegacyFileName = ".homewatch-credentials.json";
    private const string MachineFileName = ".homewatch-credentials.machine.json";

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            var appDirectory = AppContext.BaseDirectory;
            var workingDirectory = Directory.GetCurrentDirectory();
            var credentialDirectory = FindCredentialDirectory(appDirectory, workingDirectory);
            var machinePath = Path.Combine(credentialDirectory, MachineFileName);
            var legacyPath = Path.Combine(credentialDirectory, LegacyFileName);

            HomeWatchCredentials? credentials = null;

            if (File.Exists(machinePath))
            {
                credentials = ReadMachineCredentials(machinePath);
            }
            else if (File.Exists(legacyPath))
            {
                credentials = ReadLegacyCredentials(legacyPath);
                WriteMachineCredentials(machinePath, credentials);
                Console.WriteLine($"HomeWatch migrated credentials to machine-scoped storage: {machinePath}");
            }

            if (credentials is not null)
            {
                SetIfMissing("AdGuard__BaseUrl", string.IsNullOrWhiteSpace(credentials.AdGuardBaseUrl) ? "http://127.0.0.1" : credentials.AdGuardBaseUrl);
                SetIfMissing("AdGuard__Username", credentials.Username);
                SetIfMissing("AdGuard__Password", credentials.Password);
                SetIfMissing("Ntfy__BaseUrl", string.IsNullOrWhiteSpace(credentials.NtfyBaseUrl) ? "https://ntfy.sh" : credentials.NtfyBaseUrl);
                SetIfMissing("Ntfy__Topic", credentials.NtfyTopic);
            }

            var nmapPath = FindNmapExecutable();
            if (!string.IsNullOrWhiteSpace(nmapPath))
                SetIfMissing("HomeWatch__NmapPath", nmapPath);
        }
        catch (Exception ex)
        {
            Environment.SetEnvironmentVariable("HomeWatch__CredentialError", ex.Message);
            Console.Error.WriteLine($"HomeWatch credential bootstrap failed: {ex.Message}");
        }
    }

    private static string FindCredentialDirectory(params string[] candidates)
    {
        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(candidate, MachineFileName)) || File.Exists(Path.Combine(candidate, LegacyFileName)))
                return candidate;
        }

        return candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? Directory.GetCurrentDirectory();
    }

    private static HomeWatchCredentials ReadMachineCredentials(string path)
    {
        var stored = JsonSerializer.Deserialize<MachineCredentialFile>(File.ReadAllText(path), JsonOptions())
            ?? throw new InvalidOperationException("The machine credential file is empty or invalid.");

        if (string.IsNullOrWhiteSpace(stored.ProtectedPassword))
            throw new InvalidOperationException("The machine credential file does not contain a protected password.");

        var encrypted = Convert.FromBase64String(stored.ProtectedPassword);
        var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);

        return new HomeWatchCredentials(
            stored.Username ?? string.Empty,
            Encoding.UTF8.GetString(plaintext),
            stored.NtfyTopic ?? string.Empty,
            stored.AdGuardBaseUrl ?? "http://127.0.0.1",
            stored.NtfyBaseUrl ?? "https://ntfy.sh");
    }

    private static HomeWatchCredentials ReadLegacyCredentials(string path)
    {
        var stored = JsonSerializer.Deserialize<LegacyCredentialFile>(File.ReadAllText(path), JsonOptions())
            ?? throw new InvalidOperationException("The legacy credential file is empty or invalid.");

        if (string.IsNullOrWhiteSpace(stored.Password))
            throw new InvalidOperationException("The legacy credential file does not contain a protected password.");

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromHexString(stored.Password.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The legacy AdGuard password is not in the expected Windows DPAPI format.");
        }

        byte[] plaintext;
        try
        {
            plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("The legacy credentials belong to another Windows account. Run HomeWatch once as the Windows user who originally saved them so they can be migrated for the service.");
        }

        return new HomeWatchCredentials(
            stored.Username ?? string.Empty,
            Encoding.Unicode.GetString(plaintext).TrimEnd('\0'),
            stored.NtfyTopic ?? string.Empty,
            "http://127.0.0.1",
            "https://ntfy.sh");
    }

    private static void WriteMachineCredentials(string path, HomeWatchCredentials credentials)
    {
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(credentials.Password), null, DataProtectionScope.LocalMachine);
        var stored = new MachineCredentialFile
        {
            Version = 1,
            Username = credentials.Username,
            ProtectedPassword = Convert.ToBase64String(encrypted),
            NtfyTopic = credentials.NtfyTopic,
            AdGuardBaseUrl = credentials.AdGuardBaseUrl,
            NtfyBaseUrl = credentials.NtfyBaseUrl
        };

        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, true);
    }

    private static void SetIfMissing(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) && value is not null)
            Environment.SetEnvironmentVariable(name, value);
    }

    private static string? FindNmapExecutable()
    {
        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var candidates = pathDirectories.Select(directory => Path.Combine(directory, "nmap.exe")).Concat(new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nmap", "nmap.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Nmap", "nmap.exe"),
            @"C:\Program Files\Nmap\nmap.exe",
            @"C:\Program Files (x86)\Nmap\nmap.exe"
        });

        return candidates.FirstOrDefault(File.Exists);
    }

    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };

    private sealed record HomeWatchCredentials(string Username, string Password, string NtfyTopic, string AdGuardBaseUrl, string NtfyBaseUrl);

    private sealed class LegacyCredentialFile
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? NtfyTopic { get; set; }
    }

    private sealed class MachineCredentialFile
    {
        public int Version { get; set; }
        public string? Username { get; set; }
        public string? ProtectedPassword { get; set; }
        public string? NtfyTopic { get; set; }
        public string? AdGuardBaseUrl { get; set; }
        public string? NtfyBaseUrl { get; set; }
    }
}
