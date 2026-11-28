using BirkNext.TestPlanning;
using O = BirkNext.Web.Services.ManualTestingOverviewPresentation;

namespace BirkNext.Web.Services;

/// <summary>
/// Teknisk testoversikt and the manual E2E area of Testoversikt (Technical Testing V1/V2, 2026-11-28): figures over the SAME inventory and plans as
/// Manual Testing — the project's last scenario discovery (classified by <see cref="TestWorkClassifier"/> / <see cref="E2ERules"/>, with the testers'
/// explicit method choices) and the saved cases of the shared plans. Every figure carries the exact records behind it (candidate ids, or plan/case
/// ids), so a drilldown opens exactly what was counted. Unique flows (one per source identity), scenario candidates, prepared cases and recorded
/// results are separate figures; a discovered or automated candidate is never an executed test. Pure; no I/O.
/// </summary>
public static class TechnicalTestingDashboard
{
    public const string E2E = "e2e", E2EConfirmed = "e2e-confirmed", E2ECandidate = "e2e-candidate", Api = "api", GraphQl = "graphql", Integration = "integration",
        Clarify = "clarify", Automated = "automated", Deviation = "deviation";
    public const string Prepared = "prepared", Ready = "ready", Approved = "approved", SourceAffected = "source";
    public const string Executed = "executed", Passed = "passed", Failed = "failed", Blocked = "blocked";
    public const string ManualE2EConfirmed = "m-e2e-confirmed", ManualE2ECandidate = "m-e2e-candidate", ManualE2ECases = "m-e2e-cases", ManualE2EReady = "m-e2e-ready",
        ManualE2EMissing = "m-e2e-missing", ManualE2ESource = "m-e2e-source";

    /// <param name="Paths">The source files the candidate's evidence names (its specification, contract, test document …).</param>
    /// <param name="Modules">The modules of those files (canonical module rule); more than one = a cross-module flow.</param>
    public sealed record Work(DiscoveredScenario Candidate, TestWorkClassification Classification)
    {
        public IReadOnlyList<string> Paths { get; init; } = [];
        public IReadOnlyList<string> Modules { get; init; } = [];
        public bool CrossModule => Modules.Count > 1;
    }

    /// <param name="Value">The count; equals <see cref="Candidates"/> or <see cref="Cases"/> count.</param>
    public sealed record Figure(string Key, string Label, int Value, string Detail, IReadOnlyList<Work> Candidates, IReadOnlyList<OverviewCase> Cases, bool OfCases = false)
    {
        public bool OpensCases => OfCases;
    }

    public sealed record Dashboard(IReadOnlyList<Figure> Discovered, IReadOnlyList<Figure> Prepared, IReadOnlyList<Figure> Execution, IReadOnlyList<Work> All,
        IReadOnlyList<OverviewCase> TechnicalCases)
    {
        public Figure? Find(string key) => Discovered.Concat(Prepared).Concat(Execution).FirstOrDefault(f => f.Key == key);
    }

    // ── Method decisions (precedence: evidence → rules → the person's choice) ───────────────────────────────────────────────────────────────────

    /// <summary>The newest explicit choice per scenario source identity (a legacy choice without an identity is not applied to anything).</summary>
    public static IReadOnlyDictionary<string, ScenarioTestKind> Decisions(IEnumerable<OverviewMethodDecision> decisions) =>
        decisions.Where(d => d.SourceIdentity is not null).GroupBy(d => d.SourceIdentity!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(d => d.At).Last().Kind, StringComparer.Ordinal);

