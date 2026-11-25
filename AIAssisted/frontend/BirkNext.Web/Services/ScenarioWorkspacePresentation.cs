using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using S = BirkNext.Web.Services.ScenarioBrowserPresentation;

namespace BirkNext.Web.Services;

/// <summary>
/// The User Story–aware scenario workspace of Manuell testing → Finn og forstå (2026-11-24). Pure: it reads the stored discovery, the work queues
/// and the saved-case trace, never changes them, and never invents a story, a requirement link, a case or a status.
/// <list type="bullet">
/// <item><b>User stories group, they do not gate.</b> «Alle scenarioer» is always available, search reaches every story, and scenarios with no story
/// (contracts, source code, requirements) have their own group — a story is business context, never a compulsory step.</item>
/// <item><b>Requirement relations are named for what they are:</b> named in the source (explicit), confirmed by a tester on a saved case, or
/// suggested (A–D, with the reason). A suggestion is never shown as a link.</item>
/// <item><b>Readiness is the evidence-based check list</b> of <see cref="TestDesignAdvisor.Readiness(TestCaseDraft)"/> in six tester questions;
/// unknown is never shown as done, and each open point says who usually resolves it.</item>
/// </list>
/// </summary>
public static class ScenarioWorkspacePresentation
{
    /// <summary>The <see cref="S.Filter.Story"/> value of the group «Uten brukerhistorie».</summary>
    public const string NoStory = "(none)";

    // ── User stories ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <param name="Declared">Acceptance scenarios the specification gives the story (null for the no-story group).</param>
    /// <param name="Shown">Scenarios of the story the other filters show (search, area, technical content …; not the work queue).</param>
    /// <param name="WithCases">Scenarios of the story with at least one current saved test case.</param>
    /// <param name="Technical">A story about the code base (e.g. "translate the source to English"), or one whose scenarios are all technical: not
    /// manual-testing work, so the list hides it unless «Vis teknisk innhold» is on.</param>
    public sealed record StoryEntry(string Key, CoverageUserStory? Story, string Label, string Title, string? Priority, int? Declared, int Discovered, int Shown,
        int WithCases, string? Feature, bool Technical = false);

    /// <summary>The stories the rail shows: manual-testing stories first and only; technical stories and an empty «Uten brukerhistorie» group appear
    /// when technical content is shown (or when the tester has one of them open).</summary>
    public static (List<StoryEntry> Shown, int HiddenTechnical) RailStories(IReadOnlyList<StoryEntry> stories, bool showTechnical, string? current)
    {
        bool Keep(StoryEntry e) => showTechnical || e.Key == current || (!e.Technical && (e.Story is not null || e.Shown > 0));
        var shown = stories.Where(Keep).ToList();
        return (shown, stories.Count(e => !Keep(e) && e.Story is not null));
    }

    public static string StoryLabel(CoverageUserStory s) => s.StoryId is { Length: > 0 } id ? id : $"Historie {s.Ordinal}";

    /// <summary>The story's own title without the heading's "User Story 1 -" prefix and "(Priority: P1)" suffix (shown as chips); the stored title
    /// is unchanged. Anything else is kept as written.</summary>
    public static string StoryTitle(CoverageUserStory s) => CleanStoryTitle(s.Title);

    /// <summary>A story title without its "User Story 1 - " / "Brukerhistorie 1 - " prefix and "(Priority: P1)" / "(Prioritet: P1)" suffix — also
    /// for a translated title.</summary>
    public static string CleanStoryTitle(string title)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(title.Trim(), @"^(?:User\s+Story|Brukerhistorie|US)\s*-?\s*\w*\d+\w*\s*[-–—:]\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*\((?:Priority|Prioritet)\s*:\s*[^)]*\)\s*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return t.Length > 0 ? t : title;
    }

