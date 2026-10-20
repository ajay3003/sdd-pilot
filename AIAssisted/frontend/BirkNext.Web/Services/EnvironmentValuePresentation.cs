using BirkNext.SourceDomains;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Plain wording for an environment-specific value shown in a setup form: what is configured, what source declares for this environment, and
/// that neither is runtime verified. Generic: names no product, host or project.
/// </summary>
public static class EnvironmentValuePresentation
{
    /// <summary>The source environment a target environment profile matches (explicit type; a custom type only by an environment its name states).</summary>
    public static SourceEnvironmentKind? TargetKind(FrontendAnalysisProfile? profile) =>
        profile is null ? null : EnvironmentValueResolver.TargetKind(profile.EnvironmentType.ToString(), profile.Name);

    public static SourceEnvironmentKind? TargetKind(FrontendEnvironmentType type, string? name) => EnvironmentValueResolver.TargetKind(type.ToString(), name);

    public static string EnvironmentLabel(SourceEnvironmentKind? kind) => kind switch
    {
        null => "this environment",
        SourceEnvironmentKind.QA => "QA",
        _ => kind.Value.ToString(),
    };

    /// <summary>"Gateway.Api · appsettings.Qa.json (Cors:AllowedOrigins:0)".</summary>
    public static string Provenance(EnvironmentValueCandidate candidate) =>
        $"{(string.IsNullOrWhiteSpace(candidate.Component) ? "" : candidate.Component + " · ")}{FileName(candidate.File)} ({candidate.ConfigurationKey})";

    public static string FileName(string path) => path.Replace('\\', '/').Split('/')[^1];

    /// <summary>The short state line under a field. Null when there is nothing to say (no source evidence for the field at all).</summary>
    public static string? StateLine(EnvironmentValueResolution resolution, bool prefilled, bool anySourceValueForKey) => resolution.State switch
    {
        EnvironmentValueState.SourceSuggested when prefilled =>
            $"Source-derived · {EnvironmentLabel(resolution.Environment)} · not saved until you save · {resolution.RuntimeState}",
        EnvironmentValueState.SourceSuggested =>
            $"Source value for {EnvironmentLabel(resolution.Environment)}: {resolution.Value} · {resolution.RuntimeState}",
        EnvironmentValueState.ReviewRequired =>
            $"Review required: source declares {resolution.Candidates.Select(c => EnvironmentValueResolver.Normalize(c.Value)).Distinct(StringComparer.OrdinalIgnoreCase).Count()} different values for {EnvironmentLabel(resolution.Environment)}. None is filled in.",
        EnvironmentValueState.ConfiguredMatchesSource => $"Configured · matches source ({EnvironmentLabel(resolution.Environment)}) · {resolution.RuntimeState}",
        EnvironmentValueState.ConfiguredDiffersFromSource =>
            $"Configured · differs from source: {EnvironmentLabel(resolution.Environment)} declares {resolution.Candidates[0].Value}. The configured value is kept.",
        EnvironmentValueState.NoValue when anySourceValueForKey && resolution.Environment is not null =>
            $"No {EnvironmentLabel(resolution.Environment)} value found in the imported source configuration.",
        _ => null,
    };

    public static string Tone(EnvironmentValueState state) => state switch
    {
        EnvironmentValueState.SourceSuggested or EnvironmentValueState.ConfiguredMatchesSource => "info",
        EnvironmentValueState.ReviewRequired or EnvironmentValueState.ConfiguredDiffersFromSource => "attention",
        _ => "muted",
    };
}
