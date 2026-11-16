using BirkNext.Applicability;

namespace BirkNext.Web.Services;

/// <summary>The five technical testing areas the workspace summarises. Each is owned by existing specialist modules.</summary>
public enum TechnicalArea { Frontend, Api, Integration, Performance, CriticalE2E }

/// <summary>Is there evidence that the area applies to this project (source and target analysis, from Technology Coverage)?</summary>
/// <remarks><see cref="EnvironmentOnly"/>: the review can run because the active Target Environment is configured (a target URL, configured
/// integrations), but nothing in this project's own source shows the area — the environment is shared, the project's evidence is not.</remarks>
public enum AreaEvidence { Applies, PartlyApplies, EnvironmentOnly, NotApplicable, Unsupported, NotEnoughEvidence, NeedsRefresh, NotRead }

/// <summary>Is what a check needs configured (target, credentials, integrations, flows)? Configuration only — never a result.</summary>
public enum AreaConfiguration { Configured, NeedsConfiguration, NotApplicable, NotRead }

/// <summary>The latest recorded runtime result (an execution against a Target Environment) — never derived from static analysis.</summary>
public enum AreaRuntime { NotRun, Passed, Failed, Incomplete, Blocked, ReviewRequired, Running }

/// <summary>One recorded runtime execution as its specialist module stored it: when, against which environment, with which identity.</summary>
/// <param name="Identity">Build, version, scope or source snapshot the run recorded; null when the module records none.</param>
/// <param name="Stale">Why this result may no longer describe the current target or source; null when nothing indicates that.</param>
public sealed record RuntimeResult(AreaRuntime State, string Verdict, DateTimeOffset At, string? Environment, string? Identity, string Where, string Href,
    string? Stale = null, string? Detail = null);

/// <summary>One fact from static evidence (source analysis, contracts, declarations) with the page that owns it.</summary>
public sealed record StaticFact(string Text, string? Href = null);

/// <summary>What the workspace knows about one area, read from the authoritative modules. Nothing here is computed beyond counting.</summary>
/// <param name="Applicability">Technology Coverage's applicability for the area's review; null when it could not be read.</param>
/// <param name="ConfigurationGaps">What the area's own configuration still lacks (in words, from the specialist module's state).</param>
/// <param name="Runs">Recorded runtime results, newest first.</param>
/// <param name="ReadError">Why part of this area could not be read; shown, never turned into "none".</param>
public sealed record AreaFacts(TechnicalArea Area, ReviewApplicability? Applicability, IReadOnlyList<StaticFact> Static, IReadOnlyList<string> ConfigurationGaps,
    IReadOnlyList<RuntimeResult> Runs, string? ReadError = null)
{
    public RuntimeResult? Latest => Runs.Count == 0 ? null : Runs[0];
}

/// <summary>
/// The Technical Testing workspace (home and its three stages) derived only from what the specialist modules already store: Technology
/// Coverage applicability (does the area apply, is configuration missing), static evidence (contracts, declared journeys, defined flows,
/// discovery) and recorded runtime results (Frontend Quality Review and API Quality Review history in this browser, Integration Quality Review
/// runs, performance runs, Critical E2E flow results). Static evidence and runtime results are separate dimensions and never combined into one
/// verdict; nothing is executed here. Pure; no I/O.
/// </summary>
public static class TechnicalTestingPresentation
{
    public const string HomeRoute = "technical-testing";
    public const string AssessRoute = "technical-testing/assess";
    public const string ConfigureRoute = "technical-testing/configure";
    public const string VerifyRoute = "technical-testing/verify";
    public const string TargetEnvironmentsRoute = "admin/system-settings?section=target-environments";

    public static readonly IReadOnlyList<WorkspaceStage> Stages =
    [
        new("assess", "Discover & assess", "What technical behaviour can be tested?", AssessRoute),
        new("configure", "Configure & prepare", "What do the checks need?", ConfigureRoute),
        new("verify", "Verify & investigate", "What was verified, and what needs investigating?", VerifyRoute),
    ];

    public static readonly IReadOnlyList<TechnicalArea> Areas = [TechnicalArea.Frontend, TechnicalArea.Api, TechnicalArea.Integration, TechnicalArea.Performance, TechnicalArea.CriticalE2E];

    /// <summary>The area's Technology Coverage review id (the applicability rules that already exist for it).</summary>
    public static string ReviewId(TechnicalArea a) => a switch
    {
        TechnicalArea.Frontend => "frontend-quality-review",
        TechnicalArea.Api => "api-quality-review",
        TechnicalArea.Integration => "integration-quality-review",
        TechnicalArea.Performance => "performance-test-review",
        _ => "critical-e2e-regression",
    };