    /// <summary>The user stories of the discovery in specification order, then «Uten brukerhistorie» when some scenarios have none. The feature
    /// (specification folder) is named only when the scenarios come from more than one specification.</summary>
    public static List<StoryEntry> Stories(IReadOnlyList<S.Item> items, S.Filter filter)
    {
        var visible = S.Apply(items, filter with { Story = null, Queue = null }).Shown.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var stories = items.Where(i => i.Candidate.Story is not null).GroupBy(i => i.Candidate.Story!.Key, StringComparer.Ordinal).ToList();
        var paths = stories.Select(g => g.First().Candidate.Story!.SpecificationPath ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var result = stories
            .Select(g =>
            {
                var s = g.First().Candidate.Story!;
                return new StoryEntry(g.Key, s, StoryLabel(s), StoryTitle(s), s.Priority, s.Scenarios > 0 ? s.Scenarios : null, g.Count(), g.Count(i => visible.Contains(i.Id)),
                    g.Count(i => i.Prepared is { Cases: > 0 }), paths > 1 ? FeatureOf(s.SpecificationPath) : null,
                    ManualTestingRelevance.StoryReason(s) is not null || g.All(i => i.Relevance == S.Relevance.Technical));
            })
            .OrderBy(e => e.Story!.SpecificationPath ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Story!.Ordinal).ToList();
        var loose = items.Where(i => i.Candidate.Story is null).ToList();
        if (loose.Count > 0)
            result.Add(new StoryEntry(NoStory, null, "Uten brukerhistorie", "Fra kontrakter, kildekode, krav og flyter", null, null, loose.Count,
                loose.Count(i => visible.Contains(i.Id)), loose.Count(i => i.Prepared is { Cases: > 0 }), null));
        return result;
    }

    /// <summary>"specs/001-skoleservice/spec.md" → "001-skoleservice"; null when there is no folder.</summary>
    public static string? FeatureOf(string? specificationPath)
    {
        if (string.IsNullOrWhiteSpace(specificationPath)) return null;
        var parts = specificationPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^2] : null;
    }

    /// <summary>"Scenario 2" for the second acceptance scenario of its story (the number the specification gives); otherwise the scenario type.</summary>
    /// <summary>A story's acceptance scenario is named by its acceptance criterion number within the story ("AC 3", 2026-11-25); the story is shown
    /// beside it, so the title never repeats "US1 — ".</summary>
    public static string ScenarioLabel(S.Item i) =>
        i.Candidate.Story is not null && i.Candidate.NumberInStory is { } n ? AcLabel(n) : i.TypeLabel;

    public static string AcLabel(int number) => $"AC {number}";

    /// <summary>The scenario title in the detail pane: "AC 3 — <title>" for a story's acceptance scenario, never "US1 — …"; else the title.</summary>
    public static string DetailTitle(S.Item i) =>
        i.Candidate.Story is not null && i.Candidate.NumberInStory is { } n ? $"{AcLabel(n)} — {RowTitle(i)}" : i.Title;

    /// <summary>The row title under a "Scenario N" label: the display title without its leading "US1 — " (the label and the story already say it).</summary>
    public static string RowTitle(S.Item i) =>
        i.Candidate.NumberInStory is not null && i.Candidate.Story?.StoryId is { Length: > 0 } id && i.Title.StartsWith(id + " — ", StringComparison.Ordinal) ? i.Title[(id.Length + 3)..] : i.Title;

    /// <summary>How another scenario is named in a link: "Scenario 1 (US2)" when the specification numbers it, otherwise its own title (shortened).</summary>
    public static string OtherScenarioLabel(S.Item i) =>
        i.Candidate.Story is { } s && i.Candidate.NumberInStory is not null ? $"{ScenarioLabel(i)} ({StoryLabel(s)})"
        : i.Title.Length > 60 ? i.Title[..57].TrimEnd() + "…" : i.Title;

    /// <summary>A saved case's stage in tester words (the trace's labels are English).</summary>
    public static string StageText(PreparedCaseStage s) => s switch
    {
        PreparedCaseStage.Approved => "Godkjent", PreparedCaseStage.NeedsClarification => "Trenger avklaring", PreparedCaseStage.Excluded => "Utelatt",
        PreparedCaseStage.Superseded => "Utgått", _ => "Forberedt",
    };

    /// <summary>A case's execution in tester words: "Not executed" is translated; a run summary is shown as the server words it.</summary>
    public static string ExecutionText(string execution) => execution == PreparedCaseTraceView.NotExecuted ? "Ikke kjørt" : execution;

