using BirkNext.ApiReview;
using BirkNext.SecurityExpectations;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Labels and exports of safe fuzzing and the security foundation: the client mirror of the backend environment policy, honest outcome
/// wording (4xx is handled validation), no secrets in exports, Passive Security never shown as a usable switch when the server cannot run
/// it, and Security Expectations fields labelled by who actually reads them.
/// </summary>
public sealed class ApiSafeFuzzingPresentationTests
{
    [Theory]
    [InlineData("Local", null)]
    [InlineData("Development", null)]
    [InlineData("QA", null)]
    [InlineData("Test", null)]
    [InlineData("Production", "Production environments are never fuzzed.")]
    [InlineData("Custom", "Environment safety could not be established")]
    [InlineData("", "Environment safety could not be established")]
    [InlineData("RC", "not RC")]
    public void ClientBlockedReason_MirrorsTheBackendAllowList(string type, string? expected)
    {
        var reason = ApiFuzzingPresentation.ClientBlockedReason(type);
        if (expected is null) reason.Should().BeNull();
        else reason.Should().Contain(expected);
    }

    [Fact]
    public void Levels_AreOffContractAndSafeSecurity_NoAggressiveMode()
    {
        ApiFuzzingPresentation.Levels.Should().Equal(ApiFuzzingLevel.Off, ApiFuzzingLevel.ContractFuzzing, ApiFuzzingLevel.SafeSecurityFuzzing);
        Enum.GetNames<ApiFuzzingLevel>().Should().NotContain(n => n.Contains("Aggressive", StringComparison.OrdinalIgnoreCase));
        ApiFuzzingPresentation.LevelDescription(ApiFuzzingLevel.SafeSecurityFuzzing).Should().Contain("No attack dictionaries");
        ApiFuzzingPresentation.Intro.Should().Be("Safe fuzzing sends a bounded set of deterministic invalid/boundary requests to non-production APIs. It is not a penetration test.");
    }

    [Fact]
    public void Outcomes_HandledValidationIsAPass_DefectsAreNot()
    {
        ApiFuzzingPresentation.OutcomeTone(ApiFuzzOutcome.HandledValidation).Should().Be("pass");
        ApiFuzzingPresentation.OutcomeTone(ApiFuzzOutcome.Unexpected5xx).Should().Be("fail");
        ApiFuzzingPresentation.OutcomeTone(ApiFuzzOutcome.PotentialInformationLeak).Should().Be("fail");
        ApiFuzzingPresentation.OutcomeTone(ApiFuzzOutcome.UnexpectedAcceptance).Should().Be("warning");
        ApiFuzzingPresentation.OutcomeTone(ApiFuzzOutcome.SafetyBlocked).Should().Be("muted");
        Enum.GetValues<ApiFuzzOutcome>().Should().OnlyContain(o => ApiFuzzingPresentation.OutcomeLabel(o).Length > 0);
        Enum.GetValues<ApiFuzzMutationType>().Should().OnlyContain(m => !ApiFuzzingPresentation.MutationLabel(m).Equals(m.ToString(), StringComparison.Ordinal));
        ApiFuzzingPresentation.CompletenessLabel(ApiFuzzCompleteness.Partial, running: true).Should().Be("Running");
    }

    [Fact]
    public void Summary_CountsFromTheReport_FindingsNeverMakeTheRunFailed()
    {
        var report = new ApiFuzzingReport
        {
            CasesPlanned = 3, CasesExecuted = 3, Completeness = ApiFuzzCompleteness.Full,
            Outcomes = new() { [ApiFuzzOutcome.HandledValidation] = 1, [ApiFuzzOutcome.Unexpected5xx] = 1, [ApiFuzzOutcome.UnexpectedAcceptance] = 1 },
            Results = [new() { CaseId = "a", LeakIndicators = ["sql-or-connection-string"] }],
        };
        var summary = ApiFuzzingPresentation.Summary(report).ToDictionary(c => c.Key, c => c.Value);
        summary["planned"].Should().Be(3); summary["handled"].Should().Be(1); summary["5xx"].Should().Be(1); summary["leaks"].Should().Be(1); summary["acceptance"].Should().Be(1);
        ApiFuzzingPresentation.CompletenessLabel(report.Completeness, false).Should().StartWith("Full");
    }

    [Fact]
    public void FuzzingExport_StatesScopeLimitsAndSafety_WithoutAnySecret()
    {
        var run = new ApiFuzzingReport
        {
            RunId = "fuzz-1", EnvironmentName = "Fixture DEV", Level = ApiFuzzingLevel.ContractFuzzing, Settings = new ApiFuzzingSettings().Clamped(), CasesPlanned = 1, CasesExecuted = 1,
            Safety = new ApiEnvironmentSafetyDecision { State = ApiEnvironmentSafetyState.Allowed, Reason = "Development environment.", Evidence = ["Target Environment classification: Development"] },
            Contracts = [new ApiFuzzContractFingerprint("rest", "OpenAPI", "https://api-dev.example.test/swagger.json", "ABCDEF")],
            Cases = [new ApiFuzzCase { CaseId = "fz-1", OperationDisplay = "GET /x", MutationType = ApiFuzzMutationType.InvalidUuid, Parameter = "id", Location = ApiFuzzParameterLocation.Path, ValuePreview = "not-a-uuid" }],
            Results = [new ApiFuzzCaseResult { CaseId = "fz-1", Executed = true, StatusCode = 400, Outcome = ApiFuzzOutcome.HandledValidation, Note = "HTTP 400" }],
            Limitations = [ApiFuzzingWording.NotAPenetrationTest],
        };
        var html = new ReportExportService().ExportApiFuzzing(run, "Fixture");
        html.Should().Contain("It is not a penetration test").And.Contain("Contract fuzzing").And.Contain("At most 40 requests").And.Contain("hash ABCDEF")
            .And.Contain("Handled validation").And.Contain("not-a-uuid").And.Contain("never inferred from fuzzing");
        html.Should().NotContainAny("Bearer", "eyJ", "Authorization:", "OWASP compliant", "penetration test passed");
    }

