using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class TaskDeltaService
{
    private sealed record ParsedTask(
        string TaskId,
        string Title,
        string FullText,
        IReadOnlyList<string> BodyLines,
        IReadOnlyList<string> SpecRefs,
        string MatchKey);

    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

    private static readonly Regex TaskLineRe = new(
        @"^T(\d{2,4})\s*[-–.:)]\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex CheckboxRe = new(
        @"^\s*[-*]\s+\[[ xX]\]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex SpecIdRe = new(
        @"\b(FR|NFR|US|SC|AC|UC|TS|REQ)-?(\d{1,4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Keywords in added lines that signal a task has grown in scope.
    private static readonly string[] ScopeExpansionKeywords =
    [
        "security", "kode 6", "kode 7", "kode6", "kode7",
        "authorization", "auth", "permission", "access control",
        "audit", "logging", "event", "integration", "export",
        "report", "search", "profile", "filter", "pagination",
        "validation", "compliance", "encryption", "masking",
        "notification", "retry", "caching", "performance",
        "classification", "sensitivity", "gdpr",
    ];

    // Area detection — intentionally a subset of AreaDefs in TaskSpecAlignmentService,
    // kept independent so the two services can evolve separately.
    private static readonly (AffectedArea Area, string[] Keywords)[] AreaDefs =
    [
        (AffectedArea.Security, ["security", "kode6", "kode7", "kode 6", "kode 7",
            "classification", "sensitive", "gradert", "securityclassification"]),
        (AffectedArea.Authorization, ["permission", "access control", "role", "grant",
            "tilgang", "policy", "authorize", "forbidden", "unauthorized", "authorization"]),
        (AffectedArea.Search, ["search", "søk", "filter", "pagination", "lookup",
            "findperson", "personsearch"]),
        (AffectedArea.Profile, ["profile", "profil", "persondata", "national id",
            "fnr", "dnr", "fodselsnummer", "fødselsnummer", "mask"]),
        (AffectedArea.AccessManagement, ["access management", "grant access", "expiry",
            "self-assign", "tildeling", "tilgangstyring"]),
        (AffectedArea.ReferenceData, ["reference data", "koderegister", "koder",
            "kjoenntype", "kjønntype", "barntype", "barnstatus", "kodetype", "kodeverk"]),
        (AffectedArea.Ingestion, ["ingest", "upsert", "duf", "freg", "idempotent",
            "importbatch", "syncperson", "ingestion"]),
        (AffectedArea.DomainEvents, ["domain event", "publish event", "hendelse",
            "event bus", "eventbus", "event consumer", "event handler"]),
        (AffectedArea.Audit, ["audit", "auditlog", "audit trail", "immutable", "sporbarhet"]),
        (AffectedArea.OperationRegistration, ["operation registration", "register operation",
            "seven operations", "operationregist"]),
        (AffectedArea.HealthMonitoring, ["health check", "healthcheck", "metric", "monitor",
            "dashboard", "availability", "readiness", "liveness"]),
        (AffectedArea.Infrastructure, ["infrastructure", "scaffold", "di registration",
            "middleware", "program.cs", "appsettings", "solution", "nuget"]),
        (AffectedArea.BusinessRules, ["business rule", "statusregel", "statustransition",
            "state transition", "eligibility", "derivation", "rule engine"]),
        (AffectedArea.Workflow, ["workflow", "arbeidsflyt", "statemachine", "state machine",
            "workflowservice", "processflow"]),
        (AffectedArea.Validation, ["validation", "validator", "valider", "validering",
            "fluentvalidation", "input validation"]),
        (AffectedArea.Testing, ["testclass", "test class", "testfixture", "unittest",
            "integrationtest", "testbuilder", "testfactory"]),
        (AffectedArea.ExceptionHandling, ["exception", "exceptionhandler", "notfoundexception",
            "errorhandler", "problem details", "globalexception"]),
    ];

    private static readonly string[] TechnicalOnlyTerms =
    [
        "nuget", "npm", "appsettings.json", "appsettings", "di registration",
        "create solution", "create project", "build setup", "project setup",
        "scaffold new", "serilog setup", "logging infrastructure",
        "ef migration", "database migration", "test infrastructure",
        "bump version", "upgrade sdk", "update packages", "readme",
        "styling ", " styling", ".css ", "connection string", "connectionstring",
        "github actions", "ci/cd", "dockerfile", "refactor ", "rename ",
    ];

    private static readonly string[] BehavioralTerms =
    [
        "endpoint", "api route", "graphql query", "graphql mutation",
        "publish event", "event consumer", "event handler",
        "authorization policy", "permission check", "business rule",
        "user flow", "user can", "database entity", "schema migration",
        "ui screen", "ui page", "ui form", "webhook",
        "register operation", "audit log", "kode 6", "kode 7",
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    public static TaskDeltaReport Analyse(string oldText, string newText)
    {
        var oldTasks = ParseTasksWithBlocks(oldText);
        var newTasks = ParseTasksWithBlocks(newText);

        var oldByKey = oldTasks.ToDictionary(t => t.MatchKey, StringComparer.OrdinalIgnoreCase);
        var newByKey = newTasks.ToDictionary(t => t.MatchKey, StringComparer.OrdinalIgnoreCase);

        var findings = new List<TaskDeltaFinding>();

        foreach (var task in newTasks.Where(t => !oldByKey.ContainsKey(t.MatchKey)))
            findings.Add(BuildAddedFinding(task));

        foreach (var task in oldTasks.Where(t => !newByKey.ContainsKey(t.MatchKey)))
            findings.Add(BuildRemovedFinding(task));

        foreach (var newTask in newTasks.Where(t => oldByKey.ContainsKey(t.MatchKey)))
        {
            var oldTask = oldByKey[newTask.MatchKey];
            if (!string.Equals(oldTask.FullText, newTask.FullText, OIC))
                findings.Add(BuildModifiedFinding(oldTask, newTask));
        }

        var ordered = findings
            .OrderBy(f => f.DeltaType switch
            {
                DeltaType.Added => 0,
                DeltaType.Modified => 1,
                DeltaType.Removed => 2,
                _ => 3,
            })
            .ThenBy(f => f.TaskId)
            .ToList();

        return new TaskDeltaReport
        {
            TotalChanges = ordered.Count,
            AddedTasks = ordered.Count(f => f.DeltaType == DeltaType.Added),
            RemovedTasks = ordered.Count(f => f.DeltaType == DeltaType.Removed),
            ModifiedTasks = ordered.Count(f => f.DeltaType == DeltaType.Modified),
            ScopeExpansions = ordered.Count(f => f.ScopeChange == ScopeChangeKind.Expansion),
            ScopeReductions = ordered.Count(f => f.ScopeChange == ScopeChangeKind.Reduction),
            Findings = ordered,
        };
    }

    // ── Task parsing ──────────────────────────────────────────────────────────

    private static List<ParsedTask> ParseTasksWithBlocks(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd()).ToArray();
        var tasks = new List<ParsedTask>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var headers = new List<(int Idx, string Id, string Title, bool IsCheckbox)>();

        for (var i = 0; i < lines.Length; i++)
        {
            var m = TaskLineRe.Match(lines[i]);
            if (m.Success)
            {
                var id = $"T{m.Groups[1].Value.PadLeft(3, '0')}";
                headers.Add((i, id, m.Groups[2].Value.Trim(), false));
                continue;
            }
            var cm = CheckboxRe.Match(lines[i]);
            if (cm.Success)
                headers.Add((i, $"T{(headers.Count + 1):D3}", cm.Groups[1].Value.Trim(), true));
        }

        for (var h = 0; h < headers.Count; h++)
        {
            var (headerIdx, id, title, isCheckbox) = headers[h];
            var matchKey = isCheckbox ? NormalizeTitle(title) : id;
            if (!seen.Add(matchKey)) continue;

            var nextHeaderIdx = h + 1 < headers.Count ? headers[h + 1].Idx : lines.Length;
            var block = new List<string> { lines[headerIdx] };
            for (var i = headerIdx + 1; i < nextHeaderIdx; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) break;
                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t' || line[0] == '-' || line[0] == '*'))
                    block.Add(line);
                else
                    break;
            }

            var fullText = string.Join("\n", block);
            var bodyLines = block.Skip(1).ToList();
            var specRefs = ExtractSpecRefs(fullText);

            tasks.Add(new ParsedTask(id, title, fullText, bodyLines, specRefs, matchKey));
        }

        return tasks;
    }

    private static string NormalizeTitle(string title) =>
        Regex.Replace(title.ToLowerInvariant().Trim(), @"\s+", " ");

    private static IReadOnlyList<string> ExtractSpecRefs(string text)
    {
        var refs = new List<string>();
        foreach (Match m in SpecIdRe.Matches(text))
        {
            var norm = $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value.PadLeft(2, '0')}";
            if (!refs.Any(r => r.Equals(norm, OIC))) refs.Add(norm);
        }
        return refs;
    }

    // ── Finding builders ──────────────────────────────────────────────────────

    private static TaskDeltaFinding BuildAddedFinding(ParsedTask task)
    {
        var lower = task.FullText.ToLowerInvariant();
        var areas = DetectAreas(lower);
        var risk = ComputeRisk(areas, task.SpecRefs.Count > 0);
        var coverage = EstimateSpecCoverage(task, lower);
        var tests = BuildTests(areas);
        var isReg = IsRegressionCandidate(risk, areas);

        return new TaskDeltaFinding
        {
            TaskId = task.TaskId,
            Title = task.Title,
            DeltaType = DeltaType.Added,
            ScopeChange = ScopeChangeKind.None,
            BeforeText = string.Empty,
            AfterText = task.FullText,
            DeltaSummary = "New task — not present in previous version.",
            RecommendedAction = coverage switch
            {
                DeltaSpecCoverage.PossibleDeviation =>
                    "Review with PO or Tech Lead — no specification item found for this behavior.",
                DeltaSpecCoverage.NeedsReview =>
                    "Verify this new task is covered by an existing requirement, or add a user story.",
                _ =>
                    "New infrastructure or technical task — verify it does not introduce undocumented behavior.",
            },
            AffectedAreas = areas,
            RiskLevel = risk,
            SpecCoverage = coverage,
            IsRegressionCandidate = isReg,
            RecommendedTests = tests,
            RiskReason = BuildRiskReason(risk, areas, false),
        };
    }

    private static TaskDeltaFinding BuildRemovedFinding(ParsedTask task)
    {
        var lower = task.FullText.ToLowerInvariant();
        var areas = DetectAreas(lower);
        var risk = ComputeRisk(areas, task.SpecRefs.Count > 0);
        var coverage = EstimateSpecCoverage(task, lower);
        var isReg = IsRegressionCandidate(risk, areas);

        return new TaskDeltaFinding
        {
            TaskId = task.TaskId,
            Title = task.Title,
            DeltaType = DeltaType.Removed,
            ScopeChange = ScopeChangeKind.None,
            BeforeText = task.FullText,
            AfterText = string.Empty,
            DeltaSummary = "Task dropped — was present in the previous version.",
            RecommendedAction = coverage == DeltaSpecCoverage.Linked
                ? "Verify the linked requirement is still fully implemented by another task."
                : "Confirm this task was completed or intentionally removed from scope.",
            AffectedAreas = areas,
            RiskLevel = risk,
            SpecCoverage = coverage,
            IsRegressionCandidate = isReg,
            RecommendedTests = [],
            RiskReason = BuildRiskReason(risk, areas, false),
        };
    }

    private static TaskDeltaFinding BuildModifiedFinding(ParsedTask oldTask, ParsedTask newTask)
    {
        var lower = newTask.FullText.ToLowerInvariant();
        var areas = DetectAreas(lower);
        var risk = ComputeRisk(areas, newTask.SpecRefs.Count > 0);
        var coverage = EstimateSpecCoverage(newTask, lower);
        var scopeChange = DetectScopeChange(oldTask, newTask);
        var tests = BuildTests(areas);
        var isReg = IsRegressionCandidate(risk, areas) || scopeChange == ScopeChangeKind.Expansion;

        var deltaSummary = scopeChange switch
        {
            ScopeChangeKind.Expansion => "Scope expanded — new responsibilities added to this task.",
            ScopeChangeKind.Reduction => "Scope reduced — responsibilities removed or simplified.",
            _ => "Task text updated — verify changes do not affect covered behavior.",
        };

        var action = (scopeChange, coverage) switch
        {
            (ScopeChangeKind.Expansion, DeltaSpecCoverage.PossibleDeviation) =>
                "Scope expanded with no spec coverage — review the new scope with PO before implementing.",
            (ScopeChangeKind.Expansion, _) =>
                "Scope expanded — run regression tests for affected areas and update spec coverage.",
            (ScopeChangeKind.Reduction, _) =>
                "Scope reduced — confirm no requirement is left partially implemented.",
            (_, DeltaSpecCoverage.PossibleDeviation) =>
                "Task changed without spec coverage — verify the new behavior is documented.",
            _ =>
                "Review changes with the team and confirm spec coverage is up-to-date.",
        };

        return new TaskDeltaFinding
        {
            TaskId = newTask.TaskId,
            Title = newTask.Title,
            DeltaType = DeltaType.Modified,
            ScopeChange = scopeChange,
            BeforeText = oldTask.FullText,
            AfterText = newTask.FullText,
            DeltaSummary = deltaSummary,
            RecommendedAction = action,
            AffectedAreas = areas,
            RiskLevel = risk,
            SpecCoverage = coverage,
            IsRegressionCandidate = isReg,
            RecommendedTests = tests,
            RiskReason = BuildRiskReason(risk, areas, scopeChange == ScopeChangeKind.Expansion),
        };
    }

    // ── Scope change detection ────────────────────────────────────────────────

    private static ScopeChangeKind DetectScopeChange(ParsedTask oldTask, ParsedTask newTask)
    {
        var oldLines = oldTask.FullText.Split('\n')
            .Select(l => l.Trim().ToLowerInvariant())
            .Where(l => l.Length > 0).ToHashSet();
        var newLines = newTask.FullText.Split('\n')
            .Select(l => l.Trim().ToLowerInvariant())
            .Where(l => l.Length > 0).ToHashSet();

        var addedLines = newLines.Except(oldLines).ToList();
        var removedLines = oldLines.Except(newLines).ToList();

        // Added lines contain scope-expanding keywords → expansion
        if (addedLines.Count > 0
            && addedLines.Any(l => ScopeExpansionKeywords.Any(kw => l.Contains(kw))))
            return ScopeChangeKind.Expansion;

        // Title grew significantly → expansion
        if (newTask.Title.Length > oldTask.Title.Length + 8)
            return ScopeChangeKind.Expansion;

        // More body lines without keywords → still expansion
        if (newTask.BodyLines.Count > oldTask.BodyLines.Count + 1)
            return ScopeChangeKind.Expansion;

        // Clearly fewer lines → reduction
        if (removedLines.Count > addedLines.Count + 1
            || oldTask.BodyLines.Count > newTask.BodyLines.Count + 1)
            return ScopeChangeKind.Reduction;

        return ScopeChangeKind.None;
    }

    // ── Area detection ────────────────────────────────────────────────────────

    private static List<AffectedArea> DetectAreas(string lowerText)
    {
        var areas = new List<AffectedArea>();
        foreach (var (area, keywords) in AreaDefs)
            if (keywords.Any(kw => lowerText.Contains(kw)))
                areas.Add(area);
        return areas;
    }

    // ── Spec coverage estimation (heuristic — no spec file) ───────────────────

    private static DeltaSpecCoverage EstimateSpecCoverage(ParsedTask task, string lower)
    {
        if (task.SpecRefs.Count > 0) return DeltaSpecCoverage.Linked;

        var isTechnical = TechnicalOnlyTerms.Any(t => lower.Contains(t));
        var hasBehavioral = BehavioralTerms.Any(t => lower.Contains(t));

        if (isTechnical && !hasBehavioral) return DeltaSpecCoverage.NotApplicable;
        if (hasBehavioral) return DeltaSpecCoverage.PossibleDeviation;
        return DeltaSpecCoverage.NeedsReview;
    }

    // ── Risk computation ──────────────────────────────────────────────────────

    private static ImpactLevel ComputeRisk(List<AffectedArea> areas, bool hasSpecRefs)
    {
        if (areas.Count == 0) return ImpactLevel.Unknown;
        var maxImpact = areas.Select(AreaImpact).Min();
        // Downgrade when spec refs exist — already documented
        if (hasSpecRefs && maxImpact == ImpactLevel.High) return ImpactLevel.Medium;
        return maxImpact;
    }

    private static ImpactLevel AreaImpact(AffectedArea a) => a switch
    {
        AffectedArea.Security
            or AffectedArea.Authorization
            or AffectedArea.DomainEvents
            or AffectedArea.Audit
            or AffectedArea.OperationRegistration => ImpactLevel.High,
        AffectedArea.Search
            or AffectedArea.Profile
            or AffectedArea.AccessManagement
            or AffectedArea.ReferenceData
            or AffectedArea.Ingestion
            or AffectedArea.HealthMonitoring
            or AffectedArea.BusinessRules
            or AffectedArea.Workflow => ImpactLevel.Medium,
        _ => ImpactLevel.Low,
    };

    private static bool IsRegressionCandidate(ImpactLevel risk, List<AffectedArea> areas) =>
        risk == ImpactLevel.High
        || areas.Any(a => a is AffectedArea.Security or AffectedArea.Authorization
                              or AffectedArea.Audit or AffectedArea.DomainEvents);

    private static List<string> BuildTests(List<AffectedArea> areas)
    {
        var byArea = new Dictionary<AffectedArea, string[]>
        {
            [AffectedArea.Security] = ["Kode 6/7 visibility regression", "Unauthorized access negative test"],
            [AffectedArea.Authorization] = ["Grant access validation", "Expiry enforcement", "Audit trail for grants"],
            [AffectedArea.Search] = ["Search by name", "Filter combination coverage", "Pagination correctness"],
            [AffectedArea.Profile] = ["Profile field visibility per classification", "National ID masking"],
            [AffectedArea.AccessManagement] = ["Grant access flow end-to-end", "Self-assignment rejection"],
            [AffectedArea.ReferenceData] = ["Active reference values returned", "Deactivated values excluded"],
            [AffectedArea.Ingestion] = ["Idempotent upsert — duplicate is safe", "Invalid records handled gracefully"],
            [AffectedArea.DomainEvents] = ["Event published with correct payload", "No PII in event payload"],
            [AffectedArea.Audit] = ["Audit event on state change", "Immutable audit trail test"],
            [AffectedArea.BusinessRules] = ["Business rule expected output", "Invalid transition rejected"],
            [AffectedArea.Workflow] = ["State transition sequence correct", "Invalid transitions rejected"],
            [AffectedArea.Validation] = ["Valid input accepted", "Invalid input rejected with correct message"],
            [AffectedArea.ExceptionHandling] = ["Exception thrown in error scenario", "No sensitive data in errors"],
        };
        return areas.Where(a => byArea.ContainsKey(a))
                    .SelectMany(a => byArea[a])
                    .Distinct().Take(6).ToList();
    }

    private static string BuildRiskReason(ImpactLevel risk, List<AffectedArea> areas, bool scopeExpanded)
    {
        if (risk == ImpactLevel.Low || risk == ImpactLevel.Unknown) return string.Empty;

        var parts = new List<string>();
        if (scopeExpanded) parts.Add("Scope has expanded to cover new concerns.");

        foreach (var area in areas.Take(3))
        {
            var exp = area switch
            {
                AffectedArea.Security => "Implements or affects Kode 6/7 visibility rules.",
                AffectedArea.Authorization => "Affects permission evaluation or access control.",
                AffectedArea.Audit => "Affects audit trail guarantees.",
                AffectedArea.DomainEvents => "Affects domain event contracts or publication.",
                AffectedArea.Profile => "Handles personal identity data or national ID masking.",
                AffectedArea.Search => "Affects what appears in search results.",
                AffectedArea.AccessManagement => "Affects access grant flows or expiry rules.",
                AffectedArea.BusinessRules => "Implements or changes business rules.",
                AffectedArea.Workflow => "Affects workflow state transitions.",
                _ => string.Empty,
            };
            if (!string.IsNullOrEmpty(exp)) parts.Add(exp);
        }

        if (parts.Count == 0) parts.Add("Medium or higher risk area — verify before release.");
        return string.Join(" ", parts);
    }
}
