using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>An engine's configured prerequisite in generic capability terms, per surface. Null = the engine has no mode for that surface.</summary>
public sealed record FrontendQualityEnginePrerequisite(
    string Label,
    IReadOnlyList<FrontendQualityCapabilityKind>? Public,
    IReadOnlyList<FrontendQualityCapabilityKind>? Authenticated,
    IReadOnlyList<FrontendQualityCapabilityKind>? CompanionPage,
    FrontendQualityCompletionRule Rule)
{
    public IReadOnlyList<FrontendQualityCapabilityKind>? For(FrontendQualityReviewSurface surface) => surface switch
    {
        FrontendQualityReviewSurface.Public => Public,
        FrontendQualityReviewSurface.Authenticated => Authenticated,
        _ => CompanionPage,
    };
}

/// <summary>Which surface completes an engine. Never "every surface of the scope": scope is not engine policy.</summary>
public enum FrontendQualityCompletionRule
{
    /// <summary>The configured prerequisite is Public HTTP: the public surface completes it; authenticated HTTP is an extension when available.</summary>
    PublicFirst,
    /// <summary>The engine needs a capability, not a surface (a DOM, a lab browser): any applicable surface in scope completes it.</summary>
    AnySurface,
    /// <summary>The engine reads recorded Browser Companion / proxy evidence; it decides itself whether evidence is enough.</summary>
    RecordedEvidence,
}

/// <summary>Runtime facts reported by backend readiness, when known (Lighthouse: Node runtime and the Chromium/CDP target, separately).</summary>
public sealed record FrontendQualityRuntimeFacts(
    bool? NodeRuntimeAvailable = null, string? NodeRuntimeDetail = null,
    bool? CdpTargetAvailable = null, string? CdpTargetProvider = null, string? CdpTargetDetail = null);

/// <summary>
/// The engine execution planner: saved engine configuration (Enabled, Required/Optional) + selected review scope + each engine's own
/// generic prerequisite + the capabilities resolved for this review → one plan per engine with surface, provider, decision, reason and
/// release impact. Pure and deterministic. Scope selects surfaces; it never chooses one access mode for all engines and never turns an
/// optional engine into a required one.
/// </summary>
public static class FrontendQualityExecutionPlanner
{
    public const string BrowserDomLabel = "Browser DOM";
    public const string LighthouseLabel = "Node runtime + Chromium/CDP target";
    public const string CompanionPageNote = "The Browser Companion page's sign-in state is not recorded, so it is not counted as public or authenticated coverage.";

    private static readonly IReadOnlyDictionary<FrontendQualityEngineId, FrontendQualityEnginePrerequisite> Prerequisites =
        new Dictionary<FrontendQualityEngineId, FrontendQualityEnginePrerequisite>
        {
            [FrontendQualityEngineId.StaticSecurity] = new("Public HTTP", [FrontendQualityCapabilityKind.PublicHttp], [FrontendQualityCapabilityKind.AuthenticatedHttp], null, FrontendQualityCompletionRule.PublicFirst),
            [FrontendQualityEngineId.PassivePerformance] = new("Public HTTP", [FrontendQualityCapabilityKind.PublicHttp], [FrontendQualityCapabilityKind.AuthenticatedHttp], null, FrontendQualityCompletionRule.PublicFirst),
            [FrontendQualityEngineId.Accessibility] = new(BrowserDomLabel, [FrontendQualityCapabilityKind.BrowserDom], [FrontendQualityCapabilityKind.AuthenticatedBrowserDom], [FrontendQualityCapabilityKind.CompanionLiveDom], FrontendQualityCompletionRule.AnySurface),
            // Browser Runtime instruments console and network in a browser it controls, which a content script cannot do: no Companion mode.
            [FrontendQualityEngineId.BrowserRuntime] = new("Browser DOM (instrumented browser runtime)", [FrontendQualityCapabilityKind.BrowserDom], [FrontendQualityCapabilityKind.AuthenticatedBrowserDom], null, FrontendQualityCompletionRule.AnySurface),
            // Lighthouse runs a fresh lab navigation; reusing a signed-in browser would reset its storage. No authenticated mode.
            [FrontendQualityEngineId.Lighthouse] = new(LighthouseLabel, [FrontendQualityCapabilityKind.NodeRuntime, FrontendQualityCapabilityKind.CdpTarget], null, null, FrontendQualityCompletionRule.AnySurface),
            [FrontendQualityEngineId.PassiveSecurity] = new("Container runtime (ZAP) + Public HTTP", [FrontendQualityCapabilityKind.ContainerRuntime, FrontendQualityCapabilityKind.PublicHttp], null, null, FrontendQualityCompletionRule.AnySurface),
            [FrontendQualityEngineId.BrowserQuality] = new("Browser Companion evidence", null, null, [FrontendQualityCapabilityKind.RecordedBrowserEvidence], FrontendQualityCompletionRule.RecordedEvidence),
            [FrontendQualityEngineId.PerformanceQuality] = new("Browser Companion and/or Local HTTPS proxy evidence", null, null, [FrontendQualityCapabilityKind.RecordedBrowserEvidence], FrontendQualityCompletionRule.RecordedEvidence),
        };

