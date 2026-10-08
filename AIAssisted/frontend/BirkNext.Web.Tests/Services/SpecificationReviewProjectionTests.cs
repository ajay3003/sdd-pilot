using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Logical entity identity, deterministic relations, primary tree context and candidate projection for Specification Explorer.</summary>
public sealed class SpecificationReviewProjectionTests
{
    [Fact]
    public void RepeatedReferencesToARequirementDoNotCreateMoreRequirements()
    {
        const string spec = """
            # Spec

            ## Requirements

            - **FR-004**: The system MUST approve requests.
            - **FR-005**: Approval MUST follow FR-004 and fr-004 rules (see FR-4).

            ## Notes

            This note references FR-004 again, and FR-004 once more.
            """;

        var projection = SpecificationReviewProjection.Build(spec);

        projection.Count(SpecEntityKind.Requirement).Should().Be(2);
        projection.OfKind(SpecEntityKind.Requirement).Select(r => r.Key).Should().Equal("FR-004", "FR-005");
        projection.Duplicates.Should().BeEmpty();
    }

    [Fact]
    public void DuplicateRequirementDefinitionsAreCountedOnceAndReported()
    {
        const string spec = """
            # Spec

            ## Requirements

            - **FR-004**: The system MUST approve requests.
            - **fr-4**: The system MUST approve requests again.
            """;

        var projection = SpecificationReviewProjection.Build(spec);

        projection.Count(SpecEntityKind.Requirement).Should().Be(1);
        projection.Duplicates.Should().ContainSingle().Which.Should().Be(new SpecDuplicateIdentity(SpecEntityKind.Requirement, "FR-004", 2));
    }

    [Fact]
    public void TestNestedInsideARequirementSectionHasThatRequirementAsPrimaryParent()
    {
        const string spec = """
            # Spec

            ## Acceptance Scenarios

            ### FR-004 Approve requests

            1. **Given** a pending request, **When** a manager approves it, **Then** the request is approved.
            """;

        var projection = SpecificationReviewProjection.Build(spec);

        projection.Count(SpecEntityKind.Requirement).Should().Be(1);
        projection.Count(SpecEntityKind.Test).Should().Be(1);
        var test = projection.OfKind(SpecEntityKind.Test).Single();
        projection.PrimaryParentOf(test.Key).Should().Be(new SpecPrimaryParent("FR-004", SpecRelationType.Verifies, SpecRelationProvenance.StructuralNesting));
        projection.Relations.Should().ContainSingle(r => r.SourceKey == test.Key && r.TargetKey == "FR-004" && r.Type == SpecRelationType.Verifies);
    }

    [Fact]
    public void ClarificationNestedUnderARequirementStaysAClarification()
    {
        const string spec = """
            # Spec

            ## Requirements

            ### FR-004 Approve requests

            The system MUST approve requests.

            - Clarification: Which approval threshold applies to large requests?
            """;

        var projection = SpecificationReviewProjection.Build(spec);

        projection.Count(SpecEntityKind.Requirement).Should().Be(1);
        projection.Count(SpecEntityKind.Clarification).Should().Be(1);
        var clarification = projection.OfKind(SpecEntityKind.Clarification).Single();
        projection.PrimaryParentOf(clarification.Key).Should().Be(new SpecPrimaryParent("FR-004", SpecRelationType.Clarifies, SpecRelationProvenance.StructuralNesting));
    }

    [Fact]
    public void TestReferencingTwoRequirementsHasTwoLinksIsCountedOnceAndKeepsItsUserStoryContext()
    {
        const string spec = """
            # Spec

            ## User Scenarios & Testing

            ### User Story 1 — Approvals (Priority: P1)

            **Acceptance Scenarios**:

            1. **Given** a pending request (FR-004, FR-007), **When** a manager approves it, **Then** the request is approved.

            ## Requirements

            - **FR-004**: The system MUST approve requests.
            - **FR-007**: The system MUST notify the requester.
            """;

        var projection = SpecificationReviewProjection.Build(spec);

        projection.Count(SpecEntityKind.Test).Should().Be(1);
        projection.Count(SpecEntityKind.Requirement).Should().Be(2);
        var test = projection.OfKind(SpecEntityKind.Test).Single();
        projection.Targets(test.Key, SpecRelationType.Verifies, SpecEntityKind.Requirement).Select(r => r.Key).Should().BeEquivalentTo("FR-004", "FR-007");
        projection.Relations.Where(r => r.SourceKey == test.Key && r.Type == SpecRelationType.Verifies)
            .Should().OnlyContain(r => r.Provenance == SpecRelationProvenance.ExplicitReference);
        // Two explicit requirement links never pick one of them: the user story stays the primary context.
        projection.PrimaryParentOf(test.Key).Should().Be(new SpecPrimaryParent("US-001", SpecRelationType.BelongsTo, SpecRelationProvenance.StructuralNesting));
        projection.Summary().TestsWithMultipleRequirementLinks.Should().Be(1);
    }

