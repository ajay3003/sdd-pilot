using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Resolves the Frontend Quality Review access scope: Target Environment configuration → available access paths →
/// what the runnable engines cover → (after the run) what the assessed engines actually covered.
///
/// Audited execution support (2026-09-25) — the reason Configured and Effective are separate:
/// <list type="bullet">
/// <item>The scope is derived from the Target Environment's sign-in rule (<c>RequiresAuthentication</c>). There is no
/// per-review scope selector; changing the rule on the Target Environment is how the authenticated path is included.</item>
/// <item>Sign-in not required → every orchestrated engine reviews the public frontend anonymously: Public only.</item>
/// <item>Sign-in required → Static Security, Passive Performance and Passive Security review the public frontend shell
/// (document and assets); Accessibility and Browser Runtime review the signed-in application through the authenticated
/// browser session (Managed Edge) when one exists; Lighthouse has no authenticated mode. One run therefore covers public
/// AND authenticated paths — but split by engine: no single engine reviews both public and signed-in pages in one run.</item>
/// <item>The Local HTTPS proxy provides an authenticated API context only; signed-in pages are never rendered with it.</item>
/// <item>Browser Companion evidence records no per-page sign-in state, so it proves neither path.</item>
/// </list>
/// </summary>
public static class FrontendQualityReviewScopes
{
    public const string ProxyApiOnlyDetail = "The Local HTTPS proxy provides an authenticated API context only; signed-in pages are not rendered with this method.";
    public const string ManualOnlyDetail = "Manual verification only; no automated access to signed-in pages.";
    public const string EnterpriseBlockedDetail = "The authenticated browser session is blocked by enterprise browser protection.";
    public const string SessionMissingDetail = "No authenticated browser session. Sign in for review to include signed-in pages.";
    public const string NotConfiguredDetail = "Sign-in is required, but no sign-in provider is configured for this Target Environment.";
    public const string AvailableDetail = "Authenticated browser session available for signed-in pages.";
    public const string NotIncludedDetail = "This Target Environment is reviewed without sign-in. Require sign-in on the Target Environment to include signed-in pages.";
    public const string NoAuthenticatedEngineDetail = "No active engine reviews signed-in pages. Accessibility and Browser Runtime are the engines that use the authenticated browser session.";

    /// <summary>"Authentication configured" value: the provider, or "Not configured" — never "None", which read as a value.</summary>
    public static string ConfiguredProviderLabel(FrontendAuthenticationType type) =>
        type == FrontendAuthenticationType.None ? "Not configured" : AuthenticationPresentation.ProviderLabel(type);

    public static string Label(FrontendReviewAccessScope scope) => scope switch
    {
        FrontendReviewAccessScope.PublicOnly => "Public only",
        FrontendReviewAccessScope.AuthenticatedOnly => "Authenticated only",
        _ => "Public + authenticated",
    };

    /// <summary>The scope from the Target Environment's sign-in rule. Never inferred from whether sign-in is configured.</summary>
    public static FrontendReviewAccessScope ConfiguredScope(bool requiresAuthentication) =>
        requiresAuthentication ? FrontendReviewAccessScope.PublicAndAuthenticated : FrontendReviewAccessScope.PublicOnly;

    /// <summary>The path an engine's access decision reaches. Companion evidence is kept apart: its sign-in state is unknown.</summary>
    public static FrontendQualityEngineAccessPath PathFor(FrontendQualityEngineAccessKind kind) => kind switch
    {
        FrontendQualityEngineAccessKind.AuthenticatedBrowserSession or FrontendQualityEngineAccessKind.AuthenticatedHttp => FrontendQualityEngineAccessPath.Authenticated,
        FrontendQualityEngineAccessKind.BrowserCompanion => FrontendQualityEngineAccessPath.CompanionEvidence,
        _ => FrontendQualityEngineAccessPath.Public,
    };

