using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class QaAuditorService : IQaAuditorService
{
    private readonly IArtifactTraceabilityService   _traceability;
    private readonly IConstitutionComplianceService _compliance;

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
        TaskTree?             tasks)
    {
        // Run sub-analyses once; all rule groups share the results
        var traceReport = _traceability.Analyze(constitution, spec, plan, tasks);
        var compReport  = constitution is not null
            ? _compliance.Analyze(constitution, spec, plan, tasks)
            : new ConstitutionComplianceReport { HasConstitution = false };

        var findings = new List<QaFinding>();
        var gaps     = new List<QaGap>();

        RunConstitutionRules(compReport,  constitution,           findings, gaps);
        RunSpecificationRules(spec,       traceReport,            findings, gaps);
        RunPlanRules(plan,                traceReport,            findings, gaps);
        RunTaskRules(tasks,               traceReport,            findings, gaps);
        RunTraceabilityRules(traceReport, constitution, spec, plan, tasks, findings, gaps);

        // Sort by severity (Critical first)
        findings.Sort((a, b) => a.Severity.CompareTo(b.Severity));
        gaps.Sort((a, b)     => a.Severity.CompareTo(b.Severity));

        var risks = BuildRisks(findings);
        var recs  = BuildRecommendations(findings);
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
            (g.ItemId?.Contains(query, ci) ?? false) ||
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

    // ── Rule group: Constitution ───────────────────────────────────────────────

    private static void RunConstitutionRules(
        ConstitutionComplianceReport compReport,
        ConstitutionDocument?        constitution,
        List<QaFinding>              findings,
        List<QaGap>                  gaps)
    {
        if (constitution is null) return;

        // CONST-001: Rule not covered
        foreach (var result in compReport.Results.Where(r => r.Status == ComplianceStatus.Missing))
        {
            var sev = result.RuleType == ConstitutionRuleType.Principle  ? QaSeverity.Critical :
                      result.RuleType == ConstitutionRuleType.Standard   ? QaSeverity.High :
                      result.RuleType == ConstitutionRuleType.Constraint ? QaSeverity.High :
                                                                           QaSeverity.Medium;
            findings.Add(F("CONST-001",
                $"Constitution rule {result.RuleId} not covered by any artifact",
                $"Rule '{result.RuleTitle}' ({result.RuleType}) has no coverage in the Specification, Plan, or Tasks.",
                sev, QaCategory.Constitution, result.RuleId));

            gaps.Add(new QaGap
            {
                GapArea     = "Missing Constitution Coverage",
                Description = $"{result.RuleId}: {result.RuleTitle}",
                ItemId      = result.RuleId,
                ItemTitle   = result.RuleTitle,
                Severity    = sev,
            });
        }

        // CONST-002: Rule partially covered
        foreach (var result in compReport.Results.Where(r => r.Status == ComplianceStatus.Partial))
        {
            findings.Add(F("CONST-002",
                $"Constitution rule {result.RuleId} only partially covered",
                $"Rule '{result.RuleTitle}' is not consistently referenced across all loaded artifacts.",
                QaSeverity.Medium, QaCategory.Constitution, result.RuleId));
        }

        // CONST-003: Violations
        foreach (var v in compReport.Violations)
        {
            var sev = v.Severity == ViolationSeverity.Critical ? QaSeverity.Critical :
                      v.Severity == ViolationSeverity.High     ? QaSeverity.High :
                                                                 QaSeverity.Medium;
            findings.Add(F("CONST-003",
                $"Constitution violation: {v.RuleId} in {v.Artifact}",
                v.Issue,
                sev, QaCategory.Compliance, $"{v.Artifact}: {v.RuleId}"));
        }
    }

    // ── Rule group: Specification ─────────────────────────────────────────────

    private static void RunSpecificationRules(
        SpecTree?                    spec,
        ArtifactTraceabilityReport   trace,
        List<QaFinding>              findings,
        List<QaGap>                  gaps)
    {
        if (spec is null)
        {
            gaps.Add(new QaGap
            {
                GapArea     = "Missing Specification Coverage",
                Description = "Specification not loaded — specification audit unavailable",
                Severity    = QaSeverity.High,
            });
            return;
        }

        var h = spec.Health;

        // SPEC-001: Requirements with no acceptance criteria
        // Use spec.Roots.Count as a presence check since FR-### headings are often SubSection
        bool hasSpecContent = spec.Roots.Count > 0 && h.TotalHeadings > 1;
        if (hasSpecContent && h.Tests + h.BddScenarios + h.SuccessCriteria == 0)
        {
            int reqCount = h.Requirements > 0 ? h.Requirements : h.TotalHeadings - 1;
            findings.Add(F("SPEC-001",
                "No acceptance criteria defined across requirements",
                $"Specification has {reqCount} requirement(s) but no acceptance criteria, BDD scenarios, or success criteria.",
                QaSeverity.High, QaCategory.Specification));
        }

        // SPEC-002: Unplanned requirements (from Spec→Plan chain)
        int unplanned = trace.SpecificationCoverage.MissingItems;
        if (unplanned > 0)
        {
            findings.Add(F("SPEC-002",
                $"{unplanned} requirement(s) without plan coverage",
                $"{unplanned} specification requirement(s) are not referenced in the implementation plan.",
                unplanned > 3 ? QaSeverity.High : QaSeverity.Medium, QaCategory.Specification));

            gaps.Add(new QaGap
            {
                GapArea     = "Missing Plan Coverage",
                Description = $"{unplanned} requirement(s) not covered by the plan",
                Severity    = unplanned > 3 ? QaSeverity.High : QaSeverity.Medium,
            });
        }

        // SPEC-003: Requirements with no task coverage (from Spec chain, when tasks loaded)
        int untasked = trace.SpecificationCoverage.MissingItems > 0
            ? trace.SpecificationCoverage.MissingItems  // already counted above
            : 0;
        // Use plan→task orphan count as a proxy for requirements without task coverage
        int orphanPlan = trace.PlanCoverage.MissingItems;
        if (orphanPlan > 0 && trace.PlanCoverage.TotalItems > 0)
        {
            findings.Add(F("SPEC-003",
                $"{orphanPlan} plan item(s) without task coverage, indicating untasked requirements",
                $"{orphanPlan} plan item(s) have no associated tasks — requirements they address may not be implemented.",
                orphanPlan > 3 ? QaSeverity.High : QaSeverity.Medium, QaCategory.Specification));
        }

        // SPEC-004: Ambiguous spec (high clarification count)
        if (h.Clarifications > 5)
        {
            findings.Add(F("SPEC-004",
                "High clarification count indicates specification ambiguity",
                $"{h.Clarifications} open clarification(s) detected. Resolve them before implementation.",
                QaSeverity.Medium, QaCategory.Specification));
        }

        // SPEC-005: Missing edge cases (uses same hasSpecContent as SPEC-001)
        if (hasSpecContent && h.EdgeCases == 0)
        {
            findings.Add(F("SPEC-005",
                "No edge cases documented",
                "Specification has requirements but no edge cases. Document boundary conditions and failure scenarios.",
                QaSeverity.Low, QaCategory.Specification));
        }
    }

    // ── Rule group: Plan ──────────────────────────────────────────────────────

    private static void RunPlanRules(
        PlanDocument?              plan,
        ArtifactTraceabilityReport trace,
        List<QaFinding>            findings,
        List<QaGap>                gaps)
    {
        if (plan is null)
        {
            gaps.Add(new QaGap
            {
                GapArea     = "Missing Plan Coverage",
                Description = "Plan not loaded — plan audit unavailable",
                Severity    = QaSeverity.High,
            });
            return;
        }

        var h = plan.Health;

        // PLAN-001: Missing implementation phases
        if (!h.HasImplementationPhases)
        {
            findings.Add(F("PLAN-001",
                "Missing implementation phases",
                "Plan has no implementation phases. Add phased delivery sections with tasks and deliverables.",
                QaSeverity.High, QaCategory.Plan));
        }

        // PLAN-002: Architecture decisions without rationale
        // Check both structured Rationale field and inline keyword in RawText
        foreach (var adr in plan.ArchitectureDecisions.Where(a =>
            string.IsNullOrWhiteSpace(a.Rationale) &&
            !a.RawText.Contains("Rationale", StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add(F("PLAN-002",
                $"Architecture decision {(string.IsNullOrEmpty(adr.Id) ? adr.Title : adr.Id)} missing rationale",
                $"ADR '{adr.Title}' has no documented rationale. Explain why this decision was made.",
                QaSeverity.Medium, QaCategory.Architecture,
                string.IsNullOrEmpty(adr.Id) ? adr.Title : adr.Id));
        }

        // PLAN-003: Missing risk analysis
        if (h.TotalRisks == 0)
        {
            findings.Add(F("PLAN-003",
                "Missing risk analysis",
                "Plan has no risks documented. Identify delivery risks with probability, impact, and mitigation.",
                QaSeverity.Medium, QaCategory.Plan));
        }

        // PLAN-004: Missing testing strategy
        if (!h.HasTestingInfo)
        {
            findings.Add(F("PLAN-004",
                "Missing testing strategy",
                "Plan has no testing section. Document test frameworks, coverage targets, and test approach.",
                QaSeverity.High, QaCategory.Testing));

            gaps.Add(new QaGap
            {
                GapArea     = "Missing Testing Coverage",
                Description = "No testing strategy documented in the plan",
                Severity    = QaSeverity.High,
            });
        }

        // PLAN-005: Plan items without task coverage
        int uncoveredItems = trace.PlanCoverage.MissingItems;
        if (uncoveredItems > 0 && trace.PlanCoverage.TotalItems > 0)
        {
            findings.Add(F("PLAN-005",
                $"{uncoveredItems} plan item(s) without task coverage",
                $"{uncoveredItems} plan item(s) have no associated tasks — implementation cannot be verified.",
                uncoveredItems > 3 ? QaSeverity.High : QaSeverity.Medium, QaCategory.Plan));

            gaps.Add(new QaGap
            {
                GapArea     = "Missing Task Coverage",
                Description = $"{uncoveredItems} plan item(s) with no associated tasks",
                Severity    = QaSeverity.Medium,
            });
        }
    }

    // ── Rule group: Tasks ─────────────────────────────────────────────────────

    private static void RunTaskRules(
        TaskTree?                  tasks,
        ArtifactTraceabilityReport trace,
        List<QaFinding>            findings,
        List<QaGap>                gaps)
    {
        if (tasks is null)
        {
            gaps.Add(new QaGap
            {
                GapArea     = "Missing Task Coverage",
                Description = "Tasks not loaded — task audit unavailable",
                Severity    = QaSeverity.High,
            });
            return;
        }

        var h = tasks.Health;

        // TASK-001: Orphan tasks (no plan/requirement linkage from traceability gaps)
        var orphanGaps = trace.Gaps
            .Where(g => g.GapIn == ArtifactType.Task && g.Status == TraceabilityStatus.Orphaned)
            .ToList();
        if (orphanGaps.Count > 0)
        {
            findings.Add(F("TASK-001",
                $"{orphanGaps.Count} orphan task(s) with no requirement or plan coverage",
                $"{orphanGaps.Count} task(s) are not linked to any specification requirement or plan item.",
                QaSeverity.Medium, QaCategory.Task));
        }

        // TASK-002: No testing tasks
        if (h.TotalTasks > 0 && h.TestingTasks == 0)
        {
            findings.Add(F("TASK-002",
                "No testing tasks defined",
                "Task list has no test implementation tasks. Add unit test, integration test, and verification tasks.",
                QaSeverity.High, QaCategory.Testing));

            gaps.Add(new QaGap
            {
                GapArea     = "Missing Testing Coverage",
                Description = "No testing tasks found in the task list",
                Severity    = QaSeverity.High,
            });
        }

        // TASK-003: Tasks without requirement references
        if (h.TotalTasks > 0 && h.UnlinkedTasks > 0)
        {
            findings.Add(F("TASK-003",
                $"{h.UnlinkedTasks} task(s) without requirement reference",
                $"{h.UnlinkedTasks} task(s) have no FR or SC references — traceability cannot be verified for these tasks.",
                QaSeverity.Low, QaCategory.Task));
        }

        // TASK-004: Missing verification/test tasks for coverage
        if (h.TotalTasks > 0 && h.SecurityTasks == 0 && h.FrLinkedTasks > 0)
        {
            // Tasks exist but no security verification found
            // Only flag as Info — not every feature needs security tasks
        }
    }

    // ── Rule group: Traceability ──────────────────────────────────────────────

    private static void RunTraceabilityRules(
        ArtifactTraceabilityReport trace,
        ConstitutionDocument?      constitution,
        SpecTree?                  spec,
        PlanDocument?              plan,
        TaskTree?                  tasks,
        List<QaFinding>            findings,
        List<QaGap>                gaps)
    {
        // TRACE-001: Missing Constitution→Spec links (only when both loaded)
        if (constitution is not null && spec is not null && trace.ConstitutionCoverage.TotalItems > 0)
        {
            int missing = trace.ConstitutionCoverage.MissingItems;
            if (missing > 0)
            {
                findings.Add(F("TRACE-001",
                    $"{missing} constitution rule(s) not referenced in specification",
                    $"{missing} constitution rule(s) have no corresponding requirements in the specification.",
                    missing > 3 ? QaSeverity.High : QaSeverity.Medium, QaCategory.Traceability));
            }
        }

        // TRACE-002: Missing Spec→Plan links (only when both loaded)
        if (spec is not null && plan is not null && trace.SpecificationCoverage.TotalItems > 0)
        {
            int missing = trace.SpecificationCoverage.MissingItems;
            if (missing > 0)
            {
                findings.Add(F("TRACE-002",
                    $"{missing} requirement(s) not referenced in the plan",
                    $"{missing} specification requirement(s) are not mentioned in the implementation plan.",
                    missing > 3 ? QaSeverity.High : QaSeverity.Medium, QaCategory.Traceability));
            }
        }

        // TRACE-003: Missing Plan→Task links (only when both loaded)
        if (plan is not null && tasks is not null && trace.PlanCoverage.TotalItems > 0)
        {
            int missing = trace.PlanCoverage.MissingItems;
            if (missing > 0)
            {
                findings.Add(F("TRACE-003",
                    $"{missing} plan item(s) with no covering tasks",
                    $"{missing} plan item(s) are not referenced in the task list — implementation coverage is incomplete.",
                    missing > 3 ? QaSeverity.High : QaSeverity.Medium, QaCategory.Traceability));
            }
        }
    }

    // ── Risks ─────────────────────────────────────────────────────────────────

    private static List<QaRisk> BuildRisks(List<QaFinding> findings)
    {
        return findings
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
    }

    // ── Recommendations ───────────────────────────────────────────────────────

    private static List<QaRecommendation> BuildRecommendations(List<QaFinding> findings)
    {
        var recs = new List<QaRecommendation>();

        foreach (var f in findings)
        {
            var text = f.RuleCode switch
            {
                "CONST-001" => $"Add coverage for {f.AffectedArtifact} to the specification, plan, and task list.",
                "CONST-002" => $"Extend coverage for {f.AffectedArtifact} across all loaded artifacts.",
                "CONST-003" => $"Resolve the violation for {f.AffectedArtifact?.Split(':').LastOrDefault()?.Trim()} in the plan.",
                "SPEC-001"  => "Add acceptance criteria (tests, BDD scenarios, or success criteria) to each requirement.",
                "SPEC-002"  => "Reference all specification requirements in the implementation plan.",
                "SPEC-003"  => "Create tasks for all plan items that cover specification requirements.",
                "SPEC-004"  => "Resolve open specification clarifications to eliminate ambiguity before implementation.",
                "SPEC-005"  => "Add edge case documentation to the specification.",
                "PLAN-001"  => "Add implementation phases with tasks and deliverables to the plan.",
                "PLAN-002"  => $"Document the rationale for architecture decision {f.AffectedArtifact}.",
                "PLAN-003"  => "Add a risk section to the plan with probability, impact, and mitigation for each risk.",
                "PLAN-004"  => "Add a testing strategy section to the plan documenting test frameworks and coverage targets.",
                "PLAN-005"  => "Create implementation tasks for all uncovered plan items.",
                "TASK-001"  => "Link orphan tasks to specification requirements or plan items.",
                "TASK-002"  => "Add testing tasks for each requirement and plan item.",
                "TASK-003"  => "Add FR/SC references to unlinked tasks to enable requirement traceability.",
                "TRACE-001" => "Add references to the missing constitution rules in specification requirements.",
                "TRACE-002" => "Ensure the plan explicitly addresses all specification requirements.",
                "TRACE-003" => "Create tasks for all plan items that lack task coverage.",
                _           => f.Title,
            };

            recs.Add(new QaRecommendation
            {
                Text             = text,
                Category         = f.Category,
                Priority         = f.Severity,
                AffectedArtifact = f.AffectedArtifact,
                RuleCode         = f.RuleCode,
            });
        }

        // De-duplicate by text
        return recs
            .GroupBy(r => r.Text, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Priority)
            .ToList();
    }

    // ── Health / scoring ──────────────────────────────────────────────────────

    private static QaAuditHealth BuildHealth(List<QaFinding> findings, List<QaGap> gaps)
    {
        int critical = findings.Count(f => f.Severity == QaSeverity.Critical);
        int high     = findings.Count(f => f.Severity == QaSeverity.High);
        int medium   = findings.Count(f => f.Severity == QaSeverity.Medium);
        int low      = findings.Count(f => f.Severity == QaSeverity.Low);
        int info     = findings.Count(f => f.Severity == QaSeverity.Info);
        int violations = findings.Count(f => f.RuleCode?.StartsWith("CONST-003") ?? false);

        // Score: start at 100, deduct per finding severity
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static QaFinding F(
        string ruleCode, string title, string description,
        QaSeverity severity, QaCategory category,
        string? affected = null) =>
        new QaFinding
        {
            RuleCode         = ruleCode,
            Title            = title,
            Description      = description,
            Severity         = severity,
            Category         = category,
            AffectedArtifact = affected,
        };

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
