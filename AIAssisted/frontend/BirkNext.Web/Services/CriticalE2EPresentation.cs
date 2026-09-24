using BirkNext.CriticalE2E;

namespace BirkNext.Web.Services;

/// <summary>How a row or headline should read. The page maps tone to colour; it never decides what something means.</summary>
public enum CriticalE2ETone { Neutral, Good, Warning, Bad }

public sealed record CriticalE2EHeadline(string Label, CriticalE2ETone Tone, string Detail);

public sealed record CriticalE2EModeSummary(
    CriticalE2EExecutionMode Mode, string Label, int Passed, int Total, int Pending, int Failed, int Blocked,
    CriticalE2EEngineStatus Engine)
{
    /// <summary>"2 / 3 passed · 1 pending" — the counts that change, and nothing else.</summary>
    public string Counts => Total == 0
        ? "No flows configured"
        : $"{Passed} / {Total} passed" + (Pending > 0 ? $" · {Pending} pending" : "") +
          (Failed > 0 ? $" · {Failed} failed" : "") + (Blocked > 0 ? $" · {Blocked} blocked" : "");
}

public sealed record CriticalE2EFlowRow(
    string FlowId, string Module, string Name, string ModeLabel, string StatusLabel, CriticalE2ETone Tone,
    bool RequiredForRelease, string? Detail, bool Runnable);

public sealed record CriticalE2ERunAction(CriticalE2EExecutionMode Mode, string Label, bool Enabled, string? BlockedReason, string? Action);

/// <summary>
/// Everything the Critical E2E page shows, derived from the backend's overview. Pure: no state, no clock, no service
/// calls, so the rules that decide what a release verdict reads as can be tested as rules.
///
/// The consistent theme is that four different things stay four different things — a flow is configured, a flow ran, a
/// flow passed, and a transport is able to run — because merging any two of them produces a surface that says a release
/// is fine when nobody has checked.
/// </summary>
public static class CriticalE2EPresentation
{
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

    public static string ModeLabel(CriticalE2EExecutionMode mode) => mode switch
    {
        // Not "manual", and not "unattended". The login is manual; the flow is not.
        CriticalE2EExecutionMode.CompanionBrowser => "Companion browser",
        _ => "Automated integration",
    };

    public static CriticalE2EHeadline Headline(CriticalE2EReleaseStatus release) => release.Disposition switch
    {
        CriticalE2EReleaseDisposition.Ready => new("Ready", CriticalE2ETone.Good, release.Summary),
        // Incomplete is not a failure. Nothing is known to be wrong; nobody has looked yet.
        CriticalE2EReleaseDisposition.Incomplete => new("Incomplete", CriticalE2ETone.Warning, release.Summary),
        CriticalE2EReleaseDisposition.Blocked => new("Blocked", CriticalE2ETone.Bad, release.Summary),
        _ => new("Not configured", CriticalE2ETone.Neutral, release.Summary),
    };

    /// <summary>
    /// Definition coverage, stated as its own sentence. "6 / 6 modules configured" is a claim about test design and
    /// says nothing at all about whether those tests pass.
    /// </summary>
    public static string Coverage(CriticalE2EReleaseStatus release) =>
        $"{release.ModulesCovered} / {release.ModulesTotal} modules have a required critical flow configured";

    public static CriticalE2EModeSummary Mode(CriticalE2EOverview overview, CriticalE2EExecutionMode mode)
    {
        var flows = overview.Flows.Where(f => f.Mode == mode && f.Enabled && f.RequiredForRelease).ToList();
        var effective = flows.Select(f => f.LastResultMatchesRelease ? f.LastStatus : CriticalE2EStatus.NotRun).ToList();
        return new CriticalE2EModeSummary(
            mode, ModeLabel(mode),
            effective.Count(s => s == CriticalE2EStatus.Passed),
            flows.Count,
            effective.Count(s => s is CriticalE2EStatus.NotRun or CriticalE2EStatus.Running),
            effective.Count(s => s == CriticalE2EStatus.Failed),
            effective.Count(s => s is CriticalE2EStatus.Blocked or CriticalE2EStatus.Cancelled),
            mode == CriticalE2EExecutionMode.CompanionBrowser ? overview.BrowserEngine : overview.IntegrationEngine);
    }

