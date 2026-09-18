namespace BirkNext.Api.Services.IntegrationQuality;

/// <summary>
/// A reusable, logical M2LB integration: what it is, who produces it and who consumes it.
///
/// A template is knowledge, not configuration. "Person CDC" is the same logical integration in
/// Development, QA and Production — what differs between them is the hub name, the namespace and
/// the consumer group, and those live in <see cref="KnownIntegrationEnvironmentBinding"/> rather
/// than here. Nothing on this record may be environment-specific, because a template that carried
/// a QA hub name would stop being reusable and would tempt callers into deriving a DEV name from it.
///
/// A template becomes a real integration only when a person selects it and supplies whatever
/// structural values their environment needs. Templates are never applied automatically and never
/// re-applied over an edited value.
/// </summary>
public sealed record KnownIntegrationTemplate
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required IntegrationType IntegrationType { get; init; }
    public required IntegrationResourceKind ResourceKind { get; init; }

    /// <summary>Null unless the audited source proved a single producing service.</summary>
    public string? SuggestedProducer { get; init; }

    /// <summary>Null unless the audited source proved a single consuming service.</summary>
    public string? SuggestedConsumer { get; init; }

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
}

/// <summary>
/// The environment-specific structural values of one template in one environment.
///
/// A binding exists only where an authoritative value was established. There is no rule that
/// generates one environment's values from another's: "m2lb-cdc-qa.birk.dbo.person" is evidence
/// about QA and says nothing about what the DEV hub is called. An absent binding, or a null field
/// within one, means the value is unknown — which the UI must state and the user must supply.
/// </summary>
public sealed record KnownIntegrationEnvironmentBinding
{
    public required string TemplateId { get; init; }

    /// <summary>Normalised environment type ("Development", "QA", "Production"), never a profile display name.</summary>
    public required string EnvironmentType { get; init; }

    /// <summary>The topic, queue or event hub name in this environment.</summary>
    public string? Resource { get; init; }

    /// <summary>Namespace or fully qualified host. Null wherever the audited source did not state one.</summary>
    public string? EndpointOrNamespace { get; init; }

    /// <summary>Event Hub consumer group. Null for Service Bus, which has subscriptions instead.</summary>
    public string? ConsumerGroup { get; init; }
}

/// <summary>
/// One template as it applies to one environment: the reusable definition, whatever binding that
/// environment has, and which structural fields are still missing.
/// </summary>
public sealed record KnownIntegrationTemplateView
{
    public required KnownIntegrationTemplate Template { get; init; }
    public required string EnvironmentType { get; init; }

    /// <summary>Null when this environment has no evidenced values at all for the template.</summary>
    public KnownIntegrationEnvironmentBinding? Binding { get; init; }

    /// <summary>Structural fields this integration type needs before it can carry a stable identity.</summary>
    public required IReadOnlyList<string> RequiredFields { get; init; }

    /// <summary>The subset of <see cref="RequiredFields"/> this environment has no value for.</summary>
    public required IReadOnlyList<string> MissingRequiredFields { get; init; }

    /// <summary>
    /// Materialises the template as a normal integration for this environment. Provenance is
    /// recorded as CodeSuggested; unknown optional values stay empty so the form shows them as
    /// still needing input.
    ///
    /// Returns null while a required structural value is missing: an integration without its
    /// structural identity would carry a baseline key that matches nothing and could never be
    /// reconciled with a later, complete record.
    /// </summary>
    public IntegrationConfigDto? ToIntegration(string id) =>
        MissingRequiredFields.Count > 0
            ? null
            : new IntegrationConfigDto
            {
                Id = id,
                Name = Template.DisplayName,
                Type = Template.IntegrationType,
                ResourceKind = Template.ResourceKind,
                Endpoint = Binding?.EndpointOrNamespace,
                Resource = Binding?.Resource,
                Consumer = Binding?.ConsumerGroup,
                LogicalProducerService = Template.SuggestedProducer,
                LogicalConsumerService = Template.SuggestedConsumer,
                ConfigurationSource = IntegrationConfigurationSource.CodeSuggested,
                Enabled = true,
            };
}

