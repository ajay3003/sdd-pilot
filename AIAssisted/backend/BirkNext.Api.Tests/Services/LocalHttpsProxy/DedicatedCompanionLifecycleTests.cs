using System.Text.Json;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

/// <summary>
/// Dedicated Edge pairing → permission → active session → heartbeat. Pins the one connection rule both surfaces read,
/// the no-churn rule, stale-vs-live replacement, permission-required state, and reconnect across a backend restart.
/// </summary>
public sealed class DedicatedCompanionLifecycleTests : IDisposable
{
    private const string Extension = "chrome-extension://mmmnnoeaidnkfkacjcogememnmakoalk";
    private const string OtherExtension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    private const string Origin = "https://m2lbdev.bufetat.no";
    private const string Build = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly Clock _time = new();
    private readonly string _state = Path.Combine(Path.GetTempPath(), "birknext-dedicated-" + Guid.NewGuid().ToString("N"));
    private BrowserCompanionService _service;

    public DedicatedCompanionLifecycleTests() => _service = NewService();
    public void Dispose() { if (Directory.Exists(_state)) Directory.Delete(_state, true); }

    private BrowserCompanionService NewService() => new(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()), _time, NullLogger<BrowserCompanionService>.Instance);
    private DedicatedCompanionProvisioner NewProvisioner() => new(_service, _time, NullLogger<DedicatedCompanionProvisioner>.Instance, _state);
    private static readonly LocalHttpsProxyScopeRequest Scope = new("dev", "fingerprint", "Development", Origin + "/app", [new Uri(Origin).Host]);

    private BrowserCompanionPairResult Pair(string launch = "launch", bool permissionPending = false) =>
        _service.PairDedicated(new("dev", "M2LB DEV", "Development", [Origin]), launch, "0.1.0", Extension, permissionPending);
    private DedicatedCompanionReadiness Readiness(string launch = "launch") => _service.DedicatedReadiness("dev", launch, "0.1.0", Build);
    private void Beat(string session, bool withPage = true) =>
        _service.Heartbeat(new(session, "dev", null, null, "0.1.0", withPage ? [new("tab1", Origin, "/", "instance1")] : [], ["element-pick"], Build), Extension);

    // ── no churn ──────────────────────────────────────────────────────────────

    [Fact]
    public void RepeatedBootstrapKeepsOneSessionWhileTheLaunchIsAlive()
    {
        var first = Pair(permissionPending: true);
        for (var i = 0; i < 20; i++)
        {
            _time.Now += TimeSpan.FromSeconds(30); // the extension's bootstrap alarm
            Assert.Equal(first.SessionId, Pair(permissionPending: true).SessionId);
        }
        // Ten minutes, far beyond the three-minute idle window, and still the same identity.
        Assert.Equal(BrowserCompanionState.Disconnected, _service.Status("dev").State);
    }

    [Fact]
    public void ALaunchThatStopsAskingIdlesOutAndOnlyThenGetsANewSession()
    {
        var first = Pair(permissionPending: true);
        _time.Now += BrowserCompanionLimits.SessionIdleLifetime + TimeSpan.FromSeconds(1);
        Assert.NotEqual(first.SessionId, Pair(permissionPending: true).SessionId);
    }

    // ── permission required ───────────────────────────────────────────────────

    [Fact]
    public void PermissionPendingIsAnExplicitStateOnBothSurfaces()
    {
        Pair(permissionPending: true);

        var readiness = Readiness();
        Assert.Equal("PermissionRequired", readiness.State);
        Assert.Equal([Origin], readiness.PermissionOrigins);
        Assert.Contains("Allow access", readiness.Message);
        Assert.False(readiness.Connected);

        var status = _service.Status("dev");
        Assert.Equal(BrowserCompanionState.Disconnected, status.State);
        Assert.True(status.OriginPermissionRequired);
        Assert.Contains(Origin, status.Message);
    }

    [Fact]
    public void FirstHeartbeatAfterTheGrantClearsPermissionRequiredAndConnects()
    {
        var pair = Pair(permissionPending: true);
        Beat(pair.SessionId!);

        Assert.Equal("Connected", Readiness().State);
        Assert.Empty(Readiness().PermissionOrigins);
        var status = _service.Status("dev");
        Assert.Equal(BrowserCompanionState.Connected, status.State);
        Assert.False(status.OriginPermissionRequired);
    }

    [Fact]
    public void WithoutAPermissionReportTheStateIsAwaitingHeartbeatNotPermissionRequired()
    {
        Pair();
        Assert.Equal("AwaitingHeartbeat", Readiness().State);
        Assert.False(_service.Status("dev").OriginPermissionRequired);
    }

    // ── canonical Connected: LastSeenAt vs LastHeartbeatAt ────────────────────

    [Fact]
    public void ActivityWithoutAHeartbeatIsNotConnectedOnEitherSurface()
    {
        Pair(); // LastSeenAt fresh, no heartbeat yet
        AssertAgree(expectedConnected: false);
    }

    [Fact]
    public void FreshHeartbeatIsConnectedOnEitherSurface()
    {
        Beat(Pair().SessionId!);
        AssertAgree(expectedConnected: true);
    }

    [Fact]
    public void StaleHeartbeatWithFreshActivityIsDisconnectedOnEitherSurface()
    {
        var pair = Pair();
        Beat(pair.SessionId!);
        _time.Now += BrowserCompanionLimits.ConnectedWindow + TimeSpan.FromSeconds(1);
        // Bootstrap activity keeps the session alive, but it is not a heartbeat.
        Pair();
        Assert.Equal(pair.SessionId, Pair().SessionId);
        AssertAgree(expectedConnected: false);
    }

    [Fact]
    public void ConnectedWindowBoundaryIsInclusiveAndSharedByBothSurfaces()
    {
        Beat(Pair().SessionId!);
        _time.Now += BrowserCompanionLimits.ConnectedWindow;
        AssertAgree(expectedConnected: true);
        _time.Now += TimeSpan.FromMilliseconds(1);
        AssertAgree(expectedConnected: false);
    }

    [Fact]
    public void BothStaleEventuallyExpires()
    {
        Beat(Pair().SessionId!);
        _time.Now += BrowserCompanionLimits.SessionIdleLifetime + TimeSpan.FromSeconds(1);
        Assert.False(Readiness().Connected);
        Assert.Equal(BrowserCompanionState.Expired, _service.Status("dev").State);
    }

    [Fact]
    public void AMissedBeatDuringWorkerSuspensionDoesNotDisconnect()
    {
        // The MV3 alarm fires every 30 s; a worker that wakes a few seconds late is still inside the 45 s window.
        var pair = Pair();
        Beat(pair.SessionId!);
        _time.Now += TimeSpan.FromSeconds(40);
        AssertAgree(expectedConnected: true);
        Beat(pair.SessionId!);
        _time.Now += TimeSpan.FromSeconds(40);
        AssertAgree(expectedConnected: true);
    }

    private void AssertAgree(bool expectedConnected)
    {
        Assert.Equal(expectedConnected, _service.Status("dev").Connected);
        Assert.Equal(expectedConnected, Readiness().Connected);
    }

    // ── live capture ──────────────────────────────────────────────────────────

    [Fact]
    public void ConnectedWithoutAnApprovedPageIsConnectedWithNoLiveCapture()
    {
        Beat(Pair().SessionId!, withPage: false);
        var status = _service.Status("dev");
        Assert.Equal(BrowserCompanionState.Connected, status.State);
        Assert.Empty(status.Live.LivePages);
        Assert.Null(status.CurrentPageOrigin);
        Assert.True(Readiness().Connected);
        Assert.False(Readiness().ApprovedPageAvailable);
    }

    [Fact]
    public void ConnectedOnAnApprovedPageHasLiveCaptureAndTheCurrentPage()
    {
        Beat(Pair().SessionId!);
        var status = _service.Status("dev");
        Assert.Equal(BrowserCompanionState.Connected, status.State);
        Assert.Single(status.Live.LivePages);
        Assert.Equal(Origin, status.CurrentPageOrigin);
        Assert.True(Readiness().ApprovedPageAvailable);
    }

    [Fact]
    public void AClosedBrowserHasNoCurrentPageEvenBeforeItsLivePagesExpire()
    {
        Beat(Pair().SessionId!);
        _time.Now += BrowserCompanionLimits.ConnectedWindow + TimeSpan.FromSeconds(1); // < LivePageLifetime
        var status = _service.Status("dev");
        Assert.Equal(BrowserCompanionState.Disconnected, status.State);
        Assert.Null(status.CurrentPageOrigin);
        Assert.Null(status.CurrentPagePath);
        Assert.Empty(status.Live.LivePages);
    }

    // ── stale vs live competing pairing ───────────────────────────────────────

    [Fact]
    public void AStaleUnexpiredPairingFromAnotherBrowserIsSuperseded()
    {
        var challenge = _service.StartPairing(new("dev", "DEV", "Development", [Origin]));
        var normal = _service.CompletePairing(new(challenge.PairingCode, "0.1.0"), OtherExtension);
        Beat(normal.SessionId!);
        _time.Now += BrowserCompanionLimits.ConnectedWindow + TimeSpan.FromSeconds(5); // unexpired, not live

        var dedicated = Pair();

        Assert.True(dedicated.Accepted);
        Assert.False(_service.ValidateSession(normal.SessionId!, "dev", OtherExtension).Accepted);
    }

    [Fact]
    public void AStaleNeverHeartbeatingPairingIsSuperseded()
    {
        var challenge = _service.StartPairing(new("dev", "DEV", "Development", [Origin]));
        _service.CompletePairing(new(challenge.PairingCode, "0.1.0"), OtherExtension);
        Assert.True(Pair().Accepted);
    }

    [Fact]
    public void ALiveCompetingPairingIsNeverTakenOver()
    {
        var challenge = _service.StartPairing(new("dev", "DEV", "Development", [Origin]));
        var normal = _service.CompletePairing(new(challenge.PairingCode, "0.1.0"), OtherExtension);
        _service.Heartbeat(new(normal.SessionId!, "dev", null, null, "0.1.0", [], [], null), OtherExtension);

        var dedicated = Pair();

        Assert.False(dedicated.Accepted);
        Assert.Contains("Only one Companion session is supported per environment", dedicated.Message);
        Assert.True(_service.ValidateSession(normal.SessionId!, "dev", OtherExtension).Accepted);
    }

    // ── backend restart ───────────────────────────────────────────────────────

    [Fact]
    public void ABackendRestartedWhileDedicatedEdgeStaysOpenReconnectsWithoutPairing()
    {
        var provisioner = NewProvisioner();
        var token = provisioner.IssueLaunch(Scope, "0.1.0", Build);
        var before = provisioner.Observe(new(token, "0.1.0", Build), Extension);
        Assert.True(before.Accepted);
        Beat(before.SessionId!);

        // Hard restart: every in-memory session and the provisioner's launch are gone; the record on disk is not.
        _service = NewService();
        var restarted = NewProvisioner();
        Assert.False(_service.ValidateSession(before.SessionId!, "dev", Extension).Accepted, "the old session really is gone");

        var after = restarted.Observe(new(token, "0.1.0", Build), Extension);
        Assert.True(after.Accepted);
        Assert.NotEqual(before.SessionId, after.SessionId);
        Beat(after.SessionId!);
        Assert.Equal(BrowserCompanionState.Connected, _service.Status("dev").State);
    }

    [Fact]
    public void TheRestoredLaunchStillRefusesAWrongTokenOrAnotherExtension()
    {
        var token = NewProvisioner().IssueLaunch(Scope, "0.1.0", Build);
        NewProvisioner().Observe(new(token, "0.1.0", Build), Extension);

        _service = NewService();
        Assert.False(NewProvisioner().Observe(new(new string('f', 64), "0.1.0", Build), Extension).Accepted);
        Assert.False(NewProvisioner().Observe(new(token, "0.1.0", Build), OtherExtension).Accepted);
    }

    [Fact]
    public void TheRecordHoldsOnlyTheTokenHash()
    {
        var token = NewProvisioner().IssueLaunch(Scope, "0.1.0", Build);
        var record = File.ReadAllText(Path.Combine(_state, "dedicated-launch-record.json"));
        Assert.DoesNotContain(token, record, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TokenSha256", record);
    }

    [Fact]
    public void RetiringTheLaunchForgetsItAcrossRestarts()
    {
        var provisioner = NewProvisioner();
        var token = provisioner.IssueLaunch(Scope, "0.1.0", Build);
        provisioner.Retire();

        _service = NewService();
        Assert.False(NewProvisioner().Observe(new(token, "0.1.0", Build), Extension).Accepted);
    }

    [Fact]
    public void AnExpiredRecordIsNotAdopted()
    {
        var token = NewProvisioner().IssueLaunch(Scope, "0.1.0", Build);
        _time.Now += DedicatedCompanionProvisioner.LaunchRecordLifetime + TimeSpan.FromMinutes(1);

        _service = NewService();
        Assert.False(NewProvisioner().Observe(new(token, "0.1.0", Build), Extension).Accepted);
        Assert.False(File.Exists(Path.Combine(_state, "dedicated-launch-record.json")));
    }

    [Fact]
    public void PermissionPendingIsCarriedFromTheBootstrapRequest()
    {
        var provisioner = NewProvisioner();
        var token = provisioner.IssueLaunch(Scope, "0.1.0", Build);
        provisioner.Observe(new(token, "0.1.0", Build, PermissionPending: true), Extension);
        Assert.True(_service.Status("dev").OriginPermissionRequired);
    }

    // ── managed manifest: exact origin only ───────────────────────────────────

    [Fact]
    public void TheManagedManifestDeclaresExactlyTheApprovedOrigin()
    {
        var manifest = WriteManifest(Origin);
        Assert.Equal(["http://127.0.0.1/*", "http://localhost/*", "https://m2lbdev.bufetat.no/*"], Hosts(manifest, "host_permissions"));
        // Normal pairing keeps its optional, user-granted permissions untouched.
        Assert.Equal(["https://*/*", "http://*/*"], Hosts(manifest, "optional_host_permissions"));
        Assert.DoesNotContain("<all_urls>", manifest.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com")]
    [InlineData("https://access.mcas.ms")]
    [InlineData(null)]
    public void AuthenticationHostsAndMissingOriginsAreNeverDeclared(string? origin) =>
        Assert.Equal(["http://127.0.0.1/*", "http://localhost/*"], Hosts(WriteManifest(origin), "host_permissions"));

    [Fact]
    public void EachBuildGetsItsOwnManagedDirectoryAndTheSameBuildKeepsIt()
    {
        var a = DedicatedCompanionProvisioner.ManagedDirectoryFor("6BD47CDA5D6E" + new string('0', 52));
        var b = DedicatedCompanionProvisioner.ManagedDirectoryFor("98F3EB76AAAA" + new string('0', 52));
        Assert.EndsWith("build-6bd47cda5d6e", a);
        Assert.NotEqual(a, b);
        Assert.Equal(a, DedicatedCompanionProvisioner.ManagedDirectoryFor("6bd47cda5d6e" + new string('f', 52)));
        DedicatedCompanionProvisioner.ValidateManagedPath(a);
        DedicatedCompanionProvisioner.ValidateManagedPath(b);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("build-6bd47cda5d6")]
    [InlineData("build-6bd47cda5d6eZ")]
    [InlineData("build-../../x")]
    public void OnlyBuildDirectoriesUnderTheManagedRootAreLoadable(string leaf) =>
        Assert.Throws<ArgumentException>(() => DedicatedCompanionProvisioner.ValidateManagedPath(
            Path.Combine(DedicatedCompanionProvisioner.ManagedRoot, leaf)));

    private JsonDocument WriteManifest(string? origin)
    {
        Directory.CreateDirectory(_state);
        var source = Path.Combine(FindRepoDirectory("browser-companion"), "manifest.json");
        File.Copy(source, Path.Combine(_state, "manifest.json"), true);
        DedicatedCompanionProvisioner.WriteDedicatedManifest(_state, origin);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(_state, "manifest.json")));
    }

    private static string[] Hosts(JsonDocument manifest, string property) =>
        manifest.RootElement.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToArray();

    private static string FindRepoDirectory(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, name);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException(name);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