    /// <summary>
    /// Pre-run scope. <paramref name="capabilities"/> decides which engines are active and runnable (so a disabled or
    /// unavailable engine never contributes coverage); the access decisions decide which path each one uses.
    /// </summary>
    public static FrontendQualityReviewScope Resolve(
        FrontendAnalysisContext context,
        FrontendQualityTargetAccessContext access,
        IReadOnlyList<FrontendQualityCapabilityRow> capabilities)
    {
        var configured = ConfiguredScope(access.RequiresAuthentication);
        var (authenticated, detail) = AuthenticatedPath(access);
        var decisions = FrontendQualityTargetAccess.DecideAll(access);

        var engines = capabilities
            .Where(row => row.IsActive && decisions.ContainsKey(row.EngineId))
            .Select(row => new FrontendQualityEngineScopeEntry(row.EngineId, row.DisplayName,
                PathFor(decisions[row.EngineId].AccessKind), row.IsAvailable && decisions[row.EngineId].IsReady))
            .ToList();
        var runnable = engines.Where(e => e.Available).Select(e => e.Path).ToList();
        // A usable session that no active engine will use is not authenticated coverage; the detail says which it is.
        if (authenticated == FrontendQualityAccessPathState.Available && !runnable.Contains(FrontendQualityEngineAccessPath.Authenticated))
            detail = NoAuthenticatedEngineDetail;

        return new FrontendQualityReviewScope(
            configured,
            context.HasTargetUrl ? FrontendQualityAccessPathState.Available : FrontendQualityAccessPathState.Unavailable,
            authenticated, detail,
            access.AuthenticatedBrowserSessionAvailable,
            Combine(runnable.Contains(FrontendQualityEngineAccessPath.Public), runnable.Contains(FrontendQualityEngineAccessPath.Authenticated)),
            engines);
    }

    /// <summary>Post-run: the paths the ASSESSED engines used. Echoing the configured scope would claim coverage that never ran.</summary>
    public static FrontendQualityExecutedScope Executed(FrontendQualityReviewReport report)
    {
        var assessed = report.EngineOutcomes
            .Where(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed && o.AccessKind is not null)
            .Select(o => (o.DisplayName, Path: PathFor(o.AccessKind!.Value)))
            .ToList();
        List<string> Names(FrontendQualityEngineAccessPath path) => assessed.Where(a => a.Path == path).Select(a => a.DisplayName).ToList();
        var publicEngines = Names(FrontendQualityEngineAccessPath.Public);
        var authenticatedEngines = Names(FrontendQualityEngineAccessPath.Authenticated);
        return new FrontendQualityExecutedScope(
            report.TargetAccess is { } access ? ConfiguredScope(access.RequiresAuthentication) : null,
            Combine(publicEngines.Count > 0, authenticatedEngines.Count > 0),
            publicEngines, authenticatedEngines, Names(FrontendQualityEngineAccessPath.CompanionEvidence));
    }

    /// <summary>"Public + authenticated", or "Public only · authenticated scope unavailable" when the run reaches less.</summary>
    public static string ScopeSummary(FrontendQualityReviewScope scope) =>
        scope.CompanionOnly ? "Browser Companion evidence only · sign-in state not recorded"
        : scope.Effective is not { } effective ? $"{Label(scope.Configured)} · no engine can reach the target"
        : !scope.Narrowed ? Label(effective)
        : effective == FrontendReviewAccessScope.PublicOnly ? $"{Label(effective)} · authenticated scope unavailable"
        : $"{Label(effective)} · public scope unavailable";

    /// <summary>"Available", "Unavailable", "Not configured", "Available · not included in this review" or "Not included in this review".</summary>
    public static string AuthenticatedAccessLabel(FrontendQualityReviewScope scope) => scope.AuthenticatedAccess switch
    {
        FrontendQualityAccessPathState.Available => "Available",
        FrontendQualityAccessPathState.NotConfigured => "Not configured",
        FrontendQualityAccessPathState.Unavailable => "Unavailable",
        _ => scope.AuthenticatedSessionAvailable ? "Available · not included in this review" : "Not included in this review",
    };

    /// <summary>
    /// Target status against the configured scope: public availability never masks a missing authenticated path.
    /// Blocked when nothing can run; Ready with limitations when only part of the configured scope is reachable.
    /// </summary>
    public static (string Label, bool Ready) TargetStatus(FrontendQualityReviewScope scope) =>
        scope.CompanionOnly ? ("Ready with limitations", false)
        : scope.Effective is null ? ("Blocked", false)
        : scope.Narrowed ? ("Ready with limitations", false)
        : ("Ready", true);

