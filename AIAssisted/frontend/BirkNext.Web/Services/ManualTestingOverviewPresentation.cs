using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// Testoversikt (2026-11-25): the figures of one scope — the whole project, one module or one specification — from the server's read-only overview.
/// One set of definitions for every card, chart and table, so they never contradict each other:
/// <list type="bullet">
/// <item>Scenarioforslag: unique scenarios (by source identity) of the specifications in scope; unknown (null) when no specification is stored.</item>
/// <item>Testtilfeller: saved cases of active plans, excluded and superseded left out, attributed by their own specification (never the selection).</item>
/// <item>Dekket scenario: a scenario with at least one saved case for the same identity in the same specification (five cases = one covered scenario).</item>
/// <item>Godkjent: review state Accepted. Klar til testing: the testability check says ready. They overlap and are never added together.</item>
/// </list>
/// Never execution: no «Gjennomført», «Bestått» or «Feilet».
/// </summary>
public static class ManualTestingOverviewPresentation
{
    public enum Level { Project, Module, Specification }

    public sealed record Scope(Level Level, string? Module, string? SpecificationPath)
    {
        public static readonly Scope Project = new(Level.Project, null, null);
    }

    public sealed record Figures(int? Scenarios, int? Covered, int Cases, int Approved, int Ready, int NeedsClarification, int Plans);

    /// <summary>One row of the overview table. <see cref="Scenarios"/> and <see cref="Covered"/> count acceptance scenarios and documented tests only
    /// (decision 2026-11-27); requirements without an acceptance scenario are kept in the data and shown separately in <see cref="Requirements"/>.</summary>
    public sealed record Row(string Key, string Label, int? Scenarios, int? Covered, int Cases, int Approved, int FollowUp, string? Module, string? SpecificationPath)
    {
        /// <summary>Requirements without an acceptance scenario in the row's documents (null when not available).</summary>
        public int? Requirements { get; init; }
    }

    public sealed record Slice(IReadOnlyList<OverviewSpecification> Specifications, IReadOnlyList<OverviewCase> Cases, bool ScenariosAvailable)
    {
        /// <summary>The user stories of the scope with their task status (2026-11-26); empty when the status was not computed.</summary>
        public IReadOnlyList<OverviewStoryStatus> Stories { get; init; } = [];
        /// <summary>False when the project's import holds no task lines (story figures are then unavailable, never zero).</summary>
        public bool StoriesAvailable { get; init; }
    }

    public static string ScopeLabel(Scope s) => s.Level switch
    {
        Level.Module => $"Valgt modul: {s.Module}",
        Level.Specification => $"Valgt spesifikasjon: {SpecName(s.SpecificationPath)}",
        _ => "Hele prosjektet",
    };

