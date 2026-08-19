using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class DeliveryReadinessService : IDeliveryReadinessAssessmentService
{
    private readonly IArtifactTraceabilityService _traceability;
    private readonly IConstitutionComplianceService _compliance;
    private readonly IQAReadinessService _readiness;
    private readonly IQaAuditorService _auditor;

    public DeliveryReadinessService(
        IArtifactTraceabilityService traceability,
        IConstitutionComplianceService compliance,
        IQAReadinessService readiness,
        IQaAuditorService auditor)
    {
        _traceability = traceability;
        _compliance   = compliance;
        _readiness    = readiness;
        _auditor      = auditor;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public DeliveryReadinessReport Assess(
        ConstitutionDocument? constitution,
        SpecTree? spec,
        PlanDocument? plan,
        TaskTree? tasks)
    {
        var reviewContext = ReviewContextFactory.Create(
            ConstitutionAnalysisService.BuildSemanticModel(constitution ?? new()),
            SpecExplorerService.BuildSemanticModel(spec ?? new(), ""),
            PlanAnalysisService.BuildSemanticModel(plan ?? new()),
            TaskExplorerService.BuildSemanticModel(tasks ?? new()),
            new DataModelSemanticModel());

        var traceReport    = _traceability.Analyze(constitution, spec, plan, tasks, reviewContext);
        var compReport     = _compliance.Analyze(constitution, spec, plan, tasks, reviewContext);
        var readinessReport = _readiness.Assess(constitution, spec, plan, tasks, reviewContext);
        var auditReport     = _auditor.Audit(constitution, spec, plan, tasks, reviewContext);

        var devGate  = EvaluateDevelopmentGate(compReport, readinessReport, auditReport);
        var testGate = EvaluateTestingGate(spec, tasks, traceReport, readinessReport, auditReport);
        var relGate  = EvaluateReleaseGate(devGate, testGate, compReport, readinessReport, auditReport);

        var allBlockers = devGate.Blockers
            .Concat(testGate.Blockers)
            .Concat(relGate.Blockers)
            .GroupBy(b => GetBlockerLogicalIdentity(b))
            .Select(g => g.OrderBy(b => (int)b.Severity).First())
            .OrderBy(b => (int)b.Severity)
            .ToList();

        var recs = BuildRecommendations(devGate, testGate, relGate, allBlockers);

        var health = new DeliveryReadinessHealth
        {
            DevelopmentScore      = devGate.Score,
            TestingScore          = testGate.Score,
            ReleaseScore          = relGate.Score,
            OverallReadinessScore = Math.Round(
                (devGate.Score + testGate.Score + relGate.Score) / 3.0, 1),
        };

        return new DeliveryReadinessReport
        {
            DevelopmentGate    = devGate,
            TestingGate        = testGate,
            ReleaseGate        = relGate,
            DevelopmentDecision = MakeDecision("Development", devGate),
            TestingDecision     = MakeDecision("Testing",     testGate),
            ReleaseDecision     = MakeDecision("Release",     relGate),
            Blockers            = allBlockers,
            Recommendations     = recs,
            Health              = health,
            HasConstitution  = constitution is not null,
            HasSpecification = spec         is not null,
            HasPlan          = plan         is not null,
            HasTasks         = tasks        is not null,
        };
    }

    public IEnumerable<ReadinessBlocker> FilterBlockersBySeverity(
        IEnumerable<ReadinessBlocker> blockers,
        GateSeverity? severity) =>
        severity is null
            ? blockers
            : blockers.Where(b => b.Severity == severity);

    public IEnumerable<ReadinessBlocker> FilterBlockersByPhase(
        IEnumerable<ReadinessBlocker> blockers,
        string? phase) =>
        string.IsNullOrWhiteSpace(phase)
            ? blockers
            : blockers.Where(b => b.Phase is null ||
                string.Equals(b.Phase, phase, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<DeliveryRecommendation> FilterRecommendationsByPhase(
        IEnumerable<DeliveryRecommendation> recs,
        string? phase) =>
        string.IsNullOrWhiteSpace(phase)
            ? recs
            : recs.Where(r => r.Phase is null ||
                string.Equals(r.Phase, phase, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<DeliveryRecommendation> SearchRecommendations(
        IEnumerable<DeliveryRecommendation> recs,
        string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return recs;
        var q = query.Trim();
        return recs.Where(r =>
            r.Text.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            r.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (r.Phase?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    // ── Gate evaluators ────────────────────────────────────────────────────────

    private static DeliveryGate EvaluateDevelopmentGate(
        ConstitutionComplianceReport compReport,
        QAReadinessReport readiness,
        QaAuditReport audit)
    {
        var passed   = new List<string>();
        var failed   = new List<string>();
        var blockers = new List<ReadinessBlocker>();

        // Check 1: Specification quality
        double specScore = GetReadinessScore(readiness, "Specification Quality");
        bool specAssessed = readiness.HasSpecification;
        if (!specAssessed)
        {
            failed.Add("Specification not loaded");
            blockers.Add(B("No specification loaded",
                "A specification is required for development readiness.",
                GateSeverity.High, "Specification", "Development"));
        }
        else if (specScore >= 60)
            passed.Add($"Specification quality: {specScore:0.#}/100");
        else
        {
            failed.Add($"Specification quality too low ({specScore:0.#}/100 — need ≥60)");
            blockers.Add(B("Specification quality insufficient",
                $"Score {specScore:0.#}/100 is below the development threshold of 60.",
                GateSeverity.High, "Specification", "Development"));
        }

        // Check 2: Plan quality
        double planScore = GetReadinessScore(readiness, "Plan Quality");
        bool planAssessed = readiness.HasPlan;
        if (!planAssessed)
        {
            failed.Add("Implementation plan not loaded");
            blockers.Add(B("No implementation plan loaded",
                "An implementation plan is required for development readiness.",
                GateSeverity.High, "Plan", "Development"));
        }
        else if (planScore >= 60)
            passed.Add($"Plan quality: {planScore:0.#}/100");
        else
        {
            failed.Add($"Plan quality too low ({planScore:0.#}/100 — need ≥60)");
            blockers.Add(B("Plan quality insufficient",
                $"Score {planScore:0.#}/100 is below the development threshold of 60.",
                GateSeverity.High, "Plan", "Development"));
        }

        // Check 3: Constitution compliance
        bool hasConstitution = compReport.HasConstitution;
        double compPct = hasConstitution ? compReport.Coverage.CompliancePercentage : 100;
        if (!hasConstitution)
            passed.Add("Constitution not loaded — compliance check skipped");
        else if (compPct >= 70)
            passed.Add($"Constitution compliance: {compPct:0.#}%");
        else
        {
            failed.Add($"Constitution compliance too low ({compPct:0.#}% — need ≥70%)");
            blockers.Add(B("Insufficient constitution compliance",
                $"Compliance is {compPct:0.#}% — must reach 70% before development begins.",
                GateSeverity.High, "Compliance", "Development"));
        }

        // Check 4: Critical constitution violations
        int critViol = compReport.Violations.Count(v => v.Severity == ViolationSeverity.Critical);
        if (critViol > 0)
        {
            failed.Add($"{critViol} critical constitution violation(s)");
            foreach (var v in compReport.Violations.Where(v => v.Severity == ViolationSeverity.Critical))
                blockers.Add(B($"Critical violation: {v.RuleId}",
                    v.Issue, GateSeverity.Critical, "Compliance", "Development"));
        }
        else if (hasConstitution)
            passed.Add("No critical constitution violations");

        // Check 5: Critical QA findings in Specification or Plan categories
        var critSpecPlanFindings = audit.Findings
            .Where(f => f.Severity == QaSeverity.Critical &&
                        f.Category is QaCategory.Specification or QaCategory.Plan)
            .ToList();
        if (critSpecPlanFindings.Count > 0)
        {
            failed.Add($"{critSpecPlanFindings.Count} critical QA finding(s) in specification/plan");
            foreach (var f in critSpecPlanFindings)
                blockers.Add(B(f.Title, f.Description,
                    GateSeverity.Critical, f.Category.ToString(), "Development", f.RuleCode));
        }
        else
            passed.Add("No critical QA findings in specification or plan");

        double rawScore = specScore * 0.40 + planScore * 0.40 + compPct * 0.20;
        double penalty  = Math.Min(rawScore, critViol * 15.0 + critSpecPlanFindings.Count * 10.0);
        double score    = Math.Round(Math.Min(100, Math.Max(0, rawScore - penalty)), 1);

        return new DeliveryGate
        {
            Phase        = "Development",
            State        = DetermineState(score, blockers),
            Score        = score,
            PassedChecks = passed,
            FailedChecks = failed,
            Blockers     = blockers,
        };
    }

    private static DeliveryGate EvaluateTestingGate(
        SpecTree? spec,
        TaskTree? tasks,
        ArtifactTraceabilityReport traceReport,
        QAReadinessReport readiness,
        QaAuditReport audit)
    {
        var passed   = new List<string>();
        var failed   = new List<string>();
        var blockers = new List<ReadinessBlocker>();

        // Check 1: Specification has acceptance criteria
        bool specLoaded = readiness.HasSpecification;
        bool hasACFinding = audit.Findings.Any(f => f.RuleCode == "SPEC-001");
        if (!specLoaded)
        {
            failed.Add("Specification not loaded — AC coverage unknown");
        }
        else if (hasACFinding)
        {
            failed.Add("Acceptance criteria missing or insufficient");
            blockers.Add(B("Missing acceptance criteria",
                "Specification lacks acceptance criteria — tests cannot be derived.",
                GateSeverity.High, "Specification", "Testing", "SPEC-001"));
        }
        else
            passed.Add("Acceptance criteria present in specification");

        // Check 2: Traceability coverage
        double tracePct = traceReport.SpecificationCoverage.TotalItems > 0
            ? traceReport.SpecificationCoverage.CoveragePercentage
            : 0;
        bool traceAssessed = readiness.HasSpecification && readiness.HasPlan;
        if (!traceAssessed)
            passed.Add("Traceability check skipped — need Spec + Plan");
        else if (tracePct >= 60)
            passed.Add($"Spec→Plan traceability: {tracePct:0.#}%");
        else
        {
            failed.Add($"Spec→Plan traceability low ({tracePct:0.#}% — need ≥60%)");
            blockers.Add(B("Insufficient traceability",
                $"Only {tracePct:0.#}% of requirements are traced to plan items.",
                GateSeverity.High, "Traceability", "Testing"));
        }

        // Check 3: Testing tasks exist
        double taskScore = GetReadinessScore(readiness, "Task Readiness");
        bool hasTaskFinding = audit.Findings.Any(f => f.RuleCode == "TASK-002");
        bool tasksLoaded = readiness.HasTasks;
        if (!tasksLoaded)
        {
            failed.Add("Tasks not loaded — testing task coverage unknown");
            blockers.Add(B("No task file loaded",
                "Task file is needed to verify testing task coverage.",
                GateSeverity.Medium, "Task", "Testing"));
        }
        else if (hasTaskFinding)
        {
            failed.Add("No dedicated testing tasks found");
            blockers.Add(B("Missing testing tasks",
                "No testing tasks found — test execution cannot be planned.",
                GateSeverity.High, "Task", "Testing", "TASK-002"));
        }
        else
            passed.Add($"Testing tasks present (task score: {taskScore:0.#}/100)");

        // Check 4: No critical QA findings in Testing/Traceability categories
        var critTestFindings = audit.Findings
            .Where(f => f.Severity == QaSeverity.Critical &&
                        f.Category is QaCategory.Testing or QaCategory.Traceability)
            .ToList();
        if (critTestFindings.Count > 0)
        {
            failed.Add($"{critTestFindings.Count} critical QA finding(s) in testing/traceability");
            foreach (var f in critTestFindings)
                blockers.Add(B(f.Title, f.Description,
                    GateSeverity.Critical, f.Category.ToString(), "Testing", f.RuleCode));
        }
        else
            passed.Add("No critical QA findings in testing or traceability");

        // Score: task 40%, traceability 35%, spec AC proxy 25%
        double specScore = GetReadinessScore(readiness, "Specification Quality");
        double acProxy   = specLoaded ? specScore * 0.50 : 0;
        double traceContrib = traceAssessed ? tracePct : 50;  // neutral when not assessed
        double rawScore = taskScore * 0.40 + traceContrib * 0.35 + acProxy * 0.25;
        double penalty  = Math.Min(rawScore, critTestFindings.Count * 10.0);
        double score    = Math.Round(Math.Min(100, Math.Max(0, rawScore - penalty)), 1);

        return new DeliveryGate
        {
            Phase        = "Testing",
            State        = DetermineState(score, blockers),
            Score        = score,
            PassedChecks = passed,
            FailedChecks = failed,
            Blockers     = blockers,
        };
    }

    private static DeliveryGate EvaluateReleaseGate(
        DeliveryGate devGate,
        DeliveryGate testGate,
        ConstitutionComplianceReport compReport,
        QAReadinessReport readiness,
        QaAuditReport audit)
    {
        var passed   = new List<string>();
        var failed   = new List<string>();
        var blockers = new List<ReadinessBlocker>();

        // Check 1: Development gate passed
        bool devOk = devGate.State is ReadinessState.Ready or ReadinessState.MostlyReady;
        if (devOk)
            passed.Add($"Development gate: {devGate.State} ({devGate.Score:0.#}/100)");
        else
        {
            failed.Add($"Development gate not cleared ({devGate.State})");
            blockers.Add(B("Development gate not cleared",
                $"Development readiness must reach MostlyReady before release assessment. Current: {devGate.State}.",
                GateSeverity.Critical, "Development", "Release"));
        }

        // Check 2: Testing gate passed
        bool testOk = testGate.State is ReadinessState.Ready or ReadinessState.MostlyReady;
        if (testOk)
            passed.Add($"Testing gate: {testGate.State} ({testGate.Score:0.#}/100)");
        else
        {
            failed.Add($"Testing gate not cleared ({testGate.State})");
            blockers.Add(B("Testing gate not cleared",
                $"Testing readiness must reach MostlyReady before release assessment. Current: {testGate.State}.",
                GateSeverity.Critical, "Testing", "Release"));
        }

        // Check 3: Compliance coverage ≥ 80%
        bool hasConstitution = compReport.HasConstitution;
        double compPct = hasConstitution ? compReport.Coverage.CompliancePercentage : 100;
        if (!hasConstitution)
            passed.Add("Constitution not loaded — compliance check skipped");
        else if (compPct >= 80)
            passed.Add($"Compliance coverage: {compPct:0.#}%");
        else
        {
            failed.Add($"Compliance coverage too low ({compPct:0.#}% — need ≥80% for release)");
            blockers.Add(B("Insufficient compliance for release",
                $"Compliance must reach 80% before release. Current: {compPct:0.#}%.",
                GateSeverity.Critical, "Compliance", "Release"));
        }

        // Check 4: No constitution violations
        int critViol = compReport.Violations.Count(v => v.Severity == ViolationSeverity.Critical);
        int highViol = compReport.Violations.Count(v => v.Severity == ViolationSeverity.High);
        if (critViol > 0)
        {
            failed.Add($"{critViol} critical constitution violation(s)");
            foreach (var v in compReport.Violations.Where(v => v.Severity == ViolationSeverity.Critical))
                blockers.Add(B($"Critical violation: {v.RuleId}",
                    v.Issue, GateSeverity.Critical, "Compliance", "Release"));
        }
        else if (highViol > 0)
        {
            failed.Add($"{highViol} high-severity constitution violation(s)");
            foreach (var v in compReport.Violations.Where(v => v.Severity == ViolationSeverity.High))
                blockers.Add(B($"High violation: {v.RuleId}",
                    v.Issue, GateSeverity.High, "Compliance", "Release"));
        }
        else if (hasConstitution)
            passed.Add("No critical/high constitution violations");

        // Check 5: No critical QA audit findings
        int critAuditCount = audit.Health.CriticalCount;
        if (critAuditCount > 0)
        {
            failed.Add($"{critAuditCount} critical QA audit finding(s) unresolved");
            foreach (var f in audit.Findings.Where(f => f.Severity == QaSeverity.Critical))
                blockers.Add(B(f.Title, f.Description,
                    GateSeverity.Critical, f.Category.ToString(), "Release", f.RuleCode));
        }
        else
            passed.Add("No critical QA audit findings");

        // Check 6: Overall readiness score ≥ 75
        double overallScore = readiness.Health.OverallScore;
        if (overallScore >= 75)
            passed.Add($"Overall readiness score: {overallScore:0.#}/100");
        else if (overallScore > 0)
        {
            failed.Add($"Overall readiness score too low ({overallScore:0.#}/100 — need ≥75)");
            if (overallScore < 50)
                blockers.Add(B("Overall readiness too low for release",
                    $"Readiness score {overallScore:0.#}/100 must reach 75 before release.",
                    GateSeverity.High, "Quality", "Release"));
        }

        // Score: weighted combination of upstream gate scores + compliance
        double relScore = devGate.Score * 0.30 + testGate.Score * 0.30 + compPct * 0.25 + overallScore * 0.15;
        double penalty  = Math.Min(relScore,
            critViol * 20.0 + highViol * 10.0 + critAuditCount * 10.0);
        double score = Math.Round(Math.Min(100, Math.Max(0, relScore - penalty)), 1);

        return new DeliveryGate
        {
            Phase        = "Release",
            State        = DetermineState(score, blockers),
            Score        = score,
            PassedChecks = passed,
            FailedChecks = failed,
            Blockers     = blockers,
        };
    }

    // ── Recommendations builder ────────────────────────────────────────────────

    private static List<DeliveryRecommendation> BuildRecommendations(
        DeliveryGate devGate,
        DeliveryGate testGate,
        DeliveryGate relGate,
        List<ReadinessBlocker> blockers)
    {
        var recs = new List<DeliveryRecommendation>();

        foreach (var b in blockers.Where(b => b.Severity == GateSeverity.Critical))
            recs.Add(new DeliveryRecommendation
            {
                Text     = $"Resolve: {b.Title} — {b.Description}",
                Category = b.Category,
                Priority = GateSeverity.Critical,
                Phase    = b.Phase,
            });

        foreach (var c in devGate.FailedChecks)
            if (!blockers.Any(b => c.Contains(b.Category, StringComparison.OrdinalIgnoreCase)))
                recs.Add(new DeliveryRecommendation
                {
                    Text     = $"Fix development check: {c}",
                    Category = "Development",
                    Priority = GateSeverity.High,
                    Phase    = "Development",
                });

        foreach (var c in testGate.FailedChecks)
            if (!blockers.Any(b => c.Contains(b.Category, StringComparison.OrdinalIgnoreCase)))
                recs.Add(new DeliveryRecommendation
                {
                    Text     = $"Fix testing check: {c}",
                    Category = "Testing",
                    Priority = GateSeverity.High,
                    Phase    = "Testing",
                });

        foreach (var c in relGate.FailedChecks)
            if (!blockers.Any(b => c.Contains(b.Category, StringComparison.OrdinalIgnoreCase)))
                recs.Add(new DeliveryRecommendation
                {
                    Text     = $"Fix release check: {c}",
                    Category = "Release",
                    Priority = GateSeverity.Medium,
                    Phase    = "Release",
                });

        return recs
            .GroupBy(r => r.Text)
            .Select(g => g.First())
            .OrderBy(r => r.Priority)
            .ToList();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static double GetReadinessScore(QAReadinessReport readiness, string category) =>
        readiness.Scores.FirstOrDefault(s => s.Category == category)?.Score ?? 0;

    private static string GetBlockerLogicalIdentity(ReadinessBlocker blocker)
    {
        // Use RuleCode as primary identity if available (from QA findings, architectural checks)
        if (!string.IsNullOrWhiteSpace(blocker.RuleCode))
            return $"rule:{blocker.RuleCode.Trim()}";

        // Fallback for blockers without RuleCode: use composite logical identity
        // This prevents merging of distinct logical issues that happen to have the same Title
        // Example: two "Missing prerequisite" blockers with different descriptions/categories remain distinct
        // Constitution violations include RuleId in Title, so Title+Description+Category uniquely identifies the logical issue
        var titleNorm = (blocker.Title ?? string.Empty).Trim();
        var descNorm = (blocker.Description ?? string.Empty).Trim();
        var catNorm = (blocker.Category ?? string.Empty).Trim();
        return $"logical:{titleNorm}|{descNorm}|{catNorm}";
    }

    private static ReadinessBlocker B(
        string title, string description,
        GateSeverity severity, string category,
        string? phase, string? ruleCode = null) =>
        new ReadinessBlocker
        {
            Title       = title,
            Description = description,
            Severity    = severity,
            Category    = category,
            Phase       = phase,
            RuleCode    = ruleCode,
        };

    private static ReadinessState DetermineState(double score, List<ReadinessBlocker> blockers)
    {
        if (blockers.Any(b => b.Severity == GateSeverity.Critical))
            return ReadinessState.Blocked;
        if (score >= 80)
            return ReadinessState.Ready;
        if (score >= 60)
            return ReadinessState.MostlyReady;
        return ReadinessState.NotReady;
    }

    private static ReadinessDecision MakeDecision(string name, DeliveryGate gate) =>
        new ReadinessDecision
        {
            Name    = name,
            State   = gate.State,
            Score   = gate.Score,
            Summary = gate.State switch
            {
                ReadinessState.Ready =>
                    $"{name} readiness confirmed ({gate.Score:0.#}/100)",
                ReadinessState.MostlyReady =>
                    $"Mostly ready for {name.ToLowerInvariant()} — {gate.FailedChecks.Count} check(s) need attention",
                ReadinessState.NotReady =>
                    $"Not ready for {name.ToLowerInvariant()} — {gate.FailedChecks.Count} check(s) failed",
                ReadinessState.Blocked =>
                    $"Blocked from {name.ToLowerInvariant()} — {gate.Blockers.Count(b => b.Severity == GateSeverity.Critical)} critical issue(s)",
                _ => null,
            },
        };
}
