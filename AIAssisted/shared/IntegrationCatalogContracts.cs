using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Integration catalog: the CONFIGURED, expected integrations of one Target Environment (Target Environment → Integrations).
// It is the source of truth Integration Quality Review runs from. Configured is never observed: nothing here is runtime
// evidence, and nothing here carries a secret (no SAS key, connection string, token, client secret or password — only the
// NAME of an authentication mechanism). Terraform is not read, imported or referenced by BirkNext.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
/// <remarks>IdentityProvisioning = inbound user provisioning (SCIM from Microsoft Entra ID) — its own flow, never Event Hub CDC.</remarks>
public enum IntegrationKind { EventHub, ServiceBus, HttpApi, Database, File, Other, IdentityProvisioning }

/// <summary>How a party authenticates. The mechanism only — never the credential.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationAuthMechanism { NotConfigured, Sas, ManagedIdentity, EntraIdClientCredentials, ApiKey, None, Other }

/// <summary>How certain the consumer of an integration is. Suggested is not Confirmed: it comes from an audited source but has
/// not been confirmed for this environment.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsumerMappingState { NeedsConfirmation, Suggested, Confirmed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationMappingEvidenceState { StrongEvidence, PartialEvidence, NoSupportingEvidence, NotTestable, Error }

/// <summary>A bounded evidence observation, never a configuration change or a mapping confirmation.</summary>
public sealed record IntegrationMappingEvidenceItem
{
    public string CheckId { get; init; } = "";
    public string Label { get; init; } = "";
    public IntegrationEvidenceState State { get; init; }
    public string Summary { get; init; } = "";
    public IntegrationEvidenceSource Provenance { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public IntegrationEvidenceItemFreshness Freshness { get; init; }
    public string? MissingReason { get; init; }
    public bool SupportsMapping { get; init; }
    /// <summary>True only for evidence that ties this exact topic to this exact consumer (subscription, attributed checkpoint or topic-scoped
    /// processing telemetry). The only kind of item that can make a check Strong evidence — and even then it never confirms the mapping.</summary>
    public bool DirectRelationship { get; init; }
}

/// <summary>Session snapshot of a read-only mapping evidence check. Existing providers do not establish an exact topic-to-application
/// relationship, so runtime reads cannot currently produce StrongEvidence. Evidence never changes Suggested → Confirmed.</summary>
public sealed record IntegrationMappingEvidenceCheck
{
    public string IntegrationId { get; init; } = "";
    public string? Topic { get; init; }
    public string? SuggestedConsumer { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public IntegrationMappingEvidenceState OverallState { get; init; }
    public List<IntegrationMappingEvidenceItem> Checks { get; init; } = [];
    public List<string> ManualFollowUp { get; init; } = [];
    /// <summary>The consumer group checkpoints were looked up for (the integration's own, or the platform's expectation) and how it is known.</summary>
    public string? ExpectedConsumerGroup { get; init; }
    public IntegrationValueProvenance ExpectedConsumerGroupProvenance { get; init; }
    /// <summary>Consumer groups Azure Resource Manager listed for the hub; null when they were not read.</summary>
    public List<string>? ObservedConsumerGroups { get; init; }
    /// <summary>The stored consumer mapping state — never changed by this check.</summary>
    public ConsumerMappingState MappingState { get; init; }
    public static string Label(IntegrationMappingEvidenceState state) => state switch
    {
        IntegrationMappingEvidenceState.StrongEvidence => "Strong evidence",
        IntegrationMappingEvidenceState.PartialEvidence => "Partial evidence",
        IntegrationMappingEvidenceState.NoSupportingEvidence => "No supporting evidence",
        IntegrationMappingEvidenceState.NotTestable => "Not testable",
        _ => "Error",
    };
}

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

/// <summary>How a configured runtime-evidence value is known. A configured assumption is never a confirmed mapping.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationValueProvenance { NotSpecified, ConfiguredAssumption, SourceConfigurationVerified, ObservedInAzure, ConfirmedByPerson, ConfiguredOnIntegration, DeclaredInSource }

/// <summary>
/// Where IQR may read read-only runtime evidence for one platform. Non-secret identifiers only — credentials come from the BirkNext
/// instance's Azure identity (Managed Identity / workload identity), never from this record. Every field is optional; a missing one
/// makes its evidence "Not configured", never assumed. Configured sources are not Azure execution: whether the instance may call Azure
/// at all is <c>IntegrationReview:Azure:Enabled</c> (see <see cref="IntegrationCatalog.AzureRuntimeEnabled"/>).
/// </summary>
public sealed record IntegrationRuntimeEvidenceSettings
{
    /// <summary>Read Event Hub metadata (hub existence, partitions, last enqueued position) with the instance's Azure identity.</summary>
    public bool EventHubMetadata { get; init; }
    /// <summary>Azure subscription of the namespace — enables the read-only consumer-group list (Azure Resource Manager).</summary>
    public string? SubscriptionId { get; init; }
    /// <summary>Display name of the subscription (reference only; the id is what is used).</summary>
    public string? SubscriptionName { get; init; }
    /// <summary>Namespace SKU and capacity as verified in Azure (reference only — never a pass criterion).</summary>
    public string? NamespaceSku { get; init; }
    public int? NamespaceCapacity { get; init; }

