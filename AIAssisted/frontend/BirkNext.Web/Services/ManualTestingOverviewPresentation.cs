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

    public sealed record Row(string Key, string Label, int? Scenarios, int? Covered, int Cases, int Approved, int FollowUp, string? Module, string? SpecificationPath);

    public sealed record Slice(IReadOnlyList<OverviewSpecification> Specifications, IReadOnlyList<OverviewCase> Cases, bool ScenariosAvailable);

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
        Level.Module => new([.. o.Specifications.Where(x => x.Module == s.Module)], [.. o.Cases.Where(c => c.Module == s.Module)], o.ScenariosAvailable),
        Level.Specification => new([.. o.Specifications.Where(x => SamePath(x.Path, s.SpecificationPath))], [.. o.Cases.Where(c => SamePath(c.SpecificationPath, s.SpecificationPath))], o.ScenariosAvailable),
        _ => new(o.Specifications, o.Cases, o.ScenariosAvailable),
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
            Level.Project => s.Specifications.GroupBy(x => x.Module).Select(g => (g.Key, g.Key, g.Sum(x => x.Scenarios.Count), g.Sum(x => x.Scenarios.Count(sc => IsCovered(x, sc, covered))))),
            Level.Module => s.Specifications.Select(x => (x.Path, x.Title, x.Scenarios.Count, x.Scenarios.Count(sc => IsCovered(x, sc, covered)))),
            _ => s.Specifications.SelectMany(x => x.Scenarios.Select(sc => (Spec: x, Sc: sc))).GroupBy(t => t.Sc.StoryId ?? "Uten brukerhistorie")
                .Select(g => (g.Key, g.Key, g.Count(), g.Count(t => IsCovered(t.Spec, t.Sc, covered)))),
        };
        return [.. groups.Where(g => g.Total > 0).OrderBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => Bar(g.Key, $"{g.Label} ({g.Covered}/{g.Total})", (int)Math.Round(100.0 * g.Covered / g.Total), g.Covered == g.Total ? DeliveryDashboardTone.Ready : DeliveryDashboardTone.Progress,
                $"{g.Covered} av {g.Total} scenarioer har testtilfeller"))];
    }

    private static readonly string[] Priorities = ["P1", "P2", "P3"];

    /// <summary>Scenarios per stated priority; «Ikke prioritert» when the source states none.</summary>
    public static IReadOnlyList<DeliveryBar> Priority(Slice s)
    {
        if (!s.ScenariosAvailable) return [];
        var all = s.Specifications.SelectMany(x => x.Scenarios).ToList();
        return [.. Priorities.Select(p => (p, all.Count(sc => sc.Priority == p))).Append(("Ikke prioritert", all.Count(sc => sc.Priority is null)))
            .Where(x => x.Item2 > 0).Select(x => Bar(x.Item1, x.Item1, x.Item2, x.Item1 == "P1" ? DeliveryDashboardTone.Change : DeliveryDashboardTone.Neutral, $"{x.Item2} scenarioer"))];
    }

    /// <summary>Manglende testdekning: scenarios without a saved case, by priority (outstanding work, unlike the coverage level above).</summary>
    public static IReadOnlyList<DeliveryBar> Gaps(Slice s)
    {
        if (!s.ScenariosAvailable) return [];
        var covered = CoveredKeys(s.Cases);
        var open = s.Specifications.SelectMany(x => x.Scenarios.Where(sc => !IsCovered(x, sc, covered))).ToList();
        return [.. Priorities.Select(p => (p, open.Count(sc => sc.Priority == p))).Append(("Ikke prioritert", open.Count(sc => sc.Priority is null)))
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
    public static IReadOnlyList<Row> Rows(Slice s, Level level)
    {
        var covered = CoveredKeys(s.Cases);
        static int FollowUp(IEnumerable<OverviewCase> cs) => cs.Count(c => !c.Ready || c.Review == TestCaseReviewState.NeedsClarification
            || c.SourceState is CaseSourceState.Changed or CaseSourceState.Removed or CaseSourceState.SourceUnavailable);
        switch (level)
        {
            case Level.Project:
                var modules = s.Specifications.Select(x => x.Module).Concat(s.Cases.Select(c => c.Module)).Distinct(StringComparer.Ordinal)
                    .OrderBy(m => m == "Uten modultilknytning" ? 1 : 0).ThenBy(m => m, StringComparer.CurrentCultureIgnoreCase);
                return [.. modules.Select(m =>
                {
                    var specs = s.Specifications.Where(x => x.Module == m).ToList(); var cs = s.Cases.Where(c => c.Module == m).ToList();
                    return new Row(m, m, s.ScenariosAvailable ? specs.Sum(x => x.Scenarios.Count) : null, s.ScenariosAvailable ? specs.Sum(x => x.Scenarios.Count(sc => IsCovered(x, sc, covered))) : null,
                        cs.Count, cs.Count(c => c.Review == TestCaseReviewState.Accepted), FollowUp(cs), m, null);
                })];
            case Level.Module:
                return [.. s.Specifications.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).Select(x =>
                {
                    var cs = s.Cases.Where(c => SamePath(c.SpecificationPath, x.Path)).ToList();
                    return new Row(x.Path, x.Title, x.Scenarios.Count, x.Scenarios.Count(sc => IsCovered(x, sc, covered)), cs.Count, cs.Count(c => c.Review == TestCaseReviewState.Accepted), FollowUp(cs), x.Module, x.Path);
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