    [Fact]
    public void ApiReviewExport_IncludesAuthEnforcementHeadersAndTheSameEnvironmentsFuzzRun()
    {
        var report = new ApiReviewReport
        {
            Environment = new ApiReviewEnvironmentSnapshot { EnvironmentId = "dev", Name = "DEV", EnvironmentType = "Development" },
            Safety = new ApiEnvironmentSafetyDecision { State = ApiEnvironmentSafetyState.Allowed, Reason = "ok" },
            Targets =
            [
                new ApiReviewTargetResult
                {
                    Target = new ApiReviewTarget { TargetId = "t", ServiceName = "Children API", Host = "api-dev.example.test" },
                    AuthenticationEnforcement = new ApiAuthenticationEnforcementResult { Status = ApiAuthenticationEnforcementStatus.Verified, Probe = "GET /api/children", StatusCode = 401, Reason = "HTTP 401 without a credential." },
                    SecurityHeaders = [new SecurityHeaderEvaluation("X-Content-Type-Options", true, false, null, SecurityHeaderOutcome.Missing, SecurityHeaderScope.Transport, "Required and absent.")],
                },
            ],
        };
        var fuzz = new ApiFuzzingReport { EnvironmentId = "dev", RunId = "fuzz-9", Level = ApiFuzzingLevel.SafeSecurityFuzzing, Settings = new ApiFuzzingSettings().Clamped() };
        var html = new ReportExportService().ExportApiReview(report, "P", fuzz);
        html.Should().Contain("Authentication enforcement").And.Contain("Verified").And.Contain("Authorization is not assessed")
            .And.Contain("Security headers vs Target Environment expectations").And.Contain("Missing (expected)").And.Contain("Safe fuzzing").And.Contain("fuzz-9");
        new ReportExportService().ExportApiReview(report, "P", fuzz with { EnvironmentId = "other" }).Should().NotContain("fuzz-9", "a fuzz run of another environment is never mixed in");
    }

    [Fact]
    public void PassiveSecurity_ServerUnavailable_IsNeverAUsableSwitch()
    {
        FrontendQualityEngineStatusDto Status(bool l1, bool l2, params FrontendQualityEngineUnavailableReasonDto[] reasons) => new()
        {
            EngineId = FrontendQualityEngineIdDto.PassiveSecurity, Layer1Allowed = l1, Layer2Enabled = l2, Reasons = [.. reasons],
        };
        FrontendReviewEnginePresentation.ServerUnavailableReason(FrontendQualityEngineId.PassiveSecurity, Status(true, false)).Should().Be("Unavailable — not enabled on server");
        FrontendReviewEnginePresentation.ServerUnavailableReason(FrontendQualityEngineId.PassiveSecurity, Status(false, true)).Should().Contain("not allowed on this BirkNext installation");
        FrontendReviewEnginePresentation.ServerUnavailableReason(FrontendQualityEngineId.PassiveSecurity, Status(true, true, FrontendQualityEngineUnavailableReasonDto.TargetNotTrusted)).Should().Contain("not registered as trusted");
        FrontendReviewEnginePresentation.ServerUnavailableReason(FrontendQualityEngineId.PassiveSecurity, Status(true, true)).Should().BeNull();
        FrontendReviewEnginePresentation.ServerUnavailableReason(FrontendQualityEngineId.PassiveSecurity, null).Should().BeNull("unknown is not claimed as unavailable");
        FrontendReviewEnginePresentation.ServerUnavailableReason(FrontendQualityEngineId.Lighthouse, Status(true, false)).Should().BeNull();
        new FrontendAnalysisFeatureToggles().EnablePassiveSecurityEngine.Should().BeFalse("a new Target Environment starts with ZAP off");
    }

    [Fact]
    public void SecurityExpectationFields_SayWhoReadsThem()
    {
        SecurityExpectationUsage.Consumers(SecurityExpectationField.SecurityHeader).Should().Contain("FQR Static Security").And.Contain("API Quality Review").And.Contain("presence only");
        SecurityExpectationUsage.Consumers(SecurityExpectationField.TenantId).Should().Be(SecurityExpectationUsage.ConfigurationReviewOnly);
        SecurityExpectationUsage.Consumers(SecurityExpectationField.RedirectUrl).Should().Be(SecurityExpectationUsage.ConfigurationReviewOnly);
        SecurityExpectationUsage.Consumers(SecurityExpectationField.ClientId).Should().Be(SecurityExpectationUsage.ConfigurationReviewOnly);
        SecurityExpectationUsage.RuntimeConsumed(SecurityExpectationField.Authority).Should().BeTrue();
        SecurityExpectationUsage.Consumers(SecurityExpectationField.RestHost).Should().Contain("proxy scope");
    }
}
