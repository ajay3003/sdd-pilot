using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Tests for ExtractionCandidateMetricsService — centralized metrics for extraction candidates.
/// </summary>
public sealed class ExtractionCandidateMetricsServiceTests
{
    private readonly ExtractionCandidateMetricsService _sut = new();

    // ── Fixtures ───────────────────────────────────────────────────────────────

    private static List<ExtractionCandidate> CreateCandidates(params (Guid id, ScenarioKind kind, string title)[] items)
    {
        return items
            .Select(item => new ExtractionCandidate
            {
                CandidateId = item.id,
                Classification = item.kind,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.Heading,
                Title = item.title,
                ContextHeading = "Test Section",
                ReviewStatus = CandidateReviewStatus.New,
            })
            .ToList();
    }

    private static List<CandidateLinkEntry> CreateLinks(params (Guid source, Guid target, CandidateLinkType type)[] items)
    {
        return items
            .Select(item => new CandidateLinkEntry
            {
                SourceId = item.source,
                TargetId = item.target,
                LinkType = item.type,
            })
            .ToList();
    }

    // ── RequirementsWithTests ──────────────────────────────────────────────────

    [Fact]
    public void CountRequirementsWithTests_NoCandidates_ReturnsZero()
    {
        var count = _sut.CountRequirementsWithTests([], []);
        count.Should().Be(0);
    }

    [Fact]
    public void CountRequirementsWithTests_OnlyRequirements_ReturnsZero()
    {
        var req1 = Guid.NewGuid();
        var candidates = CreateCandidates((req1, ScenarioKind.Requirement, "FR-001"));
        var count = _sut.CountRequirementsWithTests(candidates, []);
        count.Should().Be(0);
    }

    [Fact]
    public void CountRequirementsWithTests_RequirementLinkedToTest_ReturnsOne()
    {
        var req1 = Guid.NewGuid();
        var test1 = Guid.NewGuid();
        var candidates = CreateCandidates(
            (req1, ScenarioKind.Requirement, "FR-001"),
            (test1, ScenarioKind.Test, "Test 1"));
        var links = CreateLinks((req1, test1, CandidateLinkType.RequirementTest));

        var count = _sut.CountRequirementsWithTests(candidates, links);
        count.Should().Be(1);
    }

    [Fact]
    public void CountRequirementsWithTests_BidirectionalLink_CountsRequirement()
    {
        var req1 = Guid.NewGuid();
        var test1 = Guid.NewGuid();
        var candidates = CreateCandidates(
            (req1, ScenarioKind.Requirement, "FR-001"),
            (test1, ScenarioKind.Test, "Test 1"));
        // Link in reverse direction (test → requirement)
        var links = CreateLinks((test1, req1, CandidateLinkType.RequirementTest));

        var count = _sut.CountRequirementsWithTests(candidates, links);
        count.Should().Be(1);
    }

    [Fact]
    public void CountRequirementsWithTests_MultipleRequirementsOneLinked_CountsOnlyLinked()
    {
        var req1 = Guid.NewGuid();
        var req2 = Guid.NewGuid();
        var test1 = Guid.NewGuid();
        var candidates = CreateCandidates(
            (req1, ScenarioKind.Requirement, "FR-001"),
            (req2, ScenarioKind.Requirement, "FR-002"),
            (test1, ScenarioKind.Test, "Test 1"));
        var links = CreateLinks((req1, test1, CandidateLinkType.RequirementTest));

        var count = _sut.CountRequirementsWithTests(candidates, links);
        count.Should().Be(1);
    }

    // ── RequirementsWithoutTests ───────────────────────────────────────────────

    [Fact]
    public void CountRequirementsWithoutTests_OnlyRequirements_ReturnsAll()
    {
        var req1 = Guid.NewGuid();
        var req2 = Guid.NewGuid();
        var candidates = CreateCandidates(
            (req1, ScenarioKind.Requirement, "FR-001"),
            (req2, ScenarioKind.Requirement, "FR-002"));

        var count = _sut.CountRequirementsWithoutTests(candidates, []);
        count.Should().Be(2);
    }

    [Fact]
    public void CountRequirementsWithoutTests_AllLinked_ReturnsZero()
    {
        var req1 = Guid.NewGuid();
        var req2 = Guid.NewGuid();
        var test1 = Guid.NewGuid();
        var test2 = Guid.NewGuid();
        var candidates = CreateCandidates(
            (req1, ScenarioKind.Requirement, "FR-001"),
            (req2, ScenarioKind.Requirement, "FR-002"),
            (test1, ScenarioKind.Test, "Test 1"),
            (test2, ScenarioKind.Test, "Test 2"));
        var links = CreateLinks(
            (req1, test1, CandidateLinkType.RequirementTest),
            (req2, test2, CandidateLinkType.RequirementTest));

        var count = _sut.CountRequirementsWithoutTests(candidates, links);
        count.Should().Be(0);
    }

    // ── RequirementsWithClarifications ─────────────────────────────────────────

