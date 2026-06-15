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
}
