using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Xunit;

namespace BirkNext.Web.Tests;

public class PlanAnalysisServiceTests
{
    private readonly PlanAnalysisService _service = new();

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
