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
        FrontendQualityCapabilityState.Enabled => "Enabled",
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

    /// <summary>The capability is part of this review (enabled and selected), whatever its availability.</summary>
    public static bool IsActive(FrontendQualityCapabilityState state) =>
        state is not (FrontendQualityCapabilityState.Disabled or FrontendQualityCapabilityState.NotSelected);

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
    bool Selected = false)
{
    public bool IsActive => FrontendQualityCapabilityStates.IsActive(State);
    public bool IsAvailable => FrontendQualityCapabilityStates.IsAvailable(State);
}

public enum FrontendQualityDimensionState
{
    Enabled,
    /// <summary>Derived dimension (QA Readiness): indicators are available from the evidence other capabilities collect.</summary>
    Available,
    NotEnabled,
    Unavailable,
}

public static class FrontendQualityDimensionStates
{
    public static string Label(FrontendQualityDimensionState state) => state switch
    {
        FrontendQualityDimensionState.Enabled => "Enabled",
        FrontendQualityDimensionState.Available => "Available",
        FrontendQualityDimensionState.NotEnabled => "Not enabled",
        FrontendQualityDimensionState.Unavailable => "Unavailable",
        _ => state.ToString(),
    };

    public static string Tone(FrontendQualityDimensionState state) => state switch
    {
        FrontendQualityDimensionState.Enabled or FrontendQualityDimensionState.Available => "ready",
        FrontendQualityDimensionState.NotEnabled => "muted",
        _ => "attention",
    };
}

public sealed record FrontendQualityDimensionCard(
    FrontendQualityCategory Category,
    string Title,
    string Purpose,
    FrontendQualityDimensionState State,
    string? Limitation);

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
        FrontendQualityCoverageState.NotRequired => "Not required",
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
