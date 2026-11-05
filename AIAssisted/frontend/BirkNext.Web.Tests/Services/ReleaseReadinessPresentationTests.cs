using BirkNext.Governance;
using BirkNext.Interpretation;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// NEXT49: what blocks the release is listed once per root cause (the M2LB identity conflict appears in Governance and Open review decisions — one
/// cause, both dimensions, a link to the exact review item); an unresolved scope is its own cause; next steps follow prerequisites; imported TRX
/// results are reported as found, never as execution of the release scope.
/// </summary>
public sealed class ReleaseReadinessPresentationTests
{
    static ReadinessDimension Dim(ReadinessDimensionKey key, string title, GateDecision state, string? next = null, IReadOnlyList<string>? evidence = null,
        DimensionApplicability applicability = DimensionApplicability.Required) =>
        new(key, title, applicability, state, $"{title} summary", evidence ?? [], next, "somewhere");

    static ReleaseReadinessAssessment M2lbLanguage() => new(new ReleaseScopeReference("ci-live", "Whole project", [], Guid.NewGuid()), GateDecision.Blocked, "Blocked by governance.",
        [
            Dim(ReadinessDimensionKey.ScopeClarity, "Scope", GateDecision.NotAssessed, "Analyze relationships with the Delivery snapshot included."),
            Dim(ReadinessDimensionKey.Requirements, "Requirements", GateDecision.NotAssessed, "Resolve the release scope first."),
            Dim(ReadinessDimensionKey.Traceability, "Traceability", GateDecision.NotAssessed),
            Dim(ReadinessDimensionKey.TestDesign, "Test design", GateDecision.NotAssessed),
            Dim(ReadinessDimensionKey.Governance, "Governance", GateDecision.Blocked, "Open the governance findings in Relationship Review.", ["Blocking · Identity conflict — SkoleAdapter"]),
            Dim(ReadinessDimensionKey.OpenDecisions, "Open review decisions", GateDecision.ReadyWithLimitations, "Answer them in Relationship Review.", ["Identity conflict — SkoleAdapter: Utdanning → SkoleAdapter"]),
            Dim(ReadinessDimensionKey.Performance, "Performance", GateDecision.NotAssessed, applicability: DimensionApplicability.Optional),
        ], ["Identity conflict — SkoleAdapter: Utdanning → SkoleAdapter"], [], "release/1", "governance/1", DateTimeOffset.UnixEpoch);

    [Fact]
    public void Each_root_cause_is_listed_once_with_every_dimension_it_affects()
    {
        var causes = ReleaseReadinessPresentation.BlockingCauses(M2lbLanguage());

        causes.Select(c => c.Key).Should().Equal("scope", "identity-conflict");
        var conflict = causes.Single(c => c.Key == "identity-conflict");
        conflict.Dimensions.Should().Equal("Governance", "Open review decisions");
        conflict.Href.Should().Be("correlation-review?tab=review&type=IdentityConflict", "the link opens the exact review item");
        conflict.Detail.Should().Contain("deferring does not clear it");
        causes.Single(c => c.Key == "scope").Dimensions.Should().Equal("Scope", "Requirements", "Traceability", "Test design");
    }

    [Fact]
    public void Gaps_an_unresolved_scope_explains_are_not_repeated_and_optional_dimensions_are_never_required_gaps()
    {
        ReleaseReadinessPresentation.RequiredGaps(M2lbLanguage()).Should().BeEmpty("the scope cause explains them; Performance is optional");
    }

    [Fact]
    public void Next_steps_follow_prerequisites_scope_first()
    {
        var steps = ReleaseReadinessPresentation.NextSteps(M2lbLanguage());
        steps.Select(s => s.Title).Should().Equal("Scope", "Governance", "Open review decisions", "Requirements");
        steps.Single(s => s.Title == "Governance").Href.Should().Be("correlation-review?tab=review&type=IdentityConflict");
    }

    [Fact]
    public void Imported_results_are_counted_as_found_and_none_means_no_evidence()
    {
        ReleaseReadinessPresentation.Execution([], null).Should().BeNull();
        var run = ReleaseReadinessPresentation.Execution(
        [
            new SddTestExecutionEvidence { Result = "Passed" }, new SddTestExecutionEvidence { Result = "Passed" }, new SddTestExecutionEvidence { Result = "Failed" },
            new SddTestExecutionEvidence { Result = "Skipped" }, new SddTestExecutionEvidence { Result = "Passed", EvidenceKind = "Discovered" },
        ], null);
        run.Should().Be(new ReleaseExecutionEvidence(4, 2, 1, 1, null), "a discovered test is not an executed one");
    }
}
