using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

/// <summary>
/// Activation of one quality engine for one review, derived from SAVED configuration only:
/// <list type="bullet">
/// <item><b>Policy</b> (Required/Optional) comes from the profile's engine requirements.</item>
/// <item><b>Enabled</b> comes from the profile's engine feature toggles (per Target Environment, persisted).</item>
/// <item><b>Selected</b> is the per-review opt-out for the optional backend engines (Browser Runtime, Accessibility, Lighthouse,
/// Passive Security); the two HTTP engines are always selected when enabled.</item>
/// </list>
/// <c>Active = Enabled &amp;&amp; Selected</c>. Runtime capability, readiness, deployment policy and system settings are evaluated
/// AFTER activation and never make an engine active.
/// </summary>
public sealed record FrontendQualityEngineActivation(
    [property: JsonPropertyName("engineId")] FrontendQualityEngineId EngineId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("policy")] FrontendQualityEngineRequirement Policy,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("selected")] bool Selected)
{
    [JsonPropertyName("active")] public bool Active => Enabled && Selected;
    [JsonIgnore] public FrontendQualityEngineAccessRequirements Access => FrontendQualityEngineAccessRegistry.For(EngineId);
    /// <summary>A required engine that is disabled is a configuration inconsistency: it can never be assessed, so required coverage stays incomplete.</summary>
    [JsonIgnore] public bool RequiredButDisabled => Policy == FrontendQualityEngineRequirement.Required && !Enabled;
}

/// <summary>
/// Immutable set of engine activations captured when a review starts. Orchestration preflights and runs only the active engines;
/// coverage, release disposition and exports are computed against this snapshot, never against settings edited later.
/// </summary>
public sealed record FrontendQualityActiveEngineSnapshot
{
    [JsonPropertyName("profileId")] public string? ProfileId { get; init; }
    [JsonPropertyName("capturedAtUtc")] public DateTime CapturedAtUtc { get; init; } = DateTime.UtcNow;
    [JsonPropertyName("engines")] public IReadOnlyList<FrontendQualityEngineActivation> Engines { get; init; } = [];

    [JsonIgnore] public IReadOnlyList<FrontendQualityEngineActivation> Active => Engines.Where(e => e.Active).ToList();
    [JsonIgnore] public IReadOnlyList<FrontendQualityEngineActivation> Inactive => Engines.Where(e => !e.Active).ToList();
    [JsonIgnore] public int ActiveCount => Engines.Count(e => e.Active);
    [JsonIgnore] public int RequiredActiveCount => Engines.Count(e => e.Active && e.Policy == FrontendQualityEngineRequirement.Required);
    [JsonIgnore] public int OptionalActiveCount => Engines.Count(e => e.Active && e.Policy == FrontendQualityEngineRequirement.Optional);
    [JsonIgnore] public int DisabledCount => Engines.Count(e => !e.Enabled);
    [JsonIgnore] public bool HasActiveEngines => ActiveCount > 0;
    [JsonIgnore] public IReadOnlyList<FrontendQualityEngineActivation> RequiredButDisabled => Engines.Where(e => e.RequiredButDisabled).ToList();

    public bool IsActive(FrontendQualityEngineId engineId) => Engines.Any(e => e.EngineId == engineId && e.Active);
    public bool IsEnabled(FrontendQualityEngineId engineId) => Engines.Any(e => e.EngineId == engineId && e.Enabled);
    public FrontendQualityEngineActivation? Get(FrontendQualityEngineId engineId) => Engines.FirstOrDefault(e => e.EngineId == engineId);
}

/// <summary>The one authoritative rule for "which engines are active for this review".</summary>
public static class FrontendQualityActiveEngines
{
    public const string NoActiveEnginesMessage = "No review engines are enabled.";
    public const string NoActiveEnginesAction = "Enable at least one Frontend Quality Review engine in the Target Environment's Features tab.";

