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
/// <summary>
/// Automated accessibility coverage when the dedicated Accessibility engine is unavailable. The WCAG profile is a ruleset
/// and stays available whatever the engines say; this is only about automated evidence. Limited: another automated
/// source (Browser Quality's browser evidence) still contributes. Unavailable: none does. Never "full": automation does
/// not establish WCAG conformance, and manual assessment is required either way.
/// </summary>
public enum FrontendQualityAutomatedAccessibilityCoverage { Limited, Unavailable }

public static class FrontendQualityLandingPresentation
{
    public const string TargetEnvironmentsHref = FrontendQualityTargetAccess.TargetEnvironmentsHref;
    /// <summary>Where engine activation and Required/Optional policy are saved. This page links there; it never edits them.</summary>
    public const string FrontendReviewEnginesHref = FrontendQualityTargetAccess.FrontendReviewEnginesHref;

    public const string EnginesConfigurationNote =
        "Engine activation is configured per Target Environment. Availability is evaluated here, when the review runs.";

    /// <summary>"N configured · M available right now" — configuration and capability counted separately, never merged.</summary>
    public static string EngineSummary(IReadOnlyList<FrontendQualityCapabilityRow> rows) =>
        CapabilitySummary(rows).Headline;

    /// <summary>
    /// The counts behind the collapsed capability row, all read from the capability rows themselves — nothing here is
    /// a literal, and "enabled" is never equated with "available".
    /// </summary>
    public static FrontendQualityCapabilitySummary CapabilitySummary(IReadOnlyList<FrontendQualityCapabilityRow> rows) => new(
        TotalCount: rows.Count,
        EnabledCount: rows.Count(r => r.Enabled),
        AvailableNowCount: rows.Count(r => r.IsAvailable),
        RequiredButDisabledCount: rows.Count(r => r.Policy == FrontendQualityEngineRequirement.Required && !r.Enabled),
        NeedsPairingCount: rows.Count(r => r.IsActive && r.State is
            FrontendQualityCapabilityState.NotConfigured or
            FrontendQualityCapabilityState.RequiresBrowserSession or
            FrontendQualityCapabilityState.RequiresAuthenticatedContext),
        // Switched off, by whichever of the three switches. Never added to the unavailable count: the engine is doing
        // exactly what it was configured to do, and counting it as unavailable asks someone to fix their own decision.
        DisabledCount: rows.Count(r => FrontendQualityCapabilityStates.IsDisabled(r.State)))
        { StateSummary = new FrontendQualityPreRunEngineSummary(rows).Counts };

    /// <summary>Counts behind the collapsed "Review access" row; derived from the same rows the expanded list renders.</summary>
    public static FrontendQualityCoverageSummaryModel CoverageSummary(IReadOnlyList<FrontendQualityCoverageRow> rows) => new(
        TotalCount: rows.Count,
        AvailableCount: rows.Count(r => r.State is FrontendQualityCoverageState.Available),
        NotAvailableCount: rows.Count(r => r.State is FrontendQualityCoverageState.NotAvailable),
        NotRequiredCount: rows.Count(r => r.State is FrontendQualityCoverageState.NotRequired),
        PublicOnlyCount: rows.Count(r => r.State is FrontendQualityCoverageState.PublicOnly));

    /// <summary>
    /// What the automated and passive part of this review covers, named rather than counted.
    ///
    /// The old line counted the check list ("11 automated or passive checks included"). The items in that list are not
    /// comparable units — "CORS" and "Startup asset analysis" are one entry each — so the number measured how the list
    /// happens to be written, and it moved whenever a group was reworded. The areas are the stable fact.
    /// </summary>
    public static string ReviewScopeSummary => string.Join(" · ", CheckGroups.Select(g => g.Title));

    /// <summary>The scope boundary, named. These areas are outside this review, which is not a failure or a gap in it.</summary>
    public static string OutsideReviewSummary => string.Join(" · ", NotAssessed.Select(i => i.Title));
    public const string SystemSettingsHref = "/admin/system-settings";