    public static string Slug(TechnicalArea a) => a switch
    {
        TechnicalArea.Frontend => "frontend", TechnicalArea.Api => "api", TechnicalArea.Integration => "integration",
        TechnicalArea.Performance => "performance", _ => "e2e",
    };

    public static TechnicalArea? FromSlug(string? slug) => Areas.Cast<TechnicalArea?>().FirstOrDefault(a => string.Equals(Slug(a!.Value), slug, StringComparison.OrdinalIgnoreCase));

    public static string Title(TechnicalArea a) => a switch
    {
        TechnicalArea.Frontend => "Frontend & UI",
        TechnicalArea.Api => "APIs & contracts",
        TechnicalArea.Integration => "Integrations",
        TechnicalArea.Performance => "Performance",
        _ => "Critical E2E",
    };

    public static string Purpose(TechnicalArea a) => a switch
    {
        TechnicalArea.Frontend => "Pages, forms and browser behaviour of the target application.",
        TechnicalArea.Api => "REST and GraphQL endpoints and the contracts they follow.",
        TechnicalArea.Integration => "Connected systems, messages and integration journeys.",
        TechnicalArea.Performance => "Response times and load behaviour against documented targets.",
        _ => "The release-critical user flows and their results per build.",
    };

    /// <summary>The specialist pages that own each stage of an area. Only existing routes.</summary>
    public static IReadOnlyList<(string Label, string Href)> Links(TechnicalArea a, string stage) => (a, stage) switch
    {
        (TechnicalArea.Frontend, "assess") => [("Browser discovery", TargetEnvironmentsRoute + "&tab=browser"), ("Technology coverage", "technology-coverage")],
        (TechnicalArea.Frontend, "configure") => [("Target application", TargetEnvironmentsRoute), ("Frontend Quality Review setup", "frontend-quality-review")],
        (TechnicalArea.Frontend, _) => [("Frontend Quality Review", "frontend-quality-review"), ("Previous runs", "frontend-quality-review?tab=History")],
        (TechnicalArea.Api, "assess") => [("Contracts", "api-quality-review/contracts"), ("Endpoint discovery", TargetEnvironmentsRoute + "&tab=endpoint")],
        (TechnicalArea.Api, "configure") => [("API Quality Review setup", "api-quality-review"), ("Contract tests", "api-quality-review/contracts?mode=configure")],
        (TechnicalArea.Api, _) => [("API Quality Review findings", "api-quality-review?tab=Findings"), ("Contract coverage", "api-quality-review/contracts?mode=coverage")],
        (TechnicalArea.Integration, "assess") => [("Integration Testing", "technical-testing/integrations?stage=discover"), ("Integration topology", "integration-topology")],
        (TechnicalArea.Integration, "configure") => [("Integration Testing", "technical-testing/integrations?stage=prepare"), ("Integration setup", "integration-quality-review?view=setup-evidence")],
        (TechnicalArea.Integration, _) => [("Integration Testing", "technical-testing/integrations?stage=verify"), ("Integration review", "integration-quality-review?view=review")],
        (TechnicalArea.Performance, "assess") => [("Performance Test Review", "performance-test-review")],
        (TechnicalArea.Performance, "configure") => [("Performance scenarios and readiness", "performance-test-review")],
        (TechnicalArea.Performance, _) => [("Performance runs", "performance-test-review")],
        (TechnicalArea.CriticalE2E, "assess") => [("Critical flows", "critical-e2e-regression")],
        (TechnicalArea.CriticalE2E, "configure") => [("Flows and build", "critical-e2e-regression")],
        _ => [("Critical E2E results", "critical-e2e-regression"), ("Release readiness", "release-readiness")],
    };

    // ---- dimensions -------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the area applies, from Technology Coverage applicability — and on what basis. The shared evaluator answers "Applicable" whenever a
    /// target is configured and "Needs configuration" both when the source shows the area without a target and when nothing is known at all; so
    /// the project's own source evidence (<see cref="ReviewApplicability.Evidence"/>) decides between "evidence available", "environment
    /// configured only" and "no supported evidence yet".
    /// </summary>
    public static AreaEvidence Evidence(AreaFacts f)
    {
        if (f.Applicability is not { } a) return AreaEvidence.NotRead;
        var fromSource = a.Evidence.Count > 0;
        return a.Status switch
        {
            ApplicabilityStatus.Applicable => fromSource ? AreaEvidence.Applies : AreaEvidence.EnvironmentOnly,
            ApplicabilityStatus.PartiallyApplicable => fromSource ? AreaEvidence.PartlyApplies : AreaEvidence.EnvironmentOnly,
            // The source shows the area but a target is missing (configuration is the gap); without source evidence nothing shows it yet.
            ApplicabilityStatus.NeedsConfiguration => fromSource ? AreaEvidence.Applies : AreaEvidence.NotEnoughEvidence,
            ApplicabilityStatus.NotApplicable => AreaEvidence.NotApplicable,
            ApplicabilityStatus.Unsupported => AreaEvidence.Unsupported,
            ApplicabilityStatus.NeedsRefresh => AreaEvidence.NeedsRefresh,
            _ => AreaEvidence.NotEnoughEvidence,
        };
    }

