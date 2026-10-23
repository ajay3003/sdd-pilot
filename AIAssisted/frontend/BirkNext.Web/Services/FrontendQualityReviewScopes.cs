using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Resolves the Frontend Quality Review access scope: Target Environment configuration → available access paths →
/// what the runnable engines cover → (after the run) what the assessed engines actually covered.
///
/// Audited execution support (2026-09-25) — the reason Configured and Effective are separate:
/// <list type="bullet">
/// <item>FQR owns its persisted access selection. Legacy profiles and historical reports without an explicit scope retain
/// their original derivation from RequiresAuthentication; scope selection never edits that authentication setting.</item>
/// <item>Sign-in not required → every orchestrated engine reviews the public frontend anonymously: Public only.</item>
/// <item>Sign-in required → HTTP Security, HTTP Performance and Passive Web Security review the public frontend shell
/// (document and assets); Accessibility and Browser Runtime review the signed-in application through the authenticated
/// browser session (Managed Edge) when one exists; Lighthouse has no authenticated mode. One run therefore covers public
/// AND authenticated paths. Explicit dual-scope reviews execute browser engines separately for each supported path.</item>
/// <item>The Local HTTPS proxy provides an authenticated API context only; signed-in pages are never rendered with it.</item>
/// <item>Browser Companion evidence records no per-page sign-in state, so it proves neither path.</item>
/// </list>
/// </summary>
public static class FrontendQualityReviewScopes
{
    public static string PassStatus(FrontendQualityCoveragePass pass)
    {
        var relevant = pass.Report.EngineOutcomes.Where(o => o.Enabled && o.ExecutionState != FrontendQualityEngineExecutionState.Disabled
            && o.AccessKind != FrontendQualityEngineAccessKind.BrowserCompanion).ToList();
        if (!relevant.Any(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed))
            return "Not run — no selected engine assessed this access mode";
        return relevant.All(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed)
            ? "Completed" : "Completed with limitations";
    }
    public static string TestingContextLabel(FrontendQualityTargetAccessContext? access) => access is null ? "Checking…"
        : access.Method switch
        {
            AuthenticatedTestingMethod.LocalHttpsProxy => access.AuthenticatedApiAvailable ? "Ready" : "Not ready",
            AuthenticatedTestingMethod.ManualOnly => "Manual verification only",
            _ => access.AuthenticatedBrowserSessionAvailable ? "Ready" : "Not ready"
        };

    public static string VerificationLabel(FrontendQualityTargetAccessContext? access) => access is null ? "Checking…"
        : FrontendQualityTargetAccess.ManualVerificationLabel(access.ManualVerificationStatus);

    public static FrontendReviewAccessScope ConfiguredScope(FrontendQualityTargetAccessContext access) =>
        access.ReviewScope ?? ConfiguredScope(access.RequiresAuthentication);
    public const string ProxyApiOnlyDetail = "The Local HTTPS proxy provides an authenticated API context only; signed-in pages are not rendered with this method.";
    public const string ManualOnlyDetail = "Manual verification only; no automated access to signed-in pages.";
    public const string EnterpriseBlockedDetail = "The authenticated browser session is blocked by enterprise browser protection.";
    public const string SessionMissingDetail = "No authenticated browser session. Sign in for review to include signed-in pages.";
    public const string NotConfiguredDetail = "Sign-in is required, but no sign-in provider is configured for this Target Environment.";
    public const string AvailableDetail = "Authenticated browser session available for signed-in pages.";
    public const string NotIncludedDetail = "Authentication is not used in this review. Choose Change scope to include authenticated coverage.";
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
        var configured = ConfiguredScope(access);
        var (authenticated, detail) = AuthenticatedPath(access);
        var decisions = FrontendQualityTargetAccess.DecideAll(access);

