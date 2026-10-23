using BirkNext.AiCodeReview;

namespace BirkNext.Web.Services;

/// <summary>What one rule's outcome means for the reader. Each is its own answer; a gap or an unsupported analyzer is never a finding.</summary>
public enum CodeRiskResultKind
{
    /// <summary>The rule executed and identified a risk/condition.</summary>
    Finding,
    /// <summary>The rule executed and observed nothing.</summary>
    ExecutedClean,
    /// <summary>Evidence the rule needs is missing and the user can supply it (Specification, Tasks, test execution, baseline …).</summary>
    EvidenceGap,
    /// <summary>A prerequisite is absent that the user cannot supply here; not a pass or a failure.</summary>
    NotAssessed,
    /// <summary>The analyzer cannot evaluate this rule, source or language.</summary>
    Unsupported,
    /// <summary>The rule does not apply to this review mode.</summary>
    NotApplicable,
}

public enum CodeRiskCoverageStatus { Ready, Partial, Missing, NotSelected, Unsupported, PotentiallyStale, NotRecorded }

public sealed record CodeRiskSummary(
    int Findings, int High, int Medium, int Low, int Info,
    int Executed, int EvidenceGaps, int NotAssessed, int Unsupported, int NotApplicable,
    string CurrentState, string ChangeRisk, string Headline, bool BaselineSelected);

/// <summary>Findings of one rule: the grouped headline ("Empty catch block · 9") over the individual findings, which stay intact.</summary>
public sealed record CodeRiskRuleGroup(string RuleId, string DisplayId, string Title, AiCodeReviewCategory Category, AiFindingSeverity MaxSeverity, IReadOnlyList<AiCodeFinding> Findings)
{
    public string Anchor => $"crr-group-{DisplayId.ToLowerInvariant()}";
}

public sealed record CodeRiskCategoryGroup(AiCodeReviewCategory Category, string Label, IReadOnlyList<CodeRiskRuleGroup> Rules)
{
    public int Findings => Rules.Sum(r => r.Findings.Count);
}

/// <summary>A deterministic recommended action. <see cref="Href"/> is an in-page anchor (finding group, baseline selector) or a page route.</summary>
public sealed record CodeRiskAction(string Key, string Text, string Href, bool IsSetup);

/// <param name="Action">What to do next when the evidence is not ready (null when nothing is needed).</param>
public sealed record CodeRiskCoverageRow(string Key, string Evidence, CodeRiskCoverageStatus Status, string Detail, string Impact, string? Provenance = null, string? Action = null, string? ActionHref = null);

/// <summary>
/// The decision-support view of one Code Risk Review run, derived only from the stored result (no model, no score, no new evidence). The rule
/// table stays the expert detail; this decides the order a reader sees things in. Every ordering is deterministic: severity, security
/// relevance, changed-code relevance, count, category priority, rule id.
/// </summary>
public static class CodeRiskTriage
{
    public const string BaselineAnchor = "aic-baseline";
    public const string CoverageAnchor = "crr-coverage";

    /// <summary>Display order of categories: what tends to matter first.</summary>
    private static readonly AiCodeReviewCategory[] CategoryPriority =
    [
        AiCodeReviewCategory.Security, AiCodeReviewCategory.ValidationErrorHandling, AiCodeReviewCategory.UnresolvedReferences, AiCodeReviewCategory.Dependencies,
        AiCodeReviewCategory.Tests, AiCodeReviewCategory.RequirementsAlignment, AiCodeReviewCategory.ArchitectureConstitution, AiCodeReviewCategory.Placeholders,
        AiCodeReviewCategory.DuplicateLogic, AiCodeReviewCategory.DeadCode, AiCodeReviewCategory.ContractDrift, AiCodeReviewCategory.ConfigurationDrift,
        AiCodeReviewCategory.ChangeRisk, AiCodeReviewCategory.GeneratedDocumentation,
    ];

