using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;
using Moq;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

/// <summary>
/// The gateway is the only authenticated surface reviews use. It resolves a capability matrix from the saved method + proxy context and
/// executes approved authenticated API checks, returning typed outcomes (never a silent public downgrade) and never a token.
/// </summary>
public sealed class AuthenticatedReviewGatewayTests
{
    private const string Fp = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Mock<IAuthenticatedApiExecutionService> _exec = new();
    private readonly Mock<ILocalHttpsProxyStatusQuery> _status = new();

    private AuthenticatedReviewGateway Gateway() => new(_exec.Object, _status.Object);
    private static AuthenticatedReviewIdentity Proxy() => new(AuthenticatedTestingMethod.LocalHttpsProxy, "dev", Fp);

    private static LocalHttpsProxyStatus Available => new()
    {
        SessionId = "s", State = LocalHttpsProxyState.Ready, AuthenticatedCredentialAvailable = true,
        CredentialObservedHost = "api.example.test", CredentialExpiresAt = DateTimeOffset.UtcNow.AddMinutes(20), CredentialFormat = "JWT"
    };

    [Fact]
    public void ResolveProxyAvailableGrantsApiRestGraphQlButNeverDom()
    {
        _status.Setup(s => s.StatusForProfile("dev", Fp)).Returns(Available);
        var caps = Gateway().Resolve(Proxy());
        Assert.Equal(AuthenticatedApiContextStatus.Available, caps.ContextStatus);
        Assert.True(caps.PublicApi);
        Assert.True(caps.AuthenticatedApi);
        Assert.True(caps.AuthenticatedRest);
        Assert.True(caps.AuthenticatedGraphQlQuery);
        Assert.False(caps.AuthenticatedBrowserDom);
        Assert.False(caps.AuthenticatedBrowserRuntime);
        Assert.Equal("api.example.test", caps.ObservedHost);
        Assert.NotNull(caps.ExpiresAt);
    }

