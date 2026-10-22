using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Web.Services;

/// <summary>One integration's detail, grouped as it is explained: identity &amp; routing, evidence, authentication, contracts, runtime/health and
/// readiness. Every state comes from the shared field and readiness rules; nothing here re-decides a requirement.</summary>
public sealed record IntegrationDetailView(
    IntegrationConfigurationAssessment Configuration,
    IntegrationReviewReadinessAssessment Readiness,
    IReadOnlyList<IntegrationFieldStatus> Identity,
    IReadOnlyList<IntegrationFieldStatus> Authentication,
    IReadOnlyList<IntegrationFieldStatus> RuntimeHealth,
    IReadOnlyList<IntegrationFieldStatus> NotApplicable);

public static class IntegrationDetailPresentation
{
    private static readonly IntegrationField[] IdentityFields =
        [IntegrationField.ConfiguredChannel, IntegrationField.Namespace, IntegrationField.Producer, IntegrationField.SourceResource, IntegrationField.Consumer, IntegrationField.ConsumerMapping, IntegrationField.ConsumerGroup];

    /// <summary>
    /// Values source evidence gives fields of this integration. The channel match comes from the latest analyzed source (not necessarily the bound
    /// snapshot), so its basis says which archive it is; a differing consumer is a conflict to review, an empty consumer gets a candidate.
    /// </summary>
    public static IReadOnlyList<IntegrationFieldEvidence> FieldEvidence(IntegrationDefinition definition, SourceIntegrationMatch? match, string? latestArchive)
    {
        if (match?.SourceConsumer is not { Length: > 0 } consumer) return [];
        var basis = $"channel match in the latest analyzed source{(latestArchive is { } a ? $" {a}" : "")}{(match.SourceConsumerEvidence is { } e ? $", {SourceIntegrationsPresentation.Evidence(e).ToLowerInvariant()}" : "")}";
        return match.State switch
        {
            SourceMatchState.Conflict => [new(IntegrationField.ConsumerMapping, consumer, IntegrationFieldEvidenceOrigin.Source, basis)],
            SourceMatchState.SourceSuggestion when string.IsNullOrWhiteSpace(definition.Consumer.DisplayName) => [new(IntegrationField.Consumer, consumer, IntegrationFieldEvidenceOrigin.Source, basis)],
            _ => [],
        };
    }

    public static IntegrationDetailView View(IntegrationDefinition definition, IntegrationPlatform? platform, IntegrationEvidenceBinding? binding,
        IntegrationContractDiscovery? discovery, IReadOnlyList<IntegrationFieldEvidence> evidence)
    {
        var configuration = IntegrationFieldRules.Assess(definition, platform, evidence);
        var readiness = IntegrationReadinessRules.Assess(definition, platform, new IntegrationReadinessEvidence
        {
            SourceBound = binding is not null, BoundArchive = binding?.ArchiveName, Contracts = discovery, FieldEvidence = evidence,
        });
        bool Shown(IntegrationFieldStatus f) => f.Resolution != IntegrationFieldResolution.NotApplicable;
        return new(configuration, readiness,
            [.. IdentityFields.Select(configuration.Field).OfType<IntegrationFieldStatus>().Where(Shown)],
            [.. new[] { IntegrationField.ProducerAuthentication, IntegrationField.ConsumerAuthentication }.Select(configuration.Field).OfType<IntegrationFieldStatus>().Where(Shown)],
            [.. new[] { IntegrationField.HealthEndpoint, IntegrationField.WorkerEndpoint }.Select(configuration.Field).OfType<IntegrationFieldStatus>().Where(Shown)],
            configuration.NotApplicable.Where(f => f.Resolution == IntegrationFieldResolution.NotApplicable).ToList());
    }

    public static string Tone(IntegrationFieldResolution resolution) => resolution switch
    {
        IntegrationFieldResolution.Configured or IntegrationFieldResolution.PlatformManaged => "ready",
        IntegrationFieldResolution.TemplateDefault or IntegrationFieldResolution.DetectedFromSource or IntegrationFieldResolution.DetectedFromRuntime => "info",
        IntegrationFieldResolution.NeedsConfirmation or IntegrationFieldResolution.ReviewRequired => "attention",
        IntegrationFieldResolution.NotConfigured => "warning",
        _ => "muted",
    };

    public static string Tone(IntegrationContractCandidateState state) => state switch
    {
        IntegrationContractCandidateState.Resolved => "ready",
        IntegrationContractCandidateState.Candidate => "info",
        IntegrationContractCandidateState.NeedsConfirmation or IntegrationContractCandidateState.Ambiguous or IntegrationContractCandidateState.ParseFailed => "attention",
        _ => "muted",
    };

    /// <summary>Relationship review tone: confirmed source usage or an explicit selection is informational (never "ready" — runtime is not
    /// assessed); candidates and ambiguity need attention.</summary>
    public static string Tone(BirkNext.ContractSchemaEvidence.ContractRelationshipReviewState state) => state switch
    {
        BirkNext.ContractSchemaEvidence.ContractRelationshipReviewState.Confirmed or BirkNext.ContractSchemaEvidence.ContractRelationshipReviewState.Bound => "info",
        BirkNext.ContractSchemaEvidence.ContractRelationshipReviewState.NotAssessed or BirkNext.ContractSchemaEvidence.ContractRelationshipReviewState.NoMatchingEvidence => "muted",
        _ => "attention",
    };

    public static string Tone(IntegrationContractCompatibilityState state) => state switch
    {
        IntegrationContractCompatibilityState.Compatible => "ready",
        IntegrationContractCompatibilityState.DifferencesFound => "attention",
        IntegrationContractCompatibilityState.ReadyToAssess => "info",
        _ => "muted",
    };

    /// <summary>"Configuration status" headline: Ready, or what kind of attention it needs (never "Needs configuration" for an optional gap).</summary>
    public static string ConfigurationHeadline(IntegrationConfigurationAssessment configuration) => configuration.State switch
    {
        IntegrationConfigurationState.NeedsConfiguration => "Needs configuration",
        IntegrationConfigurationState.NeedsConfirmation => "Needs attention",
        IntegrationConfigurationState.Disabled => "Disabled",
        _ => "Ready",
    };

    /// <summary>Where the record came from — value/configuration origin, never source or runtime verification.</summary>
    public static string OriginMeaning(IntegrationDefinition definition) => definition.Origin switch
    {
        IntegrationRecordOrigin.Template => "Created from an integration template. Origin of the configured values only — not source or runtime verification.",
        IntegrationRecordOrigin.SourceSuggestion => "Added from a Source Analysis suggestion. Origin only — the mapping stays a suggestion until a person confirms it.",
        IntegrationRecordOrigin.Seed => "Seeded environment record. Origin only — not verified.",
        _ => "Configured by a person. Origin only — not source or runtime verification.",
    };

    /// <summary>The channel match note, stating which snapshot it comes from when that is not the integration's bound snapshot.</summary>
    public static string ChannelMatch(SourceIntegrationMatch? match, string? latestArchive, Guid? latestSnapshot, IntegrationEvidenceBinding? binding)
    {
        if (match is null) return "Channel match: not analyzed (no analyzed source).";
        var text = $"Channel match: {SourceIntegrationLabels.Match(match.State)} — {match.Detail}";
        if (binding is not null && latestSnapshot is { } latest && latest != binding.SnapshotId)
            text += $" (Matched against the latest analyzed source {latestArchive ?? "snapshot"}, not the bound snapshot {binding.ArchiveName}.)";
        return text;
    }
}
