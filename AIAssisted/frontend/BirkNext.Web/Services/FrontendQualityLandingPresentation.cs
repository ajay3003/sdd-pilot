using BirkNext.BrowserCompanion;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure projection of the Frontend Quality Review's authoritative state into the landing-view presentation records.
/// Inputs are the saved configuration (<see cref="FrontendAnalysisContext"/>), the ONE activation rule
/// (<see cref="FrontendQualityActiveEngineSnapshot"/>), the backend capability model (<see cref="FrontendQualityEngineStatusReportDto"/>),
/// the resolved target access (<see cref="FrontendQualityTargetAccessContext"/> + <see cref="FrontendQualityTargetAccess.Decide"/>) and
/// the Browser Companion state. No I/O, no business rule of its own: every state shown here is read from those sources.
/// </summary>
public static class FrontendQualityLandingPresentation
{
    public const string TargetEnvironmentsHref = FrontendQualityTargetAccess.TargetEnvironmentsHref;
    public const string SystemSettingsHref = "/admin/system-settings";

    public const string ReadyTitle = "Ready to review";
    public const string ReadyMessage = "The selected target and review configuration are valid. Target reachability is verified when the review starts.";
    public const string LimitedTitle = "Review can run with limitations";
    public const string BlockedTitle = "Review cannot start";
    public const string LoadingTitle = "Loading target environment";

    // ── Target summary ────────────────────────────────────────────────────────────────────────────────────────────────

    public static FrontendQualityTargetSummaryModel TargetSummary(FrontendAnalysisContext context)
    {
        if (context.ActiveTargetError is { } error)
            return new("No active Target Environment", "—", "—", "—", error, false);

        var profile = context.ActiveProfile;
        var targetReady = context.HasTargetUrl && !context.HasValidationErrors;
        return new(
            string.IsNullOrWhiteSpace(profile.Name) ? "Unnamed environment" : profile.Name,
            EnvironmentTypeLabel(profile.EnvironmentType),
            context.HasTargetUrl ? context.TargetUrl : "Not configured",
            AuthenticationLabel(context.AuthenticationType, context.RequiresAuthentication),
            targetReady ? "Ready" : context.HasTargetUrl ? "Configuration incomplete" : "Frontend URL missing",
            targetReady);
    }

    public static string EnvironmentTypeLabel(FrontendEnvironmentType type) => type switch
    {
        FrontendEnvironmentType.Development => "Dev",
        FrontendEnvironmentType.Production => "Prod",
        _ => type.ToString(),
    };

    public static string AuthenticationLabel(FrontendAuthenticationType type, bool requiresAuthentication)
    {
        if (!requiresAuthentication) return "Not required";
        return type switch
        {
            FrontendAuthenticationType.None => "Required",
            FrontendAuthenticationType.MicrosoftEntraId => "Microsoft Entra ID",
            FrontendAuthenticationType.OpenIdConnect => "OpenID Connect",
            FrontendAuthenticationType.OAuth2 => "OAuth 2.0",
            FrontendAuthenticationType.Custom => "Custom sign-in",
            _ => type.ToString(),
        };
    }

