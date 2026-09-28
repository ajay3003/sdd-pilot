using BirkNext.CriticalE2E;

namespace BirkNext.Web.Services;

/// <summary>How a row or headline should read. The page maps tone to colour; it never decides what something means.</summary>
public enum CriticalE2ETone { Neutral, Good, Warning, Bad }

public sealed record CriticalE2EHeadline(string Label, CriticalE2ETone Tone, string Detail);

/// <summary>
/// One row of run history. Kind comes from the flow the run belongs to (runs do not carry it); a run whose flow no longer
/// exists is <see cref="CriticalE2EHistoryKind.Removed"/>. ReleaseEvidence is typed, so the page never has to guess it.
/// </summary>
public enum CriticalE2EHistoryKind { Critical, Smoke, Removed }

/// <param name="Archivable">
/// Whether the row offers Archive: an active run of an existing flow that is neither the flow's latest run nor the run the
/// release verdict uses for the named build. The backend applies the same rule; this only decides whether to offer it.
/// </param>
public sealed record CriticalE2EHistoryRow(CriticalE2ERunResult Run, CriticalE2EHistoryKind Kind, bool ReleaseEvidence, string? Problem,
    bool Archivable = false)
{
    public bool Archived => Run.Archived;
}

/// <summary>"12 recent runs · 4 archived · 3 smoke/diagnostic hidden", for the runs in scope (all flows, or one).</summary>
public sealed record CriticalE2EHistoryCounts(int Recent, int Archived, int SmokeHidden);

/// <summary>
/// One flow row. Configuration (Enabled, Required, runnable) and outcome (Result) are separate columns, because a disabled
/// flow still has a last result and a flow that never ran is not disabled.
/// </summary>
public sealed record CriticalE2EFlowRow(
    string FlowId, string Module, string Name, CriticalE2EFlowKind Kind, int Steps, string Execution,
    bool Enabled, bool RequiredForRelease, string? ConfigurationProblem,
    DateTimeOffset? LastRunAt, string Result, CriticalE2ETone ResultTone, string? ResultDetail,
    bool Runnable, string? RunBlockedReason);

/// <summary>The one global run action, or none. Never a large disabled control with nothing to run behind it.</summary>
public sealed record CriticalE2ERunAction(CriticalE2EExecutionMode Mode, string Label, int FlowCount, bool Enabled, string? Reason);

/// <summary>How the paired browser reads as attended execution. Straight from the overview's live snapshot.</summary>
/// <param name="Headline">The readiness strip's one line ("Ready for attended automation"), so the state is said once there.</param>
/// <param name="CardDetail">The summary card's short secondary line; the strip carries the rest.</param>
public sealed record CriticalE2EReadinessView(string Label, CriticalE2ETone Tone, string? Reason, string Companion, string Page, string ElementPicking,
    string Headline = "", string? CardDetail = null);

/// <summary>A selector as a person reads it: what the element is, and which strategy identifies it.</summary>
public sealed record CriticalE2ETargetView(string Identity, string Strategy, bool Fragile);

/// <summary>
/// Everything the Critical E2E page shows, derived from the backend's overview. Pure: no state, no clock, no service
/// calls, so the rules that decide what the page says can be tested as rules.
///
/// Things that stay different things: a flow is configured, enabled, required, it ran, it passed, and the browser is
/// ready. Merging any two produces a page that says a release is fine when nobody has checked.
/// </summary>
public static class CriticalE2EPresentation
{
    public const string AttendedLabel = "Attended browser";
    public const string AttendedHelp = "Executed through Browser Companion in the active approved browser session.";

    public static string ExecutionLabel(CriticalE2EExecutionMode mode) => mode switch
    {
        CriticalE2EExecutionMode.CompanionBrowser => AttendedLabel,
        _ => "Automated API",
    };

    /// <summary>Kept for callers that still name the mode; the page uses <see cref="ExecutionLabel"/>.</summary>
    public static string ModeLabel(CriticalE2EExecutionMode mode) => ExecutionLabel(mode);

    public static string BrowserCompanionSetupHref(string? profileId) =>
        "/admin/system-settings?section=target-environments&tab=browser" + (string.IsNullOrWhiteSpace(profileId) ? "" : "&profile=" + Uri.EscapeDataString(profileId));

