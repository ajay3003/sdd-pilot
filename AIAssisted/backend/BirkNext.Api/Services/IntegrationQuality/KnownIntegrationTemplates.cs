namespace BirkNext.Api.Services.IntegrationQuality;

/// <summary>
/// A pre-filled starting point for an integration, offered when adding one to a Target
/// Environment.
///
/// A template is not configuration. It becomes a real integration only when a person selects it,
/// and every value it supplies stays editable afterwards. Templates are never applied
/// automatically and are never re-applied over an edited value.
/// </summary>
public sealed record KnownIntegrationTemplate
{
    public required string Id { get; init; }

    /// <summary>Environment this template is evidenced for. A template is offered only there.</summary>
    public required string EnvironmentName { get; init; }

    public required string DisplayName { get; init; }
    public required IntegrationType IntegrationType { get; init; }
    public required IntegrationResourceKind ResourceKind { get; init; }

    /// <summary>The topic, queue or event hub name.</summary>
    public required string Resource { get; init; }

    /// <summary>
    /// Namespace or fully qualified host. Null wherever the audited source did not state one; it
    /// is never guessed from the resource name.
    /// </summary>
    public string? EndpointOrNamespace { get; init; }

    /// <summary>Null unless the audited source proved a single producing service.</summary>
    public string? SuggestedProducer { get; init; }

    /// <summary>Null unless the audited source proved a single consuming service.</summary>
    public string? SuggestedConsumer { get; init; }

    /// <summary>Null unless an actual value was found. Never defaulted to "$Default".</summary>
    public string? SuggestedConsumerGroup { get; init; }

    /// <summary>
    /// Free-text note for relationships the single-service fields cannot express, such as a
    /// queue written to by several services. Descriptive only; never parsed.
    /// </summary>
    public string? RelationshipNote { get; init; }

    /// <summary>
    /// Where the suggestion came from, in words suitable for the UI. These values were taken from
    /// an external audit of the M2LB source, not from this repository and not from a running
    /// environment, so the wording claims a source reading rather than verification.
    /// </summary>
    public string SuggestionOrigin { get; init; } = KnownIntegrationTemplates.SuggestionOriginLabel;

    /// <summary>
    /// Materialises the template as a normal integration. Provenance is recorded as
    /// CodeSuggested; unknown values stay empty so the form shows them as still needing input.
    /// </summary>
    public IntegrationConfigDto ToIntegration(string id) => new()
    {
        Id = id,
        Name = DisplayName,
        Type = IntegrationType,
        ResourceKind = ResourceKind,
        Endpoint = EndpointOrNamespace,
        Resource = Resource,
        Consumer = SuggestedConsumerGroup,
        LogicalProducerService = SuggestedProducer,
        LogicalConsumerService = SuggestedConsumer,
        ConfigurationSource = IntegrationConfigurationSource.CodeSuggested,
        Enabled = true
    };
}

/// <summary>
/// Catalogue of integrations known from an external audit of the M2LB source.
///
/// Two rules govern everything here. Only values actually present in the audited source are
/// listed: a resource whose consuming service was not established keeps a null producer and
/// consumer rather than one inferred from its name. And only QA is populated, because only QA
/// resource names were evidenced — a DEV or PROD name produced by substituting "qa" would be an
/// invention, not a suggestion.
///
/// Namespaces, consumer groups and subscription names were not established by the audit and are
/// therefore absent throughout.
/// </summary>
public static class KnownIntegrationTemplates
{
    public const string SuggestionOriginLabel = "Suggested from audited M2LB source";

    /// <summary>The only environment with evidenced values.</summary>
    public const string QaEnvironmentName = "QA";

    private static readonly IReadOnlyList<KnownIntegrationTemplate> All = BuildCatalogue();

    public static IReadOnlyList<KnownIntegrationTemplate> ForEnvironment(string? environmentName) =>
        string.IsNullOrWhiteSpace(environmentName)
            ? []
            : All.Where(t => string.Equals(t.EnvironmentName, environmentName.Trim(),
                                           StringComparison.OrdinalIgnoreCase))
                 .ToList();

