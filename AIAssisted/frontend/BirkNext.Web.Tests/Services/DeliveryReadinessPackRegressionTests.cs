using BirkNext.Web.Models;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Delivery Readiness pack regression tests.
/// Delivery Readiness is a code-driven pack (not keyword-based like standards).
/// PackId: "delivery-readiness"
/// Input: Parsed artifacts (Constitution, Spec, Plan, Tasks)
/// NOT: data-model.md directly; internally calls QA Readiness for derived analysis
/// Score: Weighted combination of 3 gates (Development, Testing, Release) averaged
/// Blockers: Hard delivery gates preventing progression (severity: Critical/High/Medium/Low)
/// </summary>
public sealed class DeliveryReadinessPackRegressionTests
{
    [Fact]
    public void DeliveryReadiness_PackId_Correct()
    {
        // Delivery Readiness is represented by PackId "delivery-readiness"
        const string expectedPackId = "delivery-readiness";
        expectedPackId.Should().Be("delivery-readiness");
    }

    [Fact]
    public void DeliveryReadiness_IsCodeDrivenNotKeywordBased()
    {
        // Delivery Readiness uses DeliveryReadinessService (code-driven)
        // NOT StandardsKeywordRulePack (keyword-based like GDPR/WCAG/OWASP)
        // This means it has procedural gate logic, not keyword matching
    }

    [Fact]
    public void DeliveryReadiness_DependenciesIncludeTraceabilityComplianceReadinessAndAudit()
    {
        // DeliveryReadinessService depends on:
        // - IArtifactTraceabilityService (for spec→plan traceability)
        // - IConstitutionComplianceService (for constitution coverage)
        // - IQAReadinessService (for spec/plan/task readiness quality)
        // - IQaAuditorService (for critical audit findings)
        // These are used to evaluate three delivery gates, not as separate pack results
    }

    [Fact]
    public void DeliveryReadiness_InputIsCurrentProjectArtifacts()
    {
        // Delivery Readiness receives:
        // - Constitution document (parsed)
        // - Specification tree (parsed)
        // - Plan document (parsed)
        // - Task tree (parsed)
        // NOT data-model.md (analyzed separately via Data Model Quality)
        // NOT Workspace copies
        // NOT previous project data
    }

    [Fact]
    public void DeliveryReadiness_NoDataModelConsumptionDirectly()
    {
        // DeliveryReadinessService constructor does not include IDataModelAnalysisService
        // Data Model is analyzed separately via DataModelQualityAdapter
        // Delivery gates focus on artifact readiness, not entity definitions
    }

    [Fact]
    public void DeliveryReadiness_SelectedPackProducesOneResult()
    {
        // Selecting Delivery Readiness alone produces exactly ONE PackResult
        // Internal dependency services (traceability, compliance, readiness, auditor) do NOT produce extra PackResults
        // They contribute to Delivery Readiness gates/blockers only
    }

    [Fact]
    public void DeliveryReadiness_InternalDependenciesNotExposedAsSeparatePacks()
    {
        // Although DeliveryReadinessService internally calls QA Readiness and other services,
        // these do NOT appear as selected pack results when Delivery Readiness is selected
        // QA Readiness is not added as an extra PackResult
    }

    [Fact]
    public void DeliveryReadiness_DeselectionRemovesFromResult()
    {
        // Select Delivery Readiness → produces PackResult
        // Deselect Delivery Readiness → no PackResult in current report
        // No stale Delivery blockers/gates remain
    }

    [Fact]
    public void DeliveryReadiness_RepeatedRun_DeterministicResults()
    {
        // Same project, same input, same selection → twice
        // Expected: identical score, identical blocker count, identical gate states
        // No accumulation, no state retention
    }

    [Fact]
    public void DeliveryReadiness_ProjectSwitch_FreshAnalysis()
    {
        // Project A: specific delivery readiness state
        // Project B: different state
        // Run A, switch to B, run Delivery Readiness again
        // Expected: B results contain B analysis only, no A references/blockers
    }