    // ── Readiness ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whole-review readiness. Green only when Run is enabled AND nothing enabled is unavailable; a parsed-but-not-runnable
    /// configuration is Blocked, an enabled-but-unavailable capability makes it Limited.
    /// </summary>
    public static FrontendQualityReviewReadiness Readiness(
        FrontendAnalysisContext? context,
        FrontendQualityActiveEngineSnapshot? active,
        IReadOnlyList<FrontendQualityCapabilityRow> capabilities,
        bool statusPending)
    {
        if (context is null)
            return new(FrontendQualityReviewReadinessLevel.Loading, LoadingTitle, "The active Target Environment is still loading.", []);

        if (context.ActiveTargetError is { } error)
            return new(FrontendQualityReviewReadinessLevel.Blocked, error, "Select a Target Environment and choose “Set as Active”.", [],
                "Open Target Environments", TargetEnvironmentsHref);

        var missing = new List<string>();
        if (!context.HasTargetUrl) missing.Add("Frontend target URL");
        missing.AddRange(context.ValidationErrors);
        if (missing.Count > 0)
            return new(FrontendQualityReviewReadinessLevel.Blocked, BlockedTitle, "Resolve the missing requirements before running the review.", missing,
                "Configure Target Environment", TargetEnvironmentsHref);

        if (active is not { HasActiveEngines: true })
            return new(FrontendQualityReviewReadinessLevel.Blocked, FrontendQualityActiveEngines.NoActiveEnginesMessage, FrontendQualityActiveEngines.NoActiveEnginesAction,
                ["At least one enabled Frontend Quality Review engine"], "Open Target Environment", TargetEnvironmentsHref);

        if (statusPending)
        {
            var checking = capabilities.Count(c => c.State == FrontendQualityCapabilityState.Checking);
            return new(FrontendQualityReviewReadinessLevel.Checking, "Checking review capabilities",
                $"Checking {checking} active engine{(checking == 1 ? "" : "s")}… The review can start as soon as the check completes.", []);
        }

        var unavailable = capabilities.Where(c => c.IsActive && !c.IsAvailable).ToList();
        var details = unavailable.Select(c => $"{c.DisplayName}: {FrontendQualityCapabilityStates.Label(c.State)}").ToList();
        details.AddRange(context.ValidationWarnings);
        if (details.Count > 0)
        {
            var requiredUnavailable = unavailable.Where(c => c.Policy == FrontendQualityEngineRequirement.Required).Select(c => c.DisplayName).ToList();
            var message = unavailable.Count switch
            {
                0 => "The configuration has warnings. Target reachability is verified when the review starts.",
                1 => requiredUnavailable.Count == 1
                    ? $"1 required capability is unavailable ({requiredUnavailable[0]}); required coverage will stay incomplete."
                    : "1 optional capability is unavailable.",
                _ => requiredUnavailable.Count > 0
                    ? $"{unavailable.Count} capabilities are unavailable, including required {string.Join(", ", requiredUnavailable)}."
                    : $"{unavailable.Count} optional capabilities are unavailable.",
            };
            return new(FrontendQualityReviewReadinessLevel.Limited, LimitedTitle, message, details, "Open Target Environment", TargetEnvironmentsHref);
        }

        return new(FrontendQualityReviewReadinessLevel.Ready, ReadyTitle, ReadyMessage, []);
    }

    // ── Capabilities (engines) ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One row per engine of the activation snapshot, in policy order (Required first). State precedence per engine:
    /// saved activation → Layer 1–2 (deployment policy / System Settings) → Layer 3 readiness → target access decision.
    /// </summary>
    public static IReadOnlyList<FrontendQualityCapabilityRow> Capabilities(
        FrontendAnalysisContext context,
        FrontendQualityActiveEngineSnapshot active,
        FrontendQualityEngineStatusReportDto? status,
        bool statusPending,
        bool readinessPending,
        FrontendQualityTargetAccessContext? access,
        BrowserCompanionState? companion)
    {
        return active.Engines
            .OrderBy(e => e.Policy == FrontendQualityEngineRequirement.Required ? 0 : 1)
            .ThenBy(e => e.EngineId)
            .Select(e => Row(e, context, status, statusPending, readinessPending, access, companion))
            .ToList();
    }

