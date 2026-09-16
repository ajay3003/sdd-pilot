using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The two required engines only count as assessed when the frontend document was actually fetched. HTTP statuses, network
/// failures and real timeouts are classified precisely; the outcome labels never say "Not ready" as a catch-all.
/// </summary>
public sealed class FrontendQualityTargetFetchClassificationTests
{
    private static FrontendQualityEngineRequirementPolicy Policy() => new FrontendQualityEngineRequirementSettings().ToPolicy();

    private static WasmSecurityReviewReport Security(string status, bool analyzed = false) => new()
    {
        TargetUrl = "https://x.example.test/", ScannedAt = DateTime.UtcNow,
        Assets = [new WasmDiscoveredAsset { Url = "https://x.example.test/", AssetType = "HTML", Status = status, Analyzed = analyzed }],
    };

    private static WasmPerformanceReviewReport Performance(int statusCode, string? error = null) => new()
    {
        TargetUrl = "https://x.example.test/", ReviewedAt = DateTime.UtcNow,
        Assets = [new DiscoveredAsset { Url = "https://x.example.test/", Type = AssetType.Index, StatusCode = statusCode, Error = error }],
    };

    [Theory]
    [InlineData("Unauthorized", FrontendQualityEngineOutcomeReason.AuthenticationRequired, FrontendQualityEngineExecutionState.Unavailable, "HTTP 401")]
    [InlineData("Forbidden", FrontendQualityEngineOutcomeReason.AuthenticationRequired, FrontendQualityEngineExecutionState.Unavailable, "HTTP 403")]
    [InlineData("NotFound", FrontendQualityEngineOutcomeReason.TargetHttpError, FrontendQualityEngineExecutionState.Unavailable, "HTTP 404")]
    [InlineData("InternalServerError", FrontendQualityEngineOutcomeReason.TargetHttpError, FrontendQualityEngineExecutionState.Unavailable, "HTTP 500")]
    [InlineData("Timeout", FrontendQualityEngineOutcomeReason.TimedOut, FrontendQualityEngineExecutionState.TimedOut, "timeout period")]
    [InlineData("Error", FrontendQualityEngineOutcomeReason.TargetUnreachable, FrontendQualityEngineExecutionState.Unavailable, "unreachable")]
    public void StaticSecurity_DocumentFetchFailure_IsClassifiedPrecisely(string status, FrontendQualityEngineOutcomeReason reason, FrontendQualityEngineExecutionState state, string fragment)
    {
        var outcome = FrontendQualityEngineOutcomeNormalizer.StaticSecurity("https://x.example.test/", true, Policy(), Security(status), null);

        outcome.ExecutionState.Should().Be(state);
        outcome.OutcomeReason.Should().Be(reason);
        outcome.SanitizedFailureReason.Should().Contain(fragment);
        outcome.FindingCount.Should().BeNull();
        FrontendQualityEngineOutcomePresentation.AssessmentLabel(outcome).Should().Be("Not assessed");
        FrontendQualityEngineOutcomePresentation.GetLabel(outcome.OutcomeReason).Should().NotBe("Not ready");
    }