/// <summary>
/// Catalogue of integrations known from an external audit of the M2LB source.
///
/// Two rules govern everything here. Only values actually present in the audited source are
/// listed: a resource whose consuming service was not established keeps a null producer and
/// consumer rather than one inferred from its name. And only QA has bindings, because only QA
/// resource names were evidenced — a DEV or PROD name produced by substituting "qa" would be an
/// invention, not a suggestion.
///
/// The templates themselves are environment-independent and are therefore offered everywhere. An
/// environment without bindings does not lose the catalogue; it gains a set of values to supply.
///
/// Namespaces and subscription names were not established by the audit and are absent throughout.
/// Event Hub consumer groups are populated for QA: the audited source shows service consumers
/// configured through EventHub:ConsumerGroup with "$Default". That was read in the QA context, so
/// it is recorded as a QA binding rather than as a reusable property of the template.
/// </summary>
public static class KnownIntegrationTemplates
{
    public const string SuggestionOriginLabel = "Suggested from audited M2LB source";

    /// <summary>The only environment with evidenced binding values.</summary>
    public const string QaEnvironmentType = "QA";

    /// <summary>
    /// The consumer group the audited source shows service consumers configured with, through
    /// EventHub:ConsumerGroup.
    ///
    /// One local HendelseAdapter configuration uses the lower-case "$default". Azure treats the
    /// name case-insensitively, but the audited majority spelling is used here and the casing
    /// difference is left for that service to align rather than being mirrored into suggestions.
    ///
    /// This is distinct from the emulator's ConsumerGroups: [] setting, which describes emulator
    /// topology rather than how a service consumer is configured, and is not represented here.
    /// </summary>
    public const string AuditedEventHubConsumerGroup = "$Default";

    /// <summary>The structural field name for a messaging hub, queue or topic.</summary>
    public const string ResourceField = "Resource";

    /// <summary>The structural field name for an HTTP endpoint.</summary>
    public const string EndpointField = "Endpoint";

    private static readonly IReadOnlyList<KnownIntegrationTemplate> Definitions = BuildTemplates();
    private static readonly IReadOnlyList<KnownIntegrationEnvironmentBinding> EnvironmentBindings = BuildQaBindings();

    /// <summary>Every reusable template, in any environment. Never filtered by environment.</summary>
    public static IReadOnlyList<KnownIntegrationTemplate> All => Definitions;