        var engines = capabilities
            .Where(row => row.IsActive && decisions.ContainsKey(row.EngineId))
            .Select(row => new FrontendQualityEngineScopeEntry(row.EngineId, row.DisplayName,
                PathFor(decisions[row.EngineId].AccessKind), row.IsAvailable && decisions[row.EngineId].IsReady))
            .ToList();
        if (access.ReviewScope is not null)
        {
            engines = capabilities.Where(row => row.IsActive).SelectMany(row =>
            {
                var modes = configured == FrontendReviewAccessScope.PublicAndAuthenticated
                    ? new[] { FrontendReviewAccessScope.PublicOnly, FrontendReviewAccessScope.AuthenticatedOnly } : new[] { configured };
                return modes.Select(mode =>
                {
                    var decision = FrontendQualityTargetAccess.Decide(FrontendQualityEngineAccessRegistry.For(row.EngineId),
                        access with { ReviewScope = mode, RequiresAuthentication = mode != FrontendReviewAccessScope.PublicOnly });
                    return new FrontendQualityEngineScopeEntry(row.EngineId, row.DisplayName, PathFor(decision.AccessKind),
                        row.IsAvailable && decision.IsReady);
                });
            }).Distinct().ToList();
        }
        var runnable = engines.Where(e => e.Available).Select(e => e.Path).ToList();
        // A usable session that no active engine will use is not authenticated coverage; the detail says which it is.
        if (authenticated == FrontendQualityAccessPathState.Available && !runnable.Contains(FrontendQualityEngineAccessPath.Authenticated))
            detail = NoAuthenticatedEngineDetail;