    [Fact]
    public void CountRequirementsWithClarifications_NoLinks_ReturnsZero()
    {
        var req1 = Guid.NewGuid();
        var clr1 = Guid.NewGuid();
        var candidates = CreateCandidates(
            (req1, ScenarioKind.Requirement, "FR-001"),
            (clr1, ScenarioKind.NeedsClarification, "Clarification 1"));

        var count = _sut.CountRequirementsWithClarifications(candidates, []);
        count.Should().Be(0);
    }

    [Fact]
    public void CountRequirementsWithClarifications_LinkedToClarity_CountsRequirement()
    {
        var req1 = Guid.NewGuid();
        var clr1 = Guid.NewGuid();
        var candidates = CreateCandidates(
            (req1, ScenarioKind.Requirement, "FR-001"),
            (clr1, ScenarioKind.NeedsClarification, "Clarification 1"));
        var links = CreateLinks((req1, clr1, CandidateLinkType.RequirementClarification));

        var count = _sut.CountRequirementsWithClarifications(candidates, links);
        count.Should().Be(1);
    }

    // ── TestsWithoutRequirements ───────────────────────────────────────────────

    [Fact]
    public void CountTestsWithoutRequirements_OnlyTests_ReturnsAll()
    {
        var test1 = Guid.NewGuid();
        var test2 = Guid.NewGuid();
        var candidates = CreateCandidates(
            (test1, ScenarioKind.Test, "Test 1"),
            (test2, ScenarioKind.Test, "Test 2"));

        var count = _sut.CountTestsWithoutRequirements(candidates, []);
        count.Should().Be(2);
    }

    [Fact]
    public void CountTestsWithoutRequirements_AllLinked_ReturnsZero()
    {
        var req1 = Guid.NewGuid();
        var req2 = Guid.NewGuid();
        var test1 = Guid.NewGuid();
        var test2 = Guid.NewGuid();
        var candidates = CreateCandidates(
            (req1, ScenarioKind.Requirement, "FR-001"),
            (req2, ScenarioKind.Requirement, "FR-002"),
            (test1, ScenarioKind.Test, "Test 1"),
            (test2, ScenarioKind.Test, "Test 2"));
        var links = CreateLinks(
            (test1, req1, CandidateLinkType.RequirementTest),
            (test2, req2, CandidateLinkType.RequirementTest));

        var count = _sut.CountTestsWithoutRequirements(candidates, links);
        count.Should().Be(0);
    }

    // ── ClarificationsWithoutRequirements ──────────────────────────────────────

    [Fact]
    public void CountClarificationsWithoutRequirements_OnlyClarifications_ReturnsAll()
    {
        var clr1 = Guid.NewGuid();
        var clr2 = Guid.NewGuid();
        var candidates = CreateCandidates(
            (clr1, ScenarioKind.NeedsClarification, "Clarification 1"),
            (clr2, ScenarioKind.NeedsClarification, "Clarification 2"));

        var count = _sut.CountClarificationsWithoutRequirements(candidates, []);
        count.Should().Be(2);
    }

    [Fact]
    public void CountClarificationsWithoutRequirements_AllLinked_ReturnsZero()
    {
        var req1 = Guid.NewGuid();
        var req2 = Guid.NewGuid();
        var clr1 = Guid.NewGuid();
        var clr2 = Guid.NewGuid();
        var candidates = CreateCandidates(
            (req1, ScenarioKind.Requirement, "FR-001"),
            (req2, ScenarioKind.Requirement, "FR-002"),
            (clr1, ScenarioKind.NeedsClarification, "Clarification 1"),
            (clr2, ScenarioKind.NeedsClarification, "Clarification 2"));
        var links = CreateLinks(
            (clr1, req1, CandidateLinkType.RequirementClarification),
            (clr2, req2, CandidateLinkType.RequirementClarification));

        var count = _sut.CountClarificationsWithoutRequirements(candidates, links);
        count.Should().Be(0);
    }

    // ── UnresolvedClarifications ───────────────────────────────────────────────

    [Fact]
    public void CountUnresolvedClarifications_EmptyList_ReturnsZero()
    {
        var count = _sut.CountUnresolvedClarifications([]);
        count.Should().Be(0);
    }

    [Fact]
    public void CountUnresolvedClarifications_OnlyAccepted_ReturnsZero()
    {
        var clr1 = Guid.NewGuid();
        var candidates = new List<ExtractionCandidate>
        {
            new()
            {
                CandidateId = clr1,
                Classification = ScenarioKind.NeedsClarification,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.Heading,
                Title = "Clarification 1",
                ReviewStatus = CandidateReviewStatus.Accepted,
            },
        };

        var count = _sut.CountUnresolvedClarifications(candidates);
        count.Should().Be(0);
    }

    [Fact]
    public void CountUnresolvedClarifications_WithNewStatus_CountsIt()
    {
        var clr1 = Guid.NewGuid();
        var clr2 = Guid.NewGuid();
        var candidates = new List<ExtractionCandidate>
        {
            new()
            {
                CandidateId = clr1,
                Classification = ScenarioKind.NeedsClarification,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.Heading,
                Title = "Clarification 1",
                ReviewStatus = CandidateReviewStatus.New,
            },
            new()
            {
                CandidateId = clr2,
                Classification = ScenarioKind.NeedsClarification,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.Heading,
                Title = "Clarification 2",
                ReviewStatus = CandidateReviewStatus.NeedsReview,
            },
        };

        var count = _sut.CountUnresolvedClarifications(candidates);
        count.Should().Be(2);
    }

