using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// API Quality Review security evidence and Safe fuzzing: authentication enforcement is shown per authenticated target and never mixed with
/// authorization; header expectations show expected/observed/result; Safe fuzzing analyses, previews, runs, cancels and is blocked in
/// Production — always stated as bounded safe fuzzing, never as a penetration test.
/// </summary>
public sealed partial class ApiQualityReviewLandingUITests
{
    private static FrontendAnalysisContext ContextOf(FrontendEnvironmentType type)
    {
        var context = Context();
        context.ActiveProfile.EnvironmentType = type;
        return context;
    }

    private static ApiFuzzCase FuzzCase(string id, ApiFuzzMutationType mutation) => new()
    {
        CaseId = id, Protocol = ApiFuzzProtocol.Rest, TargetId = "rest", OperationId = "GET /api/autorisasjon/roller", OperationDisplay = "GET /api/autorisasjon/roller",
        MutationType = mutation, Parameter = "status", Location = ApiFuzzParameterLocation.Query, SourceContractRef = "#/paths/~1api~1autorisasjon~1roller/get/parameters/0",
        Method = "GET", ExpectedBehavior = ApiFuzzExpectedBehavior.RejectWithClientError, ValuePreview = "not one of 2 declared values",
    };

    private static ApiFuzzingPlan Plan(ApiFuzzingLevel level) => new()
    {
        Level = level, Settings = new ApiFuzzingSettings { Level = level }.Clamped(), GeneratedAt = DateTimeOffset.UtcNow,
        Safety = new ApiEnvironmentSafetyDecision { State = ApiEnvironmentSafetyState.Allowed, EnvironmentType = "Development", Reason = "Development environment: bounded active API testing is permitted." },
        Operations =
        [
            new() { TargetId = "rest", OperationId = "GET /api/autorisasjon/roller", Display = "GET /api/autorisasjon/roller", Protocol = ApiFuzzProtocol.Rest, Method = "GET", Classification = ApiFuzzSafetyClassification.ReadOnlyEligible, Reason = "Read-only; 2 declared parameter(s).", CaseCount = 2 },
            new() { TargetId = "rest", OperationId = "POST /api/autorisasjon/brukere", Display = "POST /api/autorisasjon/brukere", Protocol = ApiFuzzProtocol.Rest, Method = "POST", Classification = ApiFuzzSafetyClassification.UnsafeMethod, Reason = "POST can change state." },
            new() { TargetId = "gql", OperationId = "mutation oppdaterRolle", Display = "mutation oppdaterRolle", Protocol = ApiFuzzProtocol.GraphQl, Method = "POST", Classification = ApiFuzzSafetyClassification.Mutation, Reason = "GraphQL mutations are never sent." },
        ],
        Cases = [FuzzCase("fz-000000000001", ApiFuzzMutationType.InvalidEnum), FuzzCase("fz-000000000002", ApiFuzzMutationType.MissingRequired)],
    };

    private static ApiFuzzingReport FinishedRun(ApiFuzzingPlan plan, bool running = false) => new()
    {
        RunId = "fuzz-1", EnvironmentId = "dev", EnvironmentName = "M2LB DEV", Safety = plan.Safety, Level = plan.Level, Settings = plan.Settings, Operations = plan.Operations, Cases = plan.Cases,
        CasesPlanned = 2, CasesExecuted = running ? 1 : 2, StartedAt = DateTimeOffset.UtcNow, Running = running,
        Completeness = running ? ApiFuzzCompleteness.NotRun : ApiFuzzCompleteness.Full, CompletenessReason = running ? null : "All 2 planned case(s) executed.",
        Results =
        [
            new() { CaseId = "fz-000000000001", Executed = true, StatusCode = 500, Outcome = ApiFuzzOutcome.Unexpected5xx, LeakIndicators = ["stack-trace"], Note = "HTTP 500" },
            .. running ? Array.Empty<ApiFuzzCaseResult>() : [new ApiFuzzCaseResult { CaseId = "fz-000000000002", Executed = true, StatusCode = 400, Outcome = ApiFuzzOutcome.HandledValidation, Note = "HTTP 400" }],
        ],
        Outcomes = running ? new() { [ApiFuzzOutcome.Unexpected5xx] = 1 } : new() { [ApiFuzzOutcome.Unexpected5xx] = 1, [ApiFuzzOutcome.HandledValidation] = 1 },
        Findings = [new ApiFuzzFinding { LogicalKey = "fuzz-unexpected-5xx|rest|GET /api/autorisasjon/roller", RuleId = "fuzz-unexpected-5xx", Severity = ApiReviewSeverity.Medium, TargetId = "rest", Operation = "GET /api/autorisasjon/roller", Title = "Invalid input causes a server error", Description = "d", Recommendation = "r", CaseIds = ["fz-000000000001"], Evidence = ["fz-000000000001 · InvalidEnum · status (Query) → HTTP 500"] }],
        Limitations = [ApiFuzzingWording.NotAPenetrationTest],
    };

