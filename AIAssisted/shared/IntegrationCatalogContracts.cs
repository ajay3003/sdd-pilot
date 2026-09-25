using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Integration catalog: the CONFIGURED, expected integrations of one Target Environment (Target Environment → Integrations).
// It is the source of truth Integration Quality Review runs from. Configured is never observed: nothing here is runtime
// evidence, and nothing here carries a secret (no SAS key, connection string, token, client secret or password — only the
// NAME of an authentication mechanism). Terraform is not read, imported or referenced by BirkNext.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationKind { EventHub, ServiceBus, HttpApi, Database, File, Other }

/// <summary>How a party authenticates. The mechanism only — never the credential.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationAuthMechanism { NotConfigured, Sas, ManagedIdentity, EntraIdClientCredentials, ApiKey, None, Other }

/// <summary>How certain the consumer of an integration is. Suggested is not Confirmed: it comes from an audited source but has
/// not been confirmed for this environment.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsumerMappingState { NeedsConfirmation, Suggested, Confirmed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractRelationshipState { NotConfigured, ProducerContractAvailable, ConsumerContractAvailable, BothContractsAvailable, RelationshipVerified }

/// <summary>Configuration completeness only. Never runtime health and never IQR readiness; missing metadata is never "Failed".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationConfigurationState { Ready, NeedsConfirmation, NeedsConfiguration, Disabled }

/// <summary>Where a catalog record came from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationRecordOrigin { Seed, Manual, ImportedFromBrowserProfile }

/// <summary>What the consumer of a CDC topic expects for deletes. Tombstones are only judged against an explicit expectation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CdcDeleteExpectation { NotSpecified, DeleteEventOnly, DeleteEventAndTombstone, TombstoneNotExpected }

/// <summary>
/// Where IQR may read read-only runtime evidence for one platform. Non-secret identifiers only — credentials come from the BirkNext
/// instance's Azure identity (Managed Identity / workload identity), never from this record. Every field is optional; a missing one
/// makes its evidence "Not configured", never assumed.
/// </summary>
public sealed record IntegrationRuntimeEvidenceSettings
{
    /// <summary>Read Event Hub metadata (hub existence, partitions, last enqueued position) with the instance's Azure identity.</summary>
    public bool EventHubMetadata { get; init; }
    /// <summary>Azure subscription of the namespace — enables the read-only consumer-group list (Azure Resource Manager).</summary>
    public string? SubscriptionId { get; init; }
    /// <summary>Blob container of the consumers' EventProcessorClient checkpoint store, e.g. https://acct.blob.core.windows.net/checkpoints.</summary>
    public string? CheckpointContainerUrl { get; init; }
    /// <summary>Log Analytics workspace id (GUID) of the workspace-based Application Insights resource.</summary>
    public string? TelemetryWorkspaceId { get; init; }
    /// <summary>Telemetry/runtime review window in hours. Explicit and recorded in every result; 24 h when not set.</summary>
    public int? ReviewWindowHours { get; init; }
    /// <summary>Optional IQR thresholds. Null = measured values are reported as Observed, never judged.</summary>
    public long? MaxConsumerLagEvents { get; init; }
    public int? MaxCheckpointAgeMinutes { get; init; }
    public const int DefaultReviewWindowHours = 24;
    public const int MaxReviewWindowHours = 168;

