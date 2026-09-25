using BirkNext.LocalHttpsProxy;
using BirkNext.ManagedEdge;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Resolves how the selected Target Environment can be accessed by the quality engines right now. Combines the saved environment
/// (authentication requirement, authenticated testing method, manual verification record) with transient runtime evidence: the backend
/// authenticated-review capability matrix (Local HTTPS proxy API context), the review's authenticated browser session, and the Managed Edge
/// attach state. Returns non-secret status only.
/// </summary>
public interface IFrontendQualityTargetAccessResolver
{
    Task<FrontendQualityTargetAccessContext> ResolveAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default);
}

public sealed class FrontendQualityTargetAccessResolver(
    IAuthenticatedReviewCapabilitiesService capabilities,
    IAuthenticatedBrowserSessionService sessions,
    IServiceProvider services) : IFrontendQualityTargetAccessResolver
{
    public async Task<FrontendQualityTargetAccessContext> ResolveAsync(FrontendAnalysisContext context, CancellationToken cancellationToken = default)
    {
        // Identity and fingerprints come from the saved profile (computed by the context factory), never re-derived from the
        // data-minimized profile copy: a mismatched fingerprint would hide an available proxy context and report "Stale".
        var identity = ReviewAuthenticationIdentity.ForContext(context);
        var caps = context.RequiresAuthentication && identity.Method == AuthenticatedTestingMethod.LocalHttpsProxy
            ? await SafeResolveCapabilitiesAsync(identity, cancellationToken)
            : null;
        var sessionAuthenticated = context.IsAuthenticatedSessionAvailable || await SafeSessionAuthenticatedAsync();
        var manualFingerprint = context.ManualVerificationFingerprint ?? ManualAuthenticationVerificationEvidence.Fingerprint(context.ActiveProfile);
        var edge = (services.GetService(typeof(ManagedEdgeRuntime)) as ManagedEdgeRuntime)?.ForFingerprint(manualFingerprint);
        var proxy = (services.GetService(typeof(LocalHttpsProxyRuntime)) as LocalHttpsProxyRuntime)?.ForFingerprint(identity.ContextFingerprint);
        return FrontendQualityTargetAccess.Build(context, caps, sessionAuthenticated, edge?.State, proxy?.State);
    }

    private async Task<AuthenticatedReviewCapabilities?> SafeResolveCapabilitiesAsync(AuthenticatedReviewIdentity identity, CancellationToken cancellationToken)
    {
        try { return await capabilities.ResolveAsync(identity, cancellationToken); }
        catch { return null; }
    }

    private async Task<bool> SafeSessionAuthenticatedAsync()
    {
        try { return await sessions.GetStatusAsync() == AuthenticatedBrowserSessionStatus.Authenticated; }
        catch { return false; }
    }
}

/// <summary>Pure construction of the access context and the per-engine fail-fast decisions. No I/O, fully unit-testable.</summary>
public static class FrontendQualityTargetAccess
{
    public const string TargetEnvironmentsHref = "/admin/system-settings?section=target-environments";

    /// <summary>
    /// Deep link to Target Environment → Frontend Review Engines, where engine activation and Required/Optional policy
    /// are saved. Uses the existing System Settings query convention (<c>?section=</c>) with one extra key, so no second
    /// routing mechanism is introduced. Frontend Quality Review links here instead of editing engine configuration itself.
    /// </summary>
    public const string FrontendReviewEnginesHref = TargetEnvironmentsHref + "&tab=features";

    /// <summary>
    /// Deep link to System Settings → Frontend Engine Capabilities: what this installation can run (deployment policy,
    /// deployment setting, runtime readiness, effective availability). The section key stays "frontend-quality-engines"
    /// because it is part of existing URLs and the backend route; only the user-facing label changed.
    /// </summary>
    public const string FrontendEngineCapabilitiesHref = "/admin/system-settings?section=frontend-quality-engines";

