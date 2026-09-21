using System.Text.Json;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Moq;

namespace BirkNext.Web.Tests.Components;

public sealed class LocalHttpsProxyRuntimeTests
{
    private readonly Mock<ILocalHttpsProxyApiService> _api = new();
    private readonly FrontendAnalysisProfile _profile = new() { Id = "dev", TargetUrl = "https://m2lbdev.bufetat.no/", EnvironmentType = FrontendEnvironmentType.Development, RestBaseUrl = "https://api-dev.bufetat.no/" };
    private readonly LocalHttpsProxyStatus _ready = new()
    {
        RuntimeId = "runtime-only", SessionId = "runtime-only", State = LocalHttpsProxyState.Ready, RuntimeStatus = LocalHttpsProxyRuntimePhase.Running, Port = 8888, LocalIntegrationAvailable = true, EnvironmentAllowed = true, PortAvailable = true,
        AuthenticatedCredentialAvailable = true, CredentialObservedHost = "api-dev.bufetat.no", CredentialFormat = "JWT"
    };

    public LocalHttpsProxyRuntimeTests()
    {
        _profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy;
        _api.Setup(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync(_ready);
        _api.Setup(a => a.StatusAsync(It.IsAny<LocalHttpsProxySessionRequest>())).ReturnsAsync(_ready);
        _api.Setup(a => a.GetRuntimeAsync()).ReturnsAsync(_ready);
        _api.Setup(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>())).ReturnsAsync(new LocalHttpsProxyStatus { State = LocalHttpsProxyState.Stopped });
        _api.Setup(a => a.ExecuteRestAsync(It.IsAny<AuthenticatedRestRequest>())).ReturnsAsync(new AuthenticatedApiExecutionResult { StatusCode = 200, ContentType = "application/json", Outcome = "HTTP 200 application/json; 42 bytes; response body not captured." });
    }

    [Fact]
    public async Task FrontendDisposalMustNotSendStop()
    {
        var runtime = new LocalHttpsProxyRuntime(_api.Object);
        await runtime.StartAsync(_profile);
        await runtime.DisposeAsync();
        _api.Verify(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.Never);
    }

    [Fact]
    public async Task RecreatedFrontendRecoversSameSessionWithoutStartOrStop()
    {
        _api.Setup(a => a.CheckCompatibilityAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync(_ready);
        await using var runtime = new LocalHttpsProxyRuntime(_api.Object);
        await runtime.SynchronizeAsync(_profile);
        Assert.Equal(_ready.SessionId, runtime.Status.SessionId);
        Assert.Equal(_ready.Port, runtime.Status.Port);
        Assert.True(runtime.SessionActive);
        _api.Verify(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>()), Times.Never);
        _api.Verify(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.Never);
    }

    [Fact]
    public async Task StartAndStopNeverChangeProfileSerializationAndStopWipesAvailability()
    {
        await using var runtime = new LocalHttpsProxyRuntime(_api.Object);
        var json = JsonSerializer.Serialize(_profile);
        await runtime.StartAsync(_profile);
        Assert.True(runtime.For(_profile).AuthenticatedCredentialAvailable);
        Assert.True(runtime.SessionActive);
        Assert.Equal(json, JsonSerializer.Serialize(_profile));
        await runtime.StopAsync();
        Assert.False(runtime.For(_profile).AuthenticatedCredentialAvailable);
        Assert.Equal(LocalHttpsProxyState.Stopped, runtime.Status.State);
        Assert.False(runtime.SessionActive);
        Assert.Equal(json, JsonSerializer.Serialize(_profile));
        _api.Verify(a => a.StopAsync(It.Is<LocalHttpsProxySessionRequest>(r => r.SessionId == "runtime-only" && r.ProfileId == "dev")), Times.Once);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("url")]
    [InlineData("rest")]
    [InlineData("graphql")]
    [InlineData("auth")]
    [InlineData("method")]
    [InlineData("environment")]
    public async Task RelevantConfigurationChangeHidesCredentialButDoesNotStop(string change)
    {
        await using var runtime = new LocalHttpsProxyRuntime(_api.Object);
        await runtime.StartAsync(_profile);
        Assert.True(runtime.For(_profile).AuthenticatedCredentialAvailable);
        switch (change)
        {
            case "profile": _profile.Id = "qa"; break;
            case "url": _profile.TargetUrl = "https://other.bufetat.no/"; break;
            case "rest": _profile.RestBaseUrl = "https://api-qa.bufetat.no/"; break;
            case "graphql": _profile.GraphQlEndpoint = "https://graphql-dev.bufetat.no/graphql"; break;
            case "auth": _profile.Authentication.ExpectedTenant = Guid.NewGuid().ToString(); break;
            case "method": _profile.Authentication.AuthenticatedTestingMethod = AuthenticatedTestingMethod.ManagedEdgeCdp; break;
            case "environment": _profile.EnvironmentType = FrontendEnvironmentType.Production; break;
        }
        var stale = runtime.For(_profile);
        Assert.Equal(LocalHttpsProxyState.Stale, stale.State);
        Assert.False(stale.AuthenticatedCredentialAvailable);
        Assert.False(stale.RestAvailable);
        await runtime.SynchronizeAsync(_profile);
        _api.Verify(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.Never);
        Assert.True(runtime.SessionActive);
    }