    /// <summary>The project's own source shows the area (not only the shared environment).</summary>
    public static bool SourceBacked(AreaFacts f) => Evidence(f) is AreaEvidence.Applies or AreaEvidence.PartlyApplies;

    public static bool InScope(AreaFacts f) => Evidence(f) is AreaEvidence.Applies or AreaEvidence.PartlyApplies or AreaEvidence.EnvironmentOnly or AreaEvidence.NeedsRefresh
        || f.Runs.Count > 0 || f.Static.Count > 0;

    public static AreaConfiguration Configuration(AreaFacts f)
    {
        if (Evidence(f) is AreaEvidence.NotApplicable or AreaEvidence.Unsupported && f.Runs.Count == 0) return AreaConfiguration.NotApplicable;
        if (f.Applicability is null && f.ConfigurationGaps.Count == 0) return AreaConfiguration.NotRead;
        return f.Applicability?.Status == ApplicabilityStatus.NeedsConfiguration || f.ConfigurationGaps.Count > 0 ? AreaConfiguration.NeedsConfiguration : AreaConfiguration.Configured;
    }

    public static AreaRuntime Runtime(AreaFacts f) => f.Latest?.State ?? AreaRuntime.NotRun;

    public static string EvidenceLabel(AreaEvidence e) => e switch
    {
        AreaEvidence.Applies => "Evidence available",
        AreaEvidence.PartlyApplies => "Partly supported",
        AreaEvidence.EnvironmentOnly => "Environment configured · no source evidence",
        AreaEvidence.NotApplicable => "Not applicable",
        AreaEvidence.Unsupported => "Not supported by BirkNext",
        AreaEvidence.NeedsRefresh => "Re-analysis needed",
        AreaEvidence.NotEnoughEvidence => "No supported evidence yet",
        _ => "Not read",
    };

    public static string ConfigurationLabel(AreaConfiguration c) => c switch
    {
        AreaConfiguration.Configured => "Configured",
        AreaConfiguration.NeedsConfiguration => "Configuration needed",
        AreaConfiguration.NotApplicable => "—",
        _ => "Not read",
    };

    public static string RuntimeLabel(AreaFacts f) => f.Latest is { } r
        ? r.Stale is null ? r.Verdict : $"{r.Verdict} · may be out of date"
        : "Not run";

    /// <summary>Tone word for CSS (neutral states never use the failure colour).</summary>
    public static string Tone(AreaEvidence e) => e switch
    {
        AreaEvidence.Applies => "ok", AreaEvidence.PartlyApplies or AreaEvidence.NeedsRefresh => "warn", AreaEvidence.EnvironmentOnly => "info", _ => "neutral",
    };

    public static string Tone(AreaConfiguration c) => c switch { AreaConfiguration.Configured => "ok", AreaConfiguration.NeedsConfiguration => "warn", _ => "neutral" };

    public static string Tone(AreaRuntime r) => r switch
    {
        AreaRuntime.Passed => "ok", AreaRuntime.Failed or AreaRuntime.Blocked => "bad",
        AreaRuntime.Incomplete or AreaRuntime.ReviewRequired or AreaRuntime.Running => "warn", _ => "neutral",
    };

    // ---- workspace ----------------------------------------------------------------------------------------------------------------------

    /// <summary>What the home and the stages are derived from.</summary>
    /// <param name="TargetName">The active Target Environment's name; null = none configured.</param>
    public sealed record Facts(string? ProjectKey, string? ProjectName, string? TargetName, string? TargetUrl, string? SnapshotBasis, IReadOnlyList<AreaFacts> Areas,
        string? Error = null)
    {
        public AreaFacts For(TechnicalArea a) => Areas.FirstOrDefault(x => x.Area == a) ?? new(a, null, [], [], []);
    }

    public sealed record NextAction(string Label, string Href, string Why);

    public static IReadOnlyList<AreaFacts> InScope(Facts f) => [.. f.Areas.Where(InScope)];

