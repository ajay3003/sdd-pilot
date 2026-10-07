using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>Review candidate states must not be presented as traceability coverage.</summary>
public sealed class SpecExplorerCoverageDefectTests : BunitContext
{
    private const string SpecWithRequirements = "# Requirements\n- FR-001: User login\n- FR-002: User logout";

    [Fact]
    public void ParsedRequirementsDoNotShowCoverageCallout()
    {
        var cut = Render<SpecExplorerPanel>(p => p.Add(c => c.InitialSpecMarkdown, SpecWithRequirements));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());
        cut.Markup.Should().Contain("Requirements (2)");
        cut.FindAll("[data-testid='se-coverage-callout'], [data-testid='se-section-health'], [data-testid='se-quick-filters']").Should().BeEmpty();
    }

    [Fact]
    public void CandidateReviewStatusDoesNotProduceCoverageBadge()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            Candidate("FR-001", CandidateReviewStatus.Accepted),
            Candidate("FR-002", CandidateReviewStatus.Rejected),
        ];
        var cut = Render<SpecExplorerPanel>(p => p
            .Add(c => c.InitialSpecMarkdown, SpecWithRequirements)
            .Add(c => c.Candidates, candidates));

        cut.WaitForAssertion(() => cut.FindAll("[role='treeitem']").Should().NotBeEmpty());
        cut.FindAll("[data-testid='se-coverage-callout'], [data-testid='se-section-health']").Should().BeEmpty();
        cut.Markup.Should().NotContain("Healthy");
        cut.Markup.Should().NotContain("Needs Attention");
    }

    private static ExtractionCandidate Candidate(string title, CandidateReviewStatus status) => new()
    {
        Title = title,
        Classification = ScenarioKind.Requirement,
        ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
        SourceBlockType = BlockType.UnorderedListItem,
        ReviewStatus = status,
    };
}
