using BirkNext.Api.Services.IntegrationTopology;
using BirkNext.IntegrationTopology;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

/// <summary>
/// Skolenærvær's expectations of the integration topology: the logical systems its journeys involve (with the explicit aliases different
/// sources use for them), the paths its scenarios rely on, the source roles a complete topology needs and the relationships it expects.
/// Expectations only — the generic resolver resolves a relationship from configured, discovered or observed evidence, and this profile adds
/// no connection, interface or address. Archive file names never appear here: roles are recognized by content.
/// </summary>
public sealed class SkoleNaervaerTopologyProfile : ITopologyProfile
{
    public const string ProfileId = "skolenaervaer";
    private const string Docs = "docs/skolenaervaer-cross-repo-verification.md §1";

    public string Id => ProfileId;
    public string Label => "Skolenærvær";

    public IReadOnlyList<TopologyExpectedComponent> Components { get; } =
    [
        new("Altinn", ["Altinn (TT02)", "External reporting party (Altinn)"], "Reports are submitted through the Altinn app."),
        new("MU", ["Meldingsutveksleren", "Buf.Meldingsutveksler.Service"], $"Receives the report from Altinn and hands it to MM ({Docs})."),
        new("MM", ["Meldingsutvekslermottak", "Skolenærværsmottaket", "M2LB.Meldingsutvekslermottak.Host"], $"Validates and maps the report and calls Skoletjenesten ({Docs})."),
        new("Skoletjenesten", ["Skole", "Skolenærværsrapport", "Skolenaervaersrapport", "M2LB.Skole.Api"], $"Receives reports and education data ({Docs})."),
        new("Person", ["M2LB.Person.Api"], $"Publishes person events Skoletjenesten consumes ({Docs})."),
        new("SkoleAdapter", ["M2LB.Skole.BiRK.Worker"], $"Forwards education data from BiRK to Skoletjenesten ({Docs})."),
        new("Bufdata", [], $"Receives the completed dataset after period lock; no delivery exists yet (ADR-028, {Docs})."),
    ];

    public IReadOnlyList<TopologyExpectedPath> Paths { get; } =
    [
        new("rapportmottak", "Rapportmottak (normal report)",
        [
            new("Altinn → MU", "Altinn", "MU"),
            new("MU → MM", "MU", "MM"),
            new("MM → Skoletjenesten", "MM", "Skoletjenesten"),
            new("Skoletjenesten → Bufdata", "Skoletjenesten", "Bufdata"),
        ]),
        new("utdanningsdata", "Utdanningsdata (education data)", [new("SkoleAdapter → Skoletjenesten", "SkoleAdapter", "Skoletjenesten")]),
        new("person", "Person events", [new("Person → Skoletjenesten", "Person", "Skoletjenesten")]),
    ];

    public const string Mu = "mu", M2lb = "m2lb", Iac = "iac", Birk = "birk", BufdataRole = "bufdata";

    public IReadOnlyList<TopologySourceRoleDefinition> SourceRoles { get; } =
    [
        new(Mu, "Meldingsutveksleren (MU)", TopologySourceRequirement.Required)
        {
            Match = new() { Components = ["Meldingsutveksler.Service"] },
            Capabilities = [TopologyCapabilities.ApplicationSource, TopologyCapabilities.HttpServer, TopologyCapabilities.ServiceBus, TopologyCapabilities.ApplicationMetadata],
            Relationships = ["altinn-mu", "mu-mm"],
            Guidance = "Application source of Meldingsutveksleren: the Altinn webhook subscription, outbound messaging and channel routing.",
            Consequence = "Altinn → MU and MU → MM cannot be resolved.",
        },
        new(M2lb, "M2LB", TopologySourceRequirement.Required)
        {
            Match = new() { Components = ["Meldingsutvekslermottak", "Skole.Api", "Skole.BiRK"] },
            Capabilities = [TopologyCapabilities.ApplicationSource, TopologyCapabilities.HttpClient, TopologyCapabilities.HttpServer, TopologyCapabilities.ServiceBus, TopologyCapabilities.EventHub],
            Relationships = ["mu-mm", "mm-skoletjenesten", "eventhub-skoleadapter", "skoleadapter-skoletjenesten", "rapport-servicebus"],
            Guidance = "Application source of Meldingsutvekslermottak (MM), Skoletjenesten and SkoleAdapter: clients, consumers and producers.",
            Consequence = "MM, Skoletjenesten and SkoleAdapter relationships cannot be resolved.",
        },
        new(Iac, "Infrastructure as Code", TopologySourceRequirement.Recommended)
        {
            Match = new() { InfrastructureFormats = [InfrastructureFormat.Terraform, InfrastructureFormat.Bicep, InfrastructureFormat.Arm] },
            Capabilities = [TopologyCapabilities.Terraform, TopologyCapabilities.EventHub, TopologyCapabilities.ServiceBus, TopologyCapabilities.Cdc],
            Relationships = ["debezium-eventhub", "mm-skoletjenesten", "skoleadapter-skoletjenesten"],
            Guidance = "Infrastructure source: Debezium/Kafka Connect, Event Hubs, Service Bus, container app bindings and address injections.",
            Consequence = "Infrastructure topology (CDC connector, Event Hubs, module address injections) cannot be resolved.",
        },
        new(SkoleNaervaerJourneyPack.AltinnAppSourceRole, "Altinn application/API", TopologySourceRequirement.BuiltInReference)
        {
            Capabilities = [TopologyCapabilities.Reference],
            Relationships = ["altinn-mu"],
            Guidance = "Shipped with BirkNext: app identity, environments, data type and process task. No import is required.",
        },
        new(Birk, "BiRK application source", TopologySourceRequirement.Conditional)
        {
            Match = new() { ComponentPrefixes = ["Birk."] },
            Capabilities = [TopologyCapabilities.ApplicationSource],
            Relationships = ["birk-cdc"],
            Guidance = "Needed only to prove the source application side of the CDC flow (BiRK application → captured tables → Debezium).",
            Consequence = "BiRK → CDC producer relationship may remain unresolved.",
        },
        new(BufdataRole, "Bufdata consumer evidence", TopologySourceRequirement.Conditional)
        {
            Match = new() { Components = ["Bufdata"] },
            Capabilities = [TopologyCapabilities.ApplicationSource, TopologyCapabilities.ServiceBus],
            Relationships = ["servicebus-bufdata"],
            Guidance = "Needed only to prove the concrete Bufdata consumer of the Service Bus delivery (source, IaC or consumer metadata).",
            Consequence = "Service Bus → Bufdata may remain unresolved.",
        },
    ];

