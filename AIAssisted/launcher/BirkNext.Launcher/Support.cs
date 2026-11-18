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
/// <param name="ProvisionerPassword">Password of <see cref="ProvisionerUser"/>, the restore-provisioning role (null until the launcher has created it;
/// older credential files have none).</param>
public sealed record DatabaseCredentials(string AdminUser, string AdminPassword, string AppUser, string AppPassword, string Database, string? ProvisionerPassword = null)
{
    /// <summary>The restore-provisioning role: CREATEDB, NOSUPERUSER, may SET ROLE to the application role, logs in only to <c>postgres</c> from 127.0.0.1.
    /// The backend uses it for one thing — creating a new isolated restore database owned by the application role — so the application role keeps NOCREATEDB.</summary>
    public const string ProvisionerUser = "birknext_restore_provisioner";

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

    /// <summary>The restore-provisioning login (maintenance database only). Empty when the role has not been provisioned: restore then reports BLOCKED.</summary>
    public string ProvisioningConnectionString(int port) =>
        ProvisionerPassword is { Length: > 0 } p ? $"Host=127.0.0.1;Port={port};Database=postgres;Username={ProvisionerUser};Password={p};Pooling=false" : "";
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

/// <summary>
/// Package integrity: every file listed in package-manifest.json (format 2) has its recorded SHA-256. A first, cheap pass checks that every
/// listed file exists (a partial extraction is reported in seconds, without hashing anything) and sums the sizes; the second pass hashes the
/// files in chunks, reporting progress after each file and honouring cancellation between chunks. Nothing is skipped and there is no time limit:
/// on a freshly extracted package antivirus scanning can make the first full check take minutes, so the caller shows progress instead.
/// </summary>
public static class PackageIntegrity
{
    public sealed record Result(int Checked, IReadOnlyList<string> Missing, IReadOnlyList<string> Changed)
    {
        /// <summary>Files that exist but could not be read (locked by another program, or no read permission).</summary>
        public IReadOnlyList<string> Unreadable { get; init; } = [];
        public bool Ok => Missing.Count == 0 && Changed.Count == 0 && Unreadable.Count == 0;
    }

    /// <summary>Where the check is: files and bytes done of the total, and the file being read since <see cref="CurrentSince"/>.</summary>
    public sealed record Progress(int Done, int Total, long BytesDone, long BytesTotal, string? Current, DateTime CurrentSince);

    /// <summary>
    /// Verifies the listed files; <paramref name="only"/> limits the check to those package-relative paths (the quick per-start check).
    /// <paramref name="progress"/> is called before and after each file (from the verifying thread).
    /// </summary>
    public static Result Verify(string root, IReadOnlyCollection<string>? only = null, Action<Progress>? progress = null, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(root, "package-manifest.json");
        if (!File.Exists(manifestPath))
            throw new LauncherException("This BirkNext package is incomplete: its file list is missing.", "Download the BirkNext Tester Package again and extract the whole archive.");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!doc.RootElement.TryGetProperty("format", out var format) || format.GetInt32() != 2 || !doc.RootElement.TryGetProperty("fileHashes", out var hashes))
            throw new LauncherException("This BirkNext package is damaged: its file list is not valid.", "Download the BirkNext Tester Package again.");
        var filter = only?.Select(p => p.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packageRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var missing = new List<string>(); var changed = new List<string>(); var unreadable = new List<string>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<(string Rel, string Full, string Sha256, long Length)>();

        // Pass 1 (metadata only): every listed file exists; never follow a manifest entry out of the package.
        foreach (var entry in hashes.EnumerateArray())
        {
            var rel = entry.GetProperty("path").GetString() ?? "";
            listed.Add(rel);
            if (filter is not null && !filter.Contains(rel)) continue;
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase)) { changed.Add(rel); continue; }
            var info = new FileInfo(full);
            if (!info.Exists) { missing.Add(rel); continue; }
            files.Add((rel, full, entry.GetProperty("sha256").GetString() ?? "", info.Length));
        }
        if (filter is not null) missing.AddRange(filter.Where(wanted => !listed.Contains(wanted)));
        var count = files.Count + missing.Count(m => listed.Contains(m));
        // A missing file already decides the outcome ("incomplete"): report it at once instead of hashing everything else first.
        if (missing.Count > 0) return new Result(count, missing, changed);

        // Pass 2: the hashes, in chunks (cancellable inside large files).
        var total = files.Count; var bytesTotal = files.Sum(f => f.Length); long bytesDone = 0; var done = 0;
        var buffer = new byte[1 << 20];
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(new Progress(done, total, bytesDone, bytesTotal, file.Rel, DateTime.UtcNow));
            try
            {
                using var stream = new FileStream(file.Full, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (!string.Equals(Convert.ToHexString(sha.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase)) changed.Add(file.Rel);
            }
            catch (FileNotFoundException) { missing.Add(file.Rel); }
            catch (DirectoryNotFoundException) { missing.Add(file.Rel); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { unreadable.Add($"{file.Rel} ({e.GetType().Name}: {e.Message})"); }
            done++; bytesDone += file.Length;
            progress?.Invoke(new Progress(done, total, bytesDone, bytesTotal, null, DateTime.UtcNow));
        }
        return new Result(count, missing, changed) { Unreadable = unreadable };
    }

    /// <summary>SHA-256 of the manifest itself: identifies the package build whose full verification is remembered.</summary>
    public static string ManifestHash(string root)
    {
        using var stream = File.OpenRead(Path.Combine(root, "package-manifest.json"));
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>The tester's progress line for <paramref name="p"/> after <paramref name="elapsed"/>. Pure.</summary>
    public static string Describe(Progress p, TimeSpan elapsed, DateTime now, TimeSpan slowFile)
    {
        var percent = p.BytesTotal > 0 ? (int)(p.BytesDone * 100 / p.BytesTotal) : p.Total > 0 ? p.Done * 100 / p.Total : 0;
        // English launcher text: invariant number formatting (a Norwegian PC would otherwise print "2 739").
        var text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Still checking application files: {p.Done:N0} of {p.Total:N0} files ({percent}%, {p.BytesDone / (1 << 20):N0} of {p.BytesTotal / (1 << 20):N0} MB), {(int)elapsed.TotalSeconds} s");
        if (p.Current is { } current && now - p.CurrentSince >= slowFile)
            text += string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $" — {current} has been read for {(int)(now - p.CurrentSince).TotalSeconds} s; antivirus software may be scanning it");
        return text;
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
