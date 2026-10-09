using System.Net;
using System.Text.Json;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ApiQuality.Security;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.RuntimeSecurity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static BirkNext.Api.Tests.Services.ApiQuality.RuntimeSecurityTestSupport;

namespace BirkNext.Api.Tests.Services.ApiQuality;

/// <summary>
/// Explicit authorization scenarios: each identity against its OWN allow/deny expectation (never counts, never identity-vs-identity),
/// anti-disclosure 404 only when declared, GraphQL authorization errors, trusted server registration, production and arbitrary-URL blocks,
/// bounded execution, and credentials that never appear in the report.
/// </summary>
public sealed class AuthorizationScenarioTests
{
    private static AuthorizationScenario Scenario(string id = "children-list", AuthorizationScenarioApiType type = AuthorizationScenarioApiType.Rest, string? query = null,
        params AuthorizationScenarioIdentity[] identities) => new()
    {
        ScenarioId = id, DisplayName = "Children list", ApiType = type, Url = type == AuthorizationScenarioApiType.Rest ? ApiOrigin + "/api/children" : ApiOrigin + "/graphql",
        GraphQlQuery = query, Identities = identities.Length > 0 ? [.. identities] : [Identity(AuthorizationIdentityAliases.Anonymous, AuthorizationExpectation.Deny)],
    };

    private static AuthorizationScenarioIdentity Identity(string alias, AuthorizationExpectation expected, bool notFoundMeansDeny = false, string role = "") =>
        new() { Alias = alias, Expected = expected, NotFoundMeansDeny = notFoundMeansDeny, Role = role };

    private static AuthorizationScenarioService Service(Handler handler, IAuthenticatedReviewGateway? gateway = null, ITrustedSecurityTargetRegistry? trust = null, IEnumerable<IAuthorizationTestIdentityProvider>? providers = null) =>
        new(new HttpClient(handler), gateway ?? new Gateway(), ApiEnvironmentSafetyPolicy.Default, trust ?? Registry(), providers ?? [], NullLogger<AuthorizationScenarioService>.Instance);

    private static AuthorizationRunRequest Run(string type = "Development", AuthenticatedTestingMethod method = AuthenticatedTestingMethod.LocalHttpsProxy, params AuthorizationScenario[] scenarios) => new()
    {
        Review = ReviewRequest(type, method: method, targets: [Rest(auth: true)]), Scenarios = [.. scenarios],
    };

    // ── Rules ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AuthorizationExpectation.Allow, 200, false, AuthorizationOutcome.VerifiedAllow)]
    [InlineData(AuthorizationExpectation.Deny, 403, false, AuthorizationOutcome.VerifiedDeny)]
    [InlineData(AuthorizationExpectation.Deny, 401, false, AuthorizationOutcome.VerifiedDeny)]
    [InlineData(AuthorizationExpectation.Deny, 200, false, AuthorizationOutcome.UnexpectedAllow)]
    [InlineData(AuthorizationExpectation.Allow, 403, false, AuthorizationOutcome.UnexpectedDeny)]
    [InlineData(AuthorizationExpectation.Allow, 401, false, AuthorizationOutcome.AuthenticationFailed)]
    [InlineData(AuthorizationExpectation.Deny, 404, false, AuthorizationOutcome.NotVerified)]
    [InlineData(AuthorizationExpectation.Deny, 404, true, AuthorizationOutcome.VerifiedDeny)]
    [InlineData(AuthorizationExpectation.Allow, 404, true, AuthorizationOutcome.UnexpectedDeny)]
    [InlineData(AuthorizationExpectation.Allow, 404, false, AuthorizationOutcome.NotVerified)]
    [InlineData(AuthorizationExpectation.Deny, 500, false, AuthorizationOutcome.NotVerified)]
    public void Rest_ExplicitExpectation(AuthorizationExpectation expected, int status, bool notFoundMeansDeny, AuthorizationOutcome outcome) =>
        Assert.Equal(outcome, AuthorizationScenarioRules.Classify(Scenario(), Identity("x", expected, notFoundMeansDeny), true, status, null, null, null).Outcome);

