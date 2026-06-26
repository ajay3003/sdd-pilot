using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

/// <summary>
/// Detects spec drift using deterministic rules applied to live traceability data.
/// Orchestrates ImpactAnalysisService (requirement risk) and direct DB queries
/// (orphan test detection). No AI call — pure data analysis.
///
/// Drift rules implemented (v1):
///   R1 — Requirement with 0 linked tests → CoverageGap (High)
///   R2 — Requirement with exactly 1 linked test → PartialCoverage (Medium)
///   R3 — Test not linked to any requirement → OrphanTest (Medium)
///   R4 — Overall coverage below 50% → LowCoverage (High)
///
/// Extension points (not implemented):
///   - Git commit / PR / file-change integration (field hooks on ChangeAuditRequest pattern)
///   - Specification version history comparison (would add R5: coverage regression)
///   - Repository scanning for new tests/requirements not yet in the library
/// </summary>
public sealed class SpecDriftDetectionService
{
    private readonly AppDbContext _db;
    private readonly ImpactAnalysisService _impactService;
    private readonly ILogger<SpecDriftDetectionService> _logger;

    public SpecDriftDetectionService(
        AppDbContext db,
        ImpactAnalysisService impactService,
        ILogger<SpecDriftDetectionService> logger)
    {
        _db = db;
        _impactService = impactService;
        _logger = logger;
    }

    public async Task<SpecDriftReport> GetSpecDriftReportAsync(
        string projectId,
        CancellationToken ct = default)
    {
        // ── 1. Reuse ImpactAnalysisService for requirement risk levels ─────────
        // Avoids recalculating the 0/1/2+ threshold logic that already lives there.
        var impact = await _impactService.GetImpactSummaryAsync(projectId, ct);

        // ── 2. Orphan test detection — tests with no Covers links ─────────────
        var allTests = await _db.Scenarios
            .Where(s => s.ProjectId == projectId && s.Kind == ScenarioKind.Test)
            .OrderBy(s => s.Title)
            .ToListAsync(ct);

        var coveredTestIds = (await _db.TraceLinks
            .Where(t => t.ProjectId == projectId
                     && t.LinkType == TraceLinkType.Covers
                     && t.SourceKind == TraceLinkArtifactKind.Scenario)
            .Select(t => t.SourceId)
            .ToListAsync(ct))
            .ToHashSet();

        var orphanTests = allTests
            .Where(t => !coveredTestIds.Contains(t.Id))
            .ToList();

        // ── 3. Map requirements at risk (High and Medium) ─────────────────────
        var requirementsAtRisk = impact.Requirements
            .Where(r => r.RiskLevel != RiskLevel.Low)
            .Select(r => new DriftRequirement
            {
                Requirement = r.Requirement,
                DriftRisk = r.RiskLevel,
                LinkedTestCount = r.LinkedTestCount,
                DriftReason = r.RiskLevel == RiskLevel.High
                    ? "No tests linked — requirement is unvalidated."
                    : "Only one test linked — single point of failure for this requirement.",
            })
            .ToList();

        // ── 4. Coverage percentage ────────────────────────────────────────────
        var coveragePercent = impact.TotalRequirements == 0
            ? 100.0
            : Math.Round(
                (double)(impact.MediumRiskCount + impact.LowRiskCount)
                / impact.TotalRequirements * 100.0, 1);

        // ── 5. Build findings (one per triggered rule) ────────────────────────
        var findings = BuildFindings(impact, orphanTests.Count, coveragePercent);

        // ── 6. Compute overall drift risk ─────────────────────────────────────
        var overallRisk = DetermineOverallRisk(impact, orphanTests.Count, coveragePercent);

        // ── 7. Recommended actions ────────────────────────────────────────────
        var actions = BuildRecommendedActions(impact, orphanTests.Count, requirementsAtRisk);

        _logger.LogInformation(
            "SpecDrift_Report {ProjectId} {OverallRisk} {AtRisk} {Orphans} {CovPct}",
            projectId, overallRisk, requirementsAtRisk.Count, orphanTests.Count, coveragePercent);

        return new SpecDriftReport
        {
            OverallDriftRisk = overallRisk,
            TotalRequirements = impact.TotalRequirements,
            RequirementsAtRisk = requirementsAtRisk.Count,
            CoverageGaps = impact.HighRiskCount,
            OrphanTestCount = orphanTests.Count,
            CoveragePercent = coveragePercent,
            RequirementsAtRiskList = requirementsAtRisk,
            OrphanTests = orphanTests,
            Findings = findings,
            RecommendedActions = actions,
        };
    }