    public const string ProxyDomUnavailable = "Authenticated DOM unavailable with Local HTTPS Proxy. The proxy provides an authenticated API context only; browser DOM/runtime checks of the signed-in application need the Managed Edge (CDP) browser context.";
    public const string ManualOnlyReason = "Current authentication method supports manual verification only. No automated authenticated engine access exists for this environment.";
    public const string EnterpriseBlockedReason = "Managed Edge (CDP) attach to the target tab is blocked by enterprise browser protection. BirkNext does not bypass browser protection.";
    public const string SessionRequiredReason = "Authenticated browser session not available. Sign in for review to establish the authenticated browser context this engine needs.";
    public const string ProxyContextMissingReason = "Authenticated context not available. Start the Local HTTPS proxy and sign in to the target application in the proxy-configured browser.";
    public const string ProxyContextExpiredReason = "Authenticated context expired. Continue using the target application in the proxy-configured browser to refresh it.";
    public const string PublicShellNote = "Public HTTP to the frontend host (document and static assets). Authenticated user flows are not covered by this engine.";

    /// <summary>Builds the access context from configuration and non-secret runtime evidence.</summary>
    public static FrontendQualityTargetAccessContext Build(
        FrontendAnalysisContext context,
        AuthenticatedReviewCapabilities? capabilities,
        bool authenticatedBrowserSessionAvailable,
        ManagedEdgeState? managedEdgeState,
        LocalHttpsProxyState? proxyState)
    {
        var profile = context.ActiveProfile;
        var method = ReviewAuthenticationIdentity.ForContext(context).Method;
        var apiStatus = capabilities?.ContextStatus ?? AuthenticatedApiContextStatus.NotApplicable;
        var apiAvailable = capabilities?.AuthenticatedApi == true;
        var enterpriseBlocked = managedEdgeState == ManagedEdgeState.TargetTabNotInspectable;
        var domAvailable = context.RequiresAuthentication && method == AuthenticatedTestingMethod.ManagedEdgeCdp && authenticatedBrowserSessionAvailable;
        // Prefer the factory-resolved status (computed against the saved profile); fall back to the profile copy for contexts built by hand.
        var manual = context.ManualVerificationStatus
            ?? profile.ManualVerification?.StatusFor(profile)
            ?? (method == AuthenticatedTestingMethod.ManualOnly || profile.Authentication.VerificationMode == AuthenticationVerificationMode.ManualManagedEdge
                ? ManualAuthenticationVerificationStatus.Required
                : ManualAuthenticationVerificationStatus.NotRequired);

        var mode = !context.RequiresAuthentication ? FrontendQualityTargetAccessMode.PublicDirect
            : method switch
            {
                AuthenticatedTestingMethod.ManualOnly => FrontendQualityTargetAccessMode.ManualOnly,
                AuthenticatedTestingMethod.LocalHttpsProxy => apiAvailable ? FrontendQualityTargetAccessMode.LocalHttpsProxy : FrontendQualityTargetAccessMode.AuthRequiredContextMissing,
                _ => domAvailable ? FrontendQualityTargetAccessMode.ManagedEdgeCdp
                    : enterpriseBlocked ? FrontendQualityTargetAccessMode.EnterpriseBlocked
                    : FrontendQualityTargetAccessMode.AuthRequiredContextMissing,
            };

        var reason = !context.RequiresAuthentication ? "Target does not require authentication; engines access it directly."
            : method switch
            {
                AuthenticatedTestingMethod.ManualOnly => ManualOnlyReason,
                AuthenticatedTestingMethod.LocalHttpsProxy => capabilities?.Reason is { Length: > 0 } r ? r
                    : apiStatus == AuthenticatedApiContextStatus.Expired ? ProxyContextExpiredReason : ProxyContextMissingReason,
                _ => domAvailable ? "Authenticated browser session available for DOM/runtime engines."
                    : enterpriseBlocked ? EnterpriseBlockedReason
                    : SessionRequiredReason,
            };

        return new FrontendQualityTargetAccessContext
        {
            EnvironmentName = profile.Name,
            EnvironmentType = profile.EnvironmentType.ToString(),
            TargetUrl = context.TargetUrl,
            RequiresAuthentication = context.RequiresAuthentication,
            AuthenticationType = context.AuthenticationType,
            Method = method,
            Mode = mode,
            ApiContextStatus = apiStatus,
            AuthenticatedApiAvailable = apiAvailable,
            ProxyState = proxyState,
            AuthenticatedBrowserSessionAvailable = authenticatedBrowserSessionAvailable,
            AuthenticatedBrowserDomAvailable = domAvailable,
            ManagedEdgeState = managedEdgeState,
            ManualVerificationStatus = manual,
            Reason = reason,
        };
    }

    /// <summary>Fallback when no resolver is available: configuration plus the session flag already carried by the analysis context.</summary>
    public static FrontendQualityTargetAccessContext FromContext(FrontendAnalysisContext context) =>
        Build(context, null, context.IsAuthenticatedSessionAvailable, null, null);