    public static FrontendQualityEnginePrerequisite PrerequisiteFor(FrontendQualityEngineId engineId) => Prerequisites[engineId];

    public static IReadOnlyList<FrontendQualityReviewSurface> ScopeSurfaces(FrontendReviewAccessScope scope) => scope switch
    {
        FrontendReviewAccessScope.PublicOnly => [FrontendQualityReviewSurface.Public],
        FrontendReviewAccessScope.AuthenticatedOnly => [FrontendQualityReviewSurface.Authenticated],
        _ => [FrontendQualityReviewSurface.Public, FrontendQualityReviewSurface.Authenticated],
    };

    public static FrontendQualityExecutionPlan Plan(
        FrontendQualityActiveEngineSnapshot engines, FrontendReviewAccessScope scope, IReadOnlyList<FrontendQualityCapability> capabilities) =>
        new() { Scope = scope, Capabilities = capabilities, Engines = engines.Engines.Select(e => PlanEngine(e, scope, capabilities)).ToList() };

    public static FrontendQualityEnginePlan PlanEngine(
        FrontendQualityEngineActivation engine, FrontendReviewAccessScope scope, IReadOnlyList<FrontendQualityCapability> capabilities)
    {
        var prerequisite = Prerequisites[engine.EngineId];
        var plan = new FrontendQualityEnginePlan
        {
            EngineId = engine.EngineId, DisplayName = engine.DisplayName, Enabled = engine.Enabled, Selected = engine.Selected,
            Policy = engine.Policy, Scope = scope, Prerequisite = prerequisite.Label,
        };
        if (!engine.Enabled)
            return plan with { Decision = FrontendQualityPlanDecision.Excluded, ReleaseImpact = FrontendQualityReleaseImpact.NotApplicable,
                Reason = "Disabled in Target Environment → Frontend Review Engines." };
        if (!engine.Selected)
            return plan with { Decision = FrontendQualityPlanDecision.Excluded, ReleaseImpact = FrontendQualityReleaseImpact.NotApplicable,
                Reason = "Not selected for this review." };

        var surfaces = new List<FrontendQualitySurfacePlan>();
        if (prerequisite.Rule == FrontendQualityCompletionRule.RecordedEvidence)
            surfaces.Add(Surface(FrontendQualityReviewSurface.CompanionPage, prerequisite, capabilities, engine.DisplayName, primary: true));
        else
        {
            var inScope = ScopeSurfaces(scope);
            foreach (var surface in inScope)
            {
                var primary = prerequisite.Rule == FrontendQualityCompletionRule.AnySurface
                    || surface == FrontendQualityReviewSurface.Public || !inScope.Contains(FrontendQualityReviewSurface.Public);
                surfaces.Add(Surface(surface, prerequisite, capabilities, engine.DisplayName, primary));
            }
            // A live Browser Companion page can give a DOM engine the DOM a surface could not provide. It is its own surface.
            if (prerequisite.CompanionPage is { } companion
                && surfaces.Any(s => s.Decision is FrontendQualityPlanDecision.NotAssessed or FrontendQualityPlanDecision.Unsupported)
                && Missing(companion, capabilities) is null)
            {
                var noScopeSurfaceRuns = surfaces.All(s => s.Decision != FrontendQualityPlanDecision.Run);
                surfaces.Add(new(FrontendQualityReviewSurface.CompanionPage, FrontendQualityPlanDecision.Run, "Live Browser DOM",
                    Providers(companion, capabilities), $"axe runs in the live page through the Browser Companion. {CompanionPageNote}", Primary: noScopeSurfaceRuns));
            }
        }

        var applicable = surfaces.Any(s => s.Decision != FrontendQualityPlanDecision.NotApplicable);
        if (!applicable)
            return plan with
            {
                Surfaces = surfaces, Applicable = false, Decision = FrontendQualityPlanDecision.NotApplicable,
                ReleaseImpact = FrontendQualityReleaseImpact.NotApplicable, Reason = surfaces.FirstOrDefault()?.Reason ?? "Not applicable to this review scope.",
            };

        var primaries = surfaces.Where(s => s.Primary && s.Decision != FrontendQualityPlanDecision.NotApplicable).ToList();
        var running = primaries.FirstOrDefault(s => s.Decision == FrontendQualityPlanDecision.Run);
        var decision = running is not null ? FrontendQualityPlanDecision.Run
            : primaries.Any(s => s.Decision == FrontendQualityPlanDecision.NotAssessed) ? FrontendQualityPlanDecision.NotAssessed
            : FrontendQualityPlanDecision.Unsupported;
        var reason = running?.Reason is { Length: > 0 } r ? r
            : (primaries.FirstOrDefault(s => s.Decision == decision) ?? primaries.First()).Reason;
        var impact = decision == FrontendQualityPlanDecision.Run
            ? engine.Policy == FrontendQualityEngineRequirement.Required ? FrontendQualityReleaseImpact.Satisfied : FrontendQualityReleaseImpact.NonBlocking
            : engine.Policy == FrontendQualityEngineRequirement.Required ? FrontendQualityReleaseImpact.Blocking : FrontendQualityReleaseImpact.NonBlocking;
        return plan with { Surfaces = surfaces, Applicable = true, Decision = decision, Reason = reason, ReleaseImpact = impact };
    }

