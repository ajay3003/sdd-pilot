using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// API Quality Review cleanup: frontend sign-in vs selected API access, "Error handling" as a domain of behavioural checks, and
/// performance thresholds that are evaluated, displayed and exported from the one policy snapshot the review ran with.
/// </summary>
public sealed class ApiReviewAccessThresholdCleanupTests
{
    // ── Historical policy snapshot ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOldResultIsShownAgainstTheThresholdsItRanWith_NotTodaysSettings()
    {
        // Recorded before this change: 500 / 1000 ms (API Response) and one shared 1 MB payload threshold.
        var old = new ApiReviewPolicy { SlowWarningMs = 500, SlowPoorMs = 1000, LargePayloadBytes = 1024 * 1024 };
        ApiReviewEvidencePresentation.CheckDetail(new ApiReviewCheck { CheckId = "rest-latency", Detail = "106 ms." }, old)
            .Should().Be("106 ms. Review policy: warning > 500 ms · poor > 1000 ms.");
        ApiReviewEvidencePresentation.CheckDetail(new ApiReviewCheck { CheckId = "rest-payload", Detail = "141 bytes." }, old)
            .Should().Be("141 bytes. Review policy: warning > 1,048,576 bytes (1 MB).");

        // A new result carries the evaluated policy in its own detail; the renderer does not append a second, different one.
        var current = new ApiReviewPolicy { SlowWarningMs = 300, RestPayloadWarningBytes = 250 * 1024 };
        ApiReviewEvidencePresentation.CheckDetail(new ApiReviewCheck { CheckId = "rest-latency", Detail = "106 ms (warning > 1500 ms)." }, current)
            .Should().Be("106 ms (warning > 1500 ms).");
    }

    [Fact]
    public void ThePerformanceNoteStatesThePolicySnapshotAndItsSource()
    {
        var note = ApiReviewPresentation.PerformanceThresholdsNote(new ApiReviewPolicy
        {
            SlowWarningMs = 1500, LatencySource = ApiReviewPresentation.LatencySourceLabel, RestPayloadWarningBytes = 500L * 1024, GraphQlPayloadWarningBytes = 1024L * 1024,
        });
        note.Should().StartWith("API response timing is evaluated against the target's Single Request Latency threshold.");
        note.Should().Contain("response time: warning > 1500 ms (Single Request Latency)")
            .And.Contain("REST payload: warning > 512,000 bytes (500 KB)")
            .And.Contain("GraphQL payload: warning > 1,048,576 bytes (1 MB)")
            .And.Contain("captured when the review ran")
            .And.NotContain("poor");
    }

    [Fact]
    public void AnOldResult_PerformanceNote_KeepsItsTwoTierPolicy_AndNoSingleRequestSentence()
    {
        var note = ApiReviewPresentation.PerformanceThresholdsNote(new ApiReviewPolicy { SlowWarningMs = 500, SlowPoorMs = 1000, LargePayloadBytes = 1024 * 1024 });
        note.Should().Contain("response time: warning > 500 ms · poor > 1000 ms").And.NotContain("Single Request Latency");
    }

    // ── Error handling ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ErrorsIsTheErrorHandlingDomain_WithItsOwnCheckHeading()
    {
        ApiReviewPresentation.TabLabel("Errors").Should().Be("Error handling");
        ApiReviewPresentation.TabLabel("Findings").Should().Be("Findings");
        ApiReviewStatusLabels.AreaLabel(ApiReviewFindingType.Errors).Should().Be("Error handling");

        var checks = new List<ApiReviewCheck>
        {
            new() { CheckId = "errors-unknown-route", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.Pass },
            new() { CheckId = "errors-problem-details", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.Warning },
            new() { CheckId = "errors-leak", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.Pass },
            new() { CheckId = "gql-error-shape", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.Pass },
            new() { CheckId = "gql-error-leak", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.Pass },
        };
        var policy = new ApiReviewPolicy();
        ApiReviewEvidencePresentation.CheckSummary(checks, policy).Should().StartWith("Error-handling checks (5)");
        // Bounded behavioural checks pass; indicator scans say what they looked for, never "Pass".
        ApiReviewEvidencePresentation.CheckLabel(checks[0], policy).Should().Be("Pass");
        ApiReviewEvidencePresentation.CheckLabel(checks[3], policy).Should().Be("Pass");
        ApiReviewEvidencePresentation.CheckLabel(checks[2], policy).Should().Be("No indicators observed");
        ApiReviewEvidencePresentation.CheckLabel(checks[4], policy).Should().Be("No indicators observed");
        ApiReviewEvidencePresentation.CheckLabel(checks[1], policy).Should().Be(ApiReviewStatusLabels.ResultLabel(ApiReviewCheckResult.Warning));
        // Other areas keep the generic heading.
        ApiReviewEvidencePresentation.CheckSummary([new ApiReviewCheck { CheckId = "sec-tls", Area = ApiReviewFindingType.Security, Result = ApiReviewCheckResult.Pass }], policy)
            .Should().StartWith("Checks (1)");
    }

