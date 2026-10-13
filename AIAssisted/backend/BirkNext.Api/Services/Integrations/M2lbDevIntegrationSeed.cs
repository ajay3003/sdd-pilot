using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations;

/// <summary>
/// The M2LB DEV helper: ONLY the environment-specific configuration that cannot be reliably discovered from source, seeded as ordinary
/// catalog records (Origin = Seed) for the Development Target Environment whose frontend is m2lbdev.bufetat.no — and only there (the
/// server refuses any other environment). Terraform is not read at runtime; no secret is seeded (authentication is a mechanism name).
///
/// What it adds (v5):
/// <list type="bullet">
/// <item>The Event Hub platform (namespace, resource group, region, Debezium source database and topic prefix, Kafka Connect technical
/// topics) with the runtime-evidence identifiers verified at the time (<see cref="RuntimeDefaults"/>; current-at-time, not re-verified).</item>
/// <item>One integration: BIRK Person CDC → Person Adapter, the one consumer mapping CONFIRMED for M2LB DEV.</item>
/// <item>The Service Bus platform (namespace, resource group) with the send/receive grants of a Terraform audit, which application source
/// cannot prove; and the SCIM platform (container app, identity, gateway route) — ingress and identity are deployment facts.</item>
/// </list>
/// Everything Source Analysis discovers — the other CDC hubs and their consumers, Service Bus entities, publishers and listeners, SCIM
/// routes in code, Wolverine mappings — is no longer seeded: it is offered as a source suggestion in Target Environment → Integrations.
/// Consumer group stays unknown; <c>$Default</c> is a labelled CONFIGURED ASSUMPTION that never confirms a mapping or yields a Pass.
/// </summary>
public static partial class M2lbDevIntegrationSeed
{
    /// <summary>v2 adds the M2LB DEV Service Bus platform (see <see cref="ServiceBusPlatform"/>); v1 environments receive only that platform.
    /// v3 adds SCIM; v4 adds the verified Event Hub runtime-evidence defaults (<see cref="RuntimeDefaults"/>) without overwriting any value.
    /// v5 reduces a new application to what source cannot supply (one confirmed integration instead of 16, no source host) and normalizes the
    /// region label of unedited seeded platforms; existing records are never removed, overwritten or resurrected.</summary>
    public const int Version = 5;
    public const string Location = "Norway East";
    public const string PersonAdapterApp = "ca-m2lb-person-adp-dev-nwe-001";
    public const string Name = "m2lb-dev-eventhub";
    public const string FrontendHost = "m2lbdev.bufetat.no";
    public const string TemplateName = "M2LB DEV helper";
    public const string TemplateDescription = "Adds only environment-specific configuration that cannot be reliably discovered from source: the M2LB DEV platforms and their runtime identifiers, the confirmed Person CDC mapping, and the M2LB child security classification extension. Other integrations come from Source Analysis suggestions.";
    public const string PlatformId = "dev:eventhub:m2lb";
    public const string SystemName = "BIRK CDC / Debezium";
    public const string Namespace = "evhns-m2lb-dev-nwe-001";
    public const string TopicPrefix = "m2lb-cdc-dev";
    public const string SourceDatabase = "BirkM2LB";

    /// <summary>The one CDC table the helper still seeds: its consumer mapping is confirmed for M2LB DEV, which source cannot establish.</summary>
    public const string ConfirmedTable = "Person";

    /// <summary>Whether the helper fits the environment: the Development environment whose frontend host is m2lbdev.bufetat.no.</summary>
    public static bool AppliesTo(string? environmentType, string? targetUrl) => ApplyBlock(environmentType, targetUrl) is null;

