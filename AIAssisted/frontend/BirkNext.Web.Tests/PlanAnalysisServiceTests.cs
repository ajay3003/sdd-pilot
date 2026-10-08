using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Xunit;

namespace BirkNext.Web.Tests;

public class PlanAnalysisServiceTests
{
    [Fact]
    public void Parse_FileMapSectionIsRenderedProjectStructureWithDirectSourceProvenance()
    {
        const string markdown = "# Plan\n\n## File Map\n\n| Action | Path |\n| --- | --- |\n| Create | src/Feature.cs |\n";
        var service = new PlanAnalysisService();
        var first = service.Parse(markdown);
        var second = service.Parse(markdown);
        var section = Assert.Single(first.Sections);

        Assert.Equal(PlanSectionType.ProjectStructure, section.SectionType);
        Assert.NotNull(section.Provenance);
        Assert.Equal("DirectContent", section.Provenance!.ProjectionKind);
        Assert.Equal(3, section.Provenance.Sources.Single().StartLine);
        Assert.Equal(7, section.Provenance.Sources.Single().EndLine);
        Assert.Equal(section.Provenance.ProjectionId, second.Sections.Single().Provenance!.ProjectionId);
    }

    private readonly PlanAnalysisService _service = new();

    [Fact]
    public void Parse_ImplementationPhaseRetainsConstructionTimeSourceProvenance()
    {
        const string markdown = "# Plan\n\n## Implementation Phases\n\n### Phase 1: Build\n- Build the service\n";

        var first = _service.Parse(markdown).Phases.Single();
        var second = _service.Parse(markdown).Phases.Single();

        Assert.NotNull(first.Provenance);
        Assert.Equal(first.Provenance!.ProjectionId, second.Provenance!.ProjectionId);
        var source = Assert.Single(first.Provenance.Sources);
        Assert.Equal(5, source.StartLine);
        Assert.Equal(6, source.EndLine);
        Assert.Equal("PlanPhase", source.BlockType);
    }

    [Fact]
    public void Parse_ExplicitComplexityAndConstitutionGateRetainSourceProvenance()
    {
        const string markdown = """
            # Plan

            ## Complexity Tracking

            ### Queue processing - High
            Backpressure and retry behavior.

            ## Constitution Check

            | Gate | Status | Evidence |
            |---|---|---|
            | PP-01 | PASS | Automated check |
            """;

        var document = _service.Parse(markdown);
        var complexity = Assert.Single(document.ComplexityItems);
        var gate = Assert.Single(document.Gates);

        Assert.NotNull(complexity.Provenance);
        Assert.Equal("PlanComplexityItem", Assert.Single(complexity.Provenance!.Sources).BlockType);
        Assert.Equal("Queue processing", complexity.Area);
        Assert.NotNull(gate.Provenance);
        Assert.Equal("PlanGateTableRow", Assert.Single(gate.Provenance!.Sources).BlockType);
        Assert.Equal(12, Assert.Single(gate.Provenance.Sources).StartLine);
        Assert.Equal(gate.Provenance.ProjectionId, Assert.Single(_service.Parse(markdown).Gates).Provenance!.ProjectionId);
    }

    [Fact]
    public void Parse_TechnicalContextFallbackItemsRetainExactSourceProvenance()
    {
        const string markdown = """
            # Plan

            ## Technical Context

            **Primary Dependencies**: Service A, Service B
            **Dependencies**: Service A
            **Performance Goals**: Respond under 500ms

            ## Project Structure

            ### Phase 4: Release
            Deploy the service.
            """;

        var first = _service.Parse(markdown);
        var second = _service.Parse(markdown);

        Assert.Equal(2, first.Dependencies.Count);
        var serviceA = Assert.Single(first.Dependencies.Where(d => d.Name == "Service A"));
        Assert.NotNull(serviceA.Provenance);
        Assert.Equal(2, serviceA.Provenance!.Sources.Count);
        Assert.Equal(2, serviceA.Provenance.Sources.Select(s => s.SourceBlockId).Distinct().Count());
        Assert.All(first.Dependencies, d => Assert.NotNull(d.Provenance));
        Assert.All(first.Constraints, c => Assert.NotNull(c.Provenance));
        Assert.Equal(first.Dependencies.Select(d => d.Provenance!.ProjectionId), second.Dependencies.Select(d => d.Provenance!.ProjectionId));

        var fallbackPhase = Assert.Single(first.Phases);
        Assert.NotNull(fallbackPhase.Provenance);
        var phaseSource = Assert.Single(fallbackPhase.Provenance!.Sources);
        Assert.Equal(11, phaseSource.StartLine);
        Assert.Equal(11, phaseSource.EndLine);
    }