        return new FrontendQualityReviewScope(
            configured,
            configured == FrontendReviewAccessScope.AuthenticatedOnly ? FrontendQualityAccessPathState.NotIncluded
                : context.HasTargetUrl ? FrontendQualityAccessPathState.Available : FrontendQualityAccessPathState.Unavailable,
            authenticated, detail,
            access.AuthenticatedBrowserSessionAvailable,
            Combine(runnable.Contains(FrontendQualityEngineAccessPath.Public), runnable.Contains(FrontendQualityEngineAccessPath.Authenticated)),
            engines)
        {
            ExplicitSelection = access.ReviewScope is not null,
        };
    }

    /// <summary>
    /// The one place the default review scope is decided: Public + authenticated when both paths are usable, the single usable path
    /// otherwise, and null when neither is. Callers never derive a default on their own.
    /// </summary>
    public static FrontendReviewAccessScope? Recommended(bool publicAvailable, bool authenticatedReady) => Combine(publicAvailable, authenticatedReady);

    /// <summary>
    /// Which paths a review could reach right now, asked through the same machinery that scopes a run: each single-path scope is
    /// resolved with <see cref="Resolve"/> over the capability rows for that scope, so a path counts only when an active, available
    /// engine's own access decision can use it. Authenticated readiness is therefore the testing context plus a consuming engine,
    /// never "authentication is configured". <paramref name="capabilitiesFor"/> builds the rows for a given access (rows depend on
    /// each engine's access decision). Pure: nothing is started, signed in or saved.
    /// </summary>
    public static FrontendQualityScopeRecommendation Recommend(
        FrontendAnalysisContext context,
        FrontendQualityTargetAccessContext access,
        Func<FrontendAnalysisContext, FrontendQualityTargetAccessContext, IReadOnlyList<FrontendQualityCapabilityRow>> capabilitiesFor)
    {
        FrontendQualityReviewScope Probe(FrontendReviewAccessScope mode)
        {
            var modeAccess = FrontendQualityTargetAccess.ForScope(access, mode);
            var modeContext = context.ForReviewScope(mode);
            return Resolve(modeContext, modeAccess, capabilitiesFor(modeContext, modeAccess));
        }
        static bool Reaches(FrontendQualityReviewScope scope, FrontendQualityEngineAccessPath path) =>
            scope.Engines.Any(e => e.Available && e.Path == path);

        var publicAvailable = context.HasTargetUrl && Reaches(Probe(FrontendReviewAccessScope.PublicOnly), FrontendQualityEngineAccessPath.Public);
        var authenticated = Probe(FrontendReviewAccessScope.AuthenticatedOnly);
        var authenticatedReady = access.AuthenticationType != FrontendAuthenticationType.None
            && authenticated.AuthenticatedAccess == FrontendQualityAccessPathState.Available
            && Reaches(authenticated, FrontendQualityEngineAccessPath.Authenticated);
        return new(context.ActiveProfile.Id, publicAvailable, authenticatedReady, Recommended(publicAvailable, authenticatedReady));
    }

    /// <summary>One sentence under the scope choices; null when there is nothing worth saying.</summary>
    public static string? RecommendationSentence(FrontendQualityScopeRecommendation? recommendation) => recommendation?.Scope switch
    {
        FrontendReviewAccessScope.PublicAndAuthenticated => "Recommended because both public and authenticated review access are ready.",
        null when recommendation is not null => NoRunnableScope,
        _ => null,
    };

    public const string NoRunnableScope = "No runnable access scope available. Neither the public frontend nor an authenticated testing context can be reviewed right now.";
    public const string AuthenticatedNowAvailable = "Authenticated access is now available.";
    public const string ScopeNeedsReview = "Scope needs review: authenticated access is not ready.";

    /// <summary>
    /// What changed since an explicit scope was chosen, without changing it: authenticated access became available to a scope that
    /// leaves it out, or a scope that includes it can no longer reach it. Null for a recommended (not chosen) scope.
    /// </summary>
    public static string? ExplicitScopeNotice(FrontendReviewAccessScope? saved, FrontendQualityScopeRecommendation? recommendation) =>
        saved is not { } chosen || recommendation is null ? null
        : chosen == FrontendReviewAccessScope.PublicOnly && recommendation.AuthenticatedReady ? AuthenticatedNowAvailable
        : chosen != FrontendReviewAccessScope.PublicOnly && !recommendation.AuthenticatedReady ? ScopeNeedsReview
        : null;

    /// <summary>Post-run: the paths the ASSESSED engines used. Echoing the configured scope would claim coverage that never ran.</summary>
    public static FrontendQualityExecutedScope Executed(FrontendQualityReviewReport report)
    {
        var assessed = (report.CoveragePasses.Count > 0 ? report.CoveragePasses.SelectMany(p => p.Report.EngineOutcomes) : report.EngineOutcomes)
            .Where(o => o.ExecutionState == FrontendQualityEngineExecutionState.Assessed && o.AccessKind is not null)
            .Select(o => (o.DisplayName, Path: PathFor(o.AccessKind!.Value)))
            .ToList();
        List<string> Names(FrontendQualityEngineAccessPath path) => assessed.Where(a => a.Path == path).Select(a => a.DisplayName).ToList();
        var publicEngines = Names(FrontendQualityEngineAccessPath.Public);
        var authenticatedEngines = Names(FrontendQualityEngineAccessPath.Authenticated);
        return new FrontendQualityExecutedScope(
            report.TargetAccess is { } access ? ConfiguredScope(access) : null,
            Combine(publicEngines.Count > 0, authenticatedEngines.Count > 0),
            publicEngines, authenticatedEngines, Names(FrontendQualityEngineAccessPath.CompanionEvidence));
    }

    /// <summary>"Public + authenticated", or "Public only · authenticated scope unavailable" when the run reaches less.</summary>
    public static string ScopeSummary(FrontendQualityReviewScope scope) =>
        scope.ExplicitSelection ? Label(scope.Configured)
        : scope.CompanionOnly ? "Browser Companion evidence only · sign-in state not recorded"
        : scope.Effective is not { } effective ? $"{Label(scope.Configured)} · no engine can reach the target"
        : !scope.Narrowed ? Label(effective)
        : effective == FrontendReviewAccessScope.PublicOnly ? $"{Label(effective)} · authenticated scope unavailable"
        : $"{Label(effective)} · public scope unavailable";

    /// <summary>"Available", "Unavailable", "Not configured", "Available · not included in this review" or "Not included in this review".</summary>
    public static string AuthenticatedAccessLabel(FrontendQualityReviewScope scope) => scope.ExplicitSelection
        ? scope.Configured == FrontendReviewAccessScope.PublicOnly ? "Not included in this review"
            : scope.AuthenticatedAccess == FrontendQualityAccessPathState.Available ? "Included — testing context ready"
            : scope.Configured == FrontendReviewAccessScope.AuthenticatedOnly ? "Required but unavailable" : "Requested but unavailable"
        : scope.AuthenticatedAccess switch
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
    /// A missing optional manual sign-in check never limits a run: access comes from the authenticated context, not a recorded click.
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
        if (access.ReviewScope is not null && access.AuthenticatedApiAvailable)
            return (FrontendQualityAccessPathState.Available, "Authenticated API testing context ready. Signed-in browser pages require a separate supported browser context.");
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