    public static string SpecName(string? path)
    {
        var parts = (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? (parts.Length >= 3 && parts[1].Equals("specs", StringComparison.OrdinalIgnoreCase) ? $"{parts[0]} / {parts[^2]}" : parts[^2]) : path ?? "";
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Replace('\\', '/').Trim('/'), b.Replace('\\', '/').Trim('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The specifications and cases of a scope. A case outside every stored specification still counts in the project and its module.</summary>
    public static Slice Of(ManualTestingOverview o, Scope s) => s.Level switch
    {
        Level.Module => new([.. o.Specifications.Where(x => x.Module == s.Module)], [.. o.Cases.Where(c => c.Module == s.Module)], o.ScenariosAvailable)
            { Stories = [.. o.Stories.Where(x => x.Module == s.Module)], StoriesAvailable = o.StoryStatusAvailable },
        Level.Specification => new([.. o.Specifications.Where(x => SamePath(x.Path, s.SpecificationPath))], [.. o.Cases.Where(c => SamePath(c.SpecificationPath, s.SpecificationPath))], o.ScenariosAvailable)
            { Stories = [.. o.Stories.Where(x => SamePath(x.SpecificationPath, s.SpecificationPath))], StoriesAvailable = o.StoryStatusAvailable },
        _ => new(o.Specifications, o.Cases, o.ScenariosAvailable) { Stories = o.Stories, StoriesAvailable = o.StoryStatusAvailable },
    };

    /// <summary>Scenario identities with at least one saved case in the same specification.</summary>
    private static HashSet<string> CoveredKeys(IEnumerable<OverviewCase> cases) =>
        [.. cases.Where(c => c.ScenarioIdentity is not null).Select(c => Key(c.SpecificationPath, c.ScenarioIdentity!))];

    private static string Key(string? path, string identity) => $"{(path ?? "").Replace('\\', '/').Trim('/').ToLowerInvariant()}|{identity}";

    private static bool IsCovered(OverviewSpecification spec, OverviewScenario sc, HashSet<string> covered) => covered.Contains(Key(spec.Path, sc.Identity));

    public static Figures Compute(Slice s, IReadOnlyCollection<OverviewPlan> plans)
    {
        var covered = CoveredKeys(s.Cases);
        int? scenarios = s.ScenariosAvailable ? s.Specifications.Sum(x => x.Scenarios.Count) : null;
        int? coveredCount = s.ScenariosAvailable ? s.Specifications.Sum(x => x.Scenarios.Count(sc => IsCovered(x, sc, covered))) : null;
        var planIds = s.Cases.Select(c => c.PlanId).ToHashSet();
        return new(scenarios, coveredCount, s.Cases.Count, s.Cases.Count(c => c.Review == TestCaseReviewState.Accepted), s.Cases.Count(c => c.Ready),
            s.Cases.Count(c => c.Review == TestCaseReviewState.NeedsClarification), plans.Count(p => planIds.Contains(p.Id)));
    }

    private static DeliveryBar Bar(string key, string label, int value, DeliveryDashboardTone tone, string info) => new(key, label, value, tone, info);

    private static readonly System.Globalization.CultureInfo Nb = System.Globalization.CultureInfo.GetCultureInfo("nb-NO");

    /// <summary>One display policy for every share on Testoversikt (2026-11-26): «0 %» only when nothing is counted; under 1 % with one decimal
    /// («0,5 %» for 6 av 1 218); never «100 %» while something is missing («99,9 %»); otherwise whole percent.</summary>
    public static string Share(int part, int whole)
    {
        if (whole <= 0 || part <= 0) return "0 %";
        if (part >= whole) return "100 %";
        var pct = 100.0 * part / whole;
        return pct < 1 ? $"{Math.Max(0.1, Math.Round(pct, 1)).ToString("0.0", Nb)} %"
            : pct >= 99.5 ? $"{Math.Min(99.9, Math.Floor(pct * 10) / 10).ToString("0.0", Nb)} %"
            : $"{Math.Round(pct).ToString("0", Nb)} %";
    }

    /// <summary>Testforberedelse: overlapping milestones over the saved cases (never stacked, never added).</summary>
    public static IReadOnlyList<DeliveryBar> Preparation(Slice s)
    {
        if (s.Cases.Count == 0) return [];
        var n = s.Cases.Count;
        return
        [
            Bar("created", "Opprettet", n, DeliveryDashboardTone.Progress, $"{n} lagrede testtilfeller"),
            Bar("steps", "Med teststeg", s.Cases.Count(c => !c.MissingSteps), DeliveryDashboardTone.Progress, $"{s.Cases.Count(c => !c.MissingSteps)} av {n} har minst ett teststeg"),
            Bar("approved", "Godkjent", s.Cases.Count(c => c.Review == TestCaseReviewState.Accepted), DeliveryDashboardTone.Ready, $"{s.Cases.Count(c => c.Review == TestCaseReviewState.Accepted)} av {n} er godkjent"),
            Bar("ready", "Klar til testing", s.Cases.Count(c => c.Ready), DeliveryDashboardTone.Ready, $"{s.Cases.Count(c => c.Ready)} av {n} er klare til testing"),
        ];
    }

    /// <summary>Testdekning: per module (project), per specification (module) or per user story (specification) — percent of scenarios with a case.</summary>
    public static IReadOnlyList<DeliveryBar> Coverage(Slice s, Level level)
    {
        if (!s.ScenariosAvailable) return [];
        var covered = CoveredKeys(s.Cases);
        IEnumerable<(string Key, string Label, int Total, int Covered)> groups = level switch
        {
            // Acceptance scenarios and documented tests only (decision 2026-11-27): requirements without a scenario are not scenarios.
            Level.Project => s.Specifications.GroupBy(x => x.Module).Select(g => (g.Key, g.Key, g.Sum(x => x.Scenarios.Count(sc => sc.IsAcceptanceScenario)),
                g.Sum(x => x.Scenarios.Count(sc => sc.IsAcceptanceScenario && IsCovered(x, sc, covered))))),
            Level.Module => s.Specifications.Select(x => (x.Path, x.Title, x.Scenarios.Count(sc => sc.IsAcceptanceScenario), x.Scenarios.Count(sc => sc.IsAcceptanceScenario && IsCovered(x, sc, covered)))),
            _ => s.Specifications.SelectMany(x => x.Scenarios.Where(sc => sc.IsAcceptanceScenario).Select(sc => (Spec: x, Sc: sc))).GroupBy(t => t.Sc.StoryId ?? "Uten brukerhistorie")
                .Select(g => (g.Key, g.Key, g.Count(), g.Count(t => IsCovered(t.Spec, t.Sc, covered)))),
        };
        return [.. groups.Where(g => g.Total > 0).OrderBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => Bar(g.Key, $"{g.Label} ({g.Covered}/{g.Total})", (int)Math.Round(100.0 * g.Covered / g.Total), g.Covered == g.Total ? DeliveryDashboardTone.Ready : DeliveryDashboardTone.Progress,
                $"{g.Covered} av {g.Total} scenarioer har testtilfeller") with { ValueText = Share(g.Covered, g.Total) })];
    }

    private static readonly string[] Priorities = ["P1", "P2", "P3"];

    /// <summary>Every priority the source states, in order (P1, P2, P3 always first, then any other the specifications use, e.g. P4–P7), so the
    /// priority buckets plus «Ikke prioritert» always add up to the scenario count (2026-11-26: P4–P7 stories were dropped from the chart).</summary>
    public static IReadOnlyList<string> PriorityKeys(IEnumerable<OverviewScenario> scenarios) =>
        [.. Priorities.Concat(scenarios.Select(sc => sc.Priority).OfType<string>()).Distinct(StringComparer.Ordinal)
            .OrderBy(p => int.TryParse(p.AsSpan(1), out var n) ? n : 99).ThenBy(p => p, StringComparer.Ordinal)];

    /// <summary>Scenarios per stated priority; «Ikke prioritert» when the source states none.</summary>
    public static IReadOnlyList<DeliveryBar> Priority(Slice s)
    {
        if (!s.ScenariosAvailable) return [];
        var all = s.Specifications.SelectMany(x => x.Scenarios).ToList();
        return [.. PriorityKeys(all).Select(p => (p, all.Count(sc => sc.Priority == p))).Append(("Ikke prioritert", all.Count(sc => sc.Priority is null)))
            .Where(x => x.Item2 > 0).Select(x => Bar(x.Item1, x.Item1, x.Item2, x.Item1 == "P1" ? DeliveryDashboardTone.Change : DeliveryDashboardTone.Neutral, $"{x.Item2} scenarioer"))];
    }

    /// <summary>Manglende testdekning: scenarios without a saved case, by priority (outstanding work, unlike the coverage level above).</summary>
    public static IReadOnlyList<DeliveryBar> Gaps(Slice s)
    {
        if (!s.ScenariosAvailable) return [];
        var covered = CoveredKeys(s.Cases);
        var open = s.Specifications.SelectMany(x => x.Scenarios.Where(sc => !IsCovered(x, sc, covered))).ToList();
        return [.. PriorityKeys(open).Select(p => (p, open.Count(sc => sc.Priority == p))).Append(("Ikke prioritert", open.Count(sc => sc.Priority is null)))
            .Where(x => x.Item2 > 0).Select(x => Bar(x.Item1, $"{x.Item1} uten testtilfeller", x.Item2, x.Item1 == "P1" ? DeliveryDashboardTone.Blocker : DeliveryDashboardTone.Review, $"{x.Item2} scenarioer uten lagret testtilfelle"))];
    }

    /// <summary>What stands in the way: one case can have several reasons (the bars overlap); <see cref="NotReady"/> is the distinct count.</summary>
    public static IReadOnlyList<DeliveryBar> Blockers(Slice s)
    {
        var c = s.Cases;
        return [.. new[]
        {
            ("hindrance", "Hindring registrert", c.Count(x => x.Hindrance), DeliveryDashboardTone.Blocker),
            ("clarify", "Trenger avklaring", c.Count(x => x.Review == TestCaseReviewState.NeedsClarification), DeliveryDashboardTone.Review),
            ("expected", "Forventet resultat må avklares", c.Count(x => x.MissingExpected), DeliveryDashboardTone.Review),
            ("steps", "Mangler teststeg", c.Count(x => x.MissingSteps), DeliveryDashboardTone.Review),
            ("data", "Uten koblet testdata", c.Count(x => x.NoTestData), DeliveryDashboardTone.Neutral),
        }.Where(x => x.Item3 > 0).Select(x => Bar(x.Item1, x.Item2, x.Item3, x.Item4, $"{x.Item3} av {c.Count} testtilfeller"))];
    }

    public static int NotReady(Slice s) => s.Cases.Count(c => !c.Ready);

    /// <summary>Kildeendringer: cases whose own source needs attention, by verified state (another selected specification is never «missing»).</summary>
    public static IReadOnlyList<DeliveryBar> SourceChanges(Slice s) =>
    [.. new[]
        {
            ("changed", "Kilden er endret", s.Cases.Count(c => c.SourceState == CaseSourceState.Changed), DeliveryDashboardTone.Review),
            ("removed", "Kilden er fjernet", s.Cases.Count(c => c.SourceState == CaseSourceState.Removed), DeliveryDashboardTone.Blocker),
            ("unavailable", "Kunne ikke sammenlignes", s.Cases.Count(c => c.SourceState == CaseSourceState.SourceUnavailable), DeliveryDashboardTone.Neutral),
        }.Where(x => x.Item3 > 0).Select(x => Bar(x.Item1, x.Item2, x.Item3, x.Item4, $"{x.Item3} testtilfeller"))];

    /// <summary>Saved cases by their chosen test type (the persisted classification; «Ikke angitt» when none was chosen).</summary>
    public static IReadOnlyList<DeliveryBar> ManualTechnical(Slice s) =>
    [.. new[]
        {
            ("manual", "Manuell", s.Cases.Count(c => c.TestKind == ScenarioTestKind.Manual)),
            ("technical", "Teknisk", s.Cases.Count(c => c.TestKind == ScenarioTestKind.Technical)),
            ("unset", "Ikke angitt", s.Cases.Count(c => c.TestKind is null or ScenarioTestKind.Both)),
        }.Where(x => x.Item3 > 0).Select(x => Bar(x.Item1, x.Item2, x.Item3, DeliveryDashboardTone.Neutral, $"{x.Item3} testtilfeller"))];

    /// <summary>Saved and approved cases per module (counts, not a comparable percentage).</summary>
    public static IReadOnlyList<DeliveryBar> ModuleApproval(Slice s) =>
    [.. s.Cases.GroupBy(c => c.Module).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
        .Select(g => Bar(g.Key, $"{g.Key} ({g.Count(c => c.Review == TestCaseReviewState.Accepted)}/{g.Count()})", g.Count(c => c.Review == TestCaseReviewState.Accepted),
            DeliveryDashboardTone.Ready, $"{g.Count(c => c.Review == TestCaseReviewState.Accepted)} av {g.Count()} godkjent"))];

    /// <summary>The breakdown table: modules (project), specifications (module) or user stories (specification). Cases without a module get their own row.</summary>
    /// <summary>0 for a real module; 1 for the project's own specifications (<see cref="ModuleStructure.ProjectWide"/>); 2 for documents outside every
    /// module (<see cref="ModuleStructure.Unassigned"/>).</summary>
    public static int OutsideModulesRank(string? module) => module switch { ModuleStructure.ProjectWide => 1, ModuleStructure.Unassigned => 2, _ => 0 };

    /// <summary>Why the scenarios of a group outside the modules are there, in the tester's words (null for a real module).</summary>
    public static string? OutsideModulesWhy(string? module) => module switch
    {
        ModuleStructure.ProjectWide => "Prosjektets egne spesifikasjoner (specs/ på rotnivå). De gjelder hele prosjektet, ikke én modul.",
        ModuleStructure.Unassigned => "Dokumenter som ligger utenfor alle modulmappene (for eksempel docs/analysis). De har ingen modultilknytning i kilden.",
        _ => null,
    };

    public static IReadOnlyList<Row> Rows(Slice s, Level level)
    {
        var covered = CoveredKeys(s.Cases);
        static int FollowUp(IEnumerable<OverviewCase> cs) => cs.Count(c => !c.Ready || c.Review == TestCaseReviewState.NeedsClarification
            || c.SourceState is CaseSourceState.Changed or CaseSourceState.Removed or CaseSourceState.SourceUnavailable);
        switch (level)
        {
            case Level.Project:
                // Project-level audit (2026-11-27): real modules first; then the two groups outside every module, never mixed in as a module.
                var modules = s.Specifications.Select(x => x.Module).Concat(s.Cases.Select(c => c.Module)).Distinct(StringComparer.Ordinal)
                    .OrderBy(OutsideModulesRank).ThenBy(m => m, StringComparer.CurrentCultureIgnoreCase);
                return [.. modules.Select(m =>
                {
                    var specs = s.Specifications.Where(x => x.Module == m).ToList(); var cs = s.Cases.Where(c => c.Module == m).ToList();
                    return new Row(m, m, s.ScenariosAvailable ? specs.Sum(x => x.Scenarios.Count(sc => sc.IsAcceptanceScenario)) : null,
                        s.ScenariosAvailable ? specs.Sum(x => x.Scenarios.Count(sc => sc.IsAcceptanceScenario && IsCovered(x, sc, covered))) : null,
                        cs.Count, cs.Count(c => c.Review == TestCaseReviewState.Accepted), FollowUp(cs), m, null)
                        { Requirements = s.ScenariosAvailable ? specs.Sum(x => x.Scenarios.Count(sc => sc.RequirementOnly)) : null };
                })];
            case Level.Module:
                return [.. s.Specifications.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).Select(x =>
                {
                    var cs = s.Cases.Where(c => SamePath(c.SpecificationPath, x.Path)).ToList();
                    return new Row(x.Path, x.Title, x.Scenarios.Count(sc => sc.IsAcceptanceScenario), x.Scenarios.Count(sc => sc.IsAcceptanceScenario && IsCovered(x, sc, covered)), cs.Count,
                        cs.Count(c => c.Review == TestCaseReviewState.Accepted), FollowUp(cs), x.Module, x.Path) { Requirements = x.Scenarios.Count(sc => sc.RequirementOnly) };
                })];
            default:
                var scenarios = s.Specifications.SelectMany(x => x.Scenarios.Select(sc => (Spec: x, Sc: sc))).ToList();
                return [.. scenarios.GroupBy(t => t.Sc.StoryKey ?? "").OrderBy(g => g.First().Sc.StoryId ?? "~", StringComparer.Ordinal).Select(g =>
                {
                    var cs = s.Cases.Where(c => (c.StoryKey ?? "") == g.Key).ToList();
                    var label = g.Key.Length == 0 ? "Uten brukerhistorie" : $"{g.First().Sc.StoryId} {g.First().Sc.StoryTitle}";
                    return new Row(g.Key, label, g.Count(), g.Count(t => IsCovered(t.Spec, t.Sc, covered)), cs.Count, cs.Count(c => c.Review == TestCaseReviewState.Accepted), FollowUp(cs), null, null);
                })];
        }
    }

    /// <summary>Viktig å følge opp: P1 work, as counts with what they mean.</summary>
    public static IReadOnlyList<(string Key, string Text)> P1Attention(Slice s)
    {
        var covered = CoveredKeys(s.Cases);
        var list = new List<(string, string)>();
        if (s.ScenariosAvailable)
        {
            var open = s.Specifications.Sum(x => x.Scenarios.Count(sc => sc.Priority == "P1" && !IsCovered(x, sc, covered)));
            if (open > 0) list.Add(("p1-uncovered", $"{open} P1-scenario{(open == 1 ? "" : "er")} mangler testtilfeller"));
        }
        var p1 = s.Cases.Where(c => c.Priority == "P1").ToList();
        var waiting = p1.Count(c => c.Review != TestCaseReviewState.Accepted);
        if (waiting > 0) list.Add(("p1-approval", $"{waiting} P1-testtilfelle{(waiting == 1 ? "" : "r")} venter på godkjenning"));
        var notReady = p1.Count(c => !c.Ready);
        if (notReady > 0) list.Add(("p1-ready", $"{notReady} P1-testtilfelle{(notReady == 1 ? "" : "r")} er ikke klare til testing"));
        var source = p1.Count(c => c.SourceState is CaseSourceState.Changed or CaseSourceState.Removed);
        if (source > 0) list.Add(("p1-source", $"{source} P1-testtilfelle{(source == 1 ? "" : "r")} må gjennomgås etter kildeendring"));
        return list;
    }

    // ── Drill-down (2026-11-26) ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One scenario of a drill-down, with the specification it belongs to.</summary>
    public sealed record DrillScenario(OverviewSpecification Specification, OverviewScenario Scenario, bool Covered);

    // ── Follow-up workspace (UX-107, 2026-11-27) ──

    /// <summary>The tester's search and filters in a follow-up list (kept per tab and project; restored on return).</summary>
    public sealed record FollowUpFilter(string Query = "", string? Module = null, string? Specification = null)
    {
        public bool Active => Query.Trim().Length > 0 || Module is not null || Specification is not null;
    }

    /// <summary>How a scenario is named in a follow-up row: «US3 · AC 2» for an acceptance scenario; a documented test carries its id in its title.</summary>
    public static string ScenarioLabel(OverviewScenario sc) => sc.StoryId is { } st ? $"{st}{(sc.Number is { } n ? $" · AC {n}" : "")}" : sc.Standalone ? "Dokumentert test" : "Uten brukerhistorie";

    /// <summary>The scenarios of a drill-down that match the follow-up filter: every word in the title, the label, the story title or the source identity.
    /// The overview holds the complete population (computed on the server), so nothing beyond a loaded batch is missed.</summary>
    public static List<DrillScenario> FilterDrill(IEnumerable<DrillScenario> all, FollowUpFilter f)
    {
        var words = f.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. all.Where(d => (f.Module is null || d.Specification.Module == f.Module)
            && (f.Specification is null || SamePath(d.Specification.Path, f.Specification))
            && words.All(w => $"{d.Scenario.Title} {ScenarioLabel(d.Scenario)} {d.Scenario.StoryTitle} {d.Scenario.Identity}".Contains(w, StringComparison.CurrentCultureIgnoreCase)))];
    }