    public static IReadOnlyDictionary<FrontendQualityEngineId, FrontendQualityEngineAccessDecision> DecideAll(FrontendQualityTargetAccessContext access) =>
        FrontendQualityEngineAccessRegistry.All.ToDictionary(r => r.EngineId, r => Decide(r, access));

    /// <summary>
    /// Matches an engine's declared requirements against the resolved access. Never forces one access method on all engines:
    /// public-HTTP engines stay Ready for a protected application whose frontend shell is public, DOM engines are Unsupported with the
    /// Local HTTPS proxy, and every blocker is reported with its precise reason instead of waiting for a timeout.
    /// </summary>
    public const string BrowserCompanionLabel = "Browser Companion (your managed Edge session)";
    public const string PerformanceQualityLabel = "Browser Companion + Local HTTPS Proxy evidence (passive)";

    public static FrontendQualityEngineAccessDecision Decide(FrontendQualityEngineAccessRequirements engine, FrontendQualityTargetAccessContext access)
    {
        // The Browser Companion observes the user's own signed-in browser: the access path is the same for public and protected
        // applications and for every authenticated testing method. Whether evidence exists is decided by the engine itself.
        if (engine.EngineId == FrontendQualityEngineId.BrowserQuality)
            return Ready(engine, FrontendQualityEngineAccessKind.BrowserCompanion, BrowserCompanionLabel,
                "Evidence is collected by the BirkNext Browser Companion extension in your normal managed Edge; no CDP, Playwright or token handoff.");
        // BirkNext Performance Quality reads recorded page evidence from both passive sources; it never contacts the target, so it is
        // always Ready here and reports its own coverage (browser vs API) as Complete / Partial / Not assessed per evidence category.
        if (engine.EngineId == FrontendQualityEngineId.PerformanceQuality)
            return Ready(engine, FrontendQualityEngineAccessKind.BrowserCompanion, PerformanceQualityLabel,
                "Browser metrics come from the BirkNext Browser Companion in your managed Edge; API/network metrics from the Local HTTPS proxy. No Playwright, CDP, Lighthouse or token handoff.");

        var publicKind = engine.PublicAccess;
        if (!access.RequiresAuthentication)
            return Ready(engine, publicKind, publicKind == FrontendQualityEngineAccessKind.PublicHttp ? "Public HTTP" : "Browser runtime (public)");

        // Engines that only need the public frontend document/assets keep running for a protected application: the SPA shell is
        // served publicly and the engine analyses exactly that. The reachability probe reports a host-level authentication gate.
        // When the engine can also consume the Local HTTPS proxy API context, the label says so; the context state decides whether
        // the authenticated API-surface probes actually run (missing/expired context is reported, never downgraded silently).
        if (!engine.RequiresBrowserDom && !engine.RequiresBrowserRuntime && !engine.RequiresAuthenticatedHttp)
        {
            if (engine.SupportsProxyAuthenticatedContext && access.Method == AuthenticatedTestingMethod.LocalHttpsProxy)
                return access.AuthenticatedApiAvailable
                    ? Ready(engine, FrontendQualityEngineAccessKind.PublicHttp, "Public HTTP (frontend shell) + Authenticated HTTP (Local HTTPS Proxy)",
                        $"{PublicShellNote} Authenticated API-surface probes run through the proxy gateway.")
                    : Ready(engine, FrontendQualityEngineAccessKind.PublicHttp, "Public HTTP (frontend shell); authenticated API surface not available",
                        $"{PublicShellNote} {(access.ApiContextStatus == AuthenticatedApiContextStatus.Expired ? ProxyContextExpiredReason : ProxyContextMissingReason)}");
            return Ready(engine, FrontendQualityEngineAccessKind.PublicHttp, "Public HTTP (frontend shell)", PublicShellNote);
        }

        if (engine.RequiresAuthenticatedHttp)
        {
            return access.Method switch
            {
                AuthenticatedTestingMethod.LocalHttpsProxy when engine.SupportsProxyAuthenticatedContext && access.AuthenticatedApiAvailable =>
                    Ready(engine, FrontendQualityEngineAccessKind.AuthenticatedHttp, "Authenticated HTTP (Local HTTPS Proxy)"),
                AuthenticatedTestingMethod.LocalHttpsProxy when engine.SupportsProxyAuthenticatedContext =>
                    Blocked(engine, FrontendQualityEngineAccessKind.AuthenticatedHttp, "Authenticated HTTP (Local HTTPS Proxy)",
                        access.ApiContextStatus == AuthenticatedApiContextStatus.Expired ? FrontendQualityEngineOutcomeReason.AuthenticatedContextExpired : FrontendQualityEngineOutcomeReason.AuthenticatedContextUnavailable,
                        access.ApiContextStatus == AuthenticatedApiContextStatus.Expired ? ProxyContextExpiredReason : ProxyContextMissingReason,
                        "Start proxy and sign in", TargetEnvironmentsHref),
                AuthenticatedTestingMethod.ManualOnly =>
                    Unsupported(engine, FrontendQualityEngineAccessKind.AuthenticatedHttp, "Authenticated HTTP", FrontendQualityEngineOutcomeReason.ManualOnlyMethod, ManualOnlyReason),
                _ => Unsupported(engine, FrontendQualityEngineAccessKind.AuthenticatedHttp, "Authenticated HTTP",
                    FrontendQualityEngineOutcomeReason.AuthenticationModeUnsupported,
                    "Authenticated HTTP execution is available only with the Local HTTPS proxy method."),
            };
        }

        // DOM / browser-runtime engines.
        const string domLabel = "Authenticated browser session";
        if (!engine.SupportsAuthenticatedBrowserSession)
            return Unsupported(engine, publicKind, "Browser runtime (public)", FrontendQualityEngineOutcomeReason.AuthenticationModeUnsupported,
                "This engine has no authenticated mode; it cannot assess a protected application.");

        return access.Method switch
        {
            AuthenticatedTestingMethod.ManualOnly =>
                Unsupported(engine, FrontendQualityEngineAccessKind.AuthenticatedBrowserSession, domLabel, FrontendQualityEngineOutcomeReason.ManualOnlyMethod, ManualOnlyReason, "Open Authentication", TargetEnvironmentsHref),
            AuthenticatedTestingMethod.LocalHttpsProxy =>
                Unsupported(engine, FrontendQualityEngineAccessKind.AuthenticatedBrowserSession, domLabel, FrontendQualityEngineOutcomeReason.BrowserDomUnavailableForMethod,
                    access.EnterpriseBlocked ? $"{ProxyDomUnavailable} {EnterpriseBlockedReason}" : ProxyDomUnavailable,
                    access.EnterpriseBlocked ? null : "Switch to Managed Edge (CDP) for DOM checks", access.EnterpriseBlocked ? null : TargetEnvironmentsHref),
            _ when access.AuthenticatedBrowserDomAvailable =>
                Ready(engine, FrontendQualityEngineAccessKind.AuthenticatedBrowserSession, domLabel),
            _ when access.EnterpriseBlocked =>
                Blocked(engine, FrontendQualityEngineAccessKind.AuthenticatedBrowserSession, domLabel, FrontendQualityEngineOutcomeReason.EnterpriseBrowserProtectionBlocked,
                    $"{SessionRequiredReason} {EnterpriseBlockedReason}", "Open Target Environment", TargetEnvironmentsHref),
            _ => Blocked(engine, FrontendQualityEngineAccessKind.AuthenticatedBrowserSession, domLabel, FrontendQualityEngineOutcomeReason.AuthenticationRequired,
                SessionRequiredReason, "Sign in for review"),
        };
    }

