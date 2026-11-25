using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// The Manual Testing workspace (home and its two stages) derived only from stored records: the project's saved plans (Test Plan Suggestions),
/// its last stored discovery result and its specification's requirement count. Manual test execution is owned by Azure DevOps Test Plans
/// (2026-11-19): BirkNext prepares, reviews and exports test cases and records no manual runs, so nothing here says a case was executed. A
/// stage is "done" only when the records show it, and the one next action follows from what exists. Pure; no I/O.
/// </summary>
public static class ManualTestingPresentation
{
    public const string HomeRoute = "manual-testing";
    public const string DiscoverRoute = "manual-testing/discover";
    /// <summary>Planlegg og klargjør: the master-detail case editor (2026-11-23).</summary>
    public const string PlanRoute = "manual-testing/plan";
    /// <summary>Testplaner (2026-11-25): create, open, archive and transfer the project's test plans — the third and last tab.</summary>
    public const string PlansRoute = "manual-testing/plans";

    /// <summary>The Testplaner tab opened on the transfer of one test plan.</summary>
    public static string TransferRoute(Guid planId) => $"{PlansRoute}?transfer={planId}";

    /// <summary>The suggested name of a new test plan: «Testplan yyyy-MM-dd HH-mm-ss» in the tester's local time when they start creating it (the name
    /// stays editable; identity is the plan's own id, never its name).</summary>
    public static string SuggestedPlanName(DateTime local) => $"Testplan {local:yyyy-MM-dd HH-mm-ss}";
    /// <summary>The full Test Plan Suggestions page: scenario choice with discovery, history, impact, conflict resolution and the export (kept as is).</summary>
    public const string LegacyPlanRoute = "test-plan-suggestions";

    /// <summary>The two stages as the shared workspace header lists them (title, the question it answers, route). Plan &amp; prepare ends with the
    /// export to Azure DevOps Test Plans, where the cases are executed.</summary>
    public static readonly IReadOnlyList<WorkspaceStage> Stages =
    [
        new("discover", "Finn og forstå", "Hva må testes?", DiscoverRoute),
        new("plan", "Planlegg og klargjør", "Hvordan tester vi det?", PlanRoute),
        new("plans", "Testplaner", "Hvilke testplaner har vi?", PlansRoute),
    ];

    /// <summary>What the home and the stages are derived from.</summary>
    /// <param name="Requirements">Requirements of the project's selected specification; null = no specification selected or readable.</param>
    /// <param name="SpecificationChoice">Set when the project holds several specifications and none is chosen (a monorepo): what to choose and why.
    /// Discovery then finds no business scenarios from the specification, so choosing comes first.</param>
    public sealed record Facts(string? ProjectKey, string? ProjectName, IReadOnlyList<TestPlanDraftSummary> Plans, ScenarioDiscoveryResult? Discovery, int? Requirements,
        string? SpecificationChoice = null, bool AnyProjectImported = true);

    /// <summary>Discover &amp; understand: its Module / Feature / Specification lists choose what manual testing reads.</summary>
    public const string SpecificationRoute = "manual-testing/discover";

    public sealed record NextAction(string Label, string Href, string Why);

    /// <summary>Requirement filters of Discover &amp; understand: all, without test design, prepared but not approved, with an approved case.</summary>
    public const string AllRequirements = "all", WithoutDesign = "none", PreparedOnly = "prepared", WithApproved = "approved";

    public static readonly IReadOnlyList<(string Key, string Label)> RequirementFilters =
        [(AllRequirements, "All"), (WithoutDesign, "No test design"), (PreparedOnly, "Prepared"), (WithApproved, "Approved")];

