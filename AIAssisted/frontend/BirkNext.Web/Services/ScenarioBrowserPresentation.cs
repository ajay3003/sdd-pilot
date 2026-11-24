using System.Text.RegularExpressions;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// The scenario-first browser of Manual Testing → Discover &amp; understand (2026-11-23). Pure: it reads the stored discovery result and the saved
/// plans, never changes either, and never invents a story id, a requirement link, a status or a count.
/// <list type="bullet">
/// <item><b>Relevance</b> reuses the deterministic ownership rule (<see cref="ScenarioOwnership"/>): manual-testing scenarios are shown by default;
/// technical ones (API operations, page checks, contract authorization checks) are one filter away. Business integration journeys, authorization
/// checks a requirement names, scenarios with a source conflict and anything the rule cannot classify are never hidden.</item>
/// <item><b>Titles</b> put the real user story id first when the source has one, and drop a redundant "A user …" introduction only in the narrow
/// cases where that is grammatically safe. The original title stays in the details; identities are untouched.</item>
/// <item><b>Status</b> uses existing semantics: suggested, prepared (a saved case exists), approved, needs clarification, source changed, removed.</item>
/// </list>
/// </summary>
public static partial class ScenarioBrowserPresentation
{
    public const int PageSize = 40;

    /// <summary>Manual (shown by default) or Technical (behind "Vis tekniske scenarioer").</summary>
    public enum Relevance { Manual, Technical }

    public static Relevance RelevanceOf(DiscoveredScenario c, ScenarioOwnershipInfo o)
    {
        if (c.Conflicts.Count > 0) return Relevance.Manual;            // contradicting sources need a person
        // A scenario under a user story about the code base, or about the code base itself (shared rule; uncertain text is never technical).
        if (ManualTestingRelevance.StoryReason(c.Story) is not null || ManualTestingRelevance.TechnicalReason(c.Title, c.Purpose, c.ExpectedBehaviour) is not null)
            return Relevance.Technical;
        if (o.ManualRelevant || o.NeedsReview) return Relevance.Manual; // uncertain is never hidden
        if (o.Purpose == ScenarioPurpose.IntegrationFunctional) return Relevance.Manual; // business integration flows
        if (o.Purpose == ScenarioPurpose.SecurityTechnical && c.RequirementIds.Count > 0) return Relevance.Manual; // functional authorization a requirement names
        return Relevance.Technical;
    }

    // ── Titles ──────────────────────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^(?:A|An|The)\s+(?:user|system|tester|caseworker|citizen|reporter)\s+(?<verb>[a-z]{3,}s)\s+(?<rest>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishSubject();

