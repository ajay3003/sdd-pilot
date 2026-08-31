using System.Reflection;
using System.Text.Json;
using BirkNext.Web.Models;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class FrontendQualityCoverageContractTests
{
    public static IEnumerable<object[]> TerminalStates() =>
        Enum.GetValues<FrontendQualityEngineExecutionState>().Select(state => new object[] { state });

    [Theory]
    [MemberData(nameof(TerminalStates))]
    public void ExecutionState_RoundTripsWithoutCollapsing(FrontendQualityEngineExecutionState state)
    {
        var outcome = Outcome(FrontendQualityEngineId.BrowserRuntime, state);

        var json = JsonSerializer.Serialize(outcome);
        var restored = JsonSerializer.Deserialize<FrontendQualityEngineOutcome>(json);

        restored.Should().NotBeNull();
        restored!.ExecutionState.Should().Be(state);
        json.Should().Contain($"\"executionState\":\"{state}\"");
    }

    [Fact]
    public void EngineIds_AreUniqueStableAndIndependentOfDisplayName()
    {
        var values = Enum.GetValues<FrontendQualityEngineId>();
        values.Should().OnlyHaveUniqueItems();
        values.Select(v => v.ToString()).Should().Equal(
            "StaticSecurity", "PassivePerformance", "BrowserRuntime",
            "Accessibility", "Lighthouse", "PassiveSecurity");

        var outcome = Outcome(FrontendQualityEngineId.BrowserRuntime,
            FrontendQualityEngineExecutionState.Assessed) with { DisplayName = "Renamed browser presentation" };
        outcome.EngineId.Should().Be(FrontendQualityEngineId.BrowserRuntime);
        JsonSerializer.Serialize(outcome).Should().Contain("\"engineId\":\"BrowserRuntime\"");
    }

    [Fact]
    public void RequirementPolicy_RequiresExplicitMapping()
    {
        var policy = new FrontendQualityEngineRequirementPolicy(new Dictionary<FrontendQualityEngineId, FrontendQualityEngineRequirement>
        {
            [FrontendQualityEngineId.StaticSecurity] = FrontendQualityEngineRequirement.Required,
        });

        policy.GetRequirement(FrontendQualityEngineId.StaticSecurity).Should().Be(FrontendQualityEngineRequirement.Required);
        var action = () => policy.GetRequirement(FrontendQualityEngineId.Lighthouse);
        action.Should().Throw<InvalidOperationException>().WithMessage("*No explicit requirement*");
    }

    [Fact]
    public void OptionalDisabled_DoesNotReduceRequiredCoverage()
    {
        var coverage = FrontendQualityCoverage.Evaluate([
            Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineExecutionState.Assessed),
            Outcome(FrontendQualityEngineId.Lighthouse, FrontendQualityEngineExecutionState.Disabled,
                FrontendQualityEngineRequirement.Optional),
        ]);

        coverage.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed);
        coverage.ToLegacyCompleteness().Should().Be(AssessmentCompleteness.Full);
    }

    [Theory]
    [InlineData(FrontendQualityEngineExecutionState.Disabled)]
    [InlineData(FrontendQualityEngineExecutionState.Unavailable)]
    [InlineData(FrontendQualityEngineExecutionState.EngineError)]
    public void RequiredUnassessedEngine_CannotYieldFull(FrontendQualityEngineExecutionState state)
    {
        var coverage = FrontendQualityCoverage.Evaluate([
            Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineExecutionState.Assessed),
            Outcome(FrontendQualityEngineId.BrowserRuntime, state),
        ]);

        coverage.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.SomeRequiredNotAssessed);
        coverage.ToLegacyCompleteness().Should().Be(AssessmentCompleteness.Partial);
    }

    [Fact]
    public void NoRequiredAssessment_IsFailedCompatibilityCoverage()
    {
        var coverage = FrontendQualityCoverage.Evaluate([
            Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineExecutionState.Unavailable),
            Outcome(FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineExecutionState.EngineError),
        ]);

        coverage.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.NoTrustworthyRequiredAssessment);
        coverage.ToLegacyCompleteness().Should().Be(AssessmentCompleteness.Failed);
    }

    [Fact]
    public void TypedOutcomes_DriveLegacyCompatibilityListsAndCompleteness()
    {
        var report = new FrontendQualityReviewReport
        {
            Completeness = AssessmentCompleteness.Full,
            AssessedEngines = ["stale assessed"],
            FailedEngines = ["stale failed"],
            SkippedEngines = ["stale skipped"],
            EngineOutcomes =
            [
                Outcome(FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineExecutionState.Assessed),
                Outcome(FrontendQualityEngineId.Lighthouse, FrontendQualityEngineExecutionState.TimedOut),
                Outcome(FrontendQualityEngineId.Accessibility, FrontendQualityEngineExecutionState.Unavailable,
                    FrontendQualityEngineRequirement.Optional),
                Outcome(FrontendQualityEngineId.PassiveSecurity, FrontendQualityEngineExecutionState.Disabled,
                    FrontendQualityEngineRequirement.Optional),
            ],
        };

        report.AssessedEngines.Should().Equal("Static Security");
        report.FailedEngines.Should().Equal("Lighthouse");
        report.SkippedEngines.Should().Equal("Accessibility", "Passive Security");
        report.Completeness.Should().Be(AssessmentCompleteness.Partial);
        report.EngineOutcomes.Single(o => o.EngineId == FrontendQualityEngineId.Accessibility)
            .ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Unavailable);
    }

    [Fact]
    public void Phase2EContract_AddsNoNumericDecisionOrCompositeScore()
    {
        var forbidden = new[] { "OverallDecisionScore", "ReleaseScore", "ConfidenceScore", "CompositeQualityScore" };
        var phase2EProperties = typeof(FrontendQualityCoverage).GetProperties()
            .Concat(typeof(FrontendQualityEngineOutcome).GetProperties())
            .ToList();
        var decisionProperties = typeof(FrontendQualityReviewReport).GetProperties()
            .Where(p => p.Name.Contains("Decision") || p.Name.Contains("Release")
                || p.Name.Contains("Confidence") || p.Name.Contains("Composite"))
            .ToList();

        phase2EProperties.Concat(decisionProperties).Select(p => p.Name).Should().NotIntersectWith(forbidden);
        phase2EProperties.Should().NotContain(p => p.Name.EndsWith("Score", StringComparison.Ordinal) && IsNumeric(p.PropertyType));
        decisionProperties.Where(p => p.Name is not nameof(FrontendQualityReviewReport.ReleaseDisposition))
            .Should().NotContain(p => IsNumeric(p.PropertyType));
    }

    [Fact]
    public void ApprovedFactory_AppliesSourceEngineSanitizerToFailureReason()
    {
        const string sentinel = "SECRET-PHASE2E-TOKEN-12345";
        var outcome = FrontendQualityEngineOutcome.CreateWithSanitizedFailure(
            FrontendQualityEngineId.BrowserRuntime, "Browser Runtime", true,
            FrontendQualityEngineRequirement.Required, FrontendQualityEngineExecutionState.EngineError,
            $"launch failed: {sentinel}", value => value?.Replace(sentinel, "[REDACTED]", StringComparison.Ordinal));

        outcome.SanitizedFailureReason.Should().Be("launch failed: [REDACTED]");
        JsonSerializer.Serialize(outcome).Should().NotContain(sentinel);
    }

    [Fact]
    public void ReportSerialization_RoundTripsTypedCoverageAndEvidenceSemantics()
    {
        var report = new FrontendQualityReviewReport
        {
            EngineOutcomes =
            [
                Outcome(FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineExecutionState.Assessed) with
                {
                    Evidence = [new FrontendQualityEvidenceDescriptor
                    {
                        Strength = FrontendQualityEvidenceStrength.DirectObservation,
                        Disposition = FrontendQualityReviewDisposition.ManualVerificationRequired,
                        Confidence = FrontendQualityEvidenceConfidence.Moderate,
                    }],
                },
            ],
            ReleaseDisposition = FrontendQualityReleaseDisposition.ReviewRequired,
        };

        var restored = JsonSerializer.Deserialize<FrontendQualityReviewReport>(JsonSerializer.Serialize(report));
        var outcome = restored!.EngineOutcomes.Single();
        outcome.EngineId.Should().Be(FrontendQualityEngineId.BrowserRuntime);
        outcome.Requirement.Should().Be(FrontendQualityEngineRequirement.Required);
        outcome.ExecutionState.Should().Be(FrontendQualityEngineExecutionState.Assessed);
        outcome.Evidence.Single().Strength.Should().Be(FrontendQualityEvidenceStrength.DirectObservation);
        outcome.Evidence.Single().Disposition.Should().Be(FrontendQualityReviewDisposition.ManualVerificationRequired);
        outcome.Evidence.Single().Confidence.Should().Be(FrontendQualityEvidenceConfidence.Moderate);
        restored.Coverage!.RequiredCoverageState.Should().Be(FrontendQualityRequiredCoverageState.AllRequiredAssessed);
    }

    private static FrontendQualityEngineOutcome Outcome(
        FrontendQualityEngineId id,
        FrontendQualityEngineExecutionState state,
        FrontendQualityEngineRequirement requirement = FrontendQualityEngineRequirement.Required) => new()
        {
            EngineId = id,
            DisplayName = id switch
            {
                FrontendQualityEngineId.StaticSecurity => "Static Security",
                FrontendQualityEngineId.PassivePerformance => "Passive Performance",
                FrontendQualityEngineId.BrowserRuntime => "Browser Runtime",
                FrontendQualityEngineId.PassiveSecurity => "Passive Security",
                _ => id.ToString(),
            },
            Enabled = state != FrontendQualityEngineExecutionState.Disabled,
            Requirement = requirement,
            ReadinessState = FrontendQualityEngineReadinessState.NotEvaluated,
            ExecutionState = state,
        };

    private static bool IsNumeric(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long)
            || type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    }
}