    public const string ReadyTitle = "Ready to review";
    public const string ReadyMessage = "The selected target and review configuration are valid. Target reachability is verified when the review starts.";
    public const string LimitedTitle = "Review can run with limitations";
    public const string AllRequiredAvailableSentence = "All required capabilities are available, so the review can run.";
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

        var stateSummary = new FrontendQualityPreRunEngineSummary(capabilities);
        var unavailable = stateSummary.Limitations;
        // The dedicated Accessibility engine being unavailable is stated as what it means — automated accessibility
        // coverage — never as "Accessibility: Unavailable", which read as if the WCAG profile or the domain were gone.
        var accessibility = AutomatedAccessibilityLimitation(capabilities);
        var named = accessibility is null ? unavailable : unavailable.Where(c => c.EngineId != FrontendQualityEngineId.Accessibility).ToList();
        var details = (accessibility is { } coverage
                ? [$"Automated accessibility coverage: {(coverage == FrontendQualityAutomatedAccessibilityCoverage.Limited ? "Available but limited" : "Unavailable")}"]
                : new List<string>())
            .Concat(named.Select(c => $"{c.DisplayName}: {FrontendQualityCapabilityStates.Label(c.State)}")).ToList();
        details.AddRange(context.ValidationWarnings);
        if (details.Count > 0)
        {
            // Listed so the reader can see what is off, never counted with what is broken and never a reason to be
            // Limited on its own: an engine switched off is working as configured, and asking someone to fix it is
            // asking them to undo their own decision.
            details.AddRange(capabilities
                .Where(c => c.Policy == FrontendQualityEngineRequirement.Optional && FrontendQualityCapabilityStates.IsDisabled(c.State))
                .Select(c => $"{c.DisplayName}: {FrontendQualityCapabilityStates.Label(c.State)}"));
            details.Add(ManualAccessibilityDetail);
            var requiredUnavailable = unavailable.Where(c => c.Policy == FrontendQualityEngineRequirement.Required).Select(c => c.DisplayName).ToList();
            // An engine somebody switched off is reported as switched off, in its own sentence. Merged into the
            // unavailable count it read as a fault, and sent the reader to fix something that is working as configured.
            //
            // The capability is NAMED here, in the one line the reader sees before deciding to run. "1 enabled optional
            // capability is currently unavailable" is true and useless: it says a limitation exists and makes finding out
            // which one an expand-and-scroll exercise, on the surface whose whole job is that decision.
            var message = unavailable.Count == 0
                ? "The configuration has warnings. Target reachability is verified when the review starts."
                : string.Join(" ", new[]
                {
                    accessibility is { } a ? CoverageSentence(a) : null,
                    named.Count > 0 ? new FrontendQualityPreRunEngineSummary(named).BannerSentences : null,
                }.Where(s => !string.IsNullOrEmpty(s)));
            if (stateSummary.AllRequiredAvailable)
                message += " " + AllRequiredAvailableSentence;
            else if (requiredUnavailable.Count > 0)
                message += $" Required coverage will stay incomplete: {Join(requiredUnavailable)}.";

            // The action follows the cause: capability limitations are fixed where engines are configured; a configuration
            // warning with nothing unavailable belongs to the Target Environment itself.
            return unavailable.Count > 0
                ? new(FrontendQualityReviewReadinessLevel.Limited, LimitedTitle, message, details, "Edit engines", FrontendReviewEnginesHref)
                : new(FrontendQualityReviewReadinessLevel.Limited, LimitedTitle, message, details, "Open Target Environment", TargetEnvironmentsHref);
        }

