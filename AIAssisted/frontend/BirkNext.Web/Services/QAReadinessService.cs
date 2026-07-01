using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class QAReadinessService : IQAReadinessService
{
    private readonly IArtifactTraceabilityService   _traceability;
    private readonly IConstitutionComplianceService _compliance;

    public QAReadinessService(
        IArtifactTraceabilityService   traceability,
        IConstitutionComplianceService compliance)
    {
        _traceability = traceability;
        _compliance   = compliance;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public QAReadinessReport Assess(
        ConstitutionDocument? constitution,
        SpecTree?             spec,
        PlanDocument?         plan,
        TaskTree?             tasks)
    {
        // Run sub-analyses so scoring can use their outputs
        var reviewContext = ReviewContextFactory.Create(
            ConstitutionAnalysisService.BuildSemanticModel(constitution ?? new()),
            SpecExplorerService.BuildSemanticModel(spec ?? new(), ""),
            PlanAnalysisService.BuildSemanticModel(plan ?? new()),
            TaskExplorerService.BuildSemanticModel(tasks ?? new()),
            new DataModelSemanticModel());

        var traceReport      = _traceability.Analyze(constitution, spec, plan, tasks, reviewContext);
        var complianceReport = constitution is not null
            ? _compliance.Analyze(constitution, spec, plan, tasks)
            : new ConstitutionComplianceReport { HasConstitution = false };

        // Score each category
        var specScore  = ScoreSpecification(spec);
        var planScore  = ScorePlan(plan);
        var taskScore  = ScoreTask(tasks);
        var traceScore = ScoreTraceability(traceReport, spec is not null, plan is not null, tasks is not null);
        var compScore  = ScoreCompliance(complianceReport, constitution);

        // Overall (weighted, skips un-assessed categories)
        double overall = ComputeOverall(specScore, planScore, taskScore, traceScore, compScore);

        // Gates
        var gates = BuildGates(specScore, planScore, taskScore, traceScore, compScore,
                               complianceReport, constitution is not null);

        // Gaps and recommendations
        var gaps = BuildGaps(specScore, planScore, taskScore, traceScore, compScore,
                             traceReport, complianceReport);
        var recs = BuildRecommendations(gaps, spec, plan, tasks, complianceReport);

        gaps.Sort((a, b) => a.Severity.CompareTo(b.Severity));
        recs.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        return new QAReadinessReport
        {
            OverallScore   = overall,
            OverallStatus  = ToStatus(overall),
            Scores         = [specScore, planScore, taskScore, traceScore, compScore],
            Gaps           = gaps,
            Recommendations = recs,
            Gates          = gates,
            Health         = new ReadinessHealth
            {
                SpecificationScore = specScore.Score,
                PlanScore          = planScore.Score,
                TaskScore          = taskScore.Score,
                TraceabilityScore  = traceScore.Score,
                ComplianceScore    = compScore.Score,
                OverallScore       = overall,
                OverallStatus      = ToStatus(overall),
            },
            HasConstitution  = constitution is not null,
            HasSpecification = spec         is not null,
            HasPlan          = plan         is not null,
            HasTasks         = tasks        is not null,
        };
    }

    public IEnumerable<ReadinessGap> FilterGapsBySeverity(
        IEnumerable<ReadinessGap> gaps, ViolationSeverity? severity) =>
        severity is null ? gaps : gaps.Where(g => g.Severity == severity);

    public IEnumerable<ReadinessRecommendation> FilterRecommendationsByArtifact(
        IEnumerable<ReadinessRecommendation> recs, ArtifactType? artifact) =>
        artifact is null ? recs : recs.Where(r => r.TargetArtifact == artifact);

    public IEnumerable<ReadinessRecommendation> FilterRecommendationsByPriority(
        IEnumerable<ReadinessRecommendation> recs, ViolationSeverity? priority) =>
        priority is null ? recs : recs.Where(r => r.Priority == priority);

    // ── Category scorers ──────────────────────────────────────────────────────

    private static ReadinessScore ScoreSpecification(SpecTree? spec)
    {
        const string cat = "Specification Quality";
        if (spec is null)
            return NotAssessed(cat);

        var h       = spec.Health;
        var signals = new List<string>();
        var weak    = new List<string>();
        double score = 0;

        // Requirements (35 pts)
        if (h.Requirements > 0)
        { score += 35; signals.Add($"{h.Requirements} requirement(s) defined"); }
        else
            weak.Add("No requirements found — add FR/NFR/REQ items");

        // Acceptance criteria: tests + BDD + success criteria (25 pts)
        int acCount = h.Tests + h.BddScenarios + h.SuccessCriteria;
        if (acCount > 0)
        { score += 25; signals.Add($"{acCount} acceptance criterion/criteria, tests, or BDD scenarios"); }
        else
            weak.Add("No acceptance criteria, tests, or success criteria defined");

        // User stories (10 pts)
        if (h.UserStories > 0)
        { score += 10; signals.Add($"{h.UserStories} user story/stories"); }

        // Clarity — inverse of open clarifications (20 pts)
        if (h.Clarifications == 0)
        { score += 20; signals.Add("No open clarifications"); }
        else if (h.Clarifications <= 5)
        { score += 10; weak.Add($"{h.Clarifications} open clarification(s) need resolution"); }
        else
            weak.Add($"{h.Clarifications} open clarification(s) — high ambiguity, reduce before proceeding");

        // Completeness bonus: assumptions + edge cases (10 pts)
        if (h.Assumptions > 0) { score += 5; signals.Add("Assumptions documented"); }
        if (h.EdgeCases   > 0) { score += 5; signals.Add("Edge cases documented"); }

        return Scored(cat, Math.Min(100, score), signals, weak);
    }

    private static ReadinessScore ScorePlan(PlanDocument? plan)
    {
        const string cat = "Plan Quality";
        if (plan is null)
            return NotAssessed(cat);

        var h       = plan.Health;
        var signals = new List<string>();
        var weak    = new List<string>();
        double score = 0;

        // Implementation phases (25 pts)
        if (h.HasImplementationPhases)
        { score += 25; signals.Add($"{h.TotalPhases} implementation phase(s)"); }
        else
            weak.Add("No implementation phases — add phased delivery plan");

        // Architecture decisions (20 pts)
        if (h.HasArchitecture)
        { score += 20; signals.Add($"{h.TotalArchitectureDecisions} architecture decision(s)"); }
        else
            weak.Add("No architecture decisions documented");

        // Constitution checks (15 pts)
        if (h.HasConstitutionCheck)
        {
            score += 15;
            signals.Add($"{h.TotalConstitutionCheckItems + h.TotalConstitutionGates} constitution check(s)");
            if (h.NonCompliantItems + h.FailedGates > 0)
                weak.Add($"{h.NonCompliantItems + h.FailedGates} non-compliant constitution check(s)");
        }
        else
            weak.Add("No constitution compliance checks found in plan");

        // Risks (15 pts)
        if (h.TotalRisks > 0)
        { score += 15; signals.Add($"{h.TotalRisks} risk(s) documented"); }
        else
            weak.Add("No risks documented");

        // Summary (10 pts)
        if (h.HasSummary)
        { score += 10; signals.Add("Plan summary present"); }
        else
            weak.Add("No plan summary");

        // Dependencies (10 pts)
        if (h.TotalDependencies > 0)
        { score += 10; signals.Add($"{h.TotalDependencies} dependency/ies documented"); }

        // Testing strategy (5 pts)
        if (h.HasTestingInfo)
        { score += 5; signals.Add("Testing strategy documented"); }

        // Penalty: non-compliant / failed gates (−5 each, capped at −20)
        int failures = h.NonCompliantItems + h.FailedGates;
        double penalty = Math.Min(score, Math.Min(20.0, failures * 5.0));
        score -= penalty;

        return Scored(cat, Math.Min(100, Math.Max(0, score)), signals, weak);
    }

    private static ReadinessScore ScoreTask(TaskTree? tasks)
    {
        const string cat = "Task Readiness";
        if (tasks is null)
            return NotAssessed(cat);

        var h       = tasks.Health;
        var signals = new List<string>();
        var weak    = new List<string>();
        double score = 0;

        // Has tasks at all (30 pts)
        if (h.TotalTasks == 0)
        {
            weak.Add("No tasks defined — add implementation and testing tasks");
            return Scored(cat, 0, signals, weak);
        }
        score += 30;
        signals.Add($"{h.TotalTasks} task(s) defined");

        // FR link coverage: up to 40 pts
        double frRatio = h.TotalTasks > 0 ? (double)h.FrLinkedTasks / h.TotalTasks : 0;
        score += Math.Round(frRatio * 40.0, 1);
        if (frRatio >= 0.8)
            signals.Add($"{h.FrLinkedTasks}/{h.TotalTasks} tasks linked to requirements");
        else if (frRatio > 0)
            weak.Add($"Only {h.FrLinkedTasks}/{h.TotalTasks} tasks linked to requirements ({frRatio:P0})");
        else
            weak.Add("No tasks linked to requirements — add FR references to task descriptions");

        // Testing tasks (20 pts)
        if (h.TestingTasks > 0)
        { score += 20; signals.Add($"{h.TestingTasks} testing task(s)"); }
        else
            weak.Add("No testing tasks — add test implementation tasks");

        // Orphan penalty: up to 10 pts
        double orphanPts = h.UnlinkedTasks == 0
            ? 10.0
            : Math.Max(0.0, 10.0 - h.UnlinkedTasks * 2.0);
        score += orphanPts;
        if (h.UnlinkedTasks > 0)
            weak.Add($"{h.UnlinkedTasks} orphan/unlinked task(s) with no requirement reference");
        else
            signals.Add("No orphan tasks");

        return Scored(cat, Math.Min(100, Math.Max(0, score)), signals, weak);
    }

    private static ReadinessScore ScoreTraceability(
        ArtifactTraceabilityReport trace,
        bool hasSpec, bool hasPlan, bool hasTasks)
    {
        const string cat = "Traceability";

        // Need at least two artifacts loaded to measure any chain
        if (!hasSpec && !hasPlan && !hasTasks)
            return NotAssessed(cat);

        bool hasConstToSpec = trace.ConstitutionCoverage.TotalItems > 0;
        bool hasSpecToPlan  = trace.SpecificationCoverage.TotalItems > 0;
        bool hasPlanToTask  = trace.PlanCoverage.TotalItems > 0;
        int chainCount      = (hasConstToSpec ? 1 : 0) + (hasSpecToPlan ? 1 : 0) + (hasPlanToTask ? 1 : 0);

        var signals = new List<string>();
        var weak    = new List<string>();

        if (chainCount == 0)
        {
            weak.Add("Need at least two artifacts loaded to measure traceability chains");
            return Scored(cat, 0, signals, weak);
        }

        // Average coverage across available chains (0–100 average)
        double chainSum = 0;
        if (hasConstToSpec)
        {
            double pct = trace.ConstitutionCoverage.CoveragePercentage;
            chainSum += pct;
            if (pct >= 80) signals.Add($"Constitution→Spec: {pct:0.#}% covered");
            else           weak.Add($"Constitution→Spec coverage low ({pct:0.#}%)");
        }
        if (hasSpecToPlan)
        {
            double pct = trace.SpecificationCoverage.CoveragePercentage;
            chainSum += pct;
            if (pct >= 80) signals.Add($"Spec→Plan: {pct:0.#}% covered");
            else           weak.Add($"Spec→Plan coverage low ({pct:0.#}%)");
        }
        if (hasPlanToTask)
        {
            double pct = trace.PlanCoverage.CoveragePercentage;
            chainSum += pct;
            if (pct >= 80) signals.Add($"Plan→Task: {pct:0.#}% covered");
            else           weak.Add($"Plan→Task coverage low ({pct:0.#}%)");
        }

        double chainAvg = chainSum / chainCount;

        // 75% from chain coverage average, 25% from gap-free status
        double gapCount = trace.Gaps.Count;
        double gapScore = Math.Max(0.0, 25.0 - gapCount * 2.5);
        double score    = chainAvg * 0.75 + gapScore;

        if (gapCount == 0)
            signals.Add("No traceability gaps");
        else if (gapCount <= 3)
            weak.Add($"{gapCount} traceability gap(s)");
        else
            weak.Add($"{gapCount} traceability gaps — significant coverage missing");

        return Scored(cat, Math.Min(100, Math.Max(0, score)), signals, weak);
    }

    private static ReadinessScore ScoreCompliance(
        ConstitutionComplianceReport compliance,
        ConstitutionDocument? constitution)
    {
        const string cat = "Compliance";
        if (constitution is null)
            return NotAssessed(cat);

        var signals = new List<string>();
        var weak    = new List<string>();

        if (compliance.Results.Count == 0)
        {
            weak.Add("No constitution rules found to assess");
            return Scored(cat, 0, signals, weak);
        }

        // 70% from compliance coverage percentage
        double covScore = compliance.Coverage.CompliancePercentage * 0.70;

        // 30% from violation-free status
        int critical = compliance.Violations.Count(v => v.Severity == ViolationSeverity.Critical);
        int high     = compliance.Violations.Count(v => v.Severity == ViolationSeverity.High);
        double violScore = Math.Max(0.0, 30.0 - critical * 15.0 - high * 5.0);

        double score = covScore + violScore;

        if (compliance.Violations.Count == 0)
            signals.Add("No constitution violations");
        else
        {
            if (critical > 0) weak.Add($"{critical} critical constitution violation(s)");
            if (high     > 0) weak.Add($"{high} high-severity violation(s)");
        }

        int compliant = compliance.Results.Count(r => r.Status == ComplianceStatus.Compliant);
        if (compliant > 0)
            signals.Add($"{compliant}/{compliance.Results.Count} rules fully compliant");

        int missing = compliance.Results.Count(r => r.Status == ComplianceStatus.Missing);
        if (missing > 0)
            weak.Add($"{missing} governance rule(s) with no artifact coverage");

        return Scored(cat, Math.Min(100, Math.Max(0, score)), signals, weak);
    }

    // ── Overall score ─────────────────────────────────────────────────────────

    private static double ComputeOverall(
        ReadinessScore spec, ReadinessScore plan, ReadinessScore task,
        ReadinessScore trace, ReadinessScore comp)
    {
        // Fixed weights — only assessed categories contribute
        var weights = new (ReadinessScore s, double w)[]
        {
            (spec,  0.25),
            (plan,  0.25),
            (task,  0.20),
            (trace, 0.15),
            (comp,  0.15),
        };

        double sum = 0, totalW = 0;
        foreach (var (s, w) in weights)
        {
            if (!s.IsAssessed) continue;
            sum    += s.Score * w;
            totalW += w;
        }

        return totalW == 0 ? 0 : Math.Round(sum / totalW, 1);
    }

    // ── Gates ────────────────────────────────────────────────────────────────

    private static List<ReadinessGate> BuildGates(
        ReadinessScore spec, ReadinessScore plan, ReadinessScore task,
        ReadinessScore trace, ReadinessScore comp,
        ConstitutionComplianceReport compliance, bool hasConstitution)
    {
        // "Ready for Implementation" — need solid spec and plan
        bool implReady  = spec.IsAssessed && plan.IsAssessed
                       && spec.Score >= 65 && plan.Score >= 60;
        string? implBlock = !spec.IsAssessed  ? "Specification not loaded" :
                            !plan.IsAssessed  ? "Plan not loaded" :
                            spec.Score < 65   ? $"Specification score too low ({spec.Score:0.#}/100, need 65)" :
                            plan.Score < 60   ? $"Plan score too low ({plan.Score:0.#}/100, need 60)" : null;

        // "Ready for Testing" — need solid tasks and traceability
        bool testReady  = task.IsAssessed
                       && task.Score >= 60
                       && (!trace.IsAssessed || trace.Score >= 50);
        string? testBlock = !task.IsAssessed ? "Tasks not loaded" :
                            task.Score < 60  ? $"Task score too low ({task.Score:0.#}/100, need 60)" :
                            trace.IsAssessed && trace.Score < 50
                                             ? $"Traceability score too low ({trace.Score:0.#}/100, need 50)" : null;

        // "Ready for Release" — need high overall + compliance (if loaded)
        double overall = ComputeOverall(spec, plan, task, trace, comp);
        bool compOk    = !hasConstitution || (comp.IsAssessed && comp.Score >= 70);
        bool hasCritical = hasConstitution && compliance.Violations.Any(v => v.Severity == ViolationSeverity.Critical);
        bool relReady  = overall >= 75 && compOk && !hasCritical;
        string? relBlock = overall < 75   ? $"Overall score too low ({overall:0.#}/100, need 75)" :
                           hasCritical    ? "Critical constitution violations must be resolved" :
                           !compOk        ? $"Compliance score too low ({comp.Score:0.#}/100, need 70)" : null;

        return
        [
            new ReadinessGate
            {
                Name        = "Ready for Implementation",
                Question    = "Is the feature ready to begin implementation?",
                IsReady     = implReady,
                Status      = implReady ? ReadinessStatus.Ready : ReadinessStatus.NeedsWork,
                BlockReason = implBlock,
            },
            new ReadinessGate
            {
                Name        = "Ready for Testing",
                Question    = "Is the feature ready for testing?",
                IsReady     = testReady,
                Status      = testReady ? ReadinessStatus.Ready : ReadinessStatus.NeedsWork,
                BlockReason = testBlock,
            },
            new ReadinessGate
            {
                Name        = "Ready for Release",
                Question    = "Is the feature ready for release?",
                IsReady     = relReady,
                Status      = relReady ? ReadinessStatus.Ready
                            : overall < 50 ? ReadinessStatus.NotReady : ReadinessStatus.NeedsWork,
                BlockReason = relBlock,
            },
        ];
    }

    // ── Gaps ─────────────────────────────────────────────────────────────────

    private static List<ReadinessGap> BuildGaps(
        ReadinessScore spec, ReadinessScore plan, ReadinessScore task,
        ReadinessScore trace, ReadinessScore comp,
        ArtifactTraceabilityReport traceReport,
        ConstitutionComplianceReport complianceReport)
    {
        var gaps = new List<ReadinessGap>();

        AddCategoryGaps(gaps, spec,  "Specification Quality");
        AddCategoryGaps(gaps, plan,  "Plan Quality");
        AddCategoryGaps(gaps, task,  "Task Readiness");
        AddCategoryGaps(gaps, trace, "Traceability");
        AddCategoryGaps(gaps, comp,  "Compliance");

        // Traceability-specific gaps from AT report
        foreach (var g in traceReport.Gaps.Take(5))
        {
            gaps.Add(new ReadinessGap
            {
                Category    = "Traceability",
                Description = $"{g.GapIn} gap: {g.ItemTitle} — {g.Description}",
                Severity    = GapSev(g.Severity),
            });
        }

        // Compliance violations as gaps
        foreach (var v in complianceReport.Violations.Take(5))
        {
            gaps.Add(new ReadinessGap
            {
                Category    = "Compliance",
                Description = $"{v.RuleId} ({v.RuleTitle}): {v.Issue}",
                Severity    = v.Severity,
            });
        }

        return gaps;
    }

    private static void AddCategoryGaps(List<ReadinessGap> gaps, ReadinessScore score, string category)
    {
        if (!score.IsAssessed) return;
        var sev = score.Score >= 65 ? ViolationSeverity.Low :
                  score.Score >= 40 ? ViolationSeverity.Medium : ViolationSeverity.High;
        foreach (var w in score.Weaknesses)
            gaps.Add(new ReadinessGap { Category = category, Description = w, Severity = sev });
    }

    // ── Recommendations ───────────────────────────────────────────────────────

    private static List<ReadinessRecommendation> BuildRecommendations(
        List<ReadinessGap> gaps,
        SpecTree?   spec,
        PlanDocument? plan,
        TaskTree?   tasks,
        ConstitutionComplianceReport compliance)
    {
        var recs = new List<ReadinessRecommendation>();

        // Per-gap recommendations
        foreach (var gap in gaps)
        {
            var target = gap.Category switch
            {
                "Specification Quality" => ArtifactType.Specification,
                "Plan Quality"          => ArtifactType.Plan,
                "Task Readiness"        => ArtifactType.Task,
                "Compliance"            => ArtifactType.Constitution,
                _                       => ArtifactType.Specification,
            };

            recs.Add(new ReadinessRecommendation
            {
                Category       = gap.Category,
                Text           = gap.Description,
                Priority       = gap.Severity,
                TargetArtifact = target,
            });
        }

        // Compliance-specific recommendations (from the compliance report)
        foreach (var rec in compliance.Recommendations.Take(5))
        {
            recs.Add(new ReadinessRecommendation
            {
                Category       = "Compliance",
                Text           = rec.Text,
                Priority       = rec.Priority,
                TargetArtifact = rec.TargetArtifact,
            });
        }

        // De-duplicate by text
        return recs
            .GroupBy(r => r.Text, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ReadinessScore NotAssessed(string category) =>
        new ReadinessScore
        {
            Category   = category,
            IsAssessed = false,
            Score      = 0,
            Status     = ReadinessStatus.NotReady,
        };

    private static ReadinessScore Scored(
        string category, double score,
        List<string> signals, List<string> weaknesses) =>
        new ReadinessScore
        {
            Category   = category,
            IsAssessed = true,
            Score      = Math.Round(score, 1),
            Status     = ToStatus(score),
            Signals    = signals,
            Weaknesses = weaknesses,
        };

    private static ReadinessStatus ToStatus(double score) =>
        score >= 85 ? ReadinessStatus.Ready :
        score >= 65 ? ReadinessStatus.MostlyReady :
        score >= 40 ? ReadinessStatus.NeedsWork :
                      ReadinessStatus.NotReady;

    private static ViolationSeverity GapSev(GapSeverity g) => g switch
    {
        GapSeverity.High   => ViolationSeverity.High,
        GapSeverity.Medium => ViolationSeverity.Medium,
        _                  => ViolationSeverity.Low,
    };
}
