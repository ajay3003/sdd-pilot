using BirkNext.LocalHttpsProxy;

namespace BirkNext.Web.Models;

/// <summary>
/// Presentation records for the Frontend Quality Review landing view. They are PROJECTIONS of authoritative state
/// (<see cref="FrontendQualityActiveEngineSnapshot"/>, <see cref="FrontendQualityEngineStatusReportDto"/>,
/// <see cref="FrontendQualityTargetAccessContext"/>, <see cref="AuthenticatedReviewCapabilities"/>) built by
/// <c>FrontendQualityLandingPresentation</c>; they never introduce a second status taxonomy and never recompute activation,
/// access or readiness rules.
/// </summary>
public enum FrontendQualityReviewReadinessLevel
{
    /// <summary>The active Target Environment is still loading.</summary>
    Loading,
    /// <summary>Capability status for the active backend engines is being fetched; Run waits for it.</summary>
    Checking,
    /// <summary>Everything configured is available; the review can start.</summary>
    Ready,
    /// <summary>The review can start, but at least one enabled capability is unavailable or the configuration has warnings.</summary>
    Limited,
    /// <summary>The review cannot start.</summary>
    Blocked,
}

public sealed record FrontendQualityReviewReadiness(
    FrontendQualityReviewReadinessLevel Level,
    string Title,
    string Message,
    IReadOnlyList<string> Details,
    string? ActionText = null,
    string? ActionHref = null)
{
    /// <summary>Execution can start (the Run button is enabled) only in these levels; the page still re-validates at run time.</summary>
    public bool CanRun => Level is FrontendQualityReviewReadinessLevel.Ready or FrontendQualityReviewReadinessLevel.Limited;
}

/// <summary>
/// Landing-view state of one review capability (engine). Every distinct source state keeps its own value; nothing is
/// collapsed into a vague "inactive". Labels live in <see cref="FrontendQualityCapabilityStates"/>.
/// </summary>
public enum FrontendQualityCapabilityState
{
    /// <summary>Active and its runtime/access was confirmed ready.</summary>
    Ready,
    /// <summary>Active; readiness is validated when the review starts (HTTP engines, or readiness not probed).</summary>
    Enabled,
    /// <summary>Active; its capability status is being fetched.</summary>
    Checking,
    /// <summary>Disabled in the saved Target Environment configuration.</summary>
    Disabled,
    /// <summary>Enabled but deselected for this review.</summary>
    NotSelected,
    /// <summary>Blocked by deployment policy, runtime unavailable, or blocked by enterprise browser protection.</summary>
    Unavailable,
    /// <summary>Disabled in System Settings (Layer 2).</summary>
    DisabledInSystemSettings,
    /// <summary>Needs a one-time setup that has not been done (Browser Companion not paired).</summary>
    NotConfigured,
    /// <summary>Needs an authenticated browser session (review sign-in or Browser Companion session) that is not available right now.</summary>
    RequiresBrowserSession,
    /// <summary>Needs the Local HTTPS proxy authenticated API context, which is missing or expired.</summary>
    RequiresAuthenticatedContext,
    /// <summary>Cannot review the signed-in application with the selected authentication method.</summary>
    NotSupported,
}

public static class FrontendQualityCapabilityStates
{
    public static string Label(FrontendQualityCapabilityState state) => state switch
    {
        FrontendQualityCapabilityState.Ready => "Ready",
        // Runtime vocabulary only. Configuration ("Enabled" / "Disabled") is shown separately beside this label, so
        // the same word can never stand for both "switched on in the Target Environment" and "nothing is stopping it".
        FrontendQualityCapabilityState.Enabled => "Available",
        FrontendQualityCapabilityState.Checking => "Checking…",
        FrontendQualityCapabilityState.Disabled => "Disabled",
        FrontendQualityCapabilityState.NotSelected => "Not selected",
        FrontendQualityCapabilityState.Unavailable => "Unavailable",
        FrontendQualityCapabilityState.DisabledInSystemSettings => "Disabled in System Settings",
        FrontendQualityCapabilityState.NotConfigured => "Not configured",
        FrontendQualityCapabilityState.RequiresBrowserSession => "Requires browser session",
        FrontendQualityCapabilityState.RequiresAuthenticatedContext => "Requires authenticated session",
        FrontendQualityCapabilityState.NotSupported => "Not supported for this target",
        _ => state.ToString(),
    };