    [Fact]
    public void GraphQl_AuthorizationErrorIsDeny_DataIsAllow_CountsAreNeverUsed()
    {
        var gql = Scenario(type: AuthorizationScenarioApiType.GraphQl, query: "query { children { id } }");
        Assert.Equal(AuthorizationOutcome.VerifiedDeny, AuthorizationScenarioRules.Classify(gql, Identity("b", AuthorizationExpectation.Deny), true, 200, true, false, 1).Outcome);
        Assert.Equal(AuthorizationOutcome.VerifiedAllow, AuthorizationScenarioRules.Classify(gql, Identity("a", AuthorizationExpectation.Allow), true, 200, false, true, 0).Outcome);
        Assert.Equal(AuthorizationOutcome.UnexpectedAllow, AuthorizationScenarioRules.Classify(gql, Identity("b", AuthorizationExpectation.Deny), true, 200, false, true, 0).Outcome);
        // A non-authorization GraphQL error is neither allow nor deny.
        Assert.Equal(AuthorizationOutcome.NotVerified, AuthorizationScenarioRules.Classify(gql, Identity("b", AuthorizationExpectation.Deny), true, 200, false, false, 1).Outcome);
        Assert.True(AuthorizationScenarioRules.IsAuthorizationErrorCode("auth_not_authorized"));
        Assert.False(AuthorizationScenarioRules.IsAuthorizationErrorCode("VALIDATION_ERROR"));
    }

    [Fact]
    public void Validate_RejectsUnsafeMethodsMutationsAndMalformedScenarios()
    {
        Assert.Null(AuthorizationScenarioRules.Validate(Scenario()));
        Assert.NotNull(AuthorizationScenarioRules.Validate(new AuthorizationScenario { ScenarioId = "x", Url = ApiOrigin + "/a", Method = "DELETE", Identities = [Identity("anonymous", AuthorizationExpectation.Deny)] }));
        Assert.NotNull(AuthorizationScenarioRules.Validate(Scenario(type: AuthorizationScenarioApiType.GraphQl, query: "mutation { deleteChild(id: 1) }")));
        Assert.NotNull(AuthorizationScenarioRules.Validate(Scenario().with_identities()));
        Assert.NotNull(AuthorizationScenarioRules.Validate(new AuthorizationScenario { ScenarioId = "x", Url = "ftp://x", Identities = [Identity("anonymous", AuthorizationExpectation.Deny)] }));
    }

    // ── Execution ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoleA_Allow_RoleB_Deny_AreBothVerified_AgainstTheirOwnExpectations()
    {
        var handler = new Handler { Respond = (_, _) => Text(HttpStatusCode.Forbidden, "{}") };
        var gateway = new Gateway(status: 200);
        var report = await Service(handler, gateway).RunAsync(Run(scenarios: Scenario(identities: [Identity(AuthorizationIdentityAliases.ProxySession, AuthorizationExpectation.Allow, role: "Caseworker"), Identity(AuthorizationIdentityAliases.Anonymous, AuthorizationExpectation.Deny)])));
        Assert.Null(report.BlockedReason);
        Assert.Equal([AuthorizationOutcome.VerifiedAllow, AuthorizationOutcome.VerifiedDeny], report.Observations.Select(o => o.Outcome));
        Assert.Equal("Caseworker", report.Observations[0].Role);
        Assert.Equal(1, gateway.RestCalls);
        var anonymous = Assert.Single(handler.Requests);
        Assert.Null(anonymous.Headers.Authorization);
        Assert.Equal(HttpMethod.Get, anonymous.Method);
    }

    [Fact]
    public async Task UnexpectedAllow_IsReported_NotCollapsedIntoGenericFailure()
    {
        var handler = new Handler { Respond = (_, _) => Text(HttpStatusCode.OK, "{\"items\":[1,2,3]}") };
        var report = await Service(handler).RunAsync(Run(scenarios: Scenario()));
        var o = Assert.Single(report.Observations);
        Assert.Equal(AuthorizationOutcome.UnexpectedAllow, o.Outcome);
        Assert.Equal(200, o.StatusCode);
    }