    [GeneratedRegex(@"^(?:En|Ei|Et|Den|Brukeren|Saksbehandleren|Innbyggeren)\s*(?:bruker|saksbehandler|innbygger)?\s+(?<verb>[a-zæøå]{3,}er)\s+(?<rest>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex NorwegianSubject();

    [GeneratedRegex(@"^(?:a|an|the|en|ei|et)\s+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingArticle();

    /// <summary>
    /// A concise title: "US6 — Submit valid attendance report" from "A user submits a valid attendance report" under the source's user story US6.
    /// Only a subject in the short list followed by a third-person (English "-s") or present-tense (Norwegian "-er") verb is dropped; anything
    /// else is kept as written. The story id is the one the specification authored (never derived from an ordinal).
    /// </summary>
    public static string DisplayTitle(DiscoveredScenario c)
    {
        var title = Concise(c.Title.Trim());
        return c.Story?.StoryId is { Length: > 0 } story ? $"{story} — {title}" : title;
    }

    public static string Concise(string title)
    {
        if (EnglishSubject().Match(title) is { Success: true } en && BaseForm(en.Groups["verb"].Value) is { } verb)
            return Capital(verb) + " " + LeadingArticle().Replace(en.Groups["rest"].Value, "");
        if (NorwegianSubject().Match(title) is { Success: true } nb && nb.Groups["verb"].Value.Length >= 5)
            return Capital(nb.Groups["verb"].Value[..^2]) + " " + LeadingArticle().Replace(nb.Groups["rest"].Value, "");
        return title;
    }

    /// <summary>The base form of a regular English third-person verb ("submits" → "submit", "passes" → "pass"); null when it is not safely regular
    /// ("applies", "goes", "focus", "is"), so the title is then kept as written.</summary>
    private static string? BaseForm(string verb) =>
        verb.EndsWith("ies", StringComparison.Ordinal) || verb.EndsWith("oes", StringComparison.Ordinal) ? null
        : new[] { "sses", "shes", "ches", "xes", "zes" }.Any(e => verb.EndsWith(e, StringComparison.Ordinal)) ? verb[..^2]
        : verb.EndsWith("ss", StringComparison.Ordinal) || verb.EndsWith("us", StringComparison.Ordinal) || verb.EndsWith("is", StringComparison.Ordinal) ? null
        : verb[..^1];

    private static string Capital(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    // ── Status ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Saved test cases of one scenario across the project's plans (by candidate id or the scenario's source identity).</summary>
    public sealed record Prepared(int Cases, int Approved, bool SourceChanged, bool Removed, bool Conflict);

    public static Dictionary<string, Prepared> PreparedCases(IEnumerable<DiscoveredScenario> candidates, IEnumerable<TestPlanDraft> plans)
    {
        var all = plans.SelectMany(p => p.Cases).Where(c => c.Review is not (TestCaseReviewState.Excluded)).ToList();
        var result = new Dictionary<string, Prepared>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            var mine = all.Where(x => x.ParentScenario == c.CandidateId
                || x.Source.Kind == ScenarioSourceKind.ProjectDiscovery && c.SourceIdentity.Length > 0 && x.Generated.SourceIdentity == c.SourceIdentity).ToList();
            if (mine.Count == 0) continue;
            result[c.CandidateId] = new(mine.Count(x => x.Review != TestCaseReviewState.Superseded), mine.Count(x => x.Review == TestCaseReviewState.Accepted),
                mine.Any(x => x.SourceState == CaseSourceState.Changed), mine.All(x => x.Review == TestCaseReviewState.Superseded || x.SourceState == CaseSourceState.Removed),
                mine.Any(x => x.HasUnresolvedConflict));
        }
        return result;
    }

    public sealed record Status(string Key, string Text, string Tone);

    public static readonly IReadOnlyList<(string Key, string Label)> Statuses =
    [
        ("suggested", "Foreslått"), ("prepared", "Under arbeid"), ("approved", "Godkjent"), ("clarify", "Trenger avklaring"), ("changed", "Kildegrunnlag endret"),
    ];

    public static Status StatusOf(DiscoveredScenario c, Prepared? prepared)
    {
        if (c.Conflicts.Count > 0 || prepared?.Conflict == true) return new("clarify", "Trenger avklaring", "bad");
        if (prepared?.SourceChanged == true) return new("changed", "Kildegrunnlag endret", "warn");
        if (c.NeedsClarification || c.Gaps.Any(g => g.Kind == EvidenceGapKind.BusinessExpectationUnclear)) return new("clarify", "Trenger avklaring", "warn");
        if (prepared is { Approved: > 0 } p && p.Approved >= p.Cases) return new("approved", "Godkjent", "ok");
        if (prepared is { Cases: > 0 }) return new("prepared", "Under arbeid", "info");
        return new("suggested", "Foreslått", "muted");
    }

    // ── Collection ──────────────────────────────────────────────────────────────────────────────────────────

    public sealed record Item(DiscoveredScenario Candidate, ScenarioOwnershipInfo Owner, Relevance Relevance, string Title, string Area, Status Status, Prepared? Prepared,
        ScenarioWorkItem? Work = null)
    {
        private ExecutionSuitability? _suitability;
        /// <summary>How the scenario can be executed (computed once per item, from its own text; a recommendation, not a label).</summary>
        public ExecutionSuitability Suitability => _suitability ??= TestDesignAdvisor.Suitability(TestDesignAdvisor.FactsOf(Candidate));
        public string Id => Candidate.CandidateId;
        public string TypeLabel => ScenarioCandidateTypes.Label(Candidate.Type);
        public bool In(ScenarioWorkQueue queue) => Work?.Queues.Contains(queue) == true;
    }

    /// <summary>
    /// The collection from the server's work queues (<see cref="ScenarioWorkQueueView"/>): one dictionary lookup per scenario, no client-side join
    /// over plans and cases. Saved-case counts come from the queue view's coverage (every plan of the application, cases matched by identity).
    /// </summary>
    public static List<Item> Items(IReadOnlyList<DiscoveredScenario> candidates, IReadOnlyDictionary<string, ScenarioWorkItem> work)
    {
        var prepared = new Dictionary<string, Prepared>(StringComparer.Ordinal);
        foreach (var (id, w) in work)
            if (w.Coverage is { } k && (k.Current > 0 || k.SourceChanged > 0 || k.Conflicts > 0))
                prepared[id] = new Prepared(k.Current, k.Approved, k.SourceChanged > 0, false, k.Conflicts > 0);
        return [.. Items(candidates, prepared).Select(i => work.TryGetValue(i.Id, out var w) ? i with { Work = w } : i)];
    }

    // ── Work queues ─────────────────────────────────────────────────────────────────────────────────────────────

    public static readonly IReadOnlyList<(ScenarioWorkQueue? Queue, string Label)> Queues =
    [
        (ScenarioWorkQueue.Recommended, "Anbefalt å forberede"), (ScenarioWorkQueue.NeedsClarification, "Trenger avklaring"),
        (ScenarioWorkQueue.Prepared, "Allerede forberedt"), (ScenarioWorkQueue.SourceChanged, "Kildegrunnlag endret"), (null, "Alle scenarioer"),
    ];

    public static string QueueLabel(ScenarioWorkQueue? queue) => Queues.First(q => q.Queue == queue).Label;

    /// <summary>How many scenarios each queue holds under the other filters (search, area, type, status, story, requirement, technical content), and
    /// how many those filters show in all — one pass, computed when a filter changes (never per render).</summary>
    public static (Dictionary<ScenarioWorkQueue, int> Queues, int All) QueueCounts(IReadOnlyList<Item> items, Filter f)
    {
        var shown = Apply(items, f with { Queue = null }).Shown;
        var counts = Enum.GetValues<ScenarioWorkQueue>().ToDictionary(q => q, _ => 0);
        foreach (var i in shown)
            foreach (var q in i.Work?.Queues ?? [])
                counts[q]++;
        return (counts, shown.Count);
    }

    public static List<Item> Items(IReadOnlyList<DiscoveredScenario> candidates, IReadOnlyDictionary<string, Prepared> prepared) =>
        [.. candidates.Select(c =>
        {
            var owner = ScenarioOwnership.Of(c);
            prepared.TryGetValue(c.CandidateId, out var p);
            return new Item(c, owner, RelevanceOf(c, owner), DisplayTitle(c), string.IsNullOrWhiteSpace(c.Area) ? c.SourceCategory : c.Area!, StatusOf(c, p), p);
        }).OrderBy(i => i.Candidate.Story?.Ordinal ?? int.MaxValue).ThenBy(i => i.Candidate.NumberInStory ?? int.MaxValue).ThenBy(i => i.Area, StringComparer.CurrentCultureIgnoreCase)
          .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>All filters compose. <see cref="Queue"/> null = every scenario (Finn og forstå starts on <see cref="DefaultView"/>).</summary>
    public sealed record Filter(string Query = "", string? Area = null, string? Type = null, string? Status = null, string? Story = null, string? Requirement = null, bool Technical = false, bool All = false,
        ScenarioWorkQueue? Queue = null, string? Priority = null, ExecutionApproach? Approach = null);

    /// <summary>The priority filter value for scenarios whose user story declares no priority (never guessed).</summary>
    public const string NoPriority = "(none)";

    /// <summary>The declared priorities present (P1, P2 …, in order), plus <see cref="NoPriority"/> when some scenarios have none. Empty when no
    /// scenario declares one — then there is nothing to filter by.</summary>
    public static IReadOnlyList<string> Priorities(IReadOnlyList<Item> items)
    {
        var declared = items.Select(i => i.Candidate.Story?.Priority).OfType<string>().Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => int.TryParse(new string(p.Where(char.IsDigit).ToArray()), out var n) ? n : int.MaxValue).ThenBy(p => p, StringComparer.Ordinal).ToList();
        if (declared.Count > 0 && items.Any(i => string.IsNullOrEmpty(i.Candidate.Story?.Priority))) declared.Add(NoPriority);
        return declared;
    }

    /// <summary>Filters behind «Flere filtre» that are set (status, type, user story, requirement, priority).</summary>
    public static int AdvancedFilterCount(Filter f) =>
        new[] { f.Status, f.Type, f.Story, f.Requirement, f.Priority, f.Approach?.ToString() }.Count(v => v is not null);

    /// <summary>Whether search or any filter narrows the view (the queue and «Vis teknisk innhold» are views, not filters).</summary>
    public static bool HasFilters(Filter f) => f.Query.Length > 0 || f.Area is not null || AdvancedFilterCount(f) > 0;

    /// <summary>«Nullstill filtre»: search and filters cleared; the work queue and the technical-content preference stay as chosen.</summary>
    public static Filter ResetFilters(Filter f) => new(Technical: f.Technical, All: f.All, Queue: f.Queue);

    /// <summary>One short sentence per work queue (the rules are under «Hva betyr dette?»).</summary>
    public static string QueueHint(ScenarioWorkQueue? queue) => queue switch
    {
        ScenarioWorkQueue.Recommended => "Scenarioer med tydelig forventet resultat og uten forberedte testtilfeller.",
        ScenarioWorkQueue.NeedsClarification => "Scenarioer som må gjennomgås før testforberedelse.",
        ScenarioWorkQueue.Prepared => "Scenarioer med eksisterende testtilfeller.",
        ScenarioWorkQueue.SourceChanged => "Scenarioer som bør vurderes etter kildeendringer.",
        _ => "Viser alle scenarioer innenfor gjeldende filtre.",
    };

    /// <summary>Finn og forstå's default view: the work queue «Anbefalt å forberede» (technical content hidden).</summary>
    public static readonly Filter DefaultView = new(Queue: ScenarioWorkQueue.Recommended);

    /// <summary>The items the filter shows, and how many of the total the relevance filter alone hides (so "visible of total" is always honest).</summary>
    public static (List<Item> Shown, int HiddenTechnical) Apply(IReadOnlyList<Item> items, Filter f)
    {
        var words = f.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Match(Item i) =>
            (f.All || f.Technical || i.Relevance == Relevance.Manual)
            && (f.Area is null || i.Area == f.Area)
            && (f.Type is null || i.TypeLabel == f.Type)
            && (f.Status is null || i.Status.Key == f.Status)
            && (f.Story is null || i.Candidate.Story?.Key == f.Story)
            && (f.Requirement is null || i.Candidate.RequirementIds.Contains(f.Requirement, StringComparer.OrdinalIgnoreCase))
            && (f.Priority is null || (f.Priority == NoPriority ? string.IsNullOrEmpty(i.Candidate.Story?.Priority)
                : string.Equals(i.Candidate.Story?.Priority, f.Priority, StringComparison.OrdinalIgnoreCase)))
            && (f.Approach is not { } approach || i.Suitability.Has(approach))
            && words.All(w => $"{i.Title} {i.Candidate.Title} {i.Candidate.Purpose} {i.Area} {string.Join(' ', i.Candidate.RequirementIds)}".Contains(w, StringComparison.CurrentCultureIgnoreCase));
        bool InQueue(Item i) => f.Queue is not { } q || i.In(q);
        var shown = items.Where(i => InQueue(i) && Match(i)).ToList();
        // The recommended queue is a work order: the server's rank (declared priority, then support, then the specification's order).
        if (f.Queue == ScenarioWorkQueue.Recommended) shown = [.. shown.OrderBy(i => i.Work?.Rank ?? int.MaxValue)];
        var hidden = f.All || f.Technical ? 0 : items.Count(i => i.Relevance == Relevance.Technical && InQueue(i) && Match(i with { Relevance = Relevance.Manual }));
        return (shown, hidden);
    }

    /// <summary>How the expected result is supported, in plain Norwegian (never "verified" unless a reviewer confirmed it).</summary>
    public static string ExpectedBasis(DiscoveredScenario c) => c.ExpectedBehaviour is null ? "Ikke oppgitt i kilden — trenger avklaring."
        : c.Confidence switch
        {
            CandidateConfidence.ExplicitSpecification => $"Står i kilden ({c.ExpectedBasis ?? c.SourceCategory}). Bekreftes når testtilfellet godkjennes.",
            CandidateConfidence.ContractDerived => $"Dokumentert i API-kontrakten ({c.ExpectedBasis}).",
            CandidateConfidence.SourceDerived => "Foreslått fra kildekoden — implementasjonen er ikke en fasit; bekreft eller avklar.",
            CandidateConfidence.CandidateAssociation => "Fra en kilde som ikke er gjennomgått — må bekreftes.",
            _ => "Utilstrekkelig grunnlag — trenger avklaring.",
        };

    /// <summary>Why the scenario is suggested, in one sentence.</summary>
    public static string Why(Item i) => i.Owner.Basis;
}