    public IReadOnlyList<TopologyExpectedRelationship> Relationships { get; } =
    [
        new("altinn-mu", "Altinn → MU", "Altinn", "MU", "External application (webhook)", TopologyDimensions.Application)
        {
            EvidenceRoles = [Mu, SkoleNaervaerJourneyPack.AltinnAppSourceRole],
            MissingEvidence = "MU configuration that subscribes to the Skolenærvær Altinn app (source filter or app id) with its own callback endpoint.",
        },
        new("mu-mm", "MU → MM", "MU", "MM", "Service Bus", TopologyDimensions.Messaging)
        {
            EvidenceRoles = [Mu, M2lb],
            MissingEvidence = "An MU outbound queue/channel for Skolenærvær that matches the inbound queue MM consumes.",
        },
        new("mm-skoletjenesten", "MM → Skoletjenesten", "MM", "Skoletjenesten", "HTTP", TopologyDimensions.Application)
        {
            EvidenceRoles = [M2lb, Iac],
            MissingEvidence = "A concrete base address for MM's Skoletjenesten client (environment configuration, deployment settings or an IaC address injection).",
        },
        new("birk-cdc", "BiRK → CDC (Debezium)", "role:" + Birk, "resource:connector", "CDC", TopologyDimensions.Infrastructure)
        {
            EvidenceRoles = [Birk],
            MissingEvidence = "BiRK application/source-side evidence that writes the captured tables (data model or an explicit CDC source).",
        },
        new("debezium-eventhub", "Debezium → Event Hub", "resource:connector", "resource:eventhub", "Event Hub", TopologyDimensions.Infrastructure)
        {
            EvidenceRoles = [Iac, M2lb],
            MissingEvidence = "The connector's topic prefix (IaC) and Event Hub names that follow it.",
        },
        new("eventhub-skoleadapter", "Event Hub → SkoleAdapter", "resource:eventhub", "SkoleAdapter", "Event Hub", TopologyDimensions.Messaging)
        {
            EvidenceRoles = [M2lb, Iac],
            MissingEvidence = "SkoleAdapter consumer configuration naming the CDC Event Hubs.",
        },
        new("skoleadapter-skoletjenesten", "SkoleAdapter → Skoletjenesten", "SkoleAdapter", "Skoletjenesten", "HTTP", TopologyDimensions.Application)
        {
            EvidenceRoles = [M2lb, Iac],
            MissingEvidence = "A base address for SkoleAdapter's Skoletjenesten client that matches where Skoletjenesten is deployed or launched.",
        },
        new("rapport-servicebus", "Skolenærværsrapport → Service Bus", "Skoletjenesten", "resource:servicebus", "Service Bus", TopologyDimensions.Messaging)
        {
            EvidenceRoles = [M2lb],
            MissingEvidence = "A named Service Bus entity Skoletjenesten publishes reports to.",
        },
        new("servicebus-bufdata", "Service Bus → Bufdata", "resource:servicebus", "role:" + BufdataRole, "Service Bus", TopologyDimensions.ExternalConsumer)
        {
            Via = "Skoletjenesten",
            EvidenceRoles = [BufdataRole],
            MissingEvidence = "A concrete Bufdata consumer (subscription or consumer configuration) of a Service Bus entity Skoletjenesten produces.",
        },
    ];

    public IReadOnlyList<TopologyExternalReference> References { get; } =
        [new(SkoleNaervaerJourneyPack.AltinnAppSourceRole, "Altinn", LoadAltinnReference)];

    /// <summary>The app identity from the shipped reference (identifier + app base URLs); nothing is fetched.</summary>
    private static TopologyReferenceFacts? LoadAltinnReference()
    {
        var (view, loaded) = TestPackReferences.Load(SkoleNaervaerAltinnReference.Reference(SkoleNaervaerJourneyPack.AltinnAppSourceRole));
        if (!loaded) return null;
        var identifiers = view.Facts.Where(fact => fact.SemanticKey == "Application.Route").Select(fact => fact.Value).ToList();
        var addresses = view.Facts.Where(fact => fact.SemanticKey == "Application.BaseUrl").Select(fact => fact.Value).ToList();
        return new(identifiers, addresses, $"App {string.Join(", ", identifiers)} (Test Pack reference).");
    }
}
