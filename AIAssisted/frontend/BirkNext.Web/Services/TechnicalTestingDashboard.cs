using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// Teknisk testoversikt (2026-11-28): the technical tester's figures over the SAME inventory and plans as Manual Testing — the project's last scenario
/// discovery (classified by <see cref="TestWorkClassifier"/> / <see cref="E2ERules"/>) and the saved cases of the shared test plans. Every figure has
/// the exact records behind it (candidate ids or plan/case ids), so a drilldown opens exactly what was counted. Nothing is executed, moved or
/// copied; a discovered or automated candidate is never an executed test. Pure; no I/O.
/// </summary>
public static class TechnicalTestingDashboard
{
    public const string E2E = "e2e", Api = "api", GraphQl = "graphql", Integration = "integration", Clarify = "clarify", Automated = "automated";
    public const string Prepared = "prepared", Approved = "approved", SourceAffected = "source";

    public sealed record Work(DiscoveredScenario Candidate, TestWorkClassification Classification);

    /// <param name="Key">Stable figure key (drilldown parameter).</param>
    /// <param name="Value">The count; equals <see cref="Candidates"/> or <see cref="Cases"/> count.</param>
    public sealed record Figure(string Key, string Label, int Value, string Detail, IReadOnlyList<Work> Candidates, IReadOnlyList<OverviewCase> Cases)
    {
        public bool OpensCases => Cases.Count > 0 || Key is Prepared or Approved or SourceAffected;
    }

    public sealed record Dashboard(IReadOnlyList<Figure> Discovered, IReadOnlyList<Figure> Prepared, IReadOnlyList<Work> All, IReadOnlyList<OverviewCase> TechnicalCases)
    {
        public Figure? Find(string key) => Discovered.Concat(Prepared).FirstOrDefault(f => f.Key == key);
    }

    /// <summary>One entry per scenario identity (the same operation in two contract files is one piece of work); candidates without an identity by id.</summary>
    public static IReadOnlyList<Work> Classify(IEnumerable<DiscoveredScenario> candidates, IReadOnlyDictionary<string, ScenarioTestKind>? reviewed = null) =>
        [.. candidates
            .GroupBy(c => c.SourceIdentity is { Length: > 0 } id ? id : "#" + c.CandidateId, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(c => new Work(c, TestWorkClassifier.Classify(c, reviewed is not null && c.SourceIdentity is { Length: > 0 } sid && reviewed.TryGetValue(sid, out var d) ? d : null)))];

    /// <summary>
    /// A saved case is technical work when its tester chose «Teknisk», or — when no kind was chosen — when the scenario it was prepared from (same
    /// source identity) is technical or hybrid work. A case chosen «Manuell» is never counted here.
    /// </summary>
    public static IReadOnlyList<OverviewCase> TechnicalCases(IEnumerable<OverviewCase> cases, IReadOnlyList<Work> work)
    {
        var technical = work.Where(w => w.Classification.TechnicalDefault && w.Candidate.SourceIdentity is { Length: > 0 })
            .Select(w => w.Candidate.SourceIdentity).ToHashSet(StringComparer.Ordinal);
        return [.. cases.Where(c => c.TestKind == ScenarioTestKind.Technical
            || c.TestKind is null && c.ScenarioIdentity is { } id && technical.Contains(id))];
    }

    public static Dashboard Build(IEnumerable<DiscoveredScenario> candidates, IEnumerable<OverviewCase> cases, IReadOnlyDictionary<string, ScenarioTestKind>? reviewed = null)
    {
        var all = Classify(candidates, reviewed);
        var technical = all.Where(w => w.Classification.TechnicalDefault).ToList();
        Figure C(string key, string label, string detail, Func<Work, bool> match)
        {
            var list = technical.Where(match).ToList();
            return new(key, label, list.Count, detail, list, []);
        }
        var discovered = new List<Figure>
        {
            C(E2E, "Tekniske E2E-flyter", "bekreftet og kandidat, teknisk eller begge", w => w.Classification.E2E.IsE2E),
            C(Api, "REST/API", "operasjoner i API-kontrakter", w => w.Classification.Category == TestWorkCategory.TechnicalApi),
            C(GraphQl, "GraphQL", "operasjoner i GraphQL-skjema", w => w.Classification.Category == TestWorkCategory.TechnicalGraphQl),
            C(Integration, "Integrasjon", "meldings- og systemintegrasjoner", w => w.Classification.Category == TestWorkCategory.TechnicalIntegration),
            // Only a test that names THIS scenario (a shared requirement is supporting evidence, never a link to the scenario).
            C(Automated, "Med automatisert test", "funnet test i kilden – ikke kjørt", w => w.Candidate.ExistingTests.Any(t => t.Origin == TestScenarioLinkOrigin.Scenario)),
            C(Clarify, "Må klassifiseres", "kilden avgjør ikke hvem som tester", w => w.Classification.Category == TestWorkCategory.NeedsClassification
                || w.Classification.E2E.Status == E2EStatus.NeedsClarification),
        };
        var technicalCases = TechnicalCases(cases, all);
        Figure K(string key, string label, string detail, Func<OverviewCase, bool> match)
        {
            var list = technicalCases.Where(match).ToList();
            return new(key, label, list.Count, detail, [], list);
        }
        var prepared = new List<Figure>
        {
            K(Prepared, "Tekniske testtilfeller", "lagret i testplanene", _ => true),
            K(Approved, "Godkjent", "av de tekniske testtilfellene", c => c.Review == TestCaseReviewState.Accepted),
            K(SourceAffected, "Påvirket av kildeendring", "endret, fjernet eller ikke sammenlignet", c => c.SourceState is CaseSourceState.Changed
                or CaseSourceState.Removed or CaseSourceState.NotCompared or CaseSourceState.SourceUnavailable),
        };
        return new(discovered, prepared, all, technicalCases);
    }

    /// <summary>The drilldown address of a candidate figure: Finn og analyser filtered to exactly that figure.</summary>
    public static string Href(string key) => $"{TechnicalTestingPresentation.AssessRoute}?work={Uri.EscapeDataString(key)}";

    public static string Label(TechnicalSurface surface) => surface switch
    {
        TechnicalSurface.Rest => "REST",
        TechnicalSurface.GraphQl => "GraphQL",
        TechnicalSurface.Messaging => "Meldinger",
        TechnicalSurface.Ui => "Brukergrensesnitt",
        _ => "–",
    };

    public static string Label(TestExecutionStyle style) => style switch
    {
        TestExecutionStyle.Manual => "Manuell",
        TestExecutionStyle.Technical => "Teknisk",
        _ => "Manuell og teknisk",
    };
}