    /// <summary>The scenario count line of a story header: what the specification declares, and how many discovery found when that differs.</summary>
    public static string ScenarioCountText(StoryEntry e) => e.Declared is { } d
        ? d == e.Discovered ? $"{d} akseptansescenario{(d == 1 ? "" : "er")} i spesifikasjonen"
            : $"{d} akseptansescenario{(d == 1 ? "" : "er")} i spesifikasjonen · {e.Discovered} funnet ved oppdagelsen"
        : $"{e.Discovered} scenario{(e.Discovered == 1 ? "" : "er")}";

    // ── Saved test cases of a scenario ───────────────────────────────────────────────────────────────────────────

    /// <summary>The acceptance-scenario id of a specification scenario ("…/spec.md#US1-AS2" → "US1-AS2"), the same id a saved case records.</summary>
    public static string? SpecificationScenarioId(DiscoveredScenario c) =>
        c.Evidence.Where(e => e.Kind == "specification").Select(e => e.Reference.LastIndexOf('#') is var i and >= 0 ? e.Reference[(i + 1)..] : null)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    /// <summary>Saved cases prepared from the scenario (matched by its specification scenario id; cases of other scenario kinds are counted by the work
    /// queues but cannot be listed here). Excluded and superseded cases are history and are left out.</summary>
    public static List<PreparedCaseTrace> CasesOf(DiscoveredScenario c, PreparedCaseTraceView? trace)
    {
        if (trace is null || SpecificationScenarioId(c) is not { } id) return [];
        return [.. trace.CaseList.Where(x => string.Equals(x.SpecificationScenarioId, id, StringComparison.Ordinal)
            && x.Stage is not (PreparedCaseStage.Excluded or PreparedCaseStage.Superseded))];
    }

    /// <summary>The deep link to one saved case in Planlegg og klargjør (the plan opens with that case selected).</summary>
    public static string CaseHref(PreparedCaseTrace c) =>
        $"{ManualTestingPresentation.PlanRoute}?draft={c.DraftId}&case={Uri.EscapeDataString(c.CaseId)}";

    // ── Requirements around a scenario ───────────────────────────────────────────────────────────────────────────

    public enum Relation { Explicit, Confirmed, Suggested }

    /// <param name="ConfirmedOn">Saved cases on which a tester confirmed the link (empty unless <see cref="Relation"/> is Confirmed or the explicit link
    /// was also confirmed).</param>
    public sealed record RequirementLink(string Id, string? Title, string? SpecificationPath, Relation Relation, RequirementSuggestionLevel? Level, string Explanation,
        IReadOnlyList<string> ConfirmedOn);

