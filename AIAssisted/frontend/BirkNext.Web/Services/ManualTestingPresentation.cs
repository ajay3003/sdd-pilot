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
    public const string PlanRoute = "test-plan-suggestions";

    /// <summary>The two stages as the shared workspace header lists them (title, the question it answers, route). Plan &amp; prepare ends with the
    /// export to Azure DevOps Test Plans, where the cases are executed.</summary>
    public static readonly IReadOnlyList<WorkspaceStage> Stages =
    [
        new("discover", "Oppdag og forstå", "Hva må testes?", DiscoverRoute),
        new("plan", "Planlegg og forbered", "Hvordan tester vi det?", PlanRoute),
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
        IReadOnlyDictionary<string, PreparedRequirementTrace> designed, string filter, string? query)
    {
        var q = query?.Trim() ?? string.Empty;
        return [.. requirements.Where(r =>
        {
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
                ? new("Oppdag scenarioer", $"{PlanRoute}?area=choose&discover=now", "Ingenting er forberedt ennå. Finn scenarioer i applikasjonens kilder, eller skriv et testtilfelle selv.")
                : new("Velg scenarioer", DiscoverRoute, $"{f.Discovery.Candidates.Count} forslag venter; ingenting er forberedt ennå.");
        if (NeedClarification(f) > 0 && f.Plans.FirstOrDefault(p => p.NeedsClarification > 0) is { } unclear)
            return new("Avklar testtilfeller", $"{PlanRoute}?draft={unclear.Id}&area=prepare", $"{NeedClarification(f)} testtilfelle(r) venter på svar før de kan godkjennes.");
        if (Approved(f) > 0 && f.Plans.OrderByDescending(p => p.UpdatedAt).FirstOrDefault(p => p.Accepted > 0) is { } ready)
            return new("Eksporter til Azure DevOps", $"{PlanRoute}?draft={ready.Id}&area=export",
                $"{Approved(f)} godkjente testtilfelle(r) er klare for Azure DevOps Test Plans, der manuelle tester kjøres og resultatene registreres.");
        return new("Fortsett forberedelsen", $"{PlanRoute}?area=prepare", "Gå gjennom og godkjenn testtilfellene, og eksporter dem til Azure DevOps.");
    }

    /// <summary>Discovery suggestions per source (capability) and per type, for the Discover stage.</summary>
    public static IReadOnlyList<(string Label, int Count)> BySource(ScenarioDiscoveryResult d) =>
        [.. d.Candidates.GroupBy(c => string.IsNullOrWhiteSpace(c.SourceCategory) ? c.CapabilityId : c.SourceCategory).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count()))];

    public static IReadOnlyList<(string Label, int Count)> ByType(ScenarioDiscoveryResult d) =>
        [.. d.Candidates.GroupBy(c => ScenarioCandidateTypes.Label(c.Type)).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count()))];
}

/// <summary>One stage of a testing workspace (shared by the workspace header; Technical Testing defines its own).</summary>
public sealed record WorkspaceStage(string Id, string Title, string Question, string Route);
