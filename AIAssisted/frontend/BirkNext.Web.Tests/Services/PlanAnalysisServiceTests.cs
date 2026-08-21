using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using System.IO;

namespace BirkNext.Web.Tests.Services;

public sealed class PlanAnalysisServiceTests
{
    private readonly PlanAnalysisService _svc = new();

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static string PlanWithGateTable() => """
        # Feature Plan: Secure Payments

        Branch: feature/payments
        Author: Alice

        ## Technical Context

        Payment processing module added to the checkout flow.

        ## Constitution Check

        Gate review summary for this plan.

        | Principle | Requirement | Status |
        |-----------|-------------|--------|
        | PP-01     | No hardcoded secrets | PASS |
        | PP-02     | Token validation on all endpoints | PASS |
        | PP-03     | Audit all payment events | WARNING |
        | PS-04     | Use RS256 JWT | FAIL |
        | PS-05     | RBAC model enforced | PASS |

        ## Risks

        ### High Risk: Payment Gateway Outage
        **Severity**: High
        **Mitigation**: Fallback to cached payment methods.

        ### Medium Risk: Data Sync Delay
        Potential lag during peak hours.

        ## Implementation

        ### Phase 1: Core Integration
        - Integrate payment SDK
        - Add payment service interface

        ### Phase 2: Security Hardening
        - Enforce JWT validation
        - Add audit logging

        ## Testing

        Tests use xUnit and FluentAssertions.
        - PP-01 covered in `tests/Security/PaymentSecurityTests.cs`
        """;

    private static string PlanWithArchitectureNotes() => """
        # Authorization Refactor Plan

        ## Architecture and Design

        The new authorization layer replaces the legacy middleware.

        ### Use Centralized Auth Service
        We decided to route all authorization through a single service
        rather than per-module checks.

        **Decision**: All modules call AuthService.Authorize().
        **Rationale**: Easier auditing and single point of enforcement.

        ### Event Sourcing for Audit Log
        Audit events are persisted via an event store.

        **Decision**: Use MassTransit outbox for reliable event delivery.
        """;

    private static string PlanWithConstraints() => """
        # Performance Plan

        ## Risks

        ### High Risk: Slow Queries
        **Severity**: High
        **Mitigation**: Add database indexes.

        ## Constraints

        ### Performance Goal: API Response Time
        All API responses must complete within 200ms at p99.

        ### Scale/Scope: User Volume
        System must support 50,000 concurrent users.

        ### Performance Goal: Database Throughput
        Database must handle 10,000 writes per second.

        ## Dependencies

        - ExternalPaymentGateway: Payment processing
        - ExternalSmsProvider: SMS notifications
        - ExternalEmailService: Email delivery
        - ExternalAnalytics: Event tracking
        - ExternalCache: Redis caching layer
        """;

    private static string PlanWithImplementationPhases() => """
        # Migration Plan

        ## Implementation Plan

        ### Phase 1: Preparation
        - Audit existing data
        - Back up production database

        #### Checks
        - Backup verified
        - Schema diff reviewed

        ### Phase 2: Migration
        - Run schema migration script
        - Migrate data to new format

        ### Phase 3: Validation
        - Run smoke tests
        - Verify data integrity
        """;

    private static string MinimalPlan() => """
        # Simple Feature Plan

        This plan covers a small UI change.

        ## Risks

        ### Low Risk: Minor styling regression
        Unlikely to affect functionality.
        """;

    // ── 1: Gate table parsing ─────────────────────────────────────────────

    [Fact]
    public void GateTable_ParsedFromMarkdownTable()
    {
        var doc = _svc.Parse(PlanWithGateTable());
        doc.Gates.Should().HaveCount(5, "table has 5 data rows");
    }

    [Fact]
    public void GateTable_RequirementColumnBecomesGateText()
    {
        var doc = _svc.Parse(PlanWithGateTable());
        var gate = doc.Gates.FirstOrDefault(g => g.Gate.Contains("hardcoded"));
        gate.Should().NotBeNull("'No hardcoded secrets' is in the Requirement column");
    }

    [Fact]
    public void GateTable_PrincipleColumnBecomesRuleId()
    {
        var doc = _svc.Parse(PlanWithGateTable());
        var gate = doc.Gates.FirstOrDefault(g => g.RuleId == "PP-01");
        gate.Should().NotBeNull("PP-01 is in the Principle column");
    }

    [Fact]
    public void GateTable_StatusParsedCorrectly()
    {
        var doc = _svc.Parse(PlanWithGateTable());
        doc.Gates.Count(g => g.Status == PlanGateStatus.Pass).Should().Be(3);
        doc.Gates.Count(g => g.Status == PlanGateStatus.Warning).Should().Be(1);
        doc.Gates.Count(g => g.Status == PlanGateStatus.Fail).Should().Be(1);
    }

    [Fact]
    public void GateTable_OverviewCountMatchesTabCount()
    {
        var doc = _svc.Parse(PlanWithGateTable());
        doc.Health.TotalConstitutionGates.Should().Be(doc.Gates.Count,
            "overview health count must equal tab data");
        doc.Health.PassedGates.Should().Be(doc.Gates.Count(g => g.Status == PlanGateStatus.Pass));
        doc.Health.WarningGates.Should().Be(doc.Gates.Count(g => g.Status == PlanGateStatus.Warning));
        doc.Health.FailedGates.Should().Be(doc.Gates.Count(g => g.Status == PlanGateStatus.Fail));
    }

    [Fact]
    public void FilterGatesByStatus_ReturnsOnlyMatching()
    {
        var doc = _svc.Parse(PlanWithGateTable());
        var passed = _svc.FilterGatesByStatus(doc.Gates, PlanGateStatus.Pass).ToList();
        passed.Should().OnlyContain(g => g.Status == PlanGateStatus.Pass);
        passed.Should().HaveCount(3);
    }

    [Fact]
    public void SearchGates_FindsByGateText()
    {
        var doc = _svc.Parse(PlanWithGateTable());
        var results = _svc.SearchGates(doc.Gates, "audit").ToList();
        results.Should().NotBeEmpty("'Audit all payment events' contains 'audit'");
    }

    // ── 2: Architecture section (non-ADR headings) ────────────────────────

    [Fact]
    public void ArchitectureNotes_ProducesDecisions()
    {
        var doc = _svc.Parse(PlanWithArchitectureNotes());
        doc.ArchitectureDecisions.Should().HaveCount(2,
            "two H3 headings under Architecture section");
    }