    /// <summary>Visual tone (badge modifier). Text labels always accompany it; colour is never the only signal.</summary>
    public static string Tone(FrontendQualityCapabilityState state) => state switch
    {
        FrontendQualityCapabilityState.Ready or FrontendQualityCapabilityState.Enabled => "ready",
        FrontendQualityCapabilityState.Checking => "pending",
        FrontendQualityCapabilityState.Disabled or FrontendQualityCapabilityState.NotSelected => "muted",
        _ => "attention",
    };

    /// <summary>
    /// The capability is part of this review (enabled and selected), whatever its availability.
    ///
    /// Switched off in System Settings counts as off. It was excluded here for only one of the two ways an engine can be
    /// disabled, so a system-disabled engine stayed "active but not available" — it was counted among the unavailable
    /// capabilities on the readiness card and it pushed its domains to Limited. Nobody is going to fix an engine they
    /// turned off, and "unavailable" says something is wrong.
    /// </summary>
    public static bool IsActive(FrontendQualityCapabilityState state) => !IsDisabled(state);

    /// <summary>Deliberately off — by profile activation, by per-review selection, or in System Settings.</summary>
    public static bool IsDisabled(FrontendQualityCapabilityState state) =>
        state is FrontendQualityCapabilityState.Disabled
              or FrontendQualityCapabilityState.NotSelected
              or FrontendQualityCapabilityState.DisabledInSystemSettings;

    /// <summary>The capability is active and nothing known prevents it from running.</summary>
    public static bool IsAvailable(FrontendQualityCapabilityState state) =>
        state is FrontendQualityCapabilityState.Ready or FrontendQualityCapabilityState.Enabled or FrontendQualityCapabilityState.Checking;
}

public sealed record FrontendQualityCapabilityRow(
    FrontendQualityEngineId EngineId,
    string DisplayName,
    FrontendQualityEngineRequirement Policy,
    FrontendQualityCapabilityState State,
    /// <summary>Plain-language one-liner for testers (never implementation vocabulary).</summary>
    string? Summary,
    /// <summary>Exact technical reason from the source (readiness reason, access reason, policy). Collapsed by default.</summary>
    string? TechnicalReason,
    string? ActionText = null,
    string? ActionHref = null,
    /// <summary>Set when the per-review "Include in review" opt-out applies to this engine.</summary>
    FrontendQualityEngineIdDto? SelectableEngineId = null,
    bool Selected = false,
    /// <summary>
    /// Saved activation from the Target Environment. Deliberately separate from <see cref="State"/>: an engine can be
    /// Enabled and still Unavailable, and that must never read as "someone switched it off".
    /// </summary>
    bool Enabled = false)
{
    public bool IsActive => FrontendQualityCapabilityStates.IsActive(State);
    public bool IsAvailable => FrontendQualityCapabilityStates.IsAvailable(State);
    /// <summary>
    /// Show the saved configuration ("Enabled") beside the runtime state. True whenever the Target Environment has the
    /// engine switched on, so the two axes are always readable apart: "Enabled · Unavailable" is a capability problem,
    /// "Enabled · Not selected" is a choice for this run, and "Disabled" alone is a configuration decision — the one
    /// case where the state label already IS the configuration and a second chip would only repeat it.
    /// </summary>
    public bool ShowsEnabledAlongsideState => Enabled && State is not FrontendQualityCapabilityState.Disabled;
}

/// <summary>
/// Scope of a review DOMAIN — whether it is part of this review and how complete its evidence is. Deliberately a
/// different vocabulary from engine state: a domain draws on several engines, so one optional engine being
/// unavailable is a limitation in evidence, never an unavailable domain.
/// </summary>
public enum FrontendQualityDimensionState
{
    /// <summary>In the review, with every active evidence source available.</summary>
    Included,
    /// <summary>In the review, but at least one active evidence source is unavailable.</summary>
    Limited,
    /// <summary>In the review on a reduced basis: its baseline evidence is unavailable, but it still contributes.</summary>
    PartialEvidence,
    /// <summary>Nothing contributes to this domain in this review.</summary>
    NotIncluded,
}