    /// <summary>Backend (Layer 1–3) engines and their capability-DTO ids. The two HTTP engines have no backend capability record.</summary>
    public static readonly IReadOnlyDictionary<FrontendQualityEngineId, FrontendQualityEngineIdDto> BackendEngineIds =
        new Dictionary<FrontendQualityEngineId, FrontendQualityEngineIdDto>
        {
            [FrontendQualityEngineId.BrowserRuntime] = FrontendQualityEngineIdDto.BrowserRuntime,
            [FrontendQualityEngineId.Accessibility] = FrontendQualityEngineIdDto.Accessibility,
            [FrontendQualityEngineId.Lighthouse] = FrontendQualityEngineIdDto.Lighthouse,
            [FrontendQualityEngineId.PassiveSecurity] = FrontendQualityEngineIdDto.PassiveSecurity,
        };

    public static FrontendQualityEngineId ToEngineId(FrontendQualityEngineIdDto dto) => dto switch
    {
        FrontendQualityEngineIdDto.BrowserRuntime => FrontendQualityEngineId.BrowserRuntime,
        FrontendQualityEngineIdDto.Accessibility => FrontendQualityEngineId.Accessibility,
        FrontendQualityEngineIdDto.Lighthouse => FrontendQualityEngineId.Lighthouse,
        _ => FrontendQualityEngineId.PassiveSecurity,
    };

    /// <summary>Resolves the activation snapshot from the saved configuration carried by the analysis context.</summary>
    public static FrontendQualityActiveEngineSnapshot Resolve(FrontendAnalysisContext context) =>
        Resolve(context.FeatureToggles, context.EngineRequirements, context.ReviewEngineSelection, context.ActiveProfile.Id);

    public static FrontendQualityActiveEngineSnapshot Resolve(
        FrontendAnalysisFeatureToggles toggles,
        FrontendQualityEngineRequirementSettings requirements,
        ReviewEngineSelection selection,
        string? profileId = null)
    {
        var policy = requirements.ToPolicy();
        var selectionMap = selection.ToSelectionMap();
        return new FrontendQualityActiveEngineSnapshot
        {
            ProfileId = profileId,
            CapturedAtUtc = DateTime.UtcNow,
            Engines = Enum.GetValues<FrontendQualityEngineId>()
                .Select(id => new FrontendQualityEngineActivation(
                    id, DisplayName(id), policy.GetRequirement(id), IsEnabled(id, toggles), IsSelected(id, selectionMap)))
                .ToList(),
        };
    }

    /// <summary>Saved per-environment engine toggle. No fallback: a missing toggle is a disabled engine.</summary>
    public static bool IsEnabled(FrontendQualityEngineId id, FrontendAnalysisFeatureToggles toggles) => id switch
    {
        FrontendQualityEngineId.StaticSecurity => toggles.EnableSecurityEngine,
        FrontendQualityEngineId.PassivePerformance => toggles.EnablePerformanceEngine,
        FrontendQualityEngineId.BrowserRuntime => toggles.EnableBrowserRuntimeEngine,
        FrontendQualityEngineId.Accessibility => toggles.EnableAccessibilityEngine,
        FrontendQualityEngineId.Lighthouse => toggles.EnableLighthouseEngine,
        FrontendQualityEngineId.PassiveSecurity => toggles.EnablePassiveSecurityEngine,
        FrontendQualityEngineId.BrowserQuality => toggles.EnableBrowserQualityEngine,
        _ => false,
    };

    /// <summary>Per-review selection applies to the backend engines only; HTTP engines are selected whenever enabled.</summary>
    public static bool IsSelected(FrontendQualityEngineId id, IReadOnlyDictionary<FrontendQualityEngineIdDto, bool> selection) =>
        !BackendEngineIds.TryGetValue(id, out var dto) || (selection.TryGetValue(dto, out var selected) && selected);

    public static string DisplayName(FrontendQualityEngineId id) => id switch
    {
        FrontendQualityEngineId.StaticSecurity => "Static Security",
        FrontendQualityEngineId.PassivePerformance => "Passive Performance",
        FrontendQualityEngineId.BrowserRuntime => "Browser Runtime",
        FrontendQualityEngineId.Accessibility => "Accessibility",
        FrontendQualityEngineId.Lighthouse => "Lighthouse",
        FrontendQualityEngineId.PassiveSecurity => "Passive Security",
        FrontendQualityEngineId.BrowserQuality => "Browser Quality",
        _ => id.ToString(),
    };
}