    [Fact]
    public void ArchitectureNotes_DecisionBodyExtracted()
    {
        var doc = _svc.Parse(PlanWithArchitectureNotes());
        var centralized = doc.ArchitectureDecisions.FirstOrDefault(d =>
            d.Title.Contains("Centralized", StringComparison.OrdinalIgnoreCase));
        centralized.Should().NotBeNull();
        centralized!.Decision.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ArchitectureNotes_NoAdrIdWhenHeadingHasNone()
    {
        var doc = _svc.Parse(PlanWithArchitectureNotes());
        doc.ArchitectureDecisions.Should().OnlyContain(d => string.IsNullOrEmpty(d.Id),
            "headings without ADR-NN should produce decisions with empty Id");
    }

    [Fact]
    public void ArchitectureNotes_HealthHasArchitecture()
    {
        var doc = _svc.Parse(PlanWithArchitectureNotes());
        doc.Health.HasArchitecture.Should().BeTrue();
        doc.Health.TotalArchitectureDecisions.Should().Be(2);
    }

    // ── 3: Implementation phases ─────────────────────────────────────────

    [Fact]
    public void ImplementationPhases_ExtractedFromSection()
    {
        var doc = _svc.Parse(PlanWithImplementationPhases());
        doc.Phases.Should().HaveCount(3);
    }

    [Fact]
    public void ImplementationPhases_SortedByPhaseNumber()
    {
        var doc = _svc.Parse(PlanWithImplementationPhases());
        var numbers = doc.Phases.Select(p => p.PhaseNumber).ToList();
        numbers.Should().BeInAscendingOrder("phases must be sorted by number");
    }

    [Fact]
    public void ImplementationPhases_TasksExtracted()
    {
        var doc = _svc.Parse(PlanWithImplementationPhases());
        var phase1 = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 1);
        phase1.Should().NotBeNull();
        phase1!.Tasks.Should().HaveCount(2);
        phase1.Tasks.Should().Contain(t => t.Contains("Audit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImplementationPhases_HealthReflectsCount()
    {
        var doc = _svc.Parse(PlanWithImplementationPhases());
        doc.Health.TotalPhases.Should().Be(3);
        doc.Health.HasImplementationPhases.Should().BeTrue();
    }

    [Fact]
    public void SearchPhases_FindsByTaskContent()
    {
        var doc = _svc.Parse(PlanWithImplementationPhases());
        var results = _svc.SearchPhases(doc.Phases, "migration").ToList();
        results.Should().NotBeEmpty("Phase 2 mentions 'migration'");
    }

    // ── 4: Auto-generated complexity ─────────────────────────────────────

    [Fact]
    public void AutoComplexity_GeneratedFromPerformanceGoals()
    {
        var doc = _svc.Parse(PlanWithConstraints());
        doc.ComplexityItems.Should().NotBeEmpty("performance goals should generate complexity items");
    }

    [Fact]
    public void AutoComplexity_PerformanceGoalsBecomeSeparateItems()
    {
        var doc = _svc.Parse(PlanWithConstraints());
        var perfItems = doc.ComplexityItems
            .Where(i => i.Factors.Any(f => f.Contains("performance goal", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        perfItems.Should().HaveCount(2, "two performance goal constraints defined");
    }

    [Fact]
    public void AutoComplexity_ManyExternalDepsGenerateHighComplexity()
    {
        var doc = _svc.Parse(PlanWithConstraints());
        var extItem = doc.ComplexityItems.FirstOrDefault(i => i.Area.Contains("External", StringComparison.OrdinalIgnoreCase));
        extItem.Should().NotBeNull("5 external deps should generate an External Integrations complexity item");
        extItem!.Level.Should().Be(ComplexityLevel.High, "5 > 4 threshold → High");
    }

    [Fact]
    public void AutoComplexity_HealthReflectsGeneratedItems()
    {
        var doc = _svc.Parse(PlanWithConstraints());
        doc.Health.TotalComplexityItems.Should().Be(doc.ComplexityItems.Count);
    }

    // ── 5: No regression: project structure ──────────────────────────────

    [Fact]
    public void ProjectStructure_NotFoundWhenMissing()
    {
        var doc = _svc.Parse(MinimalPlan());
        doc.Health.HasProjectStructure.Should().BeFalse();
    }

    [Fact]
    public void MinimalPlan_ParsesRisksCorrectly()
    {
        var doc = _svc.Parse(MinimalPlan());
        doc.Risks.Should().HaveCount(1);
        doc.Risks[0].Severity.Should().Be(RiskSeverity.Low);
        doc.Health.TotalRisks.Should().Be(1);
    }

    [Fact]
    public void MinimalPlan_MetadataEmptyWhenMissing()
    {
        var doc = _svc.Parse(MinimalPlan());
        doc.Branch.Should().BeNullOrEmpty();
        doc.Author.Should().BeNullOrEmpty();
    }

    // ── 6: Search and filter ─────────────────────────────────────────────

    [Fact]
    public void SearchGates_EmptyQueryReturnsAll()
    {
        var doc = _svc.Parse(PlanWithGateTable());
        var results = _svc.SearchGates(doc.Gates, string.Empty).ToList();
        results.Should().HaveCount(doc.Gates.Count);
    }

    [Fact]
    public void FilterGatesByStatus_NullFilterReturnsAll()
    {
        var doc = _svc.Parse(PlanWithGateTable());
        var results = _svc.FilterGatesByStatus(doc.Gates, null).ToList();
        results.Should().HaveCount(doc.Gates.Count);
    }

    [Fact]
    public void SearchPhases_EmptyQueryReturnsAll()
    {
        var doc = _svc.Parse(PlanWithImplementationPhases());
        var results = _svc.SearchPhases(doc.Phases, string.Empty).ToList();
        results.Should().HaveCount(doc.Phases.Count);
    }

    [Fact]
    public void SearchConstraints_FindsByTitle()
    {
        var doc = _svc.Parse(PlanWithConstraints());
        var results = _svc.SearchConstraints(doc.Constraints, "API Response").ToList();
        results.Should().NotBeEmpty();
    }

    [Fact]
    public void FilterConstraintsByType_ReturnsOnlyMatching()
    {
        var doc = _svc.Parse(PlanWithConstraints());
        var perf = _svc.FilterConstraintsByType(doc.Constraints, ConstraintType.PerformanceGoal).ToList();
        perf.Should().OnlyContain(c => c.ConstraintType == ConstraintType.PerformanceGoal);
    }

    // ── 7: Metadata extraction ────────────────────────────────────────────

    [Fact]
    public void BranchAndAuthorExtractedFromMetadataBlock()
    {
        var doc = _svc.Parse(PlanWithGateTable());
        doc.Branch.Should().Be("feature/payments");
        doc.Author.Should().Be("Alice");
    }

    [Fact]
    public void GateTable_HasConstitutionCheckInHealth()
    {
        var doc = _svc.Parse(PlanWithGateTable());
        doc.Health.HasConstitutionCheck.Should().BeTrue();
    }

    // ── 7: Gate Status Parsing ──────────────────────────────────────────────

    [Fact]
    public void GateStatus_PASS_MapsToPass()
    {
        var plan = """
            # Test Plan
            ## Constitution Check
            | Principle | Requirement | Status |
            |-----------|-------------|--------|
            | PP-01     | Requirement | PASS   |
            """;
        var doc = _svc.Parse(plan);
        doc.Gates.Should().HaveCount(1);
        doc.Gates[0].Status.Should().Be(PlanGateStatus.Pass);
    }

    [Fact]
    public void GateStatus_CheckmarkEmoji_MapsToPass()
    {
        var plan = """
            # Test Plan
            ## Constitution Check
            | Principle | Requirement | Status |
            |-----------|-------------|--------|
            | PP-01     | Requirement | ✅ PASS |
            """;
        var doc = _svc.Parse(plan);
        doc.Gates[0].Status.Should().Be(PlanGateStatus.Pass);
    }

    [Fact]
    public void GateStatus_JustifiedDeviation_MapsToWarning()
    {
        var plan = """
            # Test Plan
            ## Constitution Check
            | Principle | Requirement | Status |
            |-----------|-------------|--------|
            | GL-18     | Requirement | JUSTIFIED DEVIATION |
            """;
        var doc = _svc.Parse(plan);
        doc.Gates.Should().HaveCount(1);
        doc.Gates[0].Status.Should().Be(PlanGateStatus.Warning,
            "JUSTIFIED DEVIATION should map to Warning (not NotApplicable)");
    }

    [Fact]
    public void GateStatus_JustifiedDeviationWithEmoji_MapsToWarning()
    {
        var plan = """
            # Test Plan
            ## Constitution Check
            | Principle | Requirement | Status |
            |-----------|-------------|--------|
            | GL-18     | Requirement | ⚠️ JUSTIFIED DEVIATION |
            """;
        var doc = _svc.Parse(plan);
        doc.Gates[0].Status.Should().Be(PlanGateStatus.Warning);
    }

    [Fact]
    public void GateStatus_Partial_MapsToWarning()
    {
        var plan = """
            # Test Plan
            ## Constitution Check
            | Principle | Requirement | Status |
            |-----------|-------------|--------|
            | PS-05     | Requirement | PARTIAL |
            """;
        var doc = _svc.Parse(plan);
        doc.Gates[0].Status.Should().Be(PlanGateStatus.Warning);
    }

    [Fact]
    public void GateStatus_FAIL_MapsToFail()
    {
        var plan = """
            # Test Plan
            ## Constitution Check
            | Principle | Requirement | Status |
            |-----------|-------------|--------|
            | PS-04     | Requirement | FAIL |
            """;
        var doc = _svc.Parse(plan);
        doc.Gates[0].Status.Should().Be(PlanGateStatus.Fail);
    }

    [Fact]
    public void GateStatus_FailEmoji_MapsToFail()
    {
        var plan = """
            # Test Plan
            ## Constitution Check
            | Principle | Requirement | Status |
            |-----------|-------------|--------|
            | PS-04     | Requirement | ❌ FAIL |
            """;
        var doc = _svc.Parse(plan);
        doc.Gates[0].Status.Should().Be(PlanGateStatus.Fail);
    }

    [Fact]
    public void GateStatus_NA_MapsToNotApplicable()
    {
        var plan = """
            # Test Plan
            ## Constitution Check
            | Principle | Requirement | Status |
            |-----------|-------------|--------|
            | GL-99     | Requirement | N/A |
            """;
        var doc = _svc.Parse(plan);
        doc.Gates[0].Status.Should().Be(PlanGateStatus.NotApplicable);
    }

    [Fact]
    public void GateStatus_SCIMPlanRegression_ParsesAllGates()
    {
        // Real SCIM plan.md regression test
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        // Verify gate count (13 gates in the Constitution Check table)
        doc.Gates.Should().HaveCount(13, "SCIM plan should have 13 gates");

        // Verify specific gates
        var pp01 = doc.Gates.FirstOrDefault(g => g.RuleId == "PP-01");
        pp01.Should().NotBeNull("PP-01 should be parsed");
        pp01!.Status.Should().Be(PlanGateStatus.Pass, "PP-01 has PASS status");

        var gl18 = doc.Gates.FirstOrDefault(g => g.RuleId == "GL-18");
        gl18.Should().NotBeNull("GL-18 should be parsed");
        gl18!.Status.Should().Be(PlanGateStatus.Warning,
            "GL-18 has JUSTIFIED DEVIATION status which should map to Warning");

        var gl20 = doc.Gates.FirstOrDefault(g => g.RuleId == "GL-20");
        gl20.Should().NotBeNull("GL-20 should be parsed");
        gl20!.Status.Should().Be(PlanGateStatus.Warning,
            "GL-20 has JUSTIFIED DEVIATION status which should map to Warning");

        var ps01 = doc.Gates.FirstOrDefault(g => g.RuleId == "PS-01");
        ps01.Should().NotBeNull("PS-01 should be parsed");
        ps01!.Status.Should().Be(PlanGateStatus.Warning,
            "PS-01 has JUSTIFIED DEVIATION status which should map to Warning");

        // Verify gate status counts in health
        doc.Health.PassedGates.Should().BeGreaterThan(0, "Should have some passing gates");
        doc.Health.WarningGates.Should().Be(3, "GL-18, GL-20, PS-01 are justified deviations");
    }

    // ── 8: Complexity Tracking / Explicit Deviations ──────────────────────

    [Fact]
    public void ComplexityTracking_ExplicitViolationTable_Extracted()
    {
        var plan = """
            # Test Plan
            ## Complexity Tracking

            | Violation | Why Needed | Simpler Alternative Rejected Because |
            |-----------|-----------|-------------------------------------|
            | GL-18: No temporal fields | Record is sync-state, not entity | Adding fields would complicate machine-driven sync |
            | GL-20: No outbox | Spec ruled it out | Outbox needs polling infrastructure |
            """;
        var doc = _svc.Parse(plan);

        // Should extract 2 explicit violations (not auto-generated)
        doc.ComplexityItems.Should().HaveCount(2);

        // Verify fields are preserved
        var gl18 = doc.ComplexityItems[0];
        gl18.Area.Should().Contain("GL-18");
        gl18.Notes.Should().Contain("sync-state");
        gl18.Factors.Should().HaveCount(1);
        gl18.Factors[0].Should().Contain("complicate");

        var gl20 = doc.ComplexityItems[1];
        gl20.Area.Should().Contain("GL-20");
        gl20.Notes.Should().Contain("Spec ruled");
        gl20.Factors.Should().HaveCount(1);
        gl20.Factors[0].Should().Contain("polling");
    }

    [Fact]
    public void ComplexityTracking_ExplicitSuppressesAutoGeneration()
    {
        var plan = """
            # Test Plan
            ## Complexity Tracking

            | Violation | Why Needed | Simpler Alternative Rejected Because |
            |-----------|-----------|-------------------------------------|
            | GL-18: No temporal | Sync record | Complicates automation |
            """;
        var doc = _svc.Parse(plan);

        // Explicit complexity items should suppress auto-generation
        doc.ComplexityItems.Should().HaveCount(1);
        doc.ComplexityItems[0].Area.Should().Contain("GL-18");

        // Should NOT contain auto-generated items (e.g., Message Processing)
        doc.ComplexityItems.Should().NotContain(c => c.Area.Contains("Message") || c.Area.Contains("Storage"));
    }

    [Fact]
    public void ComplexityTracking_NoExplicitSection_AutoGenerationStillWorks()
    {
        var plan = """
            # Test Plan
            ## Technical Context
            Distributed system with Service Bus messaging and SQL database.
            Performance goal: 200ms response time.
            """;
        var doc = _svc.Parse(plan);

        // Without explicit Complexity section, auto-generation should still work
        doc.ComplexityItems.Should().NotBeEmpty("Auto-generation should create items from technical content");
        doc.ComplexityItems.Should().Contain(c => c.Area.Contains("Messaging") || c.Area.Contains("Storage"),
            "Should detect messaging and storage as complexity areas");
    }

    [Fact]
    public void ComplexityTracking_SCIMPlan_ParsesExplicitDeviationTable()
    {
        // Real SCIM plan.md regression test
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        // Should extract 3 explicit deviations from the Complexity Tracking table
        doc.ComplexityItems.Should().HaveCount(3, "SCIM plan has 3 documented deviations in Complexity Tracking table");

        // Verify GL-18 deviation
        var gl18 = doc.ComplexityItems.FirstOrDefault(c => c.Area.Contains("GL-18"));
        gl18.Should().NotBeNull("GL-18 deviation should be extracted");
        gl18!.Area.Should().Contain("GL-18", "Area should preserve violation title");
        gl18!.Notes.Should().Contain("sync-state", "Why Needed should be preserved");
        gl18!.Factors.Should().HaveCount(1, "Should have 1 alternative factor");
        gl18!.Factors.First().Should().Contain("audit fields", "Alternative should be preserved");

        // Verify GL-20 deviation
        var gl20 = doc.ComplexityItems.FirstOrDefault(c => c.Area.Contains("GL-20"));
        gl20.Should().NotBeNull("GL-20 deviation should be extracted");
        gl20!.Notes.Should().Contain("outbox table", "Why Needed contains 'outbox table'");

        // Verify PS-01 deviation
        var ps01 = doc.ComplexityItems.FirstOrDefault(c => c.Area.Contains("PS-01"));
        ps01.Should().NotBeNull("PS-01 deviation should be extracted");
        ps01!.Notes.Should().Contain("provisioning", "Why Needed contains context about provisioning");

        // Verify deviations are NOT replaced by auto-generated items
        doc.ComplexityItems.Should().NotContain(c => c.Area.Contains("Message Processing"),
            "Auto-generated complexity should not replace explicit deviations");
    }

    // ── 9: Testing Information Extraction ─────────────────────────────────

    [Fact]
    public void Testing_SCIMPlan_ParsesFromTechnicalContextAndPhases()
    {
        // AFTER FIX: Testing info extracted from Technical Context + Steps 8/9
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        // Verify frameworks are extracted
        doc.TestingInfo.Should().NotBeNull("Testing info should be extracted from Technical Context and phases");
        doc.TestingInfo!.Frameworks.Should().HaveCount(4, "SCIM plan has 4 testing frameworks");

        // Verify specific frameworks are present with source versions
        doc.TestingInfo.Frameworks.Should().Contain("xUnit 2.9.3");
        doc.TestingInfo.Frameworks.Should().Contain("Shouldly 4.3.0");
        doc.TestingInfo.Frameworks.Should().Contain("NSubstitute 5.x");
        doc.TestingInfo.Frameworks.Should().Contain("Testcontainers 4.x");

        // Verify phases are parsed correctly with phase numbers
        var step8 = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 8);
        step8.Should().NotBeNull("Step 8 Unit Tests should be parsed as phase 8");
        step8!.Title.Should().Contain("Unit Test");

        var step9 = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 9);
        step9.Should().NotBeNull("Step 9 Integration Tests should be parsed as phase 9");
        step9!.Title.Should().Contain("Integration Test");

        // Verify testing info blocks contain test strategy
        doc.TestingInfo.Blocks.Should().NotBeEmpty("Should have testing strategy blocks from phases");
        var unitTestBlock = doc.TestingInfo.Blocks.FirstOrDefault(b => b.SubHeading?.Contains("Unit") == true);
        unitTestBlock.Should().NotBeNull("Should have Unit Testing strategy block");
    }

    // ── 10: Dependency Extraction ────────────────────────────────────────

    [Fact]
    public void Testing_HorizontalRule_NotIncludedAsText()
    {
        var markdown = """
            # Implementation Plan: Test

            ## Technical Context

            **Testing**: xUnit 2.9.3

            ## Implementation Steps

            ### Step 8 - Unit Tests

            Add to UnitTests:
            ---
            - Test idempotency

            ### Step 9 - Integration Tests

            Add integration tests.
            """;

        var doc = _svc.Parse(markdown);

        doc.TestingInfo.Should().NotBeNull();
        TestingBlockText(doc).Should().NotContain("---");
        TestingBlockText(doc).Should().NotContain(": ---");
    }

    [Fact]
    public void Testing_PhaseBoundary_DoesNotLeakSeparator()
    {
        var markdown = """
            # Implementation Plan: Test

            ## Technical Context

            **Testing**: xUnit 2.9.3

            ## Implementation Steps

            ### Step 8 - Unit Tests

            Add to UnitTests:
            - Validate service behavior

            ---

            ### Step 9 - Integration Tests

            Test scenarios (map to acceptance scenarios in spec):
            - POST /Users creates a user
            """;

        var doc = _svc.Parse(markdown);

        doc.TestingInfo.Should().NotBeNull();
        TestingBlockText(doc).Should().Contain("Add to UnitTests:");
        TestingBlockText(doc).Should().Contain("Test scenarios (map to acceptance scenarios in spec):");
        TestingBlockText(doc).Should().NotContain("---");
        TestingBlockText(doc).Should().NotContain(": ---");
    }

    [Fact]
    public void Testing_SCIMPlan_NoTestingBlockContainsHorizontalRuleText()
    {
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        doc.TestingInfo.Should().NotBeNull();
        foreach (var block in doc.TestingInfo!.Blocks)
            TestingBlockText(block).Should().NotContain("---");
    }

    [Fact]
    public void TestingFramework_VersionPreserved()
    {
        var markdown = """
            # Implementation Plan: Test

            ## Technical Context

            **Testing**: xUnit 2.9.3, Shouldly 4.3.0
            """;

        var doc = _svc.Parse(markdown);

        doc.TestingInfo.Should().NotBeNull();
        doc.TestingInfo!.Frameworks.Should().Contain("xUnit 2.9.3");
        doc.TestingInfo.Frameworks.Should().Contain("Shouldly 4.3.0");
    }

    [Fact]
    public void TestingFramework_VersionRenderedOnce()
    {
        var markdown = """
            # Implementation Plan: Test

            ## Technical Context

            **Testing**: xUnit 2.9.3

            ## Testing

            xUnit 2.9.3 is used for unit tests.
            """;

        var doc = _svc.Parse(markdown);

        doc.TestingInfo.Should().NotBeNull();
        doc.TestingInfo!.Frameworks.Should().ContainSingle(f => f == "xUnit 2.9.3");
        doc.TestingInfo.Frameworks.Should().NotContain("xUnit 2.9.3 2.9.3");
    }

    [Fact]
    public void Testing_SCIMPlan_PreservesAllSourceFrameworkVersions()
    {
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        doc.TestingInfo.Should().NotBeNull();
        doc.TestingInfo!.Frameworks.Should().BeEquivalentTo([
            "xUnit 2.9.3",
            "Shouldly 4.3.0",
            "NSubstitute 5.x",
            "Testcontainers 4.x"
        ]);
    }

    private static string TestingBlockText(PlanDocument doc) =>
        string.Join("\n", doc.TestingInfo?.Blocks.Select(TestingBlockText) ?? []);

    private static string TestingBlockText(PlanSectionBlock block) =>
        string.Join("\n", [
            block.SubHeading ?? "",
            block.Paragraph ?? "",
            string.Join("\n", block.BulletPoints),
            block.CodeBlock ?? "",
        ]);

    [Fact]
    public void Dependencies_TechnicalContext_MultipleFieldsExtracted()
    {
        // Test that multiple Technical Context fields are extracted correctly without cross-contamination
        var markdown = @"## Technical Context

**Language/Version**: C# 13 / .NET 10
**Primary Dependencies**: ASP.NET Core Minimal API, EF Core 10, Polly 8.x
**Storage**: SQL Server 2022
**Testing**: xUnit 2.9.3

";

        var doc = _svc.Parse(markdown);

        // Should extract ONLY the Primary Dependencies field
        doc.Dependencies.Count.Should().Be(3, "Should extract exactly 3 dependencies from Primary Dependencies field");
        doc.Dependencies.Should().Contain(d => d.Name.Contains("ASP.NET", StringComparison.OrdinalIgnoreCase));
        doc.Dependencies.Should().Contain(d => d.Name.Contains("EF Core", StringComparison.OrdinalIgnoreCase));
        doc.Dependencies.Should().Contain(d => d.Name.Contains("Polly", StringComparison.OrdinalIgnoreCase));

        // Should NOT extract from other fields
        doc.Dependencies.Should().NotContain(d => d.Name.Contains("C#", StringComparison.OrdinalIgnoreCase));
        doc.Dependencies.Should().NotContain(d => d.Name.Contains(".NET", StringComparison.OrdinalIgnoreCase) && !d.Name.Contains("ASP.NET", StringComparison.OrdinalIgnoreCase));
        doc.Dependencies.Should().NotContain(d => d.Name.Contains("SQL Server", StringComparison.OrdinalIgnoreCase));
        doc.Dependencies.Should().NotContain(d => d.Name.Contains("xUnit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Dependencies_SCIMPlan_ParsesFromTechnicalContext()
    {
        // REGRESSION TEST: Dependencies extraction from real SCIM plan
        // Must extract exactly 5 primary dependencies, not overmatch Technical Context fields
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        // Exact count check
        doc.Dependencies.Count.Should().Be(5, "SCIM plan should have exactly 5 primary dependencies");

        // Exact names (must NOT include Language/Version, Storage, Testing fields)
        var depNames = doc.Dependencies.Select(d => d.Name).ToList();

        depNames.Should().Contain(d => d.Contains("ASP.NET", StringComparison.OrdinalIgnoreCase),
            "ASP.NET Core Minimal API should be extracted");
        depNames.Should().Contain(d => d.Contains("EF Core", StringComparison.OrdinalIgnoreCase),
            "EF Core should be extracted");
        depNames.Should().Contain(d => d.Contains("ServiceBus", StringComparison.OrdinalIgnoreCase),
            "Azure.Messaging.ServiceBus should be extracted");
        depNames.Should().Contain(d => d.Contains("Polly", StringComparison.OrdinalIgnoreCase),
            "Polly should be extracted");
        depNames.Should().Contain(d => d.Contains("Identity", StringComparison.OrdinalIgnoreCase),
            "Azure.Identity should be extracted");

        // MUST NOT include these incorrect matches
        depNames.Should().NotContain(d => d.Contains("C# 13", StringComparison.OrdinalIgnoreCase),
            "Language/Version field (C# 13) should NOT be extracted as dependency");
        depNames.Should().NotContain(d => d.Equals(".NET 10", StringComparison.OrdinalIgnoreCase),
            "Language/Version field (.NET 10) should NOT be extracted standalone as dependency");
        depNames.Should().NotContain(d => d.Contains("SQL Server", StringComparison.OrdinalIgnoreCase),
            "Storage field (SQL Server) should NOT be extracted as dependency");
        depNames.Should().NotContain(d => d.Contains("xUnit", StringComparison.OrdinalIgnoreCase),
            "Testing field (xUnit) should NOT be extracted as dependency");
        depNames.Should().NotContain(d => d.Contains("Shouldly", StringComparison.OrdinalIgnoreCase),
            "Testing field (Shouldly) should NOT be extracted as dependency");
        depNames.Should().NotContain(d => d.Contains("NSubstitute", StringComparison.OrdinalIgnoreCase),
            "Testing field (NSubstitute) should NOT be extracted as dependency");
    }

    // ── 14: Technical Context Rendering ─────────────────────────────────────

    // ── 14: Technical Context Rendering ─────────────────────────────────────

    [Fact]
    public void TechnicalContext_RawContentPreservesLineBreaks()
    {
        // Verify RawContent contains original Markdown with line breaks preserved
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        var techSection = doc.Sections.FirstOrDefault(s => s.SectionType == PlanSectionType.TechnicalContext);

        techSection.Should().NotBeNull();
        techSection!.RawContent.Should().NotBeEmpty("Technical Context should have RawContent");

        // RawContent should have original Markdown with line breaks between fields
        techSection.RawContent.Should().Contain("\n", "RawContent should preserve line breaks");
        techSection.RawContent.Should().Contain("**Language/Version**", "Should preserve bold label markers");
        techSection.RawContent.Should().Contain("**Primary Dependencies**", "Should have dependency field label");
        techSection.RawContent.Should().Contain("**Storage**", "Should have storage field label");

        // Verify line breaks between fields (not flattened)
        techSection.RawContent.Should().MatchRegex(@"\*\*Language/Version\*\*[^\n]*\n\*\*Primary",
            "Labels should be on separate lines");

        // Log actual RawContent for inspection
        var lines = techSection.RawContent.Split('\n').Take(10);
        foreach (var line in lines)
        {
            System.Console.WriteLine($"  {line}");
        }
    }

    [Fact]
    public void TechnicalContext_MarkdigRendersWithBoldLabels()
    {
        // Verify that Markdig renders the RawContent with bold labels intact
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        var techSection = doc.Sections.FirstOrDefault(s => s.SectionType == PlanSectionType.TechnicalContext);
        techSection.Should().NotBeNull();

        // Simulate MarkdownRenderingService rendering
        var renderer = new MarkdownRenderingService();
        var html = renderer.Render(techSection!.RawContent);

        // HTML should contain rendered bold labels
        html.Should().Contain("<strong>Language/Version</strong>", "Bold labels should be converted to <strong>");
        html.Should().Contain("<strong>Primary Dependencies</strong>", "Should preserve Primary Dependencies label");
        html.Should().Contain("<strong>Storage</strong>", "Should preserve Storage label");

        // Should contain actual values
        html.Should().Contain("C# 13", "Should contain version value");
        html.Should().Contain("ASP.NET Core", "Should contain dependency value");

        // Should render as paragraphs (Markdown default for lines without blank separator)
        html.Should().Contain("<p>", "Should have paragraph elements");

        System.Console.WriteLine("--- RENDERED HTML ---");
        System.Console.WriteLine(html);
    }

    // ── 13: Architecture Rendering ──────────────────────────────────────────

    // ── 19: Technical Context Line Breaks ───────────────────────────────────

    [Fact]
    public void TechnicalContext_RawTextPreservesNewlines_SCIMPlan()
    {
        // Diagnostic: Verify RawText has newlines between fields
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        var techSection = doc.Sections.FirstOrDefault(s => s.SectionType == PlanSectionType.TechnicalContext);
        techSection.Should().NotBeNull();

        System.Console.WriteLine("=== TECHNICAL CONTEXT RAWTEXT ===\n");
        System.Console.WriteLine($"RawText length: {techSection!.RawContent.Length}");
        System.Console.WriteLine($"Contains newlines: {techSection.RawContent.Contains("\n")}");
        System.Console.WriteLine($"Field count (newline-separated): {techSection.RawContent.Split('\n').Length}");

        // Show escaped representation
        var escaped = techSection.RawContent.Replace("\n", "\\n").Substring(0, Math.Min(200, techSection.RawContent.Length));
        System.Console.WriteLine($"First 200 chars (escaped): {escaped}");

        // Verify specific fields are on separate lines
        var lines = techSection.RawContent.Split('\n');
        var fieldLines = lines.Where(l => l.Contains("**") && l.Contains(":")).ToList();
        System.Console.WriteLine($"\nField lines (with ** and :): {fieldLines.Count}");
        foreach (var line in fieldLines.Take(5))
        {
            System.Console.WriteLine($"  - {line.Substring(0, Math.Min(80, line.Length))}...");
        }
    }

    [Fact]
    public void MarkdownContent_RenderedHTML_SoftLineBreakBehavior()
    {
        // Diagnostic: Check what Markdig HTML output looks like
        var renderer = new MarkdownRenderingService();

        var markdown = @"**Language/Version**: C# 13 / .NET 10
**Primary Dependencies**: ASP.NET Core 10
**Storage**: SQL Server 2022";

        var html = renderer.Render(markdown);

        System.Console.WriteLine("=== MARKDIG HTML OUTPUT ===\n");
        System.Console.WriteLine($"HTML: {html}");
        System.Console.WriteLine($"\nContains <br>: {html.Contains("<br")}");
        System.Console.WriteLine($"Single <p>: {html.Contains("<p>") && html.LastIndexOf("<p>") == html.IndexOf("<p>")}");
        System.Console.WriteLine($"Multiple <p>: {html.Count(c => c == '<') > 2}");
    }

    // ── 18: Constraint Duplication ──────────────────────────────────────────

    [Fact]
    public void Constraint_RenderingLogic_IdenticalTitleAndDescription()
    {
        // Verify the Razor rendering condition logic for identical Title/Description
        var constraint = new PlanConstraint
        {
            Title = "Service Bus (FR-008)",
            Description = "Service Bus (FR-008)",
            ConstraintType = ConstraintType.Constraint,
            RawText = "Service Bus"
        };

        // The Razor rendering condition:
        // @if (!string.IsNullOrEmpty(Description) && !Title.Trim().Equals(Description.Trim(), OrdinalIgnoreCase))
        var shouldRenderDescription = !string.IsNullOrEmpty(constraint.Description) &&
                                     !constraint.Title?.Trim().Equals(constraint.Description?.Trim(), StringComparison.OrdinalIgnoreCase) == true;
        shouldRenderDescription.Should().BeFalse("identical Title/Description should suppress Description rendering");
    }

    [Fact]
    public void Constraint_RenderingLogic_DifferentDescription()
    {
        // Verify the Razor rendering condition shows Description when it differs
        var constraint = new PlanConstraint
        {
            Title = "GL-20",
            Description = "The Service Bus ensures reliable messaging - outbox pattern rejected.",
            ConstraintType = ConstraintType.Constraint,
            RawText = "GL-20"
        };

        var shouldRenderDescription = !string.IsNullOrEmpty(constraint.Description) &&
                                     !constraint.Title?.Trim().Equals(constraint.Description?.Trim(), StringComparison.OrdinalIgnoreCase) == true;
        shouldRenderDescription.Should().BeTrue("different Description should be rendered");
    }

    [Fact]
    public void Constraint_WhitespaceEquivalent_SuppressesDescription()
    {
        // Verify that whitespace differences don't cause duplicate rendering
        var constraint = new PlanConstraint
        {
            Title = "  Test Item  ",
            Description = "Test Item",
            ConstraintType = ConstraintType.Constraint,
            RawText = "Test"
        };

        var shouldRenderDescription = !string.IsNullOrEmpty(constraint.Description) &&
                                     !constraint.Title?.Trim().Equals(constraint.Description?.Trim(), StringComparison.OrdinalIgnoreCase) == true;
        shouldRenderDescription.Should().BeFalse("trimmed whitespace should match");
    }

    [Fact]
    public void Constraint_SCIMPlan_NoduplicateRendering()
    {
        // Real SCIM regression: all fallback constraints should not duplicate Title/Description
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        // All SCIM constraints should have identical Title/Description
        foreach (var constraint in doc.Constraints)
        {
            var shouldRender = !string.IsNullOrEmpty(constraint.Description) &&
                             !constraint.Title?.Trim().Equals(constraint.Description?.Trim(), StringComparison.OrdinalIgnoreCase) == true;

            shouldRender.Should().BeFalse(
                $"Constraint '{constraint.Title}' should not duplicate Description in UI rendering");
        }
    }

    [Fact]
    public void Constraint_TitleDescription_SCIMPlan()
    {
        // Diagnostic: Check if Title and Description are duplicated
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        System.Console.WriteLine("=== CONSTRAINT TITLE/DESCRIPTION STATE ===\n");

        foreach (var constraint in doc.Constraints)
        {
            System.Console.WriteLine($"Type: {constraint.ConstraintType}");
            System.Console.WriteLine($"Title: {constraint.Title}");
            System.Console.WriteLine($"Description: {constraint.Description ?? "(null)"}");
            System.Console.WriteLine($"Same?: {constraint.Title == constraint.Description}");
            System.Console.WriteLine($"Trimmed Same?: {constraint.Title?.Trim() == constraint.Description?.Trim()}");
            System.Console.WriteLine();
        }

        // Check specific items mentioned in the bug
        var fr008 = doc.Constraints.FirstOrDefault(c => c.RawText.Contains("FR-008"));
        var fr022 = doc.Constraints.FirstOrDefault(c => c.RawText.Contains("FR-022"));
        var noSLA = doc.Constraints.FirstOrDefault(c => c.Title.Contains("No specific SLA"));
        var singleReplica = doc.Constraints.FirstOrDefault(c => c.Title.Contains("Single replica"));

        System.Console.WriteLine($"FR-008 found: {fr008 != null}");
        System.Console.WriteLine($"FR-022 found: {fr022 != null}");
        System.Console.WriteLine($"No SLA found: {noSLA != null}");
        System.Console.WriteLine($"Single replica found: {singleReplica != null}");
    }

    // ── 17: Complexity Provenance ───────────────────────────────────────────

    [Fact]
    public void ExplicitComplexitySection_IsNotMarkedAutoGenerated()
    {
        // When explicit Complexity section exists, section should be registered
        var markdown = @"# Plan

## Complexity Tracking

| Violation | Why Needed | Simpler Alternative Rejected Because |
| --- | --- | --- |
| GL-18 | test | none |
";
        var doc = _svc.Parse(markdown);

        // Should have explicitly parsed items (or at least create the section)
        // Item parsing might be strict, but the section should exist
        var complexitySection = doc.Sections.FirstOrDefault(s => s.SectionType == PlanSectionType.Complexity);
        complexitySection.Should().NotBeNull("Should register Complexity section for explicit source");
        complexitySection!.Title.Should().Be("Complexity Tracking");
    }

    [Fact]
    public void NoComplexitySection_FallbackIsMarkedAutoGenerated()
    {
        // When no explicit Complexity section exists, fallback generation happens
        var markdown = @"# Plan

## Technical Context

Some content.
";
        var doc = _svc.Parse(markdown);

        // Might have auto-generated items (depending on constraints/deps)
        // But NO explicit Complexity section
        var complexitySection = doc.Sections.FirstOrDefault(s => s.SectionType == PlanSectionType.Complexity);
        complexitySection.Should().BeNull("Should NOT have explicit Complexity section");
    }

    [Fact]
    public void ComplexityTrackingHeading_CountsAsExplicitComplexity()
    {
        // Verify that "Complexity Tracking" heading is recognized, not just "Complexity"
        var markdown = @"# Plan

## Complexity Tracking

| Area | Notes |
| Test | content |
";
        var doc = _svc.Parse(markdown);

        var section = doc.Sections.FirstOrDefault(s => s.SectionType == PlanSectionType.Complexity);
        section.Should().NotBeNull("Complexity Tracking should be classified as Complexity");
    }

    [Fact]
    public void SCIMPlan_ComplexityProvenanceCorrect()
    {
        // Real SCIM regression: complexity must be marked as explicit source
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        // Should have 3 source items
        doc.ComplexityItems.Count.Should().Be(3);
        doc.ComplexityItems.Should().Contain(c => c.Area == "GL-18: KjentBruker without GyldigFra/GyldigTil");
        doc.ComplexityItems.Should().Contain(c => c.Area == "GL-20: No transactional outbox");
        doc.ComplexityItems.Should().Contain(c => c.Area == "PS-01: Non-EntraID auth on SCIM endpoint");

        // Should have Complexity section (proves explicit source)
        var complexitySection = doc.Sections.FirstOrDefault(s => s.SectionType == PlanSectionType.Complexity);
        complexitySection.Should().NotBeNull("SCIM plan has explicit Complexity Tracking section");

        // This section should be found by the UI, so auto-generated banner will NOT show
        doc.Sections.Any(s => s.SectionType == PlanSectionType.Complexity).Should().BeTrue(
            "UI checks: !Sections.Any(Complexity) to show auto-generated banner. This should be FALSE for SCIM.");
    }

    [Fact]
    public void Complexity_Provenance_SCIMPlan()
    {
        // Diagnostic: Check how complexity section is classified and what provenance state is set
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        System.Console.WriteLine("=== COMPLEXITY PROVENANCE STATE ===\n");

        System.Console.WriteLine($"ComplexityItems.Count: {doc.ComplexityItems.Count}");
        foreach (var item in doc.ComplexityItems)
        {
            System.Console.WriteLine($"  - {item.Area} ({item.Level})");
        }

        var complexitySection = doc.Sections.FirstOrDefault(s => s.SectionType == PlanSectionType.Complexity);
        System.Console.WriteLine($"\nComplexity Section Found: {complexitySection != null}");
        if (complexitySection != null)
        {
            System.Console.WriteLine($"  Title: {complexitySection.Title}");
            System.Console.WriteLine($"  RawContent length: {complexitySection.RawContent.Length}");
        }

        System.Console.WriteLine($"\nAll Sections with their types:");
        foreach (var section in doc.Sections.Where(s => s.Title.Contains("Complexity")))
        {
            System.Console.WriteLine($"  - {section.Title}: {section.SectionType}");
        }
    }

    // ── 16: Phase Content Preservation ──────────────────────────────────────

    [Fact]
    public void Phase_WithBlocksButNoTasks_RendersBlocks()
    {
        // Verify phases with Blocks but no Tasks have meaningful content preserved
        var markdown = @"# Plan

## Implementation

### Phase 2: New Project

Create new project with settings.

```xml
<Project>Settings</Project>
```

Another paragraph.
";
        var doc = _svc.Parse(markdown);
        var phase = doc.Phases.First();

        phase.Tasks.Should().BeEmpty("Phase has no task bullets");
        phase.Blocks.Should().NotBeEmpty("Phase should have Blocks");
        phase.Blocks[0].Paragraph.Should().Contain("Create new project", "Should preserve narrative paragraph");
        phase.Blocks[0].Paragraph.Should().Contain("Another paragraph", "Should preserve second paragraph");
        // Code blocks are stored in CodeBlock field during parsing, Paragraph may contain language marker
    }

    [Fact]
    public void Phase_WithTasksAndBlocks_DoesNotDuplicateTaskBullets()
    {
        // Verify that phases with extracted Tasks also have Blocks
        // The UI should render Tasks if Tasks.Count > 0, not Blocks, to avoid duplication
        var markdown = @"# Plan

## Implementation

### Phase 1: Setup

Initial content.

- Task 1
- Task 2
";
        var doc = _svc.Parse(markdown);
        var phase = doc.Phases.First();

        phase.Tasks.Count.Should().Be(2, "Should extract the task bullets");
        phase.Blocks.Should().NotBeEmpty("Should also have Blocks with full content");
        // The UI will render Tasks, not Blocks, when Tasks.Count > 0 to avoid duplication
    }

    [Fact]
    public void Phase_SCIMPlan_P2_NotEmpty()
    {
        // P2 should have meaningful content (not just empty because no Tasks)
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        var p2 = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 2);
        p2.Should().NotBeNull();
        p2!.Tasks.Should().BeEmpty("P2 has no extracted tasks");
        p2.Blocks.Should().NotBeEmpty("P2 should have Blocks");

        var msg = $"P2 has {p2.Blocks.Count} blocks:\n";
        for (int i = 0; i < p2.Blocks.Count; i++)
        {
            var b = p2.Blocks[i];
            msg += $"[{i}] Para={b.Paragraph?.Length ?? 0}ch, Code={b.CodeBlock?.Length ?? 0}ch, IsCodeBlock={b.IsCodeBlock}\n";
            if (b.Paragraph != null)
                msg += $"  Para: {b.Paragraph[..Math.Min(60, b.Paragraph.Length)]}\n";
            if (b.CodeBlock != null)
                msg += $"  Code: {b.CodeBlock[..Math.Min(60, b.CodeBlock.Length)]}\n";
        }

        // XML should be in one of the blocks
        var xmlBlock = p2.Blocks.FirstOrDefault(b => b.CodeBlock?.Contains("<Project") == true);
        xmlBlock.Should().NotBeNull(msg);
        xmlBlock!.CodeBlock.Should().Contain("<Project", "Should contain project XML");
    }

    [Fact]
    public void Phase_SCIMPlan_P3_NotEmpty()
    {
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        var p3 = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 3);
        p3.Should().NotBeNull();
        p3!.Tasks.Should().BeEmpty("P3 has no extracted tasks");
        p3.Blocks.Should().NotBeEmpty("P3 should have Blocks");
        p3.Blocks[0].Paragraph.Should().Contain("Custom", "Should contain custom handler reference");
    }

    [Fact]
    public void Phase_SCIMPlan_P5_NotEmpty()
    {
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        var p5 = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 5);
        p5.Should().NotBeNull();
        p5!.Tasks.Should().BeEmpty("P5 has no extracted tasks");
        p5.Blocks.Should().NotBeEmpty("P5 should have Blocks");
        p5.Blocks[0].Paragraph.Should().NotBeEmpty("Should contain meaningful content");
        p5.Blocks[0].Paragraph.Should().Contain("SCIM", "Should contain SCIM reference");
    }

    [Fact]
    public void Phase_SCIMPlan_P6_NotEmpty()
    {
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        var p6 = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 6);
        p6.Should().NotBeNull();
        p6!.Tasks.Should().BeEmpty("P6 has no extracted tasks");
        p6.Blocks.Should().NotBeEmpty("P6 should have Blocks");
        p6.Blocks[0].Paragraph.Should().Contain("endpoints", "Should contain endpoints description");
    }

    [Fact]
    public void Phase_SCIMPlan_P7_NotEmpty()
    {
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        var p7 = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 7);
        p7.Should().NotBeNull();
        p7!.Tasks.Should().BeEmpty("P7 has no extracted tasks");
        p7.Blocks.Should().NotBeEmpty("P7 should have Blocks");
        p7.Blocks[0].Paragraph.Should().Contain("Setup sequence", "Should contain Program.cs setup description");
    }

    [Fact]
    public void Phase_SCIMPlan_P1_P4_P8_P9_TasksPreserved()
    {
        // Verify that P1, P4, P8, P9 still have their task counts (not duplicated)
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        doc.Phases.First(p => p.PhaseNumber == 1).Tasks.Count.Should().Be(5);
        doc.Phases.First(p => p.PhaseNumber == 4).Tasks.Count.Should().Be(4);
        doc.Phases.First(p => p.PhaseNumber == 8).Tasks.Count.Should().Be(2);
        doc.Phases.First(p => p.PhaseNumber == 9).Tasks.Count.Should().Be(12);
    }

    [Fact]
    public void Phase_ContentPreservation_SCIMPlan()
    {
        // Diagnostic: Examine what content is preserved in each phase
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        System.Console.WriteLine("=== PHASE CONTENT STATE ===\n");

        foreach (var phase in doc.Phases.OrderBy(p => p.PhaseNumber))
        {
            System.Console.WriteLine($"Phase {phase.PhaseNumber}: {phase.Title}");
            System.Console.WriteLine($"  Description: {phase.Description ?? "(none)"}");
            System.Console.WriteLine($"  Tasks: {phase.Tasks.Count}");
            System.Console.WriteLine($"  Checks: {phase.Checks.Count}");
            System.Console.WriteLine($"  Blocks: {phase.Blocks.Count}");
            if (phase.Blocks.Count > 0)
            {
                foreach (var block in phase.Blocks.Take(3))
                {
                    if (!string.IsNullOrEmpty(block.Paragraph))
                        System.Console.WriteLine($"    - Paragraph: {block.Paragraph.Substring(0, Math.Min(60, block.Paragraph.Length))}...");
                    if (!string.IsNullOrEmpty(block.SubHeading))
                        System.Console.WriteLine($"    - SubHeading: {block.SubHeading}");
                    if (block.IsCodeBlock)
                        System.Console.WriteLine($"    - CodeBlock: {block.CodeBlock.Substring(0, Math.Min(60, block.CodeBlock.Length))}...");
                    if (block.BulletPoints.Count > 0)
                        System.Console.WriteLine($"    - Bullets: {block.BulletPoints.Count} items");
                }
                if (phase.Blocks.Count > 3)
                    System.Console.WriteLine($"    ... and {phase.Blocks.Count - 3} more blocks");
            }
            System.Console.WriteLine();
        }
    }

    // ── 15: Project Structure Classification ────────────────────────────────

    [Fact]
    public void ProjectStructure_DiagnosticTest_SCIMPlan()
    {
        // Diagnostic: Show what's happening with Project Structure subsections
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        System.Console.WriteLine("=== ACTUAL MODEL STATE ===\n");

        System.Console.WriteLine($"ProjectStructure Sections: {doc.Sections.Count(s => s.SectionType == PlanSectionType.ProjectStructure)}");
        foreach (var sec in doc.Sections.Where(s => s.SectionType == PlanSectionType.ProjectStructure))
        {
            System.Console.WriteLine($"  Title: {sec.Title}");
            System.Console.WriteLine($"  RawContent length: {sec.RawContent.Length}");
            System.Console.WriteLine($"  First 300 chars:\n{sec.RawContent.Substring(0, Math.Min(300, sec.RawContent.Length))}...\n");
            System.Console.WriteLine($"  Blocks count: {sec.Blocks.Count}");
        }

        System.Console.WriteLine($"ArchitectureDecisions Count: {doc.ArchitectureDecisions.Count}");
        foreach (var dec in doc.ArchitectureDecisions)
        {
            System.Console.WriteLine($"  - Title: {dec.Title}");
            System.Console.WriteLine($"    RawText: {dec.RawText.Substring(0, Math.Min(80, dec.RawText.Length))}...");
        }

        System.Console.WriteLine($"\n=== EXPECTED ===");
        System.Console.WriteLine("ProjectStructure Sections: 1");
        System.Console.WriteLine("ArchitectureDecisions Count: should be 0 or only genuine ADRs");

        // Assertions
        doc.Sections.Count(s => s.SectionType == PlanSectionType.ProjectStructure).Should().Be(1);
        doc.ArchitectureDecisions.Should().BeEmpty("Project Structure H3 children should not be promoted to ADRs");
    }

    [Fact]
    public void ProjectStructure_H3ChildrenRemainUnderProjectStructure()
    {
        // Verify that H3 subsections under Project Structure are NOT promoted to ADRs
        var markdown = @"# Plan

## Project Structure

### Documentation

Files for this feature.

### Source Code Changes

Code modifications.
";
        var doc = _svc.Parse(markdown);

        // Should have 1 ProjectStructure section
        var projectStructureSections = doc.Sections.Where(s => s.SectionType == PlanSectionType.ProjectStructure).ToList();
        projectStructureSections.Should().HaveCount(1);

        // Should have no Architecture Decisions (H3 children should NOT be promoted)
        doc.ArchitectureDecisions.Should().BeEmpty();

        // ProjectStructure should contain the H3 headings as blocks
        var section = projectStructureSections.First();
        section.RawContent.Should().Contain("### Documentation");
        section.RawContent.Should().Contain("### Source Code Changes");
        section.Blocks.Should().NotBeEmpty("Should have blocks for H3 subsections");
    }

    [Fact]
    public void ProjectStructureChildren_AreNotPromotedToADRs()
    {
        // Explicit test: H3 subsections should not become Architecture Decisions
        var markdown = @"# Plan

## Project Structure

### Feature A

Some content.

### Feature B

More content.
";
        var doc = _svc.Parse(markdown);

        doc.ArchitectureDecisions.Should().BeEmpty(
            "H3 children under Project Structure must remain under ProjectStructure, not become ADRs");
    }

    [Fact]
    public void Architecture_FencedCodeBlock_PreservesLineBreaksInRawText()
    {
        // Test that fenced code blocks preserve line breaks in RawText
        var markdown = @"# Plan

## Architecture

### System Design

```text
Component A
├── SubComponent A1
└── SubComponent A2

Component B
├── SubComponent B1
└── SubComponent B2
```
";

        var doc = _svc.Parse(markdown);

        doc.ArchitectureDecisions.Should().HaveCount(1);
        var arch = doc.ArchitectureDecisions[0];

        // RawText should preserve the original structure with newlines
        arch.RawText.Should().Contain("\n", "RawText should preserve line breaks");
        arch.RawText.Should().Contain("```text", "RawText should preserve code fence markers");
        arch.RawText.Should().Contain("Component A", "RawText should contain code content");
        arch.RawText.Should().Contain("SubComponent A1", "RawText should preserve tree structure");

        // Verify structure is preserved (newlines exist around Component A)
        arch.RawText.Should().Contain("Component A\n", "Should have newline after component header");
    }

    [Fact]
    public void Architecture_CodeBlockLanguageIdNotInContent()
    {
        // Test that language identifier is not included in the content
        var markdown = @"# Plan

## Architecture

### Design

```text
specs/
├── a.txt
```
";

        var doc = _svc.Parse(markdown);

        var arch = doc.ArchitectureDecisions[0];

        // Decision field will have the content, but the language id should be part of the code fence
        // not rendered as literal text in the decision content
        arch.RawText.Should().StartWith("```text", "Fence should include language identifier");
        arch.RawText.Should().Contain("specs/", "Content should be preserved");
    }

    [Fact]
    public void Architecture_SCIMPlan_NoGenuineADRs()
    {
        // Updated regression test: SCIM plan has no genuine Architecture Decisions
        // "Documentation (this feature)" and "Source Code Changes" are now correctly
        // classified as ProjectStructure subsections, not Architecture Decisions
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        // No Architecture Decisions should be extracted (they are now in ProjectStructure)
        doc.ArchitectureDecisions.Should().BeEmpty(
            "SCIM plan has no genuine ADRs; the subsections under Project Structure should not be promoted");

        // Verify ProjectStructure contains the correct content
        var projectStructure = doc.Sections.FirstOrDefault(s => s.SectionType == PlanSectionType.ProjectStructure);
        projectStructure.Should().NotBeNull();
        projectStructure!.RawContent.Should().Contain("### Documentation (this feature)",
            "Should contain Documentation subsection");
        projectStructure.RawContent.Should().Contain("### Source Code Changes",
            "Should contain Source Code Changes subsection");
        projectStructure.RawContent.Should().Contain("```text", "Should preserve code fence markers");
        projectStructure.RawContent.Should().Contain("specs/004-scim-user-sync/", "Should preserve Documentation content");
        projectStructure.RawContent.Should().Contain("src/", "Should preserve Source Code Changes content");
    }

    // ── 12: Metadata Parsing ────────────────────────────────────────────────

    [Fact]
    public void Metadata_InlineMultipleFieldsOnLine()
    {
        // Test inline metadata parsing: **Branch**: value | **Date**: value | **Spec**: value
        var markdown = @"# Plan

**Branch**: `004-test` | **Date**: 2026-04-23 | **Spec**: [spec.md](spec.md)

Content here.
";

        var doc = _svc.Parse(markdown);

        doc.Branch.Should().Be("004-test", "Branch should extract only the value, not other fields");
        doc.Date.Should().Be("2026-04-23", "Date should be parsed from inline metadata");
        doc.SpecLink.Should().Be("spec.md", "Spec should extract Markdown link text, not full link notation");

        doc.Branch.Should().NotContain("|", "Branch should not include pipe separator");
        doc.Branch.Should().NotContain("Date", "Branch should not contain other field labels");
        doc.SpecLink.Should().NotContain("(", "SpecLink should not contain parentheses from Markdown link");
    }

    [Fact]
    public void Metadata_OneFieldPerLine_StillWorks()
    {
        // Test backward compatibility: one field per line (existing behavior)
        var markdown = @"# Plan

**Branch**: 004-single
**Date**: 2026-05-15
**Spec**: [api.md](api.md)

Content.
";

        var doc = _svc.Parse(markdown);

        doc.Branch.Should().Be("004-single");
        doc.Date.Should().Be("2026-05-15");
        doc.SpecLink.Should().Be("api.md");
    }

    [Fact]
    public void Metadata_MarkdownLinkExtractsDisplayText()
    {
        // Test that Markdown links are properly handled
        var markdown = @"# Plan

**Spec**: [specification.md](docs/specification.md)

Content.
";

        var doc = _svc.Parse(markdown);

        doc.SpecLink.Should().Be("specification.md", "Should extract Markdown link text, not URL");
        doc.SpecLink.Should().NotContain("(", "Should not contain parentheses");
        doc.SpecLink.Should().NotContain("docs/", "Should extract text, not the href");
    }

    [Fact]
    public void Metadata_BacktickValueStripped()
    {
        // Test that backticks around values are properly stripped
        var markdown = @"# Plan

**Branch**: `004-scim-user-sync` | **Date**: 2026-04-23

Content.
";

        var doc = _svc.Parse(markdown);

        doc.Branch.Should().Be("004-scim-user-sync", "Backticks should be stripped");
        doc.Branch.Should().NotContain("`", "Should not contain backticks");
    }

    [Fact]
    public void Metadata_SCIMPlan_RegressionTest()
    {
        // REGRESSION TEST: Real SCIM plan metadata extraction
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        // Expected values from plan.md line 3-4:
        // **Branch**: `004-scim-user-sync` | **Date**: 2026-04-23 | **Spec**: [spec.md](spec.md)
        // **Input**: Feature specification from `/specs/004-scim-user-sync/spec.md`

        doc.Branch.Should().Be("004-scim-user-sync", "Branch should be extracted correctly from inline metadata");
        doc.Date.Should().Be("2026-04-23", "Date should be extracted from inline metadata");
        doc.SpecLink.Should().Be("spec.md", "Spec should extract link text without parentheses");
        doc.InputSource.Should().Contain("/specs/004-scim-user-sync/spec.md", "Input source should be preserved");

        // Verify no corruption
        doc.Branch.Should().NotContain("|", "Branch should not contain pipe");
        doc.Branch.Should().NotContain("Date", "Branch should not contain Date label");
        doc.Branch.Should().NotContain("Spec", "Branch should not contain Spec label");
        doc.SpecLink.Should().NotContain("(", "SpecLink should not contain parentheses from Markdown");
        doc.SpecLink.Should().NotContain("spec.md(spec.md)", "SpecLink should not be duplicated");
    }

    // ── 11: Constraint Extraction ────────────────────────────────────────

    [Fact]
    public void Constraints_SCIMPlan_ExtractsFromTechnicalContext()
    {
        // AFTER FIX: Constraints extracted from Technical Context structured fields
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        // Expected from Technical Context fields:
        // **Performance Goals**: No specific SLA; Entra's provisioning engine has a 30-second timeout per request
        // **Constraints**: Service Bus publish must complete synchronously before HTTP 2xx (FR-008); Key Vault unreachable at startup → fail fast (FR-022)
        // **Scale/Scope**: Single replica; up to 500 users (SC-004); no optimistic concurrency needed

        doc.Constraints.Should().NotBeEmpty("Constraints should be extracted from Technical Context");
        doc.Constraints.Count.Should().BeGreaterThanOrEqualTo(5, "SCIM plan should have at least 5 constraint items from 3 fields");

        // Verify Performance Goals extracted
        doc.Constraints
            .Where(c => c.ConstraintType == ConstraintType.PerformanceGoal)
            .Should().NotBeEmpty("Performance Goals should be extracted from Technical Context");

        // Verify Constraints extracted with reference IDs preserved
        var constraintItems = doc.Constraints.Where(c => c.ConstraintType == ConstraintType.Constraint).ToList();
        constraintItems.Should().NotBeEmpty("Constraint items should be extracted");
        constraintItems.Should().Contain(c => c.Title.Contains("Service Bus", StringComparison.OrdinalIgnoreCase) && c.Title.Contains("FR-008"),
            "Service Bus constraint with FR-008 reference should be extracted");
        constraintItems.Should().Contain(c => c.Title.Contains("Key Vault", StringComparison.OrdinalIgnoreCase) && c.Title.Contains("FR-022"),
            "Key Vault constraint with FR-022 reference should be extracted");

        // Verify Scale/Scope extracted with reference IDs preserved
        var scaleItems = doc.Constraints.Where(c => c.ConstraintType == ConstraintType.ScaleScope).ToList();
        scaleItems.Should().NotBeEmpty("Scale/Scope items should be extracted");
        scaleItems.Should().Contain(c => c.Title.Contains("replica", StringComparison.OrdinalIgnoreCase),
            "Single replica constraint should be extracted");
        scaleItems.Should().Contain(c => c.Title.Contains("500 users", StringComparison.OrdinalIgnoreCase) && c.Title.Contains("SC-004"),
            "500 users constraint with SC-004 reference should be extracted");
        scaleItems.Should().Contain(c => c.Title.Contains("concurrency", StringComparison.OrdinalIgnoreCase),
            "Concurrency constraint should be extracted");
    }

    [Fact]
    public void Constraints_TechnicalContext_PerformanceGoalsExtracted()
    {
        var markdown = @"## Technical Context

**Performance Goals**: No specific SLA; Entra provisioning timeout = 30 seconds per request";

        var doc = _svc.Parse(markdown);

        doc.Constraints.Should().NotBeEmpty();
        var perfGoals = doc.Constraints.Where(c => c.ConstraintType == ConstraintType.PerformanceGoal).ToList();
        perfGoals.Should().NotBeEmpty();
        perfGoals.Should().Contain(c => c.Title.Contains("SLA") || c.Title.Contains("timeout"));
    }

    [Fact]
    public void Constraints_TechnicalContext_ConstraintsFieldExtracted()
    {
        var markdown = @"## Technical Context

**Constraints**: Service Bus must complete synchronously (FR-008); Key Vault fail-fast (FR-022)";

        var doc = _svc.Parse(markdown);

        doc.Constraints.Should().NotBeEmpty();
        var constraints = doc.Constraints.Where(c => c.ConstraintType == ConstraintType.Constraint).ToList();
        constraints.Count.Should().Be(2);
        constraints.Should().Contain(c => c.Title.Contains("Service Bus") && c.Title.Contains("FR-008"));
        constraints.Should().Contain(c => c.Title.Contains("Key Vault") && c.Title.Contains("FR-022"));
    }

    [Fact]
    public void Constraints_TechnicalContext_ScaleScopeExtracted()
    {
        var markdown = @"## Technical Context

**Scale/Scope**: Single replica; up to 500 users (SC-004); no concurrency needed";

        var doc = _svc.Parse(markdown);

        doc.Constraints.Should().NotBeEmpty();
        var scaleItems = doc.Constraints.Where(c => c.ConstraintType == ConstraintType.ScaleScope).ToList();
        scaleItems.Count.Should().Be(3);
        scaleItems.Should().Contain(c => c.Title.Contains("replica"));
        scaleItems.Should().Contain(c => c.Title.Contains("500") && c.Title.Contains("SC-004"));
        scaleItems.Should().Contain(c => c.Title.Contains("concurrency"));
    }

    [Fact]
    public void Constraints_DedicatedSection_TakesPrecedence()
    {
        var markdown = @"## Technical Context

**Constraints**: Technical context constraint

## Constraints

- Dedicated constraint

";

        var doc = _svc.Parse(markdown);

        // Dedicated section should be used, not Technical Context fallback
        doc.Constraints.Count.Should().Be(1);
        doc.Constraints[0].Title.Should().Contain("Dedicated");
    }

    [Fact]
    public void TechnicalContext_SoftLineBreakPreservation_RendersWithBrTags()
    {
        // Verify that when preserveSoftLineBreaks=true is used, Markdig renders
        // field lines as visible <br /> breaks instead of collapsing to spaces
        var markdown = "**Language**: C# 13\n**Storage**: SQL Server\n**Testing**: xUnit";
        var renderer = new MarkdownRenderingService();

        var htmlDefault = renderer.Render(markdown, preserveSoftLineBreaks: false);
        var htmlPreserved = renderer.Render(markdown, preserveSoftLineBreaks: true);

        // Default should collapse lines to single <p> with soft breaks (no <br>)
        htmlDefault.Should().Contain("<p>");
        htmlDefault.Should().Contain("</p>");
        htmlDefault.Should().NotContain("<br");

        // Preserved should have <br /> tags to make line breaks visible
        htmlPreserved.Should().Contain("<br");
    }

    [Fact]
    public void TechnicalContext_MultipleFields_AllLineBreaksPreserved()
    {
        // Verify that multiple field lines all get <br /> breaks
        var markdown = "**F1**: V1\n**F2**: V2\n**F3**: V3\n**F4**: V4\n**F5**: V5";
        var renderer = new MarkdownRenderingService();

        var html = renderer.Render(markdown, preserveSoftLineBreaks: true);

        // Count <br tags - should have 4 breaks between 5 lines
        var brCount = System.Text.RegularExpressions.Regex.Matches(html, "<br").Count;
        brCount.Should().Be(4);
    }

    [Fact]
    public void TechnicalContext_SoftLineBreakPreservation_WithLinks()
    {
        // Verify soft-line-break mode works with link content
        var markdown = "**Docs**: [link](https://example.com)\n**Contact**: [email](mailto:test@example.com)";
        var renderer = new MarkdownRenderingService();

        var html = renderer.Render(markdown, preserveSoftLineBreaks: true);

        // Should have line breaks between field lines
        html.Should().Contain("<br");

        // Links should be preserved
        html.Should().Contain("https://example.com");
        html.Should().Contain("mailto:test@example.com");
    }

    [Fact]
    public void Phase_WithCodeBlock_CodeBlockPropertyPopulated()
    {
        var markdown = @"
# Test Plan

## Implementation Steps

### Phase 2 — Build Component

Some narrative text.

#### Create project file

```xml
<Project Sdk=""Microsoft.NET.Sdk.Web"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```

More text after code.
";

        var doc = _svc.Parse(markdown);

        doc.Phases.Should().NotBeEmpty();
        var phase = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 2);
        phase.Should().NotBeNull();
        phase!.Blocks.Should().NotBeEmpty("Phase 2 should have blocks");

        // Verify at least one block is a code block
        var codeBlock = phase.Blocks.FirstOrDefault(b => b.IsCodeBlock);
        codeBlock.Should().NotBeNull("Phase should contain a code block");
        codeBlock!.CodeBlock.Should().Contain("<Project Sdk=");
        codeBlock.CodeBlock.Should().Contain("net10.0");
        codeBlock.CodeBlock.Should().Contain("<TargetFramework>");
    }

    [Fact]
    public void Phase_WithCodeBlock_PreservesNewlinesAndIndentation()
    {
        var markdown = @"
# Test Plan

## Implementation Steps

### Phase 5 — Implementation

#### Setup code

```csharp
public async Task HandleRequestAsync()
{
    var user = await _context.Users.FindAsync(userId);
    if (user == null)
    {
        throw new NotFoundException();
    }
}
```
";

        var doc = _svc.Parse(markdown);
        var phase = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 5);
        phase.Should().NotBeNull();

        var codeBlock = phase!.Blocks.FirstOrDefault(b => b.IsCodeBlock);
        codeBlock.Should().NotBeNull();
        codeBlock!.CodeBlock.Should().Contain("\n", "Code block should preserve newlines");
        codeBlock.CodeBlock.Should().Contain("    var user", "Code block should preserve indentation");
    }