    /// <summary>Platform-level expected consumer group, used when an integration names none. Its provenance is kept: a configured
    /// assumption (e.g. <c>$Default</c>) lets checkpoint lookups run but never confirms a mapping or produces a Pass.</summary>
    public string? ExpectedConsumerGroup { get; init; }
    public IntegrationValueProvenance ExpectedConsumerGroupProvenance { get; init; }
    /// <summary>Where the expectation comes from, e.g. the hub it was observed on (reference text, no secret).</summary>
    public string? ExpectedConsumerGroupNote { get; init; }

    /// <summary>Blob container of the consumers' EventProcessorClient checkpoint store, e.g. https://acct.blob.core.windows.net/checkpoints.
    /// Kept for settings saved before the endpoint/container split; <see cref="CheckpointBlobEndpoint"/> + <see cref="CheckpointContainerName"/> win.</summary>
    public string? CheckpointContainerUrl { get; init; }
    /// <summary>Blob service endpoint of the checkpoint store account, e.g. https://acct.blob.core.windows.net/ (no query, no key).</summary>
    public string? CheckpointBlobEndpoint { get; init; }
    public string? CheckpointContainerName { get; init; }
    public IntegrationValueProvenance CheckpointProvenance { get; init; }
    /// <summary>Where the checkpoint location was verified, e.g. consumer environment-variable NAMES (never their values when secret).</summary>
    public string? CheckpointSourceNote { get; init; }

    /// <summary>Log Analytics workspace id (GUID). Optional and not required: Application Insights can be queried by resource.</summary>
    public string? TelemetryWorkspaceId { get; init; }
    /// <summary>Application Insights component queried by resource id (subscription + resource group + name). No connection string.</summary>
    public string? ApplicationInsightsResourceName { get; init; }
    public string? ApplicationInsightsResourceGroup { get; init; }
    /// <summary>True when the consumer is known to be configured with Application Insights (the setting exists). The value is never stored.</summary>
    public bool? ApplicationInsightsConfigured { get; init; }
    /// <summary>Container Apps environment log destination (e.g. <c>azure-monitor</c>); reference for why no Log Analytics workspace exists.</summary>
    public string? ContainerAppsLogDestination { get; init; }
    /// <summary>The consumer application this platform's runtime evidence was verified against (reference only).</summary>
    public string? ConsumerApplicationName { get; init; }
    public string? ConsumerApplicationResourceGroup { get; init; }

    /// <summary>Telemetry/runtime review window in hours. Explicit and recorded in every result; 24 h when not set.</summary>
    public int? ReviewWindowHours { get; init; }
    /// <summary>Optional IQR thresholds. Null = measured values are reported as Observed, never judged.</summary>
    public long? MaxConsumerLagEvents { get; init; }
    public int? MaxCheckpointAgeMinutes { get; init; }
    public const int DefaultReviewWindowHours = 24;
    public const int MaxReviewWindowHours = 168;
    public const string DefaultConsumerGroupName = "$Default";

    /// <summary>The checkpoint container the review lists: endpoint + container when both are set, otherwise the legacy container URL.</summary>
    public string? ResolvedCheckpointContainerUrl() =>
        !string.IsNullOrWhiteSpace(CheckpointBlobEndpoint) && !string.IsNullOrWhiteSpace(CheckpointContainerName)
            ? CheckpointBlobEndpoint.Trim().TrimEnd('/') + "/" + CheckpointContainerName.Trim()
            : string.IsNullOrWhiteSpace(CheckpointContainerUrl) ? null : CheckpointContainerUrl.Trim().TrimEnd('/');

    /// <summary>ARM id of the Application Insights component, when subscription, resource group and name are all known.</summary>
    public string? ApplicationInsightsResourceId() =>
        Guid.TryParse(SubscriptionId?.Trim(), out var subscription) && !string.IsNullOrWhiteSpace(ApplicationInsightsResourceGroup) && !string.IsNullOrWhiteSpace(ApplicationInsightsResourceName)
            ? $"/subscriptions/{subscription:D}/resourceGroups/{ApplicationInsightsResourceGroup.Trim()}/providers/Microsoft.Insights/components/{ApplicationInsightsResourceName.Trim()}"
            : null;

    /// <summary>Whether a telemetry source is configured: a Log Analytics workspace, or an Application Insights resource. Neither is required of the other.</summary>
    public bool TelemetryConfigured => Guid.TryParse(TelemetryWorkspaceId?.Trim(), out _) || ApplicationInsightsResourceId() is not null;