    public static int Priority(AiCodeReviewCategory category) => Array.IndexOf(CategoryPriority, category);

    /// <summary>The readiness item a NotAssessed rule waits for, when the user can supply it (an evidence gap).</summary>
    public static string? EvidenceKey(AiRuleExecution rule) => rule.RuleId switch
    {
        "AIC-REQ-002" => "tasks",
        "AIC-REQ-001" or "AIC-REQ-003" or "AIC-REQ-004" or "AIC-TEST-007" => "specification",
        "AIC-ARCH-002" => "constitution",
        "AIC-TEST-005" => "execution",
        "AIC-REF-004" => "execution",
        _ when rule.Reason?.Contains("code-risk observations", StringComparison.OrdinalIgnoreCase) == true => "code-risk",
        _ => null,
    };

    public static CodeRiskResultKind Kind(AiRuleExecution rule, AiCodeReviewResult result) => rule.State switch
    {
        AiRuleExecutionState.Executed => rule.Findings > 0 ? CodeRiskResultKind.Finding : CodeRiskResultKind.ExecutedClean,
        AiRuleExecutionState.Unsupported => CodeRiskResultKind.Unsupported,
        AiRuleExecutionState.NotApplicable => CodeRiskResultKind.NotApplicable,
        // Failed means the rule could not complete: shown as not assessed, never as a pass and never as a finding.
        _ => EvidenceKey(rule) is { } key && result.Readiness.FirstOrDefault(r => r.Key == key) is { Available: false } ? CodeRiskResultKind.EvidenceGap : CodeRiskResultKind.NotAssessed,
    };

    public static string Label(CodeRiskResultKind kind) => kind switch
    {
        CodeRiskResultKind.Finding => "Finding",
        CodeRiskResultKind.ExecutedClean => "Executed · no indicators",
        CodeRiskResultKind.EvidenceGap => "Evidence gap",
        CodeRiskResultKind.NotAssessed => "Not assessed",
        CodeRiskResultKind.Unsupported => "Unsupported",
        _ => "Not applicable",
    };

    public static string Help(CodeRiskResultKind kind) => kind switch
    {
        CodeRiskResultKind.Finding => "The rule executed and identified a risk to review; not automatically a confirmed defect.",
        CodeRiskResultKind.ExecutedClean => "The rule executed and observed nothing. This does not mean the code is correct.",
        CodeRiskResultKind.EvidenceGap => "Evidence the rule needs is missing; supply it to evaluate the rule.",
        CodeRiskResultKind.NotAssessed => "Required evidence was unavailable; this is not a pass or failure.",
        CodeRiskResultKind.Unsupported => "The current analyzer does not support this evidence or language.",
        _ => "The rule does not apply to this review mode.",
    };

    public static string Tone(CodeRiskResultKind kind) => kind switch
    {
        CodeRiskResultKind.Finding => "attention",
        CodeRiskResultKind.ExecutedClean => "clear",
        CodeRiskResultKind.EvidenceGap => "gap",
        _ => "neutral",
    };

    /// <summary>Rules that need a baseline (change-risk); the rest make up the current-state review.</summary>
    public static bool IsChangeRule(AiRuleExecution rule, AiCodeReviewResult result) =>
        rule.Category is AiCodeReviewCategory.ContractDrift or AiCodeReviewCategory.ConfigurationDrift or AiCodeReviewCategory.ChangeRisk
        || rule.RuleId is "AIC-REQ-004" or "AIC-ARCH-001" or "AIC-DEP-001" or "AIC-DEP-002" or "AIC-SEC-001" or "AIC-TEST-003" or "AIC-TEST-004" or "AIC-DOC-001"
        || result.Baseline is null && rule.State == AiRuleExecutionState.NotApplicable && rule.Reason?.Contains("baseline", StringComparison.OrdinalIgnoreCase) == true;

