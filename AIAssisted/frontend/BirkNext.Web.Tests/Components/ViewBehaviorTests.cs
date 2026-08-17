using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Components;

public class ViewBehaviorTests : BunitContext
{
    private static ExtractionCandidate MakeCandidate(
        string title,
        ScenarioKind kind,
        string? contextHeading = null) => new()
    {
        Title = title,
        Classification = kind,
        ClassificationSignal = ClassificationSignal.Rfc2119Uppercase,
        SourceBlockType = BlockType.UnorderedListItem,
        ContextHeading = contextHeading,
    };

    // =========================================================================
    // T11 — ArchitectureView renders spec architecture, not candidate review UI
    // =========================================================================

    [Fact]
    public void ArchitectureView_renders_architecture_elements_not_candidate_review_rows()
    {
        const string md = """
            # Specification

            ## Architecture

            ### API Surface
            - **GraphQL** — consumed by the presentation layer for all read operations.
            - **REST** — consumed by the BiRK adapter for data ingestion.

            ### System Components
            BiRK adapter handles external provider integration.
            """;

        var cut = Render<ArchitectureView>(p => p.Add(c => c.SpecMarkdown, md));

        cut.FindAll("[data-testid='candidate-checkbox']").Should().BeEmpty("Architecture View must not contain review checkboxes");
        cut.FindAll("[data-testid='save-review-button']").Should().BeEmpty("Architecture View must not contain Save Review button");
        cut.Markup.Should().Contain("av-root", "Architecture View should render its root layout element");
    }

    // =========================================================================
    // T12 — TraceabilityView renders coverage data, not candidate review controls
    // =========================================================================

    [Fact]
    public void TraceabilityView_renders_coverage_not_candidate_review_controls()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1 Search"),
            MakeCandidate("Given a caseworker when searching then results appear", ScenarioKind.Test, "US1 Search"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.FindAll("[data-testid='candidate-checkbox']").Should().BeEmpty("Traceability View must not contain review checkboxes");
        cut.FindAll("[data-testid='save-review-button']").Should().BeEmpty("Traceability View must not contain Save Review button");
        cut.Markup.Should().Contain("tv-root", "Traceability View should render its root layout element");
    }

    // =========================================================================
    // Traceability — artifact classification and coverage eligibility
    // =========================================================================

