using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Field semantics of one configured integration. Each field answers four separate questions: does it apply to this kind
// (applicability), must it be configured (requirement), where does its value come from (resolution + provenance), and does
// a person still have to decide (confirmation). "Not configured" means exactly one thing: applicable, required, empty and
// not resolved by acceptable evidence. Not applicable, Not assessed, Needs confirmation and optional fields never read as it.
// Derived only — nothing here is stored; export and persistence keep the configured values and recompute these states.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationField
{
    DisplayName, ConfiguredChannel, Namespace, SourceResource, ConsumerGroup, Producer, Consumer, ConsumerMapping,
    ProducerAuthentication, ConsumerAuthentication, HealthEndpoint, WorkerEndpoint,
}

/// <summary>Conditional: required only when <see cref="IntegrationFieldStatus.Condition"/> holds (e.g. checkpoint review needs a consumer group).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationFieldRequirement { Required, Conditional, Optional, NotApplicable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationFieldResolution
{
    /// <summary>Explicitly stored for this integration in this environment.</summary>
    Configured,
    /// <summary>Not stored; source evidence provides the value. Never saved by being shown.</summary>
    DetectedFromSource,
    /// <summary>Not stored; runtime evidence provides the value.</summary>
    DetectedFromRuntime,
    /// <summary>A seeded/template default nobody has changed: a safe default, not source proof. (A record a person saved from a template is
    /// Configured, with "Template" as its provenance.)</summary>
    TemplateDefault,
    /// <summary>A candidate or assumption exists; a person decides.</summary>
    NeedsConfirmation,
    /// <summary>The configured value and the evidence disagree; neither is overwritten.</summary>
    ReviewRequired,
    /// <summary>Not enough evidence or capability to determine the state.</summary>
    NotAssessed,
    /// <summary>Applicable, required, empty and not resolved by evidence.</summary>
    NotConfigured,
    /// <summary>The field does not apply to this integration's kind.</summary>
    NotApplicable,
    /// <summary>Resolution was attempted; no usable value or evidence exists.</summary>
    Unavailable,
    /// <summary>The concern exists but is handled outside this configuration, and evidence says so.</summary>
    PlatformManaged,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationFieldEvidenceOrigin { Source, Runtime, PlatformManaged }

/// <summary>A value evidence provides for one field (source, runtime, or a platform that manages the concern), with what it rests on.</summary>
public sealed record IntegrationFieldEvidence(IntegrationField Field, string Value, IntegrationFieldEvidenceOrigin Origin, string Basis);

/// <summary>One field of one integration: applicability/requirement, resolution and provenance are separate facts.</summary>
public sealed record IntegrationFieldStatus(IntegrationField Field, string Label, IntegrationFieldRequirement Requirement, IntegrationFieldResolution Resolution)
{
    public string? Value { get; init; }
    /// <summary>Where the value comes from ("Configured", "Template", "Source evidence", "User confirmed", …); null when there is none.</summary>
    public string? Provenance { get; init; }
    public string Reason { get; init; } = "";
    /// <summary>For <see cref="IntegrationFieldRequirement.Conditional"/>: when the field is required.</summary>
    public string? Condition { get; init; }
    /// <summary>The value evidence provides when it differs from the configured one (Review required), or when it is the only value.</summary>
    public string? EvidenceValue { get; init; }

    /// <summary>Required configuration that is missing: the only thing that makes an integration "Needs configuration".</summary>
    [JsonIgnore] public bool Missing => Requirement == IntegrationFieldRequirement.Required && Resolution == IntegrationFieldResolution.NotConfigured;
    /// <summary>A required field a person still has to decide (candidate, assumption, detected value or conflict).</summary>
    [JsonIgnore] public bool ToConfirm => Requirement == IntegrationFieldRequirement.Required
        && Resolution is IntegrationFieldResolution.NeedsConfirmation or IntegrationFieldResolution.ReviewRequired or IntegrationFieldResolution.DetectedFromSource or IntegrationFieldResolution.DetectedFromRuntime;
}

/// <summary>Configuration status of one integration and the field states behind it, grouped the way the status is explained.</summary>
public sealed record IntegrationConfigurationAssessment(IntegrationConfigurationState State, IReadOnlyList<IntegrationFieldStatus> Fields)
{
    public IntegrationFieldStatus? Field(IntegrationField field) => Fields.FirstOrDefault(f => f.Field == field);
    [JsonIgnore] public IReadOnlyList<IntegrationFieldStatus> Missing => [.. Fields.Where(f => f.Missing)];
    [JsonIgnore] public IReadOnlyList<IntegrationFieldStatus> ToConfirm => [.. Fields.Where(f => f.ToConfirm)];
    /// <summary>Conditional fields whose condition holds and that are not resolved: they limit the checks that need them, not completeness.</summary>
    [JsonIgnore] public IReadOnlyList<IntegrationFieldStatus> ConditionalGaps => [.. Fields.Where(f => f.Requirement == IntegrationFieldRequirement.Conditional
        && f.Resolution is IntegrationFieldResolution.NotConfigured or IntegrationFieldResolution.NeedsConfirmation)];
    [JsonIgnore] public IReadOnlyList<IntegrationFieldStatus> NotAssessed => [.. Fields.Where(f => f.Resolution == IntegrationFieldResolution.NotAssessed)];
    [JsonIgnore] public IReadOnlyList<IntegrationFieldStatus> NotApplicable => [.. Fields.Where(f => f.Requirement == IntegrationFieldRequirement.NotApplicable)];
}

/// <summary>
/// The one applicability/requirement/resolution calculation for integration fields, shared by the Integrations pane, the IQR backend and
/// the export. Kind-aware and generic: no product, pack or integration is named. Requirements are unchanged where a review uses the field
/// (messaging authentication, namespace, Event Hub consumer group for checkpoints); only fields a kind does not have are Not applicable.
/// </summary>
public static class IntegrationFieldRules
{
    public static readonly IReadOnlyList<IntegrationField> All = Enum.GetValues<IntegrationField>();

    private static bool Messaging(IntegrationKind kind) => kind is IntegrationKind.EventHub or IntegrationKind.ServiceBus;

    /// <summary>Applicability and requirement of a field for an integration kind, with the condition of a conditional field.</summary>
    public static (IntegrationFieldRequirement Requirement, string? Condition) Requirement(IntegrationKind kind, IntegrationField field) => field switch
    {
        IntegrationField.DisplayName or IntegrationField.ConfiguredChannel or IntegrationField.Producer or IntegrationField.Consumer or IntegrationField.ConsumerMapping
            => (IntegrationFieldRequirement.Required, null),
        IntegrationField.Namespace => Messaging(kind) ? (IntegrationFieldRequirement.Required, null) : (IntegrationFieldRequirement.NotApplicable, null),
        // A captured source resource (a CDC table, a queue's upstream entity) belongs to a data/messaging flow; an HTTP call has none.
        IntegrationField.SourceResource => kind is IntegrationKind.HttpApi or IntegrationKind.IdentityProvisioning ? (IntegrationFieldRequirement.NotApplicable, null) : (IntegrationFieldRequirement.Optional, null),
        // Consumer groups are an event-stream concept the review reads checkpoints for; other non-HTTP kinds may record one as metadata.
        IntegrationField.ConsumerGroup => kind switch
        {
            IntegrationKind.EventHub => (IntegrationFieldRequirement.Conditional, "Required for checkpoint and consumer-lag review"),
            IntegrationKind.HttpApi or IntegrationKind.IdentityProvisioning => (IntegrationFieldRequirement.NotApplicable, null),
            _ => (IntegrationFieldRequirement.Optional, null),
        },
        // Messaging and provisioning reviews read the mechanism (RBAC, SAS, token) — required as before. An HTTP/API boundary needs one only when the
        // endpoint requires callers to authenticate, which configuration alone does not show.
        IntegrationField.ProducerAuthentication or IntegrationField.ConsumerAuthentication => kind == IntegrationKind.HttpApi
            ? (IntegrationFieldRequirement.Conditional, "Required when the endpoint requires callers to authenticate")
            : (IntegrationFieldRequirement.Required, null),
        IntegrationField.HealthEndpoint => (IntegrationFieldRequirement.Optional, null),
        // A worker endpoint is a background consumer's (queue/topic/stream processor); a request/response API has no worker.
        IntegrationField.WorkerEndpoint => kind is IntegrationKind.HttpApi or IntegrationKind.IdentityProvisioning ? (IntegrationFieldRequirement.NotApplicable, null) : (IntegrationFieldRequirement.Optional, null),
        _ => (IntegrationFieldRequirement.Optional, null),
    };

    public static string FieldLabel(IntegrationKind kind, IntegrationField field) => field switch
    {
        IntegrationField.DisplayName => "Display name",
        IntegrationField.ConfiguredChannel => kind == IntegrationKind.HttpApi ? "Endpoint" : "Topic",
        IntegrationField.Namespace => "Namespace",
        IntegrationField.SourceResource => "Source resource",
        IntegrationField.ConsumerGroup => "Consumer group",
        IntegrationField.Producer => "Producer",
        IntegrationField.Consumer => "Consumer",
        IntegrationField.ConsumerMapping => "Consumer mapping",
        IntegrationField.ProducerAuthentication => "Producer authentication",
        IntegrationField.ConsumerAuthentication => "Consumer authentication",
        IntegrationField.HealthEndpoint => "Health endpoint",
        _ => "Worker endpoint",
    };

    public static string Label(IntegrationFieldResolution resolution) => resolution switch
    {
        IntegrationFieldResolution.Configured => "Configured",
        IntegrationFieldResolution.DetectedFromSource => "Detected from source",
        IntegrationFieldResolution.DetectedFromRuntime => "Detected from runtime",
        IntegrationFieldResolution.TemplateDefault => "Template default",
        IntegrationFieldResolution.NeedsConfirmation => "Needs confirmation",
        IntegrationFieldResolution.ReviewRequired => "Review required",
        IntegrationFieldResolution.NotAssessed => "Not assessed",
        IntegrationFieldResolution.NotConfigured => "Not configured",
        IntegrationFieldResolution.NotApplicable => "Not applicable",
        IntegrationFieldResolution.Unavailable => "Unavailable",
        _ => "Platform-managed",
    };

    public static string Label(IntegrationFieldRequirement requirement) => requirement switch
    {
        IntegrationFieldRequirement.Required => "Required",
        IntegrationFieldRequirement.Conditional => "Conditional",
        IntegrationFieldRequirement.Optional => "Optional",
        _ => "Not applicable",
    };

    /// <summary>
    /// Every field of <paramref name="definition"/>. <paramref name="evidence"/> holds values source/runtime evidence provides (never stored):
    /// an empty field it fills is Detected (to be used explicitly, never saved by being shown); a configured field it contradicts is Review required.
    /// </summary>
    public static IntegrationConfigurationAssessment Assess(IntegrationDefinition definition, IntegrationPlatform? platform, IReadOnlyList<IntegrationFieldEvidence>? evidence = null)
    {
        var fields = All.Select(field => Status(definition, platform, field, evidence?.FirstOrDefault(e => e.Field == field))).ToList();
        var state = !definition.Enabled ? IntegrationConfigurationState.Disabled
            : fields.Any(f => f.Missing) ? IntegrationConfigurationState.NeedsConfiguration
            : fields.Any(f => f.ToConfirm) ? IntegrationConfigurationState.NeedsConfirmation
            : IntegrationConfigurationState.Ready;
        return new(state, fields);
    }

    private static IntegrationFieldStatus Status(IntegrationDefinition definition, IntegrationPlatform? platform, IntegrationField field, IntegrationFieldEvidence? evidence)
    {
        var kind = definition.Kind;
        var (requirement, condition) = Requirement(kind, field);
        var label = FieldLabel(kind, field);
        var seededDefault = definition.Origin == IntegrationRecordOrigin.Seed && !definition.UserModified;
        var fromTemplate = definition.Origin == IntegrationRecordOrigin.Template;
        IntegrationFieldStatus Make(IntegrationFieldResolution resolution, string? value, string? provenance, string reason) =>
            new(field, label, requirement, resolution) { Value = value, Provenance = provenance, Reason = reason, Condition = condition };

        switch (field)
        {
            case IntegrationField.ConsumerMapping:
                return Mapping(definition, evidence, Make);
            case IntegrationField.Consumer when string.IsNullOrWhiteSpace(definition.Consumer.DisplayName):
                // The consumer is decided by a person (assign + confirm is one act); an empty consumer is something to confirm, never silently required text.
                return evidence is not null
                    ? Make(IntegrationFieldResolution.NeedsConfirmation, null, Provenance(evidence.Origin), $"Not assigned. Candidate from {Provenance(evidence.Origin).ToLowerInvariant()}: {evidence.Value} ({evidence.Basis}).") with { EvidenceValue = evidence.Value }
                    : Make(IntegrationFieldResolution.NeedsConfirmation, null, null, "No consumer is assigned. Assign and confirm the consumer.");
        }

        var (configured, auth) = Configured(definition, platform, field);
        if (requirement == IntegrationFieldRequirement.NotApplicable)
            return configured is null
                ? Make(IntegrationFieldResolution.NotApplicable, null, null, $"Does not apply to {IntegrationConfigurationRules.KindLabel(kind)} integrations.")
                : Make(IntegrationFieldResolution.Configured, configured, "Configured", $"Configured, but not used for {IntegrationConfigurationRules.KindLabel(kind)} integrations.");

        if (configured is not null)
        {
            if (evidence is not null && !SameValue(configured, evidence.Value))
                return Make(IntegrationFieldResolution.ReviewRequired, configured, "Configured",
                    $"Configured {configured}; {Provenance(evidence.Origin).ToLowerInvariant()} shows {evidence.Value} ({evidence.Basis}). Neither value is overwritten.") with { EvidenceValue = evidence.Value };
            var identity = field is IntegrationField.DisplayName or IntegrationField.ConfiguredChannel or IntegrationField.Producer or IntegrationField.Consumer;
            // A seeded record nobody changed carries defaults; a record a person saved from a template is configured, with the template as provenance.
            if (seededDefault && identity)
                return Make(IntegrationFieldResolution.TemplateDefault, configured, "Seeded default", "A seeded default nobody has changed. A default is not source evidence and is not verified.");
            return Make(IntegrationFieldResolution.Configured, configured, fromTemplate && identity ? "Template" : "Configured",
                fromTemplate && identity ? "Stored for this integration from its template. A template value is not source evidence." : "Stored for this integration.");
        }

        // Event Hub: the platform's expected consumer group is a configured assumption — checkpoints are read for it, a person confirms it.
        if (field == IntegrationField.ConsumerGroup && IntegrationConfigurationRules.EffectiveConsumerGroup(definition, platform) is ({ } group, true))
            return Make(IntegrationFieldResolution.NeedsConfirmation, group, "Platform expected consumer group (configured assumption)",
                "Assumed from the platform's expected consumer group. Checkpoints are read for it, but it needs confirmation and yields no verdict.");

        if (evidence is not null)
        {
            var resolution = evidence.Origin switch
            {
                IntegrationFieldEvidenceOrigin.Runtime => IntegrationFieldResolution.DetectedFromRuntime,
                IntegrationFieldEvidenceOrigin.PlatformManaged => IntegrationFieldResolution.PlatformManaged,
                _ => IntegrationFieldResolution.DetectedFromSource,
            };
            return Make(resolution, evidence.Value, Provenance(evidence.Origin),
                resolution == IntegrationFieldResolution.PlatformManaged ? $"Handled outside this configuration: {evidence.Basis}."
                    : $"{Provenance(evidence.Origin)}: {evidence.Basis}. Not stored — use the detected value explicitly to configure it.") with { EvidenceValue = evidence.Value };
        }

        return requirement switch
        {
            IntegrationFieldRequirement.Required => Make(IntegrationFieldResolution.NotConfigured, null, null, auth
                ? $"Required for {IntegrationConfigurationRules.KindLabel(kind)} integrations: the review reads the mechanism. Not configured and not evidenced."
                : "Required and not configured."),
            // A condition that holds for this kind (checkpoint review runs for event streams) makes the empty field genuinely needed by those checks.
            IntegrationFieldRequirement.Conditional when field == IntegrationField.ConsumerGroup =>
                Make(IntegrationFieldResolution.NotConfigured, null, null, $"{condition}. Only the checks that need it are affected; configuration is otherwise complete."),
            IntegrationFieldRequirement.Conditional => Make(IntegrationFieldResolution.NotAssessed, null, null,
                $"{condition}. No configuration or evidence shows whether it does, so it is not assessed and does not block configuration."),
            _ => Make(IntegrationFieldResolution.NotAssessed, null, null, "Optional. No value is configured or evidenced."),
        };
    }

    private static IntegrationFieldStatus Mapping(IntegrationDefinition definition, IntegrationFieldEvidence? evidence,
        Func<IntegrationFieldResolution, string?, string?, string, IntegrationFieldStatus> make)
    {
        var consumer = definition.Consumer;
        if (string.IsNullOrWhiteSpace(consumer.DisplayName))
            return make(IntegrationFieldResolution.NotAssessed, null, null, "No consumer is assigned, so there is no mapping to confirm yet.");
        // Source evidence naming another consumer is a conflict: the configured consumer is kept until a person decides.
        if (evidence is not null && !SameValue(consumer.DisplayName, evidence.Value))
            return make(IntegrationFieldResolution.ReviewRequired, consumer.DisplayName, MappingProvenance(consumer),
                $"Configured consumer {consumer.DisplayName}; source evidence points to {evidence.Value} ({evidence.Basis}). Nothing is overwritten.") with { EvidenceValue = evidence.Value };
        return consumer.MappingState switch
        {
            ConsumerMappingState.Confirmed => make(IntegrationFieldResolution.Configured, consumer.DisplayName, "User confirmed",
                $"Confirmed{(consumer.MappingConfirmedAt is { } at ? $" {at.ToUniversalTime():yyyy-MM-dd HH:mm} UTC" : "")}{(consumer.MappingSource is { } s ? $" · {s}" : "")}."),
            ConsumerMappingState.Suggested => make(IntegrationFieldResolution.NeedsConfirmation, consumer.DisplayName, MappingProvenance(consumer),
                $"Suggested{(consumer.MappingSource is { } basis ? $" by {basis}" : "")}; confirm it for this environment."),
            _ => make(IntegrationFieldResolution.NeedsConfirmation, consumer.DisplayName, MappingProvenance(consumer),
                evidence is not null ? $"Source evidence supports {evidence.Value} ({evidence.Basis}); a person confirms the mapping."
                    : "The configured consumer is not yet confirmed as the component that receives this integration."),
        };
    }

    private static string MappingProvenance(IntegrationConsumer consumer) => consumer.MappingSource is { Length: > 0 } source ? source : "Configured";

    /// <summary>The stored value of a field (an authentication mechanism other than NotConfigured counts, including None).</summary>
    private static (string? Value, bool Auth) Configured(IntegrationDefinition definition, IntegrationPlatform? platform, IntegrationField field)
    {
        static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        static string? Auth(IntegrationAuthMechanism mechanism) => mechanism == IntegrationAuthMechanism.NotConfigured ? null : IntegrationConfigurationRules.AuthLabel(mechanism);
        return field switch
        {
            IntegrationField.DisplayName => (Text(definition.DisplayName), false),
            IntegrationField.ConfiguredChannel => (Text(definition.EndpointOrTopic), false),
            IntegrationField.Namespace => (Text(platform?.Namespace), false),
            IntegrationField.SourceResource => (Text(definition.SourceResource), false),
            IntegrationField.ConsumerGroup => (Text(definition.ConsumerGroup), false),
            IntegrationField.Producer => (Text(definition.Producer), false),
            IntegrationField.Consumer => (Text(definition.Consumer.DisplayName), false),
            IntegrationField.ProducerAuthentication => (Auth(definition.ProducerAuthentication), true),
            IntegrationField.ConsumerAuthentication => (Auth(definition.ConsumerAuthentication), true),
            IntegrationField.HealthEndpoint => (Text(definition.HealthUrl), false),
            IntegrationField.WorkerEndpoint => (Text(definition.WorkerUrl), false),
            _ => (null, false),
        };
    }

    private static bool SameValue(string configured, string evidence) => string.Equals(configured.Trim(), evidence.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string Provenance(IntegrationFieldEvidenceOrigin origin) => origin switch
    {
        IntegrationFieldEvidenceOrigin.Runtime => "Runtime evidence",
        IntegrationFieldEvidenceOrigin.PlatformManaged => "Platform evidence",
        _ => "Source evidence",
    };
}