    private static FrontendQualityCapabilityRow Row(
        FrontendQualityEngineActivation engine,
        FrontendAnalysisContext context,
        FrontendQualityEngineStatusReportDto? status,
        bool statusPending,
        bool readinessPending,
        FrontendQualityTargetAccessContext? access,
        BrowserCompanionState? companion)
    {
        var isBackend = FrontendQualityActiveEngines.BackendEngineIds.TryGetValue(engine.EngineId, out var dto);
        var record = isBackend ? status?.Engines.FirstOrDefault(s => s.EngineId == dto) : null;
        // Per-review opt-out applies to enabled backend engines whose capability record (when known) says available.
        FrontendQualityEngineIdDto? selectable = isBackend && engine.Enabled && (record is null || record.Available) ? dto : null;

        FrontendQualityCapabilityRow Build(FrontendQualityCapabilityState state, string? summary = null, string? technical = null, string? actionText = null, string? actionHref = null) =>
            new(engine.EngineId, engine.DisplayName, engine.Policy, state, summary, technical, actionText, actionHref, selectable, engine.Selected);

        if (!engine.Enabled)
            return Build(FrontendQualityCapabilityState.Disabled, "Disabled in the Target Environment configuration.", null, "Open Target Environment", TargetEnvironmentsHref);
        if (!engine.Selected)
            return Build(FrontendQualityCapabilityState.NotSelected, "Enabled, but not included in this review.");

        // Browser Companion engines observe the user's own signed-in browser; their availability is the companion session.
        if (engine.EngineId is FrontendQualityEngineId.BrowserQuality or FrontendQualityEngineId.PerformanceQuality)
        {
            return companion switch
            {
                BrowserCompanionState.Connected => Build(FrontendQualityCapabilityState.Ready, "Browser Companion connected."),
                BrowserCompanionState.Disconnected => Build(FrontendQualityCapabilityState.RequiresBrowserSession, "Browser Companion is paired but not reporting. Open the application in your managed Edge."),
                BrowserCompanionState.Expired => Build(FrontendQualityCapabilityState.RequiresBrowserSession, "The Browser Companion session expired. Pair it again."),
                BrowserCompanionState.NotPaired or BrowserCompanionState.PairingPending => Build(FrontendQualityCapabilityState.NotConfigured, "Pair the Browser Companion for this Target Environment."),
                _ => Build(FrontendQualityCapabilityState.Enabled, "Evidence comes from the Browser Companion in your managed Edge."),
            };
        }

        if (isBackend)
        {
            if (record is null && statusPending)
                return Build(FrontendQualityCapabilityState.Checking, "Checking whether this capability is available.");
            if (record is not null)
            {
                if (!record.Layer1Allowed)
                    return Build(FrontendQualityCapabilityState.Unavailable, $"The {engine.DisplayName} engine is not available in this environment.", "Blocked by deployment policy.");
                if (!record.Layer2Enabled)
                    return Build(FrontendQualityCapabilityState.DisabledInSystemSettings, "Disabled in System Settings.", null, "Open System Settings", SystemSettingsHref);
                if (record.Layer3Readiness is { IsAvailable: false } readiness)
                    return Build(FrontendQualityCapabilityState.Unavailable, $"The {engine.DisplayName} engine is not available in this environment.",
                        readiness.StatusReason is { Length: > 0 } reason ? reason : "Runtime unavailable.");
            }
        }

        // Target access decision (fail-fast matching of the engine's declared requirements against the resolved access).
        if (access is not null)
        {
            var decision = FrontendQualityTargetAccess.Decide(engine.Access, access);
            if (!decision.IsReady)
            {
                var state = decision.State == FrontendQualityEngineAccessState.Unsupported ? FrontendQualityCapabilityState.NotSupported
                    : decision.OutcomeReason switch
                    {
                        FrontendQualityEngineOutcomeReason.AuthenticationRequired or FrontendQualityEngineOutcomeReason.SessionUnavailable => FrontendQualityCapabilityState.RequiresBrowserSession,
                        FrontendQualityEngineOutcomeReason.AuthenticatedContextUnavailable or FrontendQualityEngineOutcomeReason.AuthenticatedContextExpired => FrontendQualityCapabilityState.RequiresAuthenticatedContext,
                        _ => FrontendQualityCapabilityState.Unavailable,
                    };
                var summary = state switch
                {
                    FrontendQualityCapabilityState.NotSupported => "Cannot review the signed-in application with the selected authentication method.",
                    FrontendQualityCapabilityState.RequiresBrowserSession => "Sign in for review to include the signed-in application.",
                    FrontendQualityCapabilityState.RequiresAuthenticatedContext => "Start the Local HTTPS proxy and sign in to the target application.",
                    _ when decision.OutcomeReason == FrontendQualityEngineOutcomeReason.EnterpriseBrowserProtectionBlocked => "Blocked by enterprise browser protection. BirkNext does not bypass browser protection.",
                    _ => $"The {engine.DisplayName} engine cannot reach this target right now.",
                };
                return Build(state, summary, decision.Reason, decision.RequiredAction, decision.ActionHref);
            }
            if (isBackend && record is { AuthModeSupported: false } && context.RequiresAuthentication)
                return Build(FrontendQualityCapabilityState.NotSupported, "Cannot review the signed-in application with the selected authentication method.", "Not supported for authenticated review.");

            if (record?.Layer3Readiness is { IsAvailable: true })
                return Build(FrontendQualityCapabilityState.Ready, null, decision.Reason is { Length: > 0 } ? decision.Reason : null);
            var technical = decision.AccessLabel + (decision.Reason is { Length: > 0 } ? $" — {decision.Reason}" : "");
            var publicNote = access.RequiresAuthentication && decision.AccessKind == FrontendQualityEngineAccessKind.PublicHttp
                ? "Reviews the public frontend; signed-in pages are not covered by this capability." : null;
            return Build(FrontendQualityCapabilityState.Enabled,
                isBackend && readinessPending ? "Runtime readiness is being checked; the review can start meanwhile." : publicNote,
                technical);
        }

        if (record?.Layer3Readiness is { IsAvailable: true })
            return Build(FrontendQualityCapabilityState.Ready);
        if (isBackend && record is null && status is null && !statusPending)
            return Build(FrontendQualityCapabilityState.Enabled, "Capability status could not be checked; readiness is validated when the review starts.");
        return Build(FrontendQualityCapabilityState.Enabled, isBackend && readinessPending ? "Runtime readiness is being checked; the review can start meanwhile." : null);
    }

