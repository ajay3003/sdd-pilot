using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations;

/// <summary>
/// M2LB DEV Service Bus platform — a separate transport from the Event Hub platform. The expected topology below is a TRUSTED SEED from a
/// developer-side audit of the platform Terraform (Terraform-iac-m2lb, 2026-09-28):
/// <list type="bullet">
/// <item>Entities and properties: <c>layers/02-shared/service_bus_topology.tf</c> (queues/topics/subscriptions, TTL P14D, MaxDeliveryCount 10,
/// LockDuration PT1M, dead-lettering on expiration, sessions off).</item>
/// <item>Namespace: <c>layers/02-shared/service_bus.tf</c> + <c>locals.tf</c> (<c>sbns-m2lb-${env}-${location_short}-001</c>, Premium, private
/// endpoint only, local/SAS auth disabled) — name derived for env = dev, location_short = nwe.</item>
/// <item>Send/receive grants: <c>layers/04-apps/ca_*.tf</c> scopes and <c>adapters_sb_rbac.tf</c> (namespace-wide receiver for the adapters),
/// mapped to the analyzed application projects by the audit (container app → repository project).</item>
/// </list>
/// BirkNext never reads, parses or imports Terraform at runtime. The Azure subscription id is not in Terraform (it comes from the pipeline
/// service connection) and is therefore NOT seeded: runtime metadata stays Not configured until someone enters it.
/// </summary>
public static partial class M2lbDevIntegrationSeed
{
    public const string ServiceBusPlatformId = "dev:servicebus:m2lb";
    public const string ServiceBusNamespace = "sbns-m2lb-dev-nwe-001";
    public const string ServiceBusAuditSource = "Developer-side audit of Terraform-iac-m2lb (layers/02-shared/service_bus_topology.tf, layers/04-apps/ca_*.tf), seeded 2026-09-28";

    private const string AutorisasjonApi = "M2LB.Autorisasjon.Api";
    private const string HendelseApi = "M2LB.Hendelse.Api";
    private const string HendelseAdapter = "M2LB.Hendelse.BiRK.Adapter";
    private const string PersonApi = "M2LB.Person.Api";
    private const string PersonAdapter = "M2LB.PersonBiRKAdapter.Worker";
    private const string TjenesteApi = "M2LB.Tjeneste.Api";
    private const string ScimAdapter = "M2LB.Autorisasjon.ScimAdapter";
    private const string Revisjon = "M2LB.Revisjon.Worker";

    private static ServiceBusEntityExpectation Queue(string name, string[] publishers, string[] consumers, string? note = null) => new()
    {
        EntityType = ServiceBusEntityType.Queue, Name = name, Publishers = [.. publishers], Consumers = [.. consumers], Note = note,
        MaxDeliveryCount = 10, DefaultMessageTimeToLive = "P14D", DeadLetteringOnMessageExpiration = true, LockDuration = "PT1M", MaxSizeInMegabytes = 1024, RequiresSession = false,
    };

    private static ServiceBusEntityExpectation Topic(string name, params string[] publishers) => new()
    {
        EntityType = ServiceBusEntityType.Topic, Name = name, Publishers = [.. publishers],
        DefaultMessageTimeToLive = "P14D", MaxSizeInMegabytes = 1024, RequiresDuplicateDetection = false,
    };

    private static ServiceBusEntityExpectation Subscription(string topic, string name, params string[] consumers) => new()
    {
        EntityType = ServiceBusEntityType.Subscription, Topic = topic, Name = name, Consumers = [.. consumers],
        MaxDeliveryCount = 10, DefaultMessageTimeToLive = "P14D", DeadLetteringOnMessageExpiration = true, DeadLetteringOnFilterEvaluationExceptions = true,
        LockDuration = "PT1M", RequiresSession = false,
    };

    public static ServiceBusTopology ServiceBusTopology() => new()
    {
        Source = ServiceBusAuditSource, AuditedAt = new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero),
        NamespaceReceivers = [PersonAdapter, HendelseAdapter, ScimAdapter],
        Notes =
        [
            "Values are the Terraform-declared expectation for DEV, not deployment-verified; runtime metadata confirms or contradicts them.",
            "Namespace name derived from the Terraform naming convention (environment dev, location nwe); the Azure subscription id is unknown.",
            "Premium namespace with public network access disabled and local (SAS) authentication disabled: Entra-only data plane via private endpoint.",
        ],
        Entities =
        [
            Queue("operasjonsregistrering", [HendelseApi, PersonApi, TjenesteApi], [AutorisasjonApi]),
            Queue("leselogg", [HendelseApi, PersonApi, TjenesteApi, Revisjon], [Revisjon, TjenesteApi], "Read-log audit queue (ServiceBus:Leselogg:KoeName default \"leselogg\")."),
            Queue("operatorkontroll.varsler", [HendelseApi], [], "Operator-alert queue; no receiving application is granted in the audited infrastructure."),
            Queue("birk-adapter-errors", [HendelseAdapter], [], "Error events from the Birk Hendelse Adapter; no receiving application is granted in the audited infrastructure."),
            Topic("autorisasjon.operasjoner", AutorisasjonApi),
            Topic("autorisasjon.roller", AutorisasjonApi),
            Topic("autorisasjon.tilganger", AutorisasjonApi),
            Topic("autorisasjon.nodtilgang", AutorisasjonApi),
            Topic("autorisasjon.organisasjon", AutorisasjonApi),
            Topic("person.person", PersonApi),
            Topic("person.barn", PersonApi),
            Topic("person.audit", PersonApi),
            Topic("hendelser.barn", HendelseApi),
            Topic("tjeneste.tjenester", TjenesteApi),
            Topic("entra.brukere", ScimAdapter),
            Subscription("autorisasjon.operasjoner", "autorisasjon.operasjoner.cache", AutorisasjonApi),
            Subscription("autorisasjon.roller", "autorisasjon.roller.cache", AutorisasjonApi),
            Subscription("autorisasjon.tilganger", "autorisasjon.tilganger.cache", AutorisasjonApi),
            Subscription("autorisasjon.tilganger", "autorisasjon.barnerelasjoner.cache", AutorisasjonApi),
            Subscription("autorisasjon.nodtilgang", "autorisasjon.nodtilgang.cache", AutorisasjonApi),
            Subscription("autorisasjon.organisasjon", "autorisasjon.organisasjon.cache", AutorisasjonApi),
            Subscription("person.barn", "tjeneste-barnregistrert", TjenesteApi),
            Subscription("tjeneste.tjenester", "hendelsestjenesten", HendelseApi),
        ],
    };

    public static IntegrationPlatform ServiceBusPlatform(string environmentId, DateTimeOffset now) => new()
    {
        Id = ServiceBusPlatformId, EnvironmentId = environmentId, Name = "M2LB DEV Service Bus", Kind = IntegrationKind.ServiceBus, Enabled = true,
        Region = "nwe", Namespace = ServiceBusNamespace, NamespaceFqdn = $"{ServiceBusNamespace}.servicebus.windows.net", ResourceGroup = "rg-m2lb-dev-shared-nwe",
        TechnicalOwner = "platform-team", MonitoringProvider = "Application Insights", ProducerTechnology = "Wolverine and Azure.Messaging.ServiceBus",
        ProducerAuthentication = IntegrationAuthMechanism.ManagedIdentity, DefaultConsumerAuthentication = IntegrationAuthMechanism.ManagedIdentity,
        ServiceBusTopology = ServiceBusTopology(), Origin = IntegrationRecordOrigin.Seed, UpdatedAt = now,
    };
}