    public static string ProvenanceLabel(IntegrationValueProvenance provenance) => provenance switch
    {
        IntegrationValueProvenance.ConfiguredAssumption => "Configured assumption",
        // Taken from audited source/IaC configuration: declared, not deployment-verified (Declared ≠ Verified).
        IntegrationValueProvenance.SourceConfigurationVerified => "From source configuration (audited, not deployment-verified)",
        IntegrationValueProvenance.DeclaredInSource => "Declared in source (Source Analysis) — selected by a person",
        IntegrationValueProvenance.ObservedInAzure => "Observed in Azure",
        IntegrationValueProvenance.ConfirmedByPerson => "Confirmed",
        IntegrationValueProvenance.ConfiguredOnIntegration => "Configured on the integration",
        _ => "Not specified",
    };

    private static readonly System.Text.RegularExpressions.Regex Name = new(@"^[A-Za-z0-9][A-Za-z0-9._()-]{0,89}$");
    private static readonly System.Text.RegularExpressions.Regex DisplayName = new(@"^[A-Za-z0-9][A-Za-z0-9._() -]{0,89}$");
    private static readonly System.Text.RegularExpressions.Regex ContainerName = new(@"^(?!.*--)[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$");
    private static readonly System.Text.RegularExpressions.Regex ConsumerGroup = new(@"^(\$Default|[A-Za-z0-9][A-Za-z0-9._-]{0,49})$");
    private static readonly System.Text.RegularExpressions.Regex SecretLike = new(
        @"(accountkey|sharedaccesskey|sharedaccesssignature|instrumentationkey|connectionstring|endpoint\s*=\s*sb://|password\s*=|(^|[?&;])(sig|sv|se|sp|skoid|code|token|access_token|api-key|apikey)=|\bbearer\s|eyJ[A-Za-z0-9_-]{8,}\.)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>True when a value looks like a credential (key, SAS, connection string, token) rather than an identifier.</summary>
    public static bool LooksLikeSecret(string? value) => value is { Length: > 0 } && SecretLike.IsMatch(value);

    private static string? HttpsWithoutCredentials(string value, string what)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return $"The {what} must be an https URL.";
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            return $"The {what} must not carry a query string, token or credentials — never a SAS URL. BirkNext reads it with its own Azure identity.";
        return null;
    }

