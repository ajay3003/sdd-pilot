using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Where one testing capability of this BirkNext installation stands. Supported is never Available.</summary>
public enum FrontendTestingCapabilityState
{
    Available,
    Unavailable,
    RequiresSetup,
    NotSupported,
    /// <summary>The status could not be read (backend unreachable or not loaded yet).</summary>
    NotDetected,
    /// <summary>Depends on a Target Environment's own setup (authentication, paired browser); never judged here.</summary>
    TargetDependent,
}

/// <summary>
/// One capability: what it is, which review methods use it (one-to-many, no provider lock-in), its state, why, and at most one next action.
/// </summary>
public sealed record FrontendTestingCapability(
    string Id,
    string Group,
    string Name,
    string Description,
    IReadOnlyList<string> UsedBy,
    FrontendTestingCapabilityState State,
    string Reason,
    string? NextAction = null);

/// <summary>
/// Frontend Testing Capabilities: what this BirkNext installation can do, read from the capability status the page already loads.
/// It is not target readiness (Target Environments), not the review planner (Frontend Quality Review decides what runs at review time) and
/// not per-target configuration (Frontend Review Engines). It probes no remote target and runs nothing.
/// </summary>
public static class FrontendTestingCapabilitiesPresentation
{
    public const string Title = "Frontend Testing Capabilities";

    public const string Intro =
        "Shows which testing capabilities this BirkNext installation provides and which review methods use them. It does not say whether a target is ready or what a review will run: Frontend Quality Review decides that at review time. Which engines a target may use is configured separately under Frontend Review Engines.";

    public static readonly IReadOnlyList<string> Groups =
        ["Browser", "Performance", "Accessibility", "Security", "HTTP & network", "Authenticated testing", "Supporting runtime"];

    /// <summary>How to read this page, in four sentences that each separate two things people conflate.</summary>
    public static readonly IReadOnlyList<(string Term, string Meaning)> Reading =
    [
        ("Supported vs Available", "Supported means BirkNext can use the capability; Available means it is ready on this installation right now."),
        ("Installation vs target", "This page describes the installation. Whether a specific target can be tested is shown in Target Environments."),
        ("Capability vs review", "A capability being available does not run anything. Frontend Quality Review plans and runs engines per review."),
        ("Target-dependent", "Authenticated testing and the Browser Companion are set up per Target Environment, so their state is shown there, not here."),
    ];

    public static string StateLabel(FrontendTestingCapabilityState state) => state switch
    {
        FrontendTestingCapabilityState.Available => "Available",
        FrontendTestingCapabilityState.Unavailable => "Unavailable",
        FrontendTestingCapabilityState.RequiresSetup => "Requires setup",
        FrontendTestingCapabilityState.NotSupported => "Not supported",
        FrontendTestingCapabilityState.TargetDependent => "Set up per target",
        _ => "Not detected",
    };

    public static string Tone(FrontendTestingCapabilityState state) => state switch
    {
        FrontendTestingCapabilityState.Available => "ready",
        FrontendTestingCapabilityState.Unavailable or FrontendTestingCapabilityState.RequiresSetup => "needs-action",
        _ => "muted",
    };

    private static string Engine(FrontendQualityEngineId id) => FrontendQualityActiveEngines.DisplayName(id);