    /// <summary>
    /// The scenario's requirements: named in its source (A), confirmed by a tester on one of its saved cases, then suggestions (B–D) the scenario's
    /// text supports, restricted to the scenario's own specification. Each says why. Never a guess presented as a link.
    /// </summary>
    public static List<RequirementLink> Requirements(DiscoveredScenario c, IReadOnlyCollection<ProjectRequirementOption>? catalog, IReadOnlyList<PreparedCaseTrace> cases)
    {
        var path = c.Story?.SpecificationPath;
        string? TitleOf(string id) => catalog?.FirstOrDefault(r => string.Equals(r.RequirementId, id, StringComparison.OrdinalIgnoreCase)
            && (path is null || BirkNext.Specifications.SpecificationCatalogRules.SamePath(r.SpecificationPath, path)))?.Title
            ?? catalog?.FirstOrDefault(r => string.Equals(r.RequirementId, id, StringComparison.OrdinalIgnoreCase))?.Title;
        var confirmed = cases.SelectMany(k => k.Requirements.Where(r => r.Manual).Select(r => (r.RequirementId, Case: k.Title)))
            .GroupBy(x => x.RequirementId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(x => x.Case).Distinct()], StringComparer.OrdinalIgnoreCase);
        var result = new List<RequirementLink>();
        foreach (var id in c.RequirementIds.Distinct(StringComparer.OrdinalIgnoreCase))
            result.Add(new(id, TitleOf(id), path, Relation.Explicit, RequirementSuggestionLevel.Explicit, "Kravet er nevnt i scenarioets egen kilde.",
                confirmed.GetValueOrDefault(id) ?? []));
        foreach (var (id, on) in confirmed.Where(x => result.All(r => !string.Equals(r.Id, x.Key, StringComparison.OrdinalIgnoreCase))))
            result.Add(new(id, TitleOf(id), path, Relation.Confirmed, null, $"Bekreftet av en tester på testtilfellet «{on[0]}»{(on.Count > 1 ? $" og {on.Count - 1} til" : "")}.", on));
        if (catalog is { Count: > 0 })
        {
            var draft = TestDesignAdvisor.AsDraft(c);
            var excluded = catalog.Where(o => result.Any(r => string.Equals(r.Id, o.RequirementId, StringComparison.OrdinalIgnoreCase)))
                .Select(o => ScenarioPlanning.Key(o.RequirementId, o.SpecificationPath));
            var text = string.Join("\n", new[] { c.Title, c.Purpose, string.Join("\n", c.Preconditions), string.Join("\n", c.Steps.Select(s => $"{s.Action} {s.Expected}")), c.ExpectedBehaviour ?? "" }
                .Where(t => !string.IsNullOrWhiteSpace(t)));
            foreach (var s in ScenarioPlanning.SuggestRequirements(text, c.RequirementIds, ScenarioPlanning.StructuralReferences(draft), catalog, excluded, scopePath: path)
                         .Where(s => s.Level != RequirementSuggestionLevel.Explicit))
                result.Add(new(s.RequirementId, s.Title, s.SpecificationPath, Relation.Suggested, s.Level, s.Reason, []));
        }
        return result;
    }

    public static string RelationLabel(RequirementLink l) => l.Relation switch
    {
        Relation.Explicit => l.ConfirmedOn.Count > 0 ? "Nevnt i kilden · bekreftet" : "Nevnt i kilden",
        Relation.Confirmed => "Bekreftet av tester",
        _ => l.Level is { } level ? SuggestionLabel(level, l.Explanation) : "Foreslått",
    };

    /// <summary>B–D in tester words; a rule-level match ("Same rule: …") is named as the same rule with the same outcome.</summary>
    public static string SuggestionLabel(RequirementSuggestionLevel level, string reason) =>
        level == RequirementSuggestionLevel.Semantic && reason.StartsWith("Same rule:", StringComparison.Ordinal) ? "Foreslått · C · Samme regel (samme utfall)"
        : "Foreslått · " + PlanPreparePresentation.LevelLabelNb(level);

    public static string RelationTone(RequirementLink l) => l.Relation switch
    {
        Relation.Explicit or Relation.Confirmed => "ok",
        _ => l.Level is RequirementSuggestionLevel.Structural or RequirementSuggestionLevel.Semantic ? "info" : "muted",
    };

    /// <summary>The other scenarios that name the requirement (many-to-many, from the same discovery), for «Andre scenarioer med kravet».</summary>
    public static List<S.Item> ScenariosNaming(IReadOnlyList<S.Item> items, string requirementId, string? exceptId) =>
        [.. items.Where(i => i.Id != exceptId && i.Candidate.RequirementIds.Contains(requirementId, StringComparer.OrdinalIgnoreCase))];

    // ── Coverage of a story (Testdekning) ────────────────────────────────────────────────────────────────────────

    /// <param name="Basis">Where the link comes from, in tester words (story text, tasks, scenario numbers).</param>
    public sealed record RequirementCoverageRow(string Id, string? Title, string Basis, int Scenarios, int PreparedCases, int ApprovedCases, string Execution, bool Known);

    /// <summary>
    /// The requirements of a story (or of every shown scenario when no story is chosen): named by the story, by its tasks, or by its scenarios —
    /// with how many scenarios name each and the saved cases that cover it (from the trace; "ukjent" when the trace could not be read).
    /// </summary>
    public static List<RequirementCoverageRow> Coverage(CoverageUserStory? story, IReadOnlyList<S.Item> scenarios, IReadOnlyCollection<ProjectRequirementOption>? catalog,
        PreparedCaseTraceView? trace)
    {
        var basis = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string id, string why) { if (!basis.TryGetValue(id, out var l)) basis[id] = l = []; if (!l.Contains(why)) l.Add(why); }
        if (story is not null)
        {
            foreach (var id in story.NamedRequirements) Add(id, "nevnt i brukerhistorien");
            foreach (var t in story.TaskRequirements) Add(t.RequirementId, "fra oppgavene");
        }
        foreach (var i in scenarios)
        {
            foreach (var id in i.Candidate.RequirementIds) Add(id, i.Candidate.NumberInStory is { } n ? $"scenario {n}" : "scenario");
            foreach (var link in CasesOf(i.Candidate, trace).SelectMany(k => k.Requirements).Where(r => r.Manual)) Add(link.RequirementId, "bekreftet av tester");
        }
        return [.. basis.Select(b =>
        {
            var row = trace?.Requirements.FirstOrDefault(r => string.Equals(r.RequirementId, b.Key, StringComparison.OrdinalIgnoreCase));
            var count = scenarios.Count(i => i.Candidate.RequirementIds.Contains(b.Key, StringComparer.OrdinalIgnoreCase));
            var title = catalog?.FirstOrDefault(r => string.Equals(r.RequirementId, b.Key, StringComparison.OrdinalIgnoreCase))?.Title ?? row?.Title;
            return new RequirementCoverageRow(b.Key, title, Compact(b.Value), count, row?.PreparedCases ?? 0, row?.ApprovedCases ?? 0,
                row?.Execution ?? PreparedCaseTraceView.NotExecuted, trace is not null);
        }).OrderBy(r => r.Id, RequirementIdComparer.Instance)];
    }

    /// <summary>"scenario 1, scenario 2, scenario 4" → "scenario 1, 2, 4".</summary>
    private static string Compact(List<string> why)
    {
        var numbered = why.Where(w => w.StartsWith("scenario ", StringComparison.Ordinal)).Select(w => w["scenario ".Length..]).ToList();
        var other = why.Where(w => !w.StartsWith("scenario", StringComparison.Ordinal)).ToList();
        if (numbered.Count > 0) other.Add("scenario " + string.Join(", ", numbered));
        else if (why.Contains("scenario")) other.Add("scenario");
        return string.Join(" · ", other);
    }

    /// <param name="Scenarios">The scenario numbers (or titles) the requirement is suggested for.</param>
    public sealed record SuggestedRequirementRow(string Id, string? Title, string Level, IReadOnlyList<string> Scenarios, bool Weak = false);

    /// <summary>
    /// Requirements suggested (B–D) for the scenarios of one story and not linked to any of them: shown apart from coverage, never counted. Only for
    /// a chosen story (a bounded number of scenarios); the reason per scenario is in the scenario's own detail.
    /// </summary>
    public static List<SuggestedRequirementRow> Suggested(IReadOnlyList<S.Item> scenarios, IReadOnlyCollection<ProjectRequirementOption>? catalog, PreparedCaseTraceView? trace,
        IReadOnlyCollection<string> linked)
    {
        if (catalog is not { Count: > 0 }) return [];
        var linkedSet = new HashSet<string>(linked, StringComparer.OrdinalIgnoreCase);
        return [.. scenarios
            .SelectMany(i => Requirements(i.Candidate, catalog, CasesOf(i.Candidate, trace)).Where(l => l.Relation == Relation.Suggested && !linkedSet.Contains(l.Id))
                .Select(l => (Link: l, Scenario: i.Candidate.NumberInStory is { } n ? $"scenario {n}" : i.Title)))
            .GroupBy(x => x.Link.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SuggestedRequirementRow(g.Key, g.First().Link.Title, RelationLabel(g.OrderBy(x => x.Link.Level).First().Link), [.. g.Select(x => x.Scenario).Distinct()],
                g.All(x => x.Link.Level == RequirementSuggestionLevel.Weak)))
            .OrderBy(r => r.Id, RequirementIdComparer.Instance)];
    }

    /// <summary>FR-2 before FR-10, FR-015 before FR-015a (natural order).</summary>
    public sealed class RequirementIdComparer : IComparer<string>
    {
        public static readonly RequirementIdComparer Instance = new();
        public int Compare(string? x, string? y)
        {
            static (string Prefix, int Number, string Tail) Split(string? s)
            {
                var m = System.Text.RegularExpressions.Regex.Match(s ?? "", @"^(?<p>\D*)(?<n>\d+)(?<r>.*)$");
                return m.Success ? (m.Groups["p"].Value, int.Parse(m.Groups["n"].Value), m.Groups["r"].Value) : (s ?? "", -1, "");
            }
            var (px, nx, rx) = Split(x); var (py, ny, ry) = Split(y);
            var c = string.Compare(px, py, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = nx.CompareTo(ny);
            return c != 0 ? c : string.Compare(rx, ry, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The Independent Test objectives of a story and which of its scenarios appear to address each (literal content words; a hint, never a verdict).</summary>
    public static IReadOnlyList<PlanPreparePresentation.ObjectiveCoverage> Objectives(CoverageUserStory story, IReadOnlyList<S.Item> scenarios) =>
        PlanPreparePresentation.ObjectiveCoverageOf(story, scenarios.Select(i => (Label: $"{ScenarioLabel(i)} — {i.Title}",
            Text: string.Join(" ", new[] { i.Candidate.Title, i.Candidate.Purpose, i.Candidate.ExpectedBehaviour ?? "" }.Concat(i.Candidate.Preconditions)
                .Concat(i.Candidate.Steps.Select(s => $"{s.Action} {s.Expected}")).Concat(i.Candidate.Template.Select(t => t.Value ?? ""))))));

    // ── Ready to run: six questions ──────────────────────────────────────────────────────────────────────────────

    /// <param name="Owner">Who usually resolves an open point (a suggestion for the team; BirkNext assigns nobody).</param>
    public sealed record ChecklistItem(string Key, string Question, Tri State, string Detail, string Owner);

    /// <summary>
    /// The readiness check list in six tester questions, derived only from <see cref="TestDesignAdvisor.Readiness(TestCaseDraft)"/> (stored evidence):
    /// source + expected result, test data, trigger, verification method, environment and access, review. Unknown stays unknown.
    /// </summary>
    public static List<ChecklistItem> Checklist(ExecutionReadiness r)
    {
        ReadinessCheck? Get(string name) => r.Checks.FirstOrDefault(c => c.Name == name);
        static Tri Worst(params ReadinessCheck?[] checks) =>
            checks.Any(c => c?.Satisfied == Tri.No) ? Tri.No : checks.Any(c => c is null || c.Satisfied == Tri.Unknown) ? Tri.Unknown : Tri.Yes;
        var source = Get("Kildegrunnlag"); var expected = Get("Forventet resultat");
        var review = Get("Gjennomgang");
        // Confirmed means a person approved it; an expected result that is only stated in the source is not yet confirmed.
        var confirmed = Worst(source, expected) == Tri.Yes && review?.Satisfied != Tri.Yes ? Tri.Unknown : Worst(source, expected);
        return
        [
            new("source", "Kilde og forventet atferd er bekreftet", confirmed,
                confirmed == Tri.Unknown && Worst(source, expected) == Tri.Yes ? "Står i kilden; bekreftes når testtilfellet godkjennes." : $"{source?.Detail} {expected?.Detail}".Trim(),
                "Produkteier / fagperson"),
            Item("data", "Testdata er tilgjengelige", Get("Testdata"), "Tester / testdataansvarlig"),
            Item("trigger", "Utløser eller kjøremåte er fastsatt", Get("Utløser"), "Tester / utvikler"),
            Item("verify", "Verifiseringsmetode er fastsatt", Get("Verifiseringsmetode"), "Tester"),
            Item("env", "Miljø og tilgang er på plass", Get("Tilgang og miljø"), "Miljøansvarlig"),
            Item("review", "Testtilfellet er gjennomgått og godkjent", review, "Testleder / reviewer"),
        ];

        static ChecklistItem Item(string key, string question, ReadinessCheck? c, string owner) =>
            new(key, question, c?.Satisfied ?? Tri.Unknown, c?.Detail ?? "Ikke vurdert.", owner);
    }

    public static string Mark(Tri t) => t switch { Tri.Yes => "✓", Tri.No => "✕", _ => "?" };
    public static string StateText(Tri t) => t switch { Tri.Yes => "Oppfylt", Tri.No => "Mangler", _ => "Ukjent" };

    // ── Remembered working context ───────────────────────────────────────────────────────────────────────────────

    /// <summary>What Finn og forstå restores when the tester returns: the story group, the open scenario and the tab.</summary>
    public sealed record WorkContext(string? Story, string? Scenario, string Tab = Tabs.Scenarios, string? ScenarioTitle = null);

    public static class Tabs
    {
        public const string Scenarios = "scenarios";
        public const string Cases = "cases";
        public const string Coverage = "coverage";
    }
}
