using BirkNext.TestCoverage;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>How a requirement is related to a test case: named by the source, confirmed by a person (for the case or for its scenario as a
/// whole), or only suggested. A suggestion is never shown or counted as a link until a person confirms it.</summary>
public enum RequirementRelation { Explicit, Confirmed, ConfirmedForScenario, Suggested }

/// <summary>
/// «Tilknyttede krav» (2026-11-24): the functional requirements around one case in Planlegg og klargjør, as supporting context. Built only from
/// the existing pieces — <see cref="RequirementLinkPresentation.Rows"/> (explicit + confirmed links, validity), <see cref="ScenarioCentricPresentation.Suggestions"/>
/// (the existing A–D suggester, now rule-aware and specification-scoped) and the server's <see cref="PreparedCaseTraceView"/> (execution).
/// No second traceability engine.
/// </summary>
public static partial class PlanPreparePresentation
{
    /// <summary>Separate coverage levels for one requirement. Never one percentage: source text is not coverage, a draft is not approved,
    /// approved is not executed, executed is not passed.</summary>
    public sealed record RequirementCoverageLevels(bool Identified, int Scenarios, int DraftCases, int ApprovedCases, int? ExecutedCases, int? PassedCases);

    public sealed record LinkedRequirement(
        string RequirementId, string? SpecificationPath, string Title, RequirementRelation Relation, string RelationText, string Tone,
        string? Behaviour, RequirementSuggestion? Suggestion, bool Technical, bool Missing, RequirementChange? Change, RequirementCoverageLevels Coverage);

    public static string LevelLabelNb(RequirementSuggestionLevel level) => level switch
    {
        RequirementSuggestionLevel.Explicit => "A · Nevnt i scenarioet",
        RequirementSuggestionLevel.Structural => "B · Samme struktur i spesifikasjonen",
        RequirementSuggestionLevel.Semantic => "C · Samme atferd beskrevet",
        _ => "D · Mulig relatert",
    };

    /// <summary>The requirements around a case: linked first (explicit, confirmed), then suggestions. Technical links stay visible (tagged) because
    /// they are directly related; technical suggestions are left out unless the scenario names them (A).</summary>
    public static List<LinkedRequirement> LinkedRequirements(TestCaseDraft c, TestPlanDraft plan, IReadOnlyCollection<ProjectRequirementOption>? options,
        PreparedCaseTraceView? trace = null, IReadOnlyCollection<DiscoveredScenario>? candidates = null)
    {
        var result = new List<LinkedRequirement>();
        var scenario = c.ScenarioKey is { } sk ? plan.Cases.FirstOrDefault(x => IdOf(x) == sk) : null;
        foreach (var row in RequirementLinkPresentation.Rows(c, options))
        {
            var link = c.RequirementLinks.FirstOrDefault(l => RequirementLinkPresentation.Key(l.RequirementId, l.SpecificationPath) == RequirementLinkPresentation.Key(row.RequirementId, row.SpecificationPath));
            var generated = c.Generated.Requirements.FirstOrDefault(r => string.Equals(r.RuleId.Trim(), row.RequirementId, StringComparison.OrdinalIgnoreCase));
            var missing = options is not null && row.Tone == "warn";
            var current = Current(options, row.RequirementId, row.SpecificationPath);
            // What changed since the link was made (the text kept with a confirmed link, or the text the case was generated from).
            // A generated link holds the id as its title (no wording to compare): only real wording is compared.
            var before = link is { Title.Length: > 0 } && !SameId(link.Title, row.RequirementId) ? link.Title
                : generated?.Title is { } gt && !SameId(gt, row.RequirementId) ? gt : null;
            var change = current is null || before is null ? null : ScenarioPlanning.ClassifyRequirementChange(before, current.Title);
            var (relation, text, tone) = row switch
            {
                { Manual: true } when link?.Scope == RequirementLinkScope.Scenario => (RequirementRelation.ConfirmedForScenario, "Bekreftet for scenarioet (teller ikke som dekning av testtilfellet)", "info"),
                { Manual: true, Saved: true } => (RequirementRelation.Confirmed,
                    (row.Automatic ? "Nevnt i kilden og bekreftet" : "Bekreftet") + (row.LinkedBy is { } by ? $" av {by}" : "") + (row.LinkedAt is { } at ? $" {at.ToLocalTime():yyyy-MM-dd}" : ""), "ok"),
                { Manual: true } => (RequirementRelation.Confirmed, "Bekreftet — lagres sammen med testtilfellet", "info"),
                _ => (RequirementRelation.Explicit, "Nevnt i kilden", "ok"),
            };
            result.Add(new(row.RequirementId, row.SpecificationPath, row.Title, relation, text, tone,
                generated?.ExpectedBehavior ?? link?.SuggestionReason, null, IsTechnicalRequirement(row.Title), missing, change is { Kind: RequirementChangeKind.Unchanged } ? null : change,
                Levels(row.RequirementId, row.SpecificationPath, options, plan, trace, candidates)));
        }
        // Requirements the case's own evidence names ("spec.md#FR-015a") are explicit too, in the case's own specification.
        foreach (var id in c.Generated.Evidence.Where(e => e.Kind == "requirement").Select(e => e.Reference.Contains('#') ? e.Reference[(e.Reference.LastIndexOf('#') + 1)..] : e.Reference)
                     .Where(id => ScenarioPlanning.RequirementIdsIn(id).Count == 1).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (result.Any(r => SameId(r.RequirementId, id))) continue;
            var path = RequirementLinkPresentation.SpecificationPathOf(c);
            var current = Current(options, id, path);
            result.Add(new(id.Trim(), current?.SpecificationPath ?? path, current?.Title ?? id.Trim(), RequirementRelation.Explicit, "Nevnt i kilden", "ok", null, null,
                IsTechnicalRequirement(current?.Title), options is not null && current is null, null, Levels(id.Trim(), current?.SpecificationPath ?? path, options, plan, trace, candidates)));
        }
        if (scenario is not null && !ReferenceEquals(scenario, c))
            foreach (var l in scenario.RequirementLinks.Where(l => l.Scope == RequirementLinkScope.Scenario
                && !result.Any(r => RequirementLinkPresentation.Key(r.RequirementId, r.SpecificationPath) == RequirementLinkPresentation.Key(l.RequirementId, l.SpecificationPath))))
                result.Add(new(l.RequirementId, l.SpecificationPath, Current(options, l.RequirementId, l.SpecificationPath)?.Title ?? l.Title, RequirementRelation.ConfirmedForScenario,
                    "Bekreftet for scenarioet (teller ikke som dekning av testtilfellet)", "info", l.SuggestionReason, null, IsTechnicalRequirement(l.Title),
                    options is not null && Current(options, l.RequirementId, l.SpecificationPath) is null, null, Levels(l.RequirementId, l.SpecificationPath, options, plan, trace, candidates)));
        foreach (var s in ScenarioCentricPresentation.Suggestions(c, scenario, options))
        {
            var technical = IsTechnicalRequirement(s.Title);
            if (technical && s.Level != RequirementSuggestionLevel.Explicit) continue;
            if (result.Any(r => RequirementLinkPresentation.Key(r.RequirementId, r.SpecificationPath) == RequirementLinkPresentation.Key(s.RequirementId, s.SpecificationPath))) continue;
            // "Samme regel" only when the rule facts match (same stated outcome); a word overlap is "samme atferd beskrevet".
            var label = s.Level == RequirementSuggestionLevel.Semantic && s.Reason.StartsWith("Same rule", StringComparison.Ordinal) ? "C · Samme regel (samme utfall)" : LevelLabelNb(s.Level);
            result.Add(new(s.RequirementId, s.SpecificationPath, s.Title, RequirementRelation.Suggested, $"Foreslått · {label}",
                s.Level is RequirementSuggestionLevel.Weak ? "muted" : "info", s.Reason, s, technical, false, null, Levels(s.RequirementId, s.SpecificationPath, options, plan, trace, candidates)));
        }
        return result;
    }

    private static bool SameId(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static ProjectRequirementOption? Current(IReadOnlyCollection<ProjectRequirementOption>? options, string id, string? path) =>
        options?.FirstOrDefault(o => string.Equals(o.RequirementId.Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase)
            && (path is null || BirkNext.Specifications.SpecificationCatalogRules.SamePath(o.SpecificationPath, path)));

    /// <summary>The coverage levels of one requirement. Draft and approved count this plan's cases that are linked (named by the source or
    /// confirmed for the case) — never suggestions, never scenario-wide links. Execution comes from completed manual runs across saved plans
    /// (null when not known here).</summary>
    public static RequirementCoverageLevels Levels(string id, string? path, IReadOnlyCollection<ProjectRequirementOption>? options, TestPlanDraft plan,
        PreparedCaseTraceView? trace, IReadOnlyCollection<DiscoveredScenario>? candidates)
    {
        bool Same(string? p) => path is null || p is null || BirkNext.Specifications.SpecificationCatalogRules.SamePath(p, path);
        var identified = Current(options, id, path) is not null;
        var scenarios = candidates?.Count(s => s.RequirementIds.Any(r => string.Equals(r, id, StringComparison.OrdinalIgnoreCase))
            && Same(s.Story?.SpecificationPath ?? (s.Evidence.FirstOrDefault()?.Reference is { Length: > 0 } r && r.IndexOf('#') is var h and > 0 ? r[..h] : null))) ?? 0;
        var linked = plan.Cases.Where(x => x.Review is not (TestCaseReviewState.Excluded or TestCaseReviewState.Superseded) && CountsFor(x, id, path)).ToList();
        var t = trace?.Requirements.FirstOrDefault(r => string.Equals(r.RequirementId, id, StringComparison.OrdinalIgnoreCase) && Same(r.SpecificationPath));
        return new(identified, scenarios, linked.Count(x => x.Review != TestCaseReviewState.Accepted), linked.Count(x => x.Review == TestCaseReviewState.Accepted),
            trace is null ? null : t?.ExecutedCases ?? 0, trace is null ? null : t?.PassedCases ?? 0);
    }

    /// <summary>The case itself is designed for the requirement: named by its own specification evidence, or confirmed for the case (not for the
    /// whole scenario).</summary>
    private static bool CountsFor(TestCaseDraft c, string id, string? path) =>
        RequirementLinkPresentation.IsSpecificationCase(c) && (c.Generated.Requirements.Any(r => SameId(r.RuleId, id))
                || c.Generated.Evidence.Any(e => e.Kind == "requirement" && SameId(e.Reference.Contains('#') ? e.Reference[(e.Reference.LastIndexOf('#') + 1)..] : e.Reference, id)))
            && (path is null || RequirementLinkPresentation.SpecificationPathOf(c) is not { } own || BirkNext.Specifications.SpecificationCatalogRules.SamePath(own, path))
        || c.RequirementLinks.Any(l => l.Scope != RequirementLinkScope.Scenario && string.Equals(l.RequirementId.Trim(), id, StringComparison.OrdinalIgnoreCase)
            && (path is null || BirkNext.Specifications.SpecificationCatalogRules.SamePath(l.SpecificationPath, path)));

    /// <summary>The save request for one requirement-link change of one case: every other part of the plan (tester edits, approvals,
    /// translations, other cases) is sent exactly as stored, so nothing a person saved is overwritten.</summary>
    public static SaveTestPlanDraftRequest RequirementChangeRequest(TestPlanDraft plan, string caseId, Func<TestCaseDraft, TestCaseDraft> change,
        IReadOnlyCollection<ProjectRequirementOption>? requirements) =>
        TestPlanSuggestionsPresentation.ToSaveRequest(TestPlanSuggestionsPresentation.WithCase(plan, caseId, change), requirements: requirements);

    /// <summary>The save request recording one test-design decision for a scenario (replacing an earlier decision on the same proposal). Cases and
    /// everything else are sent as stored.</summary>
    public static SaveTestPlanDraftRequest DesignDecisionRequest(TestPlanDraft plan, string scenarioKey, TestCaseVariantProposal proposal, string decision) =>
        TestPlanSuggestionsPresentation.ToSaveRequest(plan) with
        {
            DesignDecisions = [.. (plan.DesignDecisions ?? []).Where(d => !(d.ScenarioKey == scenarioKey && d.ProposalKey == proposal.Key)),
                new TestDesignDecision { ScenarioKey = scenarioKey, ProposalKey = proposal.Key, ProposalTitle = proposal.Title, Decision = decision }],
        };

    /// <summary>The coverage levels in words, one per level (shown as separate chips).</summary>
    public static IReadOnlyList<(string Text, string Tone)> CoverageChips(RequirementCoverageLevels l) =>
    [
        l.Identified ? ("I spesifikasjonen", "ok") : ("Ikke i valgt spesifikasjon", "warn"),
        l.Scenarios > 0 ? ($"{l.Scenarios} akseptansescenario{(l.Scenarios == 1 ? "" : "er")}", "ok") : ("Ingen akseptansescenario", "warn"),
        l.DraftCases > 0 ? ($"{l.DraftCases} utkast i planen", "info") : ("Ingen utkast i planen", "muted"),
        l.ApprovedCases > 0 ? ($"{l.ApprovedCases} godkjent i planen", "ok") : ("Ingen godkjent i planen", "muted"),
        l.ExecutedCases is null ? ("Utførelse ikke kjent her", "muted")
            : l.ExecutedCases > 0 ? ($"Utført {l.ExecutedCases} (bestått {l.PassedCases})", l.PassedCases == l.ExecutedCases ? "ok" : "warn")
            : ("Ikke utført", "muted"),
    ];
}