    // ── Quality dimensions ────────────────────────────────────────────────────────────────────────────────────────────

    public const string AccessibilityLimitation = "Manual accessibility testing may still be required.";

    public static IReadOnlyList<FrontendQualityDimensionCard> Dimensions(IReadOnlyList<FrontendQualityCapabilityRow> capabilities)
    {
        var byId = capabilities.ToDictionary(c => c.EngineId);
        return Enum.GetValues<FrontendQualityCategory>().Select(category =>
        {
            var engines = FrontendQualityCategoryEngines.For(category).Select(id => byId.GetValueOrDefault(id)).Where(c => c is not null).Select(c => c!).ToList();
            var active = engines.Where(c => c.IsActive).ToList();
            var unavailable = active.Where(c => !c.IsAvailable).ToList();
            var state = active.Count == 0 ? FrontendQualityDimensionState.NotEnabled
                : unavailable.Count == active.Count ? FrontendQualityDimensionState.Unavailable
                : category == FrontendQualityCategory.Readiness ? FrontendQualityDimensionState.Available
                : FrontendQualityDimensionState.Enabled;
            var limitation = state switch
            {
                FrontendQualityDimensionState.NotEnabled => "No enabled capability contributes to this dimension.",
                FrontendQualityDimensionState.Unavailable => $"{Names(unavailable)} unavailable.",
                _ when unavailable.Count > 0 => $"{Names(unavailable)} unavailable; remaining checks still run.",
                _ => null,
            };
            if (category == FrontendQualityCategory.Accessibility && state != FrontendQualityDimensionState.NotEnabled)
                limitation = limitation is null ? AccessibilityLimitation : $"{limitation} {AccessibilityLimitation}";
            return new FrontendQualityDimensionCard(category, FrontendQualityCategoryEngines.Label(category), Purpose(category), state, limitation);
        }).ToList();
    }

    private static string Names(IEnumerable<FrontendQualityCapabilityRow> rows) => string.Join(", ", rows.Select(r => r.DisplayName));

    public static string Purpose(FrontendQualityCategory category) => category switch
    {
        FrontendQualityCategory.Performance => "Measures frontend loading and asset behaviour.",
        FrontendQualityCategory.Security => "Passive security review of headers, configuration and exposed indicators.",
        FrontendQualityCategory.Accessibility => "Automated accessibility checks against the rendered page.",
        FrontendQualityCategory.Standards => "Security-header and web-standards conformance checks.",
        FrontendQualityCategory.BlazorWasm => "Static Blazor and WebAssembly quality checks.",
        FrontendQualityCategory.Readiness => "Derived readiness indicators from available frontend evidence.",
        _ => "",
    };

    // ── Checks ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The automated/passive checks of this review, grouped. Same set as before; wording avoids compliance claims.</summary>
    public static readonly IReadOnlyList<FrontendQualityCheckGroup> CheckGroups =
    [
        new("Security", ["Security headers", "Content Security Policy (CSP)", "CORS", "Passive frontend security indicators (no compliance claim)"]),
        new("Performance", ["Bundle size", "Compression", "Cache headers", "Lazy loading (static detection)"]),
        new("Accessibility", ["Automated axe-core checks (when the Accessibility capability is enabled)"], "Manual accessibility testing remains separate; zero automated violations does not establish WCAG conformance."),
        new("Blazor / WASM", ["Startup asset analysis (boot resources and assemblies)", "Service worker detection (static)"]),
        new("Standards / QA readiness", ["Security-header standards", "Readiness indicators derived from the collected evidence"]),
    ];