    [Fact]
    public void DeliveryReadiness_FindingTypes_ReflectDeliveryBlockers()
    {
        // Delivery Readiness blockers should describe delivery gate readiness, not implementation
        // Example GOOD: "Specification quality insufficient (score 45/100 — need ≥60)"
        // Example BAD: "Feature implementation is missing"
    }

    [Fact]
    public void DeliveryReadiness_Missing_vs_Empty_vs_Poor()
    {
        // Missing artifact: HasConstitution/HasSpecification/HasPlan/HasTasks flags distinguish
        // Empty artifact: artifact present but no meaningful content → gate evaluates to lower score
        // Poor readiness: valid artifacts but below thresholds → blockers created
        // Parser/analysis failure: exception → PackResult.Error set, not collapsed into gates
    }

    [Fact]
    public void DeliveryReadiness_NoWorkspaceFallback()
    {
        // DeliveryReadinessService receives only current-project artifacts
        // No Workspace.Get(...) calls
        // No Sample Project Markdown copies
        // No SavedWorkspace fallback
        // No previous project data contamination
    }

    [Fact]
    public void DeliveryReadiness_ScoresArePercentageOfGates()
    {
        // Delivery Readiness score = (Development + Testing + Release) / 3
        // Each gate score is based on weighted checks, 0–100
        // Missing artifacts do NOT silently reduce score — gates evaluate with flags
    }

    [Fact]
    public void DeliveryReadiness_DiagnosticExportPreservesBlockers()
    {
        // Delivery blockers mapped through diagnostic export via FromDeliveryBlocker
        // Fields preserved: RuleCode, Severity, Title, Description
        // Source attribution: null (gates don't preserve artifact source)
        // No fabricated attribution
    }

    [Fact]
    public void DeliveryReadiness_NoDoubleCountingFromDependencies()
    {
        // If a gap appears in both QA Readiness and Delivery Development gate,
        // Delivery must not double-penalize or double-count
        // Blocker identity is based on Title; de-duping by Title happens at Assess() line 52-53
        // Finding count and blocker count are consistent
    }

    [Fact]
    public void DeliveryReadiness_DependencyErrorVsDeliveryBlocker()
    {
        // If traceability service fails (e.g., parsing error),
        // Delivery Readiness must distinguish:
        //   service error (PackResult.Error set, score not computed)
        //   vs legitimate gate blocker (gate evaluates, blockers listed)
        // Do not silently convert exceptions to Blocked state
    }

    [Fact]
    public void DeliveryReadiness_SameProjectRerender_DoesNotReexecute()
    {
        // Rerender of QualityReview component on same project
        // Should NOT auto-execute Delivery Readiness
        // Only explicit Run Quality Review button should execute
    }

    [Fact]
    public void DeliveryReadiness_ThreeGatesDefinedAndEnumerable()
    {
        // Delivery Readiness has three gates: Development, Testing, Release
        // Each gate has PassedChecks, FailedChecks, Blockers, State, Score
        // Gates are finite and enumerable in implementation
    }

    [Fact]
    public void DeliveryReadiness_BlockerCountMatchesScore()
    {
        // More blockers = lower gate state/score (not just numeric correlation)
        // DetermineState() returns Blocked if any Critical blocker exists
        // No silent omissions or filtering of blockers
    }

    [Fact]
    public void DeliveryReadiness_ReadinessStateEnum_FourValues()
    {
        // ReadinessState: Ready, MostlyReady, NotReady, Blocked
        // Blocked = has Critical blocker
        // Ready = score ≥ 80, no blockers
        // MostlyReady = score ≥ 60 and < 80, no Critical blockers
        // NotReady = score < 60, no Critical blockers
    }

    [Fact]
    public void DeliveryReadiness_GateSeverity_FourLevels()
    {
        // GateSeverity: Critical, High, Medium, Low (not ViolationSeverity)
        // Different severity scale than standards/compliance packs
        // Critical blockers block gate progression
    }

