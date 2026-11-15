using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// The one rule that turns a project's saved test cases (Test Plan Suggestions project plans) into test-design evidence for Test Coverage and
/// Requirements Traceability. Pure: no I/O, no new store. A case counts for a requirement only when its own evidence (the specification file and
/// requirement id it was prepared from) names that requirement in the reviewed specification — never by title similarity, never for a guide
/// business rule (MT-7), and never for an excluded or superseded case. A tester's manual link (Test Plan Suggestions, validated against the project's
/// specification when it was made) counts the same way, but only while its requirement is still in the reviewed specification; a link whose
/// requirement left the specification is kept and shown as such, never counted. Automatic and manual links of one case stay separate entries (origin
/// visible) and the case counts once. A requirement is counted once however many cases cover it. A prepared or approved case is never an executed or
/// passed test.
/// </summary>
public static class PreparedCaseTraceability
{
    /// <param name="drafts">Stored plans; only this project's own plans (owner "project:{key}" and ProjectKey = key) are read.</param>
    /// <param name="specificationPaths">The reviewed specification file(s); empty = none given, so no link is counted.</param>
    /// <param name="requirementIds">The reviewed specification's requirement ids; null = the ids the specification evidence names are taken as is.</param>
    /// <param name="requirementTitles">Requirement id → text of the reviewed specification (titles only; never adds a requirement).</param>
    /// <param name="runs">The project's manual runs (Execute &amp; document). Only completed runs give an outcome; the latest completed run per case
    /// counts; a run of an earlier case revision is shown as such. Null = executions not read (every case "Not executed").</param>
    public static PreparedCaseTraceView Build(string projectKey, IEnumerable<TestPlanDraft> drafts, IReadOnlyCollection<string> specificationPaths,
        IReadOnlyCollection<string>? requirementIds, IReadOnlyDictionary<string, string>? requirementTitles = null, IReadOnlyCollection<ManualTestRun>? runs = null)
    {
        var runsByCase = (runs ?? []).Where(r => r.ProjectKey == projectKey).GroupBy(r => r.CaseKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, text) in requirementTitles ?? new Dictionary<string, string>())
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(text)) titles.TryAdd(id.Trim(), text.Trim());
        var owner = TestPlanSuggestionService.ProjectOwner(projectKey);
        var plans = drafts.Where(d => d.ProjectKey == projectKey && d.GuideKey == owner).OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToList();
        var paths = specificationPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = requirementIds?.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cases = plans.SelectMany(plan => plan.Cases.Select(c => WithRuns(Trace(plan, c, paths, ids, titles), c, runsByCase))).ToList();
        var requirements = cases.SelectMany(c => c.Requirements.Where(r => r.Counted).Select(r => (Link: r, Case: c)))
            .GroupBy(x => (Id: x.Link.RequirementId.ToUpperInvariant(), Path: NormalizePath(x.Link.SpecificationPath ?? "")))
            .Select(g =>
            {
                var approved = g.Where(x => x.Case.Stage == PreparedCaseStage.Approved).Select(x => x.Case.CaseKey).Distinct(StringComparer.Ordinal).Count();
                var keys = g.Select(x => x.Case.CaseKey).Distinct(StringComparer.Ordinal).ToList();
                var id = g.First().Link.RequirementId;
                var covering = g.Select(x => x.Case).DistinctBy(c => c.CaseKey).ToList();
                var executed = covering.Count(c => c.Executed);
                var passed = covering.Count(c => c.Passed);
                return new PreparedRequirementTrace
                {
                    RequirementId = id, SpecificationPath = g.First().Link.SpecificationPath, ApprovedCases = approved,
                    PreparedCases = keys.Count - approved, CaseKeys = keys,
                    State = approved > 0 ? "Approved test case" : "Prepared, not approved yet",
                    Title = titles.GetValueOrDefault(id) ?? g.Select(x => x.Link.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
                    AutomaticCases = g.Where(x => !x.Link.Manual).Select(x => x.Case.CaseKey).Distinct(StringComparer.Ordinal).Count(),
                    ManualCases = g.Where(x => x.Link.Manual).Select(x => x.Case.CaseKey).Distinct(StringComparer.Ordinal).Count(),
                    ExecutedCases = executed, PassedCases = passed, FailedCases = executed - passed,
                    Execution = executed == 0 ? PreparedCaseTraceView.NotExecuted
                        : $"Executed: {executed} case{(executed == 1 ? "" : "s")} — {passed} passed, {executed - passed} failed",
                };
            }).OrderBy(r => r.SpecificationPath, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.RequirementId, NaturalId.Instance).ToList();

        var covered = requirements.Select(r => r.RequirementId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<PreparedRequirementGap> gaps = ids is null || paths.Count == 0 ? [] : [.. ids.Where(i => !covered.Contains(i)).OrderBy(i => i, NaturalId.Instance)
            .Select(i => new PreparedRequirementGap(i, titles.GetValueOrDefault(i)))];
        var stale = cases.SelectMany(c => c.Requirements).Count(r => r.Manual && r.Validity == PreparedLinkValidity.RequirementNotFound);
        var limitations = new List<string>();
        if (paths.Count == 0)
            limitations.Add("No specification was given, so no requirement link is counted. Select the specification in the Specification Explorer.");
        if (cases.Any(c => c.Requirements.Any(r => r.Manual)))
            limitations.Add("Manual requirement links were checked against the requirement list read from the project's specification in the browser when they were made; the server does not hold the specification, so a link is a tester's association, not an independently verified one.");
        if (cases.Any(c => c.Requirements.Any(r => r.Basis == PreparedCaseLinkBasis.GuideBusinessRule)))
            limitations.Add("Guide business rules are shown, never counted as requirements; MT-7 reviewed coverage (Traceability & Coverage) owns guide scenarios.");
        if (cases.Any(c => c.Requirements.Any(r => r.Basis == PreparedCaseLinkBasis.JourneyReference)))
            limitations.Add("Requirement ids an Integration Journey declares name no specification file, so they are shown but not counted.");
        if (stale > 0)
            limitations.Add($"{stale} manual link(s) name a requirement that is no longer in the reviewed specification (removed or renamed). They are kept with the case and not counted; open the case to remove or replace them.");

        return new PreparedCaseTraceView
        {
            ProjectKey = projectKey, SpecificationPaths = [.. specificationPaths.Where(p => !string.IsNullOrWhiteSpace(p))], Plans = plans.Count, Cases = cases.Count,
            LinkedCases = cases.Count(c => c.Counted), UnlinkedCases = cases.Count(c => !c.Counted),
            RequirementsWithApprovedCase = requirements.Count(r => r.ApprovedCases > 0),
            RequirementsWithPreparedCaseOnly = requirements.Count(r => r.ApprovedCases == 0),
            CaseList = cases, Requirements = requirements, Limitations = limitations,
            SpecificationRequirements = ids is null || paths.Count == 0 ? null : ids.Count, WithoutTestDesign = gaps, StaleManualLinks = stale,
            ExecutedCases = cases.Count(c => c.Executed), PassedCases = cases.Count(c => c.Passed),
            RequirementsExecuted = requirements.Count(r => r.ExecutedCases > 0), RequirementsPassed = requirements.Count(r => r.PassedCases > 0),
        };
    }

    public static PreparedCaseStage Stage(TestCaseReviewState review) => review switch
    {
        TestCaseReviewState.Accepted => PreparedCaseStage.Approved,
        TestCaseReviewState.NeedsClarification => PreparedCaseStage.NeedsClarification,
        TestCaseReviewState.Excluded => PreparedCaseStage.Excluded,
        TestCaseReviewState.Superseded => PreparedCaseStage.Superseded,
        _ => PreparedCaseStage.Prepared,
    };

    public static string StageLabel(PreparedCaseStage stage) => stage switch
    {
        PreparedCaseStage.Approved => "Approved (accepted for preparation)",
        PreparedCaseStage.NeedsClarification => "Prepared — needs clarification",
        PreparedCaseStage.Excluded => "Excluded by the tester",
        PreparedCaseStage.Superseded => "Superseded (left the source)",
        _ => "Prepared",
    };

    static PreparedCaseTrace Trace(TestPlanDraft plan, TestCaseDraft c, HashSet<string> paths, HashSet<string>? ids, IReadOnlyDictionary<string, string> titles)
    {
        var stage = Stage(c.Review);
        var active = stage is not (PreparedCaseStage.Excluded or PreparedCaseStage.Superseded);
        var capability = c.Generated.Capability;
        var specFile = SpecificationFile(c.Generated.Evidence);
        var links = c.Generated.Requirements.Where(r => !string.IsNullOrWhiteSpace(r.RuleId)).DistinctBy(r => r.RuleId, StringComparer.OrdinalIgnoreCase).Select(r =>
        {
            var id = r.RuleId.Trim();
            if (c.Source.Kind == ScenarioSourceKind.SystemGuide || capability == TestScenarioDiscoveryEngine.SystemGuide)
                return new PreparedCaseRequirementLink(id, null, PreparedCaseLinkBasis.GuideBusinessRule, false,
                    "A business rule of the System Understanding guide, not a specification requirement.", r.Title, PreparedLinkValidity.NotARequirement);
            if (capability != TestScenarioDiscoveryEngine.Specification)
                return new PreparedCaseRequirementLink(id, null, PreparedCaseLinkBasis.JourneyReference, false,
                    "Declared by an Integration Journey without a specification file: not matched to the reviewed specification.", r.Title, PreparedLinkValidity.NotARequirement);
            var file = RequirementFile(c.Generated.Evidence, id) ?? specFile;
            var validity = Validity(file, id, paths, ids);
            var (counted, why) = !active ? (false, $"The case is {StageLabel(stage).ToLowerInvariant()}: not counted.")
                : file is null ? (false, "The case's evidence names no specification file.")
                : validity == PreparedLinkValidity.NotChecked ? (false, "No specification is being reviewed.")
                : validity == PreparedLinkValidity.OtherSpecification ? (false, $"Prepared from {file}, not from the reviewed specification.")
                : validity == PreparedLinkValidity.RequirementNotFound ? (false, $"{id} is not a requirement of the reviewed specification.")
                : (true, $"Prepared from {file}, which names {id}.");
            return new PreparedCaseRequirementLink(id, file, PreparedCaseLinkBasis.SpecificationEvidence, counted, why, titles.GetValueOrDefault(id) ?? r.Title,
                file is null ? null : validity);
        }).ToList();
        foreach (var m in c.RequirementLinks.Where(m => !string.IsNullOrWhiteSpace(m.RequirementId))
                     .DistinctBy(m => (m.RequirementId.Trim().ToUpperInvariant(), NormalizePath(m.SpecificationPath ?? ""))))
        {
            var id = m.RequirementId.Trim();
            var file = string.IsNullOrWhiteSpace(m.SpecificationPath) ? null : m.SpecificationPath.Trim();
            var validity = Validity(file, id, paths, ids);
            var who = string.IsNullOrWhiteSpace(m.LinkedBy) ? "a tester" : m.LinkedBy;
            var (counted, why) = !active ? (false, $"Linked by {who}; the case is {StageLabel(stage).ToLowerInvariant()}: not counted.")
                : file is null ? (false, $"Linked by {who} without a specification file: not counted.")
                : validity == PreparedLinkValidity.NotChecked ? (false, $"Linked by {who}; no specification is being reviewed, so the link cannot be checked.")
                : validity == PreparedLinkValidity.OtherSpecification ? (false, $"Linked by {who} to {file}, not the reviewed specification.")
                : validity == PreparedLinkValidity.RequirementNotFound ? (false, $"Linked by {who}, but {id} is no longer in {file} (removed or renamed). Kept, not counted.")
                : (true, $"Linked by {who} to {id} of {file} (manually confirmed).");
            links.Add(new PreparedCaseRequirementLink(id, file, PreparedCaseLinkBasis.ManualConfirmation, counted, why, titles.GetValueOrDefault(id) ?? m.Title, validity,
                string.IsNullOrWhiteSpace(m.LinkedBy) ? null : m.LinkedBy, m.LinkedAt == default ? null : m.LinkedAt));
        }
        var flows = new[] { c.Source }.Concat(c.Correlations.Where(x => x.State == CorrelationState.Confirmed).Select(x => x.Target))
            .Where(r => r.Kind == ScenarioSourceKind.CriticalE2EFlow).Select(r => r.ScenarioId).Distinct(StringComparer.Ordinal).ToList();
        var caseId = c.Source.ScenarioId;
        return new PreparedCaseTrace
        {
            CaseKey = $"{plan.Id:N}/{caseId}", DraftId = plan.Id, CaseId = caseId, PlanName = plan.PlanName, PlanRevision = plan.Revision, Title = c.EffectiveTitle,
            SourceKind = c.Source.Kind, Capability = c.Source.Kind == ScenarioSourceKind.Manual ? null : capability, Stage = stage, StageLabel = StageLabel(stage),
            Requirements = links, SpecificationScenarioId = capability == TestScenarioDiscoveryEngine.Specification ? ScenarioIdOf(c.Generated.Evidence) : null,
            CriticalE2EFlowIds = flows,
            Href = $"test-plan-suggestions?draft={plan.Id}&area=prepare&case={Uri.EscapeDataString(caseId)}",
        };
    }

    /// <summary>The case's latest completed manual run (an abandoned or in-progress run never gives an outcome) and whether it ran the current content.</summary>
    static PreparedCaseTrace WithRuns(PreparedCaseTrace trace, TestCaseDraft c, IReadOnlyDictionary<string, List<ManualTestRun>> runsByCase)
    {
        if (!runsByCase.TryGetValue(trace.CaseKey, out var list)) return trace;
        var inProgress = list.Any(r => r.State == ManualRunState.InProgress);
        var latest = list.Where(r => r.State == ManualRunState.Completed).OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.StartedAt).FirstOrDefault();
        if (latest is null) return trace with { RunInProgress = inProgress, Execution = inProgress ? "Run in progress — not executed yet" : PreparedCaseTraceView.NotExecuted };
        var current = latest.CaseFingerprint == ManualTestRunService.Fingerprint(c);
        var when = latest.CompletedAt?.ToString("yyyy-MM-dd") ?? "";
        return trace with
        {
            LatestRun = ManualRunRules.Summary(latest), LatestRunCurrent = current, RunInProgress = inProgress,
            Execution = $"{ManualRunRules.OutcomeLabel(latest.Outcome)} — manual run {when} by {latest.Tester}{(current ? "" : " (an earlier revision of the case)")}",
        };
    }

    static PreparedLinkValidity Validity(string? file, string id, HashSet<string> paths, HashSet<string>? ids) =>
        paths.Count == 0 || file is null ? PreparedLinkValidity.NotChecked
        : !paths.Contains(NormalizePath(file)) ? PreparedLinkValidity.OtherSpecification
        : ids is not null && !ids.Contains(id) ? PreparedLinkValidity.RequirementNotFound
        : PreparedLinkValidity.Current;

    /// <summary>The specification file of the case's own evidence ("{file}#{id}" of kind specification or requirement).</summary>
    static string? SpecificationFile(IEnumerable<CandidateEvidenceRef> evidence) =>
        evidence.Where(e => e.Kind is "specification" or "requirement").Select(e => FileOf(e.Reference)).FirstOrDefault(f => f is not null);

    static string? RequirementFile(IEnumerable<CandidateEvidenceRef> evidence, string id) =>
        evidence.Where(e => e.Kind == "requirement" && e.Reference.EndsWith("#" + id, StringComparison.OrdinalIgnoreCase)).Select(e => FileOf(e.Reference)).FirstOrDefault(f => f is not null);

    static string? ScenarioIdOf(IEnumerable<CandidateEvidenceRef> evidence) =>
        evidence.Where(e => e.Kind == "specification").Select(e => e.Reference.LastIndexOf('#') is var i and >= 0 ? e.Reference[(i + 1)..] : null).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    /// <summary>"specs/x/spec.md#FR-001" → "specs/x/spec.md"; a bare id or the "specification" placeholder (no file was known) → null.</summary>
    static string? FileOf(string reference)
    {
        var hash = reference.LastIndexOf('#');
        if (hash <= 0) return null;
        var file = reference[..hash];
        return file == "specification" ? null : file;
    }

    static string NormalizePath(string path) => path.Replace('\\', '/').Trim().TrimStart('/');

    /// <summary>FR-2 before FR-10.</summary>
    sealed class NaturalId : IComparer<string>
    {
        public static readonly NaturalId Instance = new();
        public int Compare(string? x, string? y)
        {
            static (string Prefix, long Number) Split(string s)
            {
                var i = s.Length; while (i > 0 && char.IsAsciiDigit(s[i - 1])) i--;
                return (s[..i], i < s.Length && long.TryParse(s[i..], out var n) ? n : -1);
            }
            var (a, b) = (Split(x ?? ""), Split(y ?? ""));
            var c = string.Compare(a.Prefix, b.Prefix, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Number.CompareTo(b.Number);
        }
    }
}