    public static CodeRiskSummary Summary(AiCodeReviewResult r)
    {
        var kinds = r.Rules.Select(rule => (Rule: rule, Kind: Kind(rule, r))).ToList();
        var current = kinds.Where(k => !IsChangeRule(k.Rule, r)).ToList();
        var currentComplete = current.All(k => k.Kind is CodeRiskResultKind.Finding or CodeRiskResultKind.ExecutedClean or CodeRiskResultKind.NotApplicable)
            && (r.Languages ?? []).All(l => l.Supported);
        var changeRules = kinds.Where(k => IsChangeRule(k.Rule, r)).ToList();
        var changeRisk = r.Baseline is null ? "Limited — no baseline snapshot selected"
            : changeRules.All(k => k.Kind is CodeRiskResultKind.Finding or CodeRiskResultKind.ExecutedClean or CodeRiskResultKind.NotApplicable) ? "Complete" : "Partial";
        return new(
            r.Findings.Count,
            r.Findings.Count(f => f.Severity == AiFindingSeverity.High), r.Findings.Count(f => f.Severity == AiFindingSeverity.Medium),
            r.Findings.Count(f => f.Severity == AiFindingSeverity.Low), r.Findings.Count(f => f.Severity == AiFindingSeverity.Info),
            r.Rules.Count(x => x.State == AiRuleExecutionState.Executed),
            kinds.Count(k => k.Kind == CodeRiskResultKind.EvidenceGap), kinds.Count(k => k.Kind == CodeRiskResultKind.NotAssessed),
            kinds.Count(k => k.Kind == CodeRiskResultKind.Unsupported), kinds.Count(k => k.Kind == CodeRiskResultKind.NotApplicable),
            currentComplete ? "Complete" : "Partial", changeRisk,
            r.Baseline is null
                ? "Current-state review completed. Change-risk checks requiring a baseline were not assessed."
                : "Current-state and change-risk review completed.",
            r.Baseline is not null);
    }

    /// <summary>Findings grouped by category, then rule; groups ordered by severity, security relevance, count and rule id.</summary>
    public static List<CodeRiskCategoryGroup> Groups(IEnumerable<AiCodeFinding> findings) =>
        findings.GroupBy(f => f.Category)
            .Select(c => new CodeRiskCategoryGroup(c.Key, AiCodeReviewText.Label(c.Key), c.GroupBy(f => f.RuleId)
                .Select(g => new CodeRiskRuleGroup(g.Key, AiCodeReviewText.DisplayId(g.Key), Title(g.Key, g), c.Key, g.Max(f => f.Severity),
                    g.OrderByDescending(f => f.Severity).ThenBy(f => f.Locations.FirstOrDefault()?.File, StringComparer.Ordinal).ThenBy(f => f.Locations.FirstOrDefault()?.Line).ToList()))
                .OrderByDescending(g => g.MaxSeverity).ThenByDescending(g => g.Findings.Count).ThenBy(g => g.RuleId, StringComparer.Ordinal).ToList()))
            .OrderByDescending(c => c.Rules.Max(r => r.MaxSeverity)).ThenBy(c => Priority(c.Category)).ToList();

    /// <summary>The rule's catalogue title (never a single finding's title, which can be instance-specific).</summary>
    private static string Title(string ruleId, IEnumerable<AiCodeFinding> findings) => RuleTitles.TryGetValue(ruleId, out var title) ? title : findings.First().Title;