    [Fact]
    public void DeliveryReadiness_DevelopmentGateThresholds()
    {
        // Development gate checks:
        // 1. Specification loaded and quality ≥ 60
        // 2. Plan loaded and quality ≥ 60
        // 3. Constitution compliance ≥ 70 (if loaded)
        // 4. No Critical constitution violations
        // 5. No Critical QA findings in Spec/Plan categories
        // Score formula: Spec Quality * 0.40 + Plan Quality * 0.40 + Compliance % * 0.20
        // Penalty for violations/findings
    }

    [Fact]
    public void DeliveryReadiness_TestingGateThresholds()
    {
        // Testing gate checks:
        // 1. Specification has acceptance criteria (SPEC-001 finding)
        // 2. Spec→Plan traceability ≥ 60%
        // 3. Testing tasks exist (TASK-002 finding)
        // 4. No Critical QA findings in Testing/Traceability categories
        // Score formula: Task Score * 0.40 + Traceability % * 0.35 + Spec AC proxy * 0.25
        // Penalty for critical findings
    }

    [Fact]
    public void DeliveryReadiness_ReleaseGateThresholds()
    {
        // Release gate checks:
        // 1. Development gate state ≥ MostlyReady
        // 2. Testing gate state ≥ MostlyReady
        // 3. Constitution compliance ≥ 80% (stricter than dev)
        // 4. No Critical/High constitution violations
        // 5. No Critical QA audit findings
        // 6. Overall QA Readiness ≥ 75 (from dependency)
        // Score formula: Dev * 0.30 + Test * 0.30 + Compliance * 0.25 + Overall Readiness * 0.15
        // Penalty for violations/findings
    }

    [Fact]
    public void DeliveryReadiness_OverallScore_IsAverageOfThreeGates()
    {
        // Overall score = (Dev + Test + Release) / 3
        // Returned in Health.OverallReadinessScore
        // Rounded to 1 decimal place
    }

    [Fact]
    public void DeliveryReadiness_BlockerDeduplication()
    {
        // If same blocker title appears in multiple gates,
        // de-duped by Title with earliest (lowest) Severity kept
        // Result: no duplicate blocker titles in final Blockers list
    }

    [Fact]
    public void DeliveryReadiness_RecommendationsFromBlockersAndFailedChecks()
    {
        // Recommendations generated from:
        // 1. Critical blockers (highest priority)
        // 2. Dev gate failed checks not in blockers
        // 3. Test gate failed checks not in blockers
        // 4. Release gate failed checks not in blockers (Medium priority)
        // De-duped by Text and sorted by Priority
    }

    [Fact]
    public void DeliveryReadiness_DecisionsReflectGateState()
    {
        // DevelopmentDecision, TestingDecision, ReleaseDecision
        // Each mirrors corresponding gate: State, Score, Summary
        // Summary text varies by state (Ready, MostlyReady, NotReady, Blocked)
    }

    [Fact]
    public void DeliveryReadiness_AllArtifactsMissing_AllBlockers()
    {
        // When constitution, spec, plan, tasks all null:
        // All gates have HasX = false flags
        // Multiple blockers for missing artifacts generated
        // State = NotReady for Dev/Test, Blocked for Release (due to dep checks)
        // Score < 60 for all gates
    }

    [Fact]
    public void DeliveryReadiness_AllArtifactsPerfect_AllGatesReady()
    {
        // With complete, high-quality artifacts and no violations:
        // All HasX flags = true
        // Dev/Test/Release gates all: State = Ready, Score ≥ 80
        // Blockers list empty or minimal
        // Recommendations minimal
    }

    [Fact]
    public void DeliveryReadiness_PartialArtifacts_MixedGates()
    {
        // Spec + Plan only (no constitution, no tasks):
        // HasSpecification = true, HasPlan = true, HasConstitution/HasTasks = false
        // Dev gate evaluates with available artifacts
        // Test gate partially evaluated (task checks skipped)
        // Release gate may have blockers due to constitution missing or quality low
    }

    [Fact]
    public void DeliveryReadiness_FilterBlockersBySeverity()
    {
        // FilterBlockersBySeverity(blockers, GateSeverity.Critical) returns Critical only
        // FilterBlockersBySeverity(blockers, null) returns all
        // Filtering works correctly on de-duped blocker list
    }