    /// <summary>
    /// Whether the scope a finished review covered needed a sign-in, and with which provider: "Yes (Microsoft Entra ID)"
    /// or "No". A configured provider on a public scope is not a contradiction, so the label never implies one.
    /// </summary>
    public static string ReviewedScopeAuthenticationLabel(FrontendQualityTargetAccessContext access) =>
        access.RequiresAuthentication ? $"Yes ({AuthenticationPresentation.ProviderLabel(access.AuthenticationType)})" : "No";

    public static string ModeLabel(FrontendQualityTargetAccessMode mode) => mode switch
    {
        FrontendQualityTargetAccessMode.PublicDirect => "Public (direct)",
        FrontendQualityTargetAccessMode.LocalHttpsProxy => "Local HTTPS Proxy — authenticated API context available",
        FrontendQualityTargetAccessMode.ManagedEdgeCdp => "Managed Edge (CDP) — authenticated browser session available",
        FrontendQualityTargetAccessMode.ManualOnly => "Manual verification only",
        FrontendQualityTargetAccessMode.AuthRequiredContextMissing => "Authentication required — context not available",
        FrontendQualityTargetAccessMode.EnterpriseBlocked => "Blocked by enterprise browser protection",
        _ => mode.ToString(),
    };

    public static string ApiContextLabel(FrontendQualityTargetAccessContext access) =>
        // "Not available" beside "Authentication: Not required" read as a problem; for a public target it is simply not needed.
        !access.RequiresAuthentication && access.ApiContextStatus != AuthenticatedApiContextStatus.Available
            ? "Not needed — target does not require authentication"
            : ApiContextLabelCore(access);