    private static readonly Dictionary<string, string> RuleTitles = new(StringComparer.Ordinal)
    {
        ["AIC-SEC-003"] = "Permissive CORS policy", ["AIC-SEC-004"] = "Developer exception page without environment condition",
        ["AIC-SEC-001"] = "Authorization protection removed", ["AIC-SEC-002"] = "Endpoint without authorization metadata among protected siblings",
        ["AIC-VAL-001"] = "Empty catch block", ["AIC-VAL-002"] = "Broad catch returns success", ["AIC-VAL-003"] = "Exception details returned to client",
        ["AIC-VAL-004"] = "Request model without validation among validated siblings", ["AIC-REF-001"] = "Unresolved project namespace import",
        ["AIC-REF-002"] = "Configuration key read but not declared", ["AIC-DUP-001"] = "Potential duplicate type", ["AIC-DUP-002"] = "Potential duplicate implementation",
        ["AIC-DEP-001"] = "Introduced or changed dependency", ["AIC-DEP-002"] = "Introduced dependency without usage evidence",
        ["AIC-TEST-001"] = "Potentially weak test", ["AIC-TEST-002"] = "Skipped tests", ["AIC-TEST-003"] = "Potential test coverage gap for changed code",
        ["AIC-PLACEHOLDER-001"] = "NotImplementedException in production code", ["AIC-PLACEHOLDER-002"] = "Stub implementation marked TODO",
        ["AIC-PLACEHOLDER-003"] = "Placeholder value returned", ["AIC-PLACEHOLDER-004"] = "TODO / FIXME comment", ["AIC-DEAD-001"] = "Unused private method",
        ["AIC-DOC-002"] = "Generated documentation drift candidate", ["AIC-DOC-001"] = "Generated documentation not refreshed",
    };

    private static string Plural(int n, string one, string many) => n == 1 ? one : many;

    /// <summary>"Permissive CORS policy" → "permissive CORS policy": only the first letter, so acronyms survive.</summary>
    private static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>
    /// Deterministic recommended actions: one per finding group (High and Medium always; Low/Info only when repeated ≥ 5 times), then the
    /// setup actions that unlock rules (baseline, executed tests, Specification/Constitution/Tasks, Dependency Review, re-analysis), then
    /// unsupported analyzers. Never one action per finding.
    /// </summary>
    public static List<CodeRiskAction> Actions(AiCodeReviewResult r)
    {
        var actions = new List<CodeRiskAction>();
        var groups = Groups(r.Findings).SelectMany(c => c.Rules)
            .Where(g => g.MaxSeverity >= AiFindingSeverity.Medium || g.Findings.Count >= 5)
            .OrderByDescending(g => g.MaxSeverity)
            .ThenBy(g => g.Category == AiCodeReviewCategory.Security ? 0 : 1)
            .ThenByDescending(g => g.Findings.Any(f => f.Change is AiChangeKind.Introduced or AiChangeKind.Changed))
            .ThenByDescending(g => g.Findings.Count)
            .ThenBy(g => Priority(g.Category)).ThenBy(g => g.RuleId, StringComparer.Ordinal);
        foreach (var g in groups)
            actions.Add(new($"group:{g.RuleId}", $"Review {g.Findings.Count} {LowerFirst(g.Title)} {Plural(g.Findings.Count, "finding", "findings")}", $"#{g.Anchor}", false));

        bool Missing(string key) => r.Readiness.FirstOrDefault(i => i.Key == key) is { Available: false };
        if (r.Baseline is null) actions.Add(new("baseline", "Select a baseline snapshot to enable change and drift rules", $"#{BaselineAnchor}", true));
        if (Missing("code-risk")) actions.Add(new("code-risk", "Analyze the source again in Source Analysis to enable source-level rules", "source-analysis", true));
        if (Missing("execution")) actions.Add(new("execution", "Attach executed test evidence (TRX) — test results are not verified", "traceability", true));
        var documents = new[] { ("specification", "Specification"), ("constitution", "Constitution"), ("tasks", "Tasks") }.Where(d => Missing(d.Item1)).Select(d => d.Item2).ToList();
        if (documents.Count > 0)
            actions.Add(new("documents", $"Load {string.Join(" / ", documents)} for alignment checks", documents[0] switch
            {
                "Specification" => "specification-explorer", "Constitution" => "constitution-explorer", _ => "task-explorer",
            }, true));
        if (r.Readiness.FirstOrDefault(i => i.Key == "dependency-review") is { } dependency && (!dependency.Available || dependency.Stale))
            actions.Add(new("dependency-review", dependency.Stale ? "Re-run Dependency Review for this snapshot (latest run reviewed another snapshot)" : "Run Dependency Review for this snapshot", "dependency-review", true));
        foreach (var language in (r.Languages ?? []).Where(l => !l.Supported && l.Files > 0))
            actions.Add(new($"language:{language.Language}", $"{language.Files} {language.Language} {Plural(language.Files, "file is", "files are")} not analyzed (unsupported analyzer)", $"#{CoverageAnchor}", true));
        return actions;
    }