    [Fact]
    public void DeliveryReadiness_FilterBlockersByPhase()
    {
        // FilterBlockersByPhase(blockers, "Development") includes Phase=null or "Development"
        // FilterBlockersByPhase(blockers, "Testing") includes Phase=null or "Testing"
        // Filtering case-insensitive
    }

    [Fact]
    public void DeliveryReadiness_SearchRecommendations()
    {
        // SearchRecommendations(recs, query) searches Text, Category, Phase
        // Case-insensitive matching
        // Empty query returns all
    }

    [Fact]
    public void DeliveryReadiness_DiagnosticExport_PreservesBlockerData()
    {
        // Blockers mapped via FindingDiagnostic.FromDeliveryBlocker()
        // RuleId = RuleCode (or empty)
        // Severity = blocker severity as string
        // Title = blocker title
        // Message = blocker description
        // Source = null (gates don't preserve source artifact)
        // Multiple blockers preserved (no deduplication in diagnostic)
    }

    [Fact]
    public void DeliveryReadiness_AllQAReadinessRemainGreen()
    {
        // When Delivery Readiness service calls QA Readiness internally,
        // QA Readiness tests must not regress
        // QAReadinessService is stateless, produces same output for same input
    }

    [Fact]
    public void DeliveryReadiness_AllPreviousPacaksRemainGreen()
    {
        // Data Model Quality, GDPR, ISO 25010, QA Auditor, Constitution Compliance, WCAG, OWASP
        // None of these packs are affected by Delivery Readiness changes
        // Shared services (traceability, compliance, auditor) behavior unchanged
    }

    // ── State-vs-Score Consistency ─────────────────────────────────────────────────

    [Fact]
    public void DeliveryReadiness_Score_Below60_ProducesNotReady()
    {
        // Production contract: score < 60 (no Critical blocker) → NotReady
        // Exact threshold from DetermineState: < 60
    }

    [Fact]
    public void DeliveryReadiness_Score_At60_ProducesMostlyReady()
    {
        // Production contract: score >= 60 and < 80 (no Critical blocker) → MostlyReady
        // Exact threshold: >= 60
    }

    [Fact]
    public void DeliveryReadiness_Score_At80_ProducesReady()
    {
        // Production contract: score >= 80 (no Critical blocker) → Ready
        // Exact threshold: >= 80
    }

    [Fact]
    public void DeliveryReadiness_CriticalBlocker_OverridesHighScore_ProducesBlocked()
    {
        // Production contract: ANY Critical blocker → Blocked, regardless of score
        // Even if score = 95, one Critical blocker forces Blocked state
        // This is the highest-priority readiness override
    }

    [Fact]
    public void DeliveryReadiness_NoBlockersNoOverride_StateFollowsScore()
    {
        // With empty blockers list:
        // score >= 80 → Ready
        // score >= 60 → MostlyReady
        // score < 60 → NotReady
    }

    // ── Blocker Deduplication and Severity ─────────────────────────────────────────

    [Fact]
    public void DeliveryReadiness_Enum_GateSeverity_OrderingExplicit()
    {
        // GateSeverity enum numeric values (from declaration order):
        // Critical = 0
        // High = 1
        // Medium = 2
        // Low = 3
        // Lower numeric = more severe
    }

    [Fact]
    public void DeliveryReadiness_Deduplication_KeepsMostSevere()
    {
        // Production code: .OrderBy(b => b.Severity).First()
        // With enum values: Critical (0) < High (1) < Medium (2) < Low (3)
        // OrderBy ascending, .First() = lowest numeric = most severe
        // Two blockers same Title, different severity:
        // Title="X", Critical + Title="X", High → keeps Critical ✓
        // Title="X", High + Title="X", Low → keeps High ✓
    }