    public static int CheckCount => CheckGroups.Sum(g => g.Checks.Count);

    public static readonly IReadOnlyList<FrontendQualityNotAssessedItem> NotAssessed =
    [
        new("Core Web Vitals", "Requires production field data or a supported browser measurement path."),
        new("Testability", "Not part of this frontend audit."),
        new("Observability", "Not part of this frontend audit."),
    ];

    // ── Coverage ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<FrontendQualityCoverageRow> Coverage(FrontendQualityTargetAccessContext access, AuthenticatedReviewCapabilities? capabilities)
    {
        var rows = new List<FrontendQualityCoverageRow>
        {
            new("Public frontend", FrontendQualityCoverageState.Available, "Public pages and static assets are reviewed over HTTP."),
        };

        if (!access.RequiresAuthentication)
        {
            rows.Add(new("Authenticated application", FrontendQualityCoverageState.NotRequired, "This target does not require sign-in."));
            rows.Add(new("Browser-rendered DOM", FrontendQualityCoverageState.Available, "Available for public pages."));
            rows.Add(new("Authenticated API traffic", FrontendQualityCoverageState.NotRequired, "This target does not require sign-in."));
            rows.Add(new("Automatic engines", FrontendQualityCoverageState.Available, "Public target supported."));
            return rows;
        }

        rows.Add(access.Mode switch
        {
            FrontendQualityTargetAccessMode.ManagedEdgeCdp => new("Authenticated application", FrontendQualityCoverageState.Available, "Authenticated browser session available."),
            FrontendQualityTargetAccessMode.LocalHttpsProxy => new("Authenticated application", FrontendQualityCoverageState.Available, "Authenticated API context available. Signed-in pages are not rendered with this method."),
            FrontendQualityTargetAccessMode.ManualOnly => new("Authenticated application", FrontendQualityCoverageState.NotAvailable, "Manual verification only; no automated access to the signed-in application."),
            FrontendQualityTargetAccessMode.EnterpriseBlocked => new("Authenticated application", FrontendQualityCoverageState.NotAvailable, "Blocked by enterprise browser protection."),
            _ => new("Authenticated application", FrontendQualityCoverageState.NotAvailable,
                access.Method == AuthenticatedTestingMethod.LocalHttpsProxy ? "Start the Local HTTPS proxy and sign in to the target application." : "Sign in for review to include the signed-in application."),
        });

        rows.Add(access.AuthenticatedBrowserDomAvailable
            ? new("Browser-rendered DOM", FrontendQualityCoverageState.Available, "Available in the authenticated browser session.")
            : access.Method switch
            {
                AuthenticatedTestingMethod.LocalHttpsProxy => new("Browser-rendered DOM", FrontendQualityCoverageState.NotAvailable, "Not available with the Local HTTPS proxy method."),
                AuthenticatedTestingMethod.ManualOnly => new("Browser-rendered DOM", FrontendQualityCoverageState.NotAvailable, "Not available with manual verification only."),
                _ when access.EnterpriseBlocked => new("Browser-rendered DOM", FrontendQualityCoverageState.NotAvailable, "Blocked by enterprise browser protection."),
                _ => new("Browser-rendered DOM", FrontendQualityCoverageState.NotAvailable, "Requires an authenticated browser session (Sign in for review)."),
            });

        rows.Add(access.Method switch
        {
            AuthenticatedTestingMethod.LocalHttpsProxy when access.AuthenticatedApiAvailable =>
                new("Authenticated API traffic", FrontendQualityCoverageState.Available, ApiTrafficDetail(capabilities)),
            AuthenticatedTestingMethod.LocalHttpsProxy =>
                new("Authenticated API traffic", FrontendQualityCoverageState.NotAvailable,
                    access.ApiContextStatus == AuthenticatedApiContextStatus.Expired
                        ? "The authenticated session expired. Continue using the target application in the proxy-configured browser."
                        : "Requires an authenticated session through the Local HTTPS proxy."),
            AuthenticatedTestingMethod.ManualOnly => new("Authenticated API traffic", FrontendQualityCoverageState.NotAvailable, "Not available with manual verification only."),
            _ => new("Authenticated API traffic", FrontendQualityCoverageState.NotAvailable, "Not provided by the Managed Edge browser method."),
        });

        rows.Add(access.Mode switch
        {
            FrontendQualityTargetAccessMode.ManagedEdgeCdp => new("Automatic engines", FrontendQualityCoverageState.Available, "Public frontend and authenticated browser session."),
            FrontendQualityTargetAccessMode.LocalHttpsProxy => new("Automatic engines", FrontendQualityCoverageState.Available, "Public frontend and authenticated API context (no signed-in pages)."),
            FrontendQualityTargetAccessMode.ManualOnly => new("Automatic engines", FrontendQualityCoverageState.PublicOnly, "Manual verification only; automated engines review the public frontend."),
            FrontendQualityTargetAccessMode.EnterpriseBlocked => new("Automatic engines", FrontendQualityCoverageState.PublicOnly, "Browser protection blocks the authenticated session; automated engines review the public frontend."),
            _ => new("Automatic engines", FrontendQualityCoverageState.PublicOnly, "Authenticated context not available yet; automated engines review the public frontend."),
        });

        return rows;
    }

