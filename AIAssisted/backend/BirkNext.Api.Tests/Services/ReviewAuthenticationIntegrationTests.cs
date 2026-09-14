using System.Net;
using System.Text.Json;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// API and Integration Quality Reviews consume the authenticated context only through <see cref="IAuthenticatedReviewGateway"/>, embed
/// its capabilities and provenance in the report, execute authenticated checks only when the context is available, and never expose a token.
/// </summary>
public sealed class ReviewAuthenticationIntegrationTests
{
    private const string Fp = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static HttpClient PublicClient() => new(new StubHandler()) { BaseAddress = new Uri("https://unused.example.test/") };

    private static ApiQualityReviewService ApiService(IAuthenticatedReviewGateway gateway) =>
        new(PublicClient(), NullLogger<ApiQualityReviewService>.Instance, gateway);

    private static IntegrationQualityReviewService IntegrationService(IAuthenticatedReviewGateway gateway) =>
        new(PublicClient(), NullLogger<IntegrationQualityReviewService>.Instance, gateway);

    [Fact]
    public async Task ApiReviewWithProxyContextRecordsAuthenticatedProvenanceAndNoToken()
    {
        var gateway = new FakeGateway(available: true);
        var report = await ApiService(gateway).AnalyzeAsync(new ApiQualityReviewRequest
        {
            EnvironmentName = "Dev", RestBaseUrl = "https://api.example.test/", HealthEndpoint = "https://api.example.test/health",
            GraphQlEndpoint = "https://api.example.test/graphql",
            AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy, ProfileId = "dev", ContextFingerprint = Fp
        });
        Assert.NotNull(report.Authentication);
        Assert.True(report.Authentication!.Capabilities.AuthenticatedRest);
        Assert.True(report.Authentication.Capabilities.AuthenticatedGraphQlQuery);
        Assert.False(report.Authentication.Capabilities.AuthenticatedBrowserDom);
        Assert.NotEmpty(report.Authentication.Checks);
        Assert.All(report.Authentication.Checks, c => Assert.Equal(ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy, c.ExecutionMode));
        Assert.Contains(report.Authentication.Checks, c => c.Label == "REST API");
        Assert.Contains(report.Authentication.Checks, c => c.Label == "GraphQL");
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("eyJ", json);
        Assert.DoesNotContain("Bearer", json);
        Assert.DoesNotContain("Authorization", json);
    }

    [Fact]
    public async Task ApiReviewWithCdpMethodDoesNotExecuteAuthenticatedChecks()
    {
        var gateway = new FakeGateway(available: false);
        var report = await ApiService(gateway).AnalyzeAsync(new ApiQualityReviewRequest
        {
            EnvironmentName = "Dev", RestBaseUrl = "https://api.example.test/",
            AuthenticatedTestingMethod = AuthenticatedTestingMethod.ManagedEdgeCdp, ProfileId = "dev", ContextFingerprint = Fp
        });
        Assert.NotNull(report.Authentication);
        Assert.False(report.Authentication!.Capabilities.AuthenticatedRest);
        Assert.Empty(report.Authentication.Checks);
        Assert.Equal(0, gateway.RestCalls);
    }

    [Fact]
    public async Task IntegrationReviewWithProxyContextRecordsAuthenticatedHealthChecks()
    {
        var gateway = new FakeGateway(available: true);
        var request = new IntegrationQualityRequest
        {
            EnvironmentName = "Dev", AuthenticatedTestingMethod = AuthenticatedTestingMethod.LocalHttpsProxy, ProfileId = "dev", ContextFingerprint = Fp,
            Integrations = [new IntegrationConfigDto { Id = "i1", Name = "Orders", Type = IntegrationType.REST, Enabled = true, HealthUrl = "https://api.example.test/health" }]
        };
        var report = await IntegrationService(gateway).AnalyzeAsync(request);
        Assert.NotNull(report.Authentication);
        Assert.True(report.Authentication!.Capabilities.AuthenticatedRest);
        Assert.Contains(report.Authentication.Checks, c => c.IntegrationId == "i1" && c.Label == "Health" && c.ExecutionMode == ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy);
        Assert.DoesNotContain("eyJ", JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task IntegrationReviewWithoutContextRecordsNoAuthenticatedChecks()
    {
        var gateway = new FakeGateway(available: false);
        var request = new IntegrationQualityRequest
        {
            EnvironmentName = "Dev", AuthenticatedTestingMethod = AuthenticatedTestingMethod.ManualOnly,
            Integrations = [new IntegrationConfigDto { Id = "i1", Name = "Orders", Type = IntegrationType.REST, Enabled = true, HealthUrl = "https://api.example.test/health" }]
        };
        var report = await IntegrationService(gateway).AnalyzeAsync(request);
        Assert.NotNull(report.Authentication);
        Assert.False(report.Authentication!.Capabilities.AuthenticatedRest);
        Assert.Empty(report.Authentication.Checks);
    }

    private sealed class FakeGateway(bool available) : IAuthenticatedReviewGateway
    {
        public int RestCalls { get; private set; }

        public AuthenticatedReviewCapabilities Resolve(AuthenticatedReviewIdentity identity) =>
            available && identity.Method == AuthenticatedTestingMethod.LocalHttpsProxy
                ? new() { Method = identity.Method, ContextStatus = AuthenticatedApiContextStatus.Available, PublicApi = true, AuthenticatedApi = true, AuthenticatedRest = true, AuthenticatedGraphQlQuery = true, ObservedHost = "api.example.test", Reason = "Authenticated via Local HTTPS Proxy" }
                : new() { Method = identity.Method, ContextStatus = AuthenticatedApiContextStatus.NotApplicable, PublicApi = true, Reason = "Public only" };

        public Task<AuthenticatedReviewExecutionOutcome> ExecuteRestAsync(AuthenticatedReviewIdentity identity, string httpMethod, string url, CancellationToken cancellationToken = default)
        {
            RestCalls++;
            return Task.FromResult(new AuthenticatedReviewExecutionOutcome
            {
                Status = AuthenticatedExecutionStatus.Executed, Mode = ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy,
                Result = new AuthenticatedApiExecutionResult { StatusCode = 200, ContentType = "application/json", ElapsedMs = 5, Outcome = "HTTP 200 application/json" },
                Message = "HTTP 200 application/json"
            });
        }

        public Task<AuthenticatedReviewExecutionOutcome> ExecuteGraphQlQueryAsync(AuthenticatedReviewIdentity identity, string endpointUrl, string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthenticatedReviewExecutionOutcome
            {
                Status = AuthenticatedExecutionStatus.Executed, Mode = ReviewExecutionMode.AuthenticatedViaLocalHttpsProxy,
                Result = new AuthenticatedApiExecutionResult { StatusCode = 200, GraphQlHasData = true, GraphQlErrorCount = 0, Outcome = "HTTP 200; GraphQL data returned without errors." },
                Message = "HTTP 200; GraphQL data returned without errors."
            });
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), RequestMessage = request });
    }
}
