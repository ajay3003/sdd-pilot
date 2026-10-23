namespace BirkNext.Web.Models;

/// <summary>Why a consumer has (or has no) Target Environment. Each state is its own answer; none of them is "nothing selected" by default.</summary>
public enum TargetEnvironmentResolutionState
{
    /// <summary>An active Target Environment resolved to exactly one configured environment.</summary>
    Active,
    /// <summary>Target Environments are configured, but none is active ("Set as Active" was never chosen, or it was cleared).</summary>
    NoneSelected,
    /// <summary>No Target Environment is configured at all.</summary>
    NoneConfigured,
    /// <summary>An active environment is recorded but cannot be resolved (deleted, duplicated or unreadable settings).</summary>
    Unresolved,
}

/// <summary>
/// The platform's one answer to "which Target Environment applies here", built by <c>FrontendAnalysisContextFactory</c> from the persisted
/// Target Environment settings. Consumers read it instead of re-deriving selection: there is no per-feature selection and no fallback to the
/// first or only configured environment.
/// </summary>
public sealed record TargetEnvironmentResolution
{
    public TargetEnvironmentResolutionState State { get; init; } = TargetEnvironmentResolutionState.NoneConfigured;
    /// <summary>Configured Target Environments (any state).</summary>
    public int ConfiguredCount { get; init; }
    /// <summary>The recorded active environment id, also when it cannot be resolved. Identity only — never matched by name.</summary>
    public string? ActiveId { get; init; }
    /// <summary>Why an active environment could not be resolved (Unresolved only).</summary>
    public string? Detail { get; init; }

    public const string NoLongerExists = "Selected Target Environment no longer exists.";

    /// <summary>The rule, over the persisted settings. Same precedence as <c>IFrontendAnalysisSettingsService.ActiveProfile</c>.</summary>
    public static TargetEnvironmentResolution From(FrontendAnalysisSettings settings, FrontendAnalysisProfile? active)
    {
        var count = settings.Profiles.Count;
        var id = string.IsNullOrWhiteSpace(settings.ActiveProfileId) ? null : settings.ActiveProfileId;
        if (active is not null) return new() { State = TargetEnvironmentResolutionState.Active, ConfiguredCount = count, ActiveId = active.Id };
        if (settings.ActiveResolutionError is { Length: > 0 } error)
            return new() { State = TargetEnvironmentResolutionState.Unresolved, ConfiguredCount = count, ActiveId = id, Detail = error };
        if (id is not null)
            return new() { State = TargetEnvironmentResolutionState.Unresolved, ConfiguredCount = count, ActiveId = id,
                Detail = settings.Profiles.Count(p => p.Id == id) > 1 ? "The selected Target Environment id is configured more than once." : NoLongerExists };
        return new() { State = count == 0 ? TargetEnvironmentResolutionState.NoneConfigured : TargetEnvironmentResolutionState.NoneSelected, ConfiguredCount = count };
    }
}
