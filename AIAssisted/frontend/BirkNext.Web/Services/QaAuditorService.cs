using BirkNext.Web.Models;
using BirkNext.Web.Services.Engine;
using BirkNext.Web.Services.Engine.Packs;

namespace BirkNext.Web.Services;

/// <summary>
/// Orchestrates the deterministic QA audit using the shared <see cref="RuleEngine"/>.
/// Each dimension of the audit is a separate <see cref="IRulePack"/>:
///   <see cref="QaConstitutionRulePack"/>   — constitution coverage + violations
///   <see cref="QaSpecificationRulePack"/>  — spec quality (acceptance criteria, ambiguity)
///   <see cref="QaPlanRulePack"/>           — plan quality (phases, ADRs, testing, risks)
///   <see cref="QaTaskRulePack"/>           — task list quality (orphans, testing tasks)
///   <see cref="QaTraceabilityRulePack"/>   — end-to-end traceability chain
///
/// Adding a new audit dimension requires only a new <see cref="IRulePack"/> implementation
/// added to the <c>_rulePacks</c> list — no changes to this service or any page.
/// </summary>
public sealed class QaAuditorService : IQaAuditorService
{
    private readonly IArtifactTraceabilityService   _traceability;
    private readonly IConstitutionComplianceService _compliance;
    private readonly RuleEngine                     _engine = new();

    private static readonly IRulePack[] _rulePacks =
    [
        new QaConstitutionRulePack(),
        new QaSpecificationRulePack(),
        new QaPlanRulePack(),
        new QaTaskRulePack(),
        new QaTraceabilityRulePack(),
    ];