    /// <summary>Why the helper must not be applied to this environment, or null. Enforced by the server, never only by the UI.</summary>
    public static string? ApplyBlock(string? environmentType, string? targetUrl)
    {
        if (string.Equals(environmentType, "Production", StringComparison.OrdinalIgnoreCase))
            return "The M2LB DEV helper is never applied to a Production environment.";
        if (!string.Equals(environmentType, "Development", StringComparison.OrdinalIgnoreCase))
            return $"The M2LB DEV helper applies only to a Development environment (this one is {(string.IsNullOrWhiteSpace(environmentType) ? "of unknown type" : environmentType)}).";
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, FrontendHost, StringComparison.OrdinalIgnoreCase))
            return $"The M2LB DEV helper applies only to the M2LB DEV environment ({FrontendHost}).";
        return null;
    }

    /// <summary>Whether an environment that already carries the helper may receive its add-missing upgrades: Development only.</summary>
    public static bool MayUpgrade(string? environmentType) => string.Equals(environmentType, "Development", StringComparison.OrdinalIgnoreCase);

    /// <summary>Exactly what applying adds, with each value's provenance, for the review before applying.</summary>
    public static IReadOnlyList<IntegrationTemplateItem> Items() =>
    [
        new("Platform", "M2LB DEV Event Hubs", $"Namespace {Namespace}, resource group rg-m2lb-dev-integration-nwe, region {Location}; Debezium source database {SourceDatabase}, topic prefix {TopicPrefix}, Kafka Connect technical topics.", IntegrationTemplateProvenance.HistoricalAudit),
        new("Runtime evidence", "Event Hub runtime identifiers", "Subscription m2lb-samhandling-dev, Premium namespace, checkpoint storage (Person Adapter container), Application Insights appi-m2lb-dev-nwe-001 — verified at the time, not re-verified.", IntegrationTemplateProvenance.HistoricalAudit),
        new("Runtime evidence", "Expected consumer group $Default", "A configured assumption: lets checkpoint lookups run; never confirms a mapping or yields a Pass.", IntegrationTemplateProvenance.ConfiguredAssumption),
        new("Integration", "BIRK Person CDC → Person Adapter", $"Topic {Topic(ConfirmedTable)}; consumer mapping confirmed for M2LB DEV.", IntegrationTemplateProvenance.ConfirmedMapping),
        new("Platform", "M2LB DEV Service Bus", $"Namespace {ServiceBusNamespace}, resource group rg-m2lb-dev-shared-nwe; send/receive grants from a Terraform audit (not provable from application source).", IntegrationTemplateProvenance.HistoricalAudit),
        new("Platform", "M2LB Entra SCIM Provisioning", "Container app, managed identity and gateway route of the SCIM adapter; the public tenant URL stays unknown.", IntegrationTemplateProvenance.HistoricalAudit),
        new("Extension", "M2LB child security classification", "Enables the M2LB child security classification review extension.", IntegrationTemplateProvenance.ConfiguredAssumption),
    ];

    public static string IntegrationId(string table) => $"dev:eventhub:birk-cdc:dbo.{table}";
    public static string Topic(string table) => $"{TopicPrefix}.{SourceDatabase}.dbo.{table}";

    public static IntegrationPlatform Platform(string environmentId, DateTimeOffset now) => new()
    {
        Id = PlatformId, EnvironmentId = environmentId, Name = "M2LB DEV Event Hubs", Kind = IntegrationKind.EventHub, Enabled = true,
        Region = Location, Namespace = Namespace, NamespaceFqdn = $"{Namespace}.servicebus.windows.net", ResourceGroup = "rg-m2lb-dev-integration-nwe",
        TechnicalOwner = "platform-team", MonitoringProvider = "Application Insights", MonitoringUrl = null, RunbookUrl = null,
        ProducerTechnology = "Debezium SQL Server CDC", ProducerAuthentication = IntegrationAuthMechanism.Sas,
        DefaultConsumerAuthentication = IntegrationAuthMechanism.ManagedIdentity,
        SourceDatabase = SourceDatabase, TopicPrefix = TopicPrefix,
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

    /// <summary>The integrations the helper seeds: only the confirmed Person CDC mapping (other hubs are source suggestions).</summary>
    public static IReadOnlyList<IntegrationDefinition> Integrations(string environmentId, DateTimeOffset now) =>
        new[] { ConfirmedTable }.Select(table => new IntegrationDefinition
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

    private static IntegrationConsumer Consumer(string table) => new()
    {
        DisplayName = "Person Adapter", LogicalName = "person-adapter", ContainerApp = PersonAdapterApp,
        ManagedIdentity = "id-m2lb-person-adp-dev-nwe", MappingState = ConsumerMappingState.Confirmed, MappingSource = "Confirmed M2LB DEV mapping",
    };

    /// <summary>
    /// Seed upgrade (v5) of an unedited seeded Service Bus or SCIM platform: the region label "nwe" becomes the canonical location name.
    /// A platform a person edited, or one with any other region, is left exactly as it is.
    /// </summary>
    public static IntegrationPlatform WithCanonicalRegion(IntegrationPlatform platform) =>
        platform.Origin == IntegrationRecordOrigin.Seed && !platform.UserModified && platform.Region == "nwe" ? platform with { Region = Location } : platform;
}
