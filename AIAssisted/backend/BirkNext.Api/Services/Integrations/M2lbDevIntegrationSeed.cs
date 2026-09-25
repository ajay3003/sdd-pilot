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
/// Consumer group is left unknown everywhere: <c>$Default</c> is never assumed.
/// </summary>
public static class M2lbDevIntegrationSeed
{
    public const int Version = 1;
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
        Region = "nwe", Namespace = Namespace, NamespaceFqdn = $"{Namespace}.servicebus.windows.net", ResourceGroup = "rg-m2lb-dev-integration-nwe",
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
        Origin = IntegrationRecordOrigin.Seed, UpdatedAt = now,
    };

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
            DisplayName = "Person Adapter", LogicalName = "person-adapter", ContainerApp = "ca-m2lb-person-adp-dev-nwe-001",
            ManagedIdentity = "id-m2lb-person-adp-dev-nwe", MappingState = ConsumerMappingState.Confirmed, MappingSource = "Confirmed M2LB DEV mapping",
        },
        _ when AuditedConsumers.TryGetValue(table, out var service) => new IntegrationConsumer
        {
            DisplayName = service, MappingState = ConsumerMappingState.Suggested, MappingSource = AuditedSource,
        },
        _ => new IntegrationConsumer { MappingState = ConsumerMappingState.NeedsConfirmation },
    };
}
