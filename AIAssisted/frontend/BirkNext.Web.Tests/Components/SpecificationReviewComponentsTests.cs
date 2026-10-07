using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>Review candidate list (filters, search, preview, Given/When/Then) and the primary-context relationship panel.</summary>
public sealed class SpecificationReviewComponentsTests : BunitContext
{
    private const string StorySpec = """
        # Spec

        ## User Scenarios & Testing

        ### User Story 1 — Approvals (Priority: P1)

        **Acceptance Scenarios**:

        1. **Given** a pending request, **When** a manager approves it per FR-004 and FR-007, **Then** the request is archived.

        ## Requirements

        - **FR-004**: The system MUST approve requests.
        - **FR-007**: The system MUST notify the requester.

        ## Clarifications

        ### Session 2026-04-20

        - Q: What is the approval limit (FR-004)? → A: 10 000 NOK.
        - Q: Which wording does the whole feature use? → A: "Requester".
        """;

    [Fact]
    public void CandidateListShowsAPreviewThenAllThenLess()
    {
        var cut = RenderList(Enumerable.Range(1, 40).Select(i => Candidate($"Requirement candidate text {i:00} MUST hold.", ScenarioKind.Requirement)).ToArray());

        cut.FindAll("[data-testid='spec-review-candidates'] > li").Should().HaveCount(SpecificationReviewProjection.CandidatePreviewSize);
        cut.Find("[data-testid='src-count']").TextContent.Should().Be("Showing 15 of 40 candidates");
        var toggle = cut.Find("[data-testid='src-show-all']");
        toggle.TextContent.Trim().Should().Be("Show all 40");
        toggle.GetAttribute("aria-expanded").Should().Be("false");

        toggle.Click();
        cut.FindAll("[data-testid='spec-review-candidates'] > li").Should().HaveCount(40);
        cut.Find("[data-testid='src-show-all']").TextContent.Trim().Should().Be("Show less");

        cut.Find("[data-testid='src-show-all']").Click();
        cut.FindAll("[data-testid='spec-review-candidates'] > li").Should().HaveCount(15);
    }