public static class FrontendQualityDimensionStates
{
    public static string Label(FrontendQualityDimensionState state) => state switch
    {
        FrontendQualityDimensionState.Included => "Included",
        FrontendQualityDimensionState.Limited => "Limited",
        FrontendQualityDimensionState.PartialEvidence => "Partial evidence",
        FrontendQualityDimensionState.NotIncluded => "Not included",
        _ => state.ToString(),
    };

    public static string Tone(FrontendQualityDimensionState state) => state switch
    {
        FrontendQualityDimensionState.Included => "ready",
        FrontendQualityDimensionState.NotIncluded => "muted",
        _ => "attention",
    };
}

/// <param name="ManualAssessmentRequired">
/// The domain needs human assessment that no engine can supply. Rendered beside the scope status rather than replacing
/// it, so "Included · Manual assessment required" stays one honest statement rather than two competing ones.
///
/// It is a property of the criteria, NOT of the automation: it is true when every automated source is available and it
/// stays true however complete the automated coverage becomes. "Manual assessment" is the one term used for it across
/// the review; "manual review" was a second name for the same thing and is gone.
/// </param>
/// <param name="ScopeNote">Short scope fact, such as the selected accessibility profile. Never an engine name.</param>
public sealed record FrontendQualityDimensionCard(
    FrontendQualityCategory Category,
    string Title,
    string Purpose,
    FrontendQualityDimensionState State,
    string? Limitation,
    bool ManualAssessmentRequired = false,
    string? ScopeNote = null);

public sealed record FrontendQualityCheckGroup(string Title, IReadOnlyList<string> Checks, string? Note = null);

public sealed record FrontendQualityNotAssessedItem(string Title, string Description);

public enum FrontendQualityCoverageState
{
    Available,
    /// <summary>Only the public frontend is covered by automated engines.</summary>
    PublicOnly,
    NotAvailable,
    NotRequired,
}

public static class FrontendQualityCoverageStates
{
    public static string Label(FrontendQualityCoverageState state) => state switch
    {
        FrontendQualityCoverageState.Available => "Available",
        FrontendQualityCoverageState.PublicOnly => "Public frontend only",
        FrontendQualityCoverageState.NotAvailable => "Not available",
        // Scope-relative: the review is configured for the public surface, which says nothing about whether the target
        // uses sign-in anywhere.
        FrontendQualityCoverageState.NotRequired => "Not required for current scope",
        _ => state.ToString(),
    };

    public static string Tone(FrontendQualityCoverageState state) => state switch
    {
        FrontendQualityCoverageState.Available => "ready",
        FrontendQualityCoverageState.NotRequired => "muted",
        _ => "attention",
    };

    /// <summary>Text glyph shown next to the label so the state is never colour-only.</summary>
    public static string Glyph(FrontendQualityCoverageState state) => state == FrontendQualityCoverageState.Available ? "✓" : "○";
}

public sealed record FrontendQualityCoverageRow(string Label, FrontendQualityCoverageState State, string Detail);

public sealed record FrontendQualityTargetSummaryModel(
    string Environment,
    string EnvironmentType,
    string Url,
    string Authentication,
    string TargetStatus,
    bool TargetReady);

/// <summary>A label/value pair for the technical-details disclosures (exact source values, no wording changes).</summary>
public sealed record FrontendQualityTechnicalField(string Label, string Value);

public sealed record FrontendQualityEvidenceRow(string Label, string Value, bool Available);

