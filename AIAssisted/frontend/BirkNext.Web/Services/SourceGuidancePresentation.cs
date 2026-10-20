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
        SourceSuggestionState.SourceMissing => "Required source missing",
        _ => "Not in the bound source",
    };

    // ── Logical sources (test pack source roles) ──

    public static string SourceStateLabel(TestPackSourceState state) => state switch
    {
        TestPackSourceState.Bound => "Bound",
        TestPackSourceState.AvailableNotBound => "Available, not bound",
        TestPackSourceState.ReviewRequired => "Review required",
        TestPackSourceState.Available => "Available",
        TestPackSourceState.NotProvided => "Not provided (optional)",
        _ => "Missing",
    };

    public static string SourceStateTone(TestPackSourceState state) => state switch
    {
        TestPackSourceState.Bound or TestPackSourceState.Available => "ready",
        TestPackSourceState.Missing or TestPackSourceState.NotProvided => "muted",
        _ => "attention",
    };

    /// <summary>"MU bound", "M2LB available, not bound", "Altinn … missing".</summary>
    public static string SourceShort(TestPackSourceStatus status) => $"{status.Role.Label} {SourceStateLabel(status.State).ToLowerInvariant()}";

    /// <summary>Where a source's next action is done.</summary>
    public static string? ActionHref(string? target) => target switch
    {
        "source-analysis" => "/source-analysis",
        "iqr-evidence" => "/integration-quality-review?view=evidence",
        "target-environment-integrations" => "/admin/system-settings?section=target-environments&tab=integrations",
        _ => null,
    };

    /// <summary>The sources a journey needs, in the pack's order.</summary>
    public static IReadOnlyList<TestPackSourceStatus> ForJourney(TestPackSourceGuidance? guidance, string journeyId) =>
        guidance?.Sources.Where(item => item.Role.Journeys.Contains(journeyId, StringComparer.Ordinal)).ToList() ?? [];

    /// <summary>The logical source an expected connection's components belong to (by the sources' component names), if any.</summary>
    public static TestPackSourceStatus? ForComponents(TestPackSourceGuidance? guidance, string from, string to) =>
        guidance?.Sources.FirstOrDefault(item => !item.Role.RequiresApiDocument && item.Role.ComponentTokens.Any(token => Names(token, to)))
        ?? guidance?.Sources.FirstOrDefault(item => !item.Role.RequiresApiDocument && item.Role.ComponentTokens.Any(token => Names(token, from)));

    private static bool Names(string token, string component) =>
        component.Split([' ', '.', '/', '-', '(', ')'], StringSplitOptions.RemoveEmptyEntries).Any(word => string.Equals(word, token, StringComparison.OrdinalIgnoreCase))
        || string.Equals(component.Trim(), token, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why an expected connection is unresolved, in terms of the source that would state it.</summary>
    public static string? ConnectionHint(TestPackSourceStatus? status) => status switch
    {
        null => null,
        { State: TestPackSourceState.Bound } => $"Required source: {status.Role.Label} (bound: {string.Join(", ", status.BoundArchives)}). The bound source states no such connection yet.",
        { State: TestPackSourceState.AvailableNotBound } => $"Required source: {status.Role.Label}. Available: {string.Join(", ", status.AvailableArchives)} — not bound to a relevant integration. Next: {status.NextAction}.",
        { State: TestPackSourceState.ReviewRequired } => $"Required source: {status.Role.Label}. {status.Detail} Next: {status.NextAction}.",
        _ => $"Required source: {status.Role.Label} — not imported. Next: {status.NextAction}.",
    };

    public static string Tone(SourceSuggestionState state) => state switch
    {
        SourceSuggestionState.Suggested => "info",
        SourceSuggestionState.Conflict => "attention",
        SourceSuggestionState.AvailableNotBound => "attention",
        SourceSuggestionState.SourceMissing => "attention",
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
        SourceSuggestionState.SourceMissing => ($"Import the {suggestion.SourceRoleLabel ?? suggestion.Requirement.LogicalSource} source in Source Analysis", "/source-analysis"),
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
