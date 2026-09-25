using System.Security.Cryptography;
using System.Text.Json;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <param name="PermissionPending">The extension holds this launch's session but lacks the approved-origin permission.</param>
public sealed record DedicatedBootstrapRequest(string LaunchToken, string ExtensionVersion, string BuildId, bool? PermissionPending = null);

/// <summary>One owned launch, local packaged files, no frontend-supplied paths, no policy changes.</summary>
public sealed class DedicatedCompanionProvisioner(BrowserCompanionService companion, TimeProvider time,
    ILogger<DedicatedCompanionProvisioner> logger, string? stateDirectory = null)
{
    private readonly object _gate = new();
    /// <summary>
    /// SHA-256 of the current launch token, also the session's DedicatedLaunchId. The raw token exists only in the
    /// managed extension copy; the backend keeps and persists its hash, never the token.
    /// </summary>
    private string? _tokenHash, _profile, _version, _buildId, _extensionOrigin;
    private BrowserCompanionPairingStartRequest? _scope;
    private DateTimeOffset _requestedAt;
    private DateTimeOffset? _observedAt;
    private DedicatedCompanionReadiness _state = new();
    public static string ManagedRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext", "BrowserCompanion");

    /// <summary>
    /// One directory per Companion build, never reused for another build. An unpacked extension's id is derived from its
    /// path, and Edge keeps an extension's registered service worker across restarts and version changes: it re-reads the
    /// manifest and resources but runs the worker script it cached when the id was first registered. With one fixed
    /// directory a Dedicated Edge profile ran its first Companion worker forever, whatever BirkNext copied later, and the
    /// stale worker still passed the build check because it re-reads build-info.json. A new build gets a new directory,
    /// therefore a never-registered id, therefore its own worker. The same build relaunched keeps its id and its session.
    /// </summary>
    public static string ManagedDirectoryFor(string buildId) =>
        Path.Combine(ManagedRoot, "build-" + buildId[..12].ToLowerInvariant());

    private static readonly System.Text.RegularExpressions.Regex BuildDirectoryName =
        new("^build-[0-9a-f]{12}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// How long a launch stays recognisable after a backend restart. Bounded by the Companion session's own absolute
    /// lifetime; a graceful stop retires the launch (and closes Dedicated Edge) long before this.
    /// </summary>
    public static readonly TimeSpan LaunchRecordLifetime = BrowserCompanionLimits.SessionAbsoluteLifetime;

    // Outside the extension directory, so the extension (and anything it serves) never sees it.
    private string RecordPath => Path.Combine(stateDirectory ?? ManagedRoot, "dedicated-launch-record.json");

    public string? Prepare(LocalHttpsProxyScopeRequest scope)
    {
        lock (_gate)
        {
            Retire();
            var policy = ReadPolicyLimitation();
            if (policy is not null) { _state = new() { State = "PolicyBlocked", Message = policy }; return null; }
            var source = Path.Combine(AppContext.BaseDirectory, "BrowserCompanion");
            if (!File.Exists(Path.Combine(source, "build-info.json")))
            { _state = new() { State = "BuildMissing", Message = "Browser Companion build not available. Proxy-based authenticated API testing can continue." }; return null; }
            try
            {
                using var build = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "build-info.json")));
                _version = build.RootElement.GetProperty("version").GetString()!;
                _buildId = build.RootElement.GetProperty("buildId").GetString()!;
                ValidatePackage(source, _version, _buildId);
                var directory = ManagedDirectoryFor(_buildId);
                ValidateManagedPath(directory);
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(directory, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, true);
                }
                var token = IssueLaunch(scope, _version, _buildId);
                WriteDedicatedManifest(directory, _scope!.ApprovedOrigins.SingleOrDefault());
                File.WriteAllText(Path.Combine(directory, "dedicated-launch.json"),
                    JsonSerializer.Serialize(new { launchToken = token, backend = "http://127.0.0.1:5000" }));
                RemoveOtherBuilds(directory);
                logger.LogInformation("Dedicated Edge Companion load requested");
                return directory;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            {
                _tokenHash = null;
                DeleteRecord();
                _state = new() { State = "BuildUnavailable", Message = "Browser Companion packaged files or managed directory are invalid or unreadable. Proxy-only testing remains available." };
                return null;
            }
        }
    }

    /// <summary>
    /// Starts one launch: a fresh token for the managed copy, its hash kept here and persisted with the scope, so a
    /// backend that restarts while Dedicated Edge is still open still recognises that browser.
    /// </summary>
    internal string IssueLaunch(LocalHttpsProxyScopeRequest scope, string version, string buildId)
    {
        lock (_gate)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _tokenHash = Hash(token);
            _profile = scope.ProfileId;
            _version = version;
            _buildId = buildId;
            _extensionOrigin = null;
            _observedAt = null;
            var origin = ApplicationPagePolicy.CanonicalOrigin(scope.TargetUrl);
            _scope = new(scope.ProfileId, scope.ProfileId, scope.EnvironmentType,
                origin is not null && ApplicationPagePolicy.IsApplicationOrigin(origin) ? [origin] : []);
            _requestedAt = time.GetUtcNow();
            _state = new() { State = "AwaitingCompanion", LoadRequested = true, ExpectedVersion = _version,
                Message = "Companion load requested; waiting up to 45 seconds for the dedicated extension to report." };
            SaveRecord();
            return token;
        }
    }

    /// <summary>
    /// Declares the exact current approved origin as a host permission of the managed Dedicated Edge copy, and nothing
    /// broader. An optional host permission can only be granted from an extension page with a user gesture, and nothing
    /// in a freshly launched Dedicated Edge carries one, so its session used to park forever. A declared host permission
    /// of an extension BirkNext loads itself is granted when Edge loads it, and Edge policy (runtime_blocked_hosts) still
    /// applies. The normal-Edge extension keeps its optional permissions and the popup prompt. No application origin
    /// (for example an authentication host, which is never one) means none is declared.
    /// </summary>
    internal static void WriteDedicatedManifest(string directory, string? approvedOrigin)
    {
        var path = Path.Combine(directory, "manifest.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var hosts = new System.Text.Json.Nodes.JsonArray("http://127.0.0.1/*", "http://localhost/*");
        if (ApplicationPagePolicy.CanonicalOrigin(approvedOrigin) is { } origin && ApplicationPagePolicy.IsApplicationOrigin(origin))
            hosts.Add($"{origin}/*");
        manifest["host_permissions"] = hosts;
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void ValidateManagedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['"', ',', '\r', '\n']) >= 0 ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(ManagedRoot), StringComparison.OrdinalIgnoreCase) ||
            !BuildDirectoryName.IsMatch(Path.GetFileName(Path.GetFullPath(path))))
            throw new ArgumentException("Only the BirkNext-managed Companion directory is supported.");
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("Linked directories are not supported.");
        if (Directory.Exists(path))
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("Linked extension files are not supported.");
    }

    /// <summary>
    /// Earlier builds' copies (and the former fixed "current" directory) are never loaded again. Best effort: a copy that
    /// cannot be removed now is removed at a later launch.
    /// </summary>
    private void RemoveOtherBuilds(string keep)
    {
        foreach (var dir in Directory.EnumerateDirectories(ManagedRoot))
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase) ||
                !(BuildDirectoryName.IsMatch(name) || name == "current")) continue;
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logger.LogInformation("An earlier Companion build copy is still in use and will be removed at a later launch"); }
        }
    }

    internal static void ValidatePackage(string path, string version, string buildId)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")));
        if (manifest.RootElement.GetProperty("manifest_version").GetInt32() != 3 ||
            manifest.RootElement.GetProperty("version").GetString() != version || buildId.Length != 64)
            throw new ArgumentException("Incompatible Companion package.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                     .Where(f => Path.GetFileName(f) != "build-info.json")
                     .OrderBy(f => Path.GetRelativePath(path, f).Replace('\\', '/'), StringComparer.Ordinal))
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("Linked package file.");
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(path, file).Replace('\\', '/')));
            hash.AppendData(File.ReadAllBytes(file));
        }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(buildId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Companion package build identity mismatch.");
    }

    public BrowserCompanionPairResult Observe(DedicatedBootstrapRequest request, string origin)
    {
        lock (_gate)
        {
            if (_tokenHash is null) RestoreRecord();
            if (_tokenHash is null || request.LaunchToken is not { Length: 64 } ||
                !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(_tokenHash), System.Text.Encoding.ASCII.GetBytes(Hash(request.LaunchToken))) ||
                !BrowserCompanionService.IsExtensionOrigin(origin) || (_extensionOrigin is not null && _extensionOrigin != origin))
                return new() { Message = "Dedicated browser launch is no longer current." };
            if (_extensionOrigin is null) { _extensionOrigin = origin; SaveRecord(); }
            if (_observedAt is null) logger.LogInformation("Dedicated Edge Companion loaded capability observed");
            _observedAt = time.GetUtcNow();
            if (request.ExtensionVersion != _version || request.BuildId != _buildId)
            {
                _state = _state with { State = "VersionMismatch", Message = "Companion build differs from this BirkNext build. Restart Dedicated Edge with Browser Companion." };
                return new() { Message = _state.Message };
            }
            if (_scope?.ApprovedOrigins.Count != 1) return new() { Message = "No approved application origin is available. Authentication infrastructure hosts are never application origins." };
            var paired = companion.PairDedicated(_scope, _tokenHash, _version!, origin, request.PermissionPending == true);
            _state = _state with { State = paired.Accepted ? "AwaitingHeartbeat" : "SessionConflict", Message = paired.Message };
            return paired;
        }
    }

    public DedicatedCompanionReadiness Status(bool running)
    {
        lock (_gate)
        {
            if (!running) return _state with { Connected = false, LoadedObserved = false, ApprovedPageAvailable = false, ElementPickAvailable = false };
            var loaded = _observedAt is { } at && time.GetUtcNow() - at <= BrowserCompanionLimits.ConnectedWindow;
            if (_tokenHash is null) return _state;
            var live = companion.DedicatedReadiness(_profile!, _tokenHash, _version!, _buildId!);
            if (live.State != "NotRequested" && _state.State != "VersionMismatch")
                return live with { LoadedObserved = loaded, LoadRequested = true, ExpectedVersion = _version };
            return _state with { LoadedObserved = loaded,
                State = _observedAt is null && time.GetUtcNow() - _requestedAt > TimeSpan.FromSeconds(45) ? "NotObserved" : _state.State,
                Message = _observedAt is null && time.GetUtcNow() - _requestedAt > TimeSpan.FromSeconds(45)
                    ? "Companion not observed in Dedicated Edge. Edge may have ignored or blocked automatic loading. Check Edge extension policy or use IT-managed installation; proxy-only testing can continue."
                    : _state.Message };
        }
    }

    public void Retire()
    {
        lock (_gate)
        {
            if (_tokenHash is not null && _profile is not null) companion.RetireDedicated(_profile, _tokenHash);
            _tokenHash = _profile = _extensionOrigin = null; _observedAt = null; _state = new();
            DeleteRecord();
        }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private sealed record LaunchRecord(string TokenSha256, string ProfileId, string? EnvironmentType, IReadOnlyList<string> ApprovedOrigins,
        string Version, string BuildId, string? ExtensionOrigin, DateTimeOffset IssuedAt);

    private void SaveRecord()
    {
        if (_tokenHash is null || _scope is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RecordPath)!);
            File.WriteAllText(RecordPath, JsonSerializer.Serialize(new LaunchRecord(_tokenHash, _scope.ProfileId, _scope.EnvironmentType,
                _scope.ApprovedOrigins, _version!, _buildId!, _extensionOrigin, _requestedAt)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { logger.LogWarning("Dedicated Edge launch record could not be saved; a backend restart will need a new Dedicated Edge launch"); }
    }

    /// <summary>A backend that restarted while Dedicated Edge stayed open adopts that launch, within its lifetime.</summary>
    private void RestoreRecord()
    {
        try
        {
            if (!File.Exists(RecordPath)) return;
            var record = JsonSerializer.Deserialize<LaunchRecord>(File.ReadAllText(RecordPath));
            var now = time.GetUtcNow();
            if (record is null || record.TokenSha256.Length != 64 || now - record.IssuedAt > LaunchRecordLifetime || now < record.IssuedAt)
            { DeleteRecord(); return; }
            _tokenHash = record.TokenSha256; _profile = record.ProfileId; _version = record.Version; _buildId = record.BuildId;
            _extensionOrigin = record.ExtensionOrigin; _requestedAt = record.IssuedAt;
            _scope = new(record.ProfileId, record.ProfileId, record.EnvironmentType, record.ApprovedOrigins);
            _state = new() { State = "AwaitingHeartbeat", LoadRequested = true, ExpectedVersion = _version,
                Message = "Dedicated Edge launch restored after a backend restart; waiting for the Companion to reconnect." };
            logger.LogInformation("Dedicated Edge launch restored after backend restart");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { DeleteRecord(); }
    }

    private void DeleteRecord()
    {
        try { File.Delete(RecordPath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static string? ReadPolicyLimitation()
    {
        if (!OperatingSystem.IsWindows()) return "Automatic Dedicated Edge provisioning requires Windows.";
        try
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Edge");
                if (key is null) continue;
                using var types = key.OpenSubKey("ExtensionInstallTypeBlocklist");
                if (types?.GetValueNames().Any(n => string.Equals(types.GetValue(n) as string, "command_line", StringComparison.OrdinalIgnoreCase)) == true)
                    return "Edge policy ExtensionInstallTypeBlocklist contains command_line. Automatic Companion loading is blocked; ask IT about managed extension deployment. Proxy-only testing can continue.";
                if (key.GetValue("ExtensionDeveloperModeSettings") is int developer && developer == 1 ||
                    key.GetValue("ExtensionDeveloperModeSettings") is null && key.GetValue("DeveloperToolsAvailability") is int tools && tools == 2)
                    return "Edge developer-extension policy disallows developer mode. Use IT-managed Companion deployment; proxy-only testing can continue.";
                using var blocked = key.OpenSubKey("ExtensionInstallBlocklist");
                if (blocked?.GetValueNames().Any(n => blocked.GetValue(n) as string == "*") == true)
                    return "Edge ExtensionInstallBlocklist blocks extensions by default. IT must confirm an approved Companion deployment; proxy-only testing can continue.";
                if (key.GetValue("ExtensionSettings") is string settings)
                {
                    using var json = JsonDocument.Parse(settings);
                    if (json.RootElement.TryGetProperty("*", out var defaults) && defaults.TryGetProperty("installation_mode", out var mode) && mode.GetString() is "blocked" or "removed")
                        return "Edge ExtensionSettings blocks extensions by default. IT must approve Companion deployment; proxy-only testing can continue.";
                }
            }
            return null; // No readable blocker, NOT proof that Edge permits loading (cloud/profile policy may differ).
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or JsonException)
        { return "Edge extension policy could not be read. Automatic loading was not attempted; ask IT to verify deployment policy."; }
    }
}