    // ── RequirementsWithUnresolvedClarifications ───────────────────────────────

    [Fact]
    public void CountRequirementsWithUnresolvedClarifications_NoLinks_ReturnsZero()
    {
        var req1 = Guid.NewGuid();
        var clr1 = Guid.NewGuid();
        var candidates = CreateCandidates(
            (req1, ScenarioKind.Requirement, "FR-001"),
            (clr1, ScenarioKind.NeedsClarification, "Clarification 1"));

        var count = _sut.CountRequirementsWithUnresolvedClarifications(candidates, []);
        count.Should().Be(0);
    }

    [Fact]
    public void CountRequirementsWithUnresolvedClarifications_LinkedToAccepted_ReturnsZero()
    {
        var req1 = Guid.NewGuid();
        var clr1 = Guid.NewGuid();
        var candidates = new List<ExtractionCandidate>
        {
            new()
            {
                CandidateId = req1,
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.Heading,
                Title = "FR-001",
            },
            new()
            {
                CandidateId = clr1,
                Classification = ScenarioKind.NeedsClarification,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.Heading,
                Title = "Clarification 1",
                ReviewStatus = CandidateReviewStatus.Accepted,
            },
        };
        var links = CreateLinks((req1, clr1, CandidateLinkType.RequirementClarification));

        var count = _sut.CountRequirementsWithUnresolvedClarifications(candidates, links);
        count.Should().Be(0);
    }

    [Fact]
    public void CountRequirementsWithUnresolvedClarifications_LinkedToUnresolved_CountsRequirement()
    {
        var req1 = Guid.NewGuid();
        var req2 = Guid.NewGuid();
        var clr1 = Guid.NewGuid();
        var candidates = new List<ExtractionCandidate>
        {
            new()
            {
                CandidateId = req1,
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.Heading,
                Title = "FR-001",
            },
            new()
            {
                CandidateId = req2,
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.Heading,
                Title = "FR-002",
            },
            new()
            {
                CandidateId = clr1,
                Classification = ScenarioKind.NeedsClarification,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.Heading,
                Title = "Clarification 1",
                ReviewStatus = CandidateReviewStatus.New,
            },
        };
        var links = CreateLinks((req1, clr1, CandidateLinkType.RequirementClarification));

        var count = _sut.CountRequirementsWithUnresolvedClarifications(candidates, links);
        count.Should().Be(1);
    }

    // ── CountPending ───────────────────────────────────────────────────────────

    [Fact]
    public void CountPending_NoCandidates_ReturnsZero()
    {
        var count = _sut.CountPending([], ScenarioKind.Requirement);
        count.Should().Be(0);
    }

    [Fact]
    public void CountPending_AllAccepted_ReturnsZero()
    {
        var req1 = Guid.NewGuid();
        var candidates = new List<ExtractionCandidate>
        {
            new()
            {
                CandidateId = req1,
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.Heading,
                Title = "FR-001",
                ReviewStatus = CandidateReviewStatus.Accepted,
            },
        };

        var count = _sut.CountPending(candidates, ScenarioKind.Requirement);
        count.Should().Be(0);
    }

    [Fact]
    public void CountPending_MixedStatuses_CountsOnlyPending()
    {
        var req1 = Guid.NewGuid();
        var req2 = Guid.NewGuid();
        var req3 = Guid.NewGuid();
        var candidates = new List<ExtractionCandidate>
        {
            new()
            {
                CandidateId = req1,
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.Heading,
                Title = "FR-001",
                ReviewStatus = CandidateReviewStatus.New,
            },
            new()
            {
                CandidateId = req2,
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.Heading,
                Title = "FR-002",
                ReviewStatus = CandidateReviewStatus.NeedsReview,
            },
            new()
            {
                CandidateId = req3,
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.BddPattern,
                SourceBlockType = BlockType.Heading,
                Title = "FR-003",
                ReviewStatus = CandidateReviewStatus.Accepted,
            },
        };

        var count = _sut.CountPending(candidates, ScenarioKind.Requirement);
        count.Should().Be(2);
    }

    [Fact]
    public void CountPending_IncludesOnlySpecifiedKind()
    {
        var req1 = Guid.NewGuid();
        var test1 = Guid.NewGuid();
        var candidates = CreateCandidates(
            (req1, ScenarioKind.Requirement, "FR-001"),
            (test1, ScenarioKind.Test, "Test 1"));
        // Update status to Pending
        candidates[0].ReviewStatus = CandidateReviewStatus.New;
        candidates[1].ReviewStatus = CandidateReviewStatus.New;

        var countReq = _sut.CountPending(candidates, ScenarioKind.Requirement);
        var countTest = _sut.CountPending(candidates, ScenarioKind.Test);

        countReq.Should().Be(1);
        countTest.Should().Be(1);
    }
}