    private static FrontendQualitySurfacePlan Surface(FrontendQualityReviewSurface surface, FrontendQualityEnginePrerequisite prerequisite,
        IReadOnlyList<FrontendQualityCapability> capabilities, string engineName, bool primary)
    {
        var needed = prerequisite.For(surface);
        if (needed is null)
            return new(surface, FrontendQualityPlanDecision.NotApplicable, prerequisite.Label, null,
                surface == FrontendQualityReviewSurface.Authenticated ? $"{engineName} has no authenticated mode." : $"{engineName} has no {SurfaceLabel(surface).ToLowerInvariant()} mode.", primary);
        var label = surface == FrontendQualityReviewSurface.Authenticated ? AuthenticatedLabel(needed) : prerequisite.Label;
        if (Missing(needed, capabilities) is { } missing)
            return new(surface, missing.State == FrontendQualityCapabilityAvailability.Unsupported ? FrontendQualityPlanDecision.Unsupported : FrontendQualityPlanDecision.NotAssessed,
                label, null, missing.Reason, primary);
        return new(surface, FrontendQualityPlanDecision.Run, label, Providers(needed, capabilities), "", primary);
    }

    private static string AuthenticatedLabel(IReadOnlyList<FrontendQualityCapabilityKind> needed) =>
        needed.Contains(FrontendQualityCapabilityKind.AuthenticatedHttp) ? "Authenticated HTTP"
        : needed.Contains(FrontendQualityCapabilityKind.AuthenticatedBrowserDom) ? "Authenticated Browser DOM" : "Authenticated access";

    private static FrontendQualityCapability? Missing(IReadOnlyList<FrontendQualityCapabilityKind> needed, IReadOnlyList<FrontendQualityCapability> capabilities) =>
        needed.Select(kind => capabilities.FirstOrDefault(c => c.Kind == kind)
                ?? new FrontendQualityCapability(kind, FrontendQualityCapabilityAvailability.Unavailable, null, $"{CapabilityLabel(kind)} was not resolved for this review."))
            .FirstOrDefault(c => !c.Usable);