/// <summary>
/// Browser evidence as this review consumes it. Live and historical are separate fields on purpose: nothing in the
/// live half may be derived from the historical half, or the other way round.
/// </summary>
/// <param name="LiveDomAvailable">A DOM can be read from the open page right now. Not the same as captured DOM evidence.</param>
/// <param name="ApprovedOrigins">Technical context. Administered in Browser Discovery; shown here only under technical details.</param>
public sealed record FrontendQualityBrowserEvidenceModel(
    string LiveStatus,
    bool LiveConnected,
    string CurrentPage,
    /// <summary>Origin + route: the live page's full identity, for technical details only.</summary>
    string CurrentPageIdentity,
    bool HasLivePage,
    bool LiveDomAvailable,
    int PagesCaptured,
    string DomEvidence,
    string AccessibilityEvidence,
    string PerformanceEvidence,
    DateTimeOffset? LastCapturedAt,
    IReadOnlyList<string> ApprovedOrigins)
{
    /// <summary>Exact local timestamp, labelled as history wherever it is shown. Never a live heartbeat.</summary>
    public string LastCapturedLabel =>
        LastCapturedAt is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "None captured";

    public bool HasHistory => PagesCaptured > 0;
}

/// <summary>Workflow-oriented authenticated-review panel; built only when the target requires authentication.</summary>
public sealed record FrontendQualityAuthenticatedReviewModel(
    AuthenticatedTestingMethod Method,
    string StatusLabel,
    bool Connected,
    string Introduction,
    IReadOnlyList<string> Steps,
    IReadOnlyList<FrontendQualityEvidenceRow> Evidence,
    string? Note = null);

/// <summary>The engines whose evidence feeds each quality dimension. Single mapping shared by landing cards and result scores.</summary>
public static class FrontendQualityCategoryEngines
{
    public static IReadOnlyList<FrontendQualityEngineId> For(FrontendQualityCategory category) => category switch
    {
        FrontendQualityCategory.Security => [FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineId.PassiveSecurity],
        FrontendQualityCategory.Standards => [FrontendQualityEngineId.StaticSecurity],
        FrontendQualityCategory.Performance => [FrontendQualityEngineId.PassivePerformance, FrontendQualityEngineId.Lighthouse, FrontendQualityEngineId.BrowserQuality, FrontendQualityEngineId.PerformanceQuality],
        FrontendQualityCategory.Readiness => [FrontendQualityEngineId.PassivePerformance],
        FrontendQualityCategory.BlazorWasm => [FrontendQualityEngineId.PassivePerformance, FrontendQualityEngineId.StaticSecurity, FrontendQualityEngineId.BrowserRuntime, FrontendQualityEngineId.BrowserQuality, FrontendQualityEngineId.PerformanceQuality],
        FrontendQualityCategory.Accessibility => [FrontendQualityEngineId.Accessibility, FrontendQualityEngineId.BrowserQuality],
        _ => [],
    };

    /// <summary>
    /// The engines that carry a domain's BASELINE review — what makes the domain worth including at all. Everything
    /// else <see cref="For"/> lists is optional evidence that enriches the domain without deciding whether it runs.
    ///
    /// Accessibility deliberately has none: its scope comes from the selected WCAG profile and the manual assessment
    /// it requires, so no engine decides whether accessibility is part of the review.
    /// </summary>
    public static IReadOnlyList<FrontendQualityEngineId> BaselineFor(FrontendQualityCategory category) => category switch
    {
        FrontendQualityCategory.Security => [FrontendQualityEngineId.StaticSecurity],
        FrontendQualityCategory.Standards => [FrontendQualityEngineId.StaticSecurity],
        FrontendQualityCategory.Performance => [FrontendQualityEngineId.PassivePerformance],
        FrontendQualityCategory.Readiness => [FrontendQualityEngineId.PassivePerformance],
        FrontendQualityCategory.BlazorWasm => [FrontendQualityEngineId.PassivePerformance, FrontendQualityEngineId.StaticSecurity],
        _ => [],
    };

    /// <summary>Optional evidence sources: their absence is a limitation, never an exclusion.</summary>
    public static IReadOnlyList<FrontendQualityEngineId> OptionalFor(FrontendQualityCategory category) =>
        For(category).Except(BaselineFor(category)).ToList();