    // ── Source attribution and scope ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The source files a candidate's evidence names: its user story's specification, then the path part of each evidence reference
    /// («file:line#op», «file#id»). Never a title.</summary>
    public static IReadOnlyList<string> PathsOf(DiscoveredScenario c)
    {
        var list = new List<string>();
        if (c.Story?.SpecificationPath is { Length: > 0 } spec) list.Add(spec);
        foreach (var e in c.Evidence)
        {
            var r = e.Reference;
            if (string.IsNullOrWhiteSpace(r)) continue;
            var hash = r.IndexOf('#');
            var path = hash > 0 ? r[..hash] : r;
            var colon = path.LastIndexOf(':');
            if (colon > 1 && path[(colon + 1)..].All(char.IsDigit)) path = path[..colon];
            if (path.Contains('/') || path.Contains('.')) list.Add(path.Replace('\\', '/'));
        }
        return [.. list.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>One entry per scenario identity (the same operation in two contract files is one piece of work), classified with the person's choice,
    /// attributed to its source files and modules by the canonical module rule.</summary>
    public static IReadOnlyList<Work> Classify(IEnumerable<DiscoveredScenario> candidates, IReadOnlyDictionary<string, ScenarioTestKind>? reviewed = null,
        IEnumerable<string>? knownSpecificationPaths = null)
    {
        var unique = candidates.GroupBy(c => c.SourceIdentity is { Length: > 0 } id ? id : "#" + c.CandidateId, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var paths = unique.ToDictionary(c => c.CandidateId, PathsOf, StringComparer.Ordinal);
        var roots = new HashSet<string>(ModuleStructure.RootsOf(paths.Values.SelectMany(p => p).Concat(knownSpecificationPaths ?? []).Select(p => (string?)p)), StringComparer.Ordinal);
        string ModuleFor(string path) { if (ModuleStructure.StrongRoot(path) is { } own) roots.Add(own); return ModuleStructure.OverviewModule(path, roots); }
        return [.. unique.Select(c =>
        {
            var own = paths[c.CandidateId];
            var decided = reviewed is not null && c.SourceIdentity is { Length: > 0 } sid && reviewed.TryGetValue(sid, out var d) ? d : (ScenarioTestKind?)null;
            return new Work(c, TestWorkClassifier.Classify(c, decided))
            {
                Paths = own,
                Modules = own.Count == 0 ? [ModuleStructure.Unassigned] : [.. own.Select(ModuleFor).Distinct(StringComparer.Ordinal)],
            };
        })];
    }

    /// <summary>
    /// Scope membership of a candidate, with the same scope contract as Testoversikt (<see cref="O.Scope"/>): project = all; module = any of its
    /// files lies in the module (a cross-module flow belongs to EVERY module it touches, is counted once per scope and is marked as cross-module —
    /// never as owned by one module); specification pack = any file of that pack (in the module, when one is chosen); document = that file exactly.
    /// </summary>
    public static bool InScope(Work w, O.Scope scope)
    {
        string Name(string? module) => string.IsNullOrEmpty(module) ? ModuleStructure.ProjectWide : module;
        bool InModule() => scope.Module is null || w.Modules.Contains(Name(scope.Module), StringComparer.Ordinal);
        return scope.Level switch
        {
            O.Level.Module => w.Modules.Contains(Name(scope.Module), StringComparer.Ordinal),
            O.Level.Specification when scope.SpecificationPath is { } doc => InModule() && w.Paths.Any(p => ManualTestingDrillNavigation.SamePath(p, doc)),
            O.Level.Specification when scope.SpecificationPack is { } pack => InModule()
                && w.Paths.Any(p => string.Equals(Explorers.ArtifactModules.FeatureOf(p), pack, StringComparison.OrdinalIgnoreCase)),
            O.Level.Specification => false,
            _ => true,
        };
    }

    // ── Populations ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Technical cases: a case typed «Teknisk»; or — without a type of its own — a case whose scenario (same source identity) is technical or hybrid
    /// work (including a person's «Teknisk»/«Begge» choice). A case typed «Manuell» is never technical work. Each physical case counts once.
    /// </summary>
    public static IReadOnlyList<OverviewCase> TechnicalCases(IEnumerable<OverviewCase> cases, IReadOnlyList<Work> work)
    {
        var technical = work.Where(w => w.Classification.TechnicalDefault && w.Candidate.SourceIdentity is { Length: > 0 })
            .Select(w => w.Candidate.SourceIdentity).ToHashSet(StringComparer.Ordinal);
        return [.. cases.Where(c => c.TestKind == ScenarioTestKind.Technical || c.TestKind is null && c.ScenarioIdentity is { } id && technical.Contains(id))
            .DistinctBy(c => (c.PlanId, c.CaseId))];
    }

    private static bool Affected(OverviewCase c) => c.SourceState is CaseSourceState.Changed or CaseSourceState.Removed or CaseSourceState.NotCompared or CaseSourceState.SourceUnavailable;

    /// <param name="cases">The cases of the scope (the same slice Testoversikt uses: <see cref="O.Of"/>).</param>
    public static Dashboard Build(IEnumerable<DiscoveredScenario> candidates, IEnumerable<OverviewCase> cases, IReadOnlyDictionary<string, ScenarioTestKind>? reviewed = null,
        O.Scope? scope = null, IEnumerable<string>? knownSpecificationPaths = null)
    {
        var all = Classify(candidates, reviewed, knownSpecificationPaths);
        var inScope = scope is null ? all : [.. all.Where(w => InScope(w, scope))];
        var technical = inScope.Where(w => w.Classification.TechnicalDefault).ToList();
        Figure C(string key, string label, string detail, Func<Work, bool> match)
        {
            var list = technical.Where(match).ToList();
            return new(key, label, list.Count, detail, list, []);
        }
        var discovered = new List<Figure>
        {
            C(E2EConfirmed, "Bekreftede tekniske E2E-flyter", "startpunkt, flere systemer, utfall og kilde", w => w.Classification.E2E.Status == E2EStatus.Confirmed),
            C(E2ECandidate, "E2E-kandidater", "erklært eller antydet – må vurderes", w => w.Classification.E2E.Status == E2EStatus.Candidate),
            C(Api, "REST/API", "operasjoner i API-kontrakter", w => w.Classification.Category == TestWorkCategory.TechnicalApi),
            C(GraphQl, "GraphQL", "operasjoner i GraphQL-skjema", w => w.Classification.Category == TestWorkCategory.TechnicalGraphQl),
            C(Integration, "Integrasjon", "meldings- og systemintegrasjoner", w => w.Classification.Category == TestWorkCategory.TechnicalIntegration),
            // Only a test that names THIS scenario (a shared requirement is supporting evidence, never a link to the scenario).
            C(Automated, "Automatisert test finnes", "i kilden – ikke kjørt", w => w.Candidate.ExistingTests.Any(t => t.Origin == TestScenarioLinkOrigin.Scenario)),
            C(Clarify, "Må klassifiseres", "kilden avgjør ikke hvem som tester", w => w.Classification.Category == TestWorkCategory.NeedsClassification
                || w.Classification.E2E.Status == E2EStatus.NeedsClarification),
            C(Deviation, "Valg avviker fra forslag", "personens valg gjelder", w => w.Classification.E2E.Deviates),
        };
        var technicalCases = TechnicalCases(cases, all);
        Figure K(string key, string label, string detail, Func<OverviewCase, bool> match)
        {
            var list = technicalCases.Where(match).ToList();
            return new(key, label, list.Count, detail, [], list, OfCases: true);
        }
        var prepared = new List<Figure>
        {
            K(Prepared, "Tekniske testtilfeller", "lagret i testplanene", _ => true),
            K(Ready, "Klare", "klare til kjøring", c => c.Ready),
            K(Approved, "Godkjent", "av de tekniske testtilfellene", c => c.Review == TestCaseReviewState.Accepted),
            K(SourceAffected, "Påvirket av kildeendring", "endret, fjernet eller ikke sammenlignet", Affected),
        };
        var execution = new List<Figure>
        {
            K(Executed, "Kjørt", "registrert resultat", c => c.Execution is ScenarioExecutionStatus.Passed or ScenarioExecutionStatus.Failed or ScenarioExecutionStatus.Blocked),
            K(Passed, "Bestått", "siste resultat", c => c.Execution == ScenarioExecutionStatus.Passed),
            K(Failed, "Feilet", "siste resultat", c => c.Execution == ScenarioExecutionStatus.Failed),
            K(Blocked, "Blokkert", "siste resultat", c => c.Execution == ScenarioExecutionStatus.Blocked),
        };
        return new(discovered, prepared, execution, all, technicalCases);
    }

    /// <summary>
    /// The manual E2E area of Testoversikt: end-to-end flows a person executes (manual or both), by certainty — confirmed flows never include
    /// unreviewed candidates — and the manual population's cases of those flows. <paramref name="manualCases"/> is the manual population of the scope.
    /// </summary>
    public static IReadOnlyList<Figure> ManualE2E(IEnumerable<DiscoveredScenario> candidates, IEnumerable<OverviewCase> manualCases,
        IReadOnlyDictionary<string, ScenarioTestKind>? reviewed = null, O.Scope? scope = null, IEnumerable<string>? knownSpecificationPaths = null)
    {
        var flows = Classify(candidates, reviewed, knownSpecificationPaths)
            .Where(w => w.Classification.ManualDefault && w.Classification.E2E.IsE2E && (scope is null || InScope(w, scope))).ToList();
        var identities = flows.Select(w => w.Candidate.SourceIdentity).Where(i => i.Length > 0).ToHashSet(StringComparer.Ordinal);
        var cases = manualCases.Where(c => c.ScenarioIdentity is { } id && identities.Contains(id)).DistinctBy(c => (c.PlanId, c.CaseId)).ToList();
        var withCases = cases.Select(c => c.ScenarioIdentity!).ToHashSet(StringComparer.Ordinal);
        Figure F(string key, string label, string detail, IEnumerable<Work> list) { var l = list.ToList(); return new(key, label, l.Count, detail, l, []); }
        Figure K(string key, string label, string detail, Func<OverviewCase, bool> match) { var l = cases.Where(match).ToList(); return new(key, label, l.Count, detail, [], l, OfCases: true); }
        return
        [
            F(ManualE2EConfirmed, "Bekreftede E2E-flyter", "manuell eller begge", flows.Where(w => w.Classification.E2E.Status == E2EStatus.Confirmed)),
            F(ManualE2ECandidate, "E2E-kandidater", "må vurderes", flows.Where(w => w.Classification.E2E.Status == E2EStatus.Candidate)),
            F(ManualE2EMissing, "Flyter uten testtilfelle", "bekreftede og kandidater", flows.Where(w => !withCases.Contains(w.Candidate.SourceIdentity))),
            K(ManualE2ECases, "E2E-testtilfeller", "forberedt", _ => true),
            K(ManualE2EReady, "Klare", "av E2E-testtilfellene", c => c.Ready),
            K(ManualE2ESource, "Påvirket av kildeendring", "av E2E-testtilfellene", Affected),
        ];
    }

    /// <summary>The drilldown address of a technical candidate figure: Finn og analyser filtered to exactly that figure (in the shared scope).</summary>
    public static string Href(string key) => $"{TechnicalTestingPresentation.AssessRoute}?work={Uri.EscapeDataString(key)}";

    public static string Label(TechnicalSurface surface) => surface switch
    {
        TechnicalSurface.Rest => "REST",
        TechnicalSurface.GraphQl => "GraphQL",
        TechnicalSurface.Messaging => "Meldinger",
        TechnicalSurface.Ui => "Brukergrensesnitt",
        _ => "–",
    };

    public static string Label(TestExecutionStyle style) => E2ERules.Label(style);
}
