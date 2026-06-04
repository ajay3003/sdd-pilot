using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

// ─── Result types ─────────────────────────────────────────────────────────────

public class ImpactedTest
{
    public Scenario Test { get; init; } = null!;
    public TraceLink Link { get; init; } = null!;
}

public class RegressionItem
{
    public Scenario Test { get; init; } = null!;
    public string Reason { get; init; } = string.Empty;
}

public class RequirementImpactSummary
{
    public int TotalLinkedTests { get; init; }
    /// <summary>In v1, all linked tests are accepted (Scenario has no rejection status).</summary>
    public int AcceptedTests { get; init; }
    /// <summary>1 when no tests are linked, 0 otherwise.</summary>
    public int MissingCoverage { get; init; }
    public RiskLevel RiskLevel { get; init; }
}

public class RequirementImpact
{
    public Scenario Requirement { get; init; } = null!;
    public IReadOnlyList<ImpactedTest> LinkedTests { get; init; } = [];
    public IReadOnlyList<RegressionItem> RegressionRecommendation { get; init; } = [];
    public RequirementImpactSummary Summary { get; init; } = null!;
}

public class RequirementRiskItem
{
    public Scenario Requirement { get; init; } = null!;
    public RiskLevel RiskLevel { get; init; }
    public int LinkedTestCount { get; init; }
}

public class ImpactSummary
{
    public int TotalRequirements { get; init; }
    public int HighRiskCount { get; init; }
    public int MediumRiskCount { get; init; }
    public int LowRiskCount { get; init; }
    public IReadOnlyList<RequirementRiskItem> Requirements { get; init; } = [];
}

// ─── Service ──────────────────────────────────────────────────────────────────

/// <summary>
/// The impact engine: computes risk levels and regression recommendations
/// from trace links. Designed as the central place for future impact features
/// (AI Change Auditor, Spec Drift Detection, AI QA Auditor).
/// </summary>
public sealed class ImpactAnalysisService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ImpactAnalysisService>? _logger;

    public ImpactAnalysisService(AppDbContext db, ILogger<ImpactAnalysisService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Returns the full impact analysis for a single requirement:
    /// linked tests, regression recommendation, and risk level.
    /// Returns null when the requirement is not found in the project.
    /// </summary>
    public async Task<RequirementImpact?> GetRequirementImpactAsync(
        string projectId,
        Guid requirementId,
        CancellationToken ct = default)
    {
        var requirement = await _db.Scenarios
            .FirstOrDefaultAsync(
                s => s.Id == requirementId
                  && s.ProjectId == projectId
                  && s.Kind == ScenarioKind.Requirement,
                ct);

        if (requirement is null)
        {
            _logger?.LogWarning(
                "ImpactAnalysis_RequirementNotFound {ProjectId} {RequirementId}",
                projectId, requirementId);
            return null;
        }

        // Load all Covers links where this requirement is the target.
        var links = await _db.TraceLinks
            .Where(t => t.ProjectId == projectId
                     && t.TargetId == requirementId
                     && t.TargetKind == TraceLinkArtifactKind.Scenario
                     && t.SourceKind == TraceLinkArtifactKind.Scenario
                     && t.LinkType == TraceLinkType.Covers)
            .ToListAsync(ct);

        // Load the source test scenarios.
        var testIds = links.Select(l => l.SourceId).ToHashSet();
        var tests = await _db.Scenarios
            .Where(s => testIds.Contains(s.Id) && s.Kind == ScenarioKind.Test)
            .ToListAsync(ct);

        var testById = tests.ToDictionary(t => t.Id);

        var linkedTests = links
            .Where(l => testById.ContainsKey(l.SourceId))
            .Select(l => new ImpactedTest { Test = testById[l.SourceId], Link = l })
            .OrderBy(it => it.Test.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var riskLevel = CalculateRisk(linkedTests.Count);

        var regression = BuildRegressionRecommendation(linkedTests, requirement);

        var summary = new RequirementImpactSummary
        {
            TotalLinkedTests = linkedTests.Count,
            AcceptedTests = linkedTests.Count,
            MissingCoverage = linkedTests.Count == 0 ? 1 : 0,
            RiskLevel = riskLevel,
        };

        _logger?.LogInformation(
            "ImpactAnalysis_RequirementEvaluated {ProjectId} {RequirementId} {RiskLevel} {LinkedTests}",
            projectId, requirementId, riskLevel, linkedTests.Count);

        return new RequirementImpact
        {
            Requirement = requirement,
            LinkedTests = linkedTests,
            RegressionRecommendation = regression,
            Summary = summary,
        };
    }

    /// <summary>
    /// Returns the project-wide impact summary: all requirements ranked by risk level.
    /// </summary>
    public async Task<ImpactSummary> GetImpactSummaryAsync(
        string projectId,
        CancellationToken ct = default)
    {
        var requirements = await _db.Scenarios
            .Where(s => s.ProjectId == projectId && s.Kind == ScenarioKind.Requirement)
            .ToListAsync(ct);

        var coversLinks = await _db.TraceLinks
            .Where(t => t.ProjectId == projectId
                     && t.SourceKind == TraceLinkArtifactKind.Scenario
                     && t.TargetKind == TraceLinkArtifactKind.Scenario
                     && t.LinkType == TraceLinkType.Covers)
            .ToListAsync(ct);

        // Count linked Tests per requirement target.
        var linkCountByRequirement = coversLinks
            .GroupBy(l => l.TargetId)
            .ToDictionary(g => g.Key, g => g.Count());

        var items = requirements
            .Select(req =>
            {
                var count = linkCountByRequirement.TryGetValue(req.Id, out var c) ? c : 0;
                return new RequirementRiskItem
                {
                    Requirement = req,
                    RiskLevel = CalculateRisk(count),
                    LinkedTestCount = count,
                };
            })
            .OrderBy(r => r.RiskLevel)  // High first (enum: Low=0, Medium=1, High=2 — so descending)
            .ThenBy(r => r.Requirement.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Sort High > Medium > Low
        items.Sort((a, b) =>
        {
            var riskCompare = b.RiskLevel.CompareTo(a.RiskLevel);
            return riskCompare != 0 ? riskCompare : string.Compare(a.Requirement.Title, b.Requirement.Title, StringComparison.OrdinalIgnoreCase);
        });

        return new ImpactSummary
        {
            TotalRequirements = requirements.Count,
            HighRiskCount = items.Count(r => r.RiskLevel == RiskLevel.High),
            MediumRiskCount = items.Count(r => r.RiskLevel == RiskLevel.Medium),
            LowRiskCount = items.Count(r => r.RiskLevel == RiskLevel.Low),
            Requirements = items,
        };
    }

    // ─── Internal helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Deterministic risk thresholds:
    ///   0 tests → High, 1 test → Medium, 2+ tests → Low.
    /// </summary>
    private static RiskLevel CalculateRisk(int linkedTestCount) => linkedTestCount switch
    {
        0 => RiskLevel.High,
        1 => RiskLevel.Medium,
        _ => RiskLevel.Low,
    };

    private static IReadOnlyList<RegressionItem> BuildRegressionRecommendation(
        IReadOnlyList<ImpactedTest> linkedTests,
        Scenario requirement)
    {
        if (linkedTests.Count == 0)
            return [];

        return linkedTests
            .Select(it => new RegressionItem
            {
                Test = it.Test,
                Reason = $"Directly covers \"{requirement.Title}\"",
            })
            .ToList();
    }
}