    /// <summary>What a follow-up list means, in the tester's words (null when the title says it all).</summary>
    public static string? DrillMeaning(string chart, string key) => (chart, key) switch
    {
        ("panel.p1Attention", "p1-uncovered") => "Akseptansescenarioer og dokumenterte tester med prioritet P1 som ikke har noe lagret testtilfelle i en aktiv testplan. "
            + "Et scenario som er lagt i en testplan uten testtilfelle, er med. Ikke alle trenger et manuelt testtilfelle: noen dekkes best av automatiske tester.",
        ("chart.scenarioReadiness", nameof(PreparationStatus.NeedsPreparation)) => ReadinessMeaning(PreparationStatus.NeedsPreparation),
        ("chart.scenarioReadiness", nameof(PreparationStatus.NeedsAssessment)) => ReadinessMeaning(PreparationStatus.NeedsAssessment),
        ("chart.scenarioReadiness", nameof(PreparationStatus.Ready)) => ReadinessMeaning(PreparationStatus.Ready),
        _ => null,
    };

    /// <summary>«Klargjøring av scenarioer» (UX-109/110): what each state means and the next step.</summary>
    public static string ReadinessMeaning(PreparationStatus status) => status switch
    {
        PreparationStatus.Ready => "Alle påkrevde klargjøringspunkter er oppfylt. Det er ikke et testresultat.",
        PreparationStatus.NeedsPreparation => "Minst ett påkrevd klargjøringspunkt mangler (for eksempel testdata, tilgang eller kjøremåte).",
        _ => "Klargjøringspunktene er ikke vurdert ennå, eller må vurderes på nytt etter en endring. Det betyr ikke at noe mangler.",
    };

