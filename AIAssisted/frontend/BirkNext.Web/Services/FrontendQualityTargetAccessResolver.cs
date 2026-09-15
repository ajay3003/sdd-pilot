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
        var profile = context.ActiveProfile;
        var caps = context.RequiresAuthentication && profile.Authentication.AuthenticatedTestingMethod == AuthenticatedTestingMethod.LocalHttpsProxy
            ? await SafeResolveCapabilitiesAsync(profile, cancellationToken)
            : null;
        var sessionAuthenticated = context.IsAuthenticatedSessionAvailable || await SafeSessionAuthenticatedAsync();
        var edge = (services.GetService(typeof(ManagedEdgeRuntime)) as ManagedEdgeRuntime)?.For(profile);
        var proxy = (services.GetService(typeof(LocalHttpsProxyRuntime)) as LocalHttpsProxyRuntime)?.For(profile);
        return FrontendQualityTargetAccess.Build(context, caps, sessionAuthenticated, edge?.State, proxy?.State);
    }

    private async Task<AuthenticatedReviewCapabilities?> SafeResolveCapabilitiesAsync(FrontendAnalysisProfile profile, CancellationToken cancellationToken)
    {
        try { return await capabilities.ResolveAsync(ReviewAuthenticationIdentity.For(profile), cancellationToken); }
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
        var method = profile.Authentication.AuthenticatedTestingMethod;
        var apiStatus = capabilities?.ContextStatus ?? AuthenticatedApiContextStatus.NotApplicable;
        var apiAvailable = capabilities?.AuthenticatedApi == true;
        var enterpriseBlocked = managedEdgeState == ManagedEdgeState.TargetTabNotInspectable;
        var domAvailable = context.RequiresAuthentication && method == AuthenticatedTestingMethod.ManagedEdgeCdp && authenticatedBrowserSessionAvailable;
        var manual = profile.ManualVerification?.StatusFor(profile)
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
    public static FrontendQualityEngineAccessDecision Decide(FrontendQualityEngineAccessRequirements engine, FrontendQualityTargetAccessContext access)
    {
        var publicKind = engine.PublicAccess;
        if (!access.RequiresAuthentication)
            return Ready(engine, publicKind, publicKind == FrontendQualityEngineAccessKind.PublicHttp ? "Public HTTP" : "Browser runtime (public)");

        // Engines that only need the public frontend document/assets keep running for a protected application: the SPA shell is
        // served publicly and the engine analyses exactly that. The reachability probe reports a host-level authentication gate.
        if (!engine.RequiresBrowserDom && !engine.RequiresBrowserRuntime && !engine.RequiresAuthenticatedHttp)
            return Ready(engine, FrontendQualityEngineAccessKind.PublicHttp, "Public HTTP (frontend shell)", PublicShellNote);

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

    public static string ApiContextLabel(FrontendQualityTargetAccessContext access) => access.Method switch
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
