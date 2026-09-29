using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations;

/// <summary>
/// Known M2LB DEV Event Hub configuration, seeded as ordinary catalog records (Origin = Seed) for the Development Target
/// Environment whose frontend is m2lbdev.bufetat.no. Values were supplied by the platform team; Terraform is not read,
/// imported or referenced. No QA or production values are derived from these. No secret is seeded: authentication is the
/// NAME of a mechanism only.
///
/// Consumer mappings, strongest first:
/// <list type="bullet">
/// <item>Confirmed — BIRK Person CDC → Person Adapter (confirmed for M2LB DEV).</item>
/// <item>Suggested — hubs the audited M2LB source (QA-context audit, <c>KnownIntegrationTemplates</c>) names a consuming service
/// for. Environment-independent source evidence, not confirmed for DEV; no container app or identity is inferred.</item>
/// <item>Needs confirmation — every other hub. Receiver rights on the namespace do not make an adapter a consumer.</item>
/// </list>
/// Consumer group is left unknown on every integration. The platform carries <c>$Default</c> as an explicit, labelled CONFIGURED
/// ASSUMPTION (<see cref="RuntimeDefaults"/>): it lets checkpoint lookups run, but never confirms a mapping or yields a Pass.
/// </summary>
public static partial class M2lbDevIntegrationSeed
{
    /// <summary>v2 adds the M2LB DEV Service Bus platform (see <see cref="ServiceBusPlatform"/>); v1 environments receive only that platform.
    /// v3 adds SCIM; v4 adds the verified Event Hub runtime-evidence defaults (<see cref="RuntimeDefaults"/>) without overwriting any value.</summary>
    public const int Version = 4;
    public const string Location = "Norway East";
    public const string PersonAdapterApp = "ca-m2lb-person-adp-dev-nwe-001";
    public const string Name = "m2lb-dev-eventhub";
    public const string FrontendHost = "m2lbdev.bufetat.no";
    public const string PlatformId = "dev:eventhub:m2lb";
    public const string SystemName = "BIRK CDC / Debezium";
    public const string Namespace = "evhns-m2lb-dev-nwe-001";
    public const string TopicPrefix = "m2lb-cdc-dev";
    public const string SourceDatabase = "BirkM2LB";
    public const string AuditedSource = "Audited M2LB source (QA-context audit)";

    public static readonly string[] Tables =
    [
        "Person", "Tiltak", "Bestilling", "TjenesteType", "TiltaksStatusType", "AvslutningsGrunnType", "Barn", "BarnStatusType",
        "BarnType", "Kommune", "KjønnType", "TvangsProtokoll", "HjemmelType", "TvangsProtokollStatusType", "Romning", "RomningKategoriType",
    ];

    /// <summary>Consuming services named per hub by the audited M2LB source (templates eh-barn, eh-tiltak …). Person is confirmed separately.</summary>
    private static readonly Dictionary<string, string> AuditedConsumers = new(StringComparer.Ordinal)
    {
        ["Barn"] = "PersonBiRKAdapter",
        ["Tiltak"] = "Tjeneste API",
        ["Bestilling"] = "Tjeneste API",
        ["TjenesteType"] = "Tjeneste API",
        ["TiltaksStatusType"] = "Tjeneste API",
        ["AvslutningsGrunnType"] = "Tjeneste API",
        ["TvangsProtokoll"] = "Hendelse BiRK Adapter",
        ["Romning"] = "Hendelse BiRK Adapter",
    };

    /// <summary>The seed applies to the Development environment whose frontend host is m2lbdev.bufetat.no — nothing else.</summary>
    public static bool AppliesTo(string? environmentType, string? targetUrl) =>
        string.Equals(environmentType, "Development", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, FrontendHost, StringComparison.OrdinalIgnoreCase);

    public static string IntegrationId(string table) => $"dev:eventhub:birk-cdc:dbo.{table}";
    public static string Topic(string table) => $"{TopicPrefix}.{SourceDatabase}.dbo.{table}";