    public static List<CriticalE2EFlowRow> Rows(CriticalE2EOverview overview) =>
        overview.Flows.Select(flow =>
        {
            var (label, tone, detail) = Status(flow);
            return new CriticalE2EFlowRow(
                flow.FlowId, flow.Module, flow.Name, ModeLabel(flow.Mode), label, tone,
                flow.RequiredForRelease, detail,
                Runnable: flow.Enabled && flow.Configured && Engine(overview, flow.Mode).Ready);
        }).ToList();

    private static (string Label, CriticalE2ETone Tone, string? Detail) Status(CriticalE2EFlowSummary flow)
    {
        // A definition problem is reported as such rather than as a failing test: nobody should read "this module is
        // broken" when the truth is "this test was never finished".
        if (!flow.Enabled) return ("Disabled", CriticalE2ETone.Neutral, null);
        if (!flow.Configured) return ("Not runnable", CriticalE2ETone.Warning, flow.ConfigurationProblem);
        if (flow.LastStatus == CriticalE2EStatus.NotRun) return ("Not run", CriticalE2ETone.Neutral, null);
        if (!flow.LastResultMatchesRelease)
            return ("Pending", CriticalE2ETone.Warning, $"Last result was {flow.LastStatus} on build {flow.LastBuildId ?? "unknown"}.");

        return flow.LastStatus switch
        {
            CriticalE2EStatus.Passed => ("Passed", CriticalE2ETone.Good, null),
            CriticalE2EStatus.Failed => ("Failed", CriticalE2ETone.Bad, null),
            CriticalE2EStatus.Blocked => ("Blocked", CriticalE2ETone.Warning, "A prerequisite was unavailable, so the flow never ran."),
            CriticalE2EStatus.Cancelled => ("Cancelled", CriticalE2ETone.Neutral, null),
            _ => ("Running", CriticalE2ETone.Neutral, null),
        };
    }

    private static CriticalE2EEngineStatus Engine(CriticalE2EOverview overview, CriticalE2EExecutionMode mode) =>
        mode == CriticalE2EExecutionMode.CompanionBrowser ? overview.BrowserEngine : overview.IntegrationEngine;

    /// <summary>
    /// The run buttons. A disabled button always carries the reason and, where there is one, the thing to do about it —
    /// a control that is simply greyed out makes the user guess.
    /// </summary>
    public static List<CriticalE2ERunAction> Actions(CriticalE2EOverview overview)
    {
        var actions = new List<CriticalE2ERunAction>();
        foreach (var mode in new[] { CriticalE2EExecutionMode.AutomatedIntegration, CriticalE2EExecutionMode.CompanionBrowser })
        {
            var summary = Mode(overview, mode);
            var label = mode == CriticalE2EExecutionMode.CompanionBrowser ? "Run browser regression" : "Run automated regression";
            if (summary.Total == 0 && overview.Flows.All(f => f.Mode != mode || !f.Enabled))
            {
                actions.Add(new CriticalE2ERunAction(mode, label, false, "No flows are configured for this mode.", null));
                continue;
            }
            actions.Add(new CriticalE2ERunAction(mode, label, summary.Engine.Ready,
                summary.Engine.Ready ? null : summary.Engine.Message, summary.Engine.Ready ? null : summary.Engine.Action));
        }
        return actions;
    }

    /// <summary>
    /// What a pipeline can say about a flow it cannot run. A companion browser flow needs a human-authenticated
    /// browser, so an automated pipeline reporting it as failed would be reporting on its own limitation.
    /// </summary>
    public static string PipelineNote(CriticalE2EOverview overview) =>
        overview.Flows.Any(f => f.Enabled && f.RequiredForRelease && f.Mode == CriticalE2EExecutionMode.CompanionBrowser)
            ? "Companion browser flows need a signed-in browser, so a pipeline reports them as pending attended browser validation rather than running them."
            : "";

    /// <summary>One line per run for the history disclosure. Duration and outcome; the step detail is a level deeper.</summary>
    public static string Line(CriticalE2ERunResult run) =>
        $"{run.FlowName} · {ModeLabel(run.Mode)} · {run.Status} · {run.PassedSteps}/{run.TotalSteps} steps · {run.DurationMs / 1000:0.0} s";
}