    // ── Page state ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The release headline, in context. "Not configured" alone read as "no flows" while fifteen flows were on screen; each
    /// case now says which of flows / critical flows / enabled / required is missing.
    /// </summary>
    public static CriticalE2EHeadline Headline(CriticalE2EOverview overview)
    {
        var release = overview.Release;
        var critical = overview.Flows.Where(f => f.Kind == CriticalE2EFlowKind.Critical).ToList();
        var diagnostic = overview.Flows.Count - critical.Count;
        if (overview.Flows.Count == 0)
            return new("No critical flows configured", CriticalE2ETone.Neutral, "Create a critical user journey to start attended regression testing.");
        if (critical.Count == 0)
            return new("No release-critical flows configured", CriticalE2ETone.Neutral,
                $"{diagnostic} smoke/diagnostic {Plural(diagnostic, "flow is", "flows are")} available.");
        if (critical.All(f => !f.Enabled))
            return new("No enabled critical flows", CriticalE2ETone.Neutral, "Critical flows exist, but none are currently enabled for execution.");
        return release.Disposition switch
        {
            CriticalE2EReleaseDisposition.NotConfigured => new("Release coverage not configured", CriticalE2ETone.Neutral,
                $"{critical.Count} critical {Plural(critical.Count, "flow exists", "flows exist")}, but none {(critical.Count == 1 ? "is" : "are")} marked as required for release."),
            // No build named: the backend does not evaluate release evidence. Neither ready nor failed.
            CriticalE2EReleaseDisposition.NotEvaluated => new("Release evidence not evaluated", CriticalE2ETone.Neutral,
                $"No build is selected. {release.RequiredFlowsTotal} required {Plural(release.RequiredFlowsTotal, "flow is", "flows are")} configured; set the build to evaluate release evidence."),
            CriticalE2EReleaseDisposition.Ready => new("Release regression ready", CriticalE2ETone.Good, release.Summary),
            // Incomplete is not a failure. Nothing is known to be wrong; nobody has looked yet.
            CriticalE2EReleaseDisposition.Incomplete => new("Release regression incomplete", CriticalE2ETone.Warning, release.Summary),
            _ => new("Release regression blocked", CriticalE2ETone.Bad, release.Summary),
        };
    }

    /// <summary>"0 / 1 modules covered" — definition coverage by required critical flows. Diagnostic modules never count.</summary>
    public static string Coverage(CriticalE2EReleaseStatus release) =>
        release.ModulesTotal == 0 ? "No delivery modules yet" : $"{release.ModulesCovered} / {release.ModulesTotal} modules covered";

    public static string BuildLabel(string? buildId) => string.IsNullOrWhiteSpace(buildId) ? "Not set" : buildId.Trim();

    /// <summary>A build is not needed to run; without one, nothing the page shows is release evidence.</summary>
    public static string BuildHelp(string? buildId) => string.IsNullOrWhiteSpace(buildId)
        ? "Results can run, but will not count as release evidence."
        : "Results are recorded against this build.";

    public static CriticalE2EReadinessView Readiness(CriticalE2EOverview overview)
    {
        var a = overview.Attended;
        var companion = a.CompanionConnected ? "Connected" : "Not connected";
        var page = !a.CompanionConnected ? "—"
            : a.OpenApprovedPages == 0 ? "No approved page open"
            : a.OpenApprovedPages > 1 ? $"{a.OpenApprovedPages} approved pages open"
            : $"{HostOf(a.CurrentOrigin)}{a.CurrentRoute}";
        var picking = !a.CompanionConnected ? "—" : a.ElementPickSupported ? "Available" : "Not supported by this extension build";
        var card = a.CompanionConnected ? "Companion connected" : "Companion not connected";
        return a.Status.State switch
        {
            CriticalE2EEngineState.Ready => new("Ready", CriticalE2ETone.Good, null, companion, page, picking, "Ready for attended automation", card),
            CriticalE2EEngineState.Unavailable => new("Out of scope", CriticalE2ETone.Neutral, a.Status.Message, companion, page, picking, "Attended automation is out of scope here", null),
            // Two pages open is a precise blocker, not a vague "unavailable": the fix is to close one.
            _ when a.CompanionConnected && a.OpenApprovedPages > 1 => new("Blocked", CriticalE2ETone.Warning, a.Status.Message, companion, page, picking, "Attended automation blocked", $"{a.OpenApprovedPages} approved pages open"),
            _ => new("Not ready", CriticalE2ETone.Warning, a.Status.Message, companion, page, picking, "Attended automation not ready", card),
        };
    }

