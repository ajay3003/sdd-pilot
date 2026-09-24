using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

public sealed class DedicatedCompanionTests
{
    private const string Extension = "chrome-extension://abcdefghijklmnopabcdefghijklmnop";
    private const string Origin = "https://m2lbdev.bufetat.no";
    private const string Build = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly Clock _time = new();
    private readonly BrowserCompanionService _service;
    public DedicatedCompanionTests() => _service = new(new BrowserCompanionEvidenceSanitizer(new BrowserEvidenceSanitizer()), _time, NullLogger<BrowserCompanionService>.Instance);
    private BrowserCompanionPairResult Pair(string launch = "launch") => _service.PairDedicated(new("dev", "M2LB DEV", "Development", [Origin]), launch, "0.1.0", Extension);
    private DedicatedCompanionReadiness Status() => _service.DedicatedReadiness("dev", "launch", "0.1.0", Build);
    private void Beat(string session, string build = Build, string origin = Origin, List<string>? capabilities = null) =>
        _service.Heartbeat(new(session, "dev", null, null, "0.1.0", [new("tab1", origin, "/", "instance1")], capabilities ?? ["element-pick"], build), Extension);

    [Fact] public void LaunchKeepsProxyAndProfileAndUsesOneExtensionArgument()
    {
        var path = DedicatedCompanionProvisioner.ManagedDirectory;
        var args = LocalHttpsProxyService.BuildEdgeArguments(8888, LocalHttpsProxyService.DefaultEdgeProfileDirectory(), Origin, path);
        Assert.Contains("--proxy-server=127.0.0.1:8888", args);
        Assert.Contains("--user-data-dir=" + LocalHttpsProxyService.DefaultEdgeProfileDirectory(), args);
        Assert.Contains("--load-extension=" + path, args);
        Assert.DoesNotContain(args, a => a.StartsWith("--disable-extensions-except"));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("C:\\arbitrary extension")]
    [InlineData("C:\\extension,other")]
    [InlineData("C:\\extension\" --flag")]
    public void UncontrolledPathsAreRejected(string path) => Assert.Throws<ArgumentException>(() =>
        LocalHttpsProxyService.BuildEdgeArguments(8888, LocalHttpsProxyService.DefaultEdgeProfileDirectory(), Origin, path));

    [Fact] public void AbsentBuildCanLaunchProxyOnly() => Assert.DoesNotContain(
        LocalHttpsProxyService.BuildEdgeArguments(8888, LocalHttpsProxyService.DefaultEdgeProfileDirectory(), Origin), a => a.StartsWith("--load-extension"));

    [Fact] public void LaunchRequestCannotSupplyExtensionPath() => Assert.DoesNotContain(
        typeof(LocalHttpsProxyEdgeLaunchRequest).GetProperties(), p => p.Name.Contains("Path") || p.Name.Contains("Directory"));

    [Fact] public void PairingAloneIsNotReadiness()
    {
        Pair();
        Assert.False(Status().Connected);
        Assert.False(Status().BrowserDiscoveryReady);
    }

    [Fact] public void NormalSessionIsNotDedicatedAndIsNotReplaced()
    {
        var challenge = _service.StartPairing(new("dev", "DEV", "Development", [Origin]));
        var normal = _service.CompletePairing(new(challenge.PairingCode, "0.1.0"), Extension);
        Beat(normal.SessionId!);
        Assert.False(Pair().Accepted);
        Assert.False(Status().Connected);
        Assert.True(_service.ValidateSession(normal.SessionId!, "dev", Extension).Accepted);
    }

    [Fact] public void DedicatedHeartbeatUsesExistingPageAndAutomationChannel()
    {
        var pair = Pair(); Beat(pair.SessionId!);
        Assert.True(Status().Connected);
        Assert.True(Status().VersionCompatible);
        Assert.True(Status().BrowserDiscoveryReady);
        Assert.True(Status().ElementPickAvailable);
        Assert.Single(_service.Status("dev").Live.LivePages);
    }

    [Fact] public void WrongBuildCannotBeReady()
    {
        var pair = Pair(); Beat(pair.SessionId!, "old-build");
        Assert.True(Status().Connected);
        Assert.False(Status().VersionCompatible);
        Assert.False(Status().BrowserDiscoveryReady);
        Assert.False(Status().ElementPickAvailable);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com")]
    [InlineData("https://access.mcas.ms")]
    [InlineData("https://unapproved.test")]
    public void InfrastructureAndUnapprovedPagesDoNotMakeBrowserReady(string origin)
    {
        var pair = Pair(); Beat(pair.SessionId!, origin: origin);
        Assert.True(Status().Connected);
        Assert.False(Status().ApprovedPageAvailable);
    }

    [Fact] public void DisconnectClearsLiveReadiness()
    {
        var pair = Pair(); Beat(pair.SessionId!);
        _time.Now += TimeSpan.FromMinutes(5);
        Assert.False(Status().Connected);
        Assert.False(Status().ElementPickAvailable);
    }

    [Fact] public void RestartRejectsOldSessionAndRequiresNewHeartbeat()
    {
        var old = Pair(); Beat(old.SessionId!);
        _service.RetireDedicated("dev", "launch");
        var next = Pair();
        Assert.NotEqual(old.SessionId, next.SessionId);
        Assert.False(Status().Connected);
        Assert.False(_service.ValidateSession(old.SessionId!, "dev", Extension).Accepted);
        Beat(next.SessionId!);
        Assert.True(Status().BrowserDiscoveryReady);
    }

    [Fact] public void MissingCapabilityCannotOfferPicker()
    {
        var pair = Pair(); Beat(pair.SessionId!, capabilities: []);
        Assert.True(Status().BrowserDiscoveryReady);
        Assert.False(Status().ElementPickAvailable);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
