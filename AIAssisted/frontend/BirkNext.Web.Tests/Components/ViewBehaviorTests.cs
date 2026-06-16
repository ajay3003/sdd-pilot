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

        cut.Find(".tv-chip-req").TextContent.Should().Contain("1 Requirement",
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
        cut.Find(".tv-chip-cov").TextContent.Should().Contain("100%",
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

        cut.Find(".tv-chip-test").TextContent.Should().Contain("1 Test",
            "Test count must use distinct IDs — a single test linked to 2 requirements counts once");
    }

    [Fact]
    public void Traceability_ShowsArtifactTypeBadges()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement,       "US1: Search"),
            MakeCandidate("How should we handle timeouts?",       ScenarioKind.NeedsClarification, "US1: Search"),
            MakeCandidate("Use GraphQL for the BFF layer",        ScenarioKind.Requirement,       "Architecture Decisions"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        cut.FindAll(".tv-matrix-row").Should().ContainSingle(
            "the primary matrix must render only coverage-eligible requirements");

        cut.FindAll("button").First(b => b.TextContent.Contains("Artifacts")).Click();

        cut.Find("[data-testid='noneligible-artifacts-panel']").Should().NotBeNull();
        cut.FindAll(".tv-type-badge").Should().NotBeEmpty(
            "Non-eligible artifacts must display artifact type badges in the artifacts panel");
        cut.Markup.Should().Contain("Clarification",
            "NeedsClarification items must be labelled as Clarification");
        cut.Markup.Should().Contain("Architecture Note",
            "Items from architecture headings must be labelled as Architecture Note");
    }

    [Fact]
    public void Traceability_MatrixExcludesNonEligibleArtifacts()
    {
        IReadOnlyList<ExtractionCandidate> candidates =
        [
            MakeCandidate("FR-001: The system MUST allow search", ScenarioKind.Requirement, "US1: Search"),
            MakeCandidate("How should we handle timeouts?", ScenarioKind.NeedsClarification, "Clarifications"),
            MakeCandidate("Infrastructure sizing MUST validate the SLA.", ScenarioKind.Requirement, "Assumptions"),
            MakeCandidate("GraphQL MUST be used for presentation reads.", ScenarioKind.Requirement, "Decisions"),
            MakeCandidate("Person entity MUST carry BirkId.", ScenarioKind.Requirement, "Key Entities"),
            MakeCandidate("Status: Draft", ScenarioKind.Requirement, "Metadata"),
        ];

        var cut = Render<TraceabilityView>(p =>
        {
            p.Add(c => c.Candidates, candidates);
            p.Add(c => c.Links, (IReadOnlyList<CandidateLinkEntry>)[]);
        });

        var matrixText = cut.Find(".tv-matrix-table").TextContent;
        matrixText.Should().Contain("allow search");
        matrixText.Should().NotContain("timeouts");
        matrixText.Should().NotContain("SLA");
        matrixText.Should().NotContain("presentation reads");
        matrixText.Should().NotContain("BirkId");
        matrixText.Should().NotContain("Draft");
        cut.Find(".tv-chip-req").TextContent.Should().Contain("1 Requirement");
        cut.Find(".tv-chip-gap").TextContent.Should().Contain("1 Gap");
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
    // T18 — SpecExplorer: candidate count strip shows extraction-derived counts
    // =========================================================================

    [Fact]
    public void SpecExplorer_CandidateCountStripShowsExtractionCounts()
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

        var strip = cut.Find("[data-testid='se-candidate-strip']");
        strip.TextContent.Should().Contain("Requirements (2)", "strip must show candidate-derived requirement count");
        strip.TextContent.Should().Contain("Tests (1)",        "strip must show candidate-derived test count");
        strip.TextContent.Should().Contain("Clarifications (1)", "strip must show candidate-derived clarification count");
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
}