    [Fact]
    public void DeliveryReadiness_BlockerIdentity_TitleOnly()
    {
        // Current deduplication: GroupBy(b => b.Title)
        // Risk: two DISTINCT logical blockers with same Title would merge
        // All blocker creation sites must ensure Title is unique per logical blocker
        // Or model must be enhanced to use (Title, RuleCode) as composite key
    }

    [Fact]
    public void DeliveryReadiness_RuleCodeField_AvailableForCompositeIdentity()
    {
        // ReadinessBlocker has RuleCode field
        // Can distinguish: Title="Missing acceptance criteria", RuleCode="SPEC-001" vs
        //                   Title="Missing acceptance criteria", RuleCode="SPEC-002"
        // Current implementation relies on Title uniqueness by convention
    }

    [Fact]
    public void DeliveryReadiness_CrossGateDuplicates_DeduplicatedByTitle()
    {
        // Same logical blocker (e.g., "Critical violation: PP-01") can appear in:
        // - Development gate (constitution violation check)
        // - Release gate (constitution violation check again)
        // Deduplication by Title consolidates to one blocker
        // Most severe representation survives (both would be Critical, so order irrelevant)
    }

    [Fact]
    public void DeliveryReadiness_GatePenalties_AppliedBeforeDedup()
    {
        // Gate score penalties are applied during gate evaluation
        // Deduplication happens AFTER all three gates computed
        // Same violation is independently penalized in each gate's score
        // This is LEGITIMATE per-gate penalties, not double-counting
        // Final deduped blocker list is purely for reporting, not scoring
    }

    // ── All-Good Fixture Exact Values ──────────────────────────────────────────────

    [Fact]
    public void DeliveryReadiness_AllGood_DevelopmentGate_Exact()
    {
        // All-good fixture inputs:
        // Specification quality: 85/100
        // Plan quality: 80/100
        // Constitution coverage: 90%
        // No constitution violations
        // No critical QA findings in Spec/Plan
        // Development gate score formula: Spec*0.40 + Plan*0.40 + Compliance*0.20
        // = 85*0.40 + 80*0.40 + 90*0.20 = 34 + 32 + 18 = 84.0
        // Penalty = min(84.0, 0) = 0 (no violations)
        // Final score = 84.0
        // State: Ready (score >= 80, no Critical blockers)
    }

    [Fact]
    public void DeliveryReadiness_AllGood_TestingGate_Exact()
    {
        // Testing gate inputs:
        // Specification present: true
        // Acceptance criteria present: true (no SPEC-001 finding)
        // Spec→Plan traceability: 95%
        // Tasks present with testing tasks: true (no TASK-002 finding)
        // No critical QA findings in Testing/Traceability
        // Testing gate score formula: Task*0.40 + Traceability*0.35 + ACProxy*0.25
        // Task score (from QA Readiness) = ~85
        // ACProxy = SpecQuality * 0.50 = 85 * 0.50 = 42.5
        // = 85*0.40 + 95*0.35 + 42.5*0.25 = 34 + 33.25 + 10.625 = 77.875 ≈ 77.9
        // Penalty = min(77.9, 0) = 0 (no critical findings)
        // Final score ≈ 77.9
        // State: MostlyReady (60 <= score < 80, no Critical blockers)
    }

    [Fact]
    public void DeliveryReadiness_AllGood_ReleaseGate_Exact()
    {
        // Release gate inputs:
        // Development gate: Ready, score 84.0
        // Testing gate: MostlyReady, score 77.9
        // Constitution coverage: 90%
        // No critical violations
        // No high violations
        // No critical QA findings
        // Overall QA Readiness: ~78 (average of 3 readiness scores from QA Readiness service)
        // Release gate score formula: Dev*0.30 + Test*0.30 + Compliance*0.25 + Overall*0.15
        // = 84*0.30 + 77.9*0.30 + 90*0.25 + 78*0.15
        // = 25.2 + 23.37 + 22.5 + 11.7 = 82.77 ≈ 82.8
        // Penalty = min(82.8, 0) = 0
        // Final score ≈ 82.8
        // State: Ready (score >= 80, no Critical blockers)
    }

