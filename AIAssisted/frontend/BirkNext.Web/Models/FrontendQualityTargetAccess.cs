using System.Text.Json.Serialization;
using BirkNext.LocalHttpsProxy;
using BirkNext.ManagedEdge;

namespace BirkNext.Web.Models;

/// <summary>
/// How the selected Target Environment can currently be accessed by automated quality engines. Resolved once per review from the
/// saved environment (authentication requirement + authenticated testing method) and the transient runtime state (proxy context,
/// review browser session, Managed Edge attach state). Never carries a credential.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityTargetAccessMode
{
    /// <summary>The target does not require authentication; engines access it directly.</summary>
    PublicDirect,
    /// <summary>Local HTTPS proxy method with an authenticated API context available (API access only, never DOM).</summary>
    LocalHttpsProxy,
    /// <summary>Managed Edge (CDP) method with an authenticated review browser session available for DOM/runtime engines.</summary>
    ManagedEdgeCdp,
    /// <summary>Manual verification only; no automated authenticated access exists.</summary>
    ManualOnly,
    /// <summary>Authentication is required and an automated method is selected, but its runtime context is not available right now.</summary>
    AuthRequiredContextMissing,
    /// <summary>Managed Edge method, but the browser refuses debugger attachment (enterprise browser protection).</summary>
    EnterpriseBlocked,
}