    public static string Label(FrontendQualityCategory category) => category switch
    {
        FrontendQualityCategory.Performance => "Performance",
        FrontendQualityCategory.Security => "Security",
        FrontendQualityCategory.Accessibility => "Accessibility",
        FrontendQualityCategory.Standards => "Standards Compliance",
        FrontendQualityCategory.BlazorWasm => "Blazor / WASM",
        FrontendQualityCategory.Readiness => "QA Readiness",
        _ => category.ToString(),
    };
}

/// <summary>
/// Compact counts for the collapsed "Review capabilities" row. Configuration (<paramref name="EnabledCount"/>) and
/// capability (<paramref name="AvailableNowCount"/>) are counted separately and never merged: an engine can be enabled
/// and still unavailable, and an unavailable engine must never read as "someone switched it off".
/// </summary>
/// <param name="RequiredButDisabledCount">Required by policy but switched off — a configuration inconsistency, not a run failure.</param>
/// <param name="NeedsPairingCount">Active, but waiting on a one-time setup or a session (Browser Companion pairing, review sign-in).</param>
/// <param name="DisabledCount">
/// Engines deliberately off for this run — switched off in the Target Environment, deselected for this review, or
/// switched off in System Settings. Counted apart from <paramref name="AvailableNowCount"/> on purpose: a switched-off
/// engine is working as configured and must never be counted, or read, as something that is unavailable.
/// </param>
public sealed record FrontendQualityCapabilitySummary(
    int TotalCount,
    int EnabledCount,
    int AvailableNowCount,
    int RequiredButDisabledCount,
    int NeedsPairingCount,
    int DisabledCount = 0)
{
    public string? StateSummary { get; init; }
    /// <summary>The full line shown with the expanded list: what is configured, then what can actually run right now.</summary>
    public string Headline => StateSummary ?? $"{EnabledCount} of {TotalCount} engines enabled · {AvailableNowCount} available right now";

    /// <summary>
    /// The collapsed row: the three axes, each with its own count and none of them merged. A Required engine switched
    /// off is appended, because it is a configuration inconsistency the user would otherwise have to expand to find.
    /// </summary>
    public string Collapsed
    {
        get
        {
            if (StateSummary is not null) return StateSummary;
            var line = $"{EnabledCount} enabled · {AvailableNowCount} available";
            if (DisabledCount > 0) line += $" · {DisabledCount} disabled";
            if (RequiredButDisabledCount > 0)
                line += $" · {RequiredButDisabledCount} required engine{(RequiredButDisabledCount == 1 ? "" : "s")} disabled";
            return line;
        }
    }
}

/// <summary>
/// Compact counts for the collapsed "Coverage" row. Descriptive counts only — the coverage model carries no measured
/// proportion, so no percentage is invented from it.
/// </summary>
/// <param name="PublicOnlyCount">
/// Access paths that reach the public frontend but not the signed-in application. Counted in its own bucket because it
/// is neither available nor unavailable, and folding it into either one would overstate or understate what can be reached.
/// </param>
public sealed record FrontendQualityCoverageSummaryModel(
    int TotalCount, int AvailableCount, int NotAvailableCount, int NotRequiredCount = 0, int PublicOnlyCount = 0)
{
    /// <summary>
    /// What the collapsed "Review access" row says. Arithmetic ("3 available · 2 not required") is correct and tells the
    /// reader nothing: an access path this target does not need is not one the review is missing, so the whole answer to
    /// "can this review reach what it needs?" is a sentence, not a sum. The counts stay in the expanded rows, where each
    /// one is attached to the path it describes.
    /// </summary>
    /// <remarks>
    /// NotRequired rows exist only when the review is scoped to the public surface, so their presence says the access
    /// that is available is public access — never "all" access, which read as the whole application being reachable.
    /// </remarks>
    public string Headline =>
        NotAvailableCount > 0
            ? $"{NotAvailableCount} access path{(NotAvailableCount == 1 ? "" : "s")} not available"
            : PublicOnlyCount > 0
                ? "Automated review limited to the public frontend"
                : NotRequiredCount > 0
                    ? "Public review access available"
                    : "All required access paths available";
}
