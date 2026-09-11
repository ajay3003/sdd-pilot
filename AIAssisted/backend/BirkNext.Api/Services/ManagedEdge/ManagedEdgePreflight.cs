using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.ManagedEdge;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace BirkNext.Api.Services.ManagedEdge;

public sealed record EdgeInstallation(string ExecutablePath, string? Version);

public interface IEdgeInstallationLocator
{
    EdgeInstallation? Locate();
}

public interface IEdgePolicyReader
{
    EdgeRemoteDebuggingPolicyStatus ReadRemoteDebuggingPolicy();
}

public interface IManagedEdgeLauncher
{
    /// <summary>Starts a separate browser process. Never terminates, signals or reuses an existing process.</summary>
    bool Launch(string executablePath, IReadOnlyList<string> arguments);
}

public interface IManagedEdgePreflightService
{
    Task<ManagedEdgePreflightResult> CheckAsync(string? targetUrl, CancellationToken cancellationToken = default);
    Task<ManagedEdgePreflightResult> LaunchAsync(string? targetUrl, CancellationToken cancellationToken = default);
}

/// <summary>Standard install locations and the Windows App Paths registration only; no disk scan.</summary>
public sealed class WindowsEdgeInstallationLocator : IEdgeInstallationLocator
{
    public EdgeInstallation? Locate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var candidate in Candidates().Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!System.IO.Path.IsPathFullyQualified(candidate) || !File.Exists(candidate)) continue;
                if (!string.Equals(System.IO.Path.GetFileName(candidate), "msedge.exe", StringComparison.OrdinalIgnoreCase)) continue;
                return new EdgeInstallation(candidate, FileVersionInfo.GetVersionInfo(candidate).ProductVersion);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> Candidates()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            string? value = null;
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", false);
                value = key?.GetValue(null) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
            if (value is not null) yield return value.Trim('"');
        }
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var x64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(x86)) yield return System.IO.Path.Combine(x86, "Microsoft", "Edge", "Application", "msedge.exe");
        if (!string.IsNullOrEmpty(x64)) yield return System.IO.Path.Combine(x64, "Microsoft", "Edge", "Application", "msedge.exe");
    }
}

/// <summary>Reads the RemoteDebuggingAllowed Edge policy. Machine policy wins over user policy. Absent means NotConfigured, never Blocked.</summary>
public sealed class WindowsEdgePolicyReader : IEdgePolicyReader
{
    public EdgeRemoteDebuggingPolicyStatus ReadRemoteDebuggingPolicy()
    {
        if (!OperatingSystem.IsWindows()) return EdgeRemoteDebuggingPolicyStatus.Unknown;
        return Read();
    }

    [SupportedOSPlatform("windows")]
    private static EdgeRemoteDebuggingPolicyStatus Read()
    {
        try
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Edge", false);
                var value = key?.GetValue("RemoteDebuggingAllowed");
                if (value is int i) return i == 0 ? EdgeRemoteDebuggingPolicyStatus.Blocked : EdgeRemoteDebuggingPolicyStatus.Allowed;
                if (value is not null) return EdgeRemoteDebuggingPolicyStatus.Unknown;
            }
            return EdgeRemoteDebuggingPolicyStatus.NotConfigured;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return EdgeRemoteDebuggingPolicyStatus.Unknown;
        }
    }
}

public sealed class ProcessManagedEdgeLauncher : IManagedEdgeLauncher
{
    public bool Launch(string executablePath, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(executablePath) { UseShellExecute = false, CreateNoWindow = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info);
        return process is not null;
    }
}