    /// <summary>The sentence the readiness banner adds when the configured scope is only partly reachable.</summary>
    public static string? LimitationSentence(FrontendQualityReviewScope scope) =>
        !scope.Narrowed ? null
        : scope.Effective == FrontendReviewAccessScope.PublicOnly
            ? $"Public scope is available. Authenticated coverage is unavailable: {scope.AuthenticatedDetail}"
            : "Authenticated scope is available. No engine can review the public frontend in this run.";

    /// <summary>
    /// The readiness banner, stated against the configured scope: a run that reaches only the public part of a
    /// Public + authenticated scope is never "Ready to review" without saying so.
    /// </summary>
    public static FrontendQualityReviewReadiness ApplyTo(FrontendQualityReviewReadiness readiness, FrontendQualityReviewScope? scope)
    {
        if (scope is null || LimitationSentence(scope) is not { } sentence) return readiness;
        return readiness.Level switch
        {
            FrontendQualityReviewReadinessLevel.Ready => readiness with
            {
                Level = FrontendQualityReviewReadinessLevel.Limited,
                Title = FrontendQualityLandingPresentation.LimitedTitle,
                Message = sentence,
                Details = [$"Authenticated access: {AuthenticatedAccessLabel(scope)}"],
                ActionText = "Open Target Environment",
                ActionHref = FrontendQualityTargetAccess.TargetEnvironmentsHref,
            },
            FrontendQualityReviewReadinessLevel.Limited when !readiness.Message.Contains(sentence, StringComparison.Ordinal) =>
                readiness with { Message = $"{sentence} {readiness.Message}" },
            _ => readiness,
        };
    }

    /// <summary>What one engine covers, as a short tag beside its name.</summary>
    public static string PathLabel(FrontendQualityEngineAccessPath path) => path switch
    {
        FrontendQualityEngineAccessPath.Public => "Public frontend",
        FrontendQualityEngineAccessPath.Authenticated => "Signed-in pages",
        _ => "Companion evidence",
    };

    /// <summary>The engine's tag: the path it covers, or the path it would cover with "· not available" when it cannot run.</summary>
    public static string EngineScopeLabel(FrontendQualityEngineScopeEntry entry) =>
        entry.Available ? PathLabel(entry.Path) : $"{PathLabel(entry.Path)} · not available";

    public static string ExecutedLabel(FrontendQualityExecutedScope scope) =>
        scope.Executed is { } executed ? Label(executed) : "No access path assessed";

    private static FrontendReviewAccessScope? Combine(bool publicPath, bool authenticatedPath) => (publicPath, authenticatedPath) switch
    {
        (true, true) => FrontendReviewAccessScope.PublicAndAuthenticated,
        (true, false) => FrontendReviewAccessScope.PublicOnly,
        (false, true) => FrontendReviewAccessScope.AuthenticatedOnly,
        _ => null,
    };

    /// <summary>The authenticated frontend path from access alone (no engine information): state and one-sentence reason.</summary>
    public static (FrontendQualityAccessPathState State, string Detail) AuthenticatedPath(FrontendQualityTargetAccessContext access)
    {
        if (!access.RequiresAuthentication) return (FrontendQualityAccessPathState.NotIncluded, NotIncludedDetail);
        if (access.AuthenticationType == FrontendAuthenticationType.None) return (FrontendQualityAccessPathState.NotConfigured, NotConfiguredDetail);
        return access.Method switch
        {
            AuthenticatedTestingMethod.LocalHttpsProxy => (FrontendQualityAccessPathState.Unavailable, ProxyApiOnlyDetail),
            AuthenticatedTestingMethod.ManualOnly => (FrontendQualityAccessPathState.Unavailable, ManualOnlyDetail),
            _ when access.AuthenticatedBrowserDomAvailable => (FrontendQualityAccessPathState.Available, AvailableDetail),
            _ when access.EnterpriseBlocked => (FrontendQualityAccessPathState.Unavailable, EnterpriseBlockedDetail),
            _ => (FrontendQualityAccessPathState.Unavailable, SessionMissingDetail),
        };
    }
}