    public static KnownIntegrationTemplate? ById(string id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    private static List<KnownIntegrationTemplate> BuildCatalogue()
    {
        var templates = new List<KnownIntegrationTemplate>();

        // ── QA Event Hubs: BiRK change data capture ──────────────────────────
        // Only the person hub had its producing and consuming services established by the audit.
        // The rest carry the hub name alone; naming a consumer for them by analogy with
        // "person -> PersonBiRKAdapter" would be inference from a resource name, not evidence.
        templates.Add(EventHub(
            id: "qa-eh-person",
            displayName: "BiRK Person CDC",
            resource: "m2lb-cdc-qa.birk.dbo.person",
            producer: "BiRK / Debezium",
            consumer: "PersonBiRKAdapter"));

        foreach (var (slug, display, resource) in new[]
                 {
                     ("barn", "BiRK Barn CDC", "m2lb-cdc-qa.birk.dbo.barn"),
                     ("tiltak", "BiRK Tiltak CDC", "m2lb-cdc-qa.birk.dbo.tiltak"),
                     ("bestilling", "BiRK Bestilling CDC", "m2lb-cdc-qa.birk.dbo.bestilling"),
                     ("tjenestetype", "BiRK TjenesteType CDC", "m2lb-cdc-qa.birk.dbo.tjenesteType"),
                     ("tiltaksstatustype", "BiRK TiltaksStatusType CDC", "m2lb-cdc-qa.birk.dbo.tiltaksStatusType"),
                     ("avslutningsgrunntype", "BiRK AvslutningsGrunnType CDC", "m2lb-cdc-qa.birk.dbo.avslutningsGrunnType"),
                     ("tvangsprotokoll", "BiRK Tvangsprotokoll CDC", "m2lb-cdc-qa.birk.dbo.tvangsprotokoll"),
                     ("romning", "BiRK Rømning CDC", "m2lb-cdc-qa.birk.dbo.romning")
                 })
        {
            templates.Add(EventHub($"qa-eh-{slug}", display, resource));
        }

        // ── QA Service Bus queues ────────────────────────────────────────────
        // Leselogg is written to by several services. IntegrationConfigDto models one
        // LogicalProducerService, so a composite string would misrepresent a single service
        // identity; the producer stays unknown and the relationship is described in a note.
        templates.Add(ServiceBus(
            id: "qa-sb-leselogg",
            displayName: "Leselogg",
            resource: "leselogg",
            kind: IntegrationResourceKind.ServiceBusQueue,
            consumer: "Revisjon",
            relationshipNote: "Written to by several services; the audited source did not identify a single producing service."));

        templates.Add(ServiceBus("qa-sb-operasjonsregistrering", "Operasjonsregistrering",
            "operasjonsregistrering", IntegrationResourceKind.ServiceBusQueue));

        templates.Add(ServiceBus("qa-sb-birk-adapter-errors", "BiRK Adapter Errors",
            "birk-adapter-errors", IntegrationResourceKind.ServiceBusQueue));

        templates.Add(ServiceBus("qa-sb-operatorkontroll-varsler", "Operatørkontroll Varsler",
            "operatorkontroll.varsler", IntegrationResourceKind.ServiceBusQueue));

        // ── QA Service Bus topics ────────────────────────────────────────────
        templates.Add(ServiceBus("qa-sb-hendelser-barn", "Hendelser Barn",
            "hendelser.barn", IntegrationResourceKind.ServiceBusTopic));

        templates.Add(ServiceBus("qa-sb-tjeneste-tjenester", "Tjeneste Tjenester",
            "tjeneste.tjenester", IntegrationResourceKind.ServiceBusTopic));

        foreach (var (slug, display, resource) in new[]
                 {
                     ("operasjoner", "Autorisasjon Operasjoner", "autorisasjon.operasjoner"),
                     ("roller", "Autorisasjon Roller", "autorisasjon.roller"),
                     ("tilganger", "Autorisasjon Tilganger", "autorisasjon.tilganger"),
                     ("nodtilganger", "Autorisasjon Nødtilganger", "autorisasjon.nodtilganger"),
                     ("organisasjon", "Autorisasjon Organisasjon", "autorisasjon.organisasjon")
                 })
        {
            templates.Add(ServiceBus($"qa-sb-autorisasjon-{slug}", display, resource,
                IntegrationResourceKind.ServiceBusTopic));
        }

        return templates;
    }

    private static KnownIntegrationTemplate EventHub(
        string id,
        string displayName,
        string resource,
        string? producer = null,
        string? consumer = null) =>
        new()
        {
            Id = id,
            EnvironmentName = QaEnvironmentName,
            DisplayName = displayName,
            IntegrationType = IntegrationType.EventHub,
            ResourceKind = IntegrationResourceKind.EventHub,
            Resource = resource,
            SuggestedProducer = producer,
            SuggestedConsumer = consumer
            // Namespace and consumer group were not established by the audit and stay null.
        };

    private static KnownIntegrationTemplate ServiceBus(
        string id,
        string displayName,
        string resource,
        IntegrationResourceKind kind,
        string? producer = null,
        string? consumer = null,
        string? relationshipNote = null) =>
        new()
        {
            Id = id,
            EnvironmentName = QaEnvironmentName,
            DisplayName = displayName,
            IntegrationType = IntegrationType.ServiceBus,
            ResourceKind = kind,
            Resource = resource,
            SuggestedProducer = producer,
            SuggestedConsumer = consumer,
            RelationshipNote = relationshipNote
            // Namespace and subscription name were not established by the audit and stay null.
        };
}
