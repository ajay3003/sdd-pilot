using BirkNext.ApiReview;
using BirkNext.RuntimeSecurity;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using Bunit;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// API Quality Review runtime security: authorization scenarios (explicit expectations, trusted registration, sign-in requirement,
/// production block, results by outcome), request-body fuzzing opt-in, and the Security tab's documentation exposure and CORS matrix.
/// </summary>
public sealed partial class ApiQualityReviewLandingUITests
{
    private static AuthorizationScenario UiScenario() => new()
    {
        ScenarioId = "roller", DisplayName = "Roles list", ApiType = AuthorizationScenarioApiType.Rest, Url = $"https://{ApiHost}/api/autorisasjon/roller",
        Identities =
        [
            new() { Alias = AuthorizationIdentityAliases.ProxySession, Role = "Caseworker", Expected = AuthorizationExpectation.Allow },
            new() { Alias = AuthorizationIdentityAliases.Anonymous, Expected = AuthorizationExpectation.Deny },
        ],
    };

    private static FrontendAnalysisContext WithRuntimeSecurity(FrontendAnalysisContext context, Action<RuntimeSecurityExpectations> configure)
    {
        configure(context.SecuritySettings.RuntimeSecurity);
        return context;
    }

    private void Execution(TrustedTargetState trust, bool requireUser = false) =>
        _review.Setup(r => r.GetSecurityExecutionAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new SecurityExecutionStatus
        {
            RequireAuthenticatedUser = requireUser, Message = requireUser ? SecurityExecutionStatus.AuthenticationRequiredMessage : null,
            Trust = new TrustedTargetDecision { State = trust, ProfileId = "dev", ServerEnvironmentType = "Development", RegisteredOrigins = [$"https://{ApiHost}"], Reason = trust == TrustedTargetState.Trusted ? "Registered." : "This Target Environment is not registered by the server." },
        });

