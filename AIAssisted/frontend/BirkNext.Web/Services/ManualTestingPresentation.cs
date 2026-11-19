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
        new("discover", "Discover & understand", "What needs testing?", DiscoverRoute),
        new("plan", "Plan & prepare", "How will we test it?", PlanRoute),
    ];

    /// <summary>What the home and the stages are derived from.</summary>
    /// <param name="Requirements">Requirements of the project's selected specification; null = no specification selected or readable.</param>
    /// <param name="SpecificationChoice">Set when the project holds several specifications and none is chosen (a monorepo): what to choose and why.
    /// Discovery then finds no business scenarios from the specification, so choosing comes first.</param>
    public sealed record Facts(string? ProjectKey, string? ProjectName, IReadOnlyList<TestPlanDraftSummary> Plans, ScenarioDiscoveryResult? Discovery, int? Requirements,
        string? SpecificationChoice = null);

    /// <summary>Discover &amp; understand: its Module / Feature / Specification lists choose what manual testing reads.</summary>
    public const string SpecificationRoute = "manual-testing/discover";

    public sealed record NextAction(string Label, string Href, string Why);

    public static int Cases(Facts f) => f.Plans.Sum(p => p.Cases);
    public static int Approved(Facts f) => f.Plans.Sum(p => p.Accepted);
    public static int NeedClarification(Facts f) => f.Plans.Sum(p => p.NeedsClarification);

    public static IReadOnlyList<GuidedStep> Steps(Facts f)
    {
        if (f.ProjectKey is null)
            return [.. Stages.Select(s => new GuidedStep(s.Id, s.Title, GuidedStepState.Waiting, "No project", s.Question + " Select or import a project first.",
                DisabledReason: "Select a project first (Import Project → Use)."))];
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
        if (f.ProjectKey is null) return new("Select a project", "project-import", "Manual Testing works on one imported project at a time.");
        if ((f.Plans.Count == 0 || Cases(f) == 0) && f.SpecificationChoice is { } choose)
            return new("Choose the specification", SpecificationRoute, choose);
        if (f.Plans.Count == 0 || Cases(f) == 0)
            return f.Discovery is null
                ? new("Discover what to test", $"{PlanRoute}?area=choose", "Nothing is prepared yet. Find scenarios in the project's evidence, or write a test case by hand.")
                : new("Choose scenarios to prepare", $"{PlanRoute}?area=choose", $"{f.Discovery.Candidates.Count} suggestions are waiting; nothing is prepared yet.");
        if (NeedClarification(f) > 0 && f.Plans.FirstOrDefault(p => p.NeedsClarification > 0) is { } unclear)
            return new("Resolve cases that need clarification", $"{PlanRoute}?draft={unclear.Id}&area=prepare", $"{NeedClarification(f)} test case(s) wait for an answer before they can be approved.");
        if (Approved(f) > 0 && f.Plans.OrderByDescending(p => p.UpdatedAt).FirstOrDefault(p => p.Accepted > 0) is { } ready)
            return new("Export to Azure DevOps", $"{PlanRoute}?draft={ready.Id}&area=export",
                $"{Approved(f)} approved case(s) are ready for Azure DevOps Test Plans, where manual tests are executed and results are recorded.");
        return new("Continue preparation", $"{PlanRoute}?area=prepare", "Review and approve the prepared cases, then export them to Azure DevOps.");
    }

    /// <summary>Discovery suggestions per source (capability) and per type, for the Discover stage.</summary>
    public static IReadOnlyList<(string Label, int Count)> BySource(ScenarioDiscoveryResult d) =>
        [.. d.Candidates.GroupBy(c => string.IsNullOrWhiteSpace(c.SourceCategory) ? c.CapabilityId : c.SourceCategory).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count()))];

    public static IReadOnlyList<(string Label, int Count)> ByType(ScenarioDiscoveryResult d) =>
        [.. d.Candidates.GroupBy(c => ScenarioCandidateTypes.Label(c.Type)).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count()))];
}

/// <summary>One stage of a testing workspace (shared by the workspace header; Technical Testing defines its own).</summary>
public sealed record WorkspaceStage(string Id, string Title, string Question, string Route);