    public static KnownIntegrationTemplate? ById(string id) =>
        Definitions.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// The binding for one template in one environment, or null when that environment has no
    /// evidenced values. No value is ever adapted from another environment's binding.
    /// </summary>
    public static KnownIntegrationEnvironmentBinding? BindingFor(string templateId, string? environmentType) =>
        string.IsNullOrWhiteSpace(environmentType)
            ? null
            : EnvironmentBindings.FirstOrDefault(b =>
                string.Equals(b.TemplateId, templateId, StringComparison.Ordinal) &&
                string.Equals(b.EnvironmentType, environmentType.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The whole catalogue resolved for one environment. Every template is present whatever the
    /// environment is; only the binding varies, and a missing value is reported as missing.
    /// </summary>
    public static IReadOnlyList<KnownIntegrationTemplateView> ForEnvironment(string? environmentType)
    {
        var environment = (environmentType ?? "").Trim();
        return Definitions.Select(template =>
        {
            var binding = BindingFor(template.Id, environment);
            var required = RequiredFieldsFor(template.IntegrationType);
            var missing = required.Where(field => field switch
            {
                ResourceField => string.IsNullOrWhiteSpace(binding?.Resource),
                EndpointField => string.IsNullOrWhiteSpace(binding?.EndpointOrNamespace),
                _ => false,
            }).ToList();

            return new KnownIntegrationTemplateView
            {
                Template = template,
                EnvironmentType = environment,
                Binding = binding,
                RequiredFields = required,
                MissingRequiredFields = missing,
            };
        }).ToList();
    }

    /// <summary>
    /// The structural fields an integration of this type needs before it can carry a stable
    /// baseline identity. Messaging is identified by its hub/queue/topic; HTTP by its endpoint.
    /// The namespace is deliberately NOT required: the audit never established one, and demanding
    /// it would make every QA template unusable for a value nothing depends on structurally.
    /// </summary>
    public static IReadOnlyList<string> RequiredFieldsFor(IntegrationType type) => type switch
    {
        IntegrationType.REST or IntegrationType.GraphQL => [EndpointField],
        _ => [ResourceField],
    };

    private static List<KnownIntegrationTemplate> BuildTemplates()
    {
        var templates = new List<KnownIntegrationTemplate>();

        // ── Event Hubs: BiRK change data capture ─────────────────────────────
        // Consuming services come from the audit, per hub. Only the person hub also had its
        // producing service established; the others keep a null producer rather than one inferred
        // from "the data originates from BiRK CDC", which would be reasoning about the data's
        // origin rather than evidence of a configured producer.
        templates.Add(EventHub("eh-person", "Person CDC", producer: "BiRK / Debezium", consumer: "PersonBiRKAdapter"));

        foreach (var (slug, display, consumer) in new[]
                 {
                     ("barn", "Barn CDC", "PersonBiRKAdapter"),
                     ("tiltak", "Tiltak CDC", "Tjeneste API"),
                     ("bestilling", "Bestilling CDC", "Tjeneste API"),
                     ("tjenestetype", "TjenesteType CDC", "Tjeneste API"),
                     ("tiltaksstatustype", "TiltaksStatusType CDC", "Tjeneste API"),
                     ("avslutningsgrunntype", "AvslutningsGrunnType CDC", "Tjeneste API"),
                     ("tvangsprotokoll", "Tvangsprotokoll CDC", "Hendelse BiRK Adapter"),
                     ("romning", "Rømning CDC", "Hendelse BiRK Adapter"),
                 })
        {
            templates.Add(EventHub($"eh-{slug}", display, consumer: consumer));
        }

        // ── Service Bus queues ───────────────────────────────────────────────
        // Leselogg is written to by several services. IntegrationConfigDto models one
        // LogicalProducerService, so a composite string would misrepresent a single service
        // identity; the producer stays unknown and the relationship is described in a note.
        templates.Add(ServiceBus("sb-leselogg", "Leselogg", IntegrationResourceKind.ServiceBusQueue,
            consumer: "Revisjon",
            relationshipNote: "Written to by several services; the audited source did not identify a single producing service."));

        templates.Add(ServiceBus("sb-operasjonsregistrering", "Operasjonsregistrering", IntegrationResourceKind.ServiceBusQueue));
        templates.Add(ServiceBus("sb-birk-adapter-errors", "BiRK Adapter Errors", IntegrationResourceKind.ServiceBusQueue));
        templates.Add(ServiceBus("sb-operatorkontroll-varsler", "Operatørkontroll Varsler", IntegrationResourceKind.ServiceBusQueue));

        // ── Service Bus topics ───────────────────────────────────────────────
        templates.Add(ServiceBus("sb-hendelser-barn", "Hendelser Barn", IntegrationResourceKind.ServiceBusTopic));
        templates.Add(ServiceBus("sb-tjeneste-tjenester", "Tjeneste Tjenester", IntegrationResourceKind.ServiceBusTopic));

        foreach (var (slug, display) in new[]
                 {
                     ("operasjoner", "Autorisasjon Operasjoner"),
                     ("roller", "Autorisasjon Roller"),
                     ("tilganger", "Autorisasjon Tilganger"),
                     ("nodtilganger", "Autorisasjon Nødtilganger"),
                     ("organisasjon", "Autorisasjon Organisasjon"),
                 })
        {
            templates.Add(ServiceBus($"sb-autorisasjon-{slug}", display, IntegrationResourceKind.ServiceBusTopic));
        }

        return templates;
    }

    /// <summary>
    /// The QA values the audit established. These are the ONLY bindings in the catalogue. Adding a
    /// DEV or PROD entry requires real deployment evidence, never a string substitution on these.
    ///
    /// BirkNext's own SampleData/hendelsestjenesten names the Leselogg queue "revisjon.leselogg".
    /// The audited M2LB source identifies it as "leselogg", and sample data is not authoritative
    /// deployment evidence, so the audited value stands. The two are deliberately not reconciled
    /// automatically: the resource name is part of structural identity, so changing it would move
    /// the baseline key and detach existing history.
    /// </summary>
    private static List<KnownIntegrationEnvironmentBinding> BuildQaBindings()
    {
        var bindings = new List<KnownIntegrationEnvironmentBinding>();

        foreach (var (templateId, resource) in new[]
                 {
                     ("eh-person", "m2lb-cdc-qa.birk.dbo.person"),
                     ("eh-barn", "m2lb-cdc-qa.birk.dbo.barn"),
                     ("eh-tiltak", "m2lb-cdc-qa.birk.dbo.tiltak"),
                     ("eh-bestilling", "m2lb-cdc-qa.birk.dbo.bestilling"),
                     ("eh-tjenestetype", "m2lb-cdc-qa.birk.dbo.tjenesteType"),
                     ("eh-tiltaksstatustype", "m2lb-cdc-qa.birk.dbo.tiltaksStatusType"),
                     ("eh-avslutningsgrunntype", "m2lb-cdc-qa.birk.dbo.avslutningsGrunnType"),
                     ("eh-tvangsprotokoll", "m2lb-cdc-qa.birk.dbo.tvangsprotokoll"),
                     ("eh-romning", "m2lb-cdc-qa.birk.dbo.romning"),
                 })
        {
            // Namespace was not established by the audit and stays null.
            bindings.Add(new KnownIntegrationEnvironmentBinding
            {
                TemplateId = templateId,
                EnvironmentType = QaEnvironmentType,
                Resource = resource,
                ConsumerGroup = AuditedEventHubConsumerGroup,
            });
        }

        foreach (var (templateId, resource) in new[]
                 {
                     ("sb-leselogg", "leselogg"),
                     ("sb-operasjonsregistrering", "operasjonsregistrering"),
                     ("sb-birk-adapter-errors", "birk-adapter-errors"),
                     ("sb-operatorkontroll-varsler", "operatorkontroll.varsler"),
                     ("sb-hendelser-barn", "hendelser.barn"),
                     ("sb-tjeneste-tjenester", "tjeneste.tjenester"),
                     ("sb-autorisasjon-operasjoner", "autorisasjon.operasjoner"),
                     ("sb-autorisasjon-roller", "autorisasjon.roller"),
                     ("sb-autorisasjon-tilganger", "autorisasjon.tilganger"),
                     ("sb-autorisasjon-nodtilganger", "autorisasjon.nodtilganger"),
                     ("sb-autorisasjon-organisasjon", "autorisasjon.organisasjon"),
                 })
        {
            // Service Bus has subscriptions rather than consumer groups, so no consumer group is
            // carried here. The namespace was not established by the audit and stays null.
            bindings.Add(new KnownIntegrationEnvironmentBinding
            {
                TemplateId = templateId,
                EnvironmentType = QaEnvironmentType,
                Resource = resource,
            });
        }

        return bindings;
    }

    private static KnownIntegrationTemplate EventHub(
        string id,
        string displayName,
        string? producer = null,
        string? consumer = null) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            IntegrationType = IntegrationType.EventHub,
            ResourceKind = IntegrationResourceKind.EventHub,
            SuggestedProducer = producer,
            SuggestedConsumer = consumer,
        };

    private static KnownIntegrationTemplate ServiceBus(
        string id,
        string displayName,
        IntegrationResourceKind kind,
        string? producer = null,
        string? consumer = null,
        string? relationshipNote = null) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            IntegrationType = IntegrationType.ServiceBus,
            ResourceKind = kind,
            SuggestedProducer = producer,
            SuggestedConsumer = consumer,
            RelationshipNote = relationshipNote,
        };
}