    /// <summary>"Analyzed source files: 415 / 430" from the analyzer's own per-language counts, or null when the run did not record them.</summary>
    public static (int Analyzed, int Total)? AnalyzedFiles(AiCodeReviewResult r) =>
        r.Languages is { Count: > 0 } languages ? (languages.Where(l => l.Supported).Sum(l => l.Files), languages.Sum(l => l.Files)) : null;

    /// <summary>Review coverage: what evidence the run had, what each missing piece blocks, and what to do. Coverage is never a code defect.</summary>
    public static List<CodeRiskCoverageRow> Coverage(AiCodeReviewResult r)
    {
        AiReadinessItem? Item(string key) => r.Readiness.FirstOrDefault(i => i.Key == key);
        CodeRiskCoverageRow Row(string key, string evidence, string missingAction, string actionHref, string impact)
        {
            if (Item(key) is not { } item) return new(key, evidence, CodeRiskCoverageStatus.NotRecorded, "Not recorded for this run", impact);
            var status = item.Stale ? CodeRiskCoverageStatus.PotentiallyStale : item.Available ? CodeRiskCoverageStatus.Ready : key == "baseline" ? CodeRiskCoverageStatus.NotSelected : CodeRiskCoverageStatus.Missing;
            return new(key, evidence, status, item.Detail, item.Blocks ?? impact, item.Provenance, status == CodeRiskCoverageStatus.Ready ? null : missingAction, status == CodeRiskCoverageStatus.Ready ? null : actionHref);
        }
        var unsupported = (r.Languages ?? []).Where(l => !l.Supported && l.Files > 0).ToList();
        var analyzed = AnalyzedFiles(r);
        var rows = new List<CodeRiskCoverageRow>
        {
            new("source", "Source code", unsupported.Count > 0 ? CodeRiskCoverageStatus.Partial : CodeRiskCoverageStatus.Ready,
                analyzed is { } a ? $"Analyzed source files: {a.Analyzed} / {a.Total}" : Item("source")?.Detail ?? "", "Core source rules",
                $"{r.Current.ArchiveName} · {Short(r.Current.Fingerprint)}"),
            Row("code-risk", "Code-risk observations", "Analyze the source again", "source-analysis", "Source-level rules"),
            Row("baseline", "Baseline snapshot", "Select baseline snapshot", $"#{BaselineAnchor}", "Change and drift rules"),
            Row("specification", "Specification", "Open Specification Explorer", "specification-explorer", "Requirements alignment"),
            Row("constitution", "Constitution", "Open Constitution Explorer", "constitution-explorer", "Architecture / constitution checks"),
            Row("tasks", "Tasks", "Open Task Explorer", "task-explorer", "Completed-task checks"),
            Row("execution", "Executed test evidence", "Attach executed test evidence", "traceability", "Execution and test-result rules"),
            Row("dependency-review", "Dependency Review", "Open Dependency Review", "dependency-review", "Dependency health and update-policy context"),
            Row("dependencies", "Dependency declarations", "Analyze the source again", "source-analysis", "Dependency rules"),
            Row("contracts", "Contracts", "Open Source Analysis", "source-analysis", "Contract drift"),
        };
        // Unsupported analyzers: source exists, the analyzer does not read it — Unsupported, never Missing, never counted as analyzed.
        rows.AddRange(unsupported.Select(l => new CodeRiskCoverageRow($"language:{l.Language}", $"{l.Language} analyzer", CodeRiskCoverageStatus.Unsupported,
            $"{l.Files} {Plural(l.Files, "file", "files")} not analyzed", l.Reason ?? "Source-level rules do not cover this language")));
        return rows;
    }

