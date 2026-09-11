using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using BirkNext.Api.Controllers;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.ManagedEdge;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Tests.Services;

public sealed class ManagedEdgePreflightTests
{
    private const string Origin = "https://m2lbdev.bufetat.no";
    private const string TargetUrl = Origin + "/";
    private static readonly EdgeInstallation Installed = new(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "152.0.4191.66");

    private sealed class FakeLocator(EdgeInstallation? installation) : IEdgeInstallationLocator { public EdgeInstallation? Locate() => installation; }
    private sealed class FakePolicy(EdgeRemoteDebuggingPolicyStatus status) : IEdgePolicyReader { public EdgeRemoteDebuggingPolicyStatus ReadRemoteDebuggingPolicy() => status; }
    private sealed class FakeLauncher : IManagedEdgeLauncher
    {
        public List<(string Path, IReadOnlyList<string> Arguments)> Launches { get; } = [];
        public Func<bool>? OnLaunch { get; set; }
        public bool Launch(string executablePath, IReadOnlyList<string> arguments) { Launches.Add((executablePath, arguments)); return OnLaunch?.Invoke() ?? true; }
    }

    /// <summary>Minimal loopback HTTP server that answers /json/version and /json/list; never redirects unless told to.</summary>
    private sealed class FakeCdpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        public int Port { get; }
        public string VersionResponse { get; set; }
        public string ListJson { get; set; } = "[]";
        public int Requests;
        public FakeCdpServer(int port = 0, string? websocket = null)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            VersionResponse = Json("{\"Browser\":\"Edg/152.0.4191.66\",\"Protocol-Version\":\"1.3\",\"webSocketDebuggerUrl\":\"" + (websocket ?? $"ws://127.0.0.1:{Port}/devtools/browser/abc") + "\"}");
            _ = Task.Run(AcceptAsync);
        }
        public static string Json(string body) => $"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=UTF-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using var peer = client;
                    var stream = peer.GetStream();
                    var buffer = new byte[8192];
                    var read = await stream.ReadAsync(buffer);
                    var request = Encoding.ASCII.GetString(buffer, 0, read);
                    Interlocked.Increment(ref Requests);
                    var response = request.StartsWith("GET /json/list") ? Json(ListJson) : VersionResponse;
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
                });
            }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static ManagedEdgePreflightService Service(int port, EdgeInstallation? installation = null, EdgeRemoteDebuggingPolicyStatus policy = EdgeRemoteDebuggingPolicyStatus.NotConfigured,
        FakeLauncher? launcher = null, bool local = true, string? profileDirectory = null, int launchTimeoutSeconds = 1) =>
        new(new FakeLocator(installation), new FakePolicy(policy), launcher ?? new FakeLauncher(),
            Options.Create(new ManagedEdgeOptions { Endpoint = $"http://127.0.0.1:{port}", ProfileDirectory = profileDirectory, LaunchTimeoutSeconds = launchTimeoutSeconds }),
            Options.Create(new AuthenticatedReviewOptions { Enabled = local, Runtime = local ? "LocalWorkstation" : "Unsupported" }));

    [Fact] // A
    public async Task EdgeNotInstalledIsReportedAndLaunchDisabled()
    {
        var result = await Service(FreePort()).CheckAsync(TargetUrl);
        Assert.True(result.LocalBrowserIntegrationAvailable);
        Assert.False(result.EdgeInstalled);
        Assert.Null(result.EdgeExecutablePath);
        Assert.False(result.CanLaunchTestEdge);
        Assert.Equal(ManagedEdgePreflightService.EdgeMissingReason, result.FailureReason);
    }

    [Fact] // B
    public async Task EdgeInstalledIsReportedWithVersion()
    {
        var result = await Service(FreePort(), Installed).CheckAsync(TargetUrl);
        Assert.True(result.EdgeInstalled);
        Assert.Equal(Installed.ExecutablePath, result.EdgeExecutablePath);
        Assert.Equal("152.0.4191.66", result.EdgeVersion);
        Assert.True(result.CanLaunchTestEdge);
    }

    [Fact] // C
    public async Task BlockedPolicyDisablesConnectAndLaunchEvenWithValidCdp()
    {
        using var cdp = new FakeCdpServer();
        var launcher = new FakeLauncher();
        var service = Service(cdp.Port, Installed, EdgeRemoteDebuggingPolicyStatus.Blocked, launcher);
        var result = await service.CheckAsync(TargetUrl);
        Assert.Equal(EdgeRemoteDebuggingPolicyStatus.Blocked, result.RemoteDebuggingPolicyStatus);
        Assert.True(result.RemoteDebuggingRuntimeActive);
        Assert.False(result.CanConnect);
        Assert.False(result.CanLaunchTestEdge);
        Assert.Equal(ManagedEdgePreflightService.PolicyBlockedReason, result.FailureReason);
        var launch = await service.LaunchAsync(TargetUrl);
        Assert.False(launch.EdgeStarted);
        Assert.Empty(launcher.Launches);
    }

    [Theory] // D, E, F
    [InlineData(EdgeRemoteDebuggingPolicyStatus.Allowed)]
    [InlineData(EdgeRemoteDebuggingPolicyStatus.NotConfigured)]
    [InlineData(EdgeRemoteDebuggingPolicyStatus.Unknown)]
    public async Task NonBlockingPolicyPermitsLaunch(EdgeRemoteDebuggingPolicyStatus policy)
    {
        var result = await Service(FreePort(), Installed, policy).CheckAsync(TargetUrl);
        Assert.Equal(policy, result.RemoteDebuggingPolicyStatus);
        Assert.True(result.CanLaunchTestEdge);
        Assert.Equal(ManagedEdgePreflightService.EndpointInactiveReason, result.FailureReason);
    }

    [Fact] // G
    public async Task UnavailableEndpointIsNotActiveAndNotConnectable()
    {
        var result = await Service(FreePort(), Installed).CheckAsync(TargetUrl);
        Assert.False(result.CdpEndpointReachable);
        Assert.False(result.CdpProtocolValid);
        Assert.False(result.RemoteDebuggingRuntimeActive);
        Assert.False(result.PortInUseByOtherService);
        Assert.False(result.CanConnect);
    }

    [Fact] // H
    public async Task ValidVersionDocumentIsActiveAndFindsTargetTab()
    {
        using var cdp = new FakeCdpServer();
        cdp.ListJson = "[{\"type\":\"page\",\"url\":\"" + TargetUrl + "\"},{\"type\":\"service_worker\",\"url\":\"" + Origin + "/sw.js\"},{\"type\":\"page\",\"url\":\"https://other.test/\"},{\"type\":\"page\",\"url\":\"http://localhost:9222/json\"}]";
        var result = await Service(cdp.Port, Installed).CheckAsync(TargetUrl);
        Assert.True(result.CdpEndpointReachable);
        Assert.True(result.CdpProtocolValid);
        Assert.True(result.RemoteDebuggingRuntimeActive);
        Assert.Equal("Edg", result.BrowserProduct);
        Assert.Equal("152.0.4191.66", result.BrowserVersion);
        Assert.Equal(Origin, result.TargetOrigin);
        Assert.True(result.TargetTabFound);
        Assert.Equal(1, result.TargetTabCount);
        Assert.True(result.CanConnect);
        Assert.False(result.CanLaunchTestEdge);
        Assert.Null(result.FailureReason);
    }

    [Fact] // I
    public async Task InvalidVersionDocumentIsNotValidCdp()
    {
        using var cdp = new FakeCdpServer();
        cdp.VersionResponse = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 13\r\nConnection: close\r\n\r\n<html></html>";
        var result = await Service(cdp.Port, Installed).CheckAsync(TargetUrl);
        Assert.True(result.CdpEndpointReachable);
        Assert.False(result.CdpProtocolValid);
        Assert.True(result.PortInUseByOtherService);
        Assert.False(result.CanConnect);
        Assert.False(result.CanLaunchTestEdge);
    }

    [Fact] // J
    public async Task RedirectLeavingLoopbackIsNeverFollowed()
    {
        using var cdp = new FakeCdpServer();
        cdp.VersionResponse = "HTTP/1.1 302 Found\r\nLocation: http://192.168.1.2:9222/json/version\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        var result = await Service(cdp.Port, Installed).CheckAsync(TargetUrl);
        Assert.False(result.CdpProtocolValid);
        Assert.True(result.PortInUseByOtherService);
        Assert.Equal(1, cdp.Requests);
    }

    [Theory] // K
    [InlineData("ws://192.168.1.2:9222/devtools/browser/id")]
    [InlineData("ws://example.com:9222/devtools/browser/id")]
    [InlineData("ws://127.0.0.1:1/devtools/browser/id")]
    [InlineData("wss://127.0.0.1:9222/devtools/browser/id")]
    public async Task WebSocketLeavingLoopbackOrPortIsRejected(string websocket)
    {
        using var cdp = new FakeCdpServer(0, websocket);
        var result = await Service(cdp.Port, Installed).CheckAsync(TargetUrl);
        Assert.True(result.CdpEndpointReachable);
        Assert.False(result.CdpProtocolValid);
        Assert.False(result.CanConnect);
    }

    [Fact] // L
    public async Task PortOccupiedByNonCdpServiceIsReportedWithoutLaunch()
    {
        using var cdp = new FakeCdpServer();
        cdp.VersionResponse = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        var launcher = new FakeLauncher();
        var service = Service(cdp.Port, Installed, launcher: launcher);
        var result = await service.CheckAsync(TargetUrl);
        Assert.True(result.PortInUseByOtherService);
        Assert.False(result.CanLaunchTestEdge);
        Assert.Equal(ManagedEdgePreflightService.PortOccupiedReason, result.FailureReason);
        await service.LaunchAsync(TargetUrl);
        Assert.Empty(launcher.Launches);
    }

    [Fact] // M
    public async Task ExistingValidCdpIsReusedInsteadOfLaunchingDuplicate()
    {
        using var cdp = new FakeCdpServer();
        var launcher = new FakeLauncher();
        var result = await Service(cdp.Port, Installed, launcher: launcher).LaunchAsync(TargetUrl);
        Assert.Empty(launcher.Launches);
        Assert.False(result.EdgeStarted);
        Assert.True(result.CanConnect);
        Assert.False(result.CanLaunchTestEdge);
        Assert.Equal(ManagedEdgePreflightService.ExistingInstanceReason, result.FailureReason);
    }

    [Fact] // N
    public void LaunchArgumentsAreLoopbackOnlyWithDedicatedProfile()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BirkNextEdgeProfileTest");
        var arguments = ManagedEdgePreflightService.BuildLaunchArguments(9222, directory, TargetUrl);
        Assert.Contains("--remote-debugging-port=9222", arguments);
        Assert.Contains($"--user-data-dir={directory}", arguments);
        Assert.Contains("--no-first-run", arguments);
        Assert.Equal(TargetUrl, arguments[^1]);
        Assert.DoesNotContain(arguments, a => a.StartsWith("--remote-debugging-address", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, a => a.StartsWith("--remote-allow-origins", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, a => a.Contains("disable-web-security", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, a => a.Contains("headless", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => ManagedEdgePreflightService.BuildLaunchArguments(9222, directory, "javascript:alert(1)"));
        Assert.Throws<ArgumentException>(() => ManagedEdgePreflightService.BuildLaunchArguments(80, directory, TargetUrl));
    }

    [Fact] // O
    public void DedicatedProfileDirectoryLivesUnderBirkNextLocalAppData()
    {
        var directory = ManagedEdgePreflightService.DefaultProfileDirectory();
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), directory);
        Assert.EndsWith(System.IO.Path.Combine("BirkNext", "ManagedEdgeProfile"), directory);
        Assert.False(ManagedEdgePreflightService.IsNormalEdgeProfile(directory));
    }

    [Theory] // P
    [InlineData(@"C:\Users\tester\AppData\Local\Microsoft\Edge\User Data")]
    [InlineData(@"C:\Users\tester\AppData\Local\Microsoft\Edge\User Data\Default")]
    [InlineData(@"C:/Users/tester/AppData/Local/Microsoft/Edge/User Data/")]
    [InlineData(@"C:\Users\tester\AppData\Local\Microsoft\Edge Beta\User Data")]
    public async Task NormalEdgeProfileIsNeverReused(string directory)
    {
        Assert.True(ManagedEdgePreflightService.IsNormalEdgeProfile(directory));
        Assert.Throws<ArgumentException>(() => ManagedEdgePreflightService.BuildLaunchArguments(9222, directory, TargetUrl));
        var launcher = new FakeLauncher();
        var result = await Service(FreePort(), Installed, launcher: launcher, profileDirectory: directory).LaunchAsync(TargetUrl);
        Assert.Empty(launcher.Launches);
        Assert.False(result.EdgeStarted);
        Assert.Contains("normal Edge profile is never reused", result.FailureReason);
    }

    [Fact] // Q
    public void LaunchCodeNeverTerminatesOrEnumeratesEdgeProcesses()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(System.IO.Path.Combine(root.FullName, "AIAssisted"))) root = root.Parent;
        var source = File.ReadAllText(System.IO.Path.Combine(root!.FullName, "AIAssisted/backend/BirkNext.Api/Services/ManagedEdge/ManagedEdgePreflight.cs"));
        foreach (var forbidden in new[] { ".Kill(", "GetProcessesByName", "GetProcesses(", "taskkill", "CloseMainWindow", "CookiesAsync", "StorageState", "Authorization" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        Assert.Contains("AllowAutoRedirect = false", source);
    }

    [Fact] // R
    public async Task LaunchTimeoutIsReportedWithoutClaimingReadiness()
    {
        var launcher = new FakeLauncher();
        var result = await Service(FreePort(), Installed, launcher: launcher, launchTimeoutSeconds: 1).LaunchAsync(TargetUrl);
        Assert.Single(launcher.Launches);
        Assert.True(result.EdgeStarted);
        Assert.False(result.RemoteDebuggingRuntimeActive);
        Assert.False(result.CanConnect);
        Assert.Contains("did not become ready", result.FailureReason);
    }

    [Fact]
    public async Task SuccessfulLaunchWaitsForVersionEndpointThenReportsReadiness()
    {
        var port = FreePort();
        FakeCdpServer? cdp = null;
        var launcher = new FakeLauncher { OnLaunch = () => { cdp = new FakeCdpServer(port); return true; } };
        try
        {
            var result = await Service(port, Installed, launcher: launcher, launchTimeoutSeconds: 10).LaunchAsync(TargetUrl);
            var launch = Assert.Single(launcher.Launches);
            Assert.Equal(Installed.ExecutablePath, launch.Path);
            Assert.Contains($"--remote-debugging-port={port}", launch.Arguments);
            Assert.Contains(launch.Arguments, a => a.StartsWith("--user-data-dir=", StringComparison.Ordinal) && a.EndsWith(System.IO.Path.Combine("BirkNext", "ManagedEdgeProfile"), StringComparison.Ordinal));
            Assert.True(result.EdgeStarted);
            Assert.True(result.RemoteDebuggingRuntimeActive);
            Assert.True(result.CanConnect);
            Assert.False(result.CanLaunchTestEdge);
            Assert.False(result.TargetTabFound);
            Assert.Null(result.FailureReason);
        }
        finally { cdp?.Dispose(); }
    }

    [Fact] // V
    public async Task RemoteDeploymentNeverEvaluatesOrLaunchesLocalEdge()
    {
        using var cdp = new FakeCdpServer();
        var launcher = new FakeLauncher();
        var service = Service(cdp.Port, Installed, launcher: launcher, local: false);
        var check = await service.CheckAsync(TargetUrl);
        Assert.False(check.LocalBrowserIntegrationAvailable);
        Assert.False(check.EdgeInstalled);
        Assert.False(check.CdpEndpointReachable);
        Assert.False(check.CanConnect);
        Assert.False(check.CanLaunchTestEdge);
        Assert.Equal(ManagedEdgePreflightService.RemoteDeploymentReason, check.FailureReason);
        var launch = await service.LaunchAsync(TargetUrl);
        Assert.False(launch.EdgeStarted);
        Assert.Empty(launcher.Launches);
        Assert.Equal(0, cdp.Requests);
    }

    [Fact]
    public async Task InvalidTargetUrlDoesNotBreakCompatibilityCheck()
    {
        using var cdp = new FakeCdpServer();
        var result = await Service(cdp.Port, Installed).CheckAsync("not a url");
        Assert.Null(result.TargetOrigin);
        Assert.False(result.TargetTabFound);
        Assert.True(result.RemoteDebuggingRuntimeActive);
    }

    [Fact]
    public void WindowsReadersDoNotThrowAndReturnDefinedValues()
    {
        var policy = new WindowsEdgePolicyReader().ReadRemoteDebuggingPolicy();
        Assert.True(Enum.IsDefined(policy));
        var installation = new WindowsEdgeInstallationLocator().Locate();
        if (installation is not null)
        {
            Assert.EndsWith("msedge.exe", installation.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(installation.ExecutablePath));
        }
    }

    [Fact]
    public void BridgeControllerKeepsLocalCallerFilterAndPostOnlyActions()
    {
        var controller = typeof(ManagedEdgeCdpController);
        Assert.NotNull(controller.GetCustomAttribute<ServiceFilterAttribute>());
        Assert.Equal(typeof(ManagedEdgeLocalCallerFilter), controller.GetCustomAttribute<ServiceFilterAttribute>()!.ServiceType);
        var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => typeof(Task<IActionResult>).IsAssignableFrom(m.ReturnType)).ToArray();
        Assert.Contains(actions, a => a.Name == "Preflight");
        Assert.Contains(actions, a => a.Name == "Launch");
        foreach (var action in actions)
        {
            Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>());
            Assert.Null(action.GetCustomAttribute<HttpGetAttribute>());
        }
    }
}