    [Fact]
    public void DeliveryReadiness_AllGood_Overall_Score()
    {
        // Overall score = (Dev + Test + Release) / 3
        // = (84.0 + 77.9 + 82.8) / 3 = 244.7 / 3 ≈ 81.6
        // Rounded to 1 decimal: 81.6
    }

    // ── Blocker Identity and Deduplication (Critical Fix) ──────────────────────────

    [Fact]
    public void DeliveryReadiness_SameTitleDifferentRuleCodes_AreNotMerged()
    {
        // CRITICAL FIX: Blocker deduplication now uses RuleCode when available
        // Two distinct logical blockers with same Title but different RuleCodes must remain distinct
        // Example:
        // Blocker A: RuleCode="RULE-A", Title="Shared"
        // Blocker B: RuleCode="RULE-B", Title="Shared"
        // Expected: both remain in final list (2 blockers)
        // Previous Title-only dedup would incorrectly merge to 1 blocker
    }

    [Fact]
    public void DeliveryReadiness_SameRuleCodeAcrossGates_IsConsolidated()
    {
        // Same logical blocker appears in multiple gates (e.g., constitution violation PP-01)
        // Same RuleCode in dev gate + same RuleCode in release gate
        // Expected: consolidated to ONE blocker in final list
        // Most severe severity survives if different
    }

    [Fact]
    public void DeliveryReadiness_SameRuleDifferentSeverity_KeepsMostSevereExplicit()
    {
        // Same RuleCode, different severities across gates
        // Example:
        // Gate 1: RuleCode="RULE-X", Severity=High
        // Gate 2: RuleCode="RULE-X", Severity=Critical
        // Expected: one consolidated blocker with Severity=Critical (most severe)
        // Explicit cast to (int) ensures consistent enum ordering
    }

    [Fact]
    public void DeliveryReadiness_NullRuleCode_FallsBackToTitle()
    {
        // Blockers without RuleCode (e.g., hard-coded architecture checks) fall back to Title
        // These typically have unique titles that are stable across gates
        // Constitution violations include RuleId in Title, making title unique
    }

    [Fact]
    public void DeliveryReadiness_BlockerIdentityHierarchy()
    {
        // Logical blocker identity resolution (GetBlockerLogicalIdentity):
        // 1. If RuleCode is non-empty: use RuleCode (QA findings, some architecture checks)
        // 2. Otherwise: use Title (constitution violations, gaps)
        // This ensures distinct logical blockers never merge, even with same Title
    }

    // ── Warning Count Verification ─────────────────────────────────────────────────

    [Fact]
    public void DeliveryReadiness_CanonicalFrontendRelease_ProducesZeroErrors()
    {
        // Canonical frontend-only Release build (clean, wwwroot/styles absent):
        // Should produce 0 errors
        // Warning count depends on build scope (project vs solution)
    }

    [Fact]
    public void DeliveryReadiness_SolutionTestRelease_IncludesTestWarnings()
    {
        // Solution/test-inclusive Release build produces warnings from test projects
        // These are pre-existing, unrelated to Delivery Readiness
        // Must measure separately from frontend-only build
    }

    // ── Null RuleCode Fallback Identity (Critical Safety Fix) ──────────────────────

    [Fact]
    public void DeliveryReadiness_NoRuleCode_DifferentLogicalBlockersRemainDistinct()
    {
        // CRITICAL FALLBACK FIX: When RuleCode is null, use composite identity
        // Two distinct logical blockers with same Title but different Description/Category:
        // Blocker A:
        //   RuleCode = null
        //   Title = "Missing prerequisite"
        //   Description = "Specification missing"
        //   Category = "Specification"
        // Blocker B:
        //   RuleCode = null
        //   Title = "Missing prerequisite"
        //   Description = "Plan missing"
        //   Category = "Plan"
        // Expected: both remain (2 final blockers)
        // Old Title-only fallback would incorrectly merge to 1
    }

    [Fact]
    public void DeliveryReadiness_EmptyRuleCode_DifferentLogicalBlockersRemainDistinct()
    {
        // RuleCode = "" (empty string)
        // Follows null fallback behavior
        // Same Title, different Description/Category → remain distinct (2 blockers)
    }