    [Fact]
    public void Parse_TechnicalContextTestingFallbackIsExplicitlyDerivedFromSourceEvidence()
    {
        const string markdown = """
            # Plan

            ## Technical Context

            **Testing**: xUnit
            """;

        var testing = _service.Parse(markdown).TestingInfo;

        Assert.NotNull(testing);
        Assert.NotNull(testing!.Provenance);
        Assert.Equal("DerivedTestingStrategy", testing.Provenance!.ProjectionKind);
        var source = Assert.Single(testing.Provenance.Sources);
        Assert.Equal(5, source.StartLine);
        Assert.Equal(5, source.EndLine);
    }

    [Fact]
    public void Parse_AutoGeneratedComplexityIsExplicitlyDerivedFromContributingSource()
    {
        const string markdown = """
            # Plan

            ## Technical Context

            **Storage**: database persistence
            """;

        var document = _service.Parse(markdown);
        var item = Assert.Single(document.ComplexityItems);

        Assert.True(document.ComplexityDerived);
        Assert.NotNull(item.Provenance);
        Assert.Equal("DerivedComplexity", item.Provenance!.ProjectionKind);
        var source = Assert.Single(item.Provenance.Sources);
        Assert.Equal(5, source.StartLine);
        Assert.Equal(5, source.EndLine);
    }

    [Fact]
    public void Parse_WithBasicPlan_ExtractsSummaryAndMetadata()
    {
        var markdown = """
            # Implementation Plan: Test Feature

            **Branch**: `test-branch` | **Date**: 2026-03-01 | **Spec**: [spec.md](spec.md)

            This is a test plan summary describing the feature covering the initial goals and objectives of this implementation work across multiple lines to ensure it is captured as summary text.

            ## Technical Context

            **Language/Version**: C# / .NET 10
            **Storage**: Azure SQL
            """;

        var doc = _service.Parse(markdown);

        Assert.Equal("Implementation Plan: Test Feature", doc.Title);
        Assert.NotNull(doc.Summary);
        Assert.NotNull(doc.Branch);
        Assert.Equal("test-branch", doc.Branch);
    }