    public static string ReadinessAction(PreparationStatus status) => status switch
    {
        PreparationStatus.NeedsPreparation => "Åpne scenarioer",
        PreparationStatus.NeedsAssessment => "Start vurdering",
        _ => "Se scenarioene",
    };

    /// <summary>
    /// The exact records behind one bar or follow-up line: the scenarios (by identity, per specification) or the saved cases it counts. Built from the
    /// same definitions as the bar, so the list always has as many records as the bar says. <see cref="Narrow"/>: the module or specification a
    /// coverage bar stands for, so the overview can be narrowed to it.
    /// </summary>
    public sealed record Drill(string Chart, string Key, string Title, IReadOnlyList<DrillScenario> Scenarios, IReadOnlyList<OverviewCase> Cases, Scope? Narrow = null)
    {
        /// <summary>The user stories behind a story-status bar (2026-11-26).</summary>
        public IReadOnlyList<OverviewStoryStatus> Stories { get; init; } = [];
        /// <summary>For a story-status bar: the status every listed story has (Finn og forstå filters on it).</summary>
        public UserStoryTaskStatus? StoryStatus { get; init; }
        public int Count => Scenarios.Count + Cases.Count + Stories.Count;
    }

    private static string PriorityTitle(string key) => key == "Ikke prioritert" ? "uten oppgitt prioritet" : key;