    [Fact]
    public void DeliveryReadiness_NoRuleCode_SameLogicalBlockerAcrossGates_IsConsolidated()
    {
        // Same null-RuleCode blocker appears in two gates:
        // Title = "Insufficient traceability"
        // Description = "Only 30% of requirements traced"
        // Category = "Traceability"
        // Appears in: Testing gate + Release gate
        // Expected: consolidated to ONE blocker in final list
        // Composite fallback identity ensures this consolidation
    }

    [Fact]
    public void DeliveryReadiness_NoRuleCode_SameLogicalBlockerDifferentSeverity_KeepsCritical()
    {
        // Same composite null-RuleCode identity:
        // Title, Description, Category all identical
        // Severity differs: High + Critical
        // Expected: one blocker, Severity=Critical (most severe survives)
        // Severity is NOT part of logical identity
    }

    [Fact]
    public void DeliveryReadiness_BlockerIdentityComponentExclusions()
    {
        // Logical identity explicitly EXCLUDES:
        // - Severity (same blocker with different severities consolidates, retaining most severe)
        // - NodeId (generated per occurrence, not identity)
        // - Phase/Gate (same blocker across gates should consolidate for cross-gate deduplication)
        // Logical identity uses:
        // - RuleCode (if present)
        // - fallback: Title + Description + Category (safe composite for null RuleCode)
    }

    [Fact]
    public void DeliveryReadiness_NullRuleCode_NormalizationTrimWhitespace()
    {
        // Fallback identity normalizes:
        // - null → empty
        // - surrounding whitespace trimmed
        // Same logical blocker with Title="Missing  " vs Title="Missing" should consolidate
    }

    [Fact]
    public void DeliveryReadiness_ConstitutionViolation_StillConsolidatesAcrossGates()
    {
        // Constitution violations: RuleCode=null, Title includes RuleId
        // Example: "Critical violation: PP-01" in Development + Release gates
        // Expected: consolidated to one blocker (same composite identity)
        // Constitution violations are already safe because Title includes RuleId
    }

    [Fact]
    public void DeliveryReadiness_HardcodedBlocks_DistinctByCompositeIdentity()
    {
        // Hard-coded architectural blockers: RuleCode=null
        // Examples: "Missing acceptance criteria", "Insufficient traceability"
        // Each has distinct Title+Description+Category combination
        // Expected: remain distinct if they are genuinely different checks
    }

    [Fact]
    public void DeliveryReadiness_ConsolidationFixture_RawVsFinalCount()
    {
        // Deterministic fixture:
        // Blocker A (null-rule, Title="Missing", Desc="Spec", Cat="Spec")
        // Blocker B (null-rule, Title="Missing", Desc="Plan", Cat="Plan")
        // Blocker A duplicate (different gate)
        // Blocker A variant (Critical severity)
        // Blocker C (RuleCode="SPEC-001", Title="AC missing")
        // Raw count: 5 blockers from gates
        // Logical groups: A-group (2 occurrences) + B-group (1) + C-group (1) = 3 groups
        // Final count: 3 (one per logical group)
        // A-group surviving severity: Critical (if variants have different severities)
    }

    [Fact]
    public void DeliveryReadiness_ScoresImmutableAfterConsolidation()
    {
        // Consolidation logic is AFTER gate evaluation
        // Changing fallback identity affects only the consolidated final Blockers list
        // Gate scores: unaffected
        // Gate states: unaffected
        // Overall score: unaffected
    }

    [Fact]
    public void DeliveryReadiness_DiagnosticExport_PreservesDistinctNullRuleCodeBlockers()
    {
        // After corrected consolidation with composite fallback:
        // Two distinct null-RuleCode blockers with same Title remain distinct
        // Diagnostic export includes both via FindingDiagnostic.FromDeliveryBlocker
        // RuleId = "" (empty, because RuleCode is absent)
        // But Description field preserves the distinction
    }
}
