using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class SpecComparisonServiceTests
{
    private readonly SpecComparisonService _service = new();

    [Fact]
    public void Compare_matches_requirements_by_extracted_id_and_marks_modified()
    {
        var oldRequirement = Candidate("FR-001: The system MUST allow password login", ScenarioKind.Requirement);
        var newRequirement = Candidate("FR-001: The system MUST allow passwordless login", ScenarioKind.Requirement);

        var result = _service.Compare(
            Result([oldRequirement]),
            Result([newRequirement]));

        result.Summary.ModifiedRequirements.Should().Be(1);
        result.Summary.AddedRequirements.Should().Be(0);
        result.Summary.RemovedRequirements.Should().Be(0);
        result.RequirementDeltas.Should().ContainSingle(d =>
            d.Status == SpecDeltaStatus.Modified &&
            d.OldCandidate == oldRequirement &&
            d.NewCandidate == newRequirement);
    }

    [Fact]
    public void Compare_detects_added_removed_and_unchanged_items_deterministically()
    {
        var oldRequirement = Candidate("FR-001: The system MUST allow login", ScenarioKind.Requirement);
        var removedRequirement = Candidate("FR-002: The system MUST export invoices", ScenarioKind.Requirement);
        var unchangedTest = Candidate("Given valid credentials When submitted Then login succeeds", ScenarioKind.Test, "Login");
        var removedClarification = Candidate("Who owns invoice exports?", ScenarioKind.NeedsClarification);

        var newRequirement = Candidate("FR-001: The system MUST allow login", ScenarioKind.Requirement);
        var addedRequirement = Candidate("FR-003: The system MUST support SSO", ScenarioKind.Requirement);
        var unchangedTestNew = Candidate("Given valid credentials When submitted Then login succeeds", ScenarioKind.Test, "Login");
        var addedClarification = Candidate("What is the SSO timeout?", ScenarioKind.NeedsClarification);

        var first = _service.Compare(
            Result([oldRequirement, removedRequirement, unchangedTest, removedClarification]),
            Result([newRequirement, addedRequirement, unchangedTestNew, addedClarification]));
        var second = _service.Compare(
            Result([oldRequirement, removedRequirement, unchangedTest, removedClarification]),
            Result([newRequirement, addedRequirement, unchangedTestNew, addedClarification]));

        first.Summary.AddedRequirements.Should().Be(1);
        first.Summary.RemovedRequirements.Should().Be(1);
        first.Summary.UnchangedRequirements.Should().Be(1);
        first.Summary.AddedTests.Should().Be(0);
        first.Summary.RemovedTests.Should().Be(0);
        first.Summary.AddedClarifications.Should().Be(1);
        first.Summary.RemovedClarifications.Should().Be(1);
        first.RequirementDeltas.Select(d => d.Status)
            .Should().Equal(second.RequirementDeltas.Select(d => d.Status));
        first.RequirementDeltas.Select(d => d.MatchKey)
            .Should().Equal(second.RequirementDeltas.Select(d => d.MatchKey));
    }

    [Fact]
    public void Compare_uses_manual_links_for_impacted_tests_and_removed_requirement_hints()
    {
        var requirement = Candidate("FR-001: The system MUST allow login", ScenarioKind.Requirement);
        var test = Candidate("Given valid credentials When submitted Then login succeeds", ScenarioKind.Test);
        var newRequirement = Candidate("FR-001: The system MUST allow login with MFA", ScenarioKind.Requirement);
        var links = new List<CandidateLinkEntry>
        {
            new(requirement.CandidateId, test.CandidateId, CandidateLinkType.RequirementTest),
        };

        var result = _service.Compare(
            Result([requirement, test]),
            Result([newRequirement, test]),
            links);

        result.Summary.ModifiedRequirements.Should().Be(1);
        result.Summary.PotentiallyImpactedTests.Should().Be(1);
        result.RequirementDeltas.Single(d => d.Status == SpecDeltaStatus.Modified)
            .ImpactHints.Should().Contain("1 linked test(s) may need review");
    }

    [Fact]
    public void Compare_counts_unresolved_clarification_risk()
    {
        var oldClarification = Candidate("What is the retention period?", ScenarioKind.NeedsClarification);
        oldClarification.ReviewStatus = CandidateReviewStatus.NeedsReview;
        var stillUnresolved = Candidate("What is the retention period?", ScenarioKind.NeedsClarification);
        stillUnresolved.ReviewStatus = CandidateReviewStatus.New;
        var addedClarification = Candidate("Who approves fallback behavior?", ScenarioKind.NeedsClarification);
        addedClarification.ReviewStatus = CandidateReviewStatus.NeedsReview;
        var acceptedClarification = Candidate("Which countries are in scope?", ScenarioKind.NeedsClarification);
        acceptedClarification.ReviewStatus = CandidateReviewStatus.Accepted;

        var result = _service.Compare(
            Result([oldClarification]),
            Result([stillUnresolved, addedClarification, acceptedClarification]));

        result.Summary.StillUnresolvedClarifications.Should().Be(1);
        result.Summary.NewClarificationRisks.Should().Be(1);
        result.Summary.AddedClarifications.Should().Be(2);
    }

    [Fact]
    public void Compare_counts_uncovered_added_requirements_without_same_context_tests()
    {
        var oldRequirement = Candidate("FR-001: The system MUST allow login", ScenarioKind.Requirement, "Login");
        var newRequirement = Candidate("FR-001: The system MUST allow login", ScenarioKind.Requirement, "Login");
        var coveredAdded = Candidate("FR-002: The system MUST lock accounts", ScenarioKind.Requirement, "Security");
        var uncoveredAdded = Candidate("FR-003: The system MUST export invoices", ScenarioKind.Requirement, "Billing");
        var securityTest = Candidate("Given failed attempts When threshold is reached Then account locks", ScenarioKind.Test, "Security");

        var result = _service.Compare(
            Result([oldRequirement]),
            Result([newRequirement, coveredAdded, uncoveredAdded, securityTest]));

        result.Summary.AddedRequirements.Should().Be(2);
        result.Summary.UncoveredRequirements.Should().Be(1);
    }

    private static ExtractionCandidate Candidate(
        string title,
        ScenarioKind kind,
        string? contextHeading = null) => new()
        {
            Title = title,
            Classification = kind,
            ContextHeading = contextHeading,
            ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
            SourceBlockType = BlockType.UnorderedListItem,
        };

    private static ExtractionPipelineResult Result(IReadOnlyList<ExtractionCandidate> candidates)
    {
        return ExtractionPipelineResult.Success(
            candidates,
            inputLengthChars: 100,
            inputLineCount: 5,
            durationMs: 10,
            requirementCount: candidates.Count(c => c.Classification == ScenarioKind.Requirement),
            testCount: candidates.Count(c => c.Classification == ScenarioKind.Test),
            needsClarificationCount: candidates.Count(c => c.Classification == ScenarioKind.NeedsClarification));
    }
}
