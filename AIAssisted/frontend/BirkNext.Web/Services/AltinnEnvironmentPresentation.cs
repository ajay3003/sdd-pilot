using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Labels and tones for Target Environment → External Integrations → Altinn. Every state is shown as text (never colour alone) and states are never collapsed into a
/// generic "available": configured, detected (from source or metadata), suggested (a test-pack default), observed (runtime evidence) and
/// verified stay distinct.
/// </summary>
public static class AltinnEnvironmentPresentation
{
    /// <summary>The environment's Altinn system under Target Environment → External Integrations.</summary>
    public static string AltinnHref(string environmentId) => ExternalIntegrationPresentation.ExternalHref(environmentId, "altinn");

    public const string TestDataHref = "/test-data";

    public static string StateLabel(AltinnSettingField field) => field.State switch
    {
        AltinnSettingState.Detected => field.Source switch
        {
            AltinnSettingSource.SourceAnalysis => "Detected from source",
            AltinnSettingSource.PublicMetadata => "Detected · public metadata",
            _ => "Detected",
        },
        _ => StateLabel(field.State),
    };

    public static string StateLabel(AltinnSettingState state) => state switch
    {
        AltinnSettingState.Configured => "Configured",
        AltinnSettingState.Detected => "Detected",
        AltinnSettingState.Observed => "Observed",
        AltinnSettingState.Declared => "Suggested",
        AltinnSettingState.Confirmed => "Confirmed",
        AltinnSettingState.Verified => "Runtime verified",
        AltinnSettingState.Missing => "Missing",
        AltinnSettingState.Conflict => "Conflict",
        AltinnSettingState.AvailableToBackend => "Available to backend",
        AltinnSettingState.AvailableForSession => "Available for this session",
        _ => "Unknown",
    };

    public static string Tone(AltinnSettingField field) => field.State switch
    {
        AltinnSettingState.Configured or AltinnSettingState.Confirmed or AltinnSettingState.Verified
            or AltinnSettingState.AvailableToBackend or AltinnSettingState.AvailableForSession => "ok",
        AltinnSettingState.Conflict => "warn",
        AltinnSettingState.Missing => field.Requirement == AltinnRequirement.Optional ? "muted" : "warn",
        AltinnSettingState.Unknown => "muted",
        _ => "info",
    };

    public static string Tone(AltinnReadinessState state) => state switch
    {
        AltinnReadinessState.Ready => "ok",
        AltinnReadinessState.Partial => "warn",
        AltinnReadinessState.Blocked => "bad",
        _ => "muted",
    };

    public static string ReadinessLabel(AltinnReadinessItem item) => item.Key == "runtime" && item.State == AltinnReadinessState.Partial ? "Observed" : item.State switch
    {
        AltinnReadinessState.Ready => "Ready",
        AltinnReadinessState.Partial => "Partial",
        AltinnReadinessState.Blocked => "Blocked",
        _ => "Not verified",
    };

    public static string RequirementLabel(AltinnRequirement requirement) => requirement switch
    {
        AltinnRequirement.Manual => "Required",
        AltinnRequirement.Automated => "Automated execution",
        _ => "Optional",
    };

    public static string ObservationLabel(AltinnRuntimeObservationKind kind) => kind switch
    {
        AltinnRuntimeObservationKind.ReporterLogin => "Reporter TestID login",
        AltinnRuntimeObservationKind.IdPortenAuthentication => "ID-porten authentication",
        AltinnRuntimeObservationKind.AppOpened => "TT02 app opened",
        AltinnRuntimeObservationKind.FormCompleted => "Form completed",
        AltinnRuntimeObservationKind.Submission => "Report submitted",
        _ => "Altinn receipt generated",
    };

    /// <summary>"7 configured · 4 detected · 2 runtime observations · 1 value missing · 0 conflicts".</summary>
    public static string DetectionLine(AltinnDetectionSummary summary) =>
        $"{summary.Configured} configured · {summary.Detected} detected · {summary.Observed} runtime observation{(summary.Observed == 1 ? "" : "s")} · "
        + $"{summary.Missing} value{(summary.Missing == 1 ? "" : "s")} missing · {summary.Conflicts} conflict{(summary.Conflicts == 1 ? "" : "s")}";

    /// <summary>"1 required setting missing" (top summary), or null when nothing required is missing or in conflict.</summary>
    public static string? MissingLine(AltinnEnvironmentView view) =>
        view.Detection.ActionableGaps.Count == 0 ? null
            : $"{view.Detection.ActionableGaps.Count} required setting{(view.Detection.ActionableGaps.Count == 1 ? "" : "s")} missing or in conflict";

    /// <summary>The fields a person may deliberately apply: a proposal that is not secret and not evidence.</summary>
    public static IReadOnlyList<AltinnSettingField> Proposals(AltinnEnvironmentView view) =>
        [.. view.Fields.Where(field => field.Proposal is { Value: not null } && Applicable.Contains(field.Key))];

    private static readonly string[] Applicable = ["environment", "app", "app-base-url", "form-data-type", "content-type", "integration", "client-id", "redirect-uri", "scopes"];

    /// <summary>The save request for deliberately applied proposals (key → value). Observations and secrets are never applicable.</summary>
    public static AltinnEnvironmentSettingsUpdate Apply(IEnumerable<(string Key, string Value)> selected)
    {
        var update = new AltinnEnvironmentSettingsUpdate();
        foreach (var (key, value) in selected)
            update = key switch
            {
                "environment" => update with { Environment = value.StartsWith("Test", StringComparison.Ordinal) ? "Test" : value },
                "app" => update with { AppIdentifier = value },
                "app-base-url" => update with { AppBaseUrl = value },
                "form-data-type" => update with { FormDataType = value },
                "content-type" => update with { ContentType = value },
                "integration" => update with { IntegrationId = value },
                "client-id" => update with { ClientId = value },
                "redirect-uri" => update with { RedirectUri = value },
                "scopes" => update with { Scopes = [.. value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] },
                _ => update,
            };
        return update;
    }

    public static readonly (string Key, string Label)[] Groups =
    [
        ("Application", "Application"),
        ("Browser authentication", "Browser authentication"),
        ("Automated execution", "Automated execution"),
        ("Execution safety", "Execution safety"),
    ];
}