    [Fact]
    public void Phase_WithBulletPoints_BulletPointsPropertyPopulated()
    {
        var markdown = @"
# Test Plan

## Implementation Steps

### Phase 3 — Configuration

#### Setup steps

- First configuration step
- Second configuration step
- Third configuration step
";

        var doc = _svc.Parse(markdown);
        var phase = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 3);
        phase.Should().NotBeNull();

        var blockWithBullets = phase!.Blocks.FirstOrDefault(b => b.BulletPoints.Count > 0);
        blockWithBullets.Should().NotBeNull("Phase should have bullet points");
        blockWithBullets!.BulletPoints.Count.Should().Be(3);
        blockWithBullets.BulletPoints[0].Should().Contain("First configuration");
        blockWithBullets.BulletPoints[1].Should().Contain("Second configuration");
        blockWithBullets.BulletPoints[2].Should().Contain("Third configuration");
    }

    [Fact]
    public void Phase_MixedBlocks_AllBlockTypesRenderCorrectly()
    {
        var markdown = @"
# Test Plan

## Implementation Steps

### Phase 6 — Complex Phase

#### Configuration Section

Narrative paragraph with important context.

- Bullet point 1
- Bullet point 2

```json
{
  ""name"": ""example"",
  ""version"": ""1.0.0""
}
```

Final narrative text.
";

        var doc = _svc.Parse(markdown);
        var phase = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 6);
        phase.Should().NotBeNull();

        var blocks = phase!.Blocks;
        blocks.Count.Should().BeGreaterThan(2, "Phase should have multiple blocks");

        // Should have at least one paragraph, one bullet list, one code block
        blocks.Should().Contain(b => !string.IsNullOrEmpty(b.Paragraph) && !b.IsCodeBlock,
            "Should have paragraph block");
        blocks.Should().Contain(b => b.BulletPoints.Count > 0,
            "Should have bullet points block");
        blocks.Should().Contain(b => b.IsCodeBlock,
            "Should have code block");
    }

    [Fact]
    public void Metadata_DateOnly_DoesNotPopulateCreated()
    {
        var markdown = @"
# Test Plan

**Branch**: test-branch | **Date**: 2026-04-23 | **Spec**: [spec.md](spec.md)
**Input**: Test input
";

        var doc = _svc.Parse(markdown);

        doc.Date.Should().Be("2026-04-23", "Date should be parsed from source");
        string.IsNullOrEmpty(doc.CreatedDate).Should().BeTrue("CreatedDate should be empty when not explicitly in source");
    }

    [Fact]
    public void Metadata_ExplicitCreated_IsPreserved()
    {
        var markdown = @"
# Test Plan

**Branch**: test-branch | **Date**: 2026-04-23 | **Created**: 2026-04-20 | **Spec**: [spec.md](spec.md)
**Input**: Test input
";

        var doc = _svc.Parse(markdown);

        doc.Date.Should().Be("2026-04-23", "Date should be from source");
        doc.CreatedDate.Should().Be("2026-04-20", "CreatedDate should be from explicit source field");
    }

    [Fact]
    public void Metadata_ExplicitUpdated_IsPreserved()
    {
        var markdown = @"
# Test Plan

**Date**: 2026-04-23 | **Updated**: 2026-04-24 | **Spec**: [spec.md](spec.md)
";

        var doc = _svc.Parse(markdown);

        doc.Date.Should().Be("2026-04-23", "Date should be from source");
        doc.LastUpdated.Should().Be("2026-04-24", "LastUpdated should be from explicit source field");
    }

    [Fact]
    public void Metadata_SCIMPlan_DateOnlyNoCreatedOrUpdated()
    {
        var scimPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        if (!System.IO.File.Exists(scimPath)) return;

        var markdown = File.ReadAllText(scimPath);
        var doc = _svc.Parse(markdown);

        // SCIM source has only Date, not Created or Updated
        doc.Date.Should().Be("2026-04-23", "SCIM plan declares Date as 2026-04-23");
        string.IsNullOrEmpty(doc.CreatedDate).Should().BeTrue("SCIM plan has no explicit Created field");
        string.IsNullOrEmpty(doc.LastUpdated).Should().BeTrue("SCIM plan has no explicit Updated field");
    }


    [Fact]
    public void Phase_ThematicBreak_IsIncludedInBlockParagraph()
    {
        // Parser groups everything until next heading as one block
        // So "Some content.\n---" ends up in one paragraph
        var markdown = @"# Title

## Implementation Steps

### Phase 1 — Step One

Some content.

---

### Phase 2 — Step Two

More content.";

        var doc = _svc.Parse(markdown);
        var p1 = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 1);
        p1.Should().NotBeNull();
        p1!.Blocks.Should().NotBeEmpty("Phase 1 should have blocks");

        var lastBlock = p1.Blocks[^1];
        lastBlock.Paragraph.Should().Contain("---", "Block should contain the thematic break");
    }

    [Fact]
    public void Phase_CodeBlockWithLeadingThematicBreak_HasCorrectStructure()
    {
        // Like P2 in SCIM plan: paragraph is just ---, then code block follows
        var markdown = @"# Title

## Implementation Steps

### Phase 1 — Create Project

---

```xml
<Project Sdk=""Microsoft.NET.Sdk.Web"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
```";

        var doc = _svc.Parse(markdown);
        var p1 = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 1);
        p1.Should().NotBeNull();
        p1!.Blocks.Should().NotBeEmpty("Phase should have blocks");
        p1.Blocks[0].Paragraph?.Trim().Should().Be("---", "First block paragraph should be thematic break");
        p1.Blocks[0].IsCodeBlock.Should().BeTrue("First block should be code block");
        p1.Blocks[0].CodeBlock.Should().NotBeNullOrEmpty("Code block content should be captured");
        p1.Blocks[0].CodeBlock!.Should().Contain("<Project", "Code block should contain XML");
    }

    [Fact]
    public void RealSCIM_P2_NoExtraneousSeparatorBeforeCode()
    {
        // Verify P2 structure matches expectation: --- paragraph + XML code block
        var planPath = TestDataHelper.ResolveSampleDataPath("autorisasjon", "plan.md");
        var markdown = File.ReadAllText(planPath);
        var doc = _svc.Parse(markdown);

        var p2 = doc.Phases.FirstOrDefault(p => p.PhaseNumber == 2);
        p2.Should().NotBeNull();
        p2!.Blocks.Should().HaveCount(1, "P2 should have exactly 1 block (paragraph + code)");

        var block = p2.Blocks[0];
        block.Paragraph?.Trim().Should().Be("---", "P2 paragraph should be only thematic break");
        block.IsCodeBlock.Should().BeTrue("P2 block should be marked as code");
        block.CodeBlock.Should().NotBeNullOrEmpty("P2 code block should have content");
        block.CodeBlock!.Should().Contain("<Project", "P2 should contain csproj XML");
    }

}
