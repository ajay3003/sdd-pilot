using BirkNext.Applicability;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.SourceAnalysis;

/// <summary>Tool coverage ≠ project quality: neutral outcomes never enter a quality denominator and are never converted to 0.</summary>
public sealed class ScoreSemanticsTests
{
    [Fact]
    public void Denominator_HoldsOnlyAssessedChecks()
    {
        // 10 checks: 4 unsupported, 2 not applicable, 4 executed (3 pass, 1 fail).
        var outcomes = Enumerable.Repeat(CheckOutcome.Unsupported, 4).Concat(Enumerable.Repeat(CheckOutcome.NotApplicable, 2))
            .Concat([CheckOutcome.Pass, CheckOutcome.Pass, CheckOutcome.Pass, CheckOutcome.Fail]);
        var q = ScoreSemantics.Compute(outcomes);
        q.Denominator.Should().Be(4);
        q.QualityPercent.Should().Be(75);
        q.Coverage.TotalChecks.Should().Be(10);
        q.Coverage.ApplicableChecks.Should().Be(4);
        q.Coverage.AssessmentCoveragePercent.Should().Be(100);
    }

    [Fact]
    public void NotAssessed_LowersCoverage_NotQuality()
    {
        var q = ScoreSemantics.Compute([CheckOutcome.Pass, CheckOutcome.NotTested, CheckOutcome.NotAssessed, CheckOutcome.Unavailable]);
        q.QualityPercent.Should().Be(100);
        q.Denominator.Should().Be(1);
        q.Coverage.NotAssessedChecks.Should().Be(3);
        q.Coverage.AssessmentCoveragePercent.Should().Be(25);
    }

    [Fact]
    public void NothingAssessed_HasNoQuality_NeverZero()
    {
        var q = ScoreSemantics.Compute([CheckOutcome.Unsupported, CheckOutcome.NotApplicable, CheckOutcome.NotTested]);
        q.QualityPercent.Should().BeNull();
        q.Basis.Should().Contain("not computed");
        ScoreSemantics.Compute([]).QualityPercent.Should().BeNull();
    }

    [Fact]
    public void WarningsAndNeedsReview_CountHalf_FailuresAreOnlyFail()
    {
        ScoreSemantics.Compute([CheckOutcome.Pass, CheckOutcome.Warning]).QualityPercent.Should().Be(75);
        ScoreSemantics.Compute([CheckOutcome.NeedsReview, CheckOutcome.Partial]).QualityPercent.Should().Be(50);
        Enum.GetValues<CheckOutcome>().Where(ScoreSemantics.IsFailure).Should().Equal(CheckOutcome.Fail);
        new[] { CheckOutcome.Unsupported, CheckOutcome.NotApplicable, CheckOutcome.Unavailable, CheckOutcome.NotAssessed, CheckOutcome.NotTested }
            .Should().OnlyContain(o => ScoreSemantics.IsNeutral(o));
    }

    [Fact]
    public void OnlyApplicableReviews_EnterQualityAggregates()
    {
        Enum.GetValues<ApplicabilityStatus>().Where(s => !ScoreSemantics.ExcludedFromQuality(s))
            .Should().BeEquivalentTo([ApplicabilityStatus.Applicable, ApplicabilityStatus.PartiallyApplicable]);
        ScoreSemantics.Label(ReviewExecutionState.FailedToExecute).Should().Contain("not a project-quality result");
    }
}