    /// <summary>One next action from stored state, most urgent first: no project → no target → a failed or blocked latest result → configuration
    /// an applicable area lacks → an applicable area never run → stale results → explore what was discovered.</summary>
    public static NextAction Next(Facts f)
    {
        if (f.ProjectKey is null)
            return new("Import or choose a project", "project-import", "Technical Testing works on the selected project's source and targets.");
        var inScope = InScope(f);
        if (f.TargetName is null && inScope.Any(a => a.Area is TechnicalArea.Frontend or TechnicalArea.Api or TechnicalArea.Performance))
            return new("Configure the target application", TargetEnvironmentsRoute, "Runtime checks need a Target Environment; none is active.");
        if (inScope.FirstOrDefault(a => Runtime(a) is AreaRuntime.Failed or AreaRuntime.Blocked) is { } failed)
            return new($"Investigate {Title(failed.Area)}", $"{VerifyRoute}?area={Slug(failed.Area)}",
                $"The latest {Title(failed.Area)} result is {failed.Latest!.Verdict.ToLowerInvariant()} ({failed.Latest.Where}).");
        if (inScope.FirstOrDefault(a => Configuration(a) == AreaConfiguration.NeedsConfiguration) is { } config)
            return new($"Configure {Title(config.Area)}", $"{ConfigureRoute}?area={Slug(config.Area)}",
                config.ConfigurationGaps.FirstOrDefault() ?? config.Applicability?.Action ?? config.Applicability?.Reason ?? "This area needs configuration before it can be checked.");
        if (inScope.FirstOrDefault(a => SourceBacked(a) && a.Runs.Count == 0) is { } notRun)
            return new($"Verify {Title(notRun.Area)}", $"{VerifyRoute}?area={Slug(notRun.Area)}", $"{Title(notRun.Area)} applies to this project and has no recorded run.");
        if (inScope.FirstOrDefault(a => a.Latest?.Stale is not null) is { } stale)
            return new($"Re-check {Title(stale.Area)}", $"{VerifyRoute}?area={Slug(stale.Area)}", stale.Latest!.Stale!);
        return new("Review discovered technical areas", AssessRoute, inScope.Count == 0
            ? "No technical area is supported by this project's evidence yet; see what was discovered and what is missing."
            : "Every applicable area has a recorded result; review the evidence and the results.");
    }

    public static IReadOnlyList<GuidedStep> Steps(Facts f)
    {
        if (f.ProjectKey is null)
            return [.. Stages.Select(s => new GuidedStep(s.Id, s.Title, GuidedStepState.Waiting, "No project", s.Question + " Select or import a project first.",
                DisabledReason: "Select a project first (Import Project → Use)."))];
        var inScope = InScope(f);
        var applies = inScope.Count;
        var notRead = f.Areas.Count(a => a.Applicability is null);
        var assess = new GuidedStep("assess", "Discover & assess", applies > 0 ? GuidedStepState.Done : GuidedStepState.Current,
            applies == 0 ? "No area supported yet" : $"{applies} of {Areas.Count} areas in scope",
            "Which technical areas apply to this project, from its analysed source, contracts, declared journeys and flows.",
            Evidence: string.Join(" · ", new[] { f.SnapshotBasis, notRead > 0 ? $"{notRead} area{(notRead == 1 ? "" : "s")} not read" : null }.Where(x => x is not null)),
            ActionLabel: "Open", Href: AssessRoute);
        var needsConfig = inScope.Count(a => Configuration(a) == AreaConfiguration.NeedsConfiguration) + (f.TargetName is null ? 1 : 0);
        var configure = new GuidedStep("configure", "Configure & prepare",
            applies == 0 ? GuidedStepState.Waiting : needsConfig > 0 ? GuidedStepState.Current : GuidedStepState.Done,
            f.TargetName is null ? "No target application" : needsConfig == 0 ? "Configured" : $"{needsConfig} need configuration",
            "Target application, authentication, integrations, scenarios, flows and Test Data the checks need — in the specialist modules that own them.",
            Evidence: f.TargetName is null ? null : $"Target: {f.TargetName}", ActionLabel: "Open", Href: ConfigureRoute);
        var runs = inScope.Count(a => a.Runs.Count > 0);
        var attention = inScope.Count(a => Runtime(a) is AreaRuntime.Failed or AreaRuntime.Blocked or AreaRuntime.Incomplete or AreaRuntime.ReviewRequired || a.Latest?.Stale is not null);
        var verify = new GuidedStep("verify", "Verify & investigate",
            applies == 0 ? GuidedStepState.Waiting : attention > 0 || (runs < applies && needsConfig == 0) ? GuidedStepState.Current : runs == applies ? GuidedStepState.Done : GuidedStepState.Waiting,
            runs == 0 ? "Nothing run yet" : $"{runs} of {applies} with results" + (attention > 0 ? $" · {attention} need attention" : ""),
            "Recorded runtime results per area, with their environment, time and identity, kept apart from static review.",
            ActionLabel: "Open", Href: VerifyRoute);
        return [assess, configure, verify];
    }
}
