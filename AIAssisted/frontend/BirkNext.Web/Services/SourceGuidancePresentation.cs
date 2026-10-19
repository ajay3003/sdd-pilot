using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Web.Services;

/// <summary>
/// Generic wording for source-declared configuration: declared in source ≠ suggested ≠ configured ≠ runtime verified. Reads only the backend's
/// guidance and requirement metadata (semantic key, logical source, evidence type); it names no product, file or project.
/// </summary>
public static class SourceGuidancePresentation
{
    public static string StateLabel(SourceSuggestionState state) => state switch
    {
        SourceSuggestionState.Suggested => "Suggested from bound source",
        SourceSuggestionState.AvailableNotBound => "Available but not bound",
        SourceSuggestionState.Conflict => "Configuration conflict",
        _ => "Not declared in source",
    };

    public static string Tone(SourceSuggestionState state) => state switch
    {
        SourceSuggestionState.Suggested => "info",
        SourceSuggestionState.Conflict => "attention",
        SourceSuggestionState.AvailableNotBound => "attention",
        _ => "muted",
    };

    /// <summary>What is stored: the configured value, or why there is none.</summary>
    public static string Configured(SourceConfigurationSuggestion suggestion) =>
        suggestion.ConfiguredValue is { } value ? suggestion.ConfiguredDiffers ? $"{value} (differs from source)" : value
        : suggestion.Requirement.ConsumerField is null ? "No field to configure here" : "Not configured";

    /// <summary>"OpenAPI · api/openapi.json → servers[1]" for every artifact behind a value.</summary>
    public static IReadOnlyList<string> Provenance(SourceConfigurationSuggestion suggestion) =>
        suggestion.Candidates.SelectMany(candidate => candidate.Provenance.Select(value =>
            $"{Kind(value.EvidenceKind)} · {value.Artifact} → {value.Location}{(suggestion.State == SourceSuggestionState.Conflict ? $" = {candidate.Value}" : "")}")).Distinct().ToList();

    public static string Kind(SourceConfigurationEvidenceKind kind) => kind switch
    {
        SourceConfigurationEvidenceKind.OpenApi => "OpenAPI",
        SourceConfigurationEvidenceKind.ApplicationConfig => "Configuration",
        SourceConfigurationEvidenceKind.InfrastructureAsCode => "Infrastructure as Code",
        SourceConfigurationEvidenceKind.DeploymentManifest => "Deployment manifest",
        SourceConfigurationEvidenceKind.PipelineConfig => "Pipeline",
        SourceConfigurationEvidenceKind.SourceConstant => "Source constant",
        _ => "Source",
    };

    /// <summary>The next step for a tester, with where it is done.</summary>
    public static (string Label, string? Href) NextAction(SourceConfigurationSuggestion suggestion) => suggestion.State switch
    {
        SourceSuggestionState.Suggested when suggestion.Requirement.ConsumerField is { } field && (suggestion.ConfiguredValue is null || suggestion.ConfiguredDiffers) =>
            ($"Review and use the suggested value in {suggestion.Requirement.Owner} → {field}", null),
        SourceSuggestionState.Suggested => ("Nothing to configure; verify it at runtime", null),
        SourceSuggestionState.Conflict => ($"Decide which value is correct in {suggestion.Requirement.Owner}", null),
        SourceSuggestionState.AvailableNotBound => ("Bind the snapshot in Integration Quality Review → Evidence", "/integration-quality-review?view=evidence"),
        _ => ($"Import the {suggestion.Requirement.EvidenceType} of {suggestion.Requirement.LogicalSource} in Source Analysis", "/source-analysis"),
    };

    /// <summary>What kind of input a prerequisite needs, for a tester: source evidence, source-derived configuration, configuration, Test Data,
    /// runtime evidence or an external attestation. From category metadata (and whether source guidance answers it), never from text.</summary>
    public static string RequirementKind(JourneyPrerequisite item, bool sourceDerived) => sourceDerived ? "Source-derived configuration" : item.Category switch
    {
        JourneyPrerequisiteCategory.SourceEvidence => "Source evidence",
        JourneyPrerequisiteCategory.TrustedEnvironment or JourneyPrerequisiteCategory.Integration or JourneyPrerequisiteCategory.Authentication => "Configuration",
        JourneyPrerequisiteCategory.SyntheticData => "Test Data",
        JourneyPrerequisiteCategory.Authorization => "External attestation",
        JourneyPrerequisiteCategory.Contract when item.Source == "Source Analysis" => "Source evidence",
        JourneyPrerequisiteCategory.Contract or JourneyPrerequisiteCategory.Observation or JourneyPrerequisiteCategory.DownstreamVerification => "Runtime evidence",
        _ => "BirkNext capability",
    };

    /// <summary>The guidance that answers a prerequisite (by the requirement's declared prerequisite key).</summary>
    public static IReadOnlyList<SourceConfigurationSuggestion> For(TestPackSourceGuidance? guidance, string prerequisiteKey) =>
        guidance?.Suggestions.Where(item => item.Requirement.PrerequisiteKey == prerequisiteKey).ToList() ?? [];

    /// <summary>A display group for a configuration value.</summary>
    public static string Group(SourceConfigurationGroup group) => group switch
    {
        SourceConfigurationGroup.Application => "Application",
        SourceConfigurationGroup.Environment => "Environment / server",
        SourceConfigurationGroup.Authentication => "Authentication",
        SourceConfigurationGroup.ApiEndpoints => "API endpoints",
        SourceConfigurationGroup.Payload => "Payload",
        SourceConfigurationGroup.Contract => "Contracts",
        SourceConfigurationGroup.Messaging => "Messaging",
        SourceConfigurationGroup.Infrastructure => "Infrastructure",
        _ => "Other",
    };
}