public sealed class ManagedEdgePreflightService(IEdgeInstallationLocator locator, IEdgePolicyReader policy, IManagedEdgeLauncher launcher,
    IOptions<ManagedEdgeOptions> options, IOptions<AuthenticatedReviewOptions> runtime) : IManagedEdgePreflightService
{
    private readonly SemaphoreSlim _launchGate = new(1);

    public const string RemoteDeploymentReason = "Local browser integration is unavailable in this deployment mode: BirkNext.Api is not running as a local workstation runtime, so it cannot start or attach to a browser on your PC.";
    public const string PolicyBlockedReason = "Remote debugging is blocked by Microsoft Edge policy (RemoteDebuggingAllowed = 0). BirkNext does not bypass organization policy.";
    public const string EdgeMissingReason = "Microsoft Edge was not found in the standard installation locations.";
    public const string ExistingInstanceReason = "A compatible Microsoft Edge with remote debugging is already running on the CDP endpoint. Connect to it instead of starting another instance.";
    public const string PortOccupiedReason = "The CDP port is occupied by a service that does not speak the DevTools protocol. BirkNext does not stop the occupying process; free the port or configure another loopback port.";
    public const string EndpointInactiveReason = "Remote debugging is not active on the CDP endpoint. Start Edge for authenticated testing or start an approved Edge instance manually.";

    public static string DefaultProfileDirectory() => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext", "ManagedEdgeProfile");

    /// <summary>Only a loopback port, a dedicated profile directory, first-run suppression and the validated target URL. Never the normal Edge profile.</summary>
    public static IReadOnlyList<string> BuildLaunchArguments(int port, string profileDirectory, string? targetUrl)
    {
        if (port is < 1024 or > 65535) throw new ArgumentException("CDP port must be an unprivileged loopback port.");
        if (!System.IO.Path.IsPathFullyQualified(profileDirectory) || IsNormalEdgeProfile(profileDirectory))
            throw new ArgumentException("A dedicated BirkNext profile directory is required; the normal Edge profile is never reused.");
        var arguments = new List<string> { $"--remote-debugging-port={port}", $"--user-data-dir={profileDirectory}", "--no-first-run", "--no-default-browser-check" };
        if (!string.IsNullOrWhiteSpace(targetUrl))
        {
            ManagedEdgePolicy.Origin(targetUrl);
            arguments.Add(new Uri(targetUrl, UriKind.Absolute).AbsoluteUri);
        }
        return arguments;
    }

    public static bool IsNormalEdgeProfile(string directory)
    {
        var normalized = directory.Replace('/', '\\').TrimEnd('\\');
        return normalized.Contains(@"\Microsoft\Edge\User Data", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\Microsoft\Edge Beta\User Data", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\Microsoft\Edge Dev\User Data", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(@"\Microsoft\Edge SxS\User Data", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ManagedEdgePreflightResult> CheckAsync(string? targetUrl, CancellationToken cancellationToken = default)
    {
        var endpoint = ManagedEdgePolicy.Endpoint(options.Value.Endpoint);
        var result = new ManagedEdgePreflightResult { CdpEndpoint = endpoint.AbsoluteUri.TrimEnd('/') };
        if (!runtime.Value.IsLocalWorkstation)
            return result with { LocalBrowserIntegrationAvailable = false, FailureReason = RemoteDeploymentReason };
        result = result with { LocalBrowserIntegrationAvailable = true };

        string? origin = null;
        if (!string.IsNullOrWhiteSpace(targetUrl))
        {
            try { origin = ManagedEdgePolicy.Origin(targetUrl); }
            catch (ArgumentException) { origin = null; }
        }
        result = result with { TargetOrigin = origin };

        var installation = SafeLocate();
        result = result with { EdgeInstalled = installation is not null, EdgeExecutablePath = installation?.ExecutablePath, EdgeVersion = installation?.Version };

        var policyStatus = SafePolicy();
        result = result with { RemoteDebuggingPolicyStatus = policyStatus };

        result = await ProbeAsync(result, endpoint, origin, cancellationToken);

        var blocked = policyStatus == EdgeRemoteDebuggingPolicyStatus.Blocked;
        var canConnect = !blocked && result.RemoteDebuggingRuntimeActive;
        var portFree = !result.CdpEndpointReachable && !result.PortInUseByOtherService;
        var canLaunch = !blocked && result.EdgeInstalled && portFree;
        var reason = blocked ? PolicyBlockedReason
            : result.PortInUseByOtherService ? PortOccupiedReason
            : result.RemoteDebuggingRuntimeActive ? (result.EdgeInstalled ? null : EdgeMissingReason)
            : !result.EdgeInstalled ? EdgeMissingReason
            : EndpointInactiveReason;
        return result with { CanConnect = canConnect, CanLaunchTestEdge = canLaunch, FailureReason = reason };
    }

    public async Task<ManagedEdgePreflightResult> LaunchAsync(string? targetUrl, CancellationToken cancellationToken = default)
    {
        await _launchGate.WaitAsync(cancellationToken);
        try
        {
            var preflight = await CheckAsync(targetUrl, cancellationToken);
            if (!preflight.LocalBrowserIntegrationAvailable) return preflight;
            if (preflight.RemoteDebuggingRuntimeActive) return preflight with { CanLaunchTestEdge = false, FailureReason = ExistingInstanceReason };
            if (!preflight.CanLaunchTestEdge) return preflight;

            var endpoint = new Uri(preflight.CdpEndpoint);
            var profileDirectory = string.IsNullOrWhiteSpace(options.Value.ProfileDirectory) ? DefaultProfileDirectory() : options.Value.ProfileDirectory;
            IReadOnlyList<string> arguments;
            try
            {
                arguments = BuildLaunchArguments(endpoint.Port, profileDirectory, preflight.TargetOrigin is null ? null : targetUrl);
                Directory.CreateDirectory(profileDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return preflight with { CanLaunchTestEdge = false, FailureReason = "The dedicated BirkNext Edge profile directory is not usable. The normal Edge profile is never reused." };
            }

            bool started;
            try { started = launcher.Launch(preflight.EdgeExecutablePath!, arguments); }
            catch (Exception) { started = false; }
            if (!started) return preflight with { CanLaunchTestEdge = false, FailureReason = "Microsoft Edge did not start. No existing browser was affected." };

            var timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.LaunchTimeoutSeconds, 1, 120));
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
                var current = await CheckAsync(targetUrl, cancellationToken);
                if (current.RemoteDebuggingRuntimeActive)
                    return current with { EdgeStarted = true, CanLaunchTestEdge = false };
            }
            var final = await CheckAsync(targetUrl, cancellationToken);
            return final with { EdgeStarted = true, CanLaunchTestEdge = false,
                FailureReason = $"Edge started, but remote debugging did not become ready within {timeout.TotalSeconds:0} seconds. Check the Edge window, then run the compatibility check again." };
        }
        finally { _launchGate.Release(); }
    }

    private EdgeInstallation? SafeLocate()
    {
        try { return locator.Locate(); }
        catch (Exception) { return null; }
    }

    private EdgeRemoteDebuggingPolicyStatus SafePolicy()
    {
        try { return policy.ReadRemoteDebuggingPolicy(); }
        catch (Exception) { return EdgeRemoteDebuggingPolicyStatus.Unknown; }
    }

    private static async Task<ManagedEdgePreflightResult> ProbeAsync(ManagedEdgePreflightResult result, Uri endpoint, string? origin, CancellationToken cancellationToken)
    {
        bool portOpen;
        try
        {
            using var tcp = new TcpClient();
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(endpoint.Host, endpoint.Port, connectTimeout.Token);
            portOpen = true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested) { portOpen = false; }
        if (!portOpen) return result with { CdpEndpointReachable = false, CdpProtocolValid = false };

        // Reject every redirect, proxy and cookie path; accept only a loopback browser WebSocket on the same host and port.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = 65536 };
        try
        {
            using var response = await http.GetAsync(new Uri(endpoint, "/json/version"), cancellationToken);
            if (!response.IsSuccessStatusCode) return result with { CdpEndpointReachable = true, CdpProtocolValid = false, PortInUseByOtherService = true };
            var version = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (version.ValueKind != JsonValueKind.Object ||
                !version.TryGetProperty("Browser", out var browser) || browser.ValueKind != JsonValueKind.String ||
                !version.TryGetProperty("webSocketDebuggerUrl", out var socket) || socket.ValueKind != JsonValueKind.String)
                return result with { CdpEndpointReachable = true, CdpProtocolValid = false, PortInUseByOtherService = true };
            var websocket = ManagedEdgePolicy.Endpoint(socket.GetString()!, true);
            if (websocket.Port != endpoint.Port || websocket.Host != endpoint.Host)
                return result with { CdpEndpointReachable = true, CdpProtocolValid = false, PortInUseByOtherService = true };
            var (product, productVersion) = SplitProduct(browser.GetString()!);
            result = result with { CdpEndpointReachable = true, CdpProtocolValid = true, BrowserProduct = product, BrowserVersion = productVersion };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return result with { CdpEndpointReachable = true, CdpProtocolValid = false, PortInUseByOtherService = true };
        }

        if (origin is null) return result;
        try
        {
            using var response = await http.GetAsync(new Uri(endpoint, "/json/list"), cancellationToken);
            if (!response.IsSuccessStatusCode) return result;
            var targets = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            if (targets.ValueKind != JsonValueKind.Array) return result;
            var count = targets.EnumerateArray().Count(t => t.ValueKind == JsonValueKind.Object &&
                t.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "page" &&
                t.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && ManagedEdgePolicy.MatchesOrigin(url.GetString()!, origin));
            return result with { TargetTabFound = count > 0, TargetTabCount = count };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested) { return result; }
    }

    private static (string Product, string? Version) SplitProduct(string browser)
    {
        var slash = browser.IndexOf('/');
        var product = slash < 0 ? browser : browser[..slash];
        var version = slash < 0 ? null : browser[(slash + 1)..];
        return (Sanitize(product, 32), version is null ? null : Sanitize(version, 32));
    }

    private static string Sanitize(string value, int max) => new(value.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ').Take(max).ToArray());
}