    [Fact]
    public void Authorization_NoScenarios_RunDisabled_StatesTheWarning()
    {
        Execution(TrustedTargetState.Trusted);
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-authorization]"));
        page.Find("[data-testid=aqr-authorization-warning]").TextContent.Should().Be(RuntimeSecurityWording.NotAPenetrationTest);
        page.Find("[data-testid=aqr-authorization-run]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=aqr-authorization-run-help]").TextContent.Should().Contain("Add authorization scenarios");
    }

    [Fact]
    public void Authorization_NotRegistered_RunDisabledWithTheServerReason()
    {
        Execution(TrustedTargetState.NotRegistered);
        Register(WithRuntimeSecurity(Context(), r => r.AuthorizationScenarios.Add(UiScenario())), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-authorization-trust]").GetAttribute("data-state").Should().Be("NotRegistered"));
        page.Find("[data-testid=aqr-authorization-run]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=aqr-authorization-run-help]").TextContent.Should().Contain("not registered by the server");
        page.FindAll("[data-testid=aqr-authorization-plan-row]").Should().HaveCount(2);
    }

    [Fact]
    public void Authorization_SignInRequiredButUnconfigured_ShowsTheAuthState_AndDisablesRun()
    {
        Execution(TrustedTargetState.Trusted, requireUser: true);
        Register(WithRuntimeSecurity(Context(), r => r.AuthorizationScenarios.Add(UiScenario())), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-authorization-auth-unconfigured]").TextContent.Should().Contain(SecurityExecutionStatus.AuthenticationRequiredMessage));
        page.Find("[data-testid=aqr-authorization-run]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Authorization_Production_IsBlocked()
    {
        Execution(TrustedTargetState.Trusted);
        Register(WithRuntimeSecurity(ContextOf(FrontendEnvironmentType.Production), r => r.AuthorizationScenarios.Add(UiScenario())), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-authorization-state]").TextContent.Should().Be("Blocked"));
        page.Find("[data-testid=aqr-authorization-run-help]").TextContent.Should().Contain("Production environments are never tested");
    }

    [Fact]
    public void Authorization_Run_SendsTheScenarios_ShowsEachIdentityAgainstItsExpectation_NoCredential()
    {
        Execution(TrustedTargetState.Trusted);
        AuthorizationRunRequest? sent = null;
        _review.Setup(r => r.RunAuthorizationAsync(It.IsAny<AuthorizationRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback((AuthorizationRunRequest request, CancellationToken _) => sent = request)
            .ReturnsAsync((new AuthorizationRunReport
            {
                RunId = "auth-1", EnvironmentId = "dev", EnvironmentName = "M2LB DEV", ExpectationFingerprint = "0123456789abcdef", StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
                Trust = new TrustedTargetDecision { State = TrustedTargetState.Trusted },
                Observations =
                [
                    new() { ScenarioId = "roller", Scenario = "Roles list", IdentityAlias = "proxy-session", Role = "Caseworker", Expected = AuthorizationExpectation.Allow, StatusCode = 200, Outcome = AuthorizationOutcome.VerifiedAllow, Reason = "HTTP 200: allowed, as expected." },
                    new() { ScenarioId = "roller", Scenario = "Roles list", IdentityAlias = "anonymous", Expected = AuthorizationExpectation.Deny, StatusCode = 200, Outcome = AuthorizationOutcome.UnexpectedAllow, Reason = "HTTP 200: allowed although this identity is expected to be denied." },
                ],
                Limitations = ["Result counts are never used."],
            }, (string?)null));
        Register(WithRuntimeSecurity(Context(), r => r.AuthorizationScenarios.Add(UiScenario())), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-authorization-run]").HasAttribute("disabled").Should().BeFalse());
        page.Find("[data-testid=aqr-authorization-run]").Click();
        page.WaitForAssertion(() => page.FindAll("[data-testid=aqr-authorization-result-row]").Should().HaveCount(2));
        page.FindAll("[data-testid=aqr-authorization-result-row]").Select(r => r.GetAttribute("data-outcome")).Should().Equal("VerifiedAllow", "UnexpectedAllow");
        sent!.Scenarios.Should().ContainSingle().Which.Identities.Select(i => i.Expected).Should().Equal(AuthorizationExpectation.Allow, AuthorizationExpectation.Deny);
        sent.Review.Targets.Should().NotBeEmpty();
        page.Markup.Should().NotContain("Bearer").And.NotContain("eyJ");
        _history.For("dev").AuthorizationRuns.Should().ContainSingle().Which.RunId.Should().Be("auth-1");
    }

    [Fact]
    public void BodyFuzzing_IsOffByDefault_ToggleSendsTheOptInsWithTheRequest()
    {
        ApiFuzzingRunRequest? planned = null;
        _review.Setup(r => r.PlanFuzzingAsync(It.IsAny<ApiFuzzingRunRequest>(), It.IsAny<CancellationToken>()))
            .Callback((ApiFuzzingRunRequest request, CancellationToken _) => planned = request)
            .ReturnsAsync((Plan(ApiFuzzingLevel.ContractFuzzing), (string?)null));
        Register(WithRuntimeSecurity(Context(), r => r.BodyFuzzOperations.Add(new BodyFuzzOperationOptIn { Method = "POST", Path = "/api/autorisasjon/sok", Policy = BodyFuzzingPolicy.ReadOnlyBodySafe })),
            authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-fuzzing-body-toggle]"));
        page.Find("[data-testid=aqr-fuzzing-body-toggle]").HasAttribute("checked").Should().BeFalse();
        page.Find("[data-testid=aqr-fuzzing-body-help]").TextContent.Should().Contain("1 read-only opt-in(s)");
        page.Find("[data-testid=aqr-fuzz-level-contractfuzzing]").Change(true);
        page.Find("[data-testid=aqr-fuzzing-body-toggle]").Change(true);
        page.Find("[data-testid=aqr-fuzzing-analyze]").Click();
        page.WaitForAssertion(() => planned.Should().NotBeNull());
        planned!.Settings.BodyFuzzing.Should().BeTrue();
        planned.BodyOperations.Should().ContainSingle().Which.Key.Should().Be("POST /api/autorisasjon/sok");
    }

    [Fact]
    public void SecurityTab_ShowsDocumentationExposureAndTheCorsMatrix()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints(), request => StubReport(request, true) with
        {
            Targets = StubReport(request, true).Targets.Select((t, i) => t with
            {
                DocumentationExposure = i == 0 ? new ApiDocumentationExposureResult
                {
                    TargetId = t.Target.TargetId, Origin = $"https://{ApiHost}", Observed = ApiDocumentationProbeState.ReachablePublic, Expectation = ApiDocumentationExposureExpectation.ExpectedProtected,
                    Assessment = ApiDocumentationAssessment.UnexpectedExposure, Reason = "Documentation is expected to require authentication but is served anonymously.",
                    Probes = [new ApiDocumentationProbe { Path = "/swagger", Source = "Default candidate", StatusCode = 200, State = ApiDocumentationProbeState.ReachablePublic, DocumentKind = "Swagger UI page" }],
                } : null,
                CorsProbes = i == 0
                    ? [
                        new CorsProbeObservation { Kind = CorsProbeKind.FrontendOrigin, Origin = Origin, StatusCode = 204, AllowOrigin = Origin, Observed = CorsObservedBehavior.AllowedExplicit, Assessment = CorsAssessment.Observation },
                        new CorsProbeObservation { Kind = CorsProbeKind.ForeignOrigin, Origin = CorsProbeRules.ForeignOrigin, StatusCode = 204, AllowOrigin = CorsProbeRules.ForeignOrigin, AllowCredentials = true, Expected = "Deny", Observed = CorsObservedBehavior.ReflectedOrigin, Assessment = CorsAssessment.UnexpectedAllow, RuleId = "cors-reflected-origin-credentials" },
                      ]
                    : [],
            }).ToList(),
        });
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-run]").HasAttribute("disabled").Should().BeFalse());
        page.Find("[data-testid=aqr-run]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-tab-security]"));
        page.Find("[data-testid=aqr-tab-security]").Click();
        page.Find("[data-testid=aqr-docs-exposure-row]").GetAttribute("data-assessment").Should().Be("UnexpectedExposure");
        page.FindAll("[data-testid=aqr-cors-row]").Select(r => r.GetAttribute("data-kind")).Should().Equal("FrontendOrigin", "ForeignOrigin");
        page.Find("[data-testid=aqr-runtime-security-warning]").TextContent.Should().Contain("not a penetration test");
    }

    [Fact]
    public void RunRequest_CarriesOnlyTheApiRuntimePolicy_NeverCookieExpectations()
    {
        var context = WithRuntimeSecurity(Context(), r =>
        {
            r.ApiDocumentationExposure = ApiDocumentationExposureExpectation.ExpectedUnavailable;
            r.Cors.AllowedOrigins.Add("https://allowed.example.test");
            r.Cookies.AuthCookieNames.Add("Session");
        });
        var request = ApiQualityReview.BuildRequest(context, context.ReviewIdentity!, [], [], null);
        request.Policy.RuntimeSecurity!.ApiDocumentationExposure.Should().Be(ApiDocumentationExposureExpectation.ExpectedUnavailable);
        request.Policy.RuntimeSecurity.Cors.AllowedOrigins.Should().Equal("https://allowed.example.test");
        System.Text.Json.JsonSerializer.Serialize(request).Should().NotContainEquivalentOf("cookie");
        context.SecuritySettings.RuntimeSecurity.Cors.AllowedOrigins.Add("https://later.example.test");
        request.Policy.RuntimeSecurity.Cors.AllowedOrigins.Should().HaveCount(1, "the run keeps the expectations it was evaluated with");
    }
}