/// <summary>The access path an engine uses to reach the target. Shown in the review's "Access" column.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityEngineAccessKind
{
    /// <summary>Anonymous HTTP fetches of the public frontend document and static assets (backend network path).</summary>
    PublicHttp,
    /// <summary>Approved authenticated HTTP request executed by the backend gateway (no token exposure).</summary>
    AuthenticatedHttp,
    /// <summary>Authenticated review browser session (rendered DOM of the signed-in application).</summary>
    AuthenticatedBrowserSession,
    /// <summary>Anonymous browser navigation/runtime instrumentation.</summary>
    BrowserRuntime,
    /// <summary>The user's own signed-in managed Edge session, observed by the paired BirkNext Browser Companion (extension). No CDP, no Playwright, no token.</summary>
    BrowserCompanion,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FrontendQualityEngineAccessState { Ready, Blocked, Unsupported }

/// <summary>
/// Declared access requirements of one quality engine. Audited against the engine implementation (see
/// <see cref="FrontendQualityEngineAccessRegistry"/>); orchestration matches these against the resolved target access before any
/// request is made, so known blockers fail fast instead of timing out.
/// </summary>
public sealed record FrontendQualityEngineAccessRequirements(
    FrontendQualityEngineId EngineId,
    bool RequiresTargetReachability,
    bool RequiresPublicHttp,
    bool RequiresAuthenticatedHttp,
    bool RequiresBrowserDom,
    bool RequiresBrowserRuntime,
    bool SupportsProxyAuthenticatedContext,
    bool SupportsCdp,
    bool SupportsManualOnly,
    /// <summary>The engine can operate against the signed-in application through the review's authenticated browser session.</summary>
    bool SupportsAuthenticatedBrowserSession)
{
    /// <summary>The access path the engine uses when the target is public.</summary>
    public FrontendQualityEngineAccessKind PublicAccess =>
        RequiresBrowserRuntime || RequiresBrowserDom ? FrontendQualityEngineAccessKind.BrowserRuntime : FrontendQualityEngineAccessKind.PublicHttp;
}

/// <summary>
/// Audited (2026-09-15) access requirements of every quality engine, derived from what each implementation actually does:
/// <list type="bullet">
/// <item>Static Security — backend <c>BlazorWasmSecurityReviewService</c>: anonymous GET of the SPA document plus framework/config text assets
/// on the frontend host. Public HTTP only; it never fetches an authenticated API and never needs a DOM.</item>
/// <item>Passive Performance — backend <c>WasmAssetDiscoveryService</c>/<c>WasmApiAnalysisService</c>: anonymous GET/headers of the document,
/// boot manifest and listed assets, plus public GraphQL/OpenAPI path probes on the frontend origin. Public HTTP only; runtime metrics
/// (FCP/LCP/TTI) are explicitly out of scope, so no browser runtime.</item>
/// <item>Browser Runtime — backend Playwright Chromium page navigation with console/network capture. Needs a browser runtime and, for a
/// protected application, the review's authenticated browser session.</item>
/// <item>Accessibility — axe-core over the rendered DOM in Playwright. Same session model as Browser Runtime.</item>
/// <item>Lighthouse — anonymous synthetic navigation; no authenticated mode exists.</item>
/// <item>Passive Security — OWASP ZAP passive scan of anonymous traffic; no authenticated mode exists.</item>
/// <item>Browser Quality — BirkNext-native checks computed from Browser Companion evidence collected in the user's normal managed Edge session
/// (DOM, accessibility, performance, runtime, Blazor) and correlated with proxy network evidence per page. Needs neither public HTTP, CDP nor a
/// review browser session: it works for public and protected applications alike because the browser is already signed in.</item>
/// </list>
/// The Local HTTPS proxy provides an authenticated <em>API</em> context only. The two HTTP engines consume it through the shared
/// authenticated API-surface probes (approved REST GET / GraphQL query executed by the backend gateway; engines never see the token):
/// Static Security assesses the API's security headers and CORS posture, Passive Performance the authenticated latency/status.
/// DOM/runtime engines cannot use it: a bearer for the API does not sign a browser into an MSAL-protected SPA.
/// </summary>
public static class FrontendQualityEngineAccessRegistry
{
    private static readonly IReadOnlyDictionary<FrontendQualityEngineId, FrontendQualityEngineAccessRequirements> Requirements =
        new Dictionary<FrontendQualityEngineId, FrontendQualityEngineAccessRequirements>
        {
            // Both HTTP engines additionally consume the shared authenticated API-surface probes (approved REST GET / GraphQL query via
            // the Local HTTPS proxy gateway): Static Security assesses API security headers/CORS, Passive Performance authenticated latency.
            [FrontendQualityEngineId.StaticSecurity] = new(FrontendQualityEngineId.StaticSecurity,
                RequiresTargetReachability: true, RequiresPublicHttp: true, RequiresAuthenticatedHttp: false,
                RequiresBrowserDom: false, RequiresBrowserRuntime: false,
                SupportsProxyAuthenticatedContext: true, SupportsCdp: false, SupportsManualOnly: true, SupportsAuthenticatedBrowserSession: false),
            [FrontendQualityEngineId.PassivePerformance] = new(FrontendQualityEngineId.PassivePerformance,
                RequiresTargetReachability: true, RequiresPublicHttp: true, RequiresAuthenticatedHttp: false,
                RequiresBrowserDom: false, RequiresBrowserRuntime: false,
                SupportsProxyAuthenticatedContext: true, SupportsCdp: false, SupportsManualOnly: true, SupportsAuthenticatedBrowserSession: false),
            [FrontendQualityEngineId.BrowserRuntime] = new(FrontendQualityEngineId.BrowserRuntime,
                RequiresTargetReachability: true, RequiresPublicHttp: false, RequiresAuthenticatedHttp: false,
                RequiresBrowserDom: true, RequiresBrowserRuntime: true,
                SupportsProxyAuthenticatedContext: false, SupportsCdp: false, SupportsManualOnly: false, SupportsAuthenticatedBrowserSession: true),
            [FrontendQualityEngineId.Accessibility] = new(FrontendQualityEngineId.Accessibility,
                RequiresTargetReachability: true, RequiresPublicHttp: false, RequiresAuthenticatedHttp: false,
                RequiresBrowserDom: true, RequiresBrowserRuntime: false,
                SupportsProxyAuthenticatedContext: false, SupportsCdp: false, SupportsManualOnly: false, SupportsAuthenticatedBrowserSession: true),
            [FrontendQualityEngineId.Lighthouse] = new(FrontendQualityEngineId.Lighthouse,
                RequiresTargetReachability: true, RequiresPublicHttp: false, RequiresAuthenticatedHttp: false,
                RequiresBrowserDom: false, RequiresBrowserRuntime: true,
                SupportsProxyAuthenticatedContext: false, SupportsCdp: false, SupportsManualOnly: false, SupportsAuthenticatedBrowserSession: false),
            [FrontendQualityEngineId.PassiveSecurity] = new(FrontendQualityEngineId.PassiveSecurity,
                RequiresTargetReachability: true, RequiresPublicHttp: true, RequiresAuthenticatedHttp: false,
                RequiresBrowserDom: false, RequiresBrowserRuntime: false,
                SupportsProxyAuthenticatedContext: false, SupportsCdp: false, SupportsManualOnly: false, SupportsAuthenticatedBrowserSession: false),
            // Browser Quality consumes evidence the paired Browser Companion collected in the user's own browser; it issues no request itself.
            [FrontendQualityEngineId.BrowserQuality] = new(FrontendQualityEngineId.BrowserQuality,
                RequiresTargetReachability: false, RequiresPublicHttp: false, RequiresAuthenticatedHttp: false,
                RequiresBrowserDom: false, RequiresBrowserRuntime: false,
                SupportsProxyAuthenticatedContext: false, SupportsCdp: true, SupportsManualOnly: true, SupportsAuthenticatedBrowserSession: true),
            // BirkNext Performance Quality consumes Browser Companion evidence (page/runtime/resource/Blazor) and the proxy's already recorded
            // network observations (API latency/statuses/duplicates) per page. It issues no request itself and never uses the authenticated
            // API context (gateway probes), so SupportsProxyAuthenticatedContext stays false; either passive source yields a partial assessment.
            [FrontendQualityEngineId.PerformanceQuality] = new(FrontendQualityEngineId.PerformanceQuality,
                RequiresTargetReachability: false, RequiresPublicHttp: false, RequiresAuthenticatedHttp: false,
                RequiresBrowserDom: false, RequiresBrowserRuntime: false,
                SupportsProxyAuthenticatedContext: false, SupportsCdp: true, SupportsManualOnly: true, SupportsAuthenticatedBrowserSession: true),
        };

    public static FrontendQualityEngineAccessRequirements For(FrontendQualityEngineId engineId) =>
        Requirements.TryGetValue(engineId, out var requirements)
            ? requirements
            : throw new InvalidOperationException($"No access requirements are declared for engine '{engineId}'.");

    public static IEnumerable<FrontendQualityEngineAccessRequirements> All => Requirements.Values;
}

/// <summary>
/// Resolved access situation of the selected Target Environment for one review run. Safe to show and to export: it carries
/// configuration and non-secret runtime status only (no token, cookie, header, session id or credential).
/// </summary>
public sealed record FrontendQualityTargetAccessContext
{
    [JsonPropertyName("environmentName")] public string EnvironmentName { get; init; } = "";
    [JsonPropertyName("environmentType")] public string EnvironmentType { get; init; } = "";
    [JsonPropertyName("targetUrl")] public string TargetUrl { get; init; } = "";
    [JsonPropertyName("requiresAuthentication")] public bool RequiresAuthentication { get; init; }
    [JsonPropertyName("authenticationType")] public FrontendAuthenticationType AuthenticationType { get; init; }
    [JsonPropertyName("method")] public AuthenticatedTestingMethod Method { get; init; } = AuthenticatedTestingMethod.ManagedEdgeCdp;
    [JsonPropertyName("mode")] public FrontendQualityTargetAccessMode Mode { get; init; } = FrontendQualityTargetAccessMode.PublicDirect;

    /// <summary>Status of the Local HTTPS proxy authenticated API context as resolved by the backend gateway.</summary>
    [JsonPropertyName("apiContextStatus")] public AuthenticatedApiContextStatus ApiContextStatus { get; init; } = AuthenticatedApiContextStatus.NotApplicable;
    [JsonPropertyName("authenticatedApiAvailable")] public bool AuthenticatedApiAvailable { get; init; }
    /// <summary>Transient proxy listener state, when the proxy runtime is known to this app instance.</summary>
    [JsonPropertyName("proxyState")] public LocalHttpsProxyState? ProxyState { get; init; }

    /// <summary>The review's authenticated browser session (used by DOM/runtime engines) is signed in.</summary>
    [JsonPropertyName("authenticatedBrowserSessionAvailable")] public bool AuthenticatedBrowserSessionAvailable { get; init; }
    /// <summary>Authenticated DOM/runtime access is available to engines that support it (method permits it and a session exists).</summary>
    [JsonPropertyName("authenticatedBrowserDomAvailable")] public bool AuthenticatedBrowserDomAvailable { get; init; }
    /// <summary>Managed Edge (CDP) attach state for this environment, when known to this app instance.</summary>
    [JsonPropertyName("managedEdgeState")] public ManagedEdgeState? ManagedEdgeState { get; init; }
    [JsonPropertyName("enterpriseBlocked")] public bool EnterpriseBlocked => ManagedEdgeState == BirkNext.ManagedEdge.ManagedEdgeState.TargetTabNotInspectable;

    /// <summary>Manual verification status recorded for this environment. Passing it never grants automated access.</summary>
    [JsonPropertyName("manualVerificationStatus")] public ManualAuthenticationVerificationStatus ManualVerificationStatus { get; init; } = ManualAuthenticationVerificationStatus.NotRequired;

    /// <summary>Non-secret explanation from the capability resolution (e.g. why DOM checks are unavailable).</summary>
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>Fail-fast access decision for one engine, made before any request is issued.</summary>
public sealed record FrontendQualityEngineAccessDecision(
    FrontendQualityEngineId EngineId,
    FrontendQualityEngineAccessKind AccessKind,
    string AccessLabel,
    FrontendQualityEngineAccessState State,
    FrontendQualityEngineOutcomeReason OutcomeReason,
    string Reason,
    string? RequiredAction = null,
    string? ActionHref = null)
{
    public bool IsReady => State == FrontendQualityEngineAccessState.Ready;
}