    /// <summary>Builds every capability from the loaded status report. A null report means the status could not be read.</summary>
    public static IReadOnlyList<FrontendTestingCapability> Build(FrontendQualityEngineStatusReportDto? status)
    {
        (FrontendTestingCapabilityState, string, string?) FromEngine(FrontendQualityEngineIdDto id, string readyReason)
        {
            if (status is null) return (FrontendTestingCapabilityState.NotDetected, "The capability status could not be read from the BirkNext backend.", "Refresh local capabilities");
            var engine = status.Engines.FirstOrDefault(e => e.EngineId == id);
            if (engine is null) return (FrontendTestingCapabilityState.NotDetected, "This backend does not report the capability.", null);
            if (!engine.Layer1Allowed) return (FrontendTestingCapabilityState.NotSupported, "Not allowed by this installation's policy.", null);
            if (!engine.Layer2Enabled) return (FrontendTestingCapabilityState.RequiresSetup, "Switched off for this installation.", "Turn it on under Technical details (Edit settings)");
            if (engine.Layer3Readiness?.IsAvailable != true)
                return (FrontendTestingCapabilityState.Unavailable,
                    engine.Layer3Readiness?.StatusReason is { Length: > 0 } reason ? reason : "Its local runtime prerequisite is not available.", "Refresh local capabilities");
            return (FrontendTestingCapabilityState.Available, readyReason, null);
        }

        var (browserState, browserReason, browserAction) = FromEngine(FrontendQualityEngineIdDto.BrowserRuntime, "The BirkNext browser can render pages and expose their DOM.");
        var (axeState, axeReason, axeAction) = FromEngine(FrontendQualityEngineIdDto.Accessibility, "Accessibility rules can run in a Browser DOM.");
        var (lhState, lhReason, lhAction) = FromEngine(FrontendQualityEngineIdDto.Lighthouse, "Node runtime and a Chromium/CDP target are available for lab runs.");
        var (zapState, zapReason, zapAction) = FromEngine(FrontendQualityEngineIdDto.PassiveSecurity, "The passive scanner container can start. It never runs an active scan.");

        return
        [
            new("browser-dom", "Browser", "Browser DOM (Playwright)", "A browser BirkNext controls, used to render pages and read their DOM.",
                [Engine(FrontendQualityEngineId.BrowserRuntime), Engine(FrontendQualityEngineId.Accessibility)], browserState, browserReason, browserAction),
            new("lighthouse", "Performance", "Lighthouse lab runtime", "Node runtime plus a Chromium/CDP target for Lighthouse lab measurements.",
                [Engine(FrontendQualityEngineId.Lighthouse)], lhState, lhReason, lhAction),
            new("axe", "Accessibility", "Accessibility rules (axe-core)", "Automated WCAG rule checks run inside a Browser DOM.",
                [Engine(FrontendQualityEngineId.Accessibility)], axeState, axeReason, axeAction),
            new("zap", "Security", "Passive security scanner (OWASP ZAP)", "Observes responses for security issues without attacking the target.",
                [Engine(FrontendQualityEngineId.PassiveSecurity)], zapState, zapReason, zapAction),
            new("http", "HTTP & network", "Public HTTP client", "Anonymous HTTP requests to a target's public pages and assets, made by the BirkNext backend.",
                [Engine(FrontendQualityEngineId.StaticSecurity), Engine(FrontendQualityEngineId.PassivePerformance)],
                status is null ? FrontendTestingCapabilityState.NotDetected : FrontendTestingCapabilityState.Available,
                status is null ? "The BirkNext backend could not be reached." : "Built into the BirkNext backend; no setup needed.",
                status is null ? "Refresh local capabilities" : null),
            new("authenticated", "Authenticated testing", "Authenticated session (Local HTTPS proxy / dedicated Edge)", "Signed-in testing through a proxy or browser that a Target Environment sets up.",
                [Engine(FrontendQualityEngineId.BrowserRuntime), Engine(FrontendQualityEngineId.Accessibility), Engine(FrontendQualityEngineId.BrowserQuality), Engine(FrontendQualityEngineId.PerformanceQuality)],
                FrontendTestingCapabilityState.TargetDependent, "Configured and checked per Target Environment under Authentication.", "Open Target Environments → Authentication"),
            new("companion", "Supporting runtime", "Browser Companion", "The paired browser extension that records page and runtime evidence.",
                [Engine(FrontendQualityEngineId.BrowserQuality), Engine(FrontendQualityEngineId.PerformanceQuality)],
                FrontendTestingCapabilityState.TargetDependent, "Paired per Target Environment in Browser Discovery.", "Open Target Environments → Browser Discovery"),
        ];
    }

    /// <summary>"N available · N need setup · N unavailable · N set up per target", never counting a target-dependent capability as missing.</summary>
    public static string Summary(IReadOnlyCollection<FrontendTestingCapability> capabilities)
    {
        int Count(params FrontendTestingCapabilityState[] states) => capabilities.Count(c => states.Contains(c.State));
        return $"{Count(FrontendTestingCapabilityState.Available)} available · {Count(FrontendTestingCapabilityState.RequiresSetup)} need setup · " +
            $"{Count(FrontendTestingCapabilityState.Unavailable, FrontendTestingCapabilityState.NotDetected, FrontendTestingCapabilityState.NotSupported)} unavailable · " +
            $"{Count(FrontendTestingCapabilityState.TargetDependent)} set up per target";
    }

    /// <summary>"What needs setup": every installation capability that is not available and has something to do about it.</summary>
    public static IReadOnlyList<FrontendTestingCapability> NeedsSetup(IReadOnlyCollection<FrontendTestingCapability> capabilities) =>
        capabilities.Where(c => c.State is FrontendTestingCapabilityState.RequiresSetup or FrontendTestingCapabilityState.Unavailable or FrontendTestingCapabilityState.NotDetected).ToList();

    /// <summary>Where a target-dependent next action leads. Null for actions handled on this page.</summary>
    public static string? ActionHref(FrontendTestingCapability capability) => capability.Id switch
    {
        "authenticated" => FrontendQualityTargetAccess.TargetEnvironmentsHref + "&tab=auth",
        "companion" => FrontendQualityTargetAccess.TargetEnvironmentsHref + "&tab=browser",
        _ => null,
    };
}