    [Fact]
    public void SafeFuzzing_IsInsideApiQualityReview_StatedAsNotAPenetrationTest_OffByDefault()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-fuzzing]"));
        page.Find("[data-testid=aqr-fuzzing-intro]").TextContent.Should().Be(ApiFuzzingWording.NotAPenetrationTest);
        page.Find("[data-testid=aqr-fuzz-level-off]").HasAttribute("checked").Should().BeTrue();
        page.FindAll("[data-testid=aqr-fuzzing-levels] input[type=radio]").Should().HaveCount(3, "Off, Contract fuzzing, Safe security fuzzing — no aggressive mode");
        page.Find("[data-testid=aqr-fuzzing-analyze]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=aqr-fuzzing-run]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=aqr-fuzzing-limits]").TextContent.Should().Contain("one at a time").And.Contain("At most 40 requests");
    }

    [Fact]
    public void SafeFuzzing_Production_IsBlockedBySafetyPolicy_RunAndLevelsDisabled()
    {
        Register(ContextOf(FrontendEnvironmentType.Production), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-fuzzing-blocked]").TextContent.Should().Contain("Blocked by safety policy").And.Contain("Production"));
        page.Find("[data-testid=aqr-fuzzing-levels]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=aqr-fuzzing-run]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=aqr-fuzzing-run-help]").TextContent.Should().Contain("blocked by safety policy");
        _review.Verify(r => r.PlanFuzzingAsync(It.IsAny<ApiFuzzingRunRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void SafeFuzzing_CustomEnvironment_SafetyCouldNotBeEstablished()
    {
        Register(ContextOf(FrontendEnvironmentType.Custom), authenticated: true, AutorisasjonEndpoints());
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-fuzzing-blocked]").TextContent.Should().Contain("Environment safety could not be established"));
    }

    [Fact]
    public void SafeFuzzing_AnalyzeShowsEligibilityAndPreview_RunShowsResults()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        ApiFuzzingRunRequest? sent = null;
        _review.Setup(r => r.PlanFuzzingAsync(It.IsAny<ApiFuzzingRunRequest>(), It.IsAny<CancellationToken>()))
            .Returns((ApiFuzzingRunRequest request, CancellationToken _) => { sent = request; return Task.FromResult<(ApiFuzzingPlan?, string?)>((Plan(request.Settings.Level), null)); });
        _review.Setup(r => r.StartFuzzingAsync(It.IsAny<ApiFuzzingRunRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FinishedRun(Plan(ApiFuzzingLevel.ContractFuzzing)), (string?)null));
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-fuzzing]"));

        page.Find("[data-testid=aqr-fuzz-level-contractfuzzing]").Change(ApiFuzzingLevel.ContractFuzzing.ToString());
        page.Find("[data-testid=aqr-fuzzing-run]").HasAttribute("disabled").Should().BeTrue("the run executes exactly an analysed plan");
        page.Find("[data-testid=aqr-fuzzing-analyze]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=aqr-fuzzing-eligibility-summary]").TextContent.Should().Contain("1 eligible operation").And.Contain("2 skipped").And.Contain("2 cases planned"));
        sent!.Settings.Level.Should().Be(ApiFuzzingLevel.ContractFuzzing);
        sent.Review.Targets.Should().NotBeEmpty("the same targets the review would use");
        page.FindAll("[data-testid=aqr-fuzzing-operation-row]").Select(r => r.GetAttribute("data-classification"))
            .Should().Equal("ReadOnlyEligible", "UnsafeMethod", "Mutation");
        page.Find("[data-testid=aqr-fuzzing-preview-toggle]").Click();
        page.FindAll("[data-testid=aqr-fuzzing-case-row]").Should().HaveCount(2);
        page.Find("[data-testid=aqr-fuzzing-cases]").TextContent.Should().Contain("Invalid enum value").And.Contain("Rejected with a client error");

        page.Find("[data-testid=aqr-fuzzing-run]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-fuzzing-results]").GetAttribute("data-completeness").Should().Be("Full"));
        Count(page, "planned").Should().Be("2");
        Count(page, "5xx").Should().Be("1");
        Count(page, "handled").Should().Be("1");
        Count(page, "leaks").Should().Be("1");
        page.FindAll("[data-testid=aqr-fuzzing-finding]").Should().ContainSingle().Which.GetAttribute("data-rule").Should().Be("fuzz-unexpected-5xx");
        page.Find("[data-testid=aqr-fuzzing-status]").TextContent.Should().Contain("Safe fuzzing finished");
        _history.For("dev").FuzzRuns.Should().ContainSingle("the finished run is kept in run history with its settings and results");
    }

    [Fact]
    public void SafeFuzzing_Cancel_StopsTheRun_AndShowsPartial()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints());
        var plan = Plan(ApiFuzzingLevel.SafeSecurityFuzzing);
        var cancelled = false;
        _review.Setup(r => r.PlanFuzzingAsync(It.IsAny<ApiFuzzingRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((plan, (string?)null));
        _review.Setup(r => r.StartFuzzingAsync(It.IsAny<ApiFuzzingRunRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((FinishedRun(plan, running: true), (string?)null));
        _review.Setup(r => r.CancelFuzzingAsync("fuzz-1", It.IsAny<CancellationToken>())).Callback(() => cancelled = true).ReturnsAsync(FinishedRun(plan, running: true));
        _review.Setup(r => r.GetFuzzingRunAsync("fuzz-1", It.IsAny<CancellationToken>())).ReturnsAsync(() => cancelled
            ? FinishedRun(plan) with { Completeness = ApiFuzzCompleteness.Partial, Cancelled = true, CompletenessReason = "Cancelled by the user: no further cases were scheduled.", CasesExecuted = 1 }
            : FinishedRun(plan, running: true));
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-fuzzing]"));

        page.Find("[data-testid=aqr-fuzz-level-safesecurityfuzzing]").Change(ApiFuzzingLevel.SafeSecurityFuzzing.ToString());
        page.Find("[data-testid=aqr-fuzzing-analyze]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-fuzzing-run]").HasAttribute("disabled").Should().BeFalse());
        page.Find("[data-testid=aqr-fuzzing-run]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-fuzzing-cancel]"));
        page.Find("[data-testid=aqr-fuzzing-results]").GetAttribute("data-running").Should().Be("true");
        page.Find("[data-testid=aqr-fuzzing-cancel]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=aqr-fuzzing-results]").GetAttribute("data-completeness").Should().Be("Partial"), TimeSpan.FromSeconds(5));
        page.FindAll("[data-testid=aqr-fuzzing-cancel]").Should().BeEmpty();
        page.Find("[data-testid=aqr-fuzzing-completeness]").TextContent.Should().Contain("Partial").And.Contain("Cancelled by the user");
        page.FindAll("[data-testid=aqr-fuzzing-finding]").Should().ContainSingle("findings observed before cancellation stay valid");
    }

    [Fact]
    public void SecurityTab_ShowsAuthenticationEnforcement_AndHeaderExpectations_NotAuthorization()
    {
        Register(Context(), authenticated: true, AutorisasjonEndpoints(), report: request => StubReport(request, true) with
        {
            Policy = new ApiReviewPolicy { ExpectedSecurityHeaders = ["Strict-Transport-Security", "X-Frame-Options"] },
            Targets = StubReport(request, true).Targets.Select((t, i) => t with
            {
                AuthenticationEnforcement = i == 0
                    ? new ApiAuthenticationEnforcementResult { Status = ApiAuthenticationEnforcementStatus.Verified, Probe = "GET /api/autorisasjon/roller", StatusCode = 401, Reason = "HTTP 401 without a credential: authentication is required." }
                    : new ApiAuthenticationEnforcementResult { Status = ApiAuthenticationEnforcementStatus.UnexpectedlyPublic, Probe = ApiReviewEngineProbe, StatusCode = 200, Reason = "HTTP 200 with GraphQL data without a credential." },
                SecurityHeaders = i == 0
                    ?
                    [
                        new SecurityHeaderEvaluation("Strict-Transport-Security", true, true, "max-age=31536000", SecurityHeaderOutcome.Pass, SecurityHeaderScope.Transport, "Present as expected"),
                        new SecurityHeaderEvaluation("X-Frame-Options", true, false, null, SecurityHeaderOutcome.NotApplicable, SecurityHeaderScope.Document, "Document-level header"),
                    ]
                    : [],
            }).ToList(),
        });
        var page = Render<ApiQualityReview>();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-run]").HasAttribute("disabled").Should().BeFalse());
        page.Find("[data-testid=aqr-run]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=aqr-tab-security]"));
        page.Find("[data-testid=aqr-tab-security]").Click();

        var rows = page.FindAll("[data-testid=aqr-auth-enforcement-row]");
        rows.Select(r => r.GetAttribute("data-status")).Should().Equal("Verified", "UnexpectedlyPublic");
        rows[0].TextContent.Should().Contain("Verified").And.Contain("HTTP 401");
        rows[1].TextContent.Should().Contain("Unexpectedly public");
        page.Find("[data-testid=aqr-auth-enforcement-note]").TextContent.Should().Contain("authorization is not tested");
        page.FindAll("[data-testid=aqr-header-row]").Select(r => r.GetAttribute("data-result")).Should().Equal("Pass", "NotApplicable");
        page.Find("[data-testid=aqr-header-expectations-note]").TextContent.Should().Contain("Security Expectations").And.Contain("presence only");
        page.Find("[data-testid=aqr-security-note]").TextContent.Should().Contain("synthetic foreign origin").And.Contain("cookies are inspected by Frontend Quality Review").And.Contain("does not establish that the API is secure");
    }

    private const string ApiReviewEngineProbe = "query { __typename }";

    private static string Count(Bunit.IRenderedComponent<ApiQualityReview> page, string key) =>
        page.Find($"[data-testid=aqr-fuzzing-count][data-key='{key}'] .asf-count-value").TextContent.Trim();
}