    private static string HostOf(string? origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host : origin ?? "";

    // ── Flow table ────────────────────────────────────────────────────────────────────────────────────────────────

    public static int Count(CriticalE2EOverview overview, CriticalE2EFlowKind kind) => overview.Flows.Count(f => f.Kind == kind);

    /// <summary>Critical flows, plus smoke/diagnostic ones only when the reader asked for them — always after the critical ones.</summary>
    public static List<CriticalE2EFlowRow> Rows(CriticalE2EOverview overview, bool includeDiagnostic = true) =>
        overview.Flows
            .Where(f => includeDiagnostic || f.Kind == CriticalE2EFlowKind.Critical)
            .OrderBy(f => f.Kind).ThenBy(f => f.Module, StringComparer.CurrentCultureIgnoreCase).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(flow =>
            {
                var (result, tone, detail) = Result(flow, !string.IsNullOrWhiteSpace(overview.Release.BuildId));
                var engine = Engine(overview, flow.Mode);
                var blocked = !flow.Enabled ? "This flow is disabled."
                    : !flow.Configured ? flow.ConfigurationProblem ?? "This flow cannot run as configured."
                    : !engine.Ready ? engine.Message
                    : null;
                return new CriticalE2EFlowRow(flow.FlowId, flow.Module, flow.Name, flow.Kind, flow.StepCount, ExecutionLabel(flow.Mode),
                    flow.Enabled, flow.RequiredForRelease, flow.Configured ? null : flow.ConfigurationProblem,
                    flow.LastRunAt, result, tone, detail, blocked is null, blocked);
            }).ToList();

    /// <summary>
    /// The outcome of the latest run — never configuration. Disabled lives in its own badge; a flow that has not run reads
    /// "Not run" whether it is enabled or not.
    /// </summary>
    private static (string Label, CriticalE2ETone Tone, string? Detail) Result(CriticalE2EFlowSummary flow, bool buildNamed)
    {
        if (flow.LastStatus == CriticalE2EStatus.NotRun) return ("Not run", CriticalE2ETone.Neutral, null);
        // "Pending" is about a named build. With none, the latest result is simply the latest result.
        if (buildNamed && !flow.LastResultMatchesRelease)
            return ("Pending", CriticalE2ETone.Warning, $"Last result was {flow.LastStatus} on build {flow.LastBuildId ?? "unknown"}.");
        return flow.LastStatus switch
        {
            CriticalE2EStatus.Passed => ("Passed", CriticalE2ETone.Good, null),
            CriticalE2EStatus.Failed => ("Failed", CriticalE2ETone.Bad, null),
            CriticalE2EStatus.Blocked => ("Blocked", CriticalE2ETone.Warning, "A prerequisite was unavailable, so the flow did not finish."),
            CriticalE2EStatus.Cancelled => ("Cancelled", CriticalE2ETone.Neutral, null),
            _ => ("Running", CriticalE2ETone.Neutral, null),
        };
    }

    private static CriticalE2EEngineStatus Engine(CriticalE2EOverview overview, CriticalE2EExecutionMode mode) =>
        mode == CriticalE2EExecutionMode.CompanionBrowser ? overview.Attended.Status : overview.IntegrationEngine;

    /// <summary>
    /// Global run actions: attended regression over enabled critical browser flows, and automated API flows only when there
    /// are some. Diagnostic flows are never part of a batch. No flows behind an action means no action.
    /// </summary>
    public static List<CriticalE2ERunAction> Actions(CriticalE2EOverview overview)
    {
        var actions = new List<CriticalE2ERunAction>();
        foreach (var (mode, label) in new[] { (CriticalE2EExecutionMode.CompanionBrowser, "Run attended regression"), (CriticalE2EExecutionMode.AutomatedIntegration, "Run automated API flows") })
        {
            var count = overview.Flows.Count(f => f.Kind == CriticalE2EFlowKind.Critical && f.Enabled && f.Mode == mode);
            if (count == 0) continue;
            var engine = Engine(overview, mode);
            actions.Add(new CriticalE2ERunAction(mode, label, count, engine.Ready, engine.Ready ? null : engine.Message));
        }
        return actions;
    }

    /// <summary>
    /// What a pipeline can say about a flow it cannot run. An attended browser flow needs a person's signed-in browser, so
    /// a pipeline reporting it as failed would be reporting on its own limitation.
    /// </summary>
    public static string PipelineNote(CriticalE2EOverview overview) =>
        overview.Flows.Any(f => f.Enabled && f.RequiredForRelease && f.Kind == CriticalE2EFlowKind.Critical && f.Mode == CriticalE2EExecutionMode.CompanionBrowser)
            ? "Attended browser flows need a signed-in browser, so a pipeline reports them as pending attended validation rather than running them."
            : "";

    // ── Runs ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// History rows, newest first. A run is release evidence only when its flow is a required critical flow and it was
    /// recorded against the build currently named — the same match the backend uses for a named build. Smoke runs,
    /// runs without a build and runs of optional flows are never shown as evidence.
    /// Archived runs are hidden unless asked for, independently of the smoke/diagnostic switch; <paramref name="flowId"/>
    /// narrows the rows to one flow.
    /// </summary>
    public static List<CriticalE2EHistoryRow> History(CriticalE2EOverview overview, string? buildId, bool includeDiagnostic,
        bool includeArchived = false, string? flowId = null) =>
        AllHistory(overview, buildId, flowId)
            .Where(r => includeDiagnostic || r.Kind != CriticalE2EHistoryKind.Smoke)
            .Where(r => includeArchived || !r.Archived)
            .ToList();

    public static CriticalE2EHistoryCounts HistoryCounts(CriticalE2EOverview overview, string? buildId, bool includeDiagnostic,
        bool includeArchived, string? flowId = null)
    {
        var all = AllHistory(overview, buildId, flowId);
        return new(
            all.Count(r => !r.Archived && (includeDiagnostic || r.Kind != CriticalE2EHistoryKind.Smoke)),
            // Across all flows the backend's total counts archived runs beyond the ones it sent.
            string.IsNullOrWhiteSpace(flowId) ? Math.Max(overview.ArchivedRunCount, all.Count(r => r.Archived)) : all.Count(r => r.Archived),
            includeDiagnostic ? 0 : all.Count(r => r.Kind == CriticalE2EHistoryKind.Smoke && (includeArchived || !r.Archived)));
    }

    public static string HistoryHint(CriticalE2EHistoryCounts counts) =>
        $"{counts.Recent} recent {Plural(counts.Recent, "run", "runs")}"
        + (counts.Archived > 0 ? $" · {counts.Archived} archived" : "")
        + (counts.SmokeHidden > 0 ? $" · {counts.SmokeHidden} smoke/diagnostic hidden" : "");

    private static List<CriticalE2EHistoryRow> AllHistory(CriticalE2EOverview overview, string? buildId, string? flowId)
    {
        var flows = overview.Flows.ToDictionary(f => f.FlowId, StringComparer.Ordinal);
        var runs = overview.History.OrderByDescending(r => r.StartedAt).ToList();
        // Per flow: its latest run, and the run the release verdict reads for the named build. Neither is offered for archive.
        var kept = runs.GroupBy(r => r.FlowId).SelectMany(g =>
        {
            var ids = new List<string> { g.First().RunId };
            if (flows.TryGetValue(g.Key, out var f) && f.Enabled && f.RequiredForRelease && f.Kind == CriticalE2EFlowKind.Critical
                && g.FirstOrDefault(r => OnBuild(r, buildId)) is { } evidence)
                ids.Add(evidence.RunId);
            return ids;
        }).ToHashSet(StringComparer.Ordinal);

        return runs
            .Where(run => string.IsNullOrWhiteSpace(flowId) || run.FlowId == flowId)
            .Select(run =>
            {
                var kind = !flows.TryGetValue(run.FlowId, out var f) ? CriticalE2EHistoryKind.Removed
                    : f.Kind == CriticalE2EFlowKind.Diagnostic ? CriticalE2EHistoryKind.Smoke : CriticalE2EHistoryKind.Critical;
                var evidence = kind == CriticalE2EHistoryKind.Critical && run.RequiredForRelease && OnBuild(run, buildId);
                return new CriticalE2EHistoryRow(run, kind, evidence, run.Status == CriticalE2EStatus.Passed ? null : ShortProblem(run),
                    Archivable: kind != CriticalE2EHistoryKind.Removed && !run.Archived && !kept.Contains(run.RunId));
            })
            .ToList();
    }

    private static bool OnBuild(CriticalE2ERunResult run, string? buildId) =>
        !string.IsNullOrWhiteSpace(run.BuildId) && !string.IsNullOrWhiteSpace(buildId)
        && string.Equals(run.BuildId.Trim(), buildId.Trim(), StringComparison.OrdinalIgnoreCase);

    // ── Archive vs clear ──────────────────────────────────────────────────────────────────────────────────────────

    public const string ArchiveExplanation =
        "Archive hides old runs from this list and keeps them. Results, the latest run, coverage and release evidence do not change, and archived runs can be restored.";
    public const string ClearExplanation =
        "Clear permanently deletes this flow's runs. The flow itself is kept. Flows with build-linked release evidence cannot be cleared.";

    /// <summary>Would a clear in this scope remove release evidence? The backend refuses it; the dialog says so first.</summary>
    public static bool ClearBlocked(CriticalE2EHistoryPreview preview, bool includeArchived) =>
        (includeArchived ? preview.ReleaseEvidenceRuns : preview.ActiveReleaseEvidenceRuns) > 0;

    public static int ClearCount(CriticalE2EHistoryPreview preview, bool includeArchived) =>
        preview.ActiveRuns + (includeArchived ? preview.ArchivedRuns : 0);

    public static bool ClearConfirmed(string? typed) => string.Equals(typed?.Trim(), CriticalE2EHistoryPolicy.ClearConfirmation, StringComparison.Ordinal);

    public static string RunCount(int n) => $"{n} {Plural(n, "run", "runs")}";

    public static string KindLabel(CriticalE2EHistoryKind kind) => kind switch
    {
        CriticalE2EHistoryKind.Smoke => "Smoke",
        CriticalE2EHistoryKind.Removed => "Removed flow",
        _ => "Critical",
    };

    /// <summary>"Step 1 · Assert visible — No element matched the selector." One line; the full run has the rest.</summary>
    public static string? ShortProblem(CriticalE2ERunResult run)
    {
        var index = run.StepResults.FindIndex(s => s.Status is CriticalE2EStatus.Failed or CriticalE2EStatus.Blocked);
        if (index < 0) return run.FailureReason;
        var step = run.StepResults[index];
        var why = step.SanitizedError ?? run.FailureReason;
        return $"Step {index + 1} · {step.Description}" + (string.IsNullOrWhiteSpace(why) ? "" : $" — {why}");
    }

    public static string StatusLabel(CriticalE2EStatus status) => status switch
    {
        CriticalE2EStatus.NotRun => "Not run",
        _ => status.ToString(),
    };

    public static string StepGlyph(CriticalE2EStatus status) => status switch
    {
        CriticalE2EStatus.Passed => "✓",
        CriticalE2EStatus.Failed => "✗",
        CriticalE2EStatus.Blocked => "!",
        CriticalE2EStatus.Running => "→",
        _ => "○",
    };

    /// <summary>"5 steps · 5 passed · 0 failed · 0 blocked".</summary>
    public static string StepCounts(CriticalE2ERunResult run) =>
        $"{run.TotalSteps} {Plural(run.TotalSteps, "step", "steps")} · {run.PassedSteps} passed · " +
        $"{run.StepResults.Count(s => s.Status == CriticalE2EStatus.Failed)} failed · " +
        $"{run.StepResults.Count(s => s.Status is CriticalE2EStatus.Blocked or CriticalE2EStatus.Cancelled)} blocked";

    /// <summary>The step that decided the outcome, said the way a tester reads it. Failed and Blocked stay different words.</summary>
    public static string? FirstProblem(CriticalE2ERunResult run)
    {
        var index = run.StepResults.FindIndex(s => s.Status is CriticalE2EStatus.Failed or CriticalE2EStatus.Blocked);
        if (index < 0) return run.FailureReason;
        var step = run.StepResults[index];
        var verb = step.Status == CriticalE2EStatus.Failed ? "failed" : "blocked";
        return $"Step {index + 1} {verb}: {step.Description}. {step.SanitizedError ?? run.FailureReason ?? ""}".Trim();
    }

    /// <summary>Invariant, so an English page never reads "4,2 s" on a Norwegian machine.</summary>
    public static string Duration(double ms) => ms < 1000
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ms:0} ms")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ms / 1000:0.0} s");

    /// <summary>One line per run, for places with no room for the table.</summary>
    public static string Line(CriticalE2ERunResult run) =>
        $"{run.FlowName} · {ExecutionLabel(run.Mode)} · {StatusLabel(run.Status)} · {run.PassedSteps}/{run.TotalSteps} steps · {Duration(run.DurationMs)}";

    // ── Targets and picking ───────────────────────────────────────────────────────────────────────────────────────

    public static string StrategyLabel(CompanionSelectorKind kind) => kind switch
    {
        CompanionSelectorKind.TestId => "Test id",
        CompanionSelectorKind.Role => "Role + name",
        CompanionSelectorKind.Label => "Label",
        CompanionSelectorKind.Text => "Text",
        _ => "CSS (structural)",
    };

    public static string RoleTitle(string? role) => (role ?? "").ToLowerInvariant() switch
    {
        "button" => "Button",
        "textbox" => "Input",
        "link" => "Link",
        "listitem" => "List item",
        "combobox" => "Dropdown",
        "checkbox" => "Checkbox",
        "heading" => "Heading",
        "row" => "Row",
        "cell" => "Cell",
        "tab" => "Tab",
        "menuitem" => "Menu item",
        "status" => "Status",
        "" => "Element",
        var other => char.ToUpperInvariant(other[0]) + other[1..],
    };

    private static string TagTitle(string tag) => tag switch
    {
        "input" or "textarea" => "Input",
        "select" => "Dropdown",
        "a" => "Link",
        "button" => "Button",
        _ => "Element",
    };

    /// <summary>
    /// The identity a person recognises — "Button — Opprett ny rolle" — from a stored selector. A CSS path is never shown as
    /// the identity; it is marked fragile instead.
    /// </summary>
    public static CriticalE2ETargetView Target(CompanionSelector? selector)
    {
        if (selector is null || (string.IsNullOrWhiteSpace(selector.Value) && string.IsNullOrWhiteSpace(selector.Role)))
            return new("No element selected", "", false);
        return selector.Kind switch
        {
            CompanionSelectorKind.TestId => new($"Element with test id “{selector.Value}”", StrategyLabel(selector.Kind), false),
            CompanionSelectorKind.Role => new($"{RoleTitle(selector.Role)}{(string.IsNullOrWhiteSpace(selector.Name) ? "" : $" — {selector.Name}")}", StrategyLabel(selector.Kind), false),
            CompanionSelectorKind.Label => new($"Input — {selector.Value}", StrategyLabel(selector.Kind), false),
            CompanionSelectorKind.Text => new($"“{selector.Value}”", StrategyLabel(selector.Kind), false),
            _ => new("Element at a structural CSS path", StrategyLabel(selector.Kind), true),
        };
    }

    /// <summary>The identity of a just-picked element, from its descriptor rather than from the selector.</summary>
    public static string Identity(CompanionElementDescriptor element)
    {
        var kind = element.Role is { Length: > 0 } role ? RoleTitle(role) : TagTitle(element.TagName);
        var name = element.AccessibleName ?? element.Label;
        return string.IsNullOrWhiteSpace(name) ? kind : $"{kind} — {name}";
    }

    /// <summary>
    /// Why a picked element could not be stored. Several matches is an ambiguity the tester can act on; no match at all
    /// means nothing identifies the element.
    /// </summary>
    public static string NoSelectorReason(CompanionElementDescriptor element)
    {
        var ambiguous = element.Candidates.Where(c => c.MatchCount > 1).Select(c => c.MatchCount).DefaultIfEmpty(0).Min();
        return ambiguous > 1
            ? $"{ambiguous} matching elements were found. Choose a more specific element or selector. No action was performed."
            : "No selector identifies this element uniquely. Ask for a data-testid on it, or choose another element. No action was performed.";
    }

    /// <summary>
    /// One line about a picked element: what it is, the selector stored (or why none was), and any state that would make
    /// the step fail as written. Identity only — the descriptor never carries a field value.
    /// </summary>
    public static string PickSummary(CompanionElementDescriptor element)
    {
        var what = $"{element.TagName}{(string.IsNullOrWhiteSpace(element.AccessibleName) ? "" : $" “{element.AccessibleName}”")}"
            + (string.IsNullOrWhiteSpace(element.Label) ? "" : $" (label “{element.Label}”)");
        var alternatives = element.Candidates.Count(c => c.Unique) - (element.Recommended is null ? 0 : 1);
        var selector = element.Recommended is { } r
            ? $"Stored {r.Describe()} — unique on {element.PageRoute}" + (alternatives > 0 ? $"; {alternatives} other unique selector{(alternatives == 1 ? "" : "s")} available." : ".")
            : $"No selector identifies it uniquely on {element.PageRoute}; nothing was stored. Ask for a data-testid on this element or pick another one.";
        var state = !element.Visible ? " It is currently hidden." : !element.Enabled ? " It is currently disabled." : "";
        return $"Picked {what}. {selector}{state}";
    }

    private static string Plural(int n, string one, string many) => n == 1 ? one : many;
}