    private static string ApiTrafficDetail(AuthenticatedReviewCapabilities? capabilities)
    {
        if (capabilities is null) return "Authenticated API context available (memory only).";
        var observed = new List<string>();
        if (capabilities.AuthenticatedRest) observed.Add("REST");
        if (capabilities.AuthenticatedGraphQlQuery) observed.Add("GraphQL query");
        return observed.Count == 0
            ? "Authenticated API context available (memory only); no REST or GraphQL traffic observed yet."
            : $"Authenticated API context available (memory only); {string.Join(" and ", observed)} traffic observed.";
    }

    // ── Authenticated review workflow ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Null when the target does not require authentication (no warning block is shown then).</summary>
    public static FrontendQualityAuthenticatedReviewModel? AuthenticatedReview(
        FrontendAnalysisContext context,
        FrontendQualityTargetAccessContext? access,
        AuthenticatedReviewCapabilities? capabilities,
        AuthenticatedBrowserSession? session)
    {
        if (!context.RequiresAuthentication) return null;
        var method = access?.Method ?? context.ActiveProfile.Authentication.AuthenticatedTestingMethod;

        switch (method)
        {
            case AuthenticatedTestingMethod.LocalHttpsProxy:
            {
                var apiStatus = access?.ApiContextStatus ?? capabilities?.ContextStatus ?? AuthenticatedApiContextStatus.NotApplicable;
                var connected = access?.AuthenticatedApiAvailable ?? capabilities?.AuthenticatedApi ?? false;
                var status = apiStatus switch
                {
                    AuthenticatedApiContextStatus.Available => "Connected",
                    AuthenticatedApiContextStatus.Expired => "Session expired",
                    AuthenticatedApiContextStatus.Stale => "Environment changed — sign in again",
                    _ => "Not connected",
                };
                return new(method, status, connected,
                    "Authenticated API behaviour is reviewed through the Local HTTPS proxy. Signed-in pages are not rendered with this method.",
                    [
                        "Open the Target Environment and start the Local HTTPS proxy.",
                        "Sign in to the target application in the proxy-configured browser.",
                        "Use the pages and actions you want included so authenticated API traffic is observed.",
                        "Return here and run the review.",
                    ],
                    [
                        new("Authenticated API session", connected ? "Available (memory only)" : StatusOrNotAvailable(apiStatus), connected),
                        new("REST traffic", Availability(capabilities?.AuthenticatedRest == true, "Observed", "Not observed"), capabilities?.AuthenticatedRest == true),
                        new("GraphQL traffic", Availability(capabilities?.AuthenticatedGraphQlQuery == true, "Observed", "Not observed"), capabilities?.AuthenticatedGraphQlQuery == true),
                        new("Authenticated DOM", "Not available with this method", false),
                    ],
                    "Static Security and Passive Performance review the public frontend; Browser Runtime and Accessibility cannot assess the signed-in application with this method.");
            }
            case AuthenticatedTestingMethod.ManualOnly:
            {
                var manual = access?.ManualVerificationStatus ?? context.ManualVerificationStatus ?? ManualAuthenticationVerificationStatus.Required;
                return new(method, $"Manual verification: {FrontendQualityTargetAccess.ManualVerificationLabel(manual)}", false,
                    "This environment supports manual verification only. Automated engines that need authenticated access are not available here.",
                    [
                        "Verify access to the target application manually from the Target Environment page.",
                        "Run the review: Static Security and Passive Performance still review the public frontend.",
                    ],
                    [
                        new("Manual verification", FrontendQualityTargetAccess.ManualVerificationLabel(manual), manual == ManualAuthenticationVerificationStatus.Passed),
                        new("Authenticated DOM", "Not available", false),
                        new("Authenticated API traffic", "Not available", false),
                    ],
                    "A passed manual verification confirms user access only; it does not give automated engines access to the target.");
            }
            default:
            {
                var connected = session?.IsAuthenticated == true || (access?.AuthenticatedBrowserSessionAvailable ?? context.IsAuthenticatedSessionAvailable);
                var blocked = access?.EnterpriseBlocked == true;
                var status = connected ? "Connected" : blocked ? "Blocked by enterprise browser protection" : "Not connected";
                return new(method, status, connected,
                    "Signed-in pages are reviewed in a BirkNext-controlled Microsoft Edge window. Credentials and MFA remain under your control.",
                    [
                        "Choose “Sign in for review” to open the secure sign-in window.",
                        "Sign in to the target application.",
                        "Return here and run the review.",
                    ],
                    [
                        new("Browser session", connected ? "Connected" : "Not connected", connected),
                        new("Authenticated DOM", Availability(access?.AuthenticatedBrowserDomAvailable ?? connected), access?.AuthenticatedBrowserDomAvailable ?? connected),
                        new("Browser runtime", Availability(access?.AuthenticatedBrowserDomAvailable ?? connected), access?.AuthenticatedBrowserDomAvailable ?? connected),
                    ],
                    blocked ? FrontendQualityTargetAccess.EnterpriseBlockedReason
                    : "Browser Runtime and Accessibility run in the authenticated session; Static Security and Passive Performance review the public frontend; Lighthouse and Passive Security have no authenticated mode.");
            }
        }
    }