    public static IntegrationPlatform Platform(string environmentId, DateTimeOffset now) => new()
    {
        Id = PlatformId, EnvironmentId = environmentId, Name = "M2LB DEV Event Hubs", Kind = IntegrationKind.EventHub, Enabled = true,
        Region = Location, Namespace = Namespace, NamespaceFqdn = $"{Namespace}.servicebus.windows.net", ResourceGroup = "rg-m2lb-dev-integration-nwe",
        TechnicalOwner = "platform-team", MonitoringProvider = "Application Insights", MonitoringUrl = null, RunbookUrl = null,
        ProducerTechnology = "Debezium SQL Server CDC", ProducerAuthentication = IntegrationAuthMechanism.Sas,
        DefaultConsumerAuthentication = IntegrationAuthMechanism.ManagedIdentity,
        SourceDatabase = SourceDatabase, SourceHost = "10.31.19.31", SourcePort = 50806, TopicPrefix = TopicPrefix,
        DefaultPartitionCount = 1, DefaultRetentionDays = 7,
        TechnicalTopics =
        [
            new() { Name = TopicPrefix, Purpose = "Schema-change events" },
            new() { Name = "schemahistory", Purpose = "Debezium schema history" },
            new() { Name = "connect-configs", Purpose = "Kafka Connect internal (configs)" },
            new() { Name = "connect-offsets", Purpose = "Kafka Connect internal (offsets)" },
            new() { Name = "connect-status", Purpose = "Kafka Connect internal (status)" },
        ],
        RuntimeEvidence = RuntimeDefaults(),
        Origin = IntegrationRecordOrigin.Seed, UpdatedAt = now,
    };

    /// <summary>
    /// Verified M2LB DEV runtime-evidence sources (identifiers only). Subscription, namespace (Norway East, Premium, 1 PU), Application Insights
    /// and the Person Adapter were verified in Azure; the checkpoint location comes from the Person Adapter's environment variables
    /// (EventHub__FQDN, Storage__BlobEndpoint, Storage__ContainerName) — source/runtime configuration verified, not an observed checkpoint.
    /// <c>$Default</c> is a configured assumption. The Container Apps environment sends logs to Azure Monitor and has no Log Analytics
    /// workspace, so none is set (not required). The Application Insights connection string is never read or stored — only that it exists.
    /// No threshold: lag and checkpoint age are reported as Observed.
    /// </summary>
    public static IntegrationRuntimeEvidenceSettings RuntimeDefaults() => new()
    {
        EventHubMetadata = true,
        SubscriptionId = "2fcdb9d0-22eb-43b0-b95e-7cbba08c34b0", SubscriptionName = "m2lb-samhandling-dev",
        NamespaceSku = "Premium", NamespaceCapacity = 1,
        ExpectedConsumerGroup = IntegrationRuntimeEvidenceSettings.DefaultConsumerGroupName,
        ExpectedConsumerGroupProvenance = IntegrationValueProvenance.ConfiguredAssumption,
        ExpectedConsumerGroupNote = "Observed on hub m2lb-cdc-dev.birkm2lb.dbo.barntype (Active, 1 partition, 168 h retention, 1 consumer group); not confirmed as the group the consumer reads with.",
        CheckpointBlobEndpoint = "https://stm2bbirkdevnwe001.blob.core.windows.net/", CheckpointContainerName = "person-adapter",
        CheckpointProvenance = IntegrationValueProvenance.SourceConfigurationVerified,
        CheckpointSourceNote = "Person Adapter environment variables EventHub__FQDN, Storage__BlobEndpoint and Storage__ContainerName.",
        ApplicationInsightsResourceName = "appi-m2lb-dev-nwe-001", ApplicationInsightsResourceGroup = "rg-m2lb-dev-shared-nwe", ApplicationInsightsConfigured = true,
        ContainerAppsLogDestination = "azure-monitor",
        ConsumerApplicationName = PersonAdapterApp, ConsumerApplicationResourceGroup = "rg-m2lb-dev-apps-nwe",
        ReviewWindowHours = IntegrationRuntimeEvidenceSettings.DefaultReviewWindowHours,
    };