    /// <summary>The first reason these settings cannot be stored, or null. Shared by the UI and the backend so a SAS URL, key, token or
    /// connection string can never be saved as an "identifier".</summary>
    public string? Validate()
    {
        if (SubscriptionId is { } subscription && !Guid.TryParse(subscription.Trim(), out _)) return "The Azure subscription id is a GUID.";
        if (SubscriptionName is { Length: > 0 } subscriptionName && !DisplayName.IsMatch(subscriptionName.Trim())) return "The subscription name is a plain Azure name.";
        if (NamespaceCapacity is < 1) return "The namespace capacity is at least 1.";
        if (ExpectedConsumerGroup is { Length: > 0 } group && !ConsumerGroup.IsMatch(group.Trim())) return "The expected consumer group is $Default or an Event Hub consumer-group name (letters, digits, . _ -; up to 50).";
        if (TelemetryWorkspaceId is { } workspace && !Guid.TryParse(workspace.Trim(), out _)) return "The Log Analytics workspace id is a GUID.";
        if (ApplicationInsightsResourceName is { Length: > 0 } ai && !Name.IsMatch(ai.Trim())) return "The Application Insights resource name is a plain Azure resource name.";
        if (ApplicationInsightsResourceGroup is { Length: > 0 } aiGroup && !Name.IsMatch(aiGroup.Trim())) return "The Application Insights resource group is a plain Azure resource-group name.";
        if (ConsumerApplicationName is { Length: > 0 } app && !Name.IsMatch(app.Trim())) return "The consumer application is a plain Azure resource name.";
        if (ConsumerApplicationResourceGroup is { Length: > 0 } appGroup && !Name.IsMatch(appGroup.Trim())) return "The consumer application resource group is a plain Azure resource-group name.";
        if (ContainerAppsLogDestination is { Length: > 0 } destination && !Name.IsMatch(destination.Trim())) return "The Container Apps log destination is a plain value such as azure-monitor.";
        if (CheckpointBlobEndpoint is { Length: > 0 } endpoint)
        {
            if (HttpsWithoutCredentials(endpoint, "checkpoint Blob endpoint") is { } bad) return bad;
            if (new Uri(endpoint.Trim()).AbsolutePath.Trim('/').Length > 0) return "The checkpoint Blob endpoint is the account endpoint only (https://account.blob.core.windows.net/); the container is a separate field.";
        }
        if (CheckpointContainerName is { Length: > 0 } containerName && !ContainerName.IsMatch(containerName.Trim()))
            return "The checkpoint container name is 3–63 lower-case letters, digits and single hyphens.";
        if (string.IsNullOrWhiteSpace(CheckpointBlobEndpoint) != string.IsNullOrWhiteSpace(CheckpointContainerName) && string.IsNullOrWhiteSpace(CheckpointContainerUrl))
            return "The checkpoint store needs both the Blob endpoint and the container name.";
        if (CheckpointContainerUrl is { Length: > 0 } container)
        {
            if (HttpsWithoutCredentials(container, "checkpoint store") is { } bad) return bad;
            var path = new Uri(container.Trim()).AbsolutePath.Trim('/');
            if (path.Length == 0 || path.Contains('/'))
                return "The checkpoint store URL names one container: https://account.blob.core.windows.net/container.";
        }
        // Last: free-text and any field the specific rules above let through (notes, names) must not carry a credential either.
        foreach (var value in new[] { SubscriptionId, SubscriptionName, NamespaceSku, ExpectedConsumerGroup, ExpectedConsumerGroupNote, CheckpointContainerUrl, CheckpointBlobEndpoint,
                     CheckpointContainerName, CheckpointSourceNote, TelemetryWorkspaceId, ApplicationInsightsResourceName, ApplicationInsightsResourceGroup, ContainerAppsLogDestination,
                     ConsumerApplicationName, ConsumerApplicationResourceGroup })
            if (LooksLikeSecret(value)) return "Runtime evidence settings hold identifiers only — this looks like a key, SAS token, connection string or access token and was not stored.";
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
    /// <summary>Service Bus platforms only: the configured, expected topology (queues, topics, subscriptions and their properties).</summary>
    public ServiceBusTopology? ServiceBusTopology { get; init; }
    /// <summary>Identity provisioning platforms only: the configured SCIM provisioning integration (never a secret or token).</summary>
    public ScimProvisioningSettings? ScimProvisioning { get; init; }
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
    /// <summary>The source snapshot whose discovery evidence the person confirmed against (null when confirmed without source evidence).</summary>
    public Guid? MappingSourceSnapshotId { get; init; }
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
    /// <summary>Set when this read upgraded an explicitly applied template or imported browser-stored integrations.</summary>
    public List<string> Notices { get; init; } = [];
    /// <summary>The project template explicitly applied to this environment (e.g. the M2LB DEV template), or null. Never applied automatically.</summary>
    public string? AppliedTemplateId { get; init; }
    /// <summary>Templates a person may apply. A suggestion is only a hint (e.g. a known host); applying is always an explicit action.</summary>
    public List<IntegrationTemplateOffer> Templates { get; init; } = [];
    /// <summary>Domain extensions enabled by the applied template (e.g. m2lb.child-security-classification). Empty for generic projects.</summary>
    public List<string> DomainExtensions { get; init; } = [];
    /// <summary>Whether this BirkNext instance may call Azure for runtime evidence (<c>IntegrationReview:Azure:Enabled</c>). Separate from which sources are configured.</summary>
    public bool AzureRuntimeEnabled { get; init; }
}

/// <summary>A project integration template a person can apply explicitly. Suggested = a hint from the target (never applied automatically).</summary>
public sealed record IntegrationTemplateOffer(string Id, string Name, string Description, bool Suggested, string? SuggestionReason = null);

/// <summary>Configuration completeness of one integration, with the fields behind it. Pure; shared by pane, API and review.</summary>
public static class IntegrationConfigurationRules
{
    /// <summary>The consumer group checkpoints are read for: the integration's own group, else the platform's expected group as a
    /// configured assumption (never silently <c>$Default</c>: only when the platform states it). Null when neither is known.</summary>
    public static (string? Group, bool Assumed) EffectiveConsumerGroup(IntegrationDefinition topic, IntegrationPlatform? platform) =>
        !string.IsNullOrWhiteSpace(topic.ConsumerGroup) ? (topic.ConsumerGroup.Trim(), false)
        : topic.Kind == IntegrationKind.EventHub && !string.IsNullOrWhiteSpace(platform?.RuntimeEvidence?.ExpectedConsumerGroup) ? (platform.RuntimeEvidence.ExpectedConsumerGroup.Trim(), true)
        : (null, false);

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
        IntegrationKind.IdentityProvisioning => "Identity provisioning",
        _ => kind.ToString(),
    };
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Integration Quality Review v2: capability-specific readiness, typed checks, findings, manual follow-up and a snapshot of
// the configuration each run used. Configured expectation ≠ runtime evidence ≠ contract evidence ≠ review result.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationReviewDomain { Configuration, Connectivity, Contract, MessageFlow, Reliability, ErrorHandling, Security, Observability, Performance, DataQuality }

/// <summary>Before a run: whether a domain can produce evidence. Not a result. Partial: some of the domain's evidence can be read, but by
/// construction not all of it (e.g. transport progression without application processing).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationDomainReadiness { Ready, Available, Limited, NotAssessable, Partial }

/// <summary>Result of one check. Fail only when an explicit expected rule was violated; unavailable evidence is never Fail.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
/// <remarks>Detected and Configured are source/build facts (application messaging): evidence of configuration, never a Pass and never runtime.</remarks>
public enum IntegrationCheckStatus { Pass, Warning, Fail, NotAssessed, Unavailable, NoIndicatorsObserved, Observed, NeedsConfirmation, NotConfigured, NoRecentEvidence, Detected, Configured }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationEvidenceSource { Configuration, NetworkProbe, AzureMetadata, ApplicationInsights, HealthEndpoint, LogEvidence, ContractArtifact, EndpointDiscovery, CheckpointStore, AzureResourceManager, SourceCode, PackageManifest, Infrastructure, AzureMonitor, SourceInfrastructure }

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

/// <summary>One "What can be reviewed" card: the readiness, why, the evidence the run can read, and what stays missing.</summary>
public sealed record IntegrationDomainReadinessRow(IntegrationReviewDomain Domain, IntegrationDomainReadiness Readiness, string Explanation,
    IReadOnlyList<string>? Available = null, IReadOnlyList<string>? Missing = null);

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
    /// <summary>Topics with neither a configured consumer group nor a platform expectation.</summary>
    public int ConsumerGroupsUnknown { get; init; }
    /// <summary>Topics without their own consumer group that use the platform's expected group as a configured assumption (needs confirmation).</summary>
    public int ConsumerGroupsAssumed { get; init; }
    public string? ExpectedConsumerGroup { get; init; }
    public int ContractsConfigured { get; init; }
    /// <summary>False when the domain review for this kind is not implemented; its topics are listed, never faked as reviewed.</summary>
    public bool DomainReviewSupported { get; init; }
}

/// <summary>Pre-run: what is configured, what can be reviewed, what cannot. Never a result.</summary>
public sealed record IntegrationReviewReadiness
{
    public List<IqrSourceSnapshot> SourceSnapshots { get; init; } = [];
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
    /// <summary>Application messaging (Wolverine) evidence from analyzed source — a separate layer, never counted as a runtime evidence source.</summary>
    public List<ApplicationMessagingSummary> ApplicationMessaging { get; init; } = [];
    /// <summary>Service Bus platforms: configured topology and whether runtime metadata can be read. Never an Event Hub runtime source.</summary>
    public List<ServiceBusReadiness> ServiceBus { get; init; } = [];
    /// <summary>Identity provisioning (SCIM) platforms: configured flow and stored source evidence. Nothing is contacted before a run.</summary>
    public List<ScimReadiness> Scim { get; init; } = [];
    /// <summary>Whether this BirkNext instance may call Azure (<c>IntegrationReview:Azure:Enabled</c> and an identity). Separate from which sources are configured.</summary>
    public bool AzureRuntimeEnabled { get; init; }
    /// <summary>Why Azure runtime evidence is not read, when it is not ("IntegrationReview:Azure:Enabled is not true"). Never a configuration problem of the integration.</summary>
    public string? AzureRuntimeReason { get; init; }
    /// <summary>Event Hub runtime evidence sources configured on the platforms (settings only, whether or not Azure runs).</summary>
    public int RuntimeSourcesConfigured { get; init; }
    public int RuntimeSourcesTotal { get; init; }
}

// ── Event Hub runtime evidence as IQR consumes it (snapshot of one run) ─────────────────────────────────────────────────
// Configured ≠ observed ≠ healthy: every comparison keeps both sides and a typed state; observed values carry no verdict without a threshold.

/// <summary>Which runtime evidence sources a platform has configured — settings only, never whether Azure is called. Shared by the review and the UI.</summary>
public static class EventHubRuntimeSources
{
    public static bool Metadata(IntegrationPlatform platform) => platform.RuntimeEvidence?.EventHubMetadata == true && !string.IsNullOrWhiteSpace(platform.NamespaceFqdn);
    /// <summary>Azure Resource Manager reads (namespace, hub list, consumer groups) and Azure Monitor metrics need the subscription, resource group and namespace.</summary>
    public static bool ResourceManager(IntegrationPlatform platform) =>
        Guid.TryParse(platform.RuntimeEvidence?.SubscriptionId?.Trim(), out _) && !string.IsNullOrWhiteSpace(platform.ResourceGroup) && !string.IsNullOrWhiteSpace(platform.Namespace);
    public static bool Checkpoints(IntegrationPlatform platform) => platform.RuntimeEvidence?.ResolvedCheckpointContainerUrl() is not null;
    public static bool Telemetry(IntegrationPlatform platform) => platform.RuntimeEvidence?.TelemetryConfigured == true;