    /// <summary>The first reason these settings cannot be stored, or null. Shared by the UI and the backend so a SAS URL, key or
    /// connection string can never be saved as an "identifier".</summary>
    public string? Validate()
    {
        if (SubscriptionId is { } subscription && !Guid.TryParse(subscription.Trim(), out _)) return "The Azure subscription id is a GUID.";
        if (TelemetryWorkspaceId is { } workspace && !Guid.TryParse(workspace.Trim(), out _)) return "The Log Analytics workspace id is a GUID.";
        if (CheckpointContainerUrl is { } container)
        {
            if (!Uri.TryCreate(container.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return "The checkpoint store must be an https blob container URL.";
            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
                return "The checkpoint store URL must not carry a query string or credentials — never a SAS URL. BirkNext reads it with its own Azure identity.";
            var path = uri.AbsolutePath.Trim('/');
            if (path.Length == 0 || path.Contains('/'))
                return "The checkpoint store URL names one container: https://account.blob.core.windows.net/container.";
        }
        if (ReviewWindowHours is { } window && (window < 1 || window > MaxReviewWindowHours)) return $"The review window is 1–{MaxReviewWindowHours} hours.";
        if (MaxConsumerLagEvents is < 0) return "The consumer lag threshold cannot be negative.";
        if (MaxCheckpointAgeMinutes is < 1) return "The checkpoint age threshold is at least 1 minute.";
        return null;
    }
}

public sealed record TechnicalTopic
{
    public string Name { get; init; } = "";
    /// <summary>What the topic is for (schema changes, schema history, Kafka Connect internals). Never a business integration.</summary>
    public string Purpose { get; init; } = "";
}

/// <summary>Shared messaging platform of an environment (one Event Hubs namespace), so its values are not repeated per topic.</summary>
public sealed record IntegrationPlatform
{
    /// <summary>Stable semantic id, e.g. <c>dev:eventhub:m2lb</c>.</summary>
    public string Id { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public string Name { get; init; } = "";
    public IntegrationKind Kind { get; init; } = IntegrationKind.EventHub;
    public bool Enabled { get; init; } = true;
    public string? Region { get; init; }
    public string? Namespace { get; init; }
    public string? NamespaceFqdn { get; init; }
    public string? ResourceGroup { get; init; }
    public string? TechnicalOwner { get; init; }
    public string? MonitoringProvider { get; init; }
    public string? MonitoringUrl { get; init; }
    public string? RunbookUrl { get; init; }
    public string? ProducerTechnology { get; init; }
    public IntegrationAuthMechanism ProducerAuthentication { get; init; }
    public IntegrationAuthMechanism DefaultConsumerAuthentication { get; init; }
    public string? SourceDatabase { get; init; }
    public string? SourceHost { get; init; }
    public int? SourcePort { get; init; }
    public string? TopicPrefix { get; init; }
    public int? DefaultPartitionCount { get; init; }
    public int? DefaultRetentionDays { get; init; }
    /// <summary>Platform-support topics (schema changes, schema history, Kafka Connect). Not business integrations and never reviewed as such.</summary>
    public List<TechnicalTopic> TechnicalTopics { get; init; } = [];
    /// <summary>Read-only runtime evidence sources of this platform. Null = none configured.</summary>
    public IntegrationRuntimeEvidenceSettings? RuntimeEvidence { get; init; }
    public IntegrationRecordOrigin Origin { get; init; } = IntegrationRecordOrigin.Manual;
    public bool UserModified { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record IntegrationConsumer
{
    public string? DisplayName { get; init; }
    public string? LogicalName { get; init; }
    public string? ContainerApp { get; init; }
    public string? ManagedIdentity { get; init; }
    public ConsumerMappingState MappingState { get; init; } = ConsumerMappingState.NeedsConfirmation;
    /// <summary>Where a Suggested/Confirmed mapping came from ("Audited M2LB source (QA-context audit)", "Confirmed by test lead").</summary>
    public string? MappingSource { get; init; }
    /// <summary>What the mapping rests on ("Confirmed in BirkNext Integrations"). Receiver rights alone never confirm a mapping.</summary>
    public string? MappingEvidence { get; init; }
    public DateTimeOffset? MappingConfirmedAt { get; init; }
}

/// <summary>One configured, expected integration (for Event Hubs: one business topic and its producer/consumer relationship).</summary>
public sealed record IntegrationDefinition
{
    /// <summary>Stable semantic id, e.g. <c>dev:eventhub:birk-cdc:dbo.Person</c>.</summary>
    public string Id { get; init; } = "";
    public string EnvironmentId { get; init; } = "";
    public string? PlatformId { get; init; }
    public string DisplayName { get; init; } = "";
    public IntegrationKind Kind { get; init; } = IntegrationKind.EventHub;
    public bool Enabled { get; init; } = true;
    /// <summary>Grouping for review and display, e.g. "BIRK CDC / Debezium".</summary>
    public string? SystemName { get; init; }
    public string? SourceSystem { get; init; }
    /// <summary>E.g. <c>BirkM2LB.dbo.Person</c>.</summary>
    public string? SourceResource { get; init; }
    public string? DestinationSystem { get; init; }
    /// <summary>Event Hub name / queue / topic / endpoint.</summary>
    public string? EndpointOrTopic { get; init; }
    /// <summary>Null means unknown / not configured — never silently <c>$Default</c>.</summary>
    public string? ConsumerGroup { get; init; }
    public string? Producer { get; init; }
    public IntegrationAuthMechanism ProducerAuthentication { get; init; }
    public IntegrationConsumer Consumer { get; init; } = new();
    public IntegrationAuthMechanism ConsumerAuthentication { get; init; }
    public ContractRelationshipState ContractRelationship { get; init; }
    public string? ProducerContractReference { get; init; }
    public string? ConsumerContractReference { get; init; }
    public string? HealthUrl { get; init; }
    public string? WorkerUrl { get; init; }
    public string? MonitoringUrl { get; init; }
    public string? RunbookUrl { get; init; }
    public string? TechnicalOwner { get; init; }
    public int? PartitionCount { get; init; }
    public int? RetentionDays { get; init; }
    /// <summary>CDC: what the consumer expects for deletes. NotSpecified = tombstone handling is not judged.</summary>
    public CdcDeleteExpectation DeleteExpectation { get; init; }
    public IntegrationRecordOrigin Origin { get; init; } = IntegrationRecordOrigin.Manual;
    public bool UserModified { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationContractRole { Producer, Consumer }

/// <summary>
/// A trusted event contract (JSON Schema) uploaded for one side of one integration. Metadata only; the schema text stays in the backend.
/// Replacing it never changes an earlier review, which keeps its own snapshot of what it compared.
/// </summary>
public sealed record IntegrationContractArtifact
{
    public string EnvironmentId { get; init; } = "";
    public string IntegrationId { get; init; } = "";
    public IntegrationContractRole Role { get; init; }
    public string FileName { get; init; } = "";
    public string Format { get; init; } = "JSON Schema";
    /// <summary>SHA-256 of the UTF-8 content, lowercase hex.</summary>
    public string ContentHash { get; init; } = "";
    /// <summary>The schema's own version/$id when it declares one.</summary>
    public string? Version { get; init; }
    public int FieldCount { get; init; }
    public DateTimeOffset ImportedAt { get; init; }
    [JsonIgnore] public string ShortHash => ContentHash.Length > 12 ? ContentHash[..12] : ContentHash;
}

public sealed record IntegrationContractUpload
{
    public string EnvironmentId { get; init; } = "";
    public string IntegrationId { get; init; } = "";
    public IntegrationContractRole Role { get; init; }
    public string FileName { get; init; } = "";
    public string Content { get; init; } = "";
}

/// <summary>The catalog of one environment, as the Integrations pane and IQR read it.</summary>
public sealed record IntegrationCatalog
{
    public string EnvironmentId { get; init; } = "";
    public List<IntegrationPlatform> Platforms { get; init; } = [];
    public List<IntegrationDefinition> Integrations { get; init; } = [];
    /// <summary>Set when this read attached the M2LB DEV seed or imported browser-stored integrations.</summary>
    public List<string> Notices { get; init; } = [];
}

/// <summary>Configuration completeness of one integration, with the fields behind it. Pure; shared by pane, API and review.</summary>
public static class IntegrationConfigurationRules
{
    public static (IntegrationConfigurationState State, List<string> Missing, List<string> Unconfirmed) Evaluate(IntegrationDefinition definition, IntegrationPlatform? platform)
    {
        var missing = new List<string>();
        var unconfirmed = new List<string>();
        if (!definition.Enabled) return (IntegrationConfigurationState.Disabled, missing, unconfirmed);
        if (string.IsNullOrWhiteSpace(definition.DisplayName)) missing.Add("Display name");
        if (string.IsNullOrWhiteSpace(definition.EndpointOrTopic)) missing.Add(definition.Kind == IntegrationKind.HttpApi ? "Endpoint" : "Topic");
        if (definition.Kind is IntegrationKind.EventHub or IntegrationKind.ServiceBus && string.IsNullOrWhiteSpace(platform?.Namespace)) missing.Add("Namespace");
        if (string.IsNullOrWhiteSpace(definition.Producer)) missing.Add("Producer");
        if (definition.ProducerAuthentication == IntegrationAuthMechanism.NotConfigured) missing.Add("Producer authentication");
        if (string.IsNullOrWhiteSpace(definition.Consumer.DisplayName)) unconfirmed.Add("Consumer");
        else if (definition.Consumer.MappingState != ConsumerMappingState.Confirmed) unconfirmed.Add("Consumer mapping");
        if (definition.ConsumerAuthentication == IntegrationAuthMechanism.NotConfigured) missing.Add("Consumer authentication");
        var state = missing.Count > 0 ? IntegrationConfigurationState.NeedsConfiguration
            : unconfirmed.Count > 0 ? IntegrationConfigurationState.NeedsConfirmation
            : IntegrationConfigurationState.Ready;
        return (state, missing, unconfirmed);
    }

    public static string Label(IntegrationConfigurationState state) => state switch
    {
        IntegrationConfigurationState.Ready => "Ready",
        IntegrationConfigurationState.NeedsConfirmation => "Needs confirmation",
        IntegrationConfigurationState.NeedsConfiguration => "Needs configuration",
        _ => "Disabled",
    };

    public static string AuthLabel(IntegrationAuthMechanism mechanism) => mechanism switch
    {
        IntegrationAuthMechanism.Sas => "SAS",
        IntegrationAuthMechanism.ManagedIdentity => "Managed Identity",
        IntegrationAuthMechanism.EntraIdClientCredentials => "Entra ID (client credentials)",
        IntegrationAuthMechanism.ApiKey => "API key",
        IntegrationAuthMechanism.None => "None",
        IntegrationAuthMechanism.Other => "Other",
        _ => "Not configured",
    };

    public static string MappingLabel(ConsumerMappingState state) => state switch
    {
        ConsumerMappingState.Confirmed => "Confirmed",
        ConsumerMappingState.Suggested => "Suggested — confirm for this environment",
        _ => "Needs confirmation",
    };

    public static string ContractLabel(ContractRelationshipState state) => state switch
    {
        ContractRelationshipState.NotConfigured => "Not configured",
        ContractRelationshipState.ProducerContractAvailable => "Producer contract referenced",
        ContractRelationshipState.ConsumerContractAvailable => "Consumer contract referenced",
        ContractRelationshipState.BothContractsAvailable => "Producer and consumer contracts referenced",
        _ => "Relationship verified",
    };
    public static string KindLabel(IntegrationKind kind) => kind switch
    {
        IntegrationKind.EventHub => "Event Hub",
        IntegrationKind.ServiceBus => "Service Bus",
        IntegrationKind.HttpApi => "HTTP/API",
        _ => kind.ToString(),
    };
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Integration Quality Review v2: capability-specific readiness, typed checks, findings, manual follow-up and a snapshot of
// the configuration each run used. Configured expectation ≠ runtime evidence ≠ contract evidence ≠ review result.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationReviewDomain { Configuration, Connectivity, Contract, MessageFlow, Reliability, ErrorHandling, Security, Observability, Performance, DataQuality }

/// <summary>Before a run: whether a domain can produce evidence. Not a result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationDomainReadiness { Ready, Available, Limited, NotAssessable }

/// <summary>Result of one check. Fail only when an explicit expected rule was violated; unavailable evidence is never Fail.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationCheckStatus { Pass, Warning, Fail, NotAssessed, Unavailable, NoIndicatorsObserved, Observed, NeedsConfirmation, NotConfigured, NoRecentEvidence }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationEvidenceSource { Configuration, NetworkProbe, AzureMetadata, ApplicationInsights, HealthEndpoint, LogEvidence, ContractArtifact, EndpointDiscovery, CheckpointStore, AzureResourceManager }

/// <summary>Why a runtime evidence source did or did not deliver. Failures are never collapsed into one "unavailable".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationEvidenceState { Available, Unavailable, NotConfigured, NotAuthorized, NotSupported, NotFound, Stale, Error }

/// <summary>How current one piece of evidence is, relative to the review window.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationEvidenceItemFreshness { Current, Recent, Historical, Stale, Unknown }

/// <summary>One runtime evidence adapter's outcome for one platform in one run (shown pre-run as readiness and post-run as provenance).</summary>
public sealed record IntegrationEvidenceAdapterStatus
{
    public string Adapter { get; init; } = "";
    public IntegrationEvidenceSource Source { get; init; }
    public IntegrationEvidenceState State { get; init; }
    public string Reason { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationCheckScope { Platform, Topic }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationReviewOutcome { Completed, CompletedWithLimitations, ManualReviewRequired, NothingAssessed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationEvidenceFreshness { Current, Mixed, Historical, ConfigurationOnly }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationFindingSeverityV2 { Critical, High, Medium, Low, Info }

public sealed record IntegrationDomainReadinessRow(IntegrationReviewDomain Domain, IntegrationDomainReadiness Readiness, string Explanation);

public sealed record IntegrationSystemScope
{
    public string SystemName { get; init; } = "";
    public string? PlatformId { get; init; }
    public string? PlatformName { get; init; }
    public IntegrationKind Kind { get; init; }
    public int Topics { get; init; }
    public int Enabled { get; init; }
    public int ConsumersConfirmed { get; init; }
    public int ConsumersSuggested { get; init; }
    public int ConsumersNeedingConfirmation { get; init; }
    public int ConsumerGroupsUnknown { get; init; }
    public int ContractsConfigured { get; init; }
    /// <summary>False when the domain review for this kind is not implemented; its topics are listed, never faked as reviewed.</summary>
    public bool DomainReviewSupported { get; init; }
}

/// <summary>Pre-run: what is configured, what can be reviewed, what cannot. Never a result.</summary>
public sealed record IntegrationReviewReadiness
{
    public string EnvironmentId { get; init; } = "";
    public List<IntegrationSystemScope> Systems { get; init; } = [];
    public int ConfiguredIntegrations { get; init; }
    public int EnabledIntegrations { get; init; }
    public List<IntegrationDomainReadinessRow> Domains { get; init; } = [];
    public bool CanRun { get; init; }
    /// <summary>"Ready", "Can run with limitations" or "Cannot run".</summary>
    public string Headline { get; init; } = "";
    public List<string> Reasons { get; init; } = [];
    /// <summary>Configured state of each runtime evidence adapter per platform (configuration only; nothing is contacted pre-run).</summary>
    public List<IntegrationEvidenceAdapterStatus> EvidenceAdapters { get; init; } = [];
}

public sealed record IntegrationCheck
{
    public string CheckId { get; init; } = "";
    public IntegrationReviewDomain Domain { get; init; }
    public IntegrationCheckScope Scope { get; init; }
    /// <summary>Platform id (platform scope) or integration id (topic scope).</summary>
    public string SubjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public IntegrationCheckStatus Status { get; init; }
    public string Expectation { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string Explanation { get; init; } = "";
    public string? Recommendation { get; init; }
    public IntegrationEvidenceSource Provenance { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    /// <summary>When the underlying fact happened (last enqueued event, checkpoint update, last telemetry row), if known.</summary>
    public DateTimeOffset? SourceTimestamp { get; init; }
    public IntegrationEvidenceItemFreshness Freshness { get; init; } = IntegrationEvidenceItemFreshness.Unknown;
}

public sealed record IntegrationReviewFinding
{
    /// <summary>Stable grouping key: rule + subject. A platform problem is one finding with its affected topics, never one per topic.</summary>
    public string Key { get; init; } = "";
    public string RuleId { get; init; } = "";
    public IntegrationReviewDomain Domain { get; init; }
    public IntegrationFindingSeverityV2 Severity { get; init; }
    public string Title { get; init; } = "";
    public string Subject { get; init; } = "";
    public List<string> Evidence { get; init; } = [];
    public string Recommendation { get; init; } = "";
    public List<string> AffectedIntegrations { get; init; } = [];
}

/// <summary>A task or gap for a person — not a defect.</summary>
public sealed record IntegrationManualFollowUp(string Title, string Detail, int AffectedCount);

public sealed record IntegrationDomainResult
{
    public IntegrationReviewDomain Domain { get; init; }
    /// <summary>Checks with an assessed status (not NotAssessed/Unavailable/NotConfigured).</summary>
    public int ChecksAssessed { get; init; }
    public int ChecksTotal { get; init; }
    public int Findings { get; init; }
    /// <summary>"Assessed", "Partially assessed", "Not assessed".</summary>
    public string StateLabel { get; init; } = "";
    public string? KeyLimitation { get; init; }
}

public sealed record IntegrationTopicResult
{
    public string IntegrationId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? Topic { get; init; }
    public string? Consumer { get; init; }
    public ConsumerMappingState MappingState { get; init; }
    public IntegrationConfigurationState ConfigurationState { get; init; }
    public List<IntegrationCheck> Checks { get; init; } = [];
    /// <summary>A consumer group discovered read-only (e.g. the only non-default group Azure lists for the hub). A suggestion, never saved.</summary>
    public string? SuggestedConsumerGroup { get; init; }
    public string? SuggestedConsumerGroupSource { get; init; }
}

public sealed record IntegrationSystemResult
{
    public string SystemName { get; init; } = "";
    public string? PlatformId { get; init; }
    public IntegrationKind Kind { get; init; }
    public bool DomainReviewSupported { get; init; }
    public List<IntegrationCheck> PlatformChecks { get; init; } = [];
    public List<IntegrationTopicResult> Topics { get; init; } = [];
}

public sealed record IntegrationReviewResult
{
    public Guid RunId { get; init; }
    public string EnvironmentId { get; init; } = "";
    public string EnvironmentName { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public IntegrationReviewOutcome Outcome { get; init; }
    /// <summary>The configuration this run used, as it was. A later edit never re-renders this result.</summary>
    public IntegrationCatalog ConfigurationSnapshot { get; init; } = new();
    public List<IntegrationSystemResult> Systems { get; init; } = [];
    public List<IntegrationDomainResult> Domains { get; init; } = [];
    public List<IntegrationReviewFinding> Findings { get; init; } = [];
    public List<IntegrationManualFollowUp> ManualFollowUp { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public IntegrationEvidenceFreshness Freshness { get; init; }
    public List<IntegrationEvidenceSource> EvidenceSources { get; init; } = [];
    /// <summary>The runtime/telemetry window this run used (hours). Null in results recorded before the window was explicit.</summary>
    public int? ReviewWindowHours { get; init; }
    /// <summary>What every runtime evidence adapter returned in this run, per platform.</summary>
    public List<IntegrationEvidenceAdapterStatus> EvidenceAdapters { get; init; } = [];
    /// <summary>Contract artifacts compared in this run (file, hash, version) — a later replacement never reinterprets this result.</summary>
    public List<IntegrationContractArtifact> ContractSnapshot { get; init; } = [];
    [JsonIgnore] public int TopicsReviewed => Systems.Where(s => s.DomainReviewSupported).Sum(s => s.Topics.Count);
    [JsonIgnore] public IEnumerable<IntegrationCheck> AllChecks => Systems.SelectMany(s => s.PlatformChecks.Concat(s.Topics.SelectMany(t => t.Checks)));
}

public sealed record IntegrationReviewRunRequest
{
    public string EnvironmentId { get; init; } = "";
    public string EnvironmentName { get; init; } = "";
}

public sealed record IntegrationReviewRunSummary(Guid RunId, DateTimeOffset CompletedAt, IntegrationReviewOutcome Outcome, int Topics, int Findings);

public static class IntegrationReviewLabels
{
    public static string Domain(IntegrationReviewDomain domain) => domain switch
    {
        IntegrationReviewDomain.MessageFlow => "Message flow",
        IntegrationReviewDomain.ErrorHandling => "Error handling",
        IntegrationReviewDomain.DataQuality => "Data quality",
        IntegrationReviewDomain.Contract => "Contracts",
        _ => domain.ToString(),
    };

    public static string Status(IntegrationCheckStatus status) => status switch
    {
        IntegrationCheckStatus.NotAssessed => "Not assessed",
        IntegrationCheckStatus.NoIndicatorsObserved => "No indicators observed",
        IntegrationCheckStatus.NeedsConfirmation => "Needs confirmation",
        IntegrationCheckStatus.NotConfigured => "Not configured",
        IntegrationCheckStatus.NoRecentEvidence => "No recent evidence",
        _ => status.ToString(),
    };

    public static string Readiness(IntegrationDomainReadiness readiness) => readiness switch
    {
        IntegrationDomainReadiness.NotAssessable => "Not assessable",
        _ => readiness.ToString(),
    };

    public static string Outcome(IntegrationReviewOutcome outcome) => outcome switch
    {
        IntegrationReviewOutcome.CompletedWithLimitations => "Completed with limitations",
        IntegrationReviewOutcome.ManualReviewRequired => "Manual review required",
        IntegrationReviewOutcome.NothingAssessed => "Nothing assessed",
        _ => "Completed",
    };

    public static string Source(IntegrationEvidenceSource source) => source switch
    {
        IntegrationEvidenceSource.NetworkProbe => "Network probe",
        IntegrationEvidenceSource.AzureMetadata => "Azure metadata",
        IntegrationEvidenceSource.ApplicationInsights => "Application Insights",
        IntegrationEvidenceSource.HealthEndpoint => "Health endpoint",
        IntegrationEvidenceSource.LogEvidence => "Log evidence",
        IntegrationEvidenceSource.ContractArtifact => "Contract artifact",
        IntegrationEvidenceSource.EndpointDiscovery => "Endpoint Discovery",
        IntegrationEvidenceSource.CheckpointStore => "Checkpoint store",
        IntegrationEvidenceSource.AzureResourceManager => "Azure Resource Manager",
        _ => "Configuration",
    };

    public static string EvidenceState(IntegrationEvidenceState state) => state switch
    {
        IntegrationEvidenceState.NotConfigured => "Not configured",
        IntegrationEvidenceState.NotAuthorized => "Not authorized",
        IntegrationEvidenceState.NotSupported => "Not supported",
        IntegrationEvidenceState.NotFound => "Not found",
        _ => state.ToString(),
    };

    public static string ItemFreshness(IntegrationEvidenceItemFreshness freshness) => freshness switch
    {
        IntegrationEvidenceItemFreshness.Current => "Current (last hour)",
        IntegrationEvidenceItemFreshness.Recent => "Recent (in review window)",
        IntegrationEvidenceItemFreshness.Historical => "Older than review window",
        IntegrationEvidenceItemFreshness.Stale => "Stale",
        _ => "Unknown",
    };

    /// <summary>
    /// One freshness rule for every runtime fact: within the last hour = Current, within the review window = Recent, older = Historical.
    /// No timestamp = Unknown — old telemetry never appears live.
    /// </summary>
    public static IntegrationEvidenceItemFreshness FreshnessOf(DateTimeOffset? sourceTimestamp, DateTimeOffset capturedAt, int windowHours) =>
        sourceTimestamp is not { } at ? IntegrationEvidenceItemFreshness.Unknown
        : capturedAt - at <= TimeSpan.FromHours(1) ? IntegrationEvidenceItemFreshness.Current
        : capturedAt - at <= TimeSpan.FromHours(windowHours) ? IntegrationEvidenceItemFreshness.Recent
        : IntegrationEvidenceItemFreshness.Historical;

    /// <summary>Assessed means the check produced a statement about the subject, not that evidence was missing.</summary>
    public static bool IsAssessed(IntegrationCheckStatus status) =>
        status is not (IntegrationCheckStatus.NotAssessed or IntegrationCheckStatus.Unavailable or IntegrationCheckStatus.NotConfigured);
}