    public static string Label(CodeRiskCoverageStatus status) => status switch
    {
        CodeRiskCoverageStatus.Ready => "Ready",
        CodeRiskCoverageStatus.Partial => "Partial source coverage",
        CodeRiskCoverageStatus.Missing => "Missing",
        CodeRiskCoverageStatus.NotSelected => "Not selected",
        CodeRiskCoverageStatus.Unsupported => "Unsupported",
        CodeRiskCoverageStatus.PotentiallyStale => "Potentially stale",
        _ => "Not recorded",
    };

    /// <summary>Searches rule id (CRR- or AIC-), rule title, file path and category label; case-insensitive.</summary>
    public static bool Matches(AiCodeFinding f, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        return AiCodeReviewText.DisplayId(f.RuleId).Contains(q, StringComparison.OrdinalIgnoreCase) || f.RuleId.Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || AiCodeReviewText.Label(f.Category).Contains(q, StringComparison.OrdinalIgnoreCase)
            || f.Locations.Any(l => l.File.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Rule table filters. "Findings and evidence gaps" is the default after a run.</summary>
    public enum RuleFilter { FindingsAndGaps, Findings, ExecutedClean, EvidenceGaps, NotAssessed, Unsupported, NotApplicable, All }

    public static string Label(RuleFilter filter) => filter switch
    {
        RuleFilter.FindingsAndGaps => "Findings and evidence gaps",
        RuleFilter.Findings => "Findings",
        RuleFilter.ExecutedClean => "Executed clean",
        RuleFilter.EvidenceGaps => "Evidence gaps",
        RuleFilter.NotAssessed => "Not assessed",
        RuleFilter.Unsupported => "Unsupported",
        RuleFilter.NotApplicable => "Not applicable",
        _ => "All rules",
    };

    public static bool Matches(CodeRiskResultKind kind, RuleFilter filter) => filter switch
    {
        RuleFilter.FindingsAndGaps => kind is CodeRiskResultKind.Finding or CodeRiskResultKind.EvidenceGap,
        RuleFilter.Findings => kind == CodeRiskResultKind.Finding,
        RuleFilter.ExecutedClean => kind == CodeRiskResultKind.ExecutedClean,
        RuleFilter.EvidenceGaps => kind == CodeRiskResultKind.EvidenceGap,
        RuleFilter.NotAssessed => kind == CodeRiskResultKind.NotAssessed,
        RuleFilter.Unsupported => kind == CodeRiskResultKind.Unsupported,
        RuleFilter.NotApplicable => kind == CodeRiskResultKind.NotApplicable,
        _ => true,
    };

    /// <summary>The proposed baseline: the most recent other snapshot of the same repository analyzed before the current one. A proposal only — never applied.</summary>
    public static BirkNext.SourceEvidence.ReviewSourceSnapshot? SuggestedBaseline(IEnumerable<BirkNext.SourceEvidence.ReviewSourceSnapshot> snapshots, BirkNext.SourceEvidence.ReviewSourceSnapshot current) =>
        snapshots.Where(s => s.RepositoryKey == current.RepositoryKey && s.SnapshotId != current.SnapshotId && s.AnalyzedAt < current.AnalyzedAt)
            .OrderByDescending(s => s.AnalyzedAt).FirstOrDefault();

    public static string Short(string fingerprint) => fingerprint.Length > 8 ? fingerprint[..8] + "…" : fingerprint;
}
