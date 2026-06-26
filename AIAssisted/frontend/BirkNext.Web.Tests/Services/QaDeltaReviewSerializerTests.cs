using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public class QaDeltaReviewSerializerTests
{
    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_PreservesSummaryCounts()
    {
        var result = MakeResult(
            requirementDeltas: [ModifiedItem("FR-001 old", "FR-001 new", ScenarioKind.Requirement)],
            testDeltas: [AddedItem("TC-001", ScenarioKind.Test)],
            clarificationDeltas: []);

        var (summaryJson, deltaItemsJson) = QaDeltaReviewSerializer.Serialize(result);
        var deserialized = QaDeltaReviewSerializer.Deserialize(summaryJson, deltaItemsJson);

        deserialized.Summary.Should().BeEquivalentTo(result.Summary);
    }

    [Fact]
    public void RoundTrip_PreservesDeltaItemCounts()
    {
        var result = MakeResult(
            requirementDeltas: [ModifiedItem("R-old", "R-new", ScenarioKind.Requirement)],
            testDeltas: [AddedItem("TC-001", ScenarioKind.Test), AddedItem("TC-002", ScenarioKind.Test)],
            clarificationDeltas: [RemovedItem("CL-001", ScenarioKind.NeedsClarification)]);

        var (summaryJson, deltaItemsJson) = QaDeltaReviewSerializer.Serialize(result);
        var deserialized = QaDeltaReviewSerializer.Deserialize(summaryJson, deltaItemsJson);

        deserialized.RequirementDeltas.Should().HaveCount(1);
        deserialized.TestDeltas.Should().HaveCount(2);
        deserialized.ClarificationDeltas.Should().HaveCount(1);
    }

    [Fact]
    public void RoundTrip_PreservesOldAndNewTitles()
    {
        var result = MakeResult(
            requirementDeltas: [ModifiedItem("Old title", "New title", ScenarioKind.Requirement)],
            testDeltas: [],
            clarificationDeltas: []);

        var (summaryJson, deltaItemsJson) = QaDeltaReviewSerializer.Serialize(result);
        var deserialized = QaDeltaReviewSerializer.Deserialize(summaryJson, deltaItemsJson);

        var item = deserialized.RequirementDeltas[0];
        item.OldCandidate!.Title.Should().Be("Old title");
        item.NewCandidate!.Title.Should().Be("New title");
    }

    [Fact]
    public void RoundTrip_PreservesAddedItemTitle()
    {
        var result = MakeResult(
            requirementDeltas: [AddedItem("New requirement text", ScenarioKind.Requirement)],
            testDeltas: [],
            clarificationDeltas: []);

        var (summaryJson, deltaItemsJson) = QaDeltaReviewSerializer.Serialize(result);
        var deserialized = QaDeltaReviewSerializer.Deserialize(summaryJson, deltaItemsJson);

        var item = deserialized.RequirementDeltas[0];
        item.OldCandidate.Should().BeNull();
        item.NewCandidate!.Title.Should().Be("New requirement text");
        item.Status.Should().Be(SpecDeltaStatus.Added);
    }

    [Fact]
    public void RoundTrip_PreservesRemovedItemTitle()
    {
        var result = MakeResult(
            requirementDeltas: [RemovedItem("Deleted requirement", ScenarioKind.Requirement)],
            testDeltas: [],
            clarificationDeltas: []);

        var (summaryJson, deltaItemsJson) = QaDeltaReviewSerializer.Serialize(result);
        var deserialized = QaDeltaReviewSerializer.Deserialize(summaryJson, deltaItemsJson);

        var item = deserialized.RequirementDeltas[0];
        item.OldCandidate!.Title.Should().Be("Deleted requirement");
        item.NewCandidate.Should().BeNull();
        item.Status.Should().Be(SpecDeltaStatus.Removed);
    }

    [Fact]
    public void RoundTrip_EmptyDeltaLists_SerializeAndDeserializeCleanly()
    {
        var result = MakeResult([], [], []);

        var (summaryJson, deltaItemsJson) = QaDeltaReviewSerializer.Serialize(result);
        var deserialized = QaDeltaReviewSerializer.Deserialize(summaryJson, deltaItemsJson);

        deserialized.RequirementDeltas.Should().BeEmpty();
        deserialized.TestDeltas.Should().BeEmpty();
        deserialized.ClarificationDeltas.Should().BeEmpty();
    }

    [Fact]
    public void RoundTrip_ClassificationsGroupedCorrectly()
    {
        var result = MakeResult(
            requirementDeltas: [AddedItem("Req", ScenarioKind.Requirement)],
            testDeltas: [AddedItem("Test", ScenarioKind.Test)],
            clarificationDeltas: [AddedItem("Clarification", ScenarioKind.NeedsClarification)]);

        var (summaryJson, deltaItemsJson) = QaDeltaReviewSerializer.Serialize(result);
        var deserialized = QaDeltaReviewSerializer.Deserialize(summaryJson, deltaItemsJson);

        deserialized.RequirementDeltas[0].Classification.Should().Be(ScenarioKind.Requirement);
        deserialized.TestDeltas[0].Classification.Should().Be(ScenarioKind.Test);
        deserialized.ClarificationDeltas[0].Classification.Should().Be(ScenarioKind.NeedsClarification);
    }

    [Fact]
    public void RoundTrip_ImpactHintsPreserved()
    {
        var hints = new List<string> { "TC-001", "TC-002" };
        var delta = new SpecDeltaItem(
            SpecDeltaStatus.Modified,
            ScenarioKind.Requirement,
            OldCandidate("Old"),
            NewCandidate("New"),
            "FR-001",
            hints);
        var result = MakeResult([delta], [], []);

        var (summaryJson, deltaItemsJson) = QaDeltaReviewSerializer.Serialize(result);
        var deserialized = QaDeltaReviewSerializer.Deserialize(summaryJson, deltaItemsJson);

        deserialized.RequirementDeltas[0].ImpactHints.Should().BeEquivalentTo(hints);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SpecComparisonResult MakeResult(
        IReadOnlyList<SpecDeltaItem> requirementDeltas,
        IReadOnlyList<SpecDeltaItem> testDeltas,
        IReadOnlyList<SpecDeltaItem> clarificationDeltas)
    {
        var summary = new SpecComparisonSummary(
            AddedRequirements: requirementDeltas.Count(d => d.Status == SpecDeltaStatus.Added),
            ModifiedRequirements: requirementDeltas.Count(d => d.Status == SpecDeltaStatus.Modified),
            RemovedRequirements: requirementDeltas.Count(d => d.Status == SpecDeltaStatus.Removed),
            UnchangedRequirements: 0,
            AddedTests: testDeltas.Count(d => d.Status == SpecDeltaStatus.Added),
            RemovedTests: testDeltas.Count(d => d.Status == SpecDeltaStatus.Removed),
            PotentiallyImpactedTests: 0,
            AddedClarifications: clarificationDeltas.Count(d => d.Status == SpecDeltaStatus.Added),
            RemovedClarifications: clarificationDeltas.Count(d => d.Status == SpecDeltaStatus.Removed),
            StillUnresolvedClarifications: 0,
            UncoveredRequirements: 0,
            NewClarificationRisks: 0);

        return new SpecComparisonResult(requirementDeltas, testDeltas, clarificationDeltas, summary);
    }

    private static SpecDeltaItem ModifiedItem(string oldTitle, string newTitle, ScenarioKind kind) =>
        new(SpecDeltaStatus.Modified, kind, OldCandidate(oldTitle), NewCandidate(newTitle), oldTitle, []);

    private static SpecDeltaItem AddedItem(string title, ScenarioKind kind) =>
        new(SpecDeltaStatus.Added, kind, null, NewCandidate(title), title, []);

    private static SpecDeltaItem RemovedItem(string title, ScenarioKind kind) =>
        new(SpecDeltaStatus.Removed, kind, OldCandidate(title), null, title, []);

    private static ExtractionCandidate OldCandidate(string title) => new()
    {
        Title = title,
        Classification = ScenarioKind.Requirement,
        ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
        SourceBlockType = BlockType.UnorderedListItem,
    };

    private static ExtractionCandidate NewCandidate(string title) => new()
    {
        Title = title,
        Classification = ScenarioKind.Requirement,
        ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
        SourceBlockType = BlockType.UnorderedListItem,
    };
}
