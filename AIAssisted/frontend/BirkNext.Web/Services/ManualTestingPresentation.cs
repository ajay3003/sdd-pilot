using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// The Manual Testing workspace (home and its three stages) derived only from stored records: the project's saved plans (Test Plan
/// Suggestions), its last stored discovery result, its specification's requirement count and its manual runs. No status is invented: a
/// stage is "done" only when the records show it, and the one next action follows from what exists. Pure; no I/O.
/// </summary>
public static class ManualTestingPresentation
{
    public const string HomeRoute = "manual-testing";

    // ── Run results report (CSV) ─────────────────────────────────────────────────────────────────────────────────────────

    public static readonly IReadOnlyList<string> RunReportColumns =
        ["Test case", "Plan", "Plan revision", "Result", "Tester", "Where tested", "Started", "Completed", "Steps", "Passed", "Failed", "Blocked", "Not run",
         "Defects", "Open defects", "Evidence references", "Retest of earlier failed run"];

    /// <summary>
    /// Manual run results for stakeholders, one row per run (newest first), opened by Excel and other spreadsheet tools: UTF-8 with a byte-order mark,
    /// comma-separated, quoted. A cell starting with = + - @ (or a tab/CR) is prefixed with an apostrophe so a spreadsheet never runs it as a
    /// formula. Results only — a prepared case that was never run is not a row, and an abandoned run says so.
    /// </summary>
    public static string RunResultsCsv(IEnumerable<ManualRunSummary> runs, IReadOnlyDictionary<Guid, string> planNames)
    {
        static string Cell(string? v)
        {
            var t = v ?? "";
            if (t.Length > 0 && t[0] is '=' or '+' or '-' or '@' or '\t' or '\r') t = "'" + t;
            return "\"" + t.Replace("\"", "\"\"") + "\"";
        }
        var sb = new System.Text.StringBuilder("\uFEFF");
        sb.Append(string.Join(",", RunReportColumns.Select(Cell))).Append("\r\n");
        foreach (var r in runs.OrderByDescending(r => r.StartedAt))
            sb.Append(string.Join(",", new[]
            {
                r.Title, planNames.GetValueOrDefault(r.DraftId) ?? "", r.PlanRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), RunLabel(r.State, r.Outcome),
                r.Tester, r.Environment ?? "", r.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                r.CompletedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                N(r.Steps), N(r.Passed), N(r.Failed), N(r.Blocked), N(r.NotRun), N(r.Defects), N(r.OpenDefects), N(r.Evidence),
                r.RetestOf is null ? "" : "Yes",
            }.Select(Cell))).Append("\r\n");
        return sb.ToString();
        static string N(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public const string DiscoverRoute = "manual-testing/discover";
    public const string PlanRoute = "test-plan-suggestions";
    public const string ExecuteRoute = "manual-testing/execute";

    /// <summary>The three stages as the shared workspace header lists them (title, the question it answers, route).</summary>
    public static readonly IReadOnlyList<WorkspaceStage> Stages =
    [
        new("discover", "Discover & understand", "What needs testing?", DiscoverRoute),
        new("plan", "Plan & prepare", "How will we test it?", PlanRoute),
        new("execute", "Execute & document", "What happened during testing?", ExecuteRoute),
    ];

    /// <summary>What the home and the stages are derived from.</summary>
    /// <param name="Requirements">Requirements of the project's selected specification; null = no specification selected or readable.</param>
    public sealed record Facts(string? ProjectKey, string? ProjectName, IReadOnlyList<TestPlanDraftSummary> Plans, ScenarioDiscoveryResult? Discovery, int? Requirements,
        IReadOnlyList<ManualRunSummary> Runs);

    public sealed record NextAction(string Label, string Href, string Why);

    public static int Cases(Facts f) => f.Plans.Sum(p => p.Cases);
    public static int Approved(Facts f) => f.Plans.Sum(p => p.Accepted);
    public static int NeedClarification(Facts f) => f.Plans.Sum(p => p.NeedsClarification);
    public static IReadOnlyList<ManualRunSummary> InProgress(Facts f) => [.. f.Runs.Where(r => r.State == ManualRunState.InProgress)];
    public static IReadOnlyList<ManualRunSummary> Completed(Facts f) => [.. f.Runs.Where(r => r.State == ManualRunState.Completed)];

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
                f.Requirements is { } r ? $"{r} requirement{(r == 1 ? "" : "s")} in the specification" : "No specification selected",
                f.Discovery is null ? null : $"discovered {f.Discovery.DiscoveredAt.ToLocalTime():yyyy-MM-dd HH:mm}",
                clarify > 0 ? $"{clarify} need clarification" : null,
            }.Where(x => x is not null)),
            ActionLabel: "Open", Href: DiscoverRoute);
        var cases = Cases(f);
        var plan = new GuidedStep("plan", "Plan & prepare", cases == 0 ? (f.Discovery is null ? GuidedStepState.Waiting : GuidedStepState.Current)
                : NeedClarification(f) > 0 || Approved(f) == 0 ? GuidedStepState.Current : GuidedStepState.Done,
            cases == 0 ? "No test cases yet" : $"{cases} case{(cases == 1 ? "" : "s")} · {Approved(f)} approved",
            "How will we test it? Choose scenarios, prepare and edit test cases, link requirements and Test Data, check testability and save the plan.",
            Evidence: cases == 0 ? null : $"{f.Plans.Count} plan{(f.Plans.Count == 1 ? "" : "s")}" + (NeedClarification(f) > 0 ? $" · {NeedClarification(f)} need clarification" : ""),
            ActionLabel: "Open", Href: PlanRoute);
        var running = InProgress(f).Count;
        var done = Completed(f);
        var execute = new GuidedStep("execute", "Execute & document", running > 0 ? GuidedStepState.Current : done.Count > 0 ? GuidedStepState.Done : cases > 0 ? GuidedStepState.Current : GuidedStepState.Waiting,
            running > 0 ? $"{running} run{(running == 1 ? "" : "s")} in progress" : done.Count > 0 ? $"{done.Count} completed run{(done.Count == 1 ? "" : "s")}" : "No runs yet",
            "What happened during testing? Run a saved case step by step, record results, evidence references and defects, and keep the run history.",
            Evidence: done.Count == 0 ? null : $"{done.Count(r => r.Outcome == ManualRunOutcome.Passed)} passed · {done.Count(r => r.Outcome == ManualRunOutcome.Failed)} failed · {done.Count(r => r.Outcome is ManualRunOutcome.Blocked or ManualRunOutcome.NotRun)} blocked or not run",
            ActionLabel: "Open", Href: ExecuteRoute);
        return [discover, plan, execute];
    }

    /// <summary>The one next step, from what is stored (never a fixed workflow position).</summary>
    public static NextAction Next(Facts f)
    {
        if (f.ProjectKey is null) return new("Select a project", "project-import", "Manual Testing works on one imported project at a time.");
        if (InProgress(f).OrderByDescending(r => r.StartedAt).FirstOrDefault() is { } open)
            return new("Resume test run", $"{ExecuteRoute}?run={open.Id}", $"\"{open.Title}\" was started {open.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm} and is not completed.");
        if (f.Plans.Count == 0 || Cases(f) == 0)
            return f.Discovery is null
                ? new("Discover what to test", $"{PlanRoute}?area=choose", "Nothing is prepared yet. Find scenarios in the project's evidence, or write a test case by hand.")
                : new("Choose scenarios to prepare", $"{PlanRoute}?area=choose", $"{f.Discovery.Candidates.Count} suggestions are waiting; nothing is prepared yet.");
        if (NeedClarification(f) > 0 && f.Plans.FirstOrDefault(p => p.NeedsClarification > 0) is { } unclear)
            return new("Resolve cases that need clarification", $"{PlanRoute}?draft={unclear.Id}&area=prepare", $"{NeedClarification(f)} test case(s) wait for an answer before they can be approved.");
        if (Completed(f).Count == 0)
            return new("Run a test case", ExecuteRoute, $"{Cases(f)} saved case(s), none executed yet.");
        if (Completed(f).OrderByDescending(r => r.CompletedAt).FirstOrDefault() is { Outcome: ManualRunOutcome.Failed } failed)
            return new("Review the failed run", $"{ExecuteRoute}?run={failed.Id}", $"The latest completed run of \"{failed.Title}\" failed.");
        return new("Continue preparation", $"{PlanRoute}?area=prepare", "Keep preparing and running the remaining cases.");
    }

    /// <summary>Discovery suggestions per source (capability) and per type, for the Discover stage.</summary>
    public static IReadOnlyList<(string Label, int Count)> BySource(ScenarioDiscoveryResult d) =>
        [.. d.Candidates.GroupBy(c => string.IsNullOrWhiteSpace(c.SourceCategory) ? c.CapabilityId : c.SourceCategory).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count()))];

    public static IReadOnlyList<(string Label, int Count)> ByType(ScenarioDiscoveryResult d) =>
        [.. d.Candidates.GroupBy(c => ScenarioCandidateTypes.Label(c.Type)).OrderByDescending(g => g.Count()).Select(g => (g.Key, g.Count()))];

    public static string RunTone(ManualRunState state, ManualRunOutcome outcome) => state switch
    {
        ManualRunState.InProgress => "info",
        ManualRunState.Abandoned => "muted",
        _ => outcome switch { ManualRunOutcome.Passed => "ok", ManualRunOutcome.Failed => "bad", _ => "warn" },
    };

    public static string RunLabel(ManualRunState state, ManualRunOutcome outcome) => state switch
    {
        ManualRunState.InProgress => "In progress",
        ManualRunState.Abandoned => "Stopped",
        _ => ManualRunRules.OutcomeLabel(outcome),
    };
}

/// <summary>One stage of a testing workspace (shared by the workspace header; Technical Testing can define its own three).</summary>
public sealed record WorkspaceStage(string Id, string Title, string Question, string Route);
