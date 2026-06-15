using BirkNext.Web.Components;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;

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
        cut.FindAll("[data-testid='group-requirement']").Should().BeEmpty("Flow View must not contain Document View classification groups");
        cut.FindAll(".fv-lane").Should().HaveCount(2, "one lane per distinct ContextHeading");
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

        cut.FindAll(".fv-lane-decision").Should().HaveCount(1,
            "Session date heading must produce a decision lane, not a regular story lane");
        cut.Find(".fv-type-pill-decisions").Should().NotBeNull();
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
        cut.FindAll("[data-testid='gap-explanation-panel']").Should().BeEmpty(
            "Gap explanation panel must not appear when all items are decision items");
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
    public void FlowView_ShowsGapExplanationPanel()
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

        cut.Find("[data-testid='gap-explanation-panel']").Should().NotBeNull(
            "Gap explanation panel must appear when requirements have no test coverage");
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
        cut.Find(".fv-chip-req").TextContent.Should().Contain("1 Requirement",
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

        // First lane is auto-expanded
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
}