    private static bool HasPriority(OverviewScenario sc, string key) => key == "Ikke prioritert" ? sc.Priority is null : sc.Priority == key;

    private static IEnumerable<DrillScenario> AllScenarios(Slice s, HashSet<string> covered) =>
        s.Specifications.SelectMany(x => x.Scenarios.Select(sc => new DrillScenario(x, sc, IsCovered(x, sc, covered))));

    /// <summary>The records behind <paramref name="key"/> of <paramref name="chart"/> in the scope; null when the chart or bar is unknown.</summary>
    public static Drill? DrillDown(Slice s, Level level, string chart, string key)
    {
        var covered = CoveredKeys(s.Cases);
        var c = s.Cases;
        Drill Cases(string title, Func<OverviewCase, bool> match) => new(chart, key, title, [], [.. c.Where(match)]);
        Drill Scenarios(string title, Func<DrillScenario, bool> match, Scope? narrow = null) =>
            new(chart, key, title, s.ScenariosAvailable ? [.. AllScenarios(s, covered).Where(match)] : [], [], narrow);
        return (chart, key) switch
        {
            ("chart.preparation", "created") => Cases("Lagrede testtilfeller", _ => true),
            ("chart.preparation", "steps") => Cases("Testtilfeller med teststeg", x => !x.MissingSteps),
            ("chart.preparation", "approved") => Cases("Godkjente testtilfeller", x => x.Review == TestCaseReviewState.Accepted),
            ("chart.preparation", "ready") => Cases("Testtilfeller klare til testing", x => x.Ready),
            ("chart.moduleCoverage", _) => level switch
            {
                Level.Project => Scenarios($"{key}: scenarioer uten testtilfeller", d => d.Specification.Module == key && d.Scenario.IsAcceptanceScenario && !d.Covered, new Scope(Level.Module, key, null)),
                Level.Module => s.Specifications.FirstOrDefault(x => x.Path == key) is { } spec
                    ? Scenarios($"{spec.Title}: scenarioer uten testtilfeller", d => d.Specification.Path == key && d.Scenario.IsAcceptanceScenario && !d.Covered, new Scope(Level.Specification, spec.Module, spec.Path)) : null,
                _ => Scenarios($"{key}: scenarioer uten testtilfeller", d => (d.Scenario.StoryId ?? "Uten brukerhistorie") == key && d.Scenario.IsAcceptanceScenario && !d.Covered),
            },
            ("chart.priority", _) => Scenarios($"Scenarioer {PriorityTitle(key)}", d => HasPriority(d.Scenario, key)),
            ("chart.coverageGaps", _) => Scenarios(key == "Ikke prioritert" ? "Scenarioer uten oppgitt prioritet og uten testtilfeller" : $"{key} uten testtilfeller", d => HasPriority(d.Scenario, key) && !d.Covered),
            ("chart.blockers", "hindrance") => Cases("Testtilfeller med registrert hindring", x => x.Hindrance),
            ("chart.blockers", "clarify") => Cases("Testtilfeller som trenger avklaring", x => x.Review == TestCaseReviewState.NeedsClarification),
            ("chart.blockers", "expected") => Cases("Testtilfeller der forventet resultat må avklares", x => x.MissingExpected),
            ("chart.blockers", "steps") => Cases("Testtilfeller uten teststeg", x => x.MissingSteps),
            ("chart.blockers", "data") => Cases("Testtilfeller uten koblet testdata", x => x.NoTestData),
            ("chart.sourceChanges", "changed") => Cases("Testtilfeller der kilden er endret", x => x.SourceState == CaseSourceState.Changed),
            ("chart.sourceChanges", "removed") => Cases("Testtilfeller der kilden er fjernet", x => x.SourceState == CaseSourceState.Removed),
            ("chart.sourceChanges", "unavailable") => Cases("Testtilfeller som ikke kunne sammenlignes", x => x.SourceState == CaseSourceState.SourceUnavailable),
            ("chart.manualTechnical", "manual") => Cases("Manuelle testtilfeller", x => x.TestKind == ScenarioTestKind.Manual),
            ("chart.manualTechnical", "technical") => Cases("Tekniske testtilfeller", x => x.TestKind == ScenarioTestKind.Technical),
            ("chart.manualTechnical", "unset") => Cases("Testtilfeller uten valgt testtype", x => x.TestKind is null or ScenarioTestKind.Both),
            ("chart.moduleApproval", _) => Cases($"{key}: godkjente testtilfeller", x => x.Module == key && x.Review == TestCaseReviewState.Accepted),
            ("panel.translationReview", "missing") => Cases("Testtilfeller som mangler norsk tekst", x => x.Translation == TranslationState.None),
            ("panel.translationReview", "machine") => Cases("Maskinoversatte testtilfeller som ikke er gjennomgått", x => x.Translation == TranslationState.Current && x.TranslationOrigin == TranslationOrigin.Machine),
            ("panel.translationReview", "stale") => Cases("Oversettelser som må gjennomgås", x => x.Translation == TranslationState.SourceChanged),
            ("panel.translationReview", "reviewed") => Cases("Oversettelser redigert eller skrevet av en person", x => x.Translation == TranslationState.Current && x.TranslationOrigin is TranslationOrigin.MachineEdited or TranslationOrigin.Manual),
            ("chart.scenarioReadiness", _) => Scenarios($"{(Enum.TryParse<PreparationStatus>(key, out var ps) ? ScenarioPreparationEngine.Label(ps) : key)}: scenarioer",
                d => d.Scenario.IsAcceptanceScenario && d.Scenario.Preparation?.ToString() == key),
            ("chart.scenarioTypes", _) => Scenarios($"{TypeTitle(key)} akseptansescenarioer", d => d.Scenario.IsAcceptanceScenario && d.Scenario.Type.ToString() == key),
            ("chart.nfrCategories", _) => Enum.TryParse<QualityAttribute>(key, out var quality)
                ? Scenarios($"Scenarioer som gjelder {ScenarioTypeClassifier.Label(quality).ToLowerInvariant()}", d => d.Scenario.IsAcceptanceScenario && (d.Scenario.Qualities ?? []).Contains(quality)) : null,
            ("chart.gapsByType", _) => Scenarios($"{TypeTitle(key)} akseptansescenarioer uten testtilfeller", d => d.Scenario.IsAcceptanceScenario && d.Scenario.Type.ToString() == key && !d.Covered),
            ("chart.usImplementation", _) => Enum.TryParse<UserStoryTaskStatus>(key, out var status)
                ? new(chart, key, $"Brukerhistorier: {UserStoryStatusText.Label(status)}", [], []) { Stories = [.. s.Stories.Where(x => x.Status == status)], StoryStatus = status } : null,
            ("panel.p1Attention", "p1-uncovered") => Scenarios("P1-scenarioer uten testtilfeller", d => d.Scenario.Priority == "P1" && !d.Covered),
            ("panel.p1Attention", "p1-approval") => Cases("P1-testtilfeller som venter på godkjenning", x => x.Priority == "P1" && x.Review != TestCaseReviewState.Accepted),
            ("panel.p1Attention", "p1-ready") => Cases("P1-testtilfeller som ikke er klare til testing", x => x.Priority == "P1" && !x.Ready),
            ("panel.p1Attention", "p1-source") => Cases("P1-testtilfeller som må gjennomgås etter kildeendring", x => x.Priority == "P1" && x.SourceState is CaseSourceState.Changed or CaseSourceState.Removed),
            _ => null,
        };
    }

