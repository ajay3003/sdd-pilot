using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

public class DeltaResultCardTests : BunitContext
{
    // ── Badge rendering ──────────────────────────────────────────────────────

    [Fact]
    public void DeltaCard_Added_ShowsAddedStatusBadge()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeAdded("FR-001: Login")));

        cut.Find("[data-testid='delta-status-badge']").TextContent
            .Should().Contain("Added");
    }

    [Fact]
    public void DeltaCard_Modified_ShowsModifiedStatusBadge()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeModified("Old title", "New title")));

        cut.Find("[data-testid='delta-status-badge']").TextContent
            .Should().Contain("Modified");
    }

    [Fact]
    public void DeltaCard_Removed_ShowsRemovedStatusBadge()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeRemoved("FR-002: Auth")));

        cut.Find("[data-testid='delta-status-badge']").TextContent
            .Should().Contain("Removed");
    }

    [Fact]
    public void DeltaCard_ShowsKindBadge_ForRequirement()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeAdded("FR-001", ScenarioKind.Requirement)));

        cut.Find("[data-testid='delta-kind-badge']").TextContent
            .Should().Be("Requirement");
    }

    [Fact]
    public void DeltaCard_ShowsKindBadge_ForTest()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeAdded("TC-001", ScenarioKind.Test)));

        cut.Find("[data-testid='delta-kind-badge']").TextContent
            .Should().Be("Test");
    }

    [Fact]
    public void DeltaCard_ShowsKindBadge_ForNeedsClarification()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeAdded("CL-001", ScenarioKind.NeedsClarification)));

        cut.Find("[data-testid='delta-kind-badge']").TextContent
            .Should().Be("Needs Clarification");
    }

    [Fact]
    public void DeltaCard_StatusAndKindBadges_AreNotConcatenated()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeAdded("FR-001", ScenarioKind.Requirement)));

        var statusBadge = cut.Find("[data-testid='delta-status-badge']");
        var kindBadge   = cut.Find("[data-testid='delta-kind-badge']");

        // They must be separate DOM elements, not concatenated text
        statusBadge.Should().NotBeSameAs(kindBadge);

        // The card markup must NOT contain concatenated uppercase strings
        cut.Markup.Should().NotContain("AddedREQUIREMENT");
        cut.Markup.Should().NotContain("ADDEDREQUIREMENT");
        cut.Markup.Should().NotContain("ModifiedREQUIREMENT");
        cut.Markup.Should().NotContain("RemovedREQUIREMENT");
    }

    // ── Card structure ───────────────────────────────────────────────────────

    [Fact]
    public void DeltaCard_HasDataTestId()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeAdded("FR-001")));

        cut.Find("[data-testid='delta-card']").Should().NotBeNull();
    }

    [Fact]
    public void DeltaCard_Added_HasSingleText_NoGrid()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeAdded("FR-001: The system MUST do X")));

        cut.FindAll(".delta-diff-grid").Should().BeEmpty("Added cards use single text, not a diff grid");
        cut.Find(".delta-single-text").TextContent.Should().Contain("FR-001");
    }

    [Fact]
    public void DeltaCard_Removed_HasSingleText_NoGrid()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeRemoved("FR-002: Old requirement")));

        cut.FindAll(".delta-diff-grid").Should().BeEmpty();
        cut.Find(".delta-single-text").TextContent.Should().Contain("FR-002");
    }

    // ── Modified item: before/after ──────────────────────────────────────────

    [Fact]
    public void DeltaCard_Modified_HasDiffGrid()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeModified(
            "FR-001: Password login required",
            "FR-001: Passwordless login required")));

        cut.Find(".delta-diff-grid").Should().NotBeNull();
    }

    [Fact]
    public void DeltaCard_Modified_ShowsBeforeLabel()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeModified("Old title", "New title")));

        cut.Find("[data-testid='delta-before']").TextContent.Should().Contain("Before");
    }

    [Fact]
    public void DeltaCard_Modified_ShowsAfterLabel()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeModified("Old title", "New title")));

        cut.Find("[data-testid='delta-after']").TextContent.Should().Contain("After");
    }

    [Fact]
    public void DeltaCard_Modified_ShowsOldAndNewText()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeModified(
            "The system MUST allow password login",
            "The system MUST allow passwordless login")));

        cut.Find("[data-testid='delta-before']").TextContent.Should().Contain("password");
        cut.Find("[data-testid='delta-after']").TextContent.Should().Contain("passwordless");
    }

    [Fact]
    public void DeltaCard_Modified_NoSingleText()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeModified("Old", "New")));

        cut.FindAll(".delta-single-text").Should().BeEmpty("Modified cards use diff grid, not single text");
    }

    // ── Context and impact ───────────────────────────────────────────────────

    [Fact]
    public void DeltaCard_ShowsContextHeading_WhenPresent()
    {
        var delta = new SpecDeltaItem(
            SpecDeltaStatus.Added,
            ScenarioKind.Requirement,
            null,
            new ExtractionCandidate
            {
                Title = "FR-001: Login",
                Classification = ScenarioKind.Requirement,
                ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
                SourceBlockType = BlockType.UnorderedListItem,
                ContextHeading = "Authentication Section",
            },
            "FR-001",
            []);

        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, delta));

        cut.Find("[data-testid='delta-context']").TextContent
            .Should().Contain("Authentication Section");
    }

    [Fact]
    public void DeltaCard_HidesContextHeading_WhenAbsent()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeAdded("FR-001: No context")));

        cut.FindAll("[data-testid='delta-context']").Should().BeEmpty();
    }

    [Fact]
    public void DeltaCard_ShowsImpactHints_WhenPresent()
    {
        var delta = new SpecDeltaItem(
            SpecDeltaStatus.Modified,
            ScenarioKind.Requirement,
            Candidate("Old"),
            Candidate("New"),
            "FR-001",
            ["TC-001 may be impacted", "TC-002 may be impacted"]);

        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, delta));

        var list = cut.Find(".delta-impact-list");
        list.TextContent.Should().Contain("TC-001");
        list.TextContent.Should().Contain("TC-002");
    }

    [Fact]
    public void DeltaCard_HidesImpactList_WhenNoHints()
    {
        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, MakeAdded("FR-001")));

        cut.FindAll(".delta-impact-list").Should().BeEmpty();
    }

    // ── Match key ────────────────────────────────────────────────────────────

    [Fact]
    public void DeltaCard_ShowsMatchKey_WhenPresent()
    {
        var delta = new SpecDeltaItem(
            SpecDeltaStatus.Added,
            ScenarioKind.Requirement,
            null,
            Candidate("FR-001: New requirement"),
            "FR-001",
            []);

        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, delta));

        cut.Find("[data-testid='delta-match-key']").TextContent.Should().Be("FR-001");
    }

    [Fact]
    public void DeltaCard_HidesMatchKey_WhenEmpty()
    {
        var delta = new SpecDeltaItem(
            SpecDeltaStatus.Added,
            ScenarioKind.Requirement,
            null,
            Candidate("Some title"),
            string.Empty,
            []);

        var cut = Render<DeltaResultCard>(p => p.Add(c => c.Delta, delta));

        cut.FindAll("[data-testid='delta-match-key']").Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ExtractionCandidate Candidate(string title, ScenarioKind kind = ScenarioKind.Requirement) => new()
    {
        Title = title,
        Classification = kind,
        ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
        SourceBlockType = BlockType.UnorderedListItem,
    };

    private static SpecDeltaItem MakeAdded(string title, ScenarioKind kind = ScenarioKind.Requirement) =>
        new(SpecDeltaStatus.Added, kind, null, Candidate(title, kind), string.Empty, []);

    private static SpecDeltaItem MakeRemoved(string title, ScenarioKind kind = ScenarioKind.Requirement) =>
        new(SpecDeltaStatus.Removed, kind, Candidate(title, kind), null, string.Empty, []);

    private static SpecDeltaItem MakeModified(string oldTitle, string newTitle, ScenarioKind kind = ScenarioKind.Requirement) =>
        new(SpecDeltaStatus.Modified, kind, Candidate(oldTitle, kind), Candidate(newTitle, kind), "FR-001", []);
}