    [Fact]
    public void ResolveProxyExpiredMarksExpiredAndDisablesAuthenticatedApi()
    {
        _status.Setup(s => s.StatusForProfile("dev", Fp)).Returns(Available with { AuthenticatedCredentialAvailable = false, CredentialExpired = true });
        var caps = Gateway().Resolve(Proxy());
        Assert.Equal(AuthenticatedApiContextStatus.Expired, caps.ContextStatus);
        Assert.True(caps.PublicApi);
        Assert.False(caps.AuthenticatedApi);
        Assert.False(caps.AuthenticatedRest);
        Assert.False(caps.AuthenticatedGraphQlQuery);
        Assert.Contains("expired", caps.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveProxyNotStartedWaitsForTraffic()
    {
        _status.Setup(s => s.StatusForProfile("dev", Fp)).Returns((LocalHttpsProxyStatus?)null);
        var caps = Gateway().Resolve(Proxy());
        Assert.Equal(AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic, caps.ContextStatus);
        Assert.False(caps.AuthenticatedApi);
        Assert.True(caps.PublicApi);
        Assert.Contains("Start the local proxy", caps.Reason);
    }

    [Theory]
    [InlineData(AuthenticatedTestingMethod.ManagedEdgeCdp)]
    [InlineData(AuthenticatedTestingMethod.ManualOnly)]
    public void ResolveNonProxyMethodsNeverGrantAuthenticatedApiForReviews(AuthenticatedTestingMethod method)
    {
        var caps = Gateway().Resolve(new AuthenticatedReviewIdentity(method, "dev", Fp));
        Assert.Equal(AuthenticatedApiContextStatus.NotApplicable, caps.ContextStatus);
        Assert.True(caps.PublicApi);
        Assert.False(caps.AuthenticatedApi);
        Assert.False(caps.AuthenticatedRest);
        Assert.False(caps.AuthenticatedGraphQlQuery);
        Assert.False(caps.AuthenticatedBrowserDom);
        _status.Verify(s => s.StatusForProfile(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteRestReturnsExecutedResultWithProxyProvenance()
    {
        _exec.Setup(e => e.ExecuteRestForProfileAsync("dev", Fp, "GET", "https://api.example.test/health", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedApiExecutionResult { StatusCode = 200, ContentType = "application/json", ElapsedMs = 12, Outcome = "HTTP 200 application/json; 40 bytes; response body not captured." });
        var outcome = await Gateway().ExecuteRestAsync(Proxy(), "GET", "https://api.example.test/health");
        Assert.True(outcome.Executed);
        Assert.Equal(AuthenticatedExecutionStatus.Executed, outcome.Status);
        Assert.Equal(ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy, outcome.Mode);
        Assert.Equal(200, outcome.Result!.StatusCode);
    }

    [Fact]
    public async Task ExecuteRestMapsExpiredContextToTypedOutcomeNeverSilentDowngrade()
    {
        _exec.Setup(e => e.ExecuteRestForProfileAsync("dev", Fp, "GET", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticatedContextUnavailableException(AuthenticatedExecutionStatus.Expired, "expired"));
        var outcome = await Gateway().ExecuteRestAsync(Proxy(), "GET", "https://api.example.test/health");
        Assert.False(outcome.Executed);
        Assert.Equal(AuthenticatedExecutionStatus.Expired, outcome.Status);
        Assert.Equal(ReviewExecutionMode.AuthenticatedUnavailable, outcome.Mode);
        Assert.Contains("expired", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteRestMapsOutOfScopeAndNoContext()
    {
        _exec.Setup(e => e.ExecuteRestForProfileAsync("dev", Fp, "GET", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AuthenticatedContextUnavailableException(AuthenticatedExecutionStatus.OutOfScope, "out of scope"));
        var outcome = await Gateway().ExecuteRestAsync(Proxy(), "GET", "https://other.example.test/x");
        Assert.Equal(AuthenticatedExecutionStatus.OutOfScope, outcome.Status);

        var noProfile = await Gateway().ExecuteRestAsync(new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.LocalHttpsProxy, null, null), "GET", "https://api.example.test/x");
        Assert.Equal(AuthenticatedExecutionStatus.NoContext, noProfile.Status);
    }

    [Fact]
    public async Task ExecuteRejectsUnsafeMethodAndNonProxyMethodWithoutCallingExecution()
    {
        _exec.Setup(e => e.ExecuteRestForProfileAsync("dev", Fp, "POST", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Only GET, HEAD and OPTIONS are allowed"));
        var unsafeOutcome = await Gateway().ExecuteRestAsync(Proxy(), "POST", "https://api.example.test/x");
        Assert.Equal(AuthenticatedExecutionStatus.Rejected, unsafeOutcome.Status);

        var cdp = await Gateway().ExecuteRestAsync(new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.ManagedEdgeCdp, "dev", Fp), "GET", "https://api.example.test/x");
        Assert.Equal(AuthenticatedExecutionStatus.MethodNotProxy, cdp.Status);
        var manual = await Gateway().ExecuteRestAsync(new AuthenticatedReviewIdentity(AuthenticatedTestingMethod.ManualOnly, "dev", Fp), "GET", "https://api.example.test/x");
        Assert.Equal(AuthenticatedExecutionStatus.MethodNotProxy, manual.Status);
        Assert.Equal(ReviewExecutionMode.ManualNotExecuted, manual.Mode);
        _exec.Verify(e => e.ExecuteRestForProfileAsync("dev", Fp, "GET", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteGraphQlDelegatesToQueryExecution()
    {
        _exec.Setup(e => e.ExecuteGraphQlQueryForProfileAsync("dev", Fp, "https://api.example.test/graphql", "query { __typename }", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedApiExecutionResult { StatusCode = 200, GraphQlHasData = true, GraphQlErrorCount = 0, Outcome = "HTTP 200; GraphQL data returned without errors." });
        var outcome = await Gateway().ExecuteGraphQlQueryAsync(Proxy(), "https://api.example.test/graphql", "query { __typename }");
        Assert.True(outcome.Executed);
        Assert.Equal(ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy, outcome.Mode);
    }
}