    // ── User story status and scenario type (2026-11-26) ────────────────────────────────────────────────────────────────────

    private static string TypeTitle(string key) => Enum.TryParse<ScenarioRequirementType>(key, out var t) ? ScenarioTypeClassifier.GroupLabel(t) : key;

    private static IEnumerable<OverviewScenario> Acceptance(Slice s) => s.Specifications.SelectMany(x => x.Scenarios).Where(sc => sc.IsAcceptanceScenario);

    /// <summary>Unique user stories per figure (a story counts once; each has exactly one task status). Null when the status is unavailable.</summary>
    public static int? StoryCount(Slice s, Func<OverviewStoryStatus, bool> match) => s.StoriesAvailable ? s.Stories.DistinctBy(x => x.StoryKey).Count(match) : null;

    public static int? StoriesInProgress(Slice s) => StoryCount(s, x => x.Status == UserStoryTaskStatus.InProgress);
    public static int? StoriesWithOpenTasks(Slice s) => StoryCount(s, x => x.Eligible > 0 && x.Remaining > 0);
    public static int? StoriesTasksCompleted(Slice s) => StoryCount(s, x => x.Status == UserStoryTaskStatus.TasksCompleted);
    public static int? StoriesNeedingClarification(Slice s) => StoryCount(s, x => x.Status == UserStoryTaskStatus.NeedsClarification);