    private static string? Providers(IReadOnlyList<FrontendQualityCapabilityKind> needed, IReadOnlyList<FrontendQualityCapability> capabilities) =>
        string.Join(" + ", needed.Select(kind => capabilities.FirstOrDefault(c => c.Kind == kind)?.Provider).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct()) is { Length: > 0 } text ? text : null;

    public static string SurfaceLabel(FrontendQualityReviewSurface surface) => surface switch
    {
        FrontendQualityReviewSurface.Public => "Public",
        FrontendQualityReviewSurface.Authenticated => "Authenticated",
        _ => "Browser Companion page",
    };

    public static string CapabilityLabel(FrontendQualityCapabilityKind kind) => kind switch
    {
        FrontendQualityCapabilityKind.PublicHttp => "Public HTTP",
        FrontendQualityCapabilityKind.AuthenticatedHttp => "Authenticated HTTP/API context",
        FrontendQualityCapabilityKind.BrowserDom => "Public Browser DOM",
        FrontendQualityCapabilityKind.AuthenticatedBrowserDom => "Authenticated Browser DOM",
        FrontendQualityCapabilityKind.CompanionLiveDom => "Browser Companion live DOM",
        FrontendQualityCapabilityKind.NodeRuntime => "Node execution runtime",
        FrontendQualityCapabilityKind.CdpTarget => "Chromium/CDP execution target",
        FrontendQualityCapabilityKind.ContainerRuntime => "Container runtime",
        _ => "Recorded browser evidence",
    };

    public static string DecisionLabel(FrontendQualityPlanDecision decision) => decision switch
    {
        FrontendQualityPlanDecision.Run => "Run",
        FrontendQualityPlanDecision.NotApplicable => "Not applicable",
        FrontendQualityPlanDecision.NotAssessed => "Not assessed",
        FrontendQualityPlanDecision.Unsupported => "Unsupported",
        _ => "Not part of this review",
    };

    public static string ImpactLabel(FrontendQualityReleaseImpact impact) => impact switch
    {
        FrontendQualityReleaseImpact.Satisfied => "Satisfied",
        FrontendQualityReleaseImpact.Blocking => "Blocking",
        FrontendQualityReleaseImpact.NonBlocking => "Non-blocking",
        _ => "Not applicable",
    };
}

/// <summary>
/// Resolves the generic capabilities of one review from what the Target Environment and the running BirkNext instance provide. Each
/// capability names its provider; nothing here grants trust or authorization — a capability is "can run", never "may release".
/// </summary>
public static class FrontendQualityCapabilityResolver
{
    public const string PlaywrightProvider = "Playwright (headless Chromium, BirkNext backend)";
    public const string AuthenticatedBrowserProvider = "Authenticated review browser (Microsoft Edge, Managed Edge method)";
    public const string CompanionProvider = "Browser Companion";
    public const string BundledChromiumProvider = "Bundled Chromium (headless, launched by the Lighthouse runner)";
    public const string NodeProvider = "Node.js on the BirkNext backend host";
    public const string ProxyGatewayProvider = "Local HTTPS proxy gateway";
    public const string CompanionNoCdp = "The Browser Companion inspects pages with a content script; it has no DevTools/debugger access by design, so it is not a Chromium/CDP target.";