    [Fact]
    public void TestWithTwoRequirementLinksAndNoStoryHasNoPrimaryParent()
    {
        const string spec = """
            # Spec

            ## Acceptance Scenarios

            1. **Given** a pending request, **When** approved per FR-004 and FR-007, **Then** it is approved.

            ## Requirements

            - **FR-004**: The system MUST approve requests.
            - **FR-007**: The system MUST notify the requester.
            """;

        var projection = SpecificationReviewProjection.Build(spec);

        var test = projection.OfKind(SpecEntityKind.Test).Single();
        projection.PrimaryParentOf(test.Key).Should().BeNull();
        projection.Targets(test.Key, SpecRelationType.Verifies, SpecEntityKind.Requirement).Should().HaveCount(2);
    }

    [Fact]
    public void ClarificationWithoutReferencesStaysAtSpecificationLevel()
    {
        const string spec = """
            # Spec

            ## Requirements

            - **FR-004**: The system MUST approve requests.

            ## Clarifications

            ### Session 2026-04-20

            - Q: Which terminology does the whole feature use for applicants? → A: "Requester" everywhere.
            - Q: What is the approval limit (FR-004)? → A: 10 000 NOK.
            """;

        var projection = SpecificationReviewProjection.Build(spec);

        projection.Count(SpecEntityKind.Clarification).Should().Be(2);
        projection.Count(SpecEntityKind.Requirement).Should().Be(1);
        var general = projection.OfKind(SpecEntityKind.Clarification).First();
        var specific = projection.OfKind(SpecEntityKind.Clarification).Last();
        projection.PrimaryParentOf(general.Key).Should().BeNull();
        projection.RelationsFrom(general.Key).Should().BeEmpty();
        projection.PrimaryParentOf(specific.Key).Should().Be(new SpecPrimaryParent("FR-004", SpecRelationType.Clarifies, SpecRelationProvenance.ExplicitReference));
        specific.Answer.Should().Be("10 000 NOK.");
        projection.Summary().Should().Be(new SpecRelationshipSummary(0, 0, 0, 1, 1));
    }

    [Fact]
    public void TestInUserStoryWithoutRequirementHasTheUserStoryAsPrimaryParent()
    {
        const string spec = """
            # Spec

            ## User Scenarios & Testing

            ### User Story 2 — Approvals (Priority: P1)

            **Acceptance Scenarios**:

            1. **Given** a pending request, **When** a manager approves it, **Then** the request is approved.

            ## Requirements

            - **FR-004**: The system MUST approve pending requests when a manager approves them.
            """;

        var projection = SpecificationReviewProjection.Build(spec);

        var test = projection.OfKind(SpecEntityKind.Test).Single();
        test.DisplayId.Should().Be("US 2 · Scenario 1");
        projection.PrimaryParentOf(test.Key).Should().Be(new SpecPrimaryParent("US-002", SpecRelationType.BelongsTo, SpecRelationProvenance.StructuralNesting));
        // Similar wording ("pending request", "manager approves") never creates a requirement link.
        projection.Targets(test.Key, SpecRelationType.Verifies, SpecEntityKind.Requirement).Should().BeEmpty();
        projection.Count(SpecEntityKind.Requirement).Should().Be(1);
    }

    [Fact]
    public void SimilarTextAloneCreatesNoRelationship()
    {
        const string spec = """
            # Spec

            ## Acceptance Scenarios

            1. **Given** a pending approval request, **When** a manager approves the request, **Then** the approval request is approved.

            ## Requirements

            - **FR-004**: The system MUST let a manager approve a pending approval request.
            """;

        var projection = SpecificationReviewProjection.Build(spec);

        projection.Relations.Should().BeEmpty();
        projection.PrimaryParents.Should().BeEmpty();
    }

    [Fact]
    public void RequirementCandidatesAreCountedAsCandidatesWhileTheRequirementIsCountedOnce()
    {
        const string spec = """
            # Spec

            ## Requirements

            - **FR-004**: The system MUST approve requests.
            """;
        var projection = SpecificationReviewProjection.Build(spec);
        var candidates = new[]
        {
            Candidate("FR-004: The system MUST approve requests.", ScenarioKind.Requirement),
            Candidate("FR-004: Approval MUST be audited.", ScenarioKind.Requirement),
            Candidate("The approval process MUST be fast.", ScenarioKind.Requirement),
        };

        var reviewCandidates = SpecificationReviewProjection.BuildCandidates(projection, candidates);
        var summary = SpecificationReviewProjection.Summarize(projection, reviewCandidates);

        summary.Should().Be(new SpecificationReviewSummary(1, 0, 0, 3, 3, 0, 0));
        reviewCandidates.Take(2).Should().OnlyContain(c => c.Entity!.Key == "FR-004");
        reviewCandidates[2].Entity.Should().BeNull();
    }