    public QaAuditorService(
        IArtifactTraceabilityService   traceability,
        IConstitutionComplianceService compliance)
    {
        _traceability = traceability;
        _compliance   = compliance;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public QaAuditReport Audit(
        ConstitutionDocument? constitution,
        SpecTree?             spec,
        PlanDocument?         plan,
        TaskTree?             tasks,
        ReviewContext?        context = null)
    {
        // Use pre-built ReviewContext if provided (consumer pattern),
        // otherwise build it here (producer pattern).
        // All rule packs share the same ReviewContext via RuleContext —
        // no pack triggers duplicate semantic model building.
        var reviewContext = context ?? ReviewContextFactory.Create(
            ConstitutionAnalysisService.BuildSemanticModel(constitution ?? new()),
            SpecExplorerService.BuildSemanticModel(spec ?? new(), ""),
            PlanAnalysisService.BuildSemanticModel(plan ?? new()),
            TaskExplorerService.BuildSemanticModel(tasks ?? new()),
            new DataModelSemanticModel());

        var traceReport = _traceability.Analyze(constitution, spec, plan, tasks, reviewContext);
        var compReport  = constitution is not null
            ? _compliance.Analyze(constitution, spec, plan, tasks)
            : new ConstitutionComplianceReport { HasConstitution = false };

        var ruleContext = new RuleContext
        {
            Constitution     = constitution,
            Spec             = spec,
            Plan             = plan,
            Tasks            = tasks,
            Trace            = traceReport,
            ComplianceReport = compReport,
        };

        var packResults = _engine.Run(ruleContext, _rulePacks);

        var rawFindings = packResults.SelectMany(pr => pr.Findings).ToList();
        var rawGaps     = packResults.SelectMany(pr => pr.Gaps).ToList();

        var findings = rawFindings.Select(MapToQaFinding).ToList();
        var gaps     = rawGaps.Select(MapToQaGap).ToList();

        findings.Sort((a, b) => a.Severity.CompareTo(b.Severity));
        gaps.Sort((a, b)     => a.Severity.CompareTo(b.Severity));

        var risks  = BuildRisks(findings);
        var recs   = BuildRecommendations(rawFindings);
        var health = BuildHealth(findings, gaps);

        return new QaAuditReport
        {
            Findings        = findings,
            Risks           = risks,
            Gaps            = gaps,
            Recommendations = recs,
            Health          = health,
            HasConstitution  = constitution is not null,
            HasSpecification = spec         is not null,
            HasPlan          = plan         is not null,
            HasTasks         = tasks        is not null,
        };
    }

    // ── Search / filter ───────────────────────────────────────────────────────

    public IEnumerable<QaFinding> SearchFindings(IEnumerable<QaFinding> findings, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return findings;
        var ci = StringComparison.OrdinalIgnoreCase;
        return findings.Where(f =>
            f.Title.Contains(query, ci) ||
            f.Description.Contains(query, ci) ||
            f.RuleCode.Contains(query, ci) ||
            f.Category.ToString().Contains(query, ci) ||
            (f.AffectedArtifact?.Contains(query, ci) ?? false));
    }

    public IEnumerable<QaFinding> FilterFindingsBySeverity(IEnumerable<QaFinding> findings, QaSeverity? severity) =>
        severity is null ? findings : findings.Where(f => f.Severity == severity);

    public IEnumerable<QaFinding> FilterFindingsByCategory(IEnumerable<QaFinding> findings, QaCategory? category) =>
        category is null ? findings : findings.Where(f => f.Category == category);

    public IEnumerable<QaGap> SearchGaps(IEnumerable<QaGap> gaps, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return gaps;
        var ci = StringComparison.OrdinalIgnoreCase;
        return gaps.Where(g =>
            g.GapArea.Contains(query, ci) ||
            g.Description.Contains(query, ci) ||
            (g.ItemId?.Contains(query, ci)    ?? false) ||
            (g.ItemTitle?.Contains(query, ci) ?? false));
    }

    public IEnumerable<QaRecommendation> SearchRecommendations(IEnumerable<QaRecommendation> recs, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return recs;
        var ci = StringComparison.OrdinalIgnoreCase;
        return recs.Where(r =>
            r.Text.Contains(query, ci) ||
            r.Category.ToString().Contains(query, ci) ||
            (r.AffectedArtifact?.Contains(query, ci) ?? false));
    }

    public IEnumerable<QaRecommendation> FilterRecommendationsByCategory(
        IEnumerable<QaRecommendation> recs, QaCategory? category) =>
        category is null ? recs : recs.Where(r => r.Category == category);

    // ── Mapping: engine types → domain types ─────────────────────────────────

    private static QaFinding MapToQaFinding(RuleFinding f) => new()
    {
        RuleCode         = f.RuleId,
        Title            = f.Title,
        Description      = f.Description,
        Severity         = ParseSeverity(f.Severity),
        Category         = ParseCategory(f.Category),
        AffectedArtifact = f.AffectedItem,
    };

    private static QaGap MapToQaGap(RuleGap g) => new()
    {
        GapArea     = g.GapArea,
        Description = g.Description,
        ItemId      = g.ItemId,
        ItemTitle   = g.ItemTitle,
        Severity    = ParseSeverity(g.Severity),
    };

    private static QaSeverity ParseSeverity(string s) => s switch
    {
        "Critical" => QaSeverity.Critical,
        "High"     => QaSeverity.High,
        "Low"      => QaSeverity.Low,
        "Info"     => QaSeverity.Info,
        _          => QaSeverity.Medium,
    };

    private static QaCategory ParseCategory(string s) =>
        Enum.TryParse<QaCategory>(s, ignoreCase: true, out var c) ? c : QaCategory.Constitution;

    // ── Risks ─────────────────────────────────────────────────────────────────

    private static List<QaRisk> BuildRisks(List<QaFinding> findings) =>
        findings
            .Where(f => f.Severity == QaSeverity.Critical || f.Severity == QaSeverity.High)
            .Select(f => new QaRisk
            {
                Title       = f.Title,
                Description = f.Description,
                Severity    = f.Severity,
                Category    = f.Category,
                RuleCode    = f.RuleCode,
                Mitigation  = InferMitigation(f.RuleCode),
            })
            .ToList();

    // ── Recommendations ───────────────────────────────────────────────────────

    private static List<QaRecommendation> BuildRecommendations(List<RuleFinding> rawFindings)
    {
        var recs = new List<QaRecommendation>();

        foreach (var f in rawFindings)
        {
            // Map rule codes to recommendation text — preserves exact phrasing from the original service.
            var text = f.RuleId switch
            {
                "CONST-001" => $"Add coverage for {f.AffectedItem} to the specification, plan, and task list.",
                "CONST-002" => $"Extend coverage for {f.AffectedItem} across all loaded artifacts.",
                "CONST-003" => $"Resolve the violation for {f.AffectedItem?.Split(':').LastOrDefault()?.Trim()} in the plan.",
                "SPEC-001"  => "Add acceptance criteria (tests, BDD scenarios, or success criteria) to each requirement.",
                "SPEC-002"  => "Reference all specification requirements in the implementation plan.",
                "SPEC-003"  => "Create tasks for all plan items that cover specification requirements.",
                "SPEC-004"  => "Resolve open specification clarifications to eliminate ambiguity before implementation.",
                "SPEC-005"  => "Add edge case documentation to the specification.",
                "PLAN-001"  => "Add implementation phases with tasks and deliverables to the plan.",
                "PLAN-002"  => $"Document the rationale for architecture decision {f.AffectedItem}.",
                "PLAN-003"  => "Add a risk section to the plan with probability, impact, and mitigation for each risk.",
                "PLAN-004"  => "Add a testing strategy section to the plan documenting test frameworks and coverage targets.",
                "PLAN-005"  => "Create implementation tasks for all uncovered plan items.",
                "TASK-001"  => "Link orphan tasks to specification requirements or plan items.",
                "TASK-002"  => "Add testing tasks for each requirement and plan item.",
                "TASK-003"  => "Add FR/SC references to unlinked tasks to enable requirement traceability.",
                "TRACE-001" => "Add references to the missing constitution rules in specification requirements.",
                "TRACE-002" => "Ensure the plan explicitly addresses all specification requirements.",
                "TRACE-003" => "Create tasks for all plan items that lack task coverage.",
                _           => f.Recommendation.Length > 0 ? f.Recommendation : f.Title,
            };

            recs.Add(new QaRecommendation
            {
                Text             = text,
                Category         = ParseCategory(f.Category),
                Priority         = ParseSeverity(f.Severity),
                AffectedArtifact = f.AffectedItem,
                RuleCode         = f.RuleId,
            });
        }

        return recs
            .GroupBy(r => r.Text, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Priority)
            .ToList();
    }

    // ── Health / scoring ──────────────────────────────────────────────────────

    private static QaAuditHealth BuildHealth(List<QaFinding> findings, List<QaGap> gaps)
    {
        int critical   = findings.Count(f => f.Severity == QaSeverity.Critical);
        int high       = findings.Count(f => f.Severity == QaSeverity.High);
        int medium     = findings.Count(f => f.Severity == QaSeverity.Medium);
        int low        = findings.Count(f => f.Severity == QaSeverity.Low);
        int info       = findings.Count(f => f.Severity == QaSeverity.Info);
        int violations = findings.Count(f => f.RuleCode?.StartsWith("CONST-003") ?? false);

        double score = Math.Max(0.0,
            100.0
            - critical * 10.0
            - high     *  5.0
            - medium   *  2.0
            - low      *  1.0);

        return new QaAuditHealth
        {
            TotalFindings    = findings.Count,
            CriticalCount    = critical,
            HighCount        = high,
            MediumCount      = medium,
            LowCount         = low,
            InfoCount        = info,
            CoverageGapCount = gaps.Count,
            ViolationCount   = violations,
            AuditScore       = Math.Round(score, 1),
        };
    }

    // ── Mitigation hints ──────────────────────────────────────────────────────

    private static string? InferMitigation(string ruleCode) => ruleCode switch
    {
        "CONST-001" => "Add coverage for this rule in the specification requirements, implementation plan, and task list.",
        "CONST-003" => "Review the non-compliant item in the plan and update its status to comply.",
        "SPEC-001"  => "Add acceptance tests, BDD scenarios, or success criteria to each requirement heading.",
        "SPEC-002"  => "Add a plan item or phase that explicitly references each uncovered requirement.",
        "PLAN-001"  => "Add implementation phases with tasks, milestones, and deliverables.",
        "PLAN-002"  => "Add a 'Rationale:' section to the ADR explaining the reasoning behind the decision.",
        "PLAN-004"  => "Add a Testing section documenting frameworks, coverage targets, and test strategy.",
        "TASK-001"  => "Add FR-### or SC-### references to orphan tasks, or create a parent plan item for them.",
        "TASK-002"  => "Add test implementation tasks for each requirement in the task list.",
        "TRACE-001" => "Add the missing constitution rule IDs (e.g. PP-01) to the relevant specification requirements.",
        "TRACE-002" => "Ensure each specification requirement is referenced in a plan phase or implementation section.",
        "TRACE-003" => "Create task entries for each plan item that currently has no associated tasks.",
        _           => null,
    };
}