    /// <summary>Chromium-family browsers speak the DevTools protocol; compatibility is the protocol, never the product name ("Microsoft Edge" qualifies).</summary>
    public static bool IsChromiumCompatible(string? browserName, bool speaksDevToolsProtocol) =>
        speaksDevToolsProtocol && browserName is { Length: > 0 } name
        && (name.Contains("Chrom", StringComparison.OrdinalIgnoreCase) || name.Contains("Edge", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Edg/", StringComparison.OrdinalIgnoreCase) || name.Contains("Brave", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Opera", StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<FrontendQualityCapability> Resolve(
        FrontendAnalysisContext context, FrontendQualityTargetAccessContext access,
        BrowserCompanionLiveSession? companion = null, IReadOnlyCollection<string>? approvedOrigins = null,
        FrontendQualityRuntimeFacts? runtime = null)
    {
        var method = access.Method;
        var authConfigured = context.AuthenticationType != FrontendAuthenticationType.None;
        var capabilities = new List<FrontendQualityCapability>
        {
            new(FrontendQualityCapabilityKind.PublicHttp, FrontendQualityCapabilityAvailability.CheckedAtRun, "BirkNext backend HTTP client",
                "Reachability of the public frontend is checked when the review runs."),
            AuthenticatedHttp(access, method, authConfigured),
            new(FrontendQualityCapabilityKind.BrowserDom, FrontendQualityCapabilityAvailability.CheckedAtRun, PlaywrightProvider,
                "The backend browser's readiness is checked when the review runs."),
            AuthenticatedDom(access, method, authConfigured),
            CompanionDom(companion, approvedOrigins),
            Node(runtime),
            Cdp(runtime),
            new(FrontendQualityCapabilityKind.ContainerRuntime, FrontendQualityCapabilityAvailability.CheckedAtRun, "Container runtime (OWASP ZAP)",
                "The container runtime is checked when the review runs."),
            new(FrontendQualityCapabilityKind.RecordedBrowserEvidence, FrontendQualityCapabilityAvailability.CheckedAtRun, "Browser Companion / Local HTTPS proxy",
                "Recorded evidence is read when the review runs; the engine reports whether it was enough."),
        };
        return capabilities;
    }

    private static FrontendQualityCapability AuthenticatedHttp(FrontendQualityTargetAccessContext access, AuthenticatedTestingMethod method, bool authConfigured) =>
        // Evidence first: an available authenticated API context proves itself, whatever the saved authentication type says.
        method == AuthenticatedTestingMethod.LocalHttpsProxy && access.AuthenticatedApiAvailable
            ? new(FrontendQualityCapabilityKind.AuthenticatedHttp, FrontendQualityCapabilityAvailability.Available, ProxyGatewayProvider, "Authenticated API context available (memory only).")
        : !authConfigured ? new(FrontendQualityCapabilityKind.AuthenticatedHttp, FrontendQualityCapabilityAvailability.Unsupported, null,
                "No authentication is configured for this Target Environment.")
        : method == AuthenticatedTestingMethod.LocalHttpsProxy
            ? access.AuthenticatedApiAvailable
                ? new(FrontendQualityCapabilityKind.AuthenticatedHttp, FrontendQualityCapabilityAvailability.Available, ProxyGatewayProvider, "Authenticated API context available (memory only).")
                : new(FrontendQualityCapabilityKind.AuthenticatedHttp, FrontendQualityCapabilityAvailability.Unavailable, ProxyGatewayProvider,
                    access.ApiContextStatus == AuthenticatedApiContextStatus.Expired ? FrontendQualityTargetAccess.ProxyContextExpiredReason : FrontendQualityTargetAccess.ProxyContextMissingReason)
        : method == AuthenticatedTestingMethod.ManualOnly
            ? new(FrontendQualityCapabilityKind.AuthenticatedHttp, FrontendQualityCapabilityAvailability.Unsupported, null, FrontendQualityTargetAccess.ManualOnlyReason)
            : new(FrontendQualityCapabilityKind.AuthenticatedHttp, FrontendQualityCapabilityAvailability.Unsupported, null,
                "Authenticated HTTP needs the Local HTTPS proxy method; this Target Environment uses Managed Edge (CDP).");

    private static FrontendQualityCapability AuthenticatedDom(FrontendQualityTargetAccessContext access, AuthenticatedTestingMethod method, bool authConfigured)
    {
        if (method == AuthenticatedTestingMethod.ManagedEdgeCdp && access.AuthenticatedBrowserSessionAvailable)
            return new(FrontendQualityCapabilityKind.AuthenticatedBrowserDom, FrontendQualityCapabilityAvailability.Available, AuthenticatedBrowserProvider, "Authenticated review browser session available.");
        if (!authConfigured)
            return new(FrontendQualityCapabilityKind.AuthenticatedBrowserDom, FrontendQualityCapabilityAvailability.Unsupported, null, "No authentication is configured for this Target Environment.");
        if (method == AuthenticatedTestingMethod.LocalHttpsProxy)
            return new(FrontendQualityCapabilityKind.AuthenticatedBrowserDom, FrontendQualityCapabilityAvailability.Unsupported, null, FrontendQualityTargetAccess.ProxyDomUnavailable);
        if (method == AuthenticatedTestingMethod.ManualOnly)
            return new(FrontendQualityCapabilityKind.AuthenticatedBrowserDom, FrontendQualityCapabilityAvailability.Unsupported, null, FrontendQualityTargetAccess.ManualOnlyReason);
        if (access.AuthenticatedBrowserSessionAvailable)
            return new(FrontendQualityCapabilityKind.AuthenticatedBrowserDom, FrontendQualityCapabilityAvailability.Available, AuthenticatedBrowserProvider, "Authenticated review browser session available.");
        return new(FrontendQualityCapabilityKind.AuthenticatedBrowserDom, FrontendQualityCapabilityAvailability.Unavailable, AuthenticatedBrowserProvider,
            access.EnterpriseBlocked ? $"{FrontendQualityTargetAccess.SessionRequiredReason} {FrontendQualityTargetAccess.EnterpriseBlockedReason}" : FrontendQualityTargetAccess.SessionRequiredReason);
    }

    private static FrontendQualityCapability CompanionDom(BrowserCompanionLiveSession? live, IReadOnlyCollection<string>? approvedOrigins)
    {
        if (live is null || !live.ExtensionConnected)
            return new(FrontendQualityCapabilityKind.CompanionLiveDom, FrontendQualityCapabilityAvailability.Unavailable, CompanionProvider, "Browser Companion is not connected.");
        var pages = live.LivePages.Where(p => approvedOrigins is null || approvedOrigins.Contains(p.Origin, StringComparer.OrdinalIgnoreCase)).ToList();
        return pages.Count == 0
            ? new(FrontendQualityCapabilityKind.CompanionLiveDom, FrontendQualityCapabilityAvailability.Unavailable, CompanionProvider,
                "Browser Companion is connected, but no approved page of this Target Environment is open.")
            : new(FrontendQualityCapabilityKind.CompanionLiveDom, FrontendQualityCapabilityAvailability.Available, CompanionProvider,
                $"Live DOM available in {pages.Count} approved page{(pages.Count == 1 ? "" : "s")} ({string.Join(", ", pages.Select(p => p.Route).Distinct().Take(3))}).");
    }

    private static FrontendQualityCapability Node(FrontendQualityRuntimeFacts? runtime) => runtime?.NodeRuntimeAvailable switch
    {
        true => new(FrontendQualityCapabilityKind.NodeRuntime, FrontendQualityCapabilityAvailability.Available, NodeProvider, runtime.NodeRuntimeDetail ?? "Node.js runtime available."),
        false => new(FrontendQualityCapabilityKind.NodeRuntime, FrontendQualityCapabilityAvailability.Unavailable, NodeProvider, "Node execution runtime unavailable."),
        _ => new(FrontendQualityCapabilityKind.NodeRuntime, FrontendQualityCapabilityAvailability.CheckedAtRun, NodeProvider, "Checked when the review runs."),
    };

    private static FrontendQualityCapability Cdp(FrontendQualityRuntimeFacts? runtime) => runtime?.CdpTargetAvailable switch
    {
        true => new(FrontendQualityCapabilityKind.CdpTarget, FrontendQualityCapabilityAvailability.Available, runtime.CdpTargetProvider ?? BundledChromiumProvider,
            runtime.CdpTargetDetail ?? "Chromium-compatible DevTools target available."),
        false => new(FrontendQualityCapabilityKind.CdpTarget, FrontendQualityCapabilityAvailability.Unavailable, runtime.CdpTargetProvider ?? BundledChromiumProvider,
            "No compatible Chromium/CDP target available." + (runtime.CdpTargetDetail is { Length: > 0 } d ? $" {d}" : "")),
        _ => new(FrontendQualityCapabilityKind.CdpTarget, FrontendQualityCapabilityAvailability.CheckedAtRun, BundledChromiumProvider, "Checked when the review runs."),
    };
}