    [Fact]
    public void CandidatesReferenceStructuredTestsAndQuotedClarificationsByIdentity()
    {
        const string spec = """
            # Spec

            ## User Scenarios & Testing

            ### User Story 1 — Approvals (Priority: P1)

            **Acceptance Scenarios**:

            1. **Given** a pending request, **When** a manager approves it, **Then** the request is approved.

            ## Clarifications

            ### Session 2026-04-20

            - Q: What is the approval limit? → A: 10 000 NOK.
            """;
        var projection = SpecificationReviewProjection.Build(spec);
        var candidates = new[]
        {
            Candidate("Given a pending request, When a manager approves it, Then the request is approved.", ScenarioKind.Test),
            Candidate("Q: What is the approval limit? → A: 10 000 NOK.", ScenarioKind.Requirement),
        };

        var reviewCandidates = SpecificationReviewProjection.BuildCandidates(projection, candidates);

        reviewCandidates[0].Type.Should().Be(ReviewCandidateType.Test);
        reviewCandidates[0].Entity!.Key.Should().Be("US-001/S1");
        reviewCandidates[0].Entity!.Given.Should().Be("a pending request");
        // The analyzer's candidate type is kept; the referenced entity shows what the candidate is about.
        reviewCandidates[1].Type.Should().Be(ReviewCandidateType.Requirement);
        reviewCandidates[1].Entity!.Kind.Should().Be(SpecEntityKind.Clarification);
    }

    [Fact]
    public void FilterAndSearchUseCandidateTypeIdentifierTextAndScenarioParts()
    {
        const string spec = """
            # Spec

            ## User Scenarios & Testing

            ### User Story 1 — Approvals (Priority: P1)

            **Acceptance Scenarios**:

            1. **Given** a pending request, **When** a manager approves it, **Then** the request is archived.

            ## Requirements

            - **FR-004**: The system MUST approve requests.
            """;
        var projection = SpecificationReviewProjection.Build(spec);
        var candidates = SpecificationReviewProjection.BuildCandidates(projection, new[]
        {
            Candidate("FR-004: The system MUST approve requests.", ScenarioKind.Requirement),
            Candidate("Given a pending request, When a manager approves it, Then the request is archived.", ScenarioKind.Test),
            Candidate("Which limit applies?", ScenarioKind.NeedsClarification),
        });

        SpecificationReviewProjection.Filter(candidates, ReviewCandidateType.Test, null).Should().ContainSingle().Which.Type.Should().Be(ReviewCandidateType.Test);
        SpecificationReviewProjection.Filter(candidates, null, "fr-004").Should().ContainSingle().Which.Entity!.Key.Should().Be("FR-004");
        SpecificationReviewProjection.Filter(candidates, null, "ARCHIVED").Should().ContainSingle().Which.Type.Should().Be(ReviewCandidateType.Test);
        SpecificationReviewProjection.Filter(candidates, ReviewCandidateType.Requirement, "archived").Should().BeEmpty();
        SpecificationReviewProjection.Filter(candidates, null, "  ").Should().HaveCount(3);
    }

    [Fact]
    public void PersonAdapterSpecificationCountsUniqueEntitiesSeparatelyFromItsCandidateDistribution()
    {
        var markdown = File.ReadAllText(TestDataHelper.ResolveFixturePath("person-adapter", "spec.md"));
        var projection = SpecificationReviewProjection.Build(markdown);
        var pipeline = new ScenarioExtractionService(new ExtractionConfiguration()).ExtractAsync(markdown, ExtractionProfile.Speckit).Result;

        var candidates = SpecificationReviewProjection.BuildCandidates(projection, pipeline.Candidates);
        var summary = SpecificationReviewProjection.Summarize(projection, candidates);

        pipeline.RequirementCount.Should().Be(summary.Requirements);
        summary.Requirements.Should().Be(projection.Entities.Count(e => e.Kind == SpecEntityKind.Requirement));
        summary.Tests.Should().Be(projection.Entities.Count(e => e.Kind == SpecEntityKind.Test));
        summary.Candidates.Should().Be(candidates.Count);
        projection.Find("FR-001").Should().NotBeNull();
        candidates.Count(c => c.Entity?.Kind == SpecEntityKind.Requirement).Should().Be(summary.Requirements);
        candidates.Count(c => c.Type == ReviewCandidateType.Test).Should().Be(summary.TestCandidates);
    }

    [Fact]
    public void EmptySpecificationHasNoEntities()
    {
        SpecificationReviewProjection.Build("  ").Entities.Should().BeEmpty();
        SpecificationReviewProjection.Build((string?)null).Should().BeSameAs(SpecificationEntityProjection.Empty);
    }

    private static ExtractionCandidate Candidate(string title, ScenarioKind kind) => new()
    {
        Title = title,
        Classification = kind,
        ClassificationSignal = ClassificationSignal.Default,
        SourceBlockType = BlockType.UnorderedListItem,
    };
}