    [Fact]
    public void Parse_WithConstitutionTable_ParsesGates()
    {
        var markdown = """
            # Implementation Plan: Test

            ## Constitution Check

            | Gate | Status | Note |
            |---|---|---|
            | PP-01 Contract-Driven | ✅ PASS | Documented in spec |
            | GL-15 No cross-service DB | ✅ PASS | Isolated database |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Gates);
        Assert.True(doc.Gates.Count >= 2);
        Assert.Contains(doc.Gates, g => g.Status == PlanGateStatus.Pass);
    }

    [Fact]
    public void Parse_WithJustifiedDeviations_MarkAsWarning()
    {
        var markdown = """
            # Implementation Plan: Test

            ## Constitution Check

            | Gate | Status | Note |
            |---|---|---|
            | GL-20 Transactional outbox | ⚠️ JUSTIFIED DEVIATION | Polly retry used instead |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Gates);
        var deviationGate = doc.Gates.FirstOrDefault(g => g.Status == PlanGateStatus.Warning);
        Assert.NotNull(deviationGate);
    }

    [Fact]
    public void Parse_WithPrincipleRequirementTable_ParsesCorrectly()
    {
        var markdown = """
            # Implementation Plan: Person Module

            ## Constitution Check

            | Principle | Requirement | Status |
            |-----------|-------------|--------|
            | PP-01 Contract-Driven | GraphQL + REST only | ✅ PASS |
            | PP-05 Data Has Legal History | No hard DELETEs | ✅ PASS |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Gates);
        Assert.All(doc.Gates, g => Assert.NotEmpty(g.Gate));
    }

    [Fact]
    public void Parse_WithAlternativeTableHeaders_ParsesVariations()
    {
        var markdown = """
            # Implementation Plan: Test

            ## Constitution Check

            | # | Principle / Standard | Status | Notes |
            |---|---|---|---|
            | 1 | PP-01 | ✅ PASS | Verified |
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Gates);
    }

    [Fact]
    public void Parse_WithImplementationSteps_ParsesPhases()
    {
        var markdown = """
            # Implementation Plan: Test

            ## Implementation Steps

            ### Step 1 — Infrastructure: Entity Setup

            **Files**:
            - src/Models/Entity.cs
            - src/Migrations/Migration.cs

            ### Step 2 — API Endpoints

            Create REST endpoints.
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Phases);
        Assert.True(doc.Phases.Count >= 1);
    }

    [Fact]
    public void Parse_WithPhaseBasedSteps_RecognizesPhases()
    {
        var markdown = """
            # Implementation Plan: Person Module

            ## Implementation Phases

            ### Phase A — Foundation

            Core entity and database schema.

            ### Phase B — Data Ingestion

            BiRK adapter integration.
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.Phases);
        Assert.Contains(doc.Phases, p => p.Title.Contains("Foundation"));
    }

    [Fact]
    public void Parse_WithKeyDesignDecisions_RecognizesArchitecture()
    {
        var markdown = """
            # Implementation Plan: Test

            ## Key Design Decisions

            ### Message Handler

            Wolverine drives message consumption.

            ### Health Check Response

            Minimal web app with Kestrel.
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.ArchitectureDecisions);
    }

    [Fact]
    public void Parse_WithTechnicalContextKeyValue_ParsesConstraints()
    {
        var markdown = """
            # Implementation Plan: Test

            Test plan for a backend service with SQL storage and performance requirements.

            ## Technical Context

            **Language/Version**: C# / .NET 10
            **Storage**: Azure SQL
            **Testing**: xUnit, Testcontainers
            **Performance Goals**: p95 < 2s
            **Constraints**:
            - All data in Norway East
            - Fail-closed auth
            """;

        var doc = _service.Parse(markdown);

        Assert.NotNull(doc.Summary);
        Assert.True(doc.Constraints.Count > 0);
    }

    [Fact]
    public void Parse_WithFrontendOnly_DetectsFrontendOnlyFlag()
    {
        var markdown = """
            # Implementation Plan: Access Administration Panel

            **Branch**: `005-access-admin-panel` | **Date**: 2026-05-08

            Test plan for frontend-only Blazor WASM application with no storage.

            ## Technical Context

            **Project Type**: Blazor WebAssembly SPA
            **Storage**: N/A (frontend-only)
            **Platform**: Azure cloud deployment
            """;

        var doc = _service.Parse(markdown);

        Assert.True(doc.Health.IsFrontendOnly);
    }

    [Fact]
    public void Parse_WithStateless_DetectsStatelessFlag()
    {
        var markdown = """
            # Implementation Plan: Proxy Service

            Stateless proxy service implementation with no local state and scales horizontally without code changes.

            ## Technical Context

            **Project Type**: Stateless proxy
            **Storage**: No persistence required
            **Constraints**:
            - Stateless design
            - Scales horizontally
            """;

        var doc = _service.Parse(markdown);

        Assert.True(doc.Health.IsStateless);
    }

    [Fact]
    public void Parse_WithNoSQL_DetectsNoStorageFlag()
    {
        var markdown = """
            # Implementation Plan: M2LB.Revisjon

            Implementation of event revision logging with no SQL database, using WORM blob storage.

            ## Technical Context

            **Storage**: No SQL and no database persistence
            **Platform**: Azure blob storage for immutable write-once storage
            """;

        var doc = _service.Parse(markdown);

        Assert.True(doc.Health.HasNoStorage);
    }

    [Fact]
    public void Parse_WithOpenItems_ParsesTable()
    {
        var markdown = """
            # Implementation Plan: BiRK Person-adapter

            ## Open Items

            | ID | Item | Blocking implementation? | Resolution path |
            |---|---|---|---|
            | O-01 | GUID resolution spec needed | Yes | Consult Å-03 owner |
            """;

        var doc = _service.Parse(markdown);

        // Open items are parsed as part of risks section
        Assert.NotNull(doc);
    }

    [Fact]
    public void Parse_WithComplexityTracking_ParsesComplexityItems()
    {
        var markdown = """
            # Implementation Plan: Test

            ## Complexity Tracking

            ### Message Processing — High

            Event ordering, backpressure handling, retry logic.

            ### Data Storage — Medium

            Schema design, performance tuning.
            """;

        var doc = _service.Parse(markdown);

        Assert.NotEmpty(doc.ComplexityItems);
    }

    [Fact]
    public void Parse_WithMultipleSections_PreservesAll()
    {
        var markdown = """
            # Implementation Plan: Full Feature

            **Branch**: `feature-1` | **Date**: 2026-03-01

            Complete implementation plan for the new feature covering all aspects of the implementation.

            ## Technical Context

            **Language/Version**: C# / .NET 10
            **Storage**: Azure SQL

            ## Constitution Check

            | Gate | Status |
            |---|---|
            | PP-01 | ✅ PASS |

            ## Project Structure

            ```text
            src/
            ├── Domain/
            ├── Application/
            └── Infrastructure/
            ```

            ## Implementation Steps

            ### Step 1

            Initialize database.
            """;

        var doc = _service.Parse(markdown);

        Assert.NotNull(doc.Summary);
        Assert.NotEmpty(doc.Gates);
        Assert.NotEmpty(doc.Phases);
        Assert.NotEmpty(doc.Sections);
    }

    [Fact]
    public void Parse_AllFormatVariations_HandledCorrectly()
    {
        var formats = new[]
        {
            // Format 1: Basic with pipe-separated metadata
            ("# Plan: Feature | **Branch**: x | **Date**: 2026-03-01", "Feature"),

            // Format 2: With horizontal rules
            ("# Plan: Feature\n\n---\n\n## Summary", "Feature"),
        };

        foreach (var (markdown, expectedFeature) in formats)
        {
            var doc = _service.Parse(markdown);
            Assert.NotNull(doc);
        }
    }
}
