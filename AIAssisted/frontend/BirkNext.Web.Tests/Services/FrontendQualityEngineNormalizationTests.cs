using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class FrontendQualityEngineNormalizationTests
{
    [Fact]
    [Trait("Category", "FrontendQualityAggregateGuard")]
    public async Task AllSixEnabledAndAssessed_ProduceExactlyOneOutcomeEach()
    {
        var fixture = new Fixture();
        var result = await fixture.RunAsync(AllEnabled());

        var expected = Enum.GetValues<FrontendQualityEngineId>();
        var actual = result.QualityReport!.EngineOutcomes.Select(o => o.EngineId).ToArray();
        var missing = expected.Except(actual).ToArray();
        var duplicates = actual.GroupBy(id => id).Where(group => group.Count() > 1).Select(group => group.Key).ToArray();
        var diagnostics = $"Expected=[{string.Join(",", expected)}]; Actual=[{string.Join(",", actual)}]; " +
                          $"Missing=[{string.Join(",", missing)}]; Duplicates=[{string.Join(",", duplicates)}]; " +
                          $"Coverage={result.QualityReport.Coverage?.RequiredCoverageState}; " +
                          $"ReleaseDisposition={result.QualityReport.ReleaseDisposition}";

        actual.Should().HaveCount(expected.Length, diagnostics);
        actual.Should().BeEquivalentTo(expected, diagnostics);
        duplicates.Should().BeEmpty(diagnostics);
        result.QualityReport.EngineOutcomes.Should().OnlyContain(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed);
        fixture.Runtime.CallCount.Should().Be(1);
        fixture.Accessibility.CallCount.Should().Be(1);
        fixture.Lighthouse.CallCount.Should().Be(1);
        fixture.PassiveSecurity.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task OptionalDisabledEngines_RemainVisibleWithoutReducingRequiredCoverage()
    {
        var fixture = new Fixture();
        var context = AllEnabled();
        context.FeatureToggles.EnableBrowserRuntimeEngine = false;
        context.FeatureToggles.EnableAccessibilityEngine = false;
        context.FeatureToggles.EnableLighthouseEngine = false;
        context.FeatureToggles.EnablePassiveSecurityEngine = false;

        var report = (await fixture.RunAsync(context)).QualityReport!;

        report.EngineOutcomes.Should().HaveCount(6);
        report.EngineOutcomes.Where(o => o.Requirement == FrontendQualityEngineRequirement.Optional)
            .Should().OnlyContain(o => o.ExecutionState == FrontendQualityEngineExecutionState.Disabled);
        report.Coverage!.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed);
        report.Completeness.Should().Be(AssessmentCompleteness.Full);
    }

    [Fact]
    public async Task BrowserRuntime_SourceReportAndThreeSanitizedFindings_SurviveExactlyOnce()
    {
        const string sentinel = "SECRET-PHASE2E-NORMALIZATION-12345";
        var fixture = new Fixture
        {
            Runtime = new RuntimeSpy(new BrowserRuntimeResultDto(
                BrowserRuntimeEngineStatusDto.Assessed, BrowserName: "Chromium", BrowserVersion: "130",
                RequestedUrl: "https://example.com", FinalUrl: "https://example.com/home",
                Findings:
                [
                    RuntimeFinding("console", "ConsoleError", BrowserRuntimeFindingSeverityDto.Medium, sentinel),
                    RuntimeFinding("resource", "ResourceFailure", BrowserRuntimeFindingSeverityDto.High, sentinel),
                    RuntimeFinding("wasm", "PageError", BrowserRuntimeFindingSeverityDto.Critical, sentinel),
                ], Limitations: ["Single startup observation"]))
        };

        var report = (await fixture.RunAsync(AllEnabled())).QualityReport!;

        report.BrowserRuntimeReport.Should().NotBeNull();
        report.EngineOutcomes.Single(o => o.EngineId == FrontendQualityEngineId.BrowserRuntime).ExecutionState
            .Should().Be(FrontendQualityEngineExecutionState.Assessed);
        report.AssessedEngines.Should().Contain("Browser Runtime");
        var runtimeFindings = report.Findings.Where(f => f.SourceSystem == "Browser Runtime").ToList();
        runtimeFindings.Should().HaveCount(3);
        report.LogicalIssues.SelectMany(issue => issue.FindingInstances)
            .Should().HaveCount(report.Findings.Count);
        runtimeFindings.Select(f => f.Id).Should().OnlyHaveUniqueItems();
        runtimeFindings.Should().Contain(f => f.Severity == FrontendQualitySeverity.Critical);
        runtimeFindings.Should().Contain(f => f.Category == FrontendQualityCategory.Performance);
        JsonSerializer.Serialize(report).Should().NotContain(sentinel);
    }

    [Fact]
    public async Task BrowserRuntimeEngineError_IsolatedAndHasNoScore()
    {
        var fixture = new Fixture
        {
            Runtime = new RuntimeSpy(new BrowserRuntimeResultDto(BrowserRuntimeEngineStatusDto.EngineError,
                EngineError: "runtime unavailable"))
        };
        var context = AllEnabled();
        context.EngineRequirements.BrowserRuntime = FrontendQualityEngineRequirement.Required;

        var report = (await fixture.RunAsync(context)).QualityReport!;

        report.EngineOutcomes.Should().HaveCount(6);
        report.EngineOutcomes.Single(o => o.EngineId == FrontendQualityEngineId.BrowserRuntime).ExecutionState
            .Should().Be(FrontendQualityEngineExecutionState.EngineError);
        report.EngineOutcomes.Where(o => o.EngineId != FrontendQualityEngineId.BrowserRuntime)
            .Should().OnlyContain(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed);
        report.FailedEngines.Should().Contain("Browser Runtime");
        report.Completeness.Should().Be(AssessmentCompleteness.Partial);
        typeof(FrontendQualityEngineOutcome).GetProperties().Should().NotContain(p => p.Name.EndsWith("Score", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(AccessibilityExecutionStatusDto.Assessed, FrontendQualityEngineExecutionState.Assessed)]
    [InlineData(AccessibilityExecutionStatusDto.AuthenticationRequired, FrontendQualityEngineExecutionState.Unavailable)]
    [InlineData(AccessibilityExecutionStatusDto.EngineError, FrontendQualityEngineExecutionState.EngineError)]
    [InlineData(AccessibilityExecutionStatusDto.Skipped, FrontendQualityEngineExecutionState.SafetyBlocked)]
    public void AccessibilityStatesAndMetadata_MapPrecisely(
        AccessibilityExecutionStatusDto source, FrontendQualityEngineExecutionState expected)
    {
        var outcome = FrontendQualityEngineOutcomeNormalizer.Accessibility("https://example.com", true, Policy(),
            new AccessibilityResultDto(source, AxeVersion: "4.13", BrowserName: "Chromium", BrowserVersion: "130",
                Findings: [], Limitations: ["manual testing required"], EngineError: source == AccessibilityExecutionStatusDto.EngineError ? "failure" : null),
            null, true);

        outcome.ExecutionState.Should().Be(expected);
        outcome.ToolVersion.Should().Be("4.13");
        outcome.BrowserVersion.Should().Be("130");
        outcome.ManualTestingObligations.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(LighthouseExecutionStatusDto.Assessed, FrontendQualityEngineExecutionState.Assessed)]
    [InlineData(LighthouseExecutionStatusDto.TimedOut, FrontendQualityEngineExecutionState.TimedOut)]
    [InlineData(LighthouseExecutionStatusDto.AuthenticationRequired, FrontendQualityEngineExecutionState.NotApplicable)]
    [InlineData(LighthouseExecutionStatusDto.EngineError, FrontendQualityEngineExecutionState.EngineError)]
    [InlineData(LighthouseExecutionStatusDto.Skipped, FrontendQualityEngineExecutionState.SafetyBlocked)]
    public void LighthouseStatesAndMetadata_MapPrecisely(
        LighthouseExecutionStatusDto source, FrontendQualityEngineExecutionState expected)
    {
        var outcome = FrontendQualityEngineOutcomeNormalizer.Lighthouse("https://example.com", true, Policy(),
            new LighthouseResultDto(source, LighthouseVersion: "12.2", NodeVersion: "v24", BrowserName: "Chromium",
                BrowserVersion: "130", Audits: [], Metrics: [], Limitations: ["Lab only"]), null, true);
        outcome.ExecutionState.Should().Be(expected);
        outcome.ToolVersion.Should().Be("12.2");
        outcome.ToolName.Should().Contain("Node v24");
        outcome.BrowserVersion.Should().Be("130");
    }

    [Theory]
    [InlineData(PassiveSecurityExecutionStatusDto.Assessed, null, FrontendQualityEngineExecutionState.Assessed)]
    [InlineData(PassiveSecurityExecutionStatusDto.TimedOut, null, FrontendQualityEngineExecutionState.TimedOut)]
    [InlineData(PassiveSecurityExecutionStatusDto.AuthenticationRequired, null, FrontendQualityEngineExecutionState.NotApplicable)]
    [InlineData(PassiveSecurityExecutionStatusDto.EngineError, null, FrontendQualityEngineExecutionState.EngineError)]
    [InlineData(PassiveSecurityExecutionStatusDto.Skipped, "engine is disabled", FrontendQualityEngineExecutionState.Unavailable)]
    [InlineData(PassiveSecurityExecutionStatusDto.Skipped, "target blocked", FrontendQualityEngineExecutionState.SafetyBlocked)]
    public void PassiveSecurityStatesAndMetadata_MapPrecisely(
        PassiveSecurityExecutionStatusDto source, string? error, FrontendQualityEngineExecutionState expected)
    {
        var outcome = FrontendQualityEngineOutcomeNormalizer.PassiveSecurity("https://example.com", true, Policy(),
            Passive(source, error, "2.16.1"), null, true);
        outcome.ExecutionState.Should().Be(expected);
        outcome.ToolVersion.Should().Be("2.16.1");
        outcome.Limitations.Should().Contain("Passive only");
    }

    [Fact]
    public void DisabledUnavailableCancelledAndPreflightStates_RemainDistinct()
    {
        FrontendQualityEngineOutcomeNormalizer.BrowserRuntime("x", false, Policy(), null, null, true).ExecutionState
            .Should().Be(FrontendQualityEngineExecutionState.Disabled);
        FrontendQualityEngineOutcomeNormalizer.BrowserRuntime("x", true, Policy(), null, null, false).ExecutionState
            .Should().Be(FrontendQualityEngineExecutionState.Unavailable);
        FrontendQualityEngineOutcomeNormalizer.BrowserRuntime("x", true, Policy(), null, null, true, true).ExecutionState
            .Should().Be(FrontendQualityEngineExecutionState.Cancelled);

        var context = AllEnabled();
        FrontendQualityEngineOutcomeNormalizer.PreflightBlocked("x", context, PreflightStatus.InvalidTarget, "blocked")
            .Should().OnlyContain(o => o.ExecutionState == FrontendQualityEngineExecutionState.SafetyBlocked);
        FrontendQualityEngineOutcomeNormalizer.PreflightBlocked("x", context, PreflightStatus.AuthenticationRequired, "auth")
            .Should().OnlyContain(o => o.ExecutionState == FrontendQualityEngineExecutionState.Unavailable);
    }

    [Theory]
    [InlineData(FrontendQualityEngineId.BrowserRuntime)]
    [InlineData(FrontendQualityEngineId.Accessibility)]
    [InlineData(FrontendQualityEngineId.Lighthouse)]
    [InlineData(FrontendQualityEngineId.PassiveSecurity)]
    public async Task OptionalToolFailure_DoesNotEraseOtherOutcomesOrExecuteTwice(FrontendQualityEngineId failed)
    {
        var fixture = new Fixture();
        if (failed == FrontendQualityEngineId.BrowserRuntime)
            fixture.Runtime = new RuntimeSpy(new(BrowserRuntimeEngineStatusDto.EngineError, EngineError: "failure"));
        if (failed == FrontendQualityEngineId.Accessibility)
            fixture.Accessibility = new AccessibilitySpy(new(AccessibilityExecutionStatusDto.EngineError, EngineError: "failure"));
        if (failed == FrontendQualityEngineId.Lighthouse)
            fixture.Lighthouse = new LighthouseSpy(new(LighthouseExecutionStatusDto.EngineError, EngineError: "failure"));
        if (failed == FrontendQualityEngineId.PassiveSecurity)
            fixture.PassiveSecurity = new PassiveSecuritySpy(Passive(PassiveSecurityExecutionStatusDto.EngineError, "failure"));

        var report = (await fixture.RunAsync(AllEnabled())).QualityReport!;

        report.EngineOutcomes.Should().HaveCount(6);
        report.EngineOutcomes.Single(o => o.EngineId == failed).ExecutionState.Should().Be(FrontendQualityEngineExecutionState.EngineError);
        report.EngineOutcomes.Where(o => o.EngineId != failed).Should().OnlyContain(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed);
        fixture.Runtime.CallCount.Should().Be(1);
        fixture.Accessibility.CallCount.Should().Be(1);
        fixture.Lighthouse.CallCount.Should().Be(1);
        fixture.PassiveSecurity.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task SessionAndSerialization_PreserveSixOutcomesCoverageRuntimeAndLegacyProjection()
    {
        var fixture = new Fixture();
        var context = AllEnabled();
        var report = (await fixture.RunAsync(context)).QualityReport!;
        var session = new RuntimeReviewSessionService();
        session.SaveQualityResult(report, context);

        session.QualityReview.Report.Should().BeSameAs(report);
        var restored = JsonSerializer.Deserialize<FrontendQualityReviewReport>(JsonSerializer.Serialize(session.QualityReview.Report));
        restored!.EngineOutcomes.Should().HaveCount(6);
        restored.BrowserRuntimeReport.Should().NotBeNull();
        restored.Coverage!.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed);
        restored.AssessedEngines.Should().HaveCount(6);
    }

    private static BrowserRuntimeFindingDto RuntimeFinding(string id, string category,
        BrowserRuntimeFindingSeverityDto severity, string evidence) =>
        new(id, id, severity, category, $"description {evidence}", "recommendation", [evidence]);

    private static PassiveSecurityResultDto Passive(PassiveSecurityExecutionStatusDto state, string? error = null, string? version = null) =>
        new(state, "ZAP Passive", "Passive", version, "https://example.com", "https://example.com", DateTime.UtcNow,
            DateTime.UtcNow, 10, 0, 0, 0, 0, [], ["Passive only"], error, "Configured target", null);

    private static FrontendQualityEngineRequirementPolicy Policy() => new FrontendQualityEngineRequirementSettings().ToPolicy();

    private static FrontendAnalysisContext AllEnabled() => new()
    {
        TargetUrl = "https://example.com",
        ActiveProfile = new() { Id = "trusted", TargetUrl = "https://example.com", Performance = new() },
        FeatureToggles = new()
        {
            EnableSecurityEngine = true, EnablePerformanceEngine = true, EnableBrowserRuntimeEngine = true,
            EnableAccessibilityEngine = true, EnableLighthouseEngine = true, EnablePassiveSecurityEngine = true,
        },
        EngineRequirements = new(),
    };

    private sealed class Fixture
    {
        public RuntimeSpy Runtime { get; set; } = new(new(BrowserRuntimeEngineStatusDto.Assessed,
            BrowserName: "Chromium", BrowserVersion: "130", RequestedUrl: "https://example.com", Findings: []));
        public AccessibilitySpy Accessibility { get; set; } = new(new(AccessibilityExecutionStatusDto.Assessed,
            AxeVersion: "4.13", BrowserName: "Chromium", BrowserVersion: "130", Findings: [], Limitations: ["manual"]));
        public LighthouseSpy Lighthouse { get; set; } = new(new(LighthouseExecutionStatusDto.Assessed,
            LighthouseVersion: "12.2", NodeVersion: "v24", BrowserName: "Chromium", BrowserVersion: "130",
            Metrics: [], Audits: [], Limitations: ["Lab only"]));
        public PassiveSecuritySpy PassiveSecurity { get; set; } = new(Passive(PassiveSecurityExecutionStatusDto.Assessed, version: "2.16.1"));

        public Task<FrontendQualityReviewOrchestrationResult> RunAsync(FrontendAnalysisContext context) =>
            OrchestrationTestHelpers.CreateOrchestrator(new SecuritySpy(), new PerformanceSpy(), new Preflight(),
                new FrontendQualityReviewService(), Runtime, Accessibility, Lighthouse, PassiveSecurity)
                .RunAsync(context.TargetUrl, context);
    }

    private sealed class SecuritySpy : ISecurityScanner
    {
        public Task<(WasmSecurityReviewReport?, string?)> ScanAsync(WasmScanRequest request) =>
            Task.FromResult<(WasmSecurityReviewReport?, string?)>((new()
            {
                TargetUrl = request.TargetUrl, ScannedAt = DateTime.UtcNow, Health = new() { Score = 90 }, Limitations = ["Static only"]
            }, null));
    }

    private sealed class PerformanceSpy : IBlazorWasmPerformanceReviewService
    {
        public Task<WasmPerformanceReviewReport> RunReviewAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new WasmPerformanceReviewReport
            { TargetUrl = targetUrl, ReviewedAt = DateTime.UtcNow, Health = new() { Score = 90 }, Limitations = ["Passive only"] });
        public Task<WasmAssetDiscoveryResult> DiscoverAssetsAsync(string targetUrl, FrontendPerformanceThresholds? thresholds = null,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public WasmPerformanceReviewReport? GetCached() => null;
        public void ClearCache() { }
    }

    private sealed class RuntimeSpy(BrowserRuntimeResultDto result) : IFrontendBrowserRuntimeReviewApiService
    {
        public int CallCount { get; private set; }
        public Task<BrowserRuntimeResultDto> ReviewAsync(string targetUrl, int navigationTimeoutMs = 30000,
            int startupObservationMs = 5000, CancellationToken cancellationToken = default)
        { CallCount++; return Task.FromResult(result); }
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class AccessibilitySpy(AccessibilityResultDto result) : IFrontendAccessibilityReviewApiService
    {
        public int CallCount { get; private set; }
        public Task<AccessibilityResultDto> ReviewAsync(string targetUrl, string environmentType, bool requiresAuthentication,
            CancellationToken cancellationToken = default)
        { CallCount++; return Task.FromResult(result); }
    }

    private sealed class LighthouseSpy(LighthouseResultDto result) : IFrontendLighthouseReviewApiService
    {
        public int CallCount { get; private set; }
        public Task<LighthouseResultDto> ReviewAsync(string targetUrl, bool requiresAuthentication,
            CancellationToken cancellationToken = default)
        { CallCount++; return Task.FromResult(result); }
    }

    private sealed class PassiveSecuritySpy(PassiveSecurityResultDto result) : IFrontendPassiveSecurityApiService
    {
        public int CallCount { get; private set; }
        public Task<PassiveSecurityResultDto> ReviewAsync(string targetUrl, string profileId, string configuredBaseUrl,
            string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default)
        { CallCount++; return Task.FromResult(result); }
    }

    private sealed class Preflight : ITargetPreflightService
    {
        public Task<TargetPreflightResult> CheckTargetAsync(string targetUrl) => Task.FromResult(new TargetPreflightResult
        { Status = PreflightStatus.Ready, Message = "Ready", FinalUrl = targetUrl });
    }
}