    public static (int Configured, int Total) Count(IntegrationPlatform platform)
    {
        var flags = new[] { Metadata(platform), ResourceManager(platform), Checkpoints(platform), Telemetry(platform) };
        return (flags.Count(f => f), flags.Length);
    }

    /// <summary>Known Debezium / Kafka Connect support hubs. Observed in Azure, they are technical — never an unexpected business hub.</summary>
    public static readonly string[] KnownTechnicalHubs = ["connect-configs", "connect-offsets", "connect-status", "schemahistory"];

    public static bool IsTechnical(IntegrationPlatform platform, string hub) =>
        platform.TechnicalTopics.Any(t => string.Equals(t.Name, hub, StringComparison.OrdinalIgnoreCase))
        || KnownTechnicalHubs.Any(k => string.Equals(k, hub, StringComparison.OrdinalIgnoreCase) || hub.EndsWith("-" + k, StringComparison.OrdinalIgnoreCase) || hub.EndsWith("." + k, StringComparison.OrdinalIgnoreCase))
        || (!string.IsNullOrWhiteSpace(platform.TopicPrefix) && string.Equals(hub, platform.TopicPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>A dedicated Log Analytics workspace is only expected when one is configured; Container Apps logging to Azure Monitor needs none.</summary>
    public static string LogAnalyticsLabel(IntegrationRuntimeEvidenceSettings? settings) =>
        Guid.TryParse(settings?.TelemetryWorkspaceId?.Trim(), out var workspace) ? $"Configured ({workspace:D})" : "Not configured — not required for this platform configuration";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EventHubComparisonState { ObservedMatch, DifferenceObserved, MissingInAzure, AdditionalObserved, TechnicalObserved, ConfiguredDisabled, NotAuthorized, NotAssessed }

/// <summary>One hub as Azure Resource Manager lists it (GET only): status, partitions, retention. No message, no key.</summary>
public sealed record EventHubObservedHub
{
    public string Name { get; init; } = "";
    public string? Status { get; init; }
    public int? PartitionCount { get; init; }
    public long? RetentionHours { get; init; }
}

/// <summary>Namespace and hub list read through Azure Resource Manager in one run.</summary>
public sealed record EventHubNamespaceObservation
{
    public IntegrationEvidenceState State { get; init; }
    public string Reason { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public string? Status { get; init; }
    public string? Sku { get; init; }
    public string? Location { get; init; }
    public string? PublicNetworkAccess { get; init; }
    public bool? DisableLocalAuth { get; init; }
    public string? MinimumTlsVersion { get; init; }
    public int? PrivateEndpointConnections { get; init; }
    /// <summary>State of the hub list itself: a namespace can be readable while the list is not.</summary>
    public IntegrationEvidenceState HubListState { get; init; } = IntegrationEvidenceState.NotConfigured;
    public string HubListReason { get; init; } = "";
    public List<EventHubObservedHub> Hubs { get; init; } = [];
}

public sealed record EventHubMetricObservation(string Name, string Aggregation, double? Value, string Unit);

/// <summary>Azure Monitor platform metrics of the namespace over the review window. Observed only: no threshold, zero is not a pass.</summary>
public sealed record EventHubMetricsEvidence
{
    public IntegrationEvidenceState State { get; init; }
    public string Reason { get; init; } = "";
    public DateTimeOffset CapturedAt { get; init; }
    public int WindowHours { get; init; }
    public List<EventHubMetricObservation> Metrics { get; init; } = [];
    public double? Total(string metric) => Metrics.FirstOrDefault(m => m.Name == metric && m.Aggregation == "Total")?.Value;
}

/// <summary>Configured hub vs the hub Azure lists (or an observed hub nobody configured).</summary>
public sealed record EventHubHubComparison
{
    public string Hub { get; init; } = "";
    public string? IntegrationId { get; init; }
    public bool Technical { get; init; }
    public EventHubComparisonState State { get; init; }
    public int? ConfiguredPartitions { get; init; }
    public int? ObservedPartitions { get; init; }
    public long? ConfiguredRetentionHours { get; init; }
    public long? ObservedRetentionHours { get; init; }
    public string? ObservedStatus { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>Expected consumer group vs the groups Azure lists for the hub. An observed match never confirms the application mapping.</summary>
public sealed record EventHubConsumerGroupComparison
{
    public string IntegrationId { get; init; } = "";
    public string Hub { get; init; } = "";
    public string? Expected { get; init; }
    /// <summary>"Configured assumption", "Configured on the integration" …</summary>
    public string ExpectedProvenance { get; init; } = "";
    public List<string> Observed { get; init; } = [];
    public EventHubComparisonState State { get; init; }
    public string Mapping { get; init; } = "Needs confirmation";
    public string Reason { get; init; } = "";
}

/// <summary>Checkpoint configuration and checkpoint runtime evidence, never flattened into one state.</summary>
public sealed record EventHubCheckpointSummary
{
    public string IntegrationId { get; init; } = "";
    public string Hub { get; init; } = "";
    public string? ConsumerGroup { get; init; }
    public bool GroupAssumed { get; init; }
    /// <summary>"Verified" (source/runtime configuration verified), "Configured" or "Not configured".</summary>
    public string Configuration { get; init; } = "";
    public IntegrationEvidenceState? Runtime { get; init; }
    public string RuntimeReason { get; init; } = "";
    public int Partitions { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
}

/// <summary>Everything the Event Hub runtime evidence contributed to one run, per platform — rendered as captured, never re-queried.</summary>
public sealed record EventHubRuntimeSnapshot
{
    public string PlatformId { get; init; } = "";
    public string PlatformName { get; init; } = "";
    public string? Namespace { get; init; }
    public bool AzureRuntimeEnabled { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
    public EventHubNamespaceObservation? NamespaceObservation { get; init; }
    public EventHubMetricsEvidence? Metrics { get; init; }
    public List<EventHubHubComparison> Hubs { get; init; } = [];
    public List<EventHubConsumerGroupComparison> ConsumerGroups { get; init; } = [];
    public List<EventHubCheckpointSummary> Checkpoints { get; init; } = [];
    public string? ApplicationInsights { get; init; }
    public IntegrationEvidenceState TelemetryState { get; init; } = IntegrationEvidenceState.NotConfigured;
    public string? ContainerAppsLogDestination { get; init; }
    public string LogAnalytics { get; init; } = "";
}

public static class EventHubComparisonLabels
{
    public static string State(EventHubComparisonState state) => state switch
    {
        EventHubComparisonState.ObservedMatch => "Observed match",
        EventHubComparisonState.DifferenceObserved => "Difference observed",
        EventHubComparisonState.MissingInAzure => "Configured but not found in Azure",
        EventHubComparisonState.AdditionalObserved => "Additional observed",
        EventHubComparisonState.TechnicalObserved => "Technical / support hub",
        EventHubComparisonState.ConfiguredDisabled => "Configured (disabled, not reviewed)",
        EventHubComparisonState.NotAuthorized => "Not authorized",
        _ => "Not assessed",
    };
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
    /// <summary>What the domain's assessed checks established ("Configured Event Hub in Azure: Observed"). Evidence, never a verdict by itself.</summary>
    public List<string> Observed { get; init; } = [];
    /// <summary>What the domain could not assess, with the reason ("Consumer lag — needs both positions").</summary>
    public List<string> Missing { get; init; } = [];
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
    public List<IqrSourceSnapshot> SourceSnapshots { get; init; } = [];
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
    /// <summary>Application messaging evidence exactly as this run used it (source analysis + runtime reads). Never re-evaluated later.</summary>
    public ApplicationMessagingEvidenceSet? ApplicationMessagingSnapshot { get; init; }
    public List<ApplicationMessagingRuntime> ApplicationMessagingRuntime { get; init; } = [];
    /// <summary>Service Bus evidence exactly as this run read it (topology comparison, runtime metadata, route correlation). Never re-queried.</summary>
    public List<ServiceBusEvidenceCheck> ServiceBusSnapshot { get; init; } = [];
    /// <summary>SCIM provisioning evidence exactly as this run established it (source analysis, safe GET checks, correlations). Never re-queried.</summary>
    public List<ScimEvidenceCheck> ScimSnapshot { get; init; } = [];
    /// <summary>Security classification (source + configuration only) as this run used it. Its own review owns live checks.</summary>
    public ClassificationReviewResult? SecurityClassificationSnapshot { get; init; }
    /// <summary>Event Hub runtime evidence exactly as this run read and compared it (namespace, hubs, consumer groups, checkpoints, metrics). Never re-queried.</summary>
    public List<EventHubRuntimeSnapshot> EventHubSnapshot { get; init; } = [];
    /// <summary>Configured integration values compared with the Infrastructure evidence of the selected source snapshot (declared in source, never
    /// deployed state). Bound to that snapshot. Empty for runs without Source Analysis infrastructure evidence and for runs recorded before it existed.</summary>
    public List<BirkNext.SourceDomains.ConfiguredSourceComparison> SourceInfrastructureComparisons { get; init; } = [];
    /// <summary>Configured integration values looked up in the newest Azure Environment Analysis snapshot of the Target Environment (observed in the
    /// control plane, never verified behaviour). Bound to that snapshot. Empty when no snapshot exists or Azure Environment Analysis is hidden.</summary>
    public List<BirkNext.AzureEnvironment.ConfiguredObservedComparison> ObservedAzureComparisons { get; init; } = [];
    /// <summary>The checks this run actually executed with evidence (a source that could not be read is listed under <see cref="WhatWasNotAssessed"/>).</summary>
    public List<string> WhatWasTested { get; init; } = [];
    /// <summary>What this run did not assess, and why — explicit, never implied by absence.</summary>
    public List<string> WhatWasNotAssessed { get; init; } = [];
    /// <summary>Quality among assessed PROJECT-QUALITY checks (runtime, contract and source evidence), with coverage. Configuration completeness
    /// is <see cref="ConfigurationReadiness"/>, not quality. Computed from the checks (older stored runs recompute it); null when nothing was judged.</summary>
    public BirkNext.Applicability.QualityResult Quality => IntegrationReviewScoring.Quality(AllChecks);
    /// <summary>Configuration readiness: the configuration-provenance checks only. A configuration gap is a readiness concern, not a quality failure.</summary>
    public BirkNext.Applicability.QualityResult ConfigurationReadiness => IntegrationReviewScoring.ConfigurationReadiness(AllChecks);
    /// <summary>Per reviewed system: how much runtime evidence BirkNext can obtain for its technology (registry). Unsupported = a tool
    /// limitation (e.g. Kafka): it never lowers <see cref="Quality"/>.</summary>
    public List<IntegrationRuntimeSupport> RuntimeSupport => IntegrationReviewScoring.RuntimeSupport(Systems);
    [JsonIgnore] public int TopicsReviewed => Systems.Where(s => s.DomainReviewSupported).Sum(s => s.Topics.Count);
    [JsonIgnore] public IEnumerable<IntegrationCheck> AllChecks => Systems.SelectMany(s => s.PlatformChecks.Concat(s.Topics.SelectMany(t => t.Checks)));
}

public sealed record IntegrationReviewRunRequest
{
    public List<IqrSourceSelection> SourceSelections { get; init; } = [];
    public string EnvironmentId { get; init; } = "";
    public string EnvironmentName { get; init; } = "";
    /// <summary>The Target Environment type (Development, QA, Production …). Runtime checks that contact an endpoint refuse Production and unknown types.</summary>
    public string? EnvironmentType { get; init; }
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
        IntegrationEvidenceSource.SourceCode => "Source code",
        IntegrationEvidenceSource.PackageManifest => "Package reference",
        IntegrationEvidenceSource.Infrastructure => "Audited infrastructure configuration",
        IntegrationEvidenceSource.SourceInfrastructure => "Infrastructure as Code (Source Analysis, declared)",
        IntegrationEvidenceSource.AzureMonitor => "Azure Monitor",
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

/// <summary>Runtime-evidence support of one reviewed system's technology (from the shared technology registry).</summary>
public sealed record IntegrationRuntimeSupport(string SystemName, string TechnologyId, BirkNext.Technology.SupportLevel Level, string Detail);

/// <summary>
/// Integration Quality Review on the shared scoring semantics. Check classes:
/// <list type="bullet">
/// <item>Project quality — runtime, contract, source and infrastructure evidence judged Pass/Warning/Fail/NeedsConfirmation.</item>
/// <item>Configuration readiness — checks of the configured record itself (provenance Configuration). Never quality.</item>
/// <item>Evidence availability — NotAssessed/Unavailable/NotConfigured: coverage gaps, never failures.</item>
/// <item>Observations — Observed/Detected/Configured/NoIndicatorsObserved/NoRecentEvidence: executed, informational, no judgement (Observed ≠ Pass).</item>
/// <item>Tool coverage — a technology without a runtime provider (Kafka, RabbitMQ, SOAP …): <see cref="RuntimeSupport"/>, never quality.</item>
/// </list>
/// </summary>
public static class IntegrationReviewScoring
{
    public static BirkNext.Applicability.CheckOutcome Outcome(IntegrationCheckStatus status) => status switch
    {
        IntegrationCheckStatus.Pass => BirkNext.Applicability.CheckOutcome.Pass,
        IntegrationCheckStatus.Warning => BirkNext.Applicability.CheckOutcome.Warning,
        IntegrationCheckStatus.Fail => BirkNext.Applicability.CheckOutcome.Fail,
        IntegrationCheckStatus.NeedsConfirmation => BirkNext.Applicability.CheckOutcome.NeedsReview,
        IntegrationCheckStatus.Unavailable => BirkNext.Applicability.CheckOutcome.Unavailable,
        IntegrationCheckStatus.NotAssessed or IntegrationCheckStatus.NotConfigured => BirkNext.Applicability.CheckOutcome.NotAssessed,
        _ => BirkNext.Applicability.CheckOutcome.Informational,
    };

    public static bool IsConfigurationCheck(IntegrationCheck check) => check.Provenance == IntegrationEvidenceSource.Configuration;

    public static BirkNext.Applicability.QualityResult Quality(IEnumerable<IntegrationCheck> checks) =>
        BirkNext.Applicability.ScoreSemantics.Compute(checks.Where(c => !IsConfigurationCheck(c)).Select(c => Outcome(c.Status)));

    public static BirkNext.Applicability.QualityResult ConfigurationReadiness(IEnumerable<IntegrationCheck> checks) =>
        BirkNext.Applicability.ScoreSemantics.Compute(checks.Where(IsConfigurationCheck).Select(c => Outcome(c.Status)));

    public static List<IntegrationRuntimeSupport> RuntimeSupport(IEnumerable<IntegrationSystemResult> systems) => systems.Select(s =>
    {
        var id = BirkNext.Technology.IntegrationTechnology.Map(s.Kind, s.SystemName);
        var descriptor = BirkNext.Technology.TechnologySupportRegistry.Find(id);
        var level = descriptor?.RuntimeObservation ?? BirkNext.Technology.SupportLevel.Unsupported;
        var detail = BirkNext.Technology.TechnologySupportRegistry.IsSupported(level)
            ? $"Runtime evidence: {BirkNext.Technology.TechnologySupportRegistry.Label(level)}."
            : $"BirkNext has no runtime provider for {descriptor?.DisplayName ?? id}; configuration is reviewed, runtime is not assessed (a tool limitation, not an integration defect).";
        return new IntegrationRuntimeSupport(s.SystemName, id, level, detail);
    }).ToList();
}