    // ── Rule helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Drift risk levels mirror ImpactAnalysisService thresholds for consistency.
    ///   High  — any uncovered requirements, OR overall coverage below 25%
    ///   Medium — partially covered requirements, OR orphan tests, OR coverage below 75%
    ///   Low   — all requirements covered, no orphans, coverage ≥ 75%
    /// </summary>
    private static RiskLevel DetermineOverallRisk(
        ImpactSummary impact, int orphanCount, double coveragePercent)
    {
        if (impact.HighRiskCount > 0 || coveragePercent < 25)
            return RiskLevel.High;

        if (impact.MediumRiskCount > 0 || orphanCount > 0 || coveragePercent < 75)
            return RiskLevel.Medium;

        return RiskLevel.Low;
    }

    private static IReadOnlyList<DriftFinding> BuildFindings(
        ImpactSummary impact, int orphanCount, double coveragePercent)
    {
        var findings = new List<DriftFinding>();

        // R1 — coverage gaps
        if (impact.HighRiskCount > 0)
            findings.Add(new DriftFinding
            {
                Category = "CoverageGap",
                Description = $"{impact.HighRiskCount} requirement(s) have no linked tests and are completely unvalidated.",
                Severity = RiskLevel.High,
            });

        // R2 — partial coverage
        if (impact.MediumRiskCount > 0)
            findings.Add(new DriftFinding
            {
                Category = "PartialCoverage",
                Description = $"{impact.MediumRiskCount} requirement(s) are covered by only one test — a single failure leaves them unvalidated.",
                Severity = RiskLevel.Medium,
            });

        // R3 — orphan tests
        if (orphanCount > 0)
            findings.Add(new DriftFinding
            {
                Category = "OrphanTest",
                Description = $"{orphanCount} test(s) are not linked to any requirement — their contribution to coverage is unmeasured.",
                Severity = RiskLevel.Medium,
            });

        // R4 — low overall coverage
        if (coveragePercent < 50 && impact.TotalRequirements > 0)
            findings.Add(new DriftFinding
            {
                Category = "LowCoverage",
                Description = $"Overall requirement coverage is {coveragePercent:F0}% — below the 50% minimum health threshold.",
                Severity = RiskLevel.High,
            });

        return findings;
    }

    private static IReadOnlyList<string> BuildRecommendedActions(
        ImpactSummary impact,
        int orphanCount,
        IReadOnlyList<DriftRequirement> requirementsAtRisk)
    {
        var actions = new List<string>();

        if (impact.HighRiskCount > 0)
            actions.Add($"Link tests to {impact.HighRiskCount} uncovered requirement(s) via Traceability & Coverage.");

        if (impact.MediumRiskCount > 0)
            actions.Add($"Add a second test to {impact.MediumRiskCount} partially-covered requirement(s) to eliminate single-point-of-failure risk.");

        if (orphanCount > 0)
            actions.Add($"Review {orphanCount} orphan test(s): link each to a requirement or remove it if it is redundant.");

        if (requirementsAtRisk.Count > 0)
            actions.Add("Open Impact Analysis for each at-risk requirement to see its regression recommendation before accepting any change.");

        if (actions.Count == 0)
            actions.Add("Coverage is healthy. Re-run Spec Drift Detection after each sprint to catch regressions early.");

        return actions;
    }
}
