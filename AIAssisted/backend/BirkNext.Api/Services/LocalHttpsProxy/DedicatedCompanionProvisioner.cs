using System.Security.Cryptography;
using System.Text.Json;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.LocalHttpsProxy;

public sealed record DedicatedBootstrapRequest(string LaunchToken, string ExtensionVersion, string BuildId);

/// <summary>One owned launch, local packaged files, no frontend-supplied paths, no policy changes.</summary>
public sealed class DedicatedCompanionProvisioner(BrowserCompanionService companion, TimeProvider time,
    ILogger<DedicatedCompanionProvisioner> logger)
{
    private readonly object _gate = new();
    private string? _token, _profile, _version, _buildId, _extensionOrigin;
    private BrowserCompanionPairingStartRequest? _scope;
    private DateTimeOffset _requestedAt;
    private DateTimeOffset? _observedAt;
    private DedicatedCompanionReadiness _state = new();
    public static string ManagedDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext", "BrowserCompanion", "current");

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
                ValidateManagedPath(ManagedDirectory);
                foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(ManagedDirectory, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, true);
                }
                _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                _profile = scope.ProfileId;
                var origin = ApplicationPagePolicy.CanonicalOrigin(scope.TargetUrl);
                _scope = new(scope.ProfileId, scope.ProfileId, scope.EnvironmentType,
                    origin is not null && ApplicationPagePolicy.IsApplicationOrigin(origin) ? [origin] : []);
                File.WriteAllText(Path.Combine(ManagedDirectory, "dedicated-launch.json"),
                    JsonSerializer.Serialize(new { launchToken = _token, backend = "http://127.0.0.1:5000" }));
                _requestedAt = time.GetUtcNow();
                _state = new() { State = "AwaitingCompanion", LoadRequested = true, ExpectedVersion = _version,
                    Message = "Companion load requested; waiting up to 45 seconds for the dedicated extension to report." };
                logger.LogInformation("Dedicated Edge Companion load requested");
                return ManagedDirectory;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            {
                _token = null;
                _state = new() { State = "BuildUnavailable", Message = "Browser Companion packaged files or managed directory are invalid or unreadable. Proxy-only testing remains available." };
                return null;
            }
        }
    }

    internal static void ValidateManagedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['"', ',', '\r', '\n']) >= 0 ||
            !string.Equals(Path.GetFullPath(path), Path.GetFullPath(ManagedDirectory), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only the BirkNext-managed Companion directory is supported.");
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("Linked directories are not supported.");
        if (Directory.Exists(path))
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("Linked extension files are not supported.");
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
            if (_token is null || request.LaunchToken is null || request.LaunchToken.Length != _token.Length ||
                !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(_token), System.Text.Encoding.UTF8.GetBytes(request.LaunchToken)) ||
                !BrowserCompanionService.IsExtensionOrigin(origin) || (_extensionOrigin is not null && _extensionOrigin != origin))
                return new() { Message = "Dedicated browser launch is no longer current." };
            _extensionOrigin = origin;
            if (_observedAt is null) logger.LogInformation("Dedicated Edge Companion loaded capability observed");
            _observedAt = time.GetUtcNow();
            if (request.ExtensionVersion != _version || request.BuildId != _buildId)
            {
                _state = _state with { State = "VersionMismatch", Message = "Companion build differs from this BirkNext build. Restart Dedicated Edge with Browser Companion." };
                return new() { Message = _state.Message };
            }
            if (_scope?.ApprovedOrigins.Count != 1) return new() { Message = "No approved application origin is available. Authentication infrastructure hosts are never application origins." };
            var paired = companion.PairDedicated(_scope, _token, _version!, origin);
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
            if (_token is null) return _state;
            var live = companion.DedicatedReadiness(_profile!, _token, _version!, _buildId!);
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
            if (_token is not null && _profile is not null) companion.RetireDedicated(_profile, _token);
            _token = _profile = _extensionOrigin = null; _observedAt = null; _state = new();
        }
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