    // ── Access ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static ApiReviewTarget Target(string id, ApiReviewTargetType type, bool auth) => new()
    {
        TargetId = id, ServiceName = id, ApiType = type, Host = "api-dev.bufetat.no", BasePath = type == ApiReviewTargetType.GraphQl ? "/graphql" : "/api", AuthRequired = auth,
    };

    [Fact]
    public void BothTargetsAuthenticated_ContextAvailable_TwoOfTwoReady()
    {
        var targets = new[] { Target("rest", ApiReviewTargetType.Rest, true), Target("gql", ApiReviewTargetType.GraphQl, true) };
        var rows = ApiReviewPresentation.TargetAccess(targets, ["rest", "gql"], ApiReviewAccessAvailability.Available);
        rows.Should().HaveCount(2).And.OnlyContain(r => r.Ready && r.Requirement == "Authenticated" && r.State == "Ready");
        rows.Select(r => r.Type).Should().BeEquivalentTo(["REST", "GraphQL"]);
    }

    [Fact]
    public void AuthenticatedTargetWithoutContext_IsNeverReadyThroughPublicReachability()
    {
        var targets = new[] { Target("rest", ApiReviewTargetType.Rest, false), Target("gql", ApiReviewTargetType.GraphQl, true) };
        var rows = ApiReviewPresentation.TargetAccess(targets, ["rest", "gql"], ApiReviewAccessAvailability.WaitingForAuthenticatedTraffic);
        rows.Single(r => r.Requirement == "Public").Ready.Should().BeTrue();
        var authenticated = rows.Single(r => r.Requirement == "Authenticated");
        authenticated.Ready.Should().BeFalse();
        authenticated.State.Should().Be("Authenticated context unavailable");
        // Unselected targets are not counted.
        ApiReviewPresentation.TargetAccess(targets, ["rest"], ApiReviewAccessAvailability.Available).Should().ContainSingle();
    }

    [Fact]
    public void FrontendSignInIsAFactAboutTheFrontend_NeverTheApis()
    {
        var profile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev" };
        ApiReviewPresentation.FrontendSignInLabel(new FrontendAnalysisContext { ActiveProfile = profile, RequiresAuthentication = false })
            .Should().Be("Not required for frontend entry point");
        ApiReviewPresentation.FrontendSignInLabel(new FrontendAnalysisContext { ActiveProfile = profile, RequiresAuthentication = true, AuthenticationType = FrontendAuthenticationType.MicrosoftEntraId })
            .Should().Be("Required (Microsoft Entra ID)");
        new ApiReviewScopeSummary(2, 1, 1, 2, 5).AuthNote.Should().Be("Access requirements: 2 authenticated · 0 public");
        new ApiReviewScopeSummary(2, 1, 1, 0, 5).AuthNote.Should().BeNull();
    }

    [Theory]
    [InlineData(2, 0, "Authenticated for 2 of 2 selected targets")]
    [InlineData(1, 1, "Mixed — 1 authenticated · 1 public of 2 selected targets")]
    [InlineData(0, 2, "Public for 2 of 2 selected targets")]
    [InlineData(0, 0, "None executed")]
    public void ActualAccessIsCountedAgainstTheSelectedTargets(int authenticated, int publicExecuted, string expected)
    {
        var report = new ApiReviewReport
        {
            Targets = [new() { Target = Target("a", ApiReviewTargetType.Rest, true) }, new() { Target = Target("b", ApiReviewTargetType.GraphQl, true) }],
            Coverage = new ApiReviewCoverage { AuthenticatedExecuted = authenticated, AuthenticatedPlanned = authenticated, PublicExecuted = publicExecuted, PublicPlanned = publicExecuted },
        };
        ApiReviewPresentation.Summary(report).Coverage.Single(r => r.Label == "Access").Value.Should().Be(expected);
    }

    [Fact]
    public void ExportCarriesTheErrorHandlingLabelAndThePolicyUsed()
    {
        var report = new ApiReviewReport
        {
            Policy = new ApiReviewPolicy { SlowWarningMs = 1500, LatencySource = "Single Request Latency", RestPayloadWarningBytes = 500L * 1024, GraphQlPayloadWarningBytes = 1024L * 1024 },
            Targets = [new() { Target = Target("rest", ApiReviewTargetType.Rest, true), Checks = [new() { CheckId = "errors-unknown-route", Area = ApiReviewFindingType.Errors, Title = "Unknown route handling", Result = ApiReviewCheckResult.Pass }] }],
        };
        var html = new ReportExportService().ExportApiReview(report, "Test");
        html.Should().Contain("response time warning &gt; 1500 ms (Single Request Latency)")
            .And.Contain("REST payload warning &gt; 512,000 bytes (500 KB)")
            .And.Contain("GraphQL payload warning &gt; 1,048,576 bytes (1 MB)")
            .And.Contain("Error handling");
    }
}