    private static string Availability(bool value, string available = "Available", string unavailable = "Not available") => value ? available : unavailable;

    private static string StatusOrNotAvailable(AuthenticatedApiContextStatus status) => status switch
    {
        AuthenticatedApiContextStatus.Expired => "Expired",
        AuthenticatedApiContextStatus.Stale => "Stale — environment changed",
        AuthenticatedApiContextStatus.WaitingForAuthenticatedTraffic => "Waiting for authenticated traffic",
        _ => "Not available",
    };

    // ── Technical details (exact source values moved behind disclosure) ──────────────────────────────────────────────

    public static IReadOnlyList<FrontendQualityTechnicalField> TechnicalTargetFields(FrontendAnalysisContext context)
    {
        if (context.ActiveTargetError is { } error)
            return [new("Target Environment", error), new("Active", "No")];
        return
        [
            new("Environment type", context.ActiveProfile.EnvironmentType.ToString()),
            new("Environment ID", context.ActiveProfile.Id),
            new("Active", "Yes"),
            new("Target URL", context.HasTargetUrl ? context.TargetUrl : "Not configured"),
            new("Authentication type", context.AuthenticationType.ToString()),
            new("Authentication required", context.RequiresAuthentication ? "Yes" : "No"),
            new("Auth status", context.IsAuthenticatedSessionAvailable ? "Authenticated" : context.RequiresAuthentication ? "Session unavailable" : "Not required"),
            new("Browser Runtime", context.FeatureToggles.EnableBrowserRuntimeEngine ? "Enabled" : "Disabled"),
            new("Accessibility", context.FeatureToggles.EnableAccessibilityEngine ? "Enabled (automated axe-core checks)" : "Disabled"),
            new("Lighthouse", context.FeatureToggles.EnableLighthouseEngine ? "Enabled (synthetic lab measurement)" : "Disabled"),
            new("Passive Security", context.FeatureToggles.EnablePassiveSecurityEngine ? "Enabled (ZAP passive only)" : "Disabled"),
            new("Browser Quality", context.FeatureToggles.EnableBrowserQualityEngine ? "Enabled (Browser Companion, native checks)" : "Disabled"),
            new("BirkNext Performance Quality", context.FeatureToggles.EnablePerformanceQualityEngine ? "Enabled (Browser Companion + Local HTTPS proxy evidence, native)" : "Disabled"),
        ];
    }
}