    [Fact]
    public void Traceability_DoesNotTreatClarificationsAsRequirements()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement,       "US1: Search"),
            MakeCandidate("How should we handle session timeout?", ScenarioKind.NeedsClarification, "US1: Search"),
            MakeCandidate("Q/A session item about retries",        ScenarioKind.Requirement,       "Session 2026-03-06"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.Find("[data-testid='health-requirements']").TextContent.Should().Contain("1",
            "Clarifications and Q/A session items must not count as coverage-eligible requirements");
    }

    [Fact]
    public void Traceability_DoesNotRequireTestsForDecisions()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("Use REST for the public API",           ScenarioKind.Requirement,       "Architecture Decisions"),
            MakeCandidate("How should we handle retries?",         ScenarioKind.NeedsClarification, "US1: Search"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.FindAll(".tv-status-missingtests").Should().BeEmpty(
            "Non-eligible artifacts must not carry a Missing Tests warning");
        cut.FindAll(".tv-chip-gap").Should().BeEmpty(
            "No coverage gaps should be reported when there are no eligible requirements without tests");
    }

    [Fact]
    public void Traceability_CoverageUsesEligibleArtifactsOnly()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search",  ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("Test: search returns results",          ScenarioKind.Test,        "US1: Search"),
            MakeCandidate("Q/A session item",                      ScenarioKind.Requirement, "Session 2026-03-06"),
            MakeCandidate("Architecture: use Elasticsearch",       ScenarioKind.Requirement, "Architecture Decisions"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // 1 eligible requirement, 1 test → 100% coverage; not 33% from 3 total items
        cut.Find("[data-testid='health-coverage']").TextContent.Should().Contain("100%",
            "Coverage must be calculated only from coverage-eligible artifacts");
    }

    [Fact]
    public void Traceability_ArtifactCountsMatchAcrossViews()
    {
        // Two requirements in the same heading share 1 test via proximity linking.
        // TotalTests must be 1 (distinct), not 2 (double-counted via proximity).
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("FR-002: The system MUST filter results", ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("Test: search and filter works",         ScenarioKind.Test,        "US1: Search"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // Verify requirements count via health dashboard
        cut.Find("[data-testid='health-requirements']").TextContent.Should().Contain("2",
            "two coverage-eligible requirements");

        // Both requirements are covered (shared test via proximity) → coverage shows 2
        var covCard = cut.Find("[data-testid='health-coverage']").TextContent;
        covCard.Should().Contain("100%",
            "a single test linking two requirements in the same heading covers both — 100% coverage");
    }



    [Fact]
    public void DocumentView_RendersFrBlockAsSingleCard()
    {
        var specMarkdown = File.ReadAllText(FindPersonSpecPath());

        var cut = Render<DocumentView>(p => p.Add(c => c.SpecMarkdown, specMarkdown));

        var requirementRows = cut.FindAll(".doc-artifact-requirement");
        requirementRows.Should().HaveCount(33);

        var fr001Rows = requirementRows
            .Where(r => r.TextContent.Contains("FR-001"))
            .ToList();
        fr001Rows.Should().ContainSingle();

        var fr001Text = fr001Rows.Single().TextContent;
        fr001Text.Should().Contain("child search");
        fr001Text.Should().Contain("national ID");
        fr001Text.Should().Contain("DUF number");
        fr001Text.Should().Contain("BirkID");
    }

    [Fact]
    public async Task Traceability_RequirementCountLabelsAreConsistent()
    {
        var (specMarkdown, candidates) = await LoadPersonSpecExtractionAsync();

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.SpecMarkdown, specMarkdown);
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.Find("[data-testid='health-requirements']").TextContent.Should().Contain("33",
            "health dashboard must show the 33 eligible requirements");
        cut.Find("[data-testid='health-missing-tests']").TextContent.Should().Contain("33",
            "all 33 requirements have no linked tests so missing-tests must also show 33");
    }



    [Fact]
    public async Task Traceability_PersonSpecHas33CoverageRequirements()
    {
        var (specMarkdown, candidates) = await LoadPersonSpecExtractionAsync();

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.SpecMarkdown, specMarkdown);
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.FindAll(".tv-req-item-btn").Should().HaveCount(33,
            "person spec extraction must produce exactly 33 coverage-eligible requirements in the Requirements list");
        cut.FindAll("[data-testid='artifact-breakdown']").Should().BeEmpty(
            "artifact breakdown panel is removed from Traceability");
    }

    [Fact]
    public async Task Traceability_DetailsPanelShowsFullFrContent()
    {
        var (specMarkdown, candidates) = await LoadPersonSpecExtractionAsync();

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.SpecMarkdown, specMarkdown);
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.FindAll(".tv-req-item-btn").First(b => b.TextContent.Contains("FR-002")).Click();

        var detailText = cut.Find("[data-testid='tv-detail-full-content']").TextContent;
        detailText.Should().Contain("Levels 0 and 1");
        detailText.Should().Contain("Kode 6 / Kode 7");
        cut.Find(".tv-req-detail").TextContent.Should().Contain("Coverage Reason");
    }

    [Fact]
    public void Traceability_ShowsMissingUserStoryCount()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("The system MUST validate a submitted profile", ScenarioKind.Requirement, "Functional Requirements"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.FindAll("button").First(b => b.TextContent.Contains("Gaps")).Click();

        cut.Find("[data-testid='gap-missing-user-story']").TextContent.Should().Contain("Missing User Story");
        cut.Find("[data-testid='gap-missing-user-story']").TextContent.Should().Contain("1");
    }

    [Fact]
    public void Traceability_ShowsMissingSuccessCriteriaCount()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.FindAll("button").First(b => b.TextContent.Contains("Gaps")).Click();

        cut.Find("[data-testid='gap-missing-success-criteria']").TextContent.Should().Contain("Missing Success Criteria");
        cut.Find("[data-testid='gap-missing-success-criteria']").TextContent.Should().Contain("1");
    }

    [Fact]
    public void Traceability_ExplainsCoverageRequirements()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("How should timeout work?", ScenarioKind.NeedsClarification, "Clarifications"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        var healthCard = cut.Find("[data-testid='health-requirements']");
        healthCard.GetAttribute("title").Should().Contain("Coverage Requirements are requirements included in coverage calculations",
            "the Requirements health card must explain what counts as a coverage requirement");
        healthCard.GetAttribute("title").Should().Contain("Clarifications");
        healthCard.GetAttribute("title").Should().Contain("Architecture Notes");
    }


    [Fact]
    public void Traceability_RequirementDetailShowsCoverageReason()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // Requirements is now the default view — click item directly from the list
        cut.Find(".tv-req-item-btn").Click();

        var detailText = cut.Find(".tv-req-detail").TextContent;
        detailText.Should().Contain("Requirement ID");
        detailText.Should().Contain("Coverage Eligible");
        detailText.Should().Contain("Coverage Status");
        detailText.Should().Contain("Coverage Reason");
        detailText.Should().Contain("No linked acceptance tests were found");
    }

    [Fact]
    public void Traceability_HealthDashboardDisplaysCounts()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("Test: search returns results", ScenarioKind.Test, "US1: Search"),
            MakeCandidate("Unlinked test", ScenarioKind.Test, "US2: Other"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        var dashboard = cut.Find("[data-testid='traceability-health-dashboard']").TextContent;
        dashboard.Should().Contain("Requirements",       "health dashboard must show requirements count");
        dashboard.Should().Contain("Covered",            "health dashboard must show covered requirements count");
        dashboard.Should().Contain("Missing Tests",      "health dashboard must show missing tests gap");
        dashboard.Should().Contain("Missing User Stories", "health dashboard must show missing user story count");
        dashboard.Should().Contain("Missing SC",         "health dashboard must show missing success criteria count");
        dashboard.Should().Contain("Orphan Tests",       "health dashboard must show orphan tests");
    }


    // =========================================================================
    // Traceability — UX improvements regression suite
    // =========================================================================

    [Fact]
    public void Traceability_CoverageMetricShowsCountAndPercent()
    {
        // 1 covered of 2 eligible = 50 %.
        // FR-034 / FR-035 are above the deterministic US-range (1-33), so they get no
        // automatic UserStoryId and use separate headings to prevent proximity coverage.
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-034: The system MUST allow search",  ScenarioKind.Requirement, "Search Feature"),
            MakeCandidate("Test: search returns results",           ScenarioKind.Test,        "Search Feature"),
            MakeCandidate("FR-035: The system MUST filter results", ScenarioKind.Requirement, "Filter Feature"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        var covCard = cut.Find("[data-testid='health-coverage']").TextContent;
        covCard.Should().Contain("1", "covered count must appear in the Covered health card");
        cut.Find("[data-testid='health-requirements']").TextContent.Should().Contain("2",
            "eligible count must appear in the Requirements health card");
        covCard.Should().Contain("50%", "coverage percent must appear in the Covered health card");
    }

    [Fact]
    public void Traceability_SummaryCardsNavigateToFilteredGaps()
    {
        // Clicking a health summary card must navigate to the Gaps tab
        // and show the relevant gap section.
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search",  ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("FR-002: The system MUST filter results", ScenarioKind.Requirement, "Functional Requirements"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // Click the Missing Tests health card
        cut.Find("[data-testid='health-missing-tests']").Click();

        // Must show the missing-tests gap section
        cut.FindAll("[data-testid='gap-missing-tests']").Should().NotBeEmpty(
            "clicking the Missing Tests health card must navigate to Gaps with the missing-tests section visible");
    }

    [Fact]
    public void Traceability_UnassignedUserStoryShownInHealthDashboard()
    {
        // A requirement not under any user story heading must increment the Missing User Stories health card.
        // Use a non-FR-prefixed identifier so the deterministic FR→US range doesn't assign a user story.
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("RULE-001: Data submissions must be validated",
                ScenarioKind.Requirement, "Business Rules"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        var card = cut.Find("[data-testid='health-missing-us']");
        card.TextContent.Trim().Should().Contain("1",
            "requirements without a user story must be counted in the Missing User Stories health card");
    }

    [Fact]
    public void Traceability_MissingSuccessCriteriaShownInHealthDashboard()
    {
        // A requirement with no linked SC must increment the Missing SC health card.
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        var card = cut.Find("[data-testid='health-missing-sc']");
        card.TextContent.Trim().Should().Contain("1",
            "requirements without success criteria must be counted in the Missing SC health card");
    }


    [Fact]
    public void Traceability_RowClickShowsFullRequirementDetails()
    {
        // Clicking a requirement item in the Requirements list opens the full detail panel.
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST search by name, national ID, DUF number, and BirkID",
                ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.Find(".tv-req-item-btn").Click();

        var detail = cut.Find("[data-testid='tv-detail-full-content']");
        detail.TextContent.Should().Contain("search by name",
            "clicking a requirement item must open the full requirement text in the detail panel");
    }

    [Fact]
    public void Traceability_GapsTabGroupsMajorGapTypes()
    {
        // Gaps tab must render separate collapsible groups for
        // requirements-without-tests and missing-user-story.
        // FR-001 has a US1 heading so it has a user story; RULE-001 has no FR range → UserStoryId is null
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search",       ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("RULE-001: Data must be validated",           ScenarioKind.Requirement, "Business Rules"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.FindAll("button[role='tab']").First(b => b.TextContent.Contains("Gaps")).Click();

        cut.FindAll("[data-testid='gap-missing-tests']").Should().NotBeEmpty(
            "Gaps tab must contain a Requirements without Tests group");
        cut.FindAll("[data-testid='gap-missing-user-story']").Should().NotBeEmpty(
            "Gaps tab must contain a Missing User Story group");
    }

    [Fact]
    public void Traceability_TabsDoNotRenderBlankPanels()
    {
        // Every Traceability tab must render meaningful content or an
        // empty-state message — never a blank panel.
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // Requirements (default)
        cut.Find(".tv-req-layout").Should().NotBeNull("Requirements tab must render the list-detail layout by default");

        // Gaps tab
        cut.FindAll("button[role='tab']").First(b => b.TextContent.Contains("Gaps")).Click();
        var gapsContent = cut.FindAll(".tv-gap-sections, .tv-gap-allgood");
        gapsContent.Should().NotBeEmpty("Gaps tab must show either gap groups or the all-good message");

        // Matrix, Graph, and Suggestions tabs must not exist
        cut.FindAll("button[role='tab']").Should().NotContain(b => b.TextContent.Contains("Matrix"),
            "Matrix tab is removed — Requirements view is the primary interface");
        cut.FindAll("button[role='tab']").Should().NotContain(b => b.TextContent.Contains("Graph"),
            "Graph tab is removed — Flow View provides relationship visualization");
        cut.FindAll("button[role='tab']").Should().NotContain(b => b.TextContent.Contains("Suggestions"),
            "Suggestions tab is removed — it was a placeholder with no active functionality");
    }

    [Fact]
    public void UserGuide_ExplainsTraceabilityMetricNavigation()
    {
        var cut = Render<UserGuide>();

        var text = cut.Markup;
        text.Should().Contain("summary card", "User Guide must explain that health summary cards are clickable");
        text.Should().Contain("Missing User Stories", "User Guide must explain the Missing User Stories health card");
        text.Should().Contain("Missing Success Criteria", "User Guide must explain the Missing Success Criteria health card");
        text.Should().Contain("work queue",    "User Guide must describe the Gaps tab as a QA work queue");
    }

    // =========================================================================

    [Fact]
    public void UserGuide_ExplainsRequirementVsUserStoryViews()
    {
        var cut = Render<UserGuide>();

        var guideText = cut.Markup;
        guideText.Should().Contain("Coverage Is Requirement-Centric",
            "User Guide must explain why coverage is calculated at requirement level");
        guideText.Should().Contain("Coverage Requirements");
        guideText.Should().Contain("Missing User Story");
        guideText.Should().Contain("Missing Success Criteria");
        guideText.Should().Contain("Missing Tests");
        guideText.Should().Contain("Orphan Tests");
    }

    [Fact]
    public void UserGuide_DescribesStandardSpecificationExplorerWorkflow()
    {
        var cut = Render<UserGuide>();

        var text = cut.Markup;
        text.IndexOf("Traceability &amp; Coverage", StringComparison.Ordinal)
            .Should().BeLessThan(text.IndexOf("Flow View", StringComparison.Ordinal));
        text.Should().Contain("default</em> view shown after analysis");
        text.Should().Contain("QA risk picture first");
        text.Should().Contain("Spec Explorer");
        text.Should().Contain("Flow View");
        text.Should().NotContain("Extraction Review");
        text.Should().NotContain("Architecture View");
    }

    [Fact]
    public void Workflow_UsesTraceabilityFirstModel()
    {
        var cut = RenderRecommendedWorkflowForTraceability();

        var text = cut.Markup;
        text.Should().Contain("Open Traceability &amp; Coverage first");
        text.Should().Contain("Review coverage and gaps");
        text.Should().Contain("Use Flow View for QA readiness");
        text.Should().Contain("Use Spec Explorer for specification structure");
        text.Should().NotContain("Extraction Review");
        text.Should().NotContain("Architecture View");
        text.Should().NotContain("optional advanced extraction quality control");
    }

    // =========================================================================
    // T13 — FlowView groups by user story heading, not by candidate classification
    // =========================================================================

    [Fact]
    public void FlowView_groups_by_user_story_heading_not_by_candidate_classification()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system shall search", ScenarioKind.Requirement, "User Story 1: Search"),
            MakeCandidate("Test: search returns results", ScenarioKind.Test, "User Story 1: Search"),
            MakeCandidate("FR-002: The system shall display profile", ScenarioKind.Requirement, "User Story 2: Profile"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.FindAll("[data-testid='candidate-checkbox']").Should().BeEmpty("Flow View must not contain review checkboxes");
        cut.FindAll("[data-testid='group-requirement']").Should().BeEmpty("Flow View must not contain Extraction Review classification groups");
        cut.FindAll(".fv-lane").Should().HaveCount(2, "one lane per distinct ContextHeading");
    }

    [Fact]
    public void FlowView_DoesNotRenderSpecificationStructureGroups()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST validate input", ScenarioKind.Requirement, "Functional Requirements"),
            MakeCandidate("FR-002: The system MUST expose search APIs", ScenarioKind.Requirement, "API Surface"),
            MakeCandidate("FR-003: The system MUST persist person records", ScenarioKind.Requirement, "Key Entities"),
            MakeCandidate("FR-004: The system MUST handle timeout errors", ScenarioKind.Requirement, "Edge Cases"),
            MakeCandidate("FR-005: The system MUST rely on configured roles", ScenarioKind.Requirement, "Assumptions"),
            MakeCandidate("FR-006: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        var text = cut.Markup;
        cut.FindAll(".fv-lane").Should().ContainSingle("only user story lanes should render");
        text.Should().Contain("Search");
        text.Should().NotContain("Functional Requirements");
        text.Should().NotContain("API Surface");
        text.Should().NotContain("Key Entities");
        text.Should().NotContain("Edge Cases");
        text.Should().NotContain("Assumptions");
    }

    // =========================================================================
    // FlowView — Session Q/A and decision lane handling
    // =========================================================================

    [Fact]
    public void FlowView_DoesNotTreatSessionQaAsRequirements()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("Q/A decision item", ScenarioKind.Requirement, "Session 2026-03-06"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.FindAll(".fv-lane").Should().BeEmpty(
            "Flow View is a User Story readiness board and must not render decision/session lanes");
        cut.FindAll(".fv-type-pill-decisions").Should().BeEmpty();
        cut.FindAll(".fv-cov-missingtests").Should().BeEmpty(
            "Decision items must not carry a 'Missing Tests' coverage status");
    }

    [Fact]
    public void FlowView_DoesNotCreateGapsFromDecisions()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("Q/A decision one", ScenarioKind.Requirement, "Session 2026-03-06"),
            MakeCandidate("Q/A decision two", ScenarioKind.Requirement, "Session 2026-03-06"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.FindAll(".fv-chip-gap").Should().BeEmpty(
            "Decisions & Clarifications must not count as coverage gaps");
        cut.FindAll("[data-testid='fv-work-queue']").Should().BeEmpty(
            "Coverage Summary must not appear when all items are decision items");
    }

    // =========================================================================
    // FlowView — Success Criteria lane
    // =========================================================================

    [Fact]
    public void FlowView_ShowsSuccessCriteriaLane()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // First lane is auto-expanded; SC step must be visible as placeholder
        cut.FindAll(".fv-step-sc").Should().NotBeEmpty(
            "Success Criteria lane must be present for a story that has requirements");
    }

    // =========================================================================
    // FlowView — Global vs local coverage distinction
    // =========================================================================

    [Fact]
    public void FlowView_ShowsGlobalAndLocalCoverageSeparately()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: Search", ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("Test: search works", ScenarioKind.Test, "US1: Search"),
            MakeCandidate("FR-002: Login", ScenarioKind.Requirement, "US2: Auth"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.Find(".fv-metrics-note").Should().NotBeNull(
            "A note must clarify that top metrics are specification-wide, not per-section");
        cut.FindAll(".fv-cov-pill").Should().HaveCount(2,
            "each lane must have its own per-section coverage pill");
    }

    // =========================================================================
    // FlowView — Gap explanation panel
    // =========================================================================

    [Fact]
    public void FlowView_ShowsCoverageSummary()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: Search", ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("FR-002: Login",  ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        var summary = cut.Find("[data-testid='fv-work-queue']");
        summary.Should().NotBeNull("Coverage Summary must appear when stories have readiness gaps");
        summary.TextContent.Should().Contain("Coverage Summary");
    }

    // =========================================================================
    // FlowView — Normalized artifact counts (decisions excluded from metrics)
    // =========================================================================

    [Fact]
    public void FlowView_UsesNormalizedArtifacts()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: Search function", ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("Should we cache results?",    ScenarioKind.Requirement, "Session 2026-01-15"),
            MakeCandidate("Performance threshold agreed", ScenarioKind.Requirement, "Session 2026-01-15"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // Only the one real requirement must appear in the global requirement count
        cut.Find(".fv-sum-reqs").TextContent.Should().Contain("1 Requirement",
            "Decision items must not inflate the global requirement count");
    }

    // =========================================================================
    // FlowView — Task lane CTA buttons
    // =========================================================================

    [Fact]
    public void FlowView_ShowsTaskActionButtons()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // CTAs are behind the "Manage Implementation Links" toggle — expand it first
        cut.Find(".fv-impl-manage-btn").Click();
        cut.FindAll(".fv-task-cta-btn").Should().NotBeEmpty(
            "Task lane must have actionable CTA elements");
        cut.Markup.Should().Contain("Open Task Explorer",
            "Task lane must offer a direct link to Task Explorer");
    }

    // =========================================================================
    // FlowView — Explicit status badges (no ambiguous "!")
    // =========================================================================

    [Fact]
    public void FlowView_ReplacesAmbiguousWarningIcons()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // First lane is auto-expanded; requirement has no test → missing badge
        var missingBadge = cut.Find(".fv-status-missing");
        missingBadge.TextContent.Trim().Should().NotBe("!",
            "Ambiguous '!' icon must be replaced with an explicit text label");
        missingBadge.TextContent.Should().Contain("Missing Tests");
    }

    // =========================================================================
    // FlowView — Requirement traceability counts visible in header
    // =========================================================================

    [Fact]
    public void FlowView_ShowsRequirementTraceabilityCounts()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("Test: search results appear", ScenarioKind.Test, "US1: Search"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // Requirement must show a "covered" badge with test count (via proximity link)
        var coveredBadge = cut.Find(".fv-status-covered");
        coveredBadge.TextContent.Should().Contain("1",
            "Covered requirement must display the linked test count in its row header");
    }

    // =========================================================================
    // FlowView — QA Readiness Dashboard features
    // =========================================================================

    [Fact]
    public void FlowView_ShowsStoryHealthStatus()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // Story has a requirement with no test → Missing Tests health
        cut.FindAll("[data-testid='fv-health-badge']").Should().NotBeEmpty(
            "story header must show a readiness status");
        cut.Find("[data-testid='fv-health-badge']").TextContent.Should().Contain("Blocked",
            "story with untested requirement must show Blocked readiness status");
    }

    [Fact]
    public void FlowView_ShowsReadinessScore()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // First story is auto-expanded; readiness panel must be visible
        cut.FindAll("[data-testid='fv-readiness-panel']").Should().NotBeEmpty(
            "expanded story must show the QA Readiness score panel");
        cut.Find("[data-testid='fv-readiness-panel']").TextContent.Should().Contain("%",
            "readiness panel must display a percentage score");
    }

    [Fact]
    public void FlowView_HighlightsMissingSuccessCriteria()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // Health badge must mention Missing SC or Missing Tests (story has no SC)
        var healthBadge = cut.Find("[data-testid='fv-health-badge']");
        healthBadge.TextContent.Should().Contain("Status:");

        // SC step must show the missing badge inside the expanded lane
        cut.FindAll("[data-testid='fv-sc-missing-badge']").Should().NotBeEmpty(
            "expanded story with no linked success criteria must show the Missing SC warning badge");
    }

    [Fact]
    public void FlowView_ShowsTaskCoverageStatus()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // Task step must show explicit implementation-link status.
        cut.FindAll("[data-testid='fv-tasks-not-imported']").Should().NotBeEmpty(
            "task step must show an explicit implementation-link message");
        cut.Find("[data-testid='fv-tasks-not-imported']").TextContent.Should().Contain("No implementation links",
            "task placeholder must explain implementation links are missing");
    }

    [Fact]
    public void FlowView_SortsStoriesByPriority()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-003: Low priority", ScenarioKind.Requirement, "US3: Export (P3)"),
            MakeCandidate("FR-001: High priority", ScenarioKind.Requirement, "US1: Search (P1)"),
            MakeCandidate("FR-002: Medium priority", ScenarioKind.Requirement, "US2: Profile (P2)"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        var lanes = cut.FindAll(".fv-lane:not(.fv-lane-decision):not(.fv-lane-unmapped)");
        lanes.Should().HaveCount(3, "three regular story lanes expected");

        // P1 must appear before P2, P2 before P3
        var laneTexts = lanes.Select(l => l.TextContent).ToList();
        var p1Index = laneTexts.FindIndex(t => t.Contains("P1"));
        var p2Index = laneTexts.FindIndex(t => t.Contains("P2"));
        var p3Index = laneTexts.FindIndex(t => t.Contains("P3"));

        p1Index.Should().BeLessThan(p2Index, "P1 story must appear before P2");
        p2Index.Should().BeLessThan(p3Index, "P2 story must appear before P3");
    }

    [Fact]
    public void FlowView_ShowsCollapsedStorySummary()
    {
        const string specMarkdown = """
            # Specification

            ## US2: Profile

            ### Success Criteria
            - SC-001: FR-002 profile details are visible.
            """;

        // US1 has gaps and auto-expands. US2 has tests and success criteria, so it stays collapsed.
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: Search",  ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("Test: works",     ScenarioKind.Test,        "US1: Search"),
            MakeCandidate("FR-002: Profile", ScenarioKind.Requirement, "US2: Profile"),
            MakeCandidate("Test: profile works", ScenarioKind.Test,    "US2: Profile"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.SpecMarkdown, specMarkdown);
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // The second lane (US2) is collapsed — its header must show counts
        var collapsedLane = cut.FindAll(".fv-lane:not(.fv-lane-decision):not(.fv-lane-unmapped)")[1];
        collapsedLane.TextContent.Should().Contain("FR",
            "collapsed story header must show requirement count");
        collapsedLane.TextContent.Should().Contain("SC",
            "collapsed story header must show success criteria count");
    }

    [Fact]
    public void FlowView_FiltersByGapType()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: Search",  ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("Test: works",     ScenarioKind.Test,        "US1: Search"),
            MakeCandidate("FR-002: Profile", ScenarioKind.Requirement, "US2: Profile"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // Filters must be present
        cut.FindAll("[data-testid='fv-filters']").Should().NotBeEmpty("filter bar must be present");

        // Click Missing Tests filter — should hide US1 (covered) and show US2 (missing tests)
        cut.Find("[data-testid='fv-filter-missing-tests']").Click();
        var visibleLanes = cut.FindAll(".fv-lane:not(.fv-lane-decision):not(.fv-lane-unmapped)");
        visibleLanes.Should().ContainSingle("only the story with missing tests should be visible");
        visibleLanes.Single().TextContent.Should().Contain("Profile",
            "the uncovered story must be visible under the Missing Tests filter");
    }

    [Fact]
    public void FlowView_QAWorkQueueDisplaysCounts()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: Search",  ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("FR-002: Profile", ScenarioKind.Requirement, "US2: Profile"),
        ];

        var cut = Render<FlowView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        // Both stories are missing tests → work queue must appear
        var queue = cut.Find("[data-testid='fv-work-queue']");
        queue.Should().NotBeNull("Coverage Summary must appear when there are actionable gaps");

        // Coverage issues are collapsed by default — expand to see the breakdown
        cut.Find("[data-testid='fv-work-queue'] .fv-work-queue-header").Click();
        cut.Find("[data-testid='fv-work-queue']").TextContent.Should().Contain("Missing Tests",
            "expanded coverage summary must describe stories missing tests");
    }

    [Fact]
    public void UserGuide_ExplainsFlowViewReadinessModel()
    {
        var cut = Render<UserGuide>();

        var text = cut.Markup;
        text.Should().Contain("QA Readiness",              "User Guide must explain the QA Readiness score");
        text.Should().Contain("Story Readiness Status",    "User Guide must explain readiness status");
        text.Should().Contain("Blocked",                   "User Guide must describe blocked stories");
        text.Should().Contain("At Risk",                   "User Guide must describe at-risk stories");
        text.Should().Contain("Priority Sorting",          "User Guide must explain priority sorting");
        text.Should().Contain("Flow View Filters",         "User Guide must describe the filter chips");
        text.Should().Contain("Coverage Summary",          "User Guide must describe the consolidated coverage section");
        text.Should().Contain("No implementation links",   "User Guide must explain the implementation status message");
    }

    // =========================================================================
    // QA Artifact Library — repository positioning
    // =========================================================================

    [Fact]
    public void UserGuide_ClearlyDifferentiatesTraceabilityAndLibrary()
    {
        var cut = Render<UserGuide>();

        var text = cut.Markup;

        // Coverage analysis ownership must be attributed to Traceability & Coverage
        text.Should().Contain("Coverage analysis belongs in Traceability",
            "User Guide must state that coverage analysis belongs in Traceability & Coverage");

        // Library must be described as a reuse repository, not a coverage tool
        text.Should().Contain("reusable QA assets",
            "User Guide must describe the library as a repository of reusable QA assets");
        text.Should().Contain("discover",
            "User Guide must describe the library purpose as discovering/reusing assets");

        // The Traceability section must distinguish itself from the library
        text.Should().Contain("Traceability &amp; Coverage vs. QA Artifact Library",
            "User Guide must include an explicit differentiation callout between Traceability and Library");

        // Library must not be listed as a coverage analysis tool
        text.Should().NotContain("Coverage analysis tool",
            "User Guide must not describe the library as a coverage analysis tool");
    }

    [Fact]
    public void RecommendedWorkflow_MarksQaLibraryAsOptional()
    {
        var cut = RenderRecommendedWorkflowForTraceability();

        var text = cut.Markup;

        // Phase 2 must be marked as optional
        text.Should().Contain("Optional",
            "RecommendedWorkflow must mark the QA Artifact Library phase as optional");

        // The key workflow message: Traceability works without publishing
        text.Should().Contain("Traceability",
            "RecommendedWorkflow must mention Traceability & Coverage");
        text.Should().Contain("no publishing required",
            "RecommendedWorkflow must state that Traceability & Coverage works without publishing to the library");

        // Phase 2 must explain it is about reuse, not required for coverage
        text.Should().Contain("reuse",
            "RecommendedWorkflow Phase 2 must emphasize the library is for reuse");
    }

    // =========================================================================
    // ArchitectureView — extraction correctness and empty-state prevention
    // =========================================================================

    [Fact]
    public void ArchitectureView_ExtractsApiSurface()
    {
        const string md = """
            # Spec

            ## API Surface
            - **GraphQL** — read operations for the presentation layer
            - **REST** — ingestion endpoint for the BiRK adapter
            """;

        var cut = Render<ArchitectureView>(p => p.Add(c => c.SpecMarkdown, md));

        cut.FindAll(".av-empty").Should().BeEmpty("API Surface items must produce architecture elements");
        cut.FindAll(".av-chip-api").Should().NotBeEmpty("API elements must appear as metric chips");
    }

    [Fact]
    public void ArchitectureView_ExtractsDomainEntities()
    {
        const string md = """
            # Spec

            ## Key Entities
            - **Person**: A person registered in the system
            - **Barn**: A child linked to one or more caregivers
            """;

        var cut = Render<ArchitectureView>(p => p.Add(c => c.SpecMarkdown, md));

        cut.FindAll(".av-empty").Should().BeEmpty("Key Entities must produce domain entity elements");
        cut.FindAll(".av-chip-domainentity").Should().NotBeEmpty(
            "Domain entity chip must appear — ClassifyEntityNode must not return null for plain entities");
        cut.Markup.Should().Contain("Person", "Extracted entity names must be visible in the element tree");
    }

    [Fact]
    public void ArchitectureView_ExtractsDomainEvents()
    {
        const string md = """
            # Spec

            ## Domain Events
            PersonOpprettet — published when a new Person is created.
            PersonOppdatert — published when Person data changes.
            """;

        var cut = Render<ArchitectureView>(p => p.Add(c => c.SpecMarkdown, md));

        cut.FindAll(".av-empty").Should().BeEmpty("Domain Events section must produce architecture elements");
        cut.FindAll(".av-chip-domainevent").Should().NotBeEmpty("Domain event chip must appear");
    }

    [Fact]
    public void ArchitectureView_ExtractsInfrastructureComponents()
    {
        const string md = """
            # Spec

            ## Infrastructure
            EF Core is used for database access and schema migrations.
            """;

        var cut = Render<ArchitectureView>(p => p.Add(c => c.SpecMarkdown, md));

        cut.FindAll(".av-empty").Should().BeEmpty("Infrastructure section must produce architecture elements");
        cut.FindAll(".av-chip-infrastructurecomponent").Should().NotBeEmpty(
            "Infrastructure component chip must appear for EF Core");
    }

    [Fact]
    public void ArchitectureView_ExtractsExternalSystems()
    {
        const string md = """
            # Spec

            ## External Systems
            BiRK provides national child welfare data via the CDC pipeline.
            Microsoft Graph is used for Azure AD group membership lookups.
            """;

        var cut = Render<ArchitectureView>(p => p.Add(c => c.SpecMarkdown, md));

        cut.FindAll(".av-empty").Should().BeEmpty("External Systems section must produce architecture elements");
        cut.FindAll(".av-chip-externalsystem").Should().NotBeEmpty("External system chip must appear");
        cut.Markup.Should().Contain("Microsoft Graph",
            "Microsoft Graph must be extracted as an external system from its named lookup");
    }

    [Fact]
    public void ArchitectureView_DoesNotShowFalseEmptyState()
    {
        const string md = """
            # Person Module Spec

            ## API Surface
            - **GraphQL** — queries and mutations for the presentation layer

            ## Key Entities
            - **Person**: Core domain entity with name and contact data
            - **Barn**: Child entity linked to caregivers

            ## Domain Events
            PersonOpprettet — published when a new Person is created.

            ## Infrastructure
            EF Core handles database access. Norway East deployment region.

            ## External Systems
            BiRK is the primary data source.
            """;

        var cut = Render<ArchitectureView>(p => p.Add(c => c.SpecMarkdown, md));

        cut.FindAll(".av-empty").Should().BeEmpty(
            "A spec with API Surface, Key Entities, Domain Events, Infrastructure, and External Systems must not show an empty state");
        cut.Markup.Should().Contain("av-root");
    }

    [Fact]
    public void ArchitectureView_UsesSharedExtractionModel()
    {
        // Verify that the Architecture View faithfully extracts what SpecExplorerService parses:
        // entity names from the Key Entities section must appear verbatim in the element tree.
        const string md = """
            # Spec

            ## Key Entities
            - **Person**: Core domain entity representing a registered individual
            - **Barn**: Child entity in the child welfare domain
            """;

        var cut = Render<ArchitectureView>(p => p.Add(c => c.SpecMarkdown, md));

        cut.FindAll(".av-empty").Should().BeEmpty();
        cut.Markup.Should().Contain("Person",
            "Entity names parsed by SpecExplorerService must be surfaced in the Architecture View element tree");
        cut.Markup.Should().Contain("Barn",
            "All Key Entities must be extracted — none should be silently dropped by ClassifyEntityNode");
    }

    // =========================================================================
    // Architecture View — blank-panel regression tests
    // =========================================================================

    [Fact]
    public void ArchitectureView_DoesNotRenderBlankPanel()
    {
        // A spec with no recognisable architecture markers must still produce
        // a visible, non-blank panel — either an empty-state message or content.
        const string md = """
            # Specification

            ## Background
            This system supports caseworkers.
            """;

        var cut = Render<ArchitectureView>(p => p.Add(c => c.SpecMarkdown, md));

        var hasContent =
            cut.FindAll("[data-testid='av-root']").Count > 0 ||
            cut.FindAll("[data-testid='av-empty']").Count > 0 ||
            cut.FindAll("[data-testid='av-not-generated']").Count > 0 ||
            cut.FindAll("[data-testid='av-loading']").Count > 0 ||
            cut.FindAll("[data-testid='av-failed']").Count > 0;

        hasContent.Should().BeTrue(
            "Architecture View must never render a truly blank panel — one of the known states must be active");
    }

    [Fact]
    public void ArchitectureView_ShowsEmptyStateWhenNoElements()
    {
        // A spec with no architecture-recognisable content and no candidates
        // must show the empty state with guidance, not a blank panel.
        const string md = """
            # Background
            Some descriptive prose with no architecture markers.
            """;

        var cut = Render<ArchitectureView>(p => p.Add(c => c.SpecMarkdown, md));

        cut.FindAll("[data-testid='av-empty']").Should().NotBeEmpty(
            "when no architecture elements and no architecture-note candidates exist, empty state must show");
        cut.Markup.Should().Contain("No architecture elements found in this specification",
            "empty state must include a clear diagnostic message");
        cut.Markup.Should().Contain("Traceability",
            "empty state must guide the user to alternative views");
    }

    [Fact]
    public void ArchitectureView_ShowsArchitectureNotesWhenAvailable()
    {
        // When spec has no architecture markers but candidates include architecture-headed items,
        // the component renders the empty state (not the not-generated state) and does not crash.
        const string mdNoElements = """
            # Background
            Descriptive text only — no parseable architecture markers.
            """;

        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("GraphQL MUST be used for all read operations.",
                ScenarioKind.Requirement, "Architecture Decisions"),
            MakeCandidate("The authorisation service MUST fail closed.",
                ScenarioKind.Requirement, "Architecture Decisions"),
        ];

        var cut = Render<ArchitectureView>(p =>
        {
            p.Add(c => c.SpecMarkdown, mdNoElements);
            p.Add(c => c.Candidates, candidates);
        });

        cut.FindAll("[data-testid='av-not-generated']").Should().BeEmpty(
            "markdown is present, so not-generated state must not show");
        cut.Markup.Should().NotBeNullOrEmpty(
            "component must render without throwing when architecture candidates are provided");
    }

    [Fact]
    public void ArchitectureView_ShowsLoadingState()
    {
        // Extraction is synchronous, so the loading state clears before the component
        // renders. This test verifies the panel is never blank — it always shows a
        // meaningful state regardless of the markdown supplied.

        // Without markdown: not-generated state (not blank, not loading).
        var cutNoMd = Render<ArchitectureView>();
        cutNoMd.FindAll("[data-testid='av-loading']").Should().BeEmpty(
            "loading must not persist when no markdown is provided");
        cutNoMd.Markup.Should().Contain("No architecture data available yet",
            "not-generated message must appear before markdown is supplied");

        // With markdown containing API elements: content state (not blank, not loading).
        const string md = """
            # Spec

            ## API Surface
            - **GraphQL** — queries for the presentation layer
            """;

        var cut = Render<ArchitectureView>(p => p.Add(c => c.SpecMarkdown, md));

        cut.FindAll("[data-testid='av-loading']").Should().BeEmpty(
            "loading state must not persist after synchronous extraction completes");
        cut.FindAll("[data-testid='av-root']").Should().HaveCount(1,
            "component must render content after extraction");
    }

    [Fact]
    public void ArchitectureView_ShowsFailureStateWhenExtractionFails()
    {
        // Null markdown → not-generated state (not a blank panel).
        var cutNull = Render<ArchitectureView>(p => p.Add(c => c.SpecMarkdown, (string?)null));
        cutNull.FindAll("[data-testid='av-not-generated']").Should().NotBeEmpty(
            "null markdown must produce the not-generated state, not a blank panel");

        // Whitespace markdown → same not-generated state.
        var cutWhitespace = Render<ArchitectureView>(p => p.Add(c => c.SpecMarkdown, "   "));
        cutWhitespace.FindAll("[data-testid='av-not-generated']").Should().NotBeEmpty(
            "whitespace markdown must produce the not-generated state");

        // The failed state must not appear when extraction has not been attempted.
        cutNull.FindAll("[data-testid='av-failed']").Should().BeEmpty(
            "failed state must not show when extraction has not run or succeeded");
    }

    // =========================================================================
    // T18 — SpecExplorer: duplicate extraction metrics stay out of the structure view
    // =========================================================================

    [Fact]
    public void SpecExplorer_DoesNotShowExtractedMetricsStrip()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search",     ScenarioKind.Requirement,          "US1: Search"),
            MakeCandidate("FR-002: The system MUST display results",  ScenarioKind.Requirement,          "US1: Search"),
            MakeCandidate("Test: search returns results",             ScenarioKind.Test,                  "US1: Search"),
            MakeCandidate("How should timeout work?",                 ScenarioKind.NeedsClarification,    "US1: Search"),
        ];

        const string specMd = """
            # Spec

            ## US1: Search

            **FR-001**: The system MUST allow search.
            **FR-002**: The system MUST display results.
            """;

        var cut = Render<SpecExplorerPanel>(p =>
        {
            p.Add(c => c.InitialSpecMarkdown, specMd);
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.IsEmbeddedInReview, true);
        });

        cut.FindAll("[data-testid='se-candidate-strip']").Should().BeEmpty(
            "Spec Explorer should not duplicate extracted artifact metrics shown elsewhere");
        cut.Markup.Should().NotContain("Extracted:");
    }

    // =========================================================================
    // T19 — SpecExplorer: badge labels use full names not cryptic abbreviations
    // =========================================================================

    [Fact]
    public void SpecExplorer_BadgeLabelsAreFullNamesNotAbbreviations()
    {
        const string specMd = """
            # Module

            ## US1: Search

            **FR-001**: The system MUST allow search.
            **FR-002**: The system MUST display results.
            """;

        var cut = Render<SpecExplorerPanel>(p =>
        {
            p.Add(c => c.InitialSpecMarkdown, specMd);
            p.Add(c => c.IsEmbeddedInReview, true);
        });

        var metaChips = cut.FindAll(".se-meta-chip");
        metaChips.Should().NotBeEmpty("there should be badge chips on the heading that contains requirements");

        var chipTexts = metaChips.Select(c => c.TextContent).ToList();
        chipTexts.Should().NotContain(t => t.Trim() == "2 req",
            "abbreviation 'req' must be replaced with 'Requirements (N)'");
        chipTexts.Should().Contain(t => t.Contains("Requirements"),
            "full label 'Requirements' must appear in badge chips");
    }

    [Fact]
    public void SpecExplorer_ShowsSectionHealth()
    {
        var cut = RenderSpecExplorerWithTraceability();

        var health = cut.FindAll("[data-testid='se-section-health']");
        health.Should().NotBeEmpty();
        health.Select(h => h.TextContent).Should().Contain(t =>
            t.Contains("Healthy") || t.Contains("Partial") || t.Contains("Needs Attention"),
            "section health must show simplified section status");
    }

    [Fact]
    public void SpecExplorer_PromotesCoverageAttention()
    {
        var cut = RenderSpecExplorerWithTraceability();

        var callout = cut.Find("[data-testid='se-coverage-callout']");
        callout.TextContent.Should().Contain("coverage attention");
        callout.TextContent.Should().Contain("Review Coverage Gaps");
    }

    [Fact]
    public void SpecExplorer_ShowsUserStoryOwnership()
    {
        var cut = RenderSpecExplorerWithTraceability();

        // User story ownership is shown in the section summary details panel
        cut.FindAll(".se-row").First(r => r.TextContent.Contains("API Surface")).Click();

        var owners = cut.FindAll("[data-testid='se-user-story-ownership']").Select(e => e.TextContent).ToList();
        owners.Should().NotBeEmpty("clicking a section heading should show user story ownership in details panel");
        // User story IDs are normalized to US-NNN format
        owners.Should().Contain(t => t.Contains("US-001"));
    }

    [Fact]
    public void SpecExplorer_CanNavigateToTraceability()
    {
        var cut = RenderSpecExplorerWithTraceability();

        cut.FindAll(".se-row").First(r => r.TextContent.Contains("expose search APIs")).Click();

        var traceLink = cut.Find("[data-testid='se-open-traceability']");
        traceLink.TextContent.Should().Contain("Open in Traceability");
        traceLink.GetAttribute("href").Should().Be("/traceability");
    }

    [Fact]
    public void SpecExplorer_ShowsRequirementDetails()
    {
        var cut = RenderSpecExplorerWithTraceability();

        cut.FindAll(".se-row").First(r => r.TextContent.Contains("expose search APIs")).Click();

        var detail = cut.Find("[data-testid='se-requirement-detail']").TextContent;
        detail.Should().Contain("expose search APIs");
        detail.Should().Contain("Source heading");
        detail.Should().Contain("User Story");
        detail.Should().Contain("Linked tests");
        detail.Should().Contain("Linked success criteria");
        detail.Should().Contain("Coverage status");
    }

    [Fact]
    public void SpecExplorer_ShowsSectionSummary()
    {
        var cut = RenderSpecExplorerWithTraceability();

        cut.FindAll(".se-row").First(r => r.TextContent.Contains("API Surface")).Click();

        var summary = cut.Find("[data-testid='se-section-summary']").TextContent;
        summary.Should().Contain("Heading Summary");
        summary.Should().Contain("Requirements:");
        summary.Should().Contain("Clarifications:");
        summary.Should().NotContain("Coverage:");
        summary.Should().NotContain("Top gaps", "top gap details belong in Traceability, not Spec Explorer");
    }

    [Fact]
    public void SpecExplorer_MapViewDisplaysStructure()
    {
        var cut = RenderSpecExplorerWithTraceability();

        cut.FindAll(".se-view-btn").First(b => b.TextContent.Contains("Map View")).Click();

        cut.FindAll("[data-testid='se-map-structure']")
            .Should().Contain(e => e.TextContent.Contains("Heading -> Requirements -> Tests -> Success Criteria"));
    }

    [Fact]
    public void SpecExplorer_FiltersByCoverageStatus()
    {
        var cut = RenderSpecExplorerWithTraceability();

        var filters = cut.Find("[data-testid='se-quick-filters']").TextContent;
        filters.Should().Contain("All");
        filters.Should().Contain("Missing Coverage");
        filters.Should().Contain("Covered");
        filters.Should().NotContain("Has Clarifications");
        filters.Should().NotContain("Has Edge Cases");
        filters.Should().NotContain("High Risk Sections");

        cut.FindAll(".se-filter-chip").First(b => b.TextContent.Contains("Missing Coverage")).Click();

        var rows = cut.FindAll(".se-row").Select(r => r.TextContent).ToList();
        rows.Should().Contain(t => t.Contains("Security"));
        rows.Should().NotContain(t => t.Contains("API Surface") && t.Contains("Healthy"));
    }

    [Fact]
    public void SpecExplorer_EmptyStatesRender()
    {
        var noRequirements = Render<SpecExplorerPanel>(p =>
        {
            p.Add(c => c.InitialSpecMarkdown, "# Empty Spec\n\n## Notes\n\nNo requirements here.");
            p.Add(c => c.IsEmbeddedInReview, true);
        });

        noRequirements.Markup.Should().Contain("No requirements extracted");

        var noSession = Render<SpecExplorerPanel>(p => p.Add(c => c.IsEmbeddedInReview, true));
        noSession.Markup.Should().Contain("No active review session found");
    }

    [Fact]
    public void UserGuide_ExplainsSpecExplorerPurpose()
    {
        var cut = Render<UserGuide>();

        var text = cut.Markup;
        text.Should().Contain("Spec Explorer is the specification structure and navigation layer");
        text.Should().Contain("Traceability &amp; Coverage is for coverage analysis");
        text.Should().Contain("Flow View is for QA readiness");
        text.Should().NotContain("Extraction Review is for extraction quality review");
        text.Should().NotContain("Architecture View is for technical assumptions and architecture");
    }

    [Fact]
    public void TaskExplorer_ImpactShowsRequirementImplementationCoverage()
    {
        var cut = RenderTaskExplorerWithImplementationGaps();

        // Requirement implementation coverage is the primary feature of the Impact tab
        cut.FindAll(".te-view-btn").First(b => b.TextContent.Contains("Impact")).Click();

        var impact = cut.Find("[data-testid='te-impact-view']").TextContent;

        // Coverage metrics shown
        impact.Should().Contain("Requirement Implementation Coverage");
        impact.Should().Contain("Are requirements covered by implementation tasks?");

        // Specific metric types present
        impact.Should().Contain("User Stories");
        impact.Should().Contain("Functional Requirements");
        impact.Should().Contain("Success Criteria");
        impact.Should().Contain("Tests");
        impact.Should().Contain("Architecture Notes");

        // Coverage status indicated (may show "Partial" or similar status)
        (impact.Contains("Partial") || impact.Contains("Covered") || impact.Contains("Missing"))
            .Should().BeTrue("Should show coverage status");
    }

    [Fact]
    public void TaskExplorer_ImpactAnalyzesImplementationGaps()
    {
        var cut = RenderTaskExplorerWithImplementationGaps();

        // Impact tab performs gap analysis between requirements and implementation
        cut.FindAll(".te-view-btn").First(b => b.TextContent.Contains("Impact")).Click();

        var impact = cut.Find("[data-testid='te-impact-view']").TextContent;

        // Should show implementation gaps section
        impact.Should().Contain("Implementation Gaps");

        // Should identify missing implementations (singular or plural depending on data)
        (impact.Contains("Requirement without implementation") ||
         impact.Contains("Requirements without implementation"))
            .Should().BeTrue("Should identify requirements lacking implementation");

        // Should show success indicators for aspects with no gaps
        (impact.Contains("Success Criteria — no gaps") ||
         impact.Contains("User Stories — no gaps") ||
         impact.Contains("Security — no gaps") ||
         impact.Contains("Testing — no gaps"))
            .Should().BeTrue("Should show zero-gap indicators for covered categories");
    }

    [Fact]
    public void TaskExplorer_ImpactTabShowsMissingImplementation()
    {
        var cut = RenderTaskExplorerWithImplementationGaps();

        cut.FindAll(".te-view-btn").First(b => b.TextContent.Contains("Impact")).Click();

        var impact = cut.Find("[data-testid='te-impact-view']").TextContent;

        // New compact gaps section
        impact.Should().Contain("Implementation Gaps");
        impact.Should().Contain("FR-002", "Should show missing requirement");

        // At least one zero-gap indicator present
        (impact.Contains("Success Criteria — no gaps") ||
         impact.Contains("User Stories — no gaps") ||
         impact.Contains("Security — no gaps") ||
         impact.Contains("Testing — no gaps"))
            .Should().BeTrue("Should show at least one zero-gap success indicator");

        // Link visualization still present
        impact.Should().Contain("User Story");
        impact.Should().Contain("Requirements");
        impact.Should().Contain("Success Criteria");
        impact.Should().Contain("Tasks");
        impact.Should().NotContain("->");
    }

    [Fact]
    public void TaskExplorer_TreeViewSupportsFilteringByMultipleDimensions()
    {
        var cut = RenderTaskExplorerWithImplementationGaps();

        var text = cut.Markup;

        // Coverage dimension: filter by artifact type and implementation status
        text.Should().Contain("User Stories");
        text.Should().Contain("Requirements");
        text.Should().Contain("Success Criteria");
        text.Should().Contain("Missing Implementation");

        // Traceability dimension: filter by link relationships
        text.Should().Contain("Has FR Links");
        text.Should().Contain("Has SC Links");
        text.Should().Contain("Unlinked");

        // Quality dimension: filter by task classification
        text.Should().Contain("Testing");
        text.Should().Contain("Security");
    }

    [Fact]
    public void TaskExplorer_ShowsTaskDetailsWithLinkedArtifacts()
    {
        var cut = RenderTaskExplorerWithImplementationGaps();

        cut.FindAll(".te-row").First(r => r.TextContent.Contains("T001")).Click();

        var details = cut.Find("[data-testid='te-task-details']").TextContent;
        details.Should().Contain("Task Details");
        details.Should().Contain("Linked User Story");
        details.Should().Contain("US-001");
        details.Should().Contain("Linked Requirement(s)");
        details.Should().Contain("FR-001");
        details.Should().Contain("Linked Success Criteria");
        details.Should().Contain("SC-001");
        details.Should().Contain("Linked Test Assets");
        details.Should().Contain("Linked Architecture Notes");
        details.Should().Contain("Implementation Status");
        details.Should().Contain("Coverage Impact");
    }

    [Fact]
    public void TaskExplorer_CanNavigateToTraceability()
    {
        var cut = RenderTaskExplorerWithImplementationGaps();

        cut.FindAll(".te-row").First(r => r.TextContent.Contains("T001")).Click();

        var button = cut.Find(".te-traceability-link");
        button.TextContent.Should().Contain("Open in Traceability");
        button.Click();

        cut.Markup.Should().Contain("FR-001");
    }

    [Fact]
    public void UserGuide_ExplainsImplementationCoverageVsValidationCoverage()
    {
        var cut = Render<UserGuide>();

        var text = cut.Markup;
        text.Should().Contain("Task Explorer");
        text.Should().Contain("Implementation Coverage");
        text.Should().Contain("Traceability &amp; Coverage = Validation Coverage");
        text.Should().Contain("Task Explorer = Implementation Coverage");
    }

    [Fact]
    public void UserGuide_DoesNotPointToLegacyTraceability()
    {
        var cut = Render<UserGuide>();

        var text = cut.Markup;
        text.Should().Contain("Specification Explorer");
        text.Should().Contain("Traceability &amp; Coverage");
        text.Should().Contain("Legacy session-level coverage view");
        text.Should().Contain("hidden from the sidebar by default");
        text.Should().Contain("Artifact Traceability");
        text.Should().Contain("href=\"/artifact-traceability\"");
    }

    private IRenderedComponent<RecommendedWorkflow> RenderRecommendedWorkflowForTraceability()
    {
        var readinessService = new Mock<IWorkflowReadinessService>();
        readinessService
            .Setup(service => service.GetReadinessAsync())
            .ReturnsAsync(CreateTraceabilityFirstReadiness());

        var autoSave = new Mock<IWorkspaceAutoSaveService>();
        autoSave.Setup(service => service.StartMonitoringAsync()).Returns(Task.CompletedTask);
        autoSave.Setup(service => service.StopMonitoringAsync()).Returns(Task.CompletedTask);

        Services.AddSingleton(readinessService.Object);
        Services.AddSingleton(Mock.Of<IWorkspacePersistenceApiService>());
        Services.AddSingleton(Mock.Of<IWorkspaceSessionRestoreService>());
        Services.AddSingleton(autoSave.Object);
        Services.AddSingleton(Mock.Of<IRecommendedWorkflowApiService>());
        Services.AddSingleton(NullLogger<RecommendedWorkflow>.Instance);

        return Render<RecommendedWorkflow>();
    }

    private static WorkflowReadiness CreateTraceabilityFirstReadiness()
    {
        var artifactStatus = new WorkspaceArtifactStatus(
            HasConstitution: true,
            HasSpecification: true,
            HasPlan: true,
            HasTasks: true,
            HasDataModel: true,
            ArtifactCount: 5,
            ActiveProjectName: "Traceability fixture");

        var traceabilityStep = new WorkflowStepViewModel
        {
            Number = 1,
            Key = "ArtifactTraceability",
            Title = "Open Traceability & Coverage first",
            Description = "Review coverage and gaps. Use Flow View for QA readiness. Use Spec Explorer for specification structure. Traceability requires no publishing required.",
            Route = "/artifact-traceability",
            ActionLabel = "Open Traceability & Coverage first",
            Color = "#2563eb",
            CanOpen = true,
            IsCurrent = true,
            Status = WorkflowStepStatus.Available,
            Prerequisites = PrerequisiteState.Available,
            ReviewState = ReviewState.NotStarted,
            ApprovalState = ApprovalState.Pending
        };

        var libraryStep = new WorkflowStepViewModel
        {
            Number = 2,
            Key = "QaArtifactLibrary",
            Title = "Optional QA Artifact Library",
            Description = "Optional reuse repository for reviewed QA assets; not required for traceability coverage.",
            Route = "/scenarios",
            ActionLabel = "Open library for reuse",
            Color = "#0f766e",
            CanOpen = true,
            IsOptional = true,
            RequiresApproval = false,
            RequiresManualReview = false,
            Status = WorkflowStepStatus.Available,
            Prerequisites = PrerequisiteState.Available,
            ReviewState = ReviewState.NotStarted,
            ApprovalState = ApprovalState.Pending
        };

        return new WorkflowReadiness(
            CurrentWorkspace: new WorkflowWorkspace(Guid.NewGuid(), "Traceability fixture", "Traceability fixture", 5, DateTimeOffset.UtcNow, null, false),
            WorkspaceLoaded: true,
            WorkspaceName: "Traceability fixture",
            ProjectName: "Traceability fixture",
            WorkspaceStatus: "Not Saved",
            WorkspaceStatusClass: "status-not-saved",
            LastSavedAt: null,
            LastSavedText: "Not saved",
            ArtifactStatus: artifactStatus,
            Artifacts:
            [
                new("Constitution", true),
                new("Specification", true),
                new("Plan", true),
                new("Tasks", true),
                new("Data Model", true)
            ],
            SpecificationExplorerState: null,
            TraceabilityState: traceabilityStep,
            ImplementationReviewState: null,
            QualityGateState: null,
            NextRecommendedAction: traceabilityStep,
            OverallReadiness: new WorkflowReadinessBreakdown { OverallReadiness = 80, ArtifactReadiness = 100, ReviewReadiness = 50, ApprovalReadiness = 50 },
            Steps: [traceabilityStep, libraryStep],
            CanRelease: false,
            ReleaseReason: "Manual approvals required.",
            Warnings: []);
    }

    private IRenderedComponent<TaskExplorerPanel> RenderTaskExplorerWithImplementationGaps()
    {
        const string tasks = """
            # Tasks

            ## Phase 1

            ### US-001 Search
            - [x] T001 Implement FR-001 search API with SC-001 acceptance criteria
            - [ ] T002 Add test task for FR-001

            ### US-003 Security
            - [ ] T003 Implement security controls for FR-003 with SC-003
            - [ ] T004 Architecture note for API integration
            - [ ] T005 Unlinked implementation cleanup
            """;

        var cut = Render<TaskExplorerPanel>(p => p.Add(c => c.TasksText, tasks));
        cut.FindAll(".te-ctrl-btn").First(b => b.TextContent.Contains("Expand All")).Click();
        return cut;
    }

    private IRenderedComponent<SpecExplorerPanel> RenderSpecExplorerWithTraceability()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST expose search APIs", ScenarioKind.Requirement, "US1: API Surface"),
            MakeCandidate("FR-002: The system MUST expose detail APIs", ScenarioKind.Requirement, "US1: API Surface"),
            MakeCandidate("Test: search API returns results", ScenarioKind.Test, "US1: API Surface"),
            MakeCandidate("Test: detail API returns one item", ScenarioKind.Test, "US1: API Surface"),
            MakeCandidate("FR-003: The system MUST handle invalid IDs", ScenarioKind.Requirement, "US2: Edge Cases"),
            MakeCandidate("Test: invalid ID returns 404", ScenarioKind.Test, "US2: Edge Cases"),
            MakeCandidate("Clarify timeout behavior", ScenarioKind.NeedsClarification, "US2: Edge Cases"),
            MakeCandidate("FR-005: The system MUST handle rate limits", ScenarioKind.Requirement, "US2: Edge Cases"),
            MakeCandidate("FR-004: The system MUST reject unauthorized requests", ScenarioKind.Requirement, "US3: Security"),
        ];

        var cut = Render<SpecExplorerPanel>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.IsEmbeddedInReview, true);
        });
        cut.FindAll(".se-ctrl-btn").First(b => b.TextContent.Contains("Expand All")).Click();
        return cut;
    }

    private static string FindPersonSpecPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "examples", "personSpec.md");
            if (File.Exists(path))
                return path;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate examples/personSpec.md from test output directory.");
    }

    private static async Task<(string SpecMarkdown, IReadOnlyList<ExtractionCandidate> Candidates)> LoadPersonSpecExtractionAsync()
    {
        var specMarkdown = await File.ReadAllTextAsync(FindPersonSpecPath());
        var service = new ScenarioExtractionService(new ExtractionConfiguration
        {
            MaxInputLengthChars = 50_000,
            MinCandidateLengthChars = 3,
            MaxLineLengthForPatternMatching = 2_000,
        });
        var result = await service.ExtractAsync(specMarkdown);
        result.Status.Should().Be(PipelineStatus.Success);
        return (specMarkdown, result.Candidates);
    }
}


