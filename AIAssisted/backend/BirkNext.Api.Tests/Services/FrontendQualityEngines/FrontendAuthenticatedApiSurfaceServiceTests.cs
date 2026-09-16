using System.Net;
using BirkNext.Api.Services.FrontendQualityEngines;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;
using FluentAssertions;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services.FrontendQualityEngines;

/// <summary>
/// The Frontend Quality Review's authenticated API-surface probes go through the gateway only: approved GET / GraphQL query,
/// sanitized results, typed non-execution when the context is missing. No token, cookie or raw header ever reaches the review.
/// </summary>
public sealed class FrontendAuthenticatedApiSurfaceServiceTests
{
    private static readonly AuthenticatedReviewIdentity Identity = new(AuthenticatedTestingMethod.LocalHttpsProxy, "dev", new string('A', 64));

    private static AuthenticatedReviewCapabilities Available => new()
    {
        Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.Available,
        AuthenticatedApi = true, AuthenticatedRest = true, AuthenticatedGraphQlQuery = true, Reason = "Authenticated via Local HTTPS Proxy (memory only).",
    };

    private static AuthenticatedReviewExecutionOutcome Executed(int status, double ms, IReadOnlyDictionary<string, string>? headers = null, bool graphQl = false) => new()
    {
        Status = AuthenticatedExecutionStatus.Executed, Mode = ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy,
        Result = new AuthenticatedApiExecutionResult
        {
            StatusCode = status, ContentType = "application/json", ContentLength = 42, ElapsedMs = ms,
            GraphQlHasData = graphQl ? true : null, GraphQlErrorCount = graphQl ? 0 : null,
            Outcome = $"HTTP {status}", SecurityHeaders = headers ?? new Dictionary<string, string>(),
        },
        Message = $"HTTP {status}",
    };

