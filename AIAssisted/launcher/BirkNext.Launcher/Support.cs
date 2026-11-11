using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.BuildInfo;

namespace BirkNext.Launcher;

/// <summary>
/// The launcher log: one file per day in the data folder's logs, timestamps in UTC with a per-launch correlation id, credentials redacted,
/// and only the newest <see cref="RetainedFiles"/> launcher logs kept (older launcher logs only; nothing else is ever deleted).
/// </summary>
public sealed partial class LauncherLog
{
    public const int RetainedFiles = 14;
    private readonly string _path;
    private readonly object _gate = new();
    public string CorrelationId { get; } = Guid.NewGuid().ToString("N")[..8];
    public string FilePath => _path;

    public LauncherLog(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        _path = Path.Combine(logsDirectory, $"launcher-{DateTime.UtcNow:yyyyMMdd}.log");
        foreach (var old in Directory.GetFiles(logsDirectory, "launcher-*.log").OrderByDescending(f => f, StringComparer.Ordinal).Skip(RetainedFiles))
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{level}] [{CorrelationId}] {Redact(message)}{Environment.NewLine}";
        lock (_gate) { try { File.AppendAllText(_path, line); } catch (IOException) { } }
    }

    public void Info(string message) => Write("INF", message);
    public void Warn(string message) => Write("WRN", message);
    public void Error(string message) => Write("ERR", message);

    /// <summary>Removes password values (connection strings, PGPASSWORD, key=value pairs) from anything logged.</summary>
    public static string Redact(string text) => PasswordPattern().Replace(text, m => m.Groups[1].Value + "[redacted]");

    [GeneratedRegex(@"(?i)((?:password|pwd|pgpassword)\s*[=:]\s*)[^;\s""']+")] private static partial Regex PasswordPattern();
}

/// <summary>Database credentials generated on first run, stored protected for this Windows user (DPAPI, CurrentUser scope).</summary>
public sealed record DatabaseCredentials(string AdminUser, string AdminPassword, string AppUser, string AppPassword, string Database)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BirkNext.TesterNative.DatabaseCredentials.v1");

    /// <summary>A password from an alphabet that never needs quoting in SQL or connection strings (letters and digits, 32 characters ≈ 190 bits).</summary>
    public static string NewPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Create(32, 0, (span, _) => { for (var i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]; });
    }

    public static DatabaseCredentials New() => new("birknext_admin", NewPassword(), "birknext_app", NewPassword(), "birknext");

    public void Save(string path)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this);
        var protectedBytes = OperatingSystem.IsWindows() ? ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException("Credential protection needs Windows DPAPI.");
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, path, overwrite: true);
    }

    public static DatabaseCredentials? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser) : null;
            return bytes is null ? null : JsonSerializer.Deserialize<DatabaseCredentials>(bytes);
        }
        catch (CryptographicException e)
        {
            throw new LauncherException("The local database credentials cannot be read by this Windows user.",
                "Start BirkNext as the Windows user who first started it. The database is not changed.", e.Message);
        }
    }

    public string AppConnectionString(int port) => $"Host=127.0.0.1;Port={port};Database={Database};Username={AppUser};Password={AppPassword};Pooling=true";
    public override string ToString() => $"{AppUser}@{Database} (credentials redacted)";
}

/// <summary>Owner-only folders: the current user and SYSTEM, inheritance from the parent removed.</summary>
public static class OwnerOnly
{
    public static void Apply(string directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        var info = new DirectoryInfo(directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}

/// <summary>The runtime profile this launcher runs: explicit command line, else the package's default, else a clear failure.</summary>
public static class ProfileSelection
{
    public static (RuntimeProfile Profile, string Source) Resolve(string? commandLine, string? packageDefault)
    {
        var (fromCli, cliError) = RuntimeProfiles.Parse(commandLine);
        if (cliError is not null) throw new LauncherException(cliError, "Start BirkNext without --runtime-profile, or use --runtime-profile TesterNative.", exitCode: 64);
        var (fromPackage, packageError) = RuntimeProfiles.Parse(packageDefault);
        var profile = fromCli ?? fromPackage
            ?? throw new LauncherException("The runtime profile cannot be determined: the package names none.", "Download the BirkNext Tester Package again.", packageError);
        if (profile != RuntimeProfile.TesterNative)
            throw new LauncherException($"Start-BirkNext.exe runs the TesterNative profile only; {profile} is started with scripts/start-local.ps1 from a source checkout.",
                "Start BirkNext without --runtime-profile.", exitCode: 64);
        return (profile, fromCli is not null ? "command line" : "package default");
    }
}

/// <summary>Loopback ports for the database, backend and frontend. A port is "free" only when this process can bind it on 127.0.0.1 and ::1.</summary>
public static class Ports
{
    public sealed record Assignment(int Database, int Backend, int Frontend);

    public static readonly Assignment Preferred = new(55432, 5480, 5481);

    public static bool IsFree(int port)
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            try { var l = new TcpListener(address, port); l.Start(); l.Stop(); }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) { return false; }
            catch (SocketException) { if (address.Equals(IPAddress.Loopback)) return false; } // IPv6 disabled: IPv4 decides.
        }
        return true;
    }

    /// <summary>The preferred port when free, else the next free one in a small window; never a port in <paramref name="taken"/>.</summary>
    public static int Choose(int preferred, ISet<int> taken, Func<int, bool>? isFree = null)
    {
        isFree ??= IsFree;
        for (var p = preferred; p < preferred + 100; p++)
            if (!taken.Contains(p) && isFree(p)) return p;
        throw new LauncherException("No free local port is available for BirkNext.", "Close other local server applications and start BirkNext again.", $"Ports {preferred}-{preferred + 99} are in use.");
    }
}