    /// <summary>«Klargjøring av scenarioer»: unique scenarios per readiness status (mutually exclusive; optional items never count as missing).</summary>
    public static IReadOnlyList<DeliveryBar> ScenarioReadiness(Slice s) =>
        [.. new[] { PreparationStatus.Ready, PreparationStatus.NeedsPreparation, PreparationStatus.NeedsAssessment }
            .Select(st => (st, TileScenarios(s).Count(sc => sc.Preparation == st))).Where(x => x.Item2 > 0)
            .Select(x => Bar(x.st.ToString(), ScenarioPreparationEngine.Label(x.st), x.Item2,
                x.st switch { PreparationStatus.Ready => DeliveryDashboardTone.Ready, PreparationStatus.NeedsPreparation => DeliveryDashboardTone.Blocker, _ => DeliveryDashboardTone.Review },
                $"{x.Item2} scenarioer"))];

    // ── Custom KPI tiles (2026-11-26) ──

    /// <summary>The unique scenarios a tile can count: acceptance scenarios and standalone documented tests (requirements without a scenario,
    /// user stories, cases and plan occurrences are never counted), one per identity even when it appears in several places.</summary>
    public static IReadOnlyList<OverviewScenario> TileScenarios(Slice s) =>
        [.. s.Specifications.SelectMany(x => x.Scenarios).Where(sc => sc.IsAcceptanceScenario).DistinctBy(sc => sc.Identity, StringComparer.Ordinal)];

    public static bool Matches(OverviewScenario sc, DashboardKpiTile t) => t.Dimension switch
    {
        KpiTileDimension.TestLevel => sc.Level.ToString() == t.Metric,
        KpiTileDimension.ScenarioType => sc.Type.ToString() == t.Metric,
        _ => false,
    };

    /// <summary>The tile's count in the scope; null when the scenarios are not available (shown as «—», never an invented 0).</summary>
    public static int? TileCount(Slice s, DashboardKpiTile t) => s.ScenariosAvailable ? TileScenarios(s).Count(sc => Matches(sc, t)) : null;

    /// <summary>How many of the counted scenarios have their level only DECLARED by a document («Dokumentert E2E», not verified).</summary>
    public static int TileDeclared(Slice s, DashboardKpiTile t) => s.ScenariosAvailable ? TileScenarios(s).Count(sc => Matches(sc, t) && sc.LevelDeclared) : 0;

    /// <summary>
    /// FUN-014 (2026-11-27): the exact scenarios a tile counts — the same population and rule as <see cref="TileCount"/> (unique identities of
    /// acceptance scenarios and documented tests, across the project). Before, a tile opened Finn og forstå with only a level filter, which there
    /// applies to the selected module/specification and to every suggestion of that level: «8 E2E» opened 11 (8 documented tests + 3 suggestions).
    /// </summary>
    public static Drill TileDrill(Slice s, DashboardKpiTile t, string title)
    {
        var covered = CoveredKeys(s.Cases);
        var counted = TileScenarios(s).Where(sc => Matches(sc, t)).Select(sc => sc.Identity).ToHashSet(StringComparer.Ordinal);
        var rows = s.ScenariosAvailable
            ? AllScenarios(s, covered).Where(d => counted.Contains(d.Scenario.Identity)).DistinctBy(d => d.Scenario.Identity, StringComparer.Ordinal).ToList()
            : [];
        return new Drill("tile", t.Id, title, rows, []);
    }

    /// <summary>What an E2E tile counts and what it does not (FUN-014).</summary>
    public static string? TileMeaning(DashboardKpiTile t) => t.Dimension == KpiTileDimension.TestLevel && t.Metric == nameof(ScenarioTestLevel.EndToEnd)
        ? "Dokumenterte E2E-tester: tester i kildens testspesifikasjoner som selv sier at de er ende til ende (for eksempel TEST-E). Nivået er oppgitt i dokumentet, "
          + "ikke verifisert. E2E-scenarioforslag (fra andre kilder, som integrasjonsflyter) telles ikke her. «Har testtilfelle» betyr forberedt, ikke kjørt eller bestått."
        : null;