    [Fact]
    public async Task ContextAvailable_ProbesConfiguredEndpointsThroughGatewayOnly()
    {
        var gateway = new Mock<IAuthenticatedReviewGateway>(MockBehavior.Strict);
        gateway.Setup(g => g.Resolve(Identity)).Returns(Available);
        gateway.Setup(g => g.ExecuteRestAsync(Identity, "GET", "https://api-dev.example.test/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Executed(200, 120, new Dictionary<string, string> { ["strict-transport-security"] = "max-age=31536000" }));
        gateway.Setup(g => g.ExecuteRestAsync(Identity, "GET", "https://api-dev.example.test/health?token=SHOULD-NOT-ECHO", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Executed(401, 80));
        gateway.Setup(g => g.ExecuteGraphQlQueryAsync(Identity, "https://api-dev.example.test/graphql", FrontendAuthenticatedApiSurfaceService.GraphQlProbe, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Executed(200, 210, graphQl: true));
        var service = new FrontendAuthenticatedApiSurfaceService(gateway.Object);

        var result = await service.ProbeAsync(new FrontendAuthenticatedApiSurfaceRequest(Identity, "https://api-dev.example.test/", "https://api-dev.example.test/health?token=SHOULD-NOT-ECHO", "https://api-dev.example.test/graphql"));

        result.ContextAvailable.Should().BeTrue();
        result.Checks.Should().HaveCount(3);
        result.ExecutedCount.Should().Be(3);
        result.Checks[0].Should().Match<FrontendAuthenticatedApiCheck>(c => c.Label == "REST API" && c.StatusCode == 200 && c.ElapsedMs == 120 && c.SecurityHeaders.ContainsKey("strict-transport-security"));
        result.Checks[1].Should().Match<FrontendAuthenticatedApiCheck>(c => c.Label == "Health" && c.AuthenticationRejected && c.Url == "https://api-dev.example.test/health");
        result.Checks[2].Should().Match<FrontendAuthenticatedApiCheck>(c => c.Label == "GraphQL" && c.GraphQlHasData == true && c.Mode == ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy);
        System.Text.Json.JsonSerializer.Serialize(result).Should().NotContainAny("SHOULD-NOT-ECHO", "Authorization", "Bearer ", "Cookie");
        gateway.VerifyAll();
    }

    [Fact]
    public async Task ContextMissing_NothingExecuted_TypedReason()
    {
        var gateway = new Mock<IAuthenticatedReviewGateway>(MockBehavior.Strict);
        gateway.Setup(g => g.Resolve(Identity)).Returns(new AuthenticatedReviewCapabilities
        {
            Method = AuthenticatedTestingMethod.LocalHttpsProxy, ContextStatus = AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic, Reason = "Start the local proxy, sign in to the target application in the proxy-configured browser, and perform an authenticated action.",
        });
        var service = new FrontendAuthenticatedApiSurfaceService(gateway.Object);

        var result = await service.ProbeAsync(new FrontendAuthenticatedApiSurfaceRequest(Identity, "https://api-dev.example.test/", null, null));

        result.ContextAvailable.Should().BeFalse();
        result.Checks.Should().BeEmpty();
        result.NotExecutedReason.Should().Contain("Start the local proxy");
        gateway.Verify(g => g.ExecuteRestAsync(It.IsAny<AuthenticatedReviewIdentity>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoHttpsEndpointsConfigured_ContextAvailableButNothingToProbe()
    {
        var gateway = new Mock<IAuthenticatedReviewGateway>(MockBehavior.Strict);
        gateway.Setup(g => g.Resolve(Identity)).Returns(Available);
        var service = new FrontendAuthenticatedApiSurfaceService(gateway.Object);

        var result = await service.ProbeAsync(new FrontendAuthenticatedApiSurfaceRequest(Identity, "http://insecure.example.test/", null, ""));

        result.ContextAvailable.Should().BeTrue();
        result.Checks.Should().BeEmpty();
        result.NotExecutedReason.Should().Contain("No HTTPS REST base");
    }

    [Fact]
    public async Task ExpiredContextDuringProbe_IsTypedNotSilentlyPublic()
    {
        var gateway = new Mock<IAuthenticatedReviewGateway>(MockBehavior.Strict);
        gateway.Setup(g => g.Resolve(Identity)).Returns(Available);
        gateway.Setup(g => g.ExecuteRestAsync(Identity, "GET", "https://api-dev.example.test/", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticatedReviewExecutionOutcome { Status = AuthenticatedExecutionStatus.Expired, Mode = ReviewExecutionMode.AuthenticatedUnavailable, Message = "Authenticated API session expired." });
        var service = new FrontendAuthenticatedApiSurfaceService(gateway.Object);

        var result = await service.ProbeAsync(new FrontendAuthenticatedApiSurfaceRequest(Identity, "https://api-dev.example.test/", null, null));

        result.Checks.Should().ContainSingle().Which.Should().Match<FrontendAuthenticatedApiCheck>(c => !c.Executed && c.Status == AuthenticatedExecutionStatus.Expired && c.Mode == ReviewExecutionMode.AuthenticatedUnavailable);
        result.ExecutedCount.Should().Be(0);
    }

    [Fact]
    public void CollectSecurityHeaders_AllowListOnly_NeverCookiesOrAuthHeaders()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        response.Headers.TryAddWithoutValidation("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        response.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff");
        response.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "*");
        response.Headers.TryAddWithoutValidation("Set-Cookie", "session=SECRET-COOKIE; HttpOnly");
        response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer realm=\"api\"");
        response.Headers.TryAddWithoutValidation("X-Request-Id", "abc");
        response.Headers.TryAddWithoutValidation("Content-Security-Policy", new string('x', 2000));

        var headers = AuthenticatedApiExecutionService.CollectSecurityHeaders(response);

        headers.Keys.Should().BeEquivalentTo(["strict-transport-security", "x-content-type-options", "access-control-allow-origin", "content-security-policy"]);
        headers["content-security-policy"].Should().HaveLength(512, "values are capped");
        System.Text.Json.JsonSerializer.Serialize(headers).Should().NotContainAny("SECRET-COOKIE", "WWW-Authenticate", "Bearer", "abc");
    }
}