    [Fact]
    public async Task TransportFailureRevokesAvailability()
    {
        await using var runtime = new LocalHttpsProxyRuntime(_api.Object);
        await runtime.StartAsync(_profile);
        _api.Setup(a => a.GetRuntimeAsync()).ThrowsAsync(new HttpRequestException("secret-in-message"));
        await runtime.RefreshAsync();
        Assert.NotNull(runtime.Status.FailureReason);
        Assert.False(runtime.Status.AuthenticatedCredentialAvailable);
        Assert.DoesNotContain("secret-in-message", runtime.Status.Evidence);
    }

    [Fact]
    public async Task AuthenticatedChecksRunOnlyWithASessionAndKeepOnlySanitizedOutcomes()
    {
        await using var runtime = new LocalHttpsProxyRuntime(_api.Object);
        await runtime.ExecuteRestCheckAsync("https://api-dev.bufetat.no/health");
        _api.Verify(a => a.ExecuteRestAsync(It.IsAny<AuthenticatedRestRequest>()), Times.Never);
        await runtime.StartAsync(_profile);
        await runtime.ExecuteRestCheckAsync("https://api-dev.bufetat.no/health");
        _api.Verify(a => a.ExecuteRestAsync(It.Is<AuthenticatedRestRequest>(r => r.Method == "GET" && r.SessionId == "runtime-only")), Times.Once);
        Assert.Equal(200, runtime.LastRestResult!.StatusCode);
        Assert.DoesNotContain("eyJ", JsonSerializer.Serialize(runtime.LastRestResult));
        await runtime.StopAsync();
        Assert.Null(runtime.LastRestResult);
    }

    [Fact]
    public async Task TwoClientsObserveOneRuntimeAndStopFromEitherIsVisible()
    {
        await using var first = new LocalHttpsProxyRuntime(_api.Object);
        await using var second = new LocalHttpsProxyRuntime(_api.Object);
        await first.StartAsync(_profile);
        await second.SynchronizeAsync(_profile);
        Assert.Equal(first.Status.RuntimeId, second.Status.RuntimeId);
        Assert.Equal(first.Status.Port, second.Status.Port);
        await second.StartAsync(_profile);
        _api.Verify(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.Never);
        await second.StopAsync();
        _api.Setup(a => a.GetRuntimeAsync()).ReturnsAsync(new LocalHttpsProxyStatus { State = LocalHttpsProxyState.Stopped });
        await first.RefreshAsync();
        Assert.False(first.SessionActive);
        Assert.Equal(LocalHttpsProxyState.Stopped, first.Status.State);
    }

    [Fact]
    public async Task DisposalDuringStartDoesNotSendAbandonedSessionStop()
    {
        var pending = new TaskCompletionSource<LocalHttpsProxyStatus>();
        _api.Setup(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).Returns(pending.Task);
        var first = new LocalHttpsProxyRuntime(_api.Object);
        var start = first.StartAsync(_profile);
        await first.DisposeAsync();
        pending.SetResult(_ready);
        await start;
        await using var recreated = new LocalHttpsProxyRuntime(_api.Object);
        await recreated.SynchronizeAsync(_profile);
        Assert.Equal(_ready.RuntimeId, recreated.Status.RuntimeId);
        _api.Verify(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>()), Times.Once);
        _api.Verify(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.Never);
    }

    [Fact]
    public async Task DisposalDuringStopDoesNotRestartOrRepeatStop()
    {
        var pending = new TaskCompletionSource<LocalHttpsProxyStatus>();
        var first = new LocalHttpsProxyRuntime(_api.Object);
        await first.StartAsync(_profile);
        _api.Setup(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>())).Returns(pending.Task);
        var stop = first.StopAsync();
        Assert.Equal(LocalHttpsProxyRuntimePhase.Stopping, first.Status.RuntimeStatus);
        await first.DisposeAsync();
        var stopped = new LocalHttpsProxyStatus { State = LocalHttpsProxyState.Stopped };
        pending.SetResult(stopped);
        await stop;
        _api.Setup(a => a.GetRuntimeAsync()).ReturnsAsync(stopped);
        _api.Setup(a => a.CheckCompatibilityAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).ReturnsAsync(stopped);
        await using var recreated = new LocalHttpsProxyRuntime(_api.Object);
        await recreated.SynchronizeAsync(_profile);
        Assert.Equal(LocalHttpsProxyState.Stopped, recreated.Status.State);
        _api.Verify(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>()), Times.Once);
        _api.Verify(a => a.StopAsync(It.IsAny<LocalHttpsProxySessionRequest>()), Times.Once);
    }

    [Fact]
    public async Task DoubleClickDuringStartSendsOneCommand()
    {
        var pending = new TaskCompletionSource<LocalHttpsProxyStatus>();
        _api.Setup(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>())).Returns(pending.Task);
        await using var runtime = new LocalHttpsProxyRuntime(_api.Object);
        var first = runtime.StartAsync(_profile);
        await runtime.StartAsync(_profile);
        pending.SetResult(_ready);
        await first;
        _api.Verify(a => a.StartAsync(It.IsAny<LocalHttpsProxyScopeRequest>()), Times.Once);
    }

    [Fact]
    public void StatusContractExposesNoCredentialShapedMembers()
    {
        foreach (var type in new[] { typeof(LocalHttpsProxyStatus), typeof(ProxyCertificateStatus), typeof(AuthenticatedApiExecutionResult), typeof(LocalHttpsProxyScopeRequest) })
            Assert.DoesNotContain(type.GetProperties().Select(p => p.Name), p =>
                p.Contains("Token", StringComparison.OrdinalIgnoreCase) || p.Contains("Bearer", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("Cookie", StringComparison.OrdinalIgnoreCase) || p.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }
}