    /// <summary>The requirements a filter and a search (id or text, case-insensitive) show, in the specification's order.</summary>
    public static List<ProjectRequirementOption> FilterRequirements(IEnumerable<ProjectRequirementOption> requirements,
        IReadOnlyDictionary<string, PreparedRequirementTrace> designed, string filter, string? query, bool showTechnical = true)
    {
        var q = query?.Trim() ?? string.Empty;
        return [.. requirements.Where(r =>
        {
            if (!showTechnical && ManualTestingRelevance.RequirementReason(r.Title) is not null) return false;
            var row = designed.GetValueOrDefault(r.RequirementId);
            var keep = filter switch
            {
                WithoutDesign => row is null,
                PreparedOnly => row is { ApprovedCases: 0 },
                WithApproved => row is { ApprovedCases: > 0 },
                _ => true,
            };
            return keep && (q.Length == 0 || r.RequirementId.Contains(q, StringComparison.OrdinalIgnoreCase) || (r.Title ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        })];
    }

    public static int Cases(Facts f) => f.Plans.Sum(p => p.Cases);
    public static int Approved(Facts f) => f.Plans.Sum(p => p.Accepted);
    public static int NeedClarification(Facts f) => f.Plans.Sum(p => p.NeedsClarification);

    public static IReadOnlyList<GuidedStep> Steps(Facts f)
    {
        if (f.ProjectKey is null)
            return [.. Stages.Select(s => new GuidedStep(s.Id, s.Title, GuidedStepState.Waiting, "No project", s.Question,
                DisabledReason: f.AnyProjectImported ? "Choose a project in the Project list above first." : "Import a project first."))];
        var candidates = f.Discovery?.Candidates.Count ?? 0;
        var clarify = f.Discovery?.Candidates.Count(c => c.NeedsClarification) ?? 0;
        var discover = new GuidedStep("discover", "Discover & understand", f.Discovery is null && f.Plans.Count == 0 ? GuidedStepState.Current : GuidedStepState.Done,
            f.Discovery is null ? "Not discovered yet" : $"{candidates} suggestion{(candidates == 1 ? "" : "s")}",
            "What needs testing? Requirements, scenario suggestions from the project's evidence, existing tests and what needs clarification.",
            Evidence: string.Join(" · ", new[]
            {
                f.SpecificationChoice is not null ? "Specification not chosen yet" : f.Requirements is { } r ? $"{r} requirement{(r == 1 ? "" : "s")} in the specification" : "No specification selected",
                f.Discovery is null ? null : $"discovered {f.Discovery.DiscoveredAt.ToLocalTime():yyyy-MM-dd HH:mm}",
                clarify > 0 ? $"{clarify} need clarification" : null,
            }.Where(x => x is not null)),
            ActionLabel: "Open", Href: DiscoverRoute);
        var cases = Cases(f);
        var plan = new GuidedStep("plan", "Plan & prepare", cases == 0 ? (f.Discovery is null ? GuidedStepState.Waiting : GuidedStepState.Current)
                : NeedClarification(f) > 0 || Approved(f) == 0 ? GuidedStepState.Current : GuidedStepState.Done,
            cases == 0 ? "No test cases yet" : $"{cases} case{(cases == 1 ? "" : "s")} · {Approved(f)} approved",
            "How will we test it? Choose scenarios, prepare and edit test cases, link requirements and Test Data, check testability, save the plan and export it to Azure DevOps Test Plans, where it is executed.",
            Evidence: cases == 0 ? null : $"{f.Plans.Count} plan{(f.Plans.Count == 1 ? "" : "s")}" + (NeedClarification(f) > 0 ? $" · {NeedClarification(f)} need clarification" : ""),
            ActionLabel: "Open", Href: PlanRoute);
        return [discover, plan];
    }

    /// <summary>The one next step, from what is stored (never a fixed workflow position).</summary>
    public static NextAction Next(Facts f)
    {
        if (f.ProjectKey is null)
            return f.AnyProjectImported
                ? new("Velg applikasjon", "project-import", "Manuell testing jobber med én importert applikasjon om gangen: velg den i prosjektlisten.")
                : new("Importer en applikasjon", "project-import", "Ingenting er importert ennå. Importer en prosjekt-ZIP — kildekode, dokumenter eller begge — så finner Manuell testing hva som skal testes.");
        if ((f.Plans.Count == 0 || Cases(f) == 0) && f.SpecificationChoice is { } choose)
            return new("Velg spesifikasjon", SpecificationRoute, choose);
        if (f.Plans.Count == 0 || Cases(f) == 0)
            return f.Discovery is null
                ? new("Finn scenarioer", DiscoverRoute, "Ingenting er forberedt ennå. Finn scenarioer i applikasjonens kilder, eller skriv et testtilfelle selv.")
                : new("Velg scenarioer", DiscoverRoute, $"{f.Discovery.Candidates.Count} forslag venter; ingenting er forberedt ennå.");
        if (NeedClarification(f) > 0 && f.Plans.FirstOrDefault(p => p.NeedsClarification > 0) is { } unclear)
            return new("Avklar testtilfeller", $"{PlanRoute}?draft={unclear.Id}", $"{NeedClarification(f)} testtilfelle(r) venter på svar før de kan godkjennes.");
        if (Approved(f) > 0 && f.Plans.OrderByDescending(p => p.UpdatedAt).FirstOrDefault(p => p.Accepted > 0) is { } ready)
            return new("Overfør testplan", TransferRoute(ready.Id),
                $"{Approved(f)} godkjente testtilfelle(r) kan overføres til Azure DevOps Test Plans, der manuelle tester kjøres og resultatene registreres. Godkjent betyr ikke at hele testplanen er klar.");
        return new("Fortsett forberedelsen", PlanRoute, "Gå gjennom og godkjenn testtilfellene, og eksporter dem til Azure DevOps.");
    }

    /// <summary>
    /// Where an old Test Plan Suggestions link belongs now (2026-11-23): Planlegg og klargjør is the one place cases are prepared, translated and
    /// exported; Finn og forstå chooses scenarios. Null = stay on the full page: guide-mode plans (System Understanding), a guide scenario deep link,
    /// a candidate view, or an explicit request for the advanced tools.
    /// </summary>
    public static string? CanonicalRoute(string? guide, string? draft, string? area, string? caseId, string? advanced, string? scenarios = null, string? view = null)
    {
        if (!string.IsNullOrWhiteSpace(advanced) || !string.IsNullOrWhiteSpace(guide) || !string.IsNullOrWhiteSpace(scenarios) || !string.IsNullOrWhiteSpace(view)) return null;
        if (string.Equals(area, "choose", StringComparison.OrdinalIgnoreCase)) return DiscoverRoute;
        var query = new List<string>();
        var hasId = Guid.TryParse(draft, out var id);
        // Export lives on Testplaner → «Overfør testplan» (2026-11-25).
        if (string.Equals(area, "export", StringComparison.OrdinalIgnoreCase)) return hasId ? TransferRoute(id) : PlansRoute;
        if (hasId) query.Add($"draft={id}");
        if (!string.IsNullOrWhiteSpace(caseId)) query.Add($"case={Uri.EscapeDataString(caseId)}");
        return query.Count == 0 ? PlanRoute : $"{PlanRoute}?{string.Join("&", query)}";
    }

    /// <summary>Discovery suggestions per source (capability) and per type, for the Discover stage.</summary>
    public static IReadOnlyList<(string Label, int Count)> BySource(ScenarioDiscoveryResult d) =>
        [.. d.Candidates.GroupBy(c => string.IsNullOrWhiteSpace(c.SourceCategory) ? c.CapabilityId : c.SourceCategory).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count()))];

    public static IReadOnlyList<(string Label, int Count)> ByType(ScenarioDiscoveryResult d) =>
        [.. d.Candidates.GroupBy(c => ScenarioCandidateTypes.Label(c.Type)).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count()))];
}

/// <summary>One stage of a testing workspace (shared by the workspace header; Technical Testing defines its own).</summary>
public sealed record WorkspaceStage(string Id, string Title, string Question, string Route);