    /// <summary>Finn og forstå's filter for a tile: exactly the tile's dimension and value; nothing else is changed.</summary>
    public static ScenarioBrowserPresentation.Filter TileFilter(DashboardKpiTile t) => t.Dimension == KpiTileDimension.TestLevel
        ? new ScenarioBrowserPresentation.Filter(All: true, TestLevel: t.Metric)
        : new ScenarioBrowserPresentation.Filter(All: true, ScenarioType: t.Metric);

    /// <summary>Acceptance scenarios of exactly one type (a mixed scenario is only «Blandet»). Null when no specification is stored.</summary>
    public static int? ScenariosOfType(Slice s, ScenarioRequirementType type) => s.ScenariosAvailable ? Acceptance(s).Count(sc => sc.Type == type) : null;

    /// <summary>Implementeringsframdrift: user stories per task status — mutually exclusive, so the bars add up to the stories in scope.</summary>
    public static IReadOnlyList<DeliveryBar> StoryImplementation(Slice s) => !s.StoriesAvailable ? [] :
        [.. UserStoryStatusText.FilterOrder.Select(st => (st, s.Stories.DistinctBy(x => x.StoryKey).Count(x => x.Status == st))).Where(x => x.Item2 > 0)
            .Select(x => Bar(x.st.ToString(), UserStoryStatusText.Label(x.st), x.Item2, x.st switch
            {
                UserStoryTaskStatus.TasksCompleted => DeliveryDashboardTone.Ready, UserStoryTaskStatus.InProgress => DeliveryDashboardTone.Progress,
                UserStoryTaskStatus.DependencyPending or UserStoryTaskStatus.NeedsClarification => DeliveryDashboardTone.Review, _ => DeliveryDashboardTone.Neutral,
            }, $"{x.Item2} brukerhistorier"))];

    /// <summary>Scenarioer etter type: acceptance scenarios, each in exactly one type.</summary>
    public static IReadOnlyList<DeliveryBar> ScenarioTypes(Slice s) => !s.ScenariosAvailable ? [] :
        [.. ScenarioTypeClassifier.Order.Select(t => (t, Acceptance(s).Count(sc => sc.Type == t))).Where(x => x.Item2 > 0)
            .Select(x => Bar(x.t.ToString(), ScenarioTypeClassifier.GroupLabel(x.t), x.Item2, x.t == ScenarioRequirementType.Unclassified ? DeliveryDashboardTone.Review : DeliveryDashboardTone.Neutral,
                $"{x.Item2} akseptansescenarioer"))];

    /// <summary>Ikke-funksjonelle scenarioer per kategori: a scenario with two qualities is in both bars (the note says so).</summary>
    public static IReadOnlyList<DeliveryBar> NonFunctionalCategories(Slice s) => !s.ScenariosAvailable ? [] :
        [.. Enum.GetValues<QualityAttribute>().Select(q => (q, Acceptance(s).Count(sc => sc.Type is ScenarioRequirementType.NonFunctional or ScenarioRequirementType.Mixed && (sc.Qualities ?? []).Contains(q))))
            .Where(x => x.Item2 > 0).Select(x => Bar(x.q.ToString(), ScenarioTypeClassifier.Label(x.q), x.Item2, DeliveryDashboardTone.Neutral, $"{x.Item2} scenarioer"))];

    /// <summary>Manglende dekning etter type: acceptance scenarios without a saved case, by type.</summary>
    public static IReadOnlyList<DeliveryBar> GapsByType(Slice s)
    {
        if (!s.ScenariosAvailable) return [];
        var covered = CoveredKeys(s.Cases);
        var open = s.Specifications.SelectMany(x => x.Scenarios.Where(sc => sc.IsAcceptanceScenario && !IsCovered(x, sc, covered))).ToList();
        return [.. ScenarioTypeClassifier.Order.Select(t => (t, open.Count(sc => sc.Type == t))).Where(x => x.Item2 > 0)
            .Select(x => Bar(x.t.ToString(), $"{ScenarioTypeClassifier.GroupLabel(x.t)} uten testtilfeller", x.Item2, DeliveryDashboardTone.Review, $"{x.Item2} akseptansescenarioer uten lagret testtilfelle"))];
    }

    /// <summary>Oversettelser til gjennomgang: from the stored translation state only (nothing is translated to compute it).</summary>
    public static IReadOnlyList<DeliveryBar> Translations(Slice s) =>
    [.. new[]
        {
            ("missing", "Mangler norsk tekst", s.Cases.Count(c => c.Translation == TranslationState.None), DeliveryDashboardTone.Neutral),
            ("machine", "Maskinoversatt — ikke gjennomgått", s.Cases.Count(c => c.Translation == TranslationState.Current && c.TranslationOrigin == TranslationOrigin.Machine), DeliveryDashboardTone.Review),
            ("stale", "Må gjennomgås (originalen er endret)", s.Cases.Count(c => c.Translation == TranslationState.SourceChanged), DeliveryDashboardTone.Blocker),
            ("reviewed", "Redigert eller skrevet av en person", s.Cases.Count(c => c.Translation == TranslationState.Current && c.TranslationOrigin is TranslationOrigin.MachineEdited or TranslationOrigin.Manual), DeliveryDashboardTone.Ready),
        }.Where(x => x.Item3 > 0).Select(x => Bar(x.Item1, x.Item2, x.Item3, x.Item4, $"{x.Item3} testtilfeller"))];
}
