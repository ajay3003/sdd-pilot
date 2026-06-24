using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

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
}