    [Fact]
    public void FiltersShowCandidateDistributionAndPressedState()
    {
        var cut = RenderList(
            Candidate("FR-004: The system MUST approve requests.", ScenarioKind.Requirement),
            Candidate("FR-004: Approval MUST be audited.", ScenarioKind.Requirement),
            Candidate("Given a pending request, When a manager approves it per FR-004 and FR-007, Then the request is archived.", ScenarioKind.Test),
            Candidate("Which limit applies?", ScenarioKind.NeedsClarification));

        cut.Find("[data-testid='src-filter-all']").TextContent.Should().Contain("All candidates").And.Contain("4");
        cut.Find("[data-testid='src-filter-requirement']").TextContent.Should().Contain("Requirement candidates").And.Contain("2");
        cut.Find("[data-testid='src-filter-test']").TextContent.Should().Contain("Test candidates").And.Contain("1");
        cut.Find("[data-testid='src-filter-clarification']").TextContent.Should().Contain("Clarification candidates").And.Contain("1");
        cut.Find("[data-testid='src-filter-all']").GetAttribute("aria-pressed").Should().Be("true");

        cut.Find("[data-testid='src-filter-test']").Click();

        cut.Find("[data-testid='src-filter-test']").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid='src-filter-all']").GetAttribute("aria-pressed").Should().Be("false");
        cut.FindAll("[data-testid='spec-review-candidates'] > li").Should().ContainSingle()
            .Which.GetAttribute("data-candidate-type").Should().Be("Test");
        cut.Find("[data-testid='src-count']").TextContent.Should().Be("Showing 1 of 1 test candidate");
        cut.FindAll("[data-testid='src-show-all']").Should().BeEmpty();
    }

    [Fact]
    public void SearchMatchesIdentifierAndGivenWhenThenContent()
    {
        var cut = RenderList(
            Candidate("FR-004: The system MUST approve requests.", ScenarioKind.Requirement),
            Candidate("Given a pending request, When a manager approves it per FR-004 and FR-007, Then the request is archived.", ScenarioKind.Test),
            Candidate("Which limit applies?", ScenarioKind.NeedsClarification));

        cut.Find("[data-testid='src-search']").Input("archived");
        cut.FindAll("[data-testid='spec-review-candidates'] > li").Should().ContainSingle()
            .Which.GetAttribute("data-candidate-type").Should().Be("Test");

        cut.Find("[data-testid='src-search']").Input("no such text");
        cut.Find("[data-testid='src-empty']").TextContent.Should().Contain("No review candidates match");

        cut.Find("[data-testid='src-search']").Input("limit");
        cut.FindAll("[data-testid='spec-review-candidates'] > li").Should().ContainSingle()
            .Which.GetAttribute("data-candidate-type").Should().Be("Clarification");
    }

    [Fact]
    public void StructuredTestCandidateRendersGivenWhenThenAsSeparateLinesWithItsEntity()
    {
        var cut = RenderList(Candidate("Given a pending request, When a manager approves it per FR-004 and FR-007, Then the request is archived.", ScenarioKind.Test));

        var item = cut.Find("[data-testid='spec-review-candidates'] > li");
        item.QuerySelector(".src-type")!.TextContent.Should().Be("Test candidate");
        item.QuerySelector("[data-testid='src-entity']")!.TextContent.Should().Be("Refers to US 1 · Scenario 1");
        var parts = item.QuerySelectorAll(".traceability-item__scenario-part");
        parts.Should().HaveCount(3);
        parts[0].TextContent.Should().Be("Given a pending request");
        parts[1].TextContent.Should().Be("When a manager approves it per FR-004 and FR-007");
        parts[2].TextContent.Should().Be("Then the request is archived.");
        item.QuerySelector(".traceability-item__given")!.TextContent.Should().Be("Given");
        item.QuerySelector(".traceability-item__when")!.TextContent.Should().Be("When");
        item.QuerySelector(".traceability-item__then")!.TextContent.Should().Be("Then");
        // The keywords carry the component's CSS scope, so the keyword colours actually apply (they did not when built by hand).
        foreach (var keyword in item.QuerySelectorAll(".traceability-item__keyword"))
            keyword.Attributes.Should().Contain(a => a.Name.StartsWith("b-"));
    }

    [Fact]
    public void LongCandidateTextIsRenderedInFullWithShowMore()
    {
        var longText = "FR-099: " + string.Join(' ', Enumerable.Repeat("The system MUST keep the complete audit trail.", 12)) + " END-OF-TEXT";
        var cut = RenderList(Candidate(longText, ScenarioKind.Requirement));

        var item = cut.Find("[data-testid='spec-review-candidates'] > li");
        item.QuerySelector(".traceability-item__text")!.TextContent.Should().Be(longText);
        item.QuerySelector(".traceability-item__toggle")!.TextContent.Should().Be("Show more");
    }

    [Fact]
    public void CandidateListSaysCandidatesAreNotFindings()
    {
        var cut = RenderList(Candidate("FR-004: The system MUST approve requests.", ScenarioKind.Requirement));

        cut.Find(".src-note").TextContent.Should().Contain("not a finding, a defect or a failed requirement");
        cut.Markup.Should().NotContain("finding(s)").And.NotContain("Failed");
    }

    [Fact]
    public void ScenarioKeywordColoursAreNeverRedOrThePassedColour()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BirkNext.Web", "Components"))) dir = dir.Parent;
        var css = File.ReadAllText(Path.Combine(dir!.FullName, "BirkNext.Web", "Components", "TraceabilityItemText.razor.css"));
        foreach (var keyword in new[] { "given", "when", "then" })
        {
            var rule = css.Split('\n').Single(l => l.StartsWith($".traceability-item__{keyword} "));
            rule.Should().NotContainAny("danger", "#dc2626", "red", "success");
        }
    }

    [Fact]
    public void RelationshipPanelPlacesEachItemOnceAndListsAdditionalLinks()
    {
        var projection = SpecificationReviewProjection.Build(StorySpec);
        var cut = Render<SpecificationRelationshipsPanel>(p => p.Add(x => x.Projection, projection));

        cut.Find("[data-testid='srp-tests-linked']").TextContent.Should().Be("1 / 1");
        cut.Find("[data-testid='srp-tests-story']").TextContent.Should().Be("1 / 1");
        cut.Find("[data-testid='srp-clar-linked']").TextContent.Should().Be("1 / 2");
        cut.Find("[data-testid='srp-clar-spec']").TextContent.Should().Be("1");

        // The multi-linked test appears once, under its user story, with both requirement links as badges.
        var testItems = cut.FindAll("[data-testid='srp-item'][data-entity='US-001/S1']");
        testItems.Should().ContainSingle();
        testItems[0].Closest("[data-testid='srp-group']")!.GetAttribute("data-entity").Should().Be("US-001");
        testItems[0].QuerySelectorAll("[data-testid='srp-link']").Select(l => l.TextContent)
            .Should().BeEquivalentTo("Also verifies FR-004 (reference)", "Also verifies FR-007 (reference)");
        testItems[0].QuerySelectorAll(".traceability-item__scenario-part").Should().HaveCount(3);

        // The clarification naming FR-004 is placed under FR-004; the general one stays at specification level.
        var underRequirement = cut.Find("[data-testid='srp-item'][data-entity='FR-004'] [data-testid='srp-item']");
        underRequirement.GetAttribute("data-entity").Should().Be("CL#1");
        underRequirement.TextContent.Should().Contain("Placed by explicit reference").And.Contain("Answer: 10 000 NOK.");
        cut.Find("[data-testid='srp-group'][data-entity='specification-clarifications'] [data-testid='srp-item']")
            .GetAttribute("data-entity").Should().Be("CL#2");
        cut.Markup.Should().Contain("not a coverage verdict");
    }

    [Fact]
    public void RelationshipPanelReportsDuplicateIdentifiers()
    {
        var projection = SpecificationReviewProjection.Build("# Spec\n\n## Requirements\n\n- **FR-001**: A MUST hold.\n- **FR-001**: B MUST hold.\n");
        var cut = Render<SpecificationRelationshipsPanel>(p => p.Add(x => x.Projection, projection));

        cut.Find("[data-testid='srp-duplicates']").TextContent.Should().Contain("FR-001 (2 definitions)");
        cut.FindAll("[data-testid='srp-item'][data-entity='FR-001']").Should().ContainSingle();
    }

    [Fact]
    public void RelationshipPanelRendersNothingWithoutEntities()
    {
        var cut = Render<SpecificationRelationshipsPanel>(p => p.Add(x => x.Projection, SpecificationEntityProjection.Empty));
        cut.Markup.Trim().Should().BeEmpty();
    }

    private IRenderedComponent<SpecificationReviewCandidateList> RenderList(params ExtractionCandidate[] candidates)
    {
        var projection = SpecificationReviewProjection.Build(StorySpec);
        var review = SpecificationReviewProjection.BuildCandidates(projection, candidates);
        return Render<SpecificationReviewCandidateList>(p => p
            .Add(x => x.Candidates, review)
            .Add(x => x.Summary, SpecificationReviewProjection.Summarize(projection, review)));
    }

    private static ExtractionCandidate Candidate(string title, ScenarioKind kind) => new()
    {
        Title = title,
        Classification = kind,
        ClassificationSignal = ClassificationSignal.Default,
        SourceBlockType = BlockType.UnorderedListItem,
    };
}