        return new(FrontendQualityReviewReadinessLevel.Ready, ReadyTitle,
            (stateSummary.AllRequiredAvailable ? "All required capabilities are available. " : "") + ReadyMessage, []);
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
        BrowserCompanionState? companion,
        BrowserEvidenceTotals? recorded = null)
    {
        return active.Engines
            .OrderBy(e => e.Policy == FrontendQualityEngineRequirement.Required ? 0 : 1)
            .ThenBy(e => e.EngineId)
            .Select(e => Row(e, context, status, statusPending, readinessPending, access, companion, recorded))
            .ToList();
    }

    private static FrontendQualityCapabilityRow Row(
        FrontendQualityEngineActivation engine,
        FrontendAnalysisContext context,
        FrontendQualityEngineStatusReportDto? status,
        bool statusPending,
        bool readinessPending,
        FrontendQualityTargetAccessContext? access,
        BrowserCompanionState? companion,
        BrowserEvidenceTotals? recorded)
    {
        var isBackend = FrontendQualityActiveEngines.BackendEngineIds.TryGetValue(engine.EngineId, out var dto);
        var record = isBackend ? status?.Engines.FirstOrDefault(s => s.EngineId == dto) : null;
        // Per-review opt-out applies to enabled backend engines whose capability record (when known) says available.
        FrontendQualityEngineIdDto? selectable = isBackend && engine.Enabled && (record is null || record.Available) ? dto : null;

        FrontendQualityCapabilityRow Build(FrontendQualityCapabilityState state, string? summary = null, string? technical = null, string? actionText = null, string? actionHref = null) =>
            new(engine.EngineId, engine.DisplayName, engine.Policy, state, summary, technical, actionText, actionHref, selectable, engine.Selected, engine.Enabled);

        if (!engine.Enabled)
            return Build(FrontendQualityCapabilityState.Disabled, "Disabled in the Target Environment configuration.", null, "Edit engines", FrontendReviewEnginesHref);
        if (!engine.Selected)
            return Build(FrontendQualityCapabilityState.NotSelected, "Enabled, but not included in this review.");

        // Browser Companion engines observe the user's own signed-in browser; their availability is the companion session.
        if (engine.EngineId is FrontendQualityEngineId.BrowserQuality or FrontendQualityEngineId.PerformanceQuality)
        {
            // Ready from recorded evidence under exactly the rules the run applies (BrowserQualityReviewResult.Assessed,
            // PerformanceQualityReviewResult.Assessed), so the reason names the source the review will actually read.
            // Counts are the typed Browser Discovery totals, never parsed from any message.
            var browserPages = recorded?.Pages ?? 0;
            var performancePages = recorded?.PerformancePages ?? 0;
            if (companion is not BrowserCompanionState.Connected and not null)
            {
                if (engine.EngineId == FrontendQualityEngineId.BrowserQuality && companion == BrowserCompanionState.Disconnected && browserPages > 0)
                    return Build(FrontendQualityCapabilityState.Ready, $"Uses captured browser evidence ({Pages(browserPages)}). Browser Companion is not currently reporting.");
                if (engine.EngineId == FrontendQualityEngineId.PerformanceQuality && performancePages > 0)
                    return Build(FrontendQualityCapabilityState.Ready, $"Uses recorded performance evidence ({Pages(performancePages)}). Browser Companion is not connected.");
            }
            return companion switch
            {
                BrowserCompanionState.Connected => Build(FrontendQualityCapabilityState.Ready,
                    engine.EngineId == FrontendQualityEngineId.BrowserQuality
                        ? "Browser Companion connected." + (browserPages > 0 ? $" {Pages(browserPages)} of captured evidence." : "")
                        : "Browser Companion connected." + (performancePages > 0 ? $" Recorded performance evidence for {Pages(performancePages)}." : "")),
                BrowserCompanionState.Disconnected => Build(FrontendQualityCapabilityState.RequiresBrowserSession, "Browser Companion is paired but not reporting. Open the application in your managed Edge."),
                BrowserCompanionState.Expired => Build(FrontendQualityCapabilityState.RequiresBrowserSession, "The Browser Companion session expired. Pair it again."),
                BrowserCompanionState.NotPaired or BrowserCompanionState.PairingPending => Build(FrontendQualityCapabilityState.NotConfigured,
                    "Pair the Browser Companion for this Target Environment." + (engine.EngineId == FrontendQualityEngineId.PerformanceQuality
                        ? " Recorded performance evidence can still contribute." : ""),
                    actionText: "Open Browser Discovery", actionHref: FrontendQualityBrowserEvidencePresentation.BrowserDiscoveryHref),
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
            return Build(FrontendQualityCapabilityState.Enabled, EnabledSummary(isBackend, record, status, statusPending, readinessPending) ?? publicNote, technical);
        }

        if (record?.Layer3Readiness is { IsAvailable: true })
            return Build(FrontendQualityCapabilityState.Ready);
        return Build(FrontendQualityCapabilityState.Enabled, EnabledSummary(isBackend, record, status, statusPending, readinessPending));
    }

    private static string Pages(int count) => $"{count.ToString(System.Globalization.CultureInfo.InvariantCulture)} page{(count == 1 ? "" : "s")}";

    /// <summary>Why an active backend engine is "Enabled" rather than "Ready": status fetch failed, or readiness still being probed.</summary>
    private static string? EnabledSummary(bool isBackend, FrontendQualityEngineStatusDto? record, FrontendQualityEngineStatusReportDto? status, bool statusPending, bool readinessPending)
    {
        if (!isBackend) return null;
        if (record is null && status is null && !statusPending)
            return "Capability status could not be checked; readiness is validated when the review starts.";
        return readinessPending ? "Runtime readiness is being checked; the review can start meanwhile." : null;
    }

    // ── Quality dimensions ────────────────────────────────────────────────────────────────────────────────────────────


    /// <summary>
    /// The one place a domain's scope is derived. The rule, per domain:
    ///
    /// <list type="bullet">
    /// <item><b>Baseline</b> engines carry the domain. If one is active and available, the domain is in the review.</item>
    /// <item><b>Optional</b> engines enrich it. Their absence downgrades Included to Limited — never to unavailable.</item>
    /// <item>A domain with no baseline at all (Accessibility) is scoped by its profile and manual assessment, so no
    /// engine can exclude it; when no automated evidence is available it reports Partial evidence.</item>
    /// <item><b>Not included</b> only when nothing — engine, profile or derived evidence — contributes.</item>
    /// </list>
    ///
    /// Engine names never become the domain status; they appear at most once, inside a short limitation line.
    /// </summary>
    public static IReadOnlyList<FrontendQualityDimensionCard> Dimensions(
        IReadOnlyList<FrontendQualityCapabilityRow> capabilities, WcagAssessmentProfile? accessibilityProfile = null)
    {
        var byId = capabilities.ToDictionary(c => c.EngineId);

        List<FrontendQualityCapabilityRow> Rows(IEnumerable<FrontendQualityEngineId> ids) =>
            ids.Select(id => byId.GetValueOrDefault(id)).Where(c => c is not null).Select(c => c!).ToList();

        return Enum.GetValues<FrontendQualityCategory>().Select(category =>
        {
            var baseline = Rows(FrontendQualityCategoryEngines.BaselineFor(category));
            var optional = Rows(FrontendQualityCategoryEngines.OptionalFor(category));

            var baselineReady = baseline.Any(c => c.IsActive && c.IsAvailable);
            var optionalMissing = optional.Where(c => c.IsActive && !c.IsAvailable).ToList();
            var optionalReady = optional.Any(c => c.IsActive && c.IsAvailable);
            var accessibility = category == FrontendQualityCategory.Accessibility;

            var state =
                // Accessibility is scoped by its profile, so it is in the review whatever the engines say.
                accessibility ? (optionalReady ? (optionalMissing.Count > 0 ? FrontendQualityDimensionState.Limited
                                                                           : FrontendQualityDimensionState.Included)
                                               : FrontendQualityDimensionState.PartialEvidence)
                : baselineReady ? (optionalMissing.Count > 0 ? FrontendQualityDimensionState.Limited
                                                             : FrontendQualityDimensionState.Included)
                // No baseline, but optional evidence still contributes: a reduced review, not an absent one.
                : optionalReady ? FrontendQualityDimensionState.PartialEvidence
                : baseline.Count == 0 && optional.Count == 0 ? FrontendQualityDimensionState.NotIncluded
                : baseline.Any(c => c.IsActive) || optional.Any(c => c.IsActive)
                    ? FrontendQualityDimensionState.PartialEvidence
                    : FrontendQualityDimensionState.NotIncluded;

            // Engine-level detail (which accessibility engine is ready) stays in Engines; the card states scope only.
            var limitation = Limitation(category, state, optionalMissing,
                optional.Where(c => c.State == FrontendQualityCapabilityState.Ready).ToList());
            var scopeNote = accessibility ? (accessibilityProfile ?? WcagProfiles.Norwegian).Label : null;

            return new FrontendQualityDimensionCard(
                category, FrontendQualityCategoryEngines.Label(category), Purpose(category), state, limitation,
                ManualAssessmentRequired: accessibility, ScopeNote: scopeNote);
        }).ToList();
    }

    /// <summary>
    /// One short sentence about impact, in evidence words. The exact engine-level reason stays in Review capabilities;
    /// this line says only what it means for the domain.
    /// </summary>
    /// <summary>
    /// One short sentence about impact. A Limited domain names the contributor that is actually missing, because a fixed
    /// sentence per category could not: Performance said "Optional browser evidence is unavailable" while browser
    /// evidence existed and Lighthouse was the engine that could not start. The engine is named once, here, and the
    /// technical reason stays in Review capabilities.
    /// </summary>
    private static string? Limitation(
        FrontendQualityCategory category, FrontendQualityDimensionState state,
        IReadOnlyList<FrontendQualityCapabilityRow> optionalMissing,
        IReadOnlyList<FrontendQualityCapabilityRow>? optionalReady = null) => state switch
    {
        FrontendQualityDimensionState.NotIncluded => "Nothing in this review contributes to this area.",

        FrontendQualityDimensionState.PartialEvidence or FrontendQualityDimensionState.Limited
            when category == FrontendQualityCategory.Accessibility && optionalMissing.Any(c => c.EngineId == FrontendQualityEngineId.Accessibility) =>
            AccessibilityCoverageLimitation(state, optionalMissing),
        FrontendQualityDimensionState.PartialEvidence when category == FrontendQualityCategory.Accessibility =>
            "Automated accessibility evidence is limited. " + new FrontendQualityPreRunEngineSummary(optionalMissing).LimitationSentences,
        FrontendQualityDimensionState.PartialEvidence when category == FrontendQualityCategory.Readiness =>
            "Derived from partial review evidence.",
        FrontendQualityDimensionState.PartialEvidence =>
            "Baseline evidence is unavailable; the remaining checks still run.",

        // Security names its own baseline, because "Passive Security evidence is unavailable" beside the word Limited
        // read as "security cannot be reviewed". The static security review is unaffected and says so first.
        FrontendQualityDimensionState.Limited when category == FrontendQualityCategory.Security && optionalMissing.Count > 0 =>
            "Static Security is included. " + new FrontendQualityPreRunEngineSummary(optionalMissing).LimitationSentences,

        FrontendQualityDimensionState.Limited when category == FrontendQualityCategory.Accessibility && optionalMissing.Count > 0 =>
            new FrontendQualityPreRunEngineSummary(optionalMissing).LimitationSentences,

        FrontendQualityDimensionState.Limited when optionalMissing.Count > 0 =>
            (category == FrontendQualityCategory.Performance ? PerformanceIncluded(optionalReady) : "")
                + new FrontendQualityPreRunEngineSummary(optionalMissing).LimitationSentences,
        FrontendQualityDimensionState.Limited =>
            "Some optional evidence for this area is unavailable.",

        // Not a shortfall in the automation. Manual assessment is what these criteria require, and it would still be
        // required if every automated source were available — so this sentence never blames the engines for it.
        FrontendQualityDimensionState.Included when category == FrontendQualityCategory.Accessibility =>
            "Automated accessibility checks are included.",
        _ => null,
    };

    /// <summary>
    /// Null unless the dedicated Accessibility engine is active and unavailable. Then Limited when Browser Quality is active
    /// and available (its browser evidence carries automated accessibility checks), otherwise Unavailable. Typed row state
    /// only — never a reason string.
    /// </summary>
    public static FrontendQualityAutomatedAccessibilityCoverage? AutomatedAccessibilityLimitation(IReadOnlyList<FrontendQualityCapabilityRow> rows)
    {
        if (rows.FirstOrDefault(r => r.EngineId == FrontendQualityEngineId.Accessibility) is not { IsActive: true, IsAvailable: false })
            return null;
        return rows.FirstOrDefault(r => r.EngineId == FrontendQualityEngineId.BrowserQuality) is { IsActive: true, IsAvailable: true }
            ? FrontendQualityAutomatedAccessibilityCoverage.Limited
            : FrontendQualityAutomatedAccessibilityCoverage.Unavailable;
    }

    public static string CoverageLabel(FrontendQualityAutomatedAccessibilityCoverage coverage) =>
        coverage == FrontendQualityAutomatedAccessibilityCoverage.Limited ? "Limited" : "Unavailable";

    /// <summary>
    /// Limited means automated accessibility checks still run (Browser Quality carries them) but the dedicated engine does
    /// not, so it is stated as "available but limited" — never as if automation were absent.
    /// </summary>
    private static string CoverageSentence(FrontendQualityAutomatedAccessibilityCoverage coverage) =>
        coverage == FrontendQualityAutomatedAccessibilityCoverage.Limited
            ? "Automated accessibility coverage is available but limited."
            : "Automated accessibility coverage is unavailable.";

    /// <summary>Stated in the limitation details whenever they are shown: the WCAG profile's manual obligation is
    /// independent of every engine, so no engine state removes it.</summary>
    public const string ManualAccessibilityDetail = "Manual accessibility assessment is still required by the selected WCAG profile";

    /// <summary>The accessibility card's limitation when the dedicated engine is one of the missing contributors.</summary>
    private static string AccessibilityCoverageLimitation(FrontendQualityDimensionState state, IReadOnlyList<FrontendQualityCapabilityRow> optionalMissing)
    {
        var rest = optionalMissing.Where(c => c.EngineId != FrontendQualityEngineId.Accessibility).ToList();
        var coverage = state == FrontendQualityDimensionState.Limited
            ? FrontendQualityAutomatedAccessibilityCoverage.Limited : FrontendQualityAutomatedAccessibilityCoverage.Unavailable;
        return (CoverageSentence(coverage) + " " + new FrontendQualityPreRunEngineSummary(rest).LimitationSentences).Trim();
    }

    /// <summary>
    /// What Performance will read. Browser Quality / BirkNext Performance Quality are named only when Ready — the state that
    /// mirrors the run's Assessed rule — so an engine that is merely available is never claimed as a contributor.
    /// </summary>
    private static string PerformanceIncluded(IReadOnlyList<FrontendQualityCapabilityRow>? optionalReady) =>
        optionalReady?.Any(c => c.EngineId is FrontendQualityEngineId.BrowserQuality or FrontendQualityEngineId.PerformanceQuality) == true
            ? "Passive Performance and Browser Companion evidence are included. "
            : "Passive Performance is included. ";

    /// <summary>"A", "A and B", "A, B and C". Used wherever capabilities are named in a sentence rather than counted.</summary>
    private static string Join(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(" and ", string.Join(", ", list.Take(list.Count - 1)), list[^1]),
        };
    }

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
        new("Accessibility", ["Automated axe-core checks (when the Accessibility capability is enabled)"], AccessibilityDisclaimer),
        new("Blazor / WASM", ["Startup asset analysis (boot resources and assemblies)", "Service worker detection (static)"]),
        new("Standards / QA readiness", [], "Standards compliance is derived from the security-header checks above; QA readiness indicators are derived from the collected performance evidence. No additional requests are made."),
    ];

    /// <summary>
    /// The one accessibility disclaimer on this page. Both halves matter and neither implies the other: automated checks
    /// cannot establish conformance, and manual assessment is required whatever those checks find. Said where the
    /// accessibility scope is, and nowhere else — repeated in five places it stops being read at all.
    /// </summary>
    public const string AccessibilityDisclaimer =
        "Automated accessibility checks do not establish complete WCAG conformance and do not replace the required manual assessment.";

    /// <summary>
    /// Scope boundaries, not gaps. Each says where the area belongs instead, so none of them reads as something this
    /// review failed to do.
    /// </summary>
    public static readonly IReadOnlyList<FrontendQualityNotAssessedItem> NotAssessed =
    [
        new("Core Web Vitals", "Measured separately: requires production field data or a supported field measurement path."),
        new("Testability", "Not part of Frontend Quality Review."),
        new("Observability", "Not part of Frontend Quality Review."),
    ];

    // ── Coverage ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<FrontendQualityCoverageRow> Coverage(FrontendQualityTargetAccessContext access, AuthenticatedReviewCapabilities? capabilities)
    {
        var rows = new List<FrontendQualityCoverageRow>
        {
            new("Public frontend", FrontendQualityCoverageState.Available, "Public pages and static assets can be reviewed over HTTP(S)."),
        };

        // Public-only scope: the Target Environment is configured without sign-in (RequiresAuthentication = false). That
        // scopes THIS review to the public surface; it does not establish that the target has no signed-in areas, so
        // nothing here claims that. Configuring sign-in on the Target Environment moves the review to the branch below.
        if (!access.RequiresAuthentication)
        {
            // One short sentence each; the state chip already says "for current scope".
            rows.Add(new("Authenticated application", FrontendQualityCoverageState.NotRequired,
                "This review runs against public pages only."));
            rows.Add(new("Browser-rendered DOM", FrontendQualityCoverageState.Available, "Rendered DOM is available for pages in the current scope."));
            rows.Add(new("Authenticated API traffic", FrontendQualityCoverageState.NotRequired,
                "Needed only for authenticated API-backed functionality."));
            rows.Add(new("Automatic engines", FrontendQualityCoverageState.Available,
                "Required engines can run for the current public scope."));
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
            // A configured provider and a public review scope are both true at once; the labels say which is which, so
            // "Microsoft Entra ID" beside "No" no longer reads as a contradiction.
            new("Authentication configured", AuthenticationPresentation.ProviderLabel(context.AuthenticationType)),
            new("Authentication required for current review scope", context.RequiresAuthentication ? "Yes" : "No"),
            new("Authenticated session for current scope", context.IsAuthenticatedSessionAvailable ? "Available" : context.RequiresAuthentication ? "Unavailable" : "Not required"),
            new("Browser Runtime", context.FeatureToggles.EnableBrowserRuntimeEngine ? "Enabled" : "Disabled"),
            new("Accessibility", context.FeatureToggles.EnableAccessibilityEngine ? "Enabled (automated axe-core checks)" : "Disabled"),
            new("Lighthouse", context.FeatureToggles.EnableLighthouseEngine ? "Enabled (synthetic lab measurement)" : "Disabled"),
            new("Passive Security", context.FeatureToggles.EnablePassiveSecurityEngine ? "Enabled (ZAP passive only)" : "Disabled"),
            new("Browser Quality", context.FeatureToggles.EnableBrowserQualityEngine ? "Enabled (Browser Companion, native checks)" : "Disabled"),
            new("BirkNext Performance Quality", context.FeatureToggles.EnablePerformanceQualityEngine ? "Enabled (Browser Companion + Local HTTPS proxy evidence, native)" : "Disabled"),
        ];
    }
}
