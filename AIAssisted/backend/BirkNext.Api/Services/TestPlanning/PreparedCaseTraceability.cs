using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// The one rule that turns a project's saved test cases (Test Plan Suggestions project plans) into test-design evidence for Test Coverage and
/// Requirements Traceability. Pure: no I/O, no new store. A case counts for a requirement only when its own evidence (the specification file and
/// requirement id it was prepared from) names that requirement in the reviewed specification — never by title similarity, never for a guide
/// business rule (MT-7), and never for an excluded or superseded case. A requirement is counted once however many cases cover it. A prepared or
/// approved case is never an executed or passed test.
/// </summary>
public static class PreparedCaseTraceability
{
    /// <param name="drafts">Stored plans; only this project's own plans (owner "project:{key}" and ProjectKey = key) are read.</param>
    /// <param name="specificationPaths">The reviewed specification file(s); empty = none given, so no link is counted.</param>
    /// <param name="requirementIds">The reviewed specification's requirement ids; null = the ids the specification evidence names are taken as is.</param>
    public static PreparedCaseTraceView Build(string projectKey, IEnumerable<TestPlanDraft> drafts, IReadOnlyCollection<string> specificationPaths,
        IReadOnlyCollection<string>? requirementIds)
    {
        var owner = TestPlanSuggestionService.ProjectOwner(projectKey);
        var plans = drafts.Where(d => d.ProjectKey == projectKey && d.GuideKey == owner).OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).ToList();
        var paths = specificationPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = requirementIds?.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cases = plans.SelectMany(plan => plan.Cases.Select(c => Trace(plan, c, paths, ids))).ToList();
        var requirements = cases.SelectMany(c => c.Requirements.Where(r => r.Counted).Select(r => (Link: r, Case: c)))
            .GroupBy(x => (Id: x.Link.RequirementId.ToUpperInvariant(), Path: NormalizePath(x.Link.SpecificationPath ?? "")))
            .Select(g =>
            {
                var approved = g.Where(x => x.Case.Stage == PreparedCaseStage.Approved).Select(x => x.Case.CaseKey).Distinct(StringComparer.Ordinal).Count();
                var keys = g.Select(x => x.Case.CaseKey).Distinct(StringComparer.Ordinal).ToList();
                return new PreparedRequirementTrace
                {
                    RequirementId = g.First().Link.RequirementId, SpecificationPath = g.First().Link.SpecificationPath, ApprovedCases = approved,
                    PreparedCases = keys.Count - approved, CaseKeys = keys,
                    State = approved > 0 ? "Approved test case" : "Prepared, not approved yet",
                };
            }).OrderBy(r => r.SpecificationPath, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.RequirementId, NaturalId.Instance).ToList();

        var limitations = new List<string>();
        if (paths.Count == 0)
            limitations.Add("No specification was given, so no requirement link is counted. Select the specification in the Specification Explorer.");
        if (cases.Any(c => c.Requirements.Any(r => r.Basis == PreparedCaseLinkBasis.GuideBusinessRule)))
            limitations.Add("Guide business rules are shown, never counted as requirements; MT-7 reviewed coverage (Traceability & Coverage) owns guide scenarios.");
        if (cases.Any(c => c.Requirements.Any(r => r.Basis == PreparedCaseLinkBasis.JourneyReference)))
            limitations.Add("Requirement ids an Integration Journey declares name no specification file, so they are shown but not counted.");

        return new PreparedCaseTraceView
        {
            ProjectKey = projectKey, SpecificationPaths = [.. specificationPaths.Where(p => !string.IsNullOrWhiteSpace(p))], Plans = plans.Count, Cases = cases.Count,
            LinkedCases = cases.Count(c => c.Counted), UnlinkedCases = cases.Count(c => !c.Counted),
            RequirementsWithApprovedCase = requirements.Count(r => r.ApprovedCases > 0),
            RequirementsWithPreparedCaseOnly = requirements.Count(r => r.ApprovedCases == 0),
            CaseList = cases, Requirements = requirements, Limitations = limitations,
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

    static PreparedCaseTrace Trace(TestPlanDraft plan, TestCaseDraft c, HashSet<string> paths, HashSet<string>? ids)
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
                    "A business rule of the System Understanding guide, not a specification requirement.");
            if (capability != TestScenarioDiscoveryEngine.Specification)
                return new PreparedCaseRequirementLink(id, null, PreparedCaseLinkBasis.JourneyReference, false,
                    "Declared by an Integration Journey without a specification file: not matched to the reviewed specification.");
            var file = RequirementFile(c.Generated.Evidence, id) ?? specFile;
            var (counted, why) = !active ? (false, $"The case is {StageLabel(stage).ToLowerInvariant()}: not counted.")
                : file is null ? (false, "The case's evidence names no specification file.")
                : paths.Count == 0 ? (false, "No specification is being reviewed.")
                : !paths.Contains(NormalizePath(file)) ? (false, $"Prepared from {file}, not from the reviewed specification.")
                : ids is not null && !ids.Contains(id) ? (false, $"{id} is not a requirement of the reviewed specification.")
                : (true, $"Prepared from {file}, which names {id}.");
            return new PreparedCaseRequirementLink(id, file, PreparedCaseLinkBasis.SpecificationEvidence, counted, why);
        }).ToList();
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