/// <summary>Package integrity: every file listed in package-manifest.json (format 2) has its recorded SHA-256 and size.</summary>
public static class PackageIntegrity
{
    public sealed record Result(int Checked, IReadOnlyList<string> Missing, IReadOnlyList<string> Changed)
    {
        public bool Ok => Missing.Count == 0 && Changed.Count == 0;
    }

    /// <summary>Verifies the listed files; <paramref name="only"/> limits the check to those package-relative paths (the quick per-start check).</summary>
    public static Result Verify(string root, IReadOnlyCollection<string>? only = null)
    {
        var manifestPath = Path.Combine(root, "package-manifest.json");
        if (!File.Exists(manifestPath))
            throw new LauncherException("This BirkNext package is incomplete: its file list is missing.", "Download the BirkNext Tester Package again and extract the whole archive.");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!doc.RootElement.TryGetProperty("format", out var format) || format.GetInt32() != 2 || !doc.RootElement.TryGetProperty("fileHashes", out var hashes))
            throw new LauncherException("This BirkNext package is damaged: its file list is not valid.", "Download the BirkNext Tester Package again.");
        var filter = only?.Select(p => p.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>(); var changed = new List<string>(); var count = 0;
        foreach (var entry in hashes.EnumerateArray())
        {
            var rel = entry.GetProperty("path").GetString() ?? "";
            if (filter is not null && !filter.Contains(rel)) continue;
            // Never follow a manifest entry out of the package.
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { changed.Add(rel); continue; }
            count++;
            if (!File.Exists(full)) { missing.Add(rel); continue; }
            using var stream = File.OpenRead(full);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) changed.Add(rel);
        }
        if (filter is not null)
            foreach (var wanted in filter.Where(w => !hashes.EnumerateArray().Any(e => string.Equals(e.GetProperty("path").GetString(), w, StringComparison.OrdinalIgnoreCase))))
                missing.Add(wanted);
        return new Result(count, missing, changed);
    }

    /// <summary>SHA-256 of the manifest itself: identifies the package build whose full verification is remembered.</summary>
    public static string ManifestHash(string root)
    {
        using var stream = File.OpenRead(Path.Combine(root, "package-manifest.json"));
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>Windows prerequisites that cannot be bundled: 64-bit Windows 10/11 and the Visual C++ runtime the PostgreSQL binaries import.</summary>
public static class Prerequisites
{
    public static IReadOnlyList<string> Problems(Func<string, bool>? fileExists = null, long? freeBytes = null)
    {
        fileExists ??= File.Exists;
        var problems = new List<string>();
        if (!OperatingSystem.IsWindows() || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            problems.Add("BirkNext Tester needs Windows 10 (1809 or later) or Windows 11.");
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            problems.Add($"BirkNext Tester needs a 64-bit (x64) Windows PC; this PC is {RuntimeInformation.OSArchitecture}.");
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!fileExists(Path.Combine(system, "vcruntime140.dll")) || !fileExists(Path.Combine(system, "msvcp140.dll")))
            problems.Add("The Microsoft Visual C++ 2015–2022 Redistributable (x64) is not installed; the local database needs it. Ask IT to install it.");
        if (freeBytes is < 1L << 30)
            problems.Add("Less than 1 GB of free disk space is available for BirkNext's data. Free some space and start again.");
        return problems;
    }
}