    /// <summary>
    /// Seed upgrade (v4) of an existing Event Hub platform: fills only what is missing, never overwrites a value. A platform a person edited
    /// keeps its runtime settings untouched once it has any; an unedited seeded platform gains each missing default field.
    /// </summary>
    public static IntegrationPlatform WithRuntimeDefaults(IntegrationPlatform platform)
    {
        if (platform.Origin != IntegrationRecordOrigin.Seed || platform.Kind != IntegrationKind.EventHub) return platform;
        var defaults = RuntimeDefaults();
        if (platform.RuntimeEvidence is null) return platform with { RuntimeEvidence = defaults, Region = platform.UserModified || platform.Region is not (null or "nwe") ? platform.Region : Location };
        if (platform.UserModified) return platform;
        var r = platform.RuntimeEvidence;
        static string? Keep(string? current, string? fallback) => string.IsNullOrWhiteSpace(current) ? fallback : current;
        return platform with
        {
            Region = platform.Region is null or "nwe" ? Location : platform.Region,
            RuntimeEvidence = r with
            {
                SubscriptionId = Keep(r.SubscriptionId, defaults.SubscriptionId), SubscriptionName = Keep(r.SubscriptionName, defaults.SubscriptionName),
                NamespaceSku = Keep(r.NamespaceSku, defaults.NamespaceSku), NamespaceCapacity = r.NamespaceCapacity ?? defaults.NamespaceCapacity,
                ExpectedConsumerGroup = Keep(r.ExpectedConsumerGroup, defaults.ExpectedConsumerGroup),
                ExpectedConsumerGroupProvenance = string.IsNullOrWhiteSpace(r.ExpectedConsumerGroup) ? defaults.ExpectedConsumerGroupProvenance : r.ExpectedConsumerGroupProvenance,
                ExpectedConsumerGroupNote = string.IsNullOrWhiteSpace(r.ExpectedConsumerGroup) ? defaults.ExpectedConsumerGroupNote : r.ExpectedConsumerGroupNote,
                CheckpointBlobEndpoint = string.IsNullOrWhiteSpace(r.CheckpointContainerUrl) ? Keep(r.CheckpointBlobEndpoint, defaults.CheckpointBlobEndpoint) : r.CheckpointBlobEndpoint,
                CheckpointContainerName = string.IsNullOrWhiteSpace(r.CheckpointContainerUrl) ? Keep(r.CheckpointContainerName, defaults.CheckpointContainerName) : r.CheckpointContainerName,
                CheckpointProvenance = r.ResolvedCheckpointContainerUrl() is null ? defaults.CheckpointProvenance : r.CheckpointProvenance,
                CheckpointSourceNote = r.ResolvedCheckpointContainerUrl() is null ? defaults.CheckpointSourceNote : r.CheckpointSourceNote,
                ApplicationInsightsResourceName = Keep(r.ApplicationInsightsResourceName, defaults.ApplicationInsightsResourceName),
                ApplicationInsightsResourceGroup = Keep(r.ApplicationInsightsResourceGroup, defaults.ApplicationInsightsResourceGroup),
                ApplicationInsightsConfigured = r.ApplicationInsightsConfigured ?? defaults.ApplicationInsightsConfigured,
                ContainerAppsLogDestination = Keep(r.ContainerAppsLogDestination, defaults.ContainerAppsLogDestination),
                ConsumerApplicationName = Keep(r.ConsumerApplicationName, defaults.ConsumerApplicationName),
                ConsumerApplicationResourceGroup = Keep(r.ConsumerApplicationResourceGroup, defaults.ConsumerApplicationResourceGroup),
                ReviewWindowHours = r.ReviewWindowHours ?? defaults.ReviewWindowHours,
            },
        };
    }

    public static IReadOnlyList<IntegrationDefinition> Integrations(string environmentId, DateTimeOffset now) =>
        Tables.Select(table => new IntegrationDefinition
        {
            Id = IntegrationId(table), EnvironmentId = environmentId, PlatformId = PlatformId, DisplayName = $"BIRK {table} CDC",
            Kind = IntegrationKind.EventHub, Enabled = true, SystemName = SystemName,
            SourceSystem = "BIRK SQL Server", SourceResource = $"{SourceDatabase}.dbo.{table}", DestinationSystem = "M2LB",
            EndpointOrTopic = Topic(table), ConsumerGroup = null,
            Producer = "Debezium SQL Server CDC", ProducerAuthentication = IntegrationAuthMechanism.Sas,
            Consumer = Consumer(table), ConsumerAuthentication = IntegrationAuthMechanism.ManagedIdentity,
            ContractRelationship = ContractRelationshipState.NotConfigured,
            PartitionCount = 1, RetentionDays = 7, Origin = IntegrationRecordOrigin.Seed, UpdatedAt = now,
        }).ToList();

    private static IntegrationConsumer Consumer(string table) => table switch
    {
        "Person" => new IntegrationConsumer
        {
            DisplayName = "Person Adapter", LogicalName = "person-adapter", ContainerApp = PersonAdapterApp,
            ManagedIdentity = "id-m2lb-person-adp-dev-nwe", MappingState = ConsumerMappingState.Confirmed, MappingSource = "Confirmed M2LB DEV mapping",
        },
        _ when AuditedConsumers.TryGetValue(table, out var service) => new IntegrationConsumer
        {
            DisplayName = service, MappingState = ConsumerMappingState.Suggested, MappingSource = AuditedSource,
        },
        _ => new IntegrationConsumer { MappingState = ConsumerMappingState.NeedsConfirmation },
    };
}