    private static string ApiContextLabelCore(FrontendQualityTargetAccessContext access) => access.Method switch
    {
        AuthenticatedTestingMethod.LocalHttpsProxy => access.ApiContextStatus switch
        {
            AuthenticatedApiContextStatus.Available => "Available — memory only",
            AuthenticatedApiContextStatus.Expired => "Expired",
            AuthenticatedApiContextStatus.Stale => "Stale — environment changed",
            AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic => "Not available — waiting for authenticated traffic",
            _ => "Not available",
        },
        AuthenticatedTestingMethod.ManualOnly => "Not applicable — manual verification only",
        _ => access.AuthenticatedBrowserSessionAvailable ? "Available — authenticated browser session" : "Not available",
    };

    public static string BrowserDomLabel(FrontendQualityTargetAccessContext access) =>
        !access.RequiresAuthentication ? "Available (public)"
        : access.AuthenticatedBrowserDomAvailable ? "Available — authenticated browser session"
        : access.Method == AuthenticatedTestingMethod.LocalHttpsProxy ? "Not available with Local HTTPS Proxy"
        : access.Method == AuthenticatedTestingMethod.ManualOnly ? "Not available — manual verification only"
        : access.EnterpriseBlocked ? "Not available — blocked by enterprise browser protection"
        : "Not available — sign in for review";

    public static string ManualVerificationLabel(ManualAuthenticationVerificationStatus status) => status switch
    {
        ManualAuthenticationVerificationStatus.Passed => "Passed",
        ManualAuthenticationVerificationStatus.Failed => "Failed",
        ManualAuthenticationVerificationStatus.Pending => "Pending",
        ManualAuthenticationVerificationStatus.Required => "Required",
        ManualAuthenticationVerificationStatus.Stale => "Stale — re-verify after settings change",
        _ => "Not required",
    };

    /// <summary>Automated engine access, kept separate from manual verification: a passed manual verification never grants automation access.</summary>
    public static string AutomatedAccessLabel(FrontendQualityTargetAccessContext access) => access.Mode switch
    {
        FrontendQualityTargetAccessMode.PublicDirect => "Available — public target",
        FrontendQualityTargetAccessMode.LocalHttpsProxy => "Available — authenticated API context (no DOM)",
        FrontendQualityTargetAccessMode.ManagedEdgeCdp => "Available — authenticated browser session",
        FrontendQualityTargetAccessMode.ManualOnly => "Unavailable — manual verification only",
        FrontendQualityTargetAccessMode.EnterpriseBlocked => "Unavailable — blocked by enterprise browser protection",
        _ => "Unavailable — authenticated context missing",
    };

    private static FrontendQualityEngineAccessDecision Ready(FrontendQualityEngineAccessRequirements engine, FrontendQualityEngineAccessKind kind, string label, string reason = "") =>
        new(engine.EngineId, kind, label, FrontendQualityEngineAccessState.Ready, FrontendQualityEngineOutcomeReason.None, reason);

    private static FrontendQualityEngineAccessDecision Blocked(FrontendQualityEngineAccessRequirements engine, FrontendQualityEngineAccessKind kind, string label,
        FrontendQualityEngineOutcomeReason reason, string text, string? action = null, string? href = null) =>
        new(engine.EngineId, kind, label, FrontendQualityEngineAccessState.Blocked, reason, text, action, href);

    private static FrontendQualityEngineAccessDecision Unsupported(FrontendQualityEngineAccessRequirements engine, FrontendQualityEngineAccessKind kind, string label,
        FrontendQualityEngineOutcomeReason reason, string text, string? action = null, string? href = null) =>
        new(engine.EngineId, kind, label, FrontendQualityEngineAccessState.Unsupported, reason, text, action, href);
}