    [Fact]
    public void StaticSecurity_DocumentFetched_IsAssessedEvenWithZeroFindings()
    {
        var outcome = FrontendQualityEngineOutcomeNormalizer.StaticSecurity("https://x.example.test/", true, Policy(), Security("200 OK", analyzed: true), null);

        outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        outcome.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.None);
        outcome.FindingCount.Should().Be(0);
        FrontendQualityEngineOutcomePresentation.StateLabel(outcome).Should().Be("Completed — no findings");
    }

    [Fact]
    public void StaticSecurity_ReportWithoutAssetEvidence_KeepsLegacyAssessedSemantics()
    {
        FrontendQualityEngineOutcomeNormalizer.StaticSecurity("https://x.example.test/", true, Policy(), new WasmSecurityReviewReport(), null)
            .ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
    }

    [Fact]
    public void StaticSecurity_NoReportNoError_IsUnavailableNotAssessedLabel()
    {
        var outcome = FrontendQualityEngineOutcomeNormalizer.StaticSecurity("https://x.example.test/", true, Policy(), null, null);

        outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Unavailable);
        outcome.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.ReadinessUnavailable);
        FrontendQualityEngineOutcomePresentation.GetLabel(outcome.OutcomeReason).Should().NotBe("Assessed");
    }

    [Theory]
    [InlineData(401, FrontendQualityEngineOutcomeReason.AuthenticationRequired, FrontendQualityEngineExecutionState.Unavailable)]
    [InlineData(403, FrontendQualityEngineOutcomeReason.AuthenticationRequired, FrontendQualityEngineExecutionState.Unavailable)]
    [InlineData(404, FrontendQualityEngineOutcomeReason.TargetHttpError, FrontendQualityEngineExecutionState.Unavailable)]
    [InlineData(500, FrontendQualityEngineOutcomeReason.TargetHttpError, FrontendQualityEngineExecutionState.Unavailable)]
    public void PassivePerformance_IndexStatus_IsClassifiedPrecisely(int status, FrontendQualityEngineOutcomeReason reason, FrontendQualityEngineExecutionState state)
    {
        var outcome = FrontendQualityEngineOutcomeNormalizer.PassivePerformance("https://x.example.test/", true, Policy(), Performance(status), null);

        outcome.ExecutionState.Should().Be(state);
        outcome.OutcomeReason.Should().Be(reason);
        outcome.SanitizedFailureReason.Should().Contain($"HTTP {status}");
    }

    [Fact]
    public void PassivePerformance_IndexTimeout_IsTimedOut()
    {
        var outcome = FrontendQualityEngineOutcomeNormalizer.PassivePerformance("https://x.example.test/", true, Policy(), Performance(0, "Request timed out"), null);

        outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.TimedOut);
        outcome.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.TimedOut);
        outcome.SanitizedFailureReason.Should().Be("Target did not respond within timeout period.");
    }

    [Fact]
    public void PassivePerformance_IndexNetworkFailure_IsUnreachable()
    {
        var outcome = FrontendQualityEngineOutcomeNormalizer.PassivePerformance("https://x.example.test/", true, Policy(), Performance(0, "No such host is known"), null);

        outcome.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.TargetUnreachable);
        outcome.SanitizedFailureReason.Should().NotContain("timeout");
    }

    [Fact]
    public void PassivePerformance_IndexOk_IsAssessed()
    {
        FrontendQualityEngineOutcomeNormalizer.PassivePerformance("https://x.example.test/", true, Policy(), Performance(200), null)
            .ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
    }

    [Theory]
    [InlineData(PreflightStatus.TimedOut, FrontendQualityEngineExecutionState.TimedOut, FrontendQualityEngineOutcomeReason.TimedOut)]
    [InlineData(PreflightStatus.Unreachable, FrontendQualityEngineExecutionState.Unavailable, FrontendQualityEngineOutcomeReason.TargetUnreachable)]
    [InlineData(PreflightStatus.AuthenticationRequired, FrontendQualityEngineExecutionState.Unavailable, FrontendQualityEngineOutcomeReason.AuthenticationRequired)]
    [InlineData(PreflightStatus.ScannerUnavailable, FrontendQualityEngineExecutionState.Unavailable, FrontendQualityEngineOutcomeReason.EngineUnavailable)]
    [InlineData(PreflightStatus.InvalidTarget, FrontendQualityEngineExecutionState.SafetyBlocked, FrontendQualityEngineOutcomeReason.TargetPolicyRejected)]
    public void PreflightOutcome_MapsEachStatusDistinctly(PreflightStatus status, FrontendQualityEngineExecutionState state, FrontendQualityEngineOutcomeReason reason)
    {
        FrontendQualityEngineOutcomeNormalizer.PreflightOutcome(status).Should().Be((state, reason));
    }

    [Theory]
    [InlineData(FrontendQualityEngineOutcomeReason.TargetUnreachable, "Target unreachable")]
    [InlineData(FrontendQualityEngineOutcomeReason.TargetHttpError, "Target HTTP error")]
    [InlineData(FrontendQualityEngineOutcomeReason.TimedOut, "Timed out")]
    [InlineData(FrontendQualityEngineOutcomeReason.AuthenticatedContextUnavailable, "Authenticated context not available")]
    [InlineData(FrontendQualityEngineOutcomeReason.AuthenticatedContextExpired, "Authenticated context expired")]
    [InlineData(FrontendQualityEngineOutcomeReason.EnterpriseBrowserProtectionBlocked, "Blocked by enterprise browser protection")]
    [InlineData(FrontendQualityEngineOutcomeReason.BrowserDomUnavailableForMethod, "Authenticated DOM unavailable for method")]
    [InlineData(FrontendQualityEngineOutcomeReason.ManualOnlyMethod, "Manual verification only")]
    public void NewReasons_HaveExplicitLabels(FrontendQualityEngineOutcomeReason reason, string label)
    {
        FrontendQualityEngineOutcomePresentation.GetLabel(reason).Should().Be(label);
        FrontendQualityEngineOutcomePresentation.IsAssessed(reason).Should().BeFalse();
    }

    [Fact]
    public void StateLabels_CoverEveryExplicitState()
    {
        FrontendQualityEngineOutcome Make(FrontendQualityEngineExecutionState state, FrontendQualityEngineOutcomeReason reason, int? findings = null) =>
            new() { EngineId = FrontendQualityEngineId.StaticSecurity, ExecutionState = state, OutcomeReason = reason, FindingCount = findings, Enabled = true };

        FrontendQualityEngineOutcomePresentation.StateLabel(Make(FrontendQualityEngineExecutionState.Assessed, FrontendQualityEngineOutcomeReason.None, 3)).Should().Be("Completed — findings");
        FrontendQualityEngineOutcomePresentation.StateLabel(Make(FrontendQualityEngineExecutionState.Assessed, FrontendQualityEngineOutcomeReason.None, 0)).Should().Be("Completed — no findings");
        FrontendQualityEngineOutcomePresentation.StateLabel(Make(FrontendQualityEngineExecutionState.TimedOut, FrontendQualityEngineOutcomeReason.TimedOut)).Should().Be("Timed out");
        FrontendQualityEngineOutcomePresentation.StateLabel(Make(FrontendQualityEngineExecutionState.EngineError, FrontendQualityEngineOutcomeReason.EngineError)).Should().Be("Failed");
        FrontendQualityEngineOutcomePresentation.StateLabel(Make(FrontendQualityEngineExecutionState.Unavailable, FrontendQualityEngineOutcomeReason.AuthenticatedContextUnavailable)).Should().Be("Blocked");
        FrontendQualityEngineOutcomePresentation.StateLabel(Make(FrontendQualityEngineExecutionState.Unavailable, FrontendQualityEngineOutcomeReason.TargetUnreachable)).Should().Be("Blocked");
        FrontendQualityEngineOutcomePresentation.StateLabel(Make(FrontendQualityEngineExecutionState.NotApplicable, FrontendQualityEngineOutcomeReason.BrowserDomUnavailableForMethod)).Should().Be("Unsupported");
        FrontendQualityEngineOutcomePresentation.StateLabel(Make(FrontendQualityEngineExecutionState.Unavailable, FrontendQualityEngineOutcomeReason.AuthenticationModeUnsupported)).Should().Be("Unsupported");
        FrontendQualityEngineOutcomePresentation.StateLabel(Make(FrontendQualityEngineExecutionState.NotApplicable, FrontendQualityEngineOutcomeReason.NotSelected)).Should().Be("Not selected");
        FrontendQualityEngineOutcomePresentation.StateLabel(Make(FrontendQualityEngineExecutionState.Disabled, FrontendQualityEngineOutcomeReason.DisabledInSystemSettings)).Should().Be("Disabled");
        FrontendQualityEngineOutcomePresentation.StateLabel(Make(FrontendQualityEngineExecutionState.Cancelled, FrontendQualityEngineOutcomeReason.Cancelled)).Should().Be("Cancelled");
    }

    [Fact]
    public void ApplyAccessDecisions_OverridesOnlyEnginesThatDidNotRun()
    {
        var context = new FrontendAnalysisContext { FeatureToggles = new() { EnableBrowserRuntimeEngine = true, EnableAccessibilityEngine = false } };
        var outcomes = FrontendQualityEngineOutcomeNormalizer.NormalizeAll("https://x.example.test/", context, new FrontendQualityReviewOrchestrationResult(
            SecurityReport: Security("200 OK", analyzed: true)), true, true, true, true);
        var decisions = new Dictionary<FrontendQualityEngineId, FrontendQualityEngineAccessDecision>
        {
            [FrontendQualityEngineId.StaticSecurity] = new(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineAccessKind.PublicHttp, "Public HTTP", FrontendQualityEngineAccessState.Ready, FrontendQualityEngineOutcomeReason.None, ""),
            [FrontendQualityEngineId.BrowserRuntime] = new(FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineAccessKind.AuthenticatedBrowserSession, "Authenticated browser session", FrontendQualityEngineAccessState.Blocked, FrontendQualityEngineOutcomeReason.EnterpriseBrowserProtectionBlocked, "Blocked by enterprise browser protection.", "Open Target Environment", "/x"),
            [FrontendQualityEngineId.Accessibility] = new(FrontendQualityEngineId.Accessibility, FrontendQualityEngineAccessKind.AuthenticatedBrowserSession, "Authenticated browser session", FrontendQualityEngineAccessState.Blocked, FrontendQualityEngineOutcomeReason.AuthenticationRequired, "Sign in.", "Sign in for review"),
        };

        var applied = FrontendQualityEngineOutcomeNormalizer.ApplyAccessDecisions(outcomes, decisions);

        var security = applied.Single(o => o.EngineId == FrontendQualityEngineId.StaticSecurity);
        security.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed, "a completed engine keeps its result");
        security.AccessLabel.Should().Be("Public HTTP");
        var runtime = applied.Single(o => o.EngineId == FrontendQualityEngineId.BrowserRuntime);
        runtime.OutcomeReason.Should().Be(FrontendQualityEngineOutcomeReason.EnterpriseBrowserProtectionBlocked);
        runtime.RequiredAction.Should().Be("Open Target Environment");
        runtime.ActionHref.Should().Be("/x");
        var accessibility = applied.Single(o => o.EngineId == FrontendQualityEngineId.Accessibility);
        accessibility.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Disabled, "a disabled engine is left alone");
        accessibility.OutcomeReason.Should().NotBe(FrontendQualityEngineOutcomeReason.AuthenticationRequired);
        accessibility.AccessLabel.Should().Be("Authenticated browser session");
    }
}