    [Fact]
    public async Task GraphQlAnonymous_AuthErrorEnvelope_IsVerifiedDeny()
    {
        var handler = new Handler { Respond = (_, _) => Text(HttpStatusCode.OK, "{\"data\":{\"children\":null},\"errors\":[{\"message\":\"no\",\"extensions\":{\"code\":\"AUTH_NOT_AUTHORIZED\"}}]}") };
        var report = await Service(handler).RunAsync(Run(scenarios: Scenario(type: AuthorizationScenarioApiType.GraphQl, query: "query { children { id } }")));
        var o = Assert.Single(report.Observations);
        Assert.Equal(AuthorizationOutcome.VerifiedDeny, o.Outcome);
        Assert.True(o.GraphQlAuthorizationError);
        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task UnknownIdentityAlias_IsExecutionUnavailable_NothingSent()
    {
        var handler = new Handler();
        var report = await Service(handler).RunAsync(Run(scenarios: Scenario(identities: [Identity("supervisor", AuthorizationExpectation.Allow)])));
        Assert.Equal(AuthorizationOutcome.ExecutionUnavailable, Assert.Single(report.Observations).Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ServerRegisteredProvider_AppliesItsOwnCredential_WhichNeverReachesTheReport()
    {
        var handler = new Handler { Respond = (r, _) => Text(r.Headers.Authorization?.Parameter == SecretToken ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, "{}") };
        var report = await Service(handler, providers: [new TokenProvider()]).RunAsync(Run(scenarios: Scenario(identities: [Identity("supervisor", AuthorizationExpectation.Allow)])));
        Assert.Equal(AuthorizationOutcome.VerifiedAllow, Assert.Single(report.Observations).Outcome);
        Assert.DoesNotContain(SecretToken, JsonSerializer.Serialize(report));
    }

    private sealed class TokenProvider : IAuthorizationTestIdentityProvider
    {
        public string Alias => "supervisor";
        public Task<string?> ApplyAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.Authorization = new("Bearer", SecretToken);
            return Task.FromResult<string?>(null);
        }
    }

    [Fact]
    public async Task ProxySession_WithoutAuthenticatedContext_IsExecutionUnavailable()
    {
        var report = await Service(new Handler(), new Gateway(available: false)).RunAsync(Run(scenarios: Scenario(identities: [Identity(AuthorizationIdentityAliases.ProxySession, AuthorizationExpectation.Allow)])));
        Assert.Equal(AuthorizationOutcome.ExecutionUnavailable, Assert.Single(report.Observations).Outcome);
    }

    // ── Safety ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Production_IsBlocked_NothingSent()
    {
        var handler = new Handler();
        var report = await Service(handler).RunAsync(Run("Production", scenarios: Scenario()));
        Assert.NotNull(report.BlockedReason);
        Assert.Empty(handler.Requests);
        Assert.All(report.Observations, o => Assert.Equal(AuthorizationOutcome.ExecutionUnavailable, o.Outcome));
    }

    [Fact]
    public async Task UnknownEnvironmentType_IsBlocked()
    {
        var handler = new Handler();
        var report = await Service(handler).RunAsync(Run("Custom", scenarios: Scenario()));
        Assert.NotNull(report.BlockedReason);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ServerRegisteredAsProduction_IsBlocked_EvenWhenTheClientClaimsDevelopment()
    {
        var handler = new Handler();
        var report = await Service(handler, trust: Registry("Production")).RunAsync(Run(scenarios: Scenario()));
        Assert.Equal(TrustedTargetState.ProductionBlocked, report.Trust.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NotRegisteredProfile_AndArbitraryUrl_AreBlocked()
    {
        var handler = new Handler();
        var unregistered = await Service(handler, trust: new TrustedSecurityTargetRegistry(Options.Create(new SecurityTestingOptions()))).RunAsync(Run(scenarios: Scenario()));
        Assert.Equal(TrustedTargetState.NotRegistered, unregistered.Trust.State);
        var arbitrary = Scenario();
        arbitrary.Url = "https://evil.example.test/api/children";
        var outside = await Service(handler).RunAsync(Run(scenarios: arbitrary));
        Assert.Equal(TrustedTargetState.OriginNotRegistered, outside.Trust.State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GraphQlMutationScenario_IsNeverSent()
    {
        var handler = new Handler();
        var report = await Service(handler).RunAsync(Run(scenarios: Scenario(type: AuthorizationScenarioApiType.GraphQl, query: "mutation { removeChild(id: 1) { id } }")));
        Assert.Equal(AuthorizationOutcome.ExecutionUnavailable, Assert.Single(report.Observations).Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Bounded_ScenarioAndIdentityCapsApply()
    {
        var handler = new Handler { Respond = (_, _) => Text(HttpStatusCode.Forbidden, "{}") };
        var scenarios = Enumerable.Range(0, AuthorizationScenarioRules.MaxScenarios + 5).Select(i => Scenario($"s{i}")).ToArray();
        var report = await Service(handler).RunAsync(Run(scenarios: scenarios));
        Assert.Equal(AuthorizationScenarioRules.MaxScenarios, report.Scenarios.Count);
        Assert.True(handler.Requests.Count <= AuthorizationScenarioService.MaxRequests);
    }

    [Fact]
    public async Task Cancellation_StopsSending()
    {
        var handler = new Handler { Respond = (_, _) => Text(HttpStatusCode.Forbidden, "{}") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var report = await Service(handler).RunAsync(Run(scenarios: [Scenario("a"), Scenario("b")]), cts.Token);
        Assert.Empty(handler.Requests);
        Assert.All(report.Observations, o => Assert.Equal(AuthorizationOutcome.NotVerified, o.Outcome));
    }

    [Fact]
    public async Task Report_StoresAliasesRolesAndFingerprint_NeverCredentials()
    {
        var report = await Service(new Handler { Respond = (_, _) => Text(HttpStatusCode.Forbidden, "{}") }, new Gateway(status: 200)).RunAsync(Run(scenarios: Scenario(identities: [Identity(AuthorizationIdentityAliases.ProxySession, AuthorizationExpectation.Allow, role: "Lead")])));
        var json = JsonSerializer.Serialize(report);
        Assert.Matches("^[0-9a-f]{16}$", report.ExpectationFingerprint);
        Assert.DoesNotContain("eyJ", json);
        Assert.DoesNotContain("Bearer", json);
        Assert.Contains("\"Role\":\"Lead\"", json);
    }

    [Fact]
    public void Controller_RefusesProtectedExecution_WhenUserAuthenticationIsRequiredButUnavailable()
    {
        var controller = new BirkNext.Api.Controllers.ApiQualityController(new NoReview(), NullLogger<BirkNext.Api.Controllers.ApiQualityController>.Instance)
        {
            ControllerContext = new() { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() },
        };
        var options = Options.Create(new SecurityTestingOptions { RequireAuthenticatedUser = true });
        var result = controller.RunAuthorization(new AuthorizationRunRequest(), Service(new Handler()), options, default).GetAwaiter().GetResult();
        var refused = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(result);
        Assert.Equal(503, refused.StatusCode);
        Assert.Contains(SecurityExecutionStatus.AuthenticationRequiredMessage, JsonSerializer.Serialize(refused.Value));
        var status = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(controller.SecurityExecution("dev", options, Registry()));
        Assert.False(((SecurityExecutionStatus)status.Value!).ProtectedExecutionAvailable);
    }

    private sealed class NoReview : IApiQualityReviewService
    {
        public Task<ApiQualityReviewReport> AnalyzeAsync(ApiQualityReviewRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

internal static class ScenarioTestExtensions
{
    /// <summary>A scenario with no identities (invalid: every scenario needs explicit expectations).</summary>
    public static AuthorizationScenario with_identities(this AuthorizationScenario s) { s.Identities = []; return s; }
}
