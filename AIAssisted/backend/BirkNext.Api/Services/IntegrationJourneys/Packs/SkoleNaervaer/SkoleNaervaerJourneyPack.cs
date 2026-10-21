using BirkNext.Api.Services.ActiveEventTesting.Providers.SkoleNaervaer;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.ContractEvolution;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

/// <summary>
/// Skolenærvær Testing journey pack. Three journeys from the approved architecture diagram:
/// <list type="bullet">
/// <item>Utdanningsdata — BiRK → Debezium → Event Hub → SkoleAdapter → Utdanning. Executed by the existing Active Event provider
/// (<see cref="SkoleNaervaerScenarioProvider"/>); this pack only summarizes it.</item>
/// <item>Rapportmottak — external reporter → Altinn → MU → MM → Skolenærværsrapport.</item>
/// <item>Periodeavslutning — Skolenærværsrapport → Service Bus (SkolenaervaersperiodeLukket) → Bufdata.</item>
/// </list>
/// Integration configuration comes only from Integration Quality Review (the integration catalog and its message-flow test configuration);
/// this pack defines no endpoint, namespace, topic, consumer group, authentication or environment. No MU, MM, Skolenærværsrapport, Bufdata or
/// Altinn source, contract or read surface exists in the evidence BirkNext has, so those journeys are modeled truthfully as not ready.
/// </summary>
public sealed class SkoleNaervaerJourneyPack(IIntegrationMessageFlowStore messageFlows, AltinnTestingPolicy altinnPolicy, AltinnAppContractService altinnContracts,
    SkoleNaervaerMessageEvidence? messageEvidence = null, BirkNext.Api.Services.TestData.TestDataCatalog? testDataCatalog = null) : IIntegrationJourneyPack
{
    public const string Id = "skolenaervaer";
    public const string UtdanningsdataId = "utdanningsdata";
    public const string RapportmottakId = "rapportmottak";
    public const string PeriodeavslutningId = "periodeavslutning";
    public const string PeriodClosedEvent = "SkolenaervaersperiodeLukket";
    /// <summary>Read-only observation of an existing Altinn instance (Altinn Storage GET); sends and changes nothing.</summary>
    public const string InstanceEvidenceScenario = "instance-evidence";
    /// <summary>Read-only verification of one existing message from recorded and telemetry evidence (nothing is sent).</summary>
    public const string MessageVerificationScenario = "message-verification";
    /// <summary>The scenarios that create and submit a report; only these need the submission contract and the test-data references.</summary>
    public static readonly IReadOnlyList<string> SubmissionScenarios = ["valid-report", "person-not-found", "overlapping-report"];
    /// <summary>Scenarios that call Altinn (and so need reporter authentication and the approved app); a message verification calls nothing.</summary>
    public static readonly IReadOnlyList<string> AltinnCallingScenarios = [.. SubmissionScenarios, InstanceEvidenceScenario];

    /// <summary>What a confirmed submission run does, stated in the run confirmation before anything is sent.</summary>
    public static readonly IReadOnlyList<string> SubmissionEffects =
    [
        "This will make a real external call to Altinn TT02 (ID-porten TEST for the reporter's sign-in). Production is never called.",
        "Authenticate the selected synthetic reporter unattended.",
        "Create one new instance.",
        "Upload the typed form data.",
        "Validate the instance.",
        "Make at most one process transition (process/next), only if the live instance is at the expected task and validation passed.",
        "Read the resulting receipt/status.",
        "No automatic retry and no automatic second process transition.",
    ];

    private const string EducationCdc = "education-cdc";
    private const string ReportIntake = "report-intake";
    private const string PeriodClosed = "period-closed";
    private const string DiagramBasis = "Approved Skolenærvær architecture diagram and the SkoleAdapter/Skolenærvær specifications (external, documented). Not source verified.";

    /// <summary>How Skoletjenesten locks a period, from a review of its source (not runtime verified; see docs/skolenaervaer-cross-repo-verification.md).</summary>
    internal const string SkoleLockLifecycle =
        "Skoletjenesten locks a period on its own schedule (lock planner job; by default 06:00 Europe/Oslo on the 11th of the following month and no July period, configurable in Skolenaervaer:Kalender). " +
        "PUT /api/skole/v1/drift/perioder/{periode}/laastidspunkt only postpones that time, and the only immediate trigger is mapped for local development only (Skole source review; not runtime verified).";

    /// <summary>What Skoletjenesten does with a locked period's delivery today, from a review of its source (not runtime verified).</summary>
    internal const string SkoleDeliveryState =
        "At lock Skoletjenesten prepares delivery rows (status Klargjort) but does not yet write dataset files, mark a delivery Levert or publish SkolenaervaersperiodeLukket (Skole source review, ADR-028 pending; not runtime verified).";

    public string PackId => Id;
    public string PackVersion => "1";
    public string DisplayName => "Skolenærvær Testing";
    public string Description => "Three Skolenærvær journeys: Utdanningsdata (CDC), Rapportmottak (Altinn → MU → MM → Skolenærværsrapport) and Periodeavslutning (Service Bus → Bufdata).";

    /// <summary>Message Verification: the read-only Rapportmottak scenario, offered as the pack's verification view (nothing is sent).</summary>
    public IReadOnlyList<TestPackView> VerificationViews { get; } =
        [new("message-verification", "Message Verification", TestPackViewKind.Verification, RapportmottakId, MessageVerificationScenario,
            "Read-only verification of what happened to one message, from recorded and telemetry evidence.")];

    public IReadOnlyList<TestPackCapabilityRequirement> RequiredEvidenceCapabilities { get; } =
    [
        new("source-architecture", "Source Analysis architecture evidence", "Source Analysis (bound in Integration Quality Review)", "Components and dependency evidence for the architecture rules."),
        new("report-schema", "Report XML Schema", "Source Analysis (bound in Integration Quality Review)", "The submitted report's XSD, read by the shared XSD engine."),
        new("runtime-observation", "Downstream observation", "Integration Quality Review runtime evidence", "MU, MM, Skolenærværsrapport and Bufdata observers; none is available yet."),
    ];

    public IReadOnlyList<TestPackCapabilityRequirement> RequiredTestDataCapabilities { get; } =
    [
        new(SkoleNaervaerTestData.ReporterRole, "Synthetic reporter reference", "Test Data", "An approved, validated synthetic reporter for the target test environment."),
        new(SkoleNaervaerTestData.ChildRole, "Approved synthetic identity", "Test Data", "An approved synthetic person declared known or unknown to Person; never a real identity."),
    ];

    private const string AltinnAppSource = "Altinn Skolenærvær app";
    public const string MuSource = "mu";
    public const string M2lbSource = "m2lb";
    public const string AltinnAppSourceRole = "altinn-app";
    public const string InfrastructureSourceRole = "infrastructure";

    /// <summary>The logical sources Skolenærvær needs. Each is resolved through the IQR binding of its own integrations and recognized by its
    /// components (or, for the Altinn app, by its API document) — archive names are only provenance. No single source contains everything.</summary>
    /// <summary>
    /// Add-integration templates, one per pack integration requirement whose shape the pack knows (it also recognizes the result through
    /// <see cref="Matches"/>). Only deterministic, non-secret values: the report intake webhook is MU's known endpoint (never offered for a
    /// production environment), the period-closed topic is the pack's event name; the CDC Event Hub name and source table vary per environment
    /// and are left for the tester. No MU → MM or MM → Skoletjenesten template: the pack has no integration requirement for them.
    /// </summary>
    public IReadOnlyList<IntegrationTemplateDefinition> IntegrationTemplates { get; } =
    [
        new("skolenaervaer-report-intake", "Skolenærvær – Altinn report intake", "Altinn delivers submitted school-attendance reports to Meldingsutveksleren (MU) through its webhook.", IntegrationKind.HttpApi)
        {
            RequirementKey = ReportIntake, SystemName = "Altinn → Meldingsutveksleren (MU)", Producer = "Altinn", Consumer = "Meldingsutveksleren (MU)",
            SourceSystem = "Altinn", DestinationSystem = "Meldingsutveksleren (MU)",
            EndpointOrTopic = "https://buf-meldings-utveksler.azurewebsites.net/api/altinn/webhook", EvidenceSourceRoleKey = MuSource,
            // The Altinn app reference describes how the report is submitted to the app (scheme, token providers, data type, task); the schema
            // named by the form data type is looked up in MU's own source, which is evidence of what MU holds — not proof of what MM validates.
            Reference = new(AltinnAppSourceRole)
            {
                AuthenticationFacts = ["Authentication scheme", "Security scheme name", "Token providers"],
                ContractFacts = ["Form data type", "Content type", "Process task", "Task name"],
                SchemaFactKey = BirkNext.SourceDomains.SourceConfigurationKeys.ApplicationFormDataType,
            },
        },
        new("skolenaervaer-education-cdc", "Skolenærvær – BiRK CDC → SkoleAdapter", "Debezium captures BiRK education changes and publishes them on Event Hub; SkoleAdapter consumes them.", IntegrationKind.EventHub)
        {
            RequirementKey = EducationCdc, SystemName = "BiRK CDC → SkoleAdapter", Producer = "Debezium", Consumer = "SkoleAdapter",
            SourceSystem = "BiRK", DestinationSystem = "SkoleAdapter", EvidenceSourceRoleKey = M2lbSource,
        },
        new("skolenaervaer-period-closed", "Skolenærvær – Period closed → Bufdata", $"Skolenærværsrapport publishes {PeriodClosedEvent} on Service Bus when a reporting period closes; Bufdata consumes it.", IntegrationKind.ServiceBus)
        {
            RequirementKey = PeriodClosed, SystemName = "Skolenærværsrapport → Bufdata", Producer = "Skolenærværsrapport", Consumer = "Bufdata",
            SourceSystem = "Skolenærværsrapport", DestinationSystem = "Bufdata", EndpointOrTopic = PeriodClosedEvent, EvidenceSourceRoleKey = M2lbSource,
        },
    ];

    public IReadOnlyList<TestPackSourceRole> SourceRoles { get; } =
    [
        new(MuSource, "MU (Meldingsutveksleren)", "The message hub that receives Altinn's webhook and forwards reports.")
        {
            IntegrationRequirementKeys = [ReportIntake], ComponentTokens = ["Meldingsutveksler", "Meldingsutveksleren"], ExpectedArtifact = "MU repository source archive",
            // M2LB's receiver (MM) has an adapter project named after MU; that archive is the M2LB source, not MU.
            ExcludedComponentTokens = ["Meldingsutvekslermottak"],
            Capabilities = ["Meldingsutveksleren", "Altinn webhook", "Altinn source filter", "Report-intake configuration"], Journeys = [RapportmottakId],
        },
        new(M2lbSource, "M2LB", "The downstream Skolenærvær implementation (MM, Skoletjenesten, SkoleAdapter, Utdanning, Skolenærværsrapport).")
        {
            IntegrationRequirementKeys = [EducationCdc, PeriodClosed],
            ComponentTokens = ["Meldingsutvekslermottak", "Skoletjenesten", "Skole", "SkoleAdapter", "Utdanning", "Skolenaervaersrapport", "Skolenærværsrapport"],
            ExpectedArtifact = "M2LB repository source archive",
            Capabilities = ["Meldingsutvekslermottak (MM)", "Skoletjenesten", "SkoleAdapter", "Utdanning", "Skolenærværsrapport", "Period closure", "Service Bus", "Event Hub", "Contracts and XSDs"],
            Journeys = [UtdanningsdataId, RapportmottakId, PeriodeavslutningId],
        },
        // Provided by the pack itself: the app's OpenAPI facts are a built-in Test Pack reference (no import needed). An imported Altinn app
        // document is still recognized, as additional imported evidence that never replaces the reference.
        new(AltinnAppSourceRole, "Altinn application/API", "The Altinn app's own API description (identity, servers, form data type, process task, authentication).")
        {
            Provider = TestPackSourceProvider.TestPackReference,
            ComponentTokens = ["skolenaervaer"], RequiresApiDocument = true,
            // An Altinn app document declares the app route, form data type or process task; another API that only says "skolenærvær" is not it.
            ApiDocumentKeys = [BirkNext.SourceDomains.SourceConfigurationKeys.ApplicationRoute, BirkNext.SourceDomains.SourceConfigurationKeys.ApplicationFormDataType,
                BirkNext.SourceDomains.SourceConfigurationKeys.ApplicationTaskId], ExpectedArtifact = "OpenAPI / Swagger document or application metadata of the Altinn app",
            Capabilities = ["App identity (org/app)", "Server / environment", "Form data type", "Process task", "Authentication scheme"], Journeys = [RapportmottakId],
            RequirementCondition = "Provides the Altinn application metadata Rapportmottak submits with.",
        },
        // No Skolenærvær prerequisite needs declared infrastructure: it adds resource/runtime evidence and never blocks the pack. Any IaC format
        // counts — the capability is Infrastructure as Code, not one technology.
        new(InfrastructureSourceRole, "Infrastructure as Code", "Declared Azure infrastructure: resource names, namespaces, resource groups, managed identities, Event Hub and Service Bus.")
        {
            RequiresInfrastructureAsCode = true, BindingRequired = false, Requirement = TestPackSourceRequirement.Optional,
            RequirementCondition = "Useful only when infrastructure/runtime configuration must be source-verified.",
            ExpectedArtifact = "Infrastructure as Code source (Terraform, Bicep, ARM, Kubernetes or Helm)",
            Capabilities = ["Azure resource names", "Namespaces and resource groups", "Managed identities", "Event Hub / Service Bus platform references", "Deployment bindings"],
        },
    ];
    public IReadOnlyList<TestPackReference> References { get; } = [SkoleNaervaerAltinnReference.Reference(AltinnAppSourceRole)];

    private const string ApiDocument = "OpenAPI / Swagger document";
    private const string AppReference = "Altinn app reference in configuration (e.g. an event subscription) or OpenAPI / Swagger document";
    /// <summary>Which app's values are meant when a source declares several apps (an Altinn route's app segment).</summary>
    private const string AppScope = "skolenaervaer";

    /// <summary>The Altinn values Rapportmottak needs, by generic semantic key: any artifact an analyzer reads may provide them (the app's
    /// OpenAPI document provides all of them; no process.bpmn or applicationmetadata.json is required for these values).</summary>
    public IReadOnlyList<BirkNext.SourceDomains.SourceConfigurationRequirement> RequiredConfiguration { get; } =
    [
        new("altinn-environment", "Altinn environment", BirkNext.SourceDomains.SourceConfigurationKeys.ApplicationEnvironment, AltinnAppSource, AppReference,
            "Which Altinn platform the app is tested on (only Test is ever applied).", "Integration Quality Review → Extensions → Altinn")
            {
                PrerequisiteKey = "altinn-environment", ConsumerField = "Environment", ScopeHint = AppScope, SourceRoles = [MuSource, AltinnAppSourceRole], AutoApply = true,
                // TT02 is Altinn's test platform and the only one Skolenærvær runs against; production is never a default.
                DefaultValue = "Test", OwnerAction = AltinnOwnerAction,
            },
        new("altinn-app-route", "Altinn app (org/app)", BirkNext.SourceDomains.SourceConfigurationKeys.ApplicationRoute, AltinnAppSource, AppReference,
            "Which app Rapportmottak submits to.", "Integration Quality Review → Extensions → Altinn")
            { PrerequisiteKey = "altinn-app", ConsumerField = "App identifier", ScopeHint = AppScope, SourceRoles = [MuSource, AltinnAppSourceRole], AutoApply = true, OwnerAction = AltinnOwnerAction },
        new("altinn-base-url", "Altinn app base URL", BirkNext.SourceDomains.SourceConfigurationKeys.ApplicationBaseUrl, AltinnAppSource, AppReference,
            "The app's address on the test platform (a safe reference; BirkNext builds its calls from the backend endpoints).", "Integration Quality Review → Extensions → Altinn")
            { PrerequisiteKey = "altinn-app", ConsumerField = "Safe base URL reference", ScopeHint = AppScope, SourceRoles = [MuSource, AltinnAppSourceRole], AutoApply = true, OwnerAction = AltinnOwnerAction },
        new("altinn-event-webhook", "Report intake webhook", BirkNext.SourceDomains.SourceConfigurationKeys.EndpointEventWebhook, "Report intake receiver (MU)",
            "Application configuration (Altinn event subscription)", "Where Altinn delivers the app's completed instances.", "Integration Quality Review → Extensions → Altinn")
            {
                PrerequisiteKey = "integration:report-intake", ConsumerField = "Endpoint reference", ScopeHint = AppScope, SourceRoles = [MuSource, AltinnAppSourceRole], AutoApply = true,
                // The report-intake integration record owns the webhook: IQR and this pack read it there instead of keeping a second copy.
                IntegrationRequirementKey = ReportIntake, IntegrationField = nameof(IntegrationDefinition.EndpointOrTopic),
                OwnerAction = "Target Environment → Integrations → Skolenærvær – Altinn report intake",
            },
        new("altinn-test-server", "Altinn test server", BirkNext.SourceDomains.SourceConfigurationKeys.Server(BirkNext.SourceDomains.SourceEnvironmentRole.Test), AltinnAppSource, AppReference,
            "The test (TT02) host the app is served on.", "Backend Altinn test endpoints") { PrerequisiteKey = "altinn-environment", ScopeHint = AppScope, SourceRoles = [MuSource, AltinnAppSourceRole] },
        new("altinn-form-data-type", "Form data type", BirkNext.SourceDomains.SourceConfigurationKeys.ApplicationFormDataType, AltinnAppSource, ApiDocument,
            "The data element the report is written to.", "Altinn app contract") { PrerequisiteKey = "submission-contract", ScopeHint = AppScope, SourceRoles = [AltinnAppSourceRole] },
        new("altinn-content-type", "Form content type", BirkNext.SourceDomains.SourceConfigurationKeys.PayloadContentType, AltinnAppSource, ApiDocument,
            "How the report payload is sent.", "Altinn app contract") { PrerequisiteKey = "submission-contract", ScopeHint = AppScope, SourceRoles = [AltinnAppSourceRole] },
        new("altinn-process-task", "Process task", BirkNext.SourceDomains.SourceConfigurationKeys.ApplicationTaskId, AltinnAppSource, ApiDocument,
            "The task the form belongs to before process/next.", "Altinn app contract") { PrerequisiteKey = "process-contract", ScopeHint = AppScope, SourceRoles = [AltinnAppSourceRole] },
        new("altinn-auth-scheme", "Authentication scheme", BirkNext.SourceDomains.SourceConfigurationKeys.AuthenticationScheme, AltinnAppSource, ApiDocument,
            "How callers authenticate. The QA client's registration, redirect URI and scopes are not in the document and are not verified by it.", "Local execution config")
            { PrerequisiteKey = "idporten-client", ScopeHint = AppScope, SourceRoles = [AltinnAppSourceRole] },
        // Messaging values the Skolenærvær integrations own (Utdanningsdata's CDC stream, Periodeavslutning's event). Their records are the owner;
        // canonical M2LB evidence is compared, never substituted, and nothing is guessed for an environment that has no record.
        new("cdc-event-hub", "Education CDC Event Hub", BirkNext.SourceDomains.SourceConfigurationKeys.MessagingEventHubName, "M2LB (SkoleAdapter)", MessagingEvidence,
            "The Event Hub the BiRK education changes are published on.", "Target Environment → Integrations")
        {
            PrerequisiteKey = "integration:education-cdc", SourceRoles = [M2lbSource], IntegrationRequirementKey = EducationCdc, IntegrationField = nameof(IntegrationDefinition.EndpointOrTopic),
            OwnerAction = "Target Environment → Integrations → Skolenærvær – BiRK CDC → SkoleAdapter",
        },
        new("cdc-consumer-group", "Education CDC consumer group", BirkNext.SourceDomains.SourceConfigurationKeys.MessagingEventHubConsumerGroup, "M2LB (SkoleAdapter)", MessagingEvidence,
            "The consumer group SkoleAdapter reads with (never assumed to be $Default).", "Target Environment → Integrations")
        {
            PrerequisiteKey = "integration:education-cdc", SourceRoles = [M2lbSource], IntegrationRequirementKey = EducationCdc, IntegrationField = nameof(IntegrationDefinition.ConsumerGroup),
            OwnerAction = "Target Environment → Integrations → Skolenærvær – BiRK CDC → SkoleAdapter",
        },
        new("period-closed-topic", "Period-closed Service Bus topic", BirkNext.SourceDomains.SourceConfigurationKeys.MessagingServiceBusTopic, "M2LB (Skolenærværsrapport)", MessagingEvidence,
            "Where Skolenærværsrapport publishes the closed period for Bufdata.", "Target Environment → Integrations")
        {
            PrerequisiteKey = "integration:period-closed", SourceRoles = [M2lbSource], IntegrationRequirementKey = PeriodClosed, IntegrationField = nameof(IntegrationDefinition.EndpointOrTopic),
            OwnerAction = "Target Environment → Integrations → Skolenærvær – Period closed → Bufdata",
        },
    ];

    private const string AltinnOwnerAction = "Integration Quality Review → Extensions → Altinn test configuration";
    private const string MessagingEvidence = "Messaging configuration or Infrastructure as Code";

    /// <summary>What is stored for the requirements: the app identifier (IQR → Extensions → Altinn) and the backend's test host for that app.</summary>
    public async Task<IReadOnlyDictionary<string, string?>> ConfiguredValuesAsync(JourneyPackContext context, CancellationToken ct)
    {
        var config = (await messageFlows.GetAsync(context.EnvironmentId, ct)).AltinnConfiguration ?? new AltinnTestConfiguration();
        var appIdentifier = config.AppIdentifier;
        string? Stored(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
        var values = new Dictionary<string, string?>
        {
            ["altinn-app-route"] = Stored(appIdentifier), ["altinn-base-url"] = Stored(config.BaseUrlReference), ["altinn-event-webhook"] = Stored(config.EndpointReference),
            ["altinn-environment"] = config.Environment == ExternalTestEnvironment.Unknown ? null : config.Environment.ToString(),
        };
        if (altinnPolicy.ApprovedApp(appIdentifier, out _) is { } app)
            values["altinn-test-server"] = altinnPolicy.Endpoints.AppBase(app.Org, app.App).GetLeftPart(UriPartial.Authority);
        return values;
    }

    /// <summary>Stores safe source-derived values into EMPTY Altinn test-configuration fields only (IQR → Extensions → Altinn), through the
    /// normal message-flow save (credential protection included). Environment is only ever set to Test; nothing else is touched.</summary>
    public async Task<IReadOnlyList<string>> ApplyConfigurationAsync(JourneyPackContext context, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var package = await messageFlows.GetAsync(context.EnvironmentId, ct);
        var config = package.AltinnConfiguration ?? new AltinnTestConfiguration();
        var applied = new List<string>();
        if (values.TryGetValue("altinn-environment", out var environment) && environment == nameof(ExternalTestEnvironment.Test) && config.Environment == ExternalTestEnvironment.Unknown)
        { config = config with { Environment = ExternalTestEnvironment.Test }; applied.Add("altinn-environment"); }
        if (values.TryGetValue("altinn-app-route", out var route) && string.IsNullOrWhiteSpace(config.AppIdentifier))
        { config = config with { AppIdentifier = route }; applied.Add("altinn-app-route"); }
        if (values.TryGetValue("altinn-base-url", out var baseUrl) && string.IsNullOrWhiteSpace(config.BaseUrlReference))
        { config = config with { BaseUrlReference = baseUrl }; applied.Add("altinn-base-url"); }
        if (values.TryGetValue("altinn-event-webhook", out var webhook) && string.IsNullOrWhiteSpace(config.EndpointReference))
        { config = config with { EndpointReference = webhook }; applied.Add("altinn-event-webhook"); }
        if (applied.Count == 0) return [];
        var (saved, _) = await messageFlows.SaveAsync(context.EnvironmentId, package with { AltinnConfiguration = config }, ct);
        return saved is null ? [] : applied;
    }

    /// <summary>Whether the Altinn app's reference metadata is available (Test Pack reference). Never mandatory and never a runtime fact.</summary>
    private JourneyPrerequisite AppReferenceRow(JourneyPackContext context)
    {
        var status = TestPackSources.Status(this, context, SourceRoles.Single(role => role.Key == AltinnAppSourceRole));
        var available = status.State == TestPackSourceState.ReferenceAvailable;
        return new JourneyPrerequisite("app-reference", "Altinn app reference metadata", JourneyPrerequisiteCategory.Contract,
            available ? JourneyReadinessState.Ready : JourneyReadinessState.NotReady,
            available ? $"Reference available: app {SkoleNaervaerAltinnReference.AppScope}, form data type, content type, process task and authentication scheme from the Test Pack reference. Reference metadata only — not the deployed app, not runtime verified."
                : $"Test Pack reference unavailable: {string.Join(" ", status.Reference?.Diagnostics ?? [])}", Mandatory: false) { Source = TestPackReferences.ProvenanceLabel };
    }

    /// <summary>" Known: buf/skolenaervaer (Test Pack reference) — not stored yet …" when the shared resolution knows a value the run needs but its
    /// consumer has not stored it; runs use stored values only, so the person applies it once (no re-entry). Empty otherwise.</summary>
    private static string KnownNotStored(JourneyPackContext context, string requirementKey, bool missing) =>
        missing && context.Resolved(requirementKey) is { ResolvedValue: { } value, ResolvedFrom: var origin, ReviewRequired: false } && origin != BirkNext.SourceDomains.ConfigurationValueOrigin.Configured
            ? $" Known: {value} ({BirkNext.SourceDomains.SourceConfigurationPrecedence.Label(origin)}) — not stored for runs yet. Use \"Apply safe configuration\" on Skolenærvær Testing or save it in {AltinnOwnerAction}."
            : "";

    /// <summary>The canonical source-evidence binding of the configured integration that fulfils this journey's report-intake requirement.</summary>
    private IntegrationEvidenceBinding? ReportIntakeBinding(IntegrationJourneyDefinition journey, JourneyPackContext context) =>
        journey.Integrations.Where(requirement => requirement.Key == ReportIntake)
            .SelectMany(requirement => context.Catalog.Integrations.Where(integration => Matches(requirement, integration)))
            .Select(integration => context.Bindings.FirstOrDefault(binding => binding.IntegrationId == integration.Id))
            .FirstOrDefault(binding => binding is not null);

    public IReadOnlyList<string> Limitations =>
    [
        "Rapportmottak's Altinn steps run through the Altinn TT02 App API as an approved synthetic TestID reporter, authenticated unattended with ID-porten TEST's documented headless login (an interactive TestID sign-in remains an optional fallback). An Altinn receipt covers the Altinn steps only: MU, MM, Skolenærværsrapport and Person steps have no observer and stay Not assessed.",
        "The documented message-flow pilot in Integration Quality Review names the intake component \"Skolenærværsmottaket\" and the report service \"Skoletjenesten\"; this pack uses the architecture diagram's names MM and Skolenærværsrapport. The correspondence is a documentation alias, not source verified.",
        "No MU, MM, Skolenærværsrapport or Bufdata observer or read endpoint is available to BirkNext; their steps cannot be observed or verified yet (a source review of Skoletjenesten and MM is recorded in docs/skolenaervaer-cross-repo-verification.md).",
        "BirkNext never publishes SkolenaervaersperiodeLukket itself and never locks a period; Skoletjenesten locks periods on its own schedule, so period closure is an operator action.",
    ];

    public IReadOnlyList<IntegrationJourneyDefinition> Journeys { get; } = WithCapabilities(
    [
        new()
        {
            PackId = Id, JourneyId = UtdanningsdataId, DisplayName = "Utdanningsdata", Flow = "BiRK → Debezium → Event Hub → SkoleAdapter → Utdanning", Order = 1, ExecutionMode = JourneyExecutionMode.DelegatedToActiveEvent,
            DelegatedProviderId = SkoleNaervaerScenarioProvider.Id,
            Description = "BiRK education data (Utdanning, ManglendeSkoletilbud) captured by Debezium, delivered through Event Hub to SkoleAdapter, which forwards the education data and the external child reference to Utdanning. Utdanning links the record to the child through Person.",
            Integrations = [new(EducationCdc, "Skolenærvær – Education CDC", IntegrationKind.EventHub, "Debezium", "SkoleAdapter", "CDC of dbo.Utdanning / dbo.ManglendeSkoletilbud")],
            Steps =
            [
                new("cdc-capture", "BiRK row change captured by Debezium", JourneyStepKind.SourceChange, "Debezium", "Table is in the CDC source-table configuration", FromComponent: "BiRK", ToComponent: "Debezium"),
                new("event-hub", "Change event delivered to Event Hub", JourneyStepKind.BrokerDelivery, "Event Hub", "Event Hub accepted the event", IntegrationKey: EducationCdc, FromComponent: "Debezium", ToComponent: "Event Hub"),
                new("adapter-consume", "SkoleAdapter consumes the event (no Person lookup)", JourneyStepKind.ConsumerProcessing, "SkoleAdapter", "Consumer activity and checkpoint continuity", IntegrationKey: EducationCdc, FromComponent: "Event Hub", ToComponent: "SkoleAdapter"),
                new("utdanning-ingest", "SkoleAdapter sends education data + external child reference to Utdanning", JourneyStepKind.DomainIntake, "SkoleAdapter", "Utdanning received the record", FromComponent: "SkoleAdapter", ToComponent: "Utdanning"),
                new("utdanning-person", "Utdanning links the record to the child through Person", JourneyStepKind.IdentityLookup, "Utdanning", "Record linked to the synthetic child", FromComponent: "Utdanning", ToComponent: "Person"),
                new("utdanning-state", "Education record stored in Utdanning", JourneyStepKind.DomainState, "Utdanning", "Expected record state read back through a read contract", FromComponent: "Utdanning", ToComponent: "Utdanning"),
            ],
            Scenarios = [new("active-event-scenarios", "Active Event scenarios (create, replay, invalid → control)", "Run from Active Event Testing with the Skolenærvær provider; update/delete/tombstone/snapshot, natural-key duplicate and late linkage are Not assessed there.", JourneyScenarioSupport.Supported)],
            EvidenceBasis = DiagramBasis,
            Limitations = ["Executed and evidenced by Active Event Testing; this journey view only summarizes readiness."],
        },
        new()
        {
            PackId = Id, JourneyId = RapportmottakId, DisplayName = "Rapportmottak", Flow = "Altinn → MU → MM → Skolenærværsrapport", Order = 2, ExecutionMode = JourneyExecutionMode.JourneyRunner,
            Description = "A private institution or foster home submits a school-attendance report through Altinn; MU receives and stores the encrypted message; MM retrieves, decrypts, validates (XSD) and maps it — transport/intake only, no Person lookup; Skolenærværsrapport receives the report and performs the Person lookup.",
            Integrations = [new(ReportIntake, "Skolenærvær – Altinn report intake", IntegrationKind.HttpApi, "External reporting party (Altinn)", "MU", "Report submission through the configured Altinn test environment")],
            Steps =
            [
                new(SkoleNaervaerAltinnExecutor.AuthStep, "Reporter authenticated with ID-porten TEST; Altinn TT02 token obtained", JourneyStepKind.ExternalAuthentication, "Synthetic reporter (TestID)",
                    "Altinn resolved the reporter's TEST party (authenticated ≠ submitted)", IntegrationKey: ReportIntake, FromComponent: "Reporter", ToComponent: "Altinn"),
                new(SkoleNaervaerAltinnExecutor.InstanceStep, "Altinn instance created", JourneyStepKind.ExternalSubmission, "Altinn", "Instance created for the reporter",
                    IntegrationKey: ReportIntake, FromComponent: "External reporter", ToComponent: "Altinn"),
                new(SkoleNaervaerAltinnExecutor.DataStep, "Report form data stored", JourneyStepKind.ExternalSubmission, "Altinn", "Typed form data stored on the instance",
                    IntegrationKey: ReportIntake, FromComponent: "External reporter", ToComponent: "Altinn"),
                new(SkoleNaervaerAltinnExecutor.ValidateStep, "Altinn app validation", JourneyStepKind.ExternalAcceptance, "Altinn", "No validation errors from the app",
                    IntegrationKey: ReportIntake, FromComponent: "Altinn", ToComponent: "Altinn"),
                new(SkoleNaervaerAltinnExecutor.SubmitStep, "Report submitted in Altinn", JourneyStepKind.ExternalSubmission, "Altinn", "Process advanced to its end (submitted ≠ received by MU)",
                    IntegrationKey: ReportIntake, FromComponent: "External reporter", ToComponent: "Altinn"),
                new(SkoleNaervaerAltinnExecutor.ReceiptStep, "Altinn receipt read back", JourneyStepKind.ExternalAcceptance, "Altinn", "Instance completed in Altinn (receipt ≠ downstream processing)",
                    IntegrationKey: ReportIntake, FromComponent: "Altinn", ToComponent: "Altinn"),
                new("mu-receive", "MU receives and stores the encrypted message/blob", JourneyStepKind.MessageTransport, "MU", "MU stored the encrypted payload", FromComponent: "Altinn", ToComponent: "MU"),
                new("mu-queue", "MU records the queue/payload reference", JourneyStepKind.MessageTransport, "MU", "Queue message with payload reference", FromComponent: "MU", ToComponent: "MU"),
                new("mm-retrieve", "MM retrieves the message/blob", JourneyStepKind.PayloadRetrieval, "MM", "MM fetched the referenced payload", FromComponent: "MU", ToComponent: "MM"),
                new("mm-decrypt", "MM decrypts the payload", JourneyStepKind.Decryption, "MM", "Decryption succeeded (no key material shown)", FromComponent: "MM", ToComponent: "MM"),
                new("mm-xsd", "MM validates the report against the XSD", JourneyStepKind.StructuralValidation, "MM", "Runtime XSD validation result (XSD exists ≠ payload validated)", FromComponent: "MM", ToComponent: "MM"),
                new("mm-map", "MM maps the report", JourneyStepKind.Mapping, "MM", "Mapped values match the synthetic input", FromComponent: "MM", ToComponent: "MM"),
                new("report-receive", "Skolenærværsrapport receives the report", JourneyStepKind.DomainIntake, "Skolenærværsrapport", "Report received by the report service", FromComponent: "MM", ToComponent: "Skolenærværsrapport"),
                new("report-person", "Skolenærværsrapport performs the Person lookup", JourneyStepKind.IdentityLookup, "Skolenærværsrapport", "Person lookup outcome for the synthetic child", FromComponent: "Skolenærværsrapport", ToComponent: "Person"),
                new("report-state", "Report state stored/validated", JourneyStepKind.DomainState, "Skolenærværsrapport", "Expected report state read back through a read contract", FromComponent: "Skolenærværsrapport", ToComponent: "Skolenærværsrapport"),
            ],
            Scenarios =
            [
                new("valid-report", "E2E-01 / MU-01 · Valid report", "One synthetic, typed report for a registered synthetic test child, submitted through the Altinn TT02 App API by an approved synthetic TestID reporter authenticated unattended. Covers the Altinn steps; MU/MM/report steps stay Not assessed.",
                    JourneyScenarioSupport.Supported, Inputs: ReportInputs(altinnPolicy, testDataCatalog, knownChild: true, [new(SkoleNaervaerReportInput.Registered, "Registered synthetic test person (known child)")]),
                    ExecutionEffects: SubmissionEffects),
                new("person-not-found", "E2E-02 · Child not found in Person", "A typed report for a synthetic child that Person does not know (declared by the tester or generated). Whether the report is held is not observable yet.",
                    JourneyScenarioSupport.Supported, Inputs: ReportInputs(altinnPolicy, testDataCatalog, knownChild: false,
                    [
                        new(SkoleNaervaerReportInput.Supplied, "Synthetic identifier I supply (declared not registered)"),
                        new(SkoleNaervaerReportInput.Generated, "Generated synthetic identifier (unregistered by assumption)"),
                    ]), ExecutionEffects: SubmissionEffects),
                new("overlapping-report", "E2E-08 · Overlapping report", "A second typed report for the same registered synthetic child with a period that overlaps an earlier report the tester declares. How overlap is marked is not observable yet.",
                    JourneyScenarioSupport.Supported, Inputs: ReportInputs(altinnPolicy, testDataCatalog, knownChild: true, [new(SkoleNaervaerReportInput.Registered, "Registered synthetic test person (known child)")],
                        new JourneyScenarioInput(SkoleNaervaerReportInput.PriorReportKey, "Earlier overlapping report", JourneyScenarioInputKind.Choice, true,
                            "Required: a report for this child with an overlapping period was submitted earlier (for example by a previous run).",
                            [new("declared", "Yes — an earlier overlapping report exists for this child")])), ExecutionEffects: SubmissionEffects),
                new(InstanceEvidenceScenario, "Altinn instance evidence (read-only)",
                    "Reads one existing Altinn instance from Altinn Storage (TT02) and records its process state and data-element metadata. Read-only: nothing is created, submitted or changed. An observed instance is not a verified submission, and Altinn's storage path is not an MU blob.",
                    JourneyScenarioSupport.Supported, StateChanging: false, Inputs:
                    [
                        Reporters(altinnPolicy, testDataCatalog),
                        new(SkoleNaervaerInstanceReference.PartyKey, "Instance owner party id", JourneyScenarioInputKind.Reference, true,
                            "The numeric Altinn party id from the instance id (partyId/instanceGuid). Used for this read only; never stored."),
                        new(SkoleNaervaerInstanceReference.InstanceKey, "Instance GUID", JourneyScenarioInputKind.Reference, true,
                            "The instance GUID from the instance id. History keeps only a short reference."),
                    ]),
                new(MessageVerificationScenario, "Message verification (read-only)",
                    "Collects the evidence that already exists for one message — an earlier BirkNext run, an Altinn instance reference or an MU message id — from BirkNext's run history and, where approved, MM's outcome events and Skoletjenesten's intake log, then checks it against the expected outcome. Nothing is submitted, sent, written or retried.",
                    JourneyScenarioSupport.Supported, StateChanging: false, Inputs: SkoleNaervaerMessageInput.Inputs),
                new("correction", "E2E-07 · Correction", "A corrected report replaces an earlier one and the earlier version is kept as history.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: how a correction is expressed in the Altinn form (a new instance, or OppfolgingAvMelding/Oppdatering) is not in any verified app contract. Skoletjenesten keeps earlier versions, and an Oppdatering that changes the dates creates a separate report (Skole source review; not runtime verified)."),
                new("multiple-reporters", "E2E-09 · Several reporters", "Two reporters report for the same child and period; both reports are kept.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: each run authenticates one approved synthetic reporter alias; two separately approved reporters and a way to show that both reports are kept downstream are not available yet."),
                new("invalid-xsd", "Invalid XSD", "A reviewed schema-invalid report rejected by MM's XSD validation.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: no reviewed invalid payload fixture and no submission contract exist; arbitrary XML is never corrupted."),
                new("period-locked", "Period locked", "A report submitted after the reporting period is locked.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: Skoletjenesten answers a report for a locked period with 422 UTENFOR_GYLDIGHETSVINDU (Skole source review; not runtime verified), but locking is operator-only and BirkNext has no report read contract to observe the outcome. Test code never re-implements the rule."),
                new("mapping-validation", "Mapping validation", "Mapped values in Skolenærværsrapport match the synthetic submission.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: no read contract for the report service exists to verify mapped values."),
                new("duplicate-report", "Duplicate report", "The same report submitted twice (exact repeat ≠ natural-key duplicate).", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: Skoletjenesten answers a repeated meldingId with its stored outcome (Skole source review; not runtime verified), so an exact repeat looks identical to the first answer (200 Mottatt). BirkNext cannot produce a repeat through Altinn or observe the stored outcome without a report read contract."),
                new("retrieval-decrypt-failure", "Message retrieval/decrypt failure", "MM cannot retrieve or decrypt the payload.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: no safe test-environment support for retrieval or cryptographic fault injection; destructive testing is never improvised."),
            ],
            EvidenceBasis = DiagramBasis,
            Limitations = ["Authenticated ≠ submitted ≠ received by MU ≠ processed by MM ≠ stored by Skolenærværsrapport: each step is evidenced on its own, and an Altinn receipt never completes this journey.", "Correlation can use only a stable test reference returned by the real Altinn/MU contract; none is known, so at best aggregate activity could be reported."],
        },
        new()
        {
            PackId = Id, JourneyId = PeriodeavslutningId, DisplayName = "Periodeavslutning", Flow = "Skolenærværsrapport → Service Bus → Bufdata", Order = 3, ExecutionMode = JourneyExecutionMode.JourneyRunner,
            Description = "When a reporting period closes, Skolenærværsrapport prepares the completed dataset and publishes SkolenaervaersperiodeLukket on Service Bus; Bufdata consumes the completed reporting data. The Bufdata dataset must not contain fødselsnummer or DUF.",
            Integrations = [new(PeriodClosed, "Skolenærvær – Period Closed", IntegrationKind.ServiceBus, "Skolenærværsrapport", "Bufdata", $"{PeriodClosedEvent} after period closure")],
            Steps =
            [
                new("period-close", "Close a synthetic reporting period through the domain workflow", JourneyStepKind.DomainState, "Skolenærværsrapport", "Period closed by the domain workflow", FromComponent: "Skolenærværsrapport", ToComponent: "Skolenærværsrapport"),
                new("dataset-prepare", "Completed dataset prepared", JourneyStepKind.DomainState, "Skolenærværsrapport", "Dataset prepared for the closed period", FromComponent: "Skolenærværsrapport", ToComponent: "Skolenærværsrapport"),
                new("publish", $"{PeriodClosedEvent} published to Service Bus", JourneyStepKind.Publication, "Skolenærværsrapport", "Event published by the domain (not by BirkNext)", IntegrationKey: PeriodClosed, FromComponent: "Skolenærværsrapport", ToComponent: "Service Bus"),
                new("broker", "Event observed by the broker", JourneyStepKind.BrokerDelivery, "Service Bus", "Broker metadata shows the event (aggregate or correlated)", IntegrationKey: PeriodClosed, FromComponent: "Service Bus", ToComponent: "Service Bus"),
                new("bufdata-activity", "Bufdata consumer activity", JourneyStepKind.ConsumerProcessing, "Bufdata", "Consumer activity (aggregate unless correlated)", IntegrationKey: PeriodClosed, FromComponent: "Service Bus", ToComponent: "Bufdata"),
                new("bufdata-processed", "Bufdata processed the completed dataset", JourneyStepKind.ConsumerProcessing, "Bufdata", "Dataset received for the period", FromComponent: "Bufdata", ToComponent: "Bufdata"),
                new("dataset-verify", "Dataset metadata verified (period, row count, coverage, correlation)", JourneyStepKind.DownstreamVerification, "Bufdata", "Metadata matches the closed synthetic period", FromComponent: "Bufdata", ToComponent: "Bufdata"),
                new("dataset-pii", "Dataset contains no fødselsnummer or DUF", JourneyStepKind.DownstreamVerification, "Bufdata", "Absence of personal identifiers verified from dataset evidence", FromComponent: "Bufdata", ToComponent: "Bufdata"),
            ],
            Scenarios =
            [
                new("close-valid-period", "Close valid period", "Close one synthetic period through a safe domain workflow and follow the emitted event to Bufdata.", JourneyScenarioSupport.NotAssessed,
                    $"Not assessed — operator required. {SkoleLockLifecycle} BirkNext never locks a period."),
                new("event-emitted", "Event emitted", $"{PeriodClosedEvent} is emitted once for the closed period.", JourneyScenarioSupport.NotAssessed,
                    $"Not assessed: requires an operator-run lock and a broker observer. {SkoleDeliveryState}"),
                new("event-metadata", "Event metadata correct", "The event carries the expected period metadata.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: Skoletjenesten declares the event (period, delivered row count, coverage) and routes it to topic skole.perioder, but nothing publishes it yet (Skole source review, ADR-028 pending; not runtime verified), so no emitted event exists to check."),
                new("bufdata-receives", "Bufdata receives dataset", "Bufdata processed the dataset for the closed period.", JourneyScenarioSupport.NotAssessed,
                    $"Not assessed: no Bufdata read/verification surface exists, and Skoletjenesten has no delivery acknowledgement. {SkoleDeliveryState}"),
                new("pii-absent", "PII absent", "The delivered dataset contains no fødselsnummer or DUF.", JourneyScenarioSupport.NotAssessed,
                    $"Not verified: no dataset evidence is available. {SkoleDeliveryState}"),
                new("coverage-metadata", "Coverage/row count metadata correct", "Row count and coverage metadata match the synthetic period.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: no dataset metadata evidence is available. Frozen coverage can be read after a lock through Skoletjenesten's GraphQL dekningsstatistikk (Skole:SeDekningsstatistikk); BirkNext has no reader for it, and that coverage is not the Bufdata delivery."),
            ],
            EvidenceBasis = DiagramBasis,
            Limitations = ["Service Bus accepted ≠ Bufdata processed: broker, consumer and dataset evidence are separate steps.", "BirkNext never sends SkolenaervaersperiodeLukket directly."],
        },
    ]);

    /// <summary>Attaches each scenario's declared capability profile (by scenario id) to the catalog; identity stays the scenario catalog's.</summary>
    private static IReadOnlyList<IntegrationJourneyDefinition> WithCapabilities(IReadOnlyList<IntegrationJourneyDefinition> journeys) =>
        journeys.Select(journey => journey with
        {
            Scenarios = journey.Scenarios.Select(scenario => scenario with { Capability = CapabilityProfiles.GetValueOrDefault(scenario.ScenarioId) }).ToArray(),
        }).ToArray();

    private const string TeamOperator = "BirkNext operator with the ID-porten TEST client owner";
    private const string TeamSkole = "Skole team (Skoletjenesten)";
    private const string TeamMm = "MM team (Meldingsutvekslermottak)";
    private const string AltinnConfiguration =
        "ID-porten TEST client id and scopes (Integration Quality Review → Altinn test configuration); redirect URI, client secret and Enabled (backend); approved Altinn app with public TT02 metadata read; trusted target with the report-intake integration enrolled.";
    private const string AltinnAccess = "A registered ID-porten TEST client; the reporter's PRIV role allowed by the confirmed app policy.";
    private const string AltinnTestData = "An approved synthetic TestID reporter alias with a confirmed Altinn TT02 profile";
    private const string AltinnExecution = "Integration journey runner: Altinn TT02 App API (create instance, store form data, validate, one process/next, read receipt) as the unattended synthetic reporter.";
    private const string ReportChain = "Altinn TT02 → MU → MM → Skoletjenesten";
    private const string NoReportRead = "A read contract for report state in Skoletjenesten: its 200 Mottatt answer is the same for accepted, held, replayed and closed-chain reports.";
    private const string NotRunnableHere = "None in BirkNext.";

    /// <summary>Per scenario: what it needs, how far its result can be verified, and who can close the gap. Statuses are computed, never declared here.</summary>
    private static readonly IReadOnlyDictionary<string, JourneyScenarioCapabilityProfile> CapabilityProfiles = new Dictionary<string, JourneyScenarioCapabilityProfile>(StringComparer.Ordinal)
    {
        ["active-event-scenarios"] = new("BiRK CDC → Event Hub → SkoleAdapter → Skoletjenesten (Utdanning)",
            "Active Event Testing with provider skolenaervaer.cdc: synthetic CDC events sent to the approved test Event Hub.",
            "An enabled Event Hub integration for dbo.Utdanning or dbo.ManglendeSkoletilbud in Integration Quality Review, its approved destination, the trusted target with that integration enrolled, and the SkoleAdapter source contract in Source Analysis.",
            "Reserved synthetic record keys and synthetic child references (provider policy) and a reviewed fixture.",
            "Send to the approved test Event Hub (backend-held connection; never a client-supplied destination).",
            JourneyResultVerification.Partial,
            "Transport and consumer/continuity evidence only. Event Hub accepted ≠ education relationship created: the Utdanning outcome has no read contract in BirkNext.",
            "SkoleAdapter source and its CDC contract (only the specification is available); an approved read-only client for Skoletjenesten's /api/skole/v1/utdanning/postidentiteter.",
            "SkoleAdapter team (source and contract); Skole team (read access)",
            "Supply the SkoleAdapter source to Source Analysis; agree a read-only verification client for the education API. Update, delete and late linkage stay Not assessed until then."),

        ["valid-report"] = new(ReportChain, AltinnExecution, AltinnConfiguration, $"{AltinnTestData}, and an approved registered synthetic child (never the reporter).", AltinnAccess,
            JourneyResultVerification.Partial,
            "The Altinn steps (stored, validated, submitted, receipt) are verified from Altinn's answers. MU, MM and Skoletjenesten outcomes are not observable.",
            $"MU and MM observers; {NoReportRead}", $"{TeamOperator} (configuration); {TeamSkole} (report read contract)",
            "Complete the ID-porten TEST configuration, confirm reporter-a's TT02 profile and approve a synthetic child, then run a dry run before the first supervised submission."),
        ["person-not-found"] = new(ReportChain, AltinnExecution, AltinnConfiguration, $"{AltinnTestData}, and a synthetic child that Person does not know.", AltinnAccess,
            JourneyResultVerification.NotVerifiable,
            "Skoletjenesten holds a report for an unknown child and still answers 200 Mottatt; the held state is visible only in its database, its SkolenaervaersrapportEndret events or the GraphQL follow-up list.",
            "An authorized read of held reports (GraphQL follow-up list or report events).", TeamSkole,
            "Use Skole's HoldtRapportTests and HoldVedLaasTests (discovered, not executed) as evidence until an authorized follow-up reader exists."),
        ["overlapping-report"] = new(ReportChain, AltinnExecution, AltinnConfiguration, $"{AltinnTestData}, a registered synthetic child and an earlier overlapping report.", AltinnAccess,
            JourneyResultVerification.NotVerifiable, "How Skoletjenesten records the overlap is not observable from BirkNext.", NoReportRead, TeamSkole,
            "Use Skole's KorreksjonOgKjedeTests, which include overlapping reports (discovered, not executed), until a report read contract exists."),
        [InstanceEvidenceScenario] = new("Altinn TT02 Storage", "Integration journey runner: one read-only Altinn Storage instance read (nothing is created or changed).",
            AltinnConfiguration, "The party id and instance GUID of an existing test instance (used for the read only, never stored).",
            "The reporter's token must be allowed to read that instance.", JourneyResultVerification.Verifiable,
            "The instance's process state and data-element metadata are read back. An observed instance is not a verified submission.",
            "None for the instance itself; it says nothing about MU, MM or Skoletjenesten.", TeamOperator, "Complete the ID-porten TEST configuration and supply a known test instance."),

        [MessageVerificationScenario] = new("BirkNext run history → MM → Skoletjenesten (read-only evidence)",
            "Integration journey runner: one read-only observer reads BirkNext's run history and, where approved, MM's outcome events and Skoletjenesten's intake log (one bounded query each).",
            "An MU message id for MM and Skoletjenesten evidence; for each, an enabled IQR integration enrolled in the trusted environment on a platform with its telemetry source.",
            "An existing message: a BirkNext run, an Altinn instance GUID or an MU message id. Nothing new is created.",
            "Read access for BirkNext's backend identity to the components' Log Analytics / Application Insights (checked when read; configured is not authorized).",
            JourneyResultVerification.Partial,
            "Altinn steps from recorded runs; MM outcomes and Skoletjenesten's HTTP answer from telemetry. Skoletjenesten's internal outcome, coverage and Bufdata receipt are not verifiable.",
            "MU message id for runs (not recorded by BirkNext); telemetry access; a report state read contract; an approved GraphQL client; a Bufdata receipt.",
            $"{TeamOperator}; {TeamMm}; {TeamSkole}", "Configure and enrol the MM and Skoletjenesten telemetry sources, grant read access, then verify messages by their MU message id."),
        ["correction"] = new(ReportChain, NotRunnableHere, "A verified Altinn app contract for expressing a correction.", "Two related synthetic reports for one child.", AltinnAccess,
            JourneyResultVerification.NotVerifiable, "Version history is kept in Skoletjenesten's database only.", $"How the Altinn form expresses a correction; {NoReportRead}",
            $"{TeamSkole}; Altinn app owner", "Use Skole's KorreksjonOgKjedeTests (discovered, not executed) as evidence."),
        ["multiple-reporters"] = new(ReportChain, NotRunnableHere, AltinnConfiguration, "Two approved synthetic reporters with confirmed Altinn TT02 profiles, one synthetic child.", AltinnAccess,
            JourneyResultVerification.NotVerifiable, "Whether both reports are kept is visible only in Skoletjenesten.", $"A second approved reporter; {NoReportRead}",
            $"{TeamOperator}; {TeamSkole}", "Approve a second synthetic reporter; until then rely on Skole's SamtidighetTests (discovered, not executed)."),
        ["invalid-xsd"] = new("MU → MM", NotRunnableHere, "A reviewed schema-invalid fixture; MM test environment.", "Synthetic invalid report (never corrupted ad hoc).", "MU send access to a test channel (not granted).",
            JourneyResultVerification.NotVerifiable, "MM's rejection is visible in MM telemetry and its receipt only.", "An MM observer or imported MM test results.", TeamMm,
            "Run MM's AvviseUgyldigMeldingTests (integration, emulators) in an isolated .NET 10 worker and import the TRX."),
        ["period-locked"] = new("Skoletjenesten", NotRunnableHere, "A locked QA period.", "A synthetic report for that period.", "Skole operations for the lock.",
            JourneyResultVerification.NotVerifiable, "The 422 UTENFOR_GYLDIGHETSVINDU answer goes to MM, not to BirkNext.", $"An operator-run lock; {NoReportRead}",
            $"{TeamSkole}; Skole operations", "Use Skole's GyldighetskontrollTests and LaasutsettelseTests (discovered, not executed) as evidence."),
        ["mapping-validation"] = new("MM → Skoletjenesten", NotRunnableHere, "Not applicable.", "A synthetic report with known values.", "Read access to stored report state (not granted).",
            JourneyResultVerification.NotVerifiable,
            "The MM → Skoletjenesten contract is compatible in source except timestamp drift D1; no live MM → Skoletjenesten call has been observed.",
            NoReportRead, $"{TeamMm}; {TeamSkole}", "Resolve drift D1 and run MM's SkoletjenestenKontraktTests; see docs/skolenaervaer-cross-repo-verification.md."),
        ["duplicate-report"] = new("MM → Skoletjenesten", NotRunnableHere, "Not applicable.", "One synthetic report sent twice with the same message id.", "MU send access (not granted).",
            JourneyResultVerification.NotVerifiable, "An exact repeat gets the stored answer; it looks identical to the first.", NoReportRead, TeamSkole,
            "Use Skole's RapportinntakIdempotensTests and MM's integration tests (discovered, not executed) as evidence."),
        ["retrieval-decrypt-failure"] = new("MU → MM", NotRunnableHere, "MM test environment with emulators.", "Synthetic encrypted message.", "None granted; no fault injection against a real MU.",
            JourneyResultVerification.NotVerifiable, "Visible in MM telemetry and its dead-letter handling only.", "Imported MM test results or an MM observer.", TeamMm,
            "Run MM's integration suites (Service Bus emulator, Azurite) in an isolated worker and import the TRX."),

        ["close-valid-period"] = new("Skoletjenesten → Service Bus → Bufdata", "None in BirkNext: Skoletjenesten's lock planner locks the period on its schedule.",
            "A QA reporting period whose lock an operator controls.", "Synthetic reports and education data for that period.",
            "Skole operations (Skole:UtsetteLaas only postpones the lock).", JourneyResultVerification.NotVerifiable,
            "After a lock, frozen coverage needs an approved GraphQL client and role; the dataset is not written yet.",
            "Dataset files, the SkolenaervaersperiodeLukket publisher and a Bufdata receipt (ADR-028 pending in Skoletjenesten).",
            "Skole operations; Skole team; Bufdata team", "Agree an operator-run QA lock procedure; until then rely on Skole's PeriodelaasEndeTilEndeTests (discovered, not executed).",
            OperatorRequired: true),
        ["event-emitted"] = new("Skoletjenesten → Service Bus", NotRunnableHere, "An operator-run lock; a broker observer for topic skole.perioder.", "A locked synthetic period.",
            "Read access to Service Bus metrics for skole.perioder.", JourneyResultVerification.NotVerifiable, "Nothing publishes the event yet.",
            "The event publisher in Skoletjenesten (ADR-028).", TeamSkole, "Wait for the publisher; then add a read-only broker observer."),
        ["event-metadata"] = new("Skoletjenesten → Service Bus", NotRunnableHere, "As for event-emitted.", "A locked synthetic period.", "As for event-emitted.",
            JourneyResultVerification.NotVerifiable, "No emitted event exists to check.", "An emitted SkolenaervaersperiodeLukket (period, delivered row count, coverage).", TeamSkole,
            "Wait for the publisher (ADR-028)."),
        ["bufdata-receives"] = new("Bufdata", NotRunnableHere, "A Bufdata read or receipt surface.", "A delivered synthetic period.", "Read access to Bufdata delivery status (none exists).",
            JourneyResultVerification.NotVerifiable, "Only prepared delivery rows exist (Klargjort); no file or receipt.", "A dataset file, a Levert state and a Bufdata receipt.",
            $"{TeamSkole}; Bufdata team", "Agree a Bufdata receipt or read-only status surface."),
        ["pii-absent"] = new("Bufdata", NotRunnableHere, "Access to the delivered dataset.", "A delivered synthetic period.", "Read access to the dataset (none exists).",
            JourneyResultVerification.NotVerifiable, "No dataset file exists to scan.", "The delivered dataset; Skole's file PII test is skipped (ADR-028).",
            $"{TeamSkole}; Bufdata team", "Use Skole's PersonvernTests (discovered, not executed) for the database and events; the file scan waits for ADR-028."),
        ["coverage-metadata"] = new("Skoletjenesten", NotRunnableHere, "An approved GraphQL client for dekningsstatistikk.", "A locked synthetic period.",
            "Role Skole:SeDekningsstatistikk (not granted).", JourneyResultVerification.NotVerifiable,
            "Frozen coverage is readable through GraphQL after a lock, but BirkNext has no approved client; it is not the Bufdata delivery population.",
            "An approved read-only GraphQL client and role.", TeamSkole, "Agree a read-only coverage client; until then rely on Skole's DekningsstatistikkTests (discovered, not executed)."),
    };

    /// <summary>Responsibility rules from the approved architecture. Evaluated on Source Analysis dependency evidence; not assessed without source.</summary>
    public IReadOnlyList<ArchitectureResponsibilityRule> Rules { get; } =
    [
        new("mm-no-person", "MM performs transport/intake only — no Person lookup", "MM", ["MM", "Meldingsutvekslermottak"], "Person", ["Person"], ArchitectureRuleExpectation.MustNotDependOn,
            "MM retrieves, decrypts, validates (XSD) and maps reports; Person lookup belongs to Skolenærværsrapport.") { SourceRole = M2lbSource },
        new("skoleadapter-no-person", "SkoleAdapter performs no Person lookup", "SkoleAdapter", ["SkoleAdapter"], "Person", ["Person"], ArchitectureRuleExpectation.MustNotDependOn,
            "SkoleAdapter forwards education data with the external child reference; Utdanning resolves the child.") { SourceRole = M2lbSource },
        new("utdanning-person", "Utdanning owns the Person lookup for BiRK education data", "Utdanning", ["Utdanning"], "Person", ["Person"], ArchitectureRuleExpectation.MustDependOn,
            "Utdanning links BiRK education data to the correct child through Person.") { SourceRole = M2lbSource },
        new("rapport-person", "Skolenærværsrapport owns the Person lookup for submitted reports", "Skolenærværsrapport", ["Skolenærværsrapport", "Skolenaervaersrapport"], "Person", ["Person"],
            ArchitectureRuleExpectation.MustDependOn, "Skolenærværsrapport performs the Person lookup for incoming reports.") { SourceRole = M2lbSource },
    ];

    /// <summary>
    /// The reporter choice: each approved synthetic TEST reporter by alias (unattended, the default) and, last, the tester's own interactive
    /// TestID sign-in as an explicit fallback. Values are aliases, never identifiers. The only usable approved reporter is preselected.
    /// </summary>
    private static JourneyScenarioInput Reporters(AltinnTestingPolicy policy, BirkNext.Api.Services.TestData.TestDataCatalog? catalog) =>
        new(SkoleNaervaerReportInput.ReporterKey, "Reporter (Altinn TEST actor)", JourneyScenarioInputKind.Choice, true,
            "An approved synthetic TestID reporter, authenticated unattended by the backend. The reporter is the Altinn actor, separate from the child the report is about.",
            [
                .. policy.Options.UnattendedIdPortenTest.Reporters.Where(item => AltinnTestingPolicy.ReporterProblem(item) is null)
                    .Select(item => new JourneyScenarioInputOption(item.Alias, string.IsNullOrWhiteSpace(item.Label) ? item.Alias : item.Label)),
                new(AltinnTestingPolicy.InteractiveReporter, "Interactive TestID sign-in (fallback; sign in first)"),
            ], new SkoleNaervaerTestData(policy.Options, catalog).DefaultReporter);

    /// <summary>
    /// Typed inputs of a report scenario: reporter, identity source (limited per scenario), the approved child or the child's synthetic
    /// identifier, period and hours. When approved synthetic children apply (known or unknown to Person, per scenario), "Approved synthetic
    /// child" is offered first and preselected, and the child is preselected when the choice is deterministic; typed identifiers stay possible.
    /// </summary>
    private static IReadOnlyList<JourneyScenarioInput> ReportInputs(AltinnTestingPolicy policy, BirkNext.Api.Services.TestData.TestDataCatalog? catalog, bool knownChild, IReadOnlyList<JourneyScenarioInputOption> identitySources,
        params JourneyScenarioInput[] extra)
    {
        var testData = new SkoleNaervaerTestData(policy.Options, catalog);
        var children = testData.Children(knownChild);
        IReadOnlyList<JourneyScenarioInputOption> sources = children.Count == 0 ? identitySources
            : [new(SkoleNaervaerReportInput.Approved, "Approved synthetic child (approved test data)"), .. identitySources];
        return
        [
            Reporters(policy, catalog),
            new(SkoleNaervaerReportInput.IdentitySourceKey, "Child identity", JourneyScenarioInputKind.Choice, true,
                "Synthetic test identities only. Known/unknown is a declaration; BirkNext cannot check Person.", sources, children.Count > 0 ? SkoleNaervaerReportInput.Approved : null),
            .. children.Count == 0 ? Array.Empty<JourneyScenarioInput>() :
            [
                new(SkoleNaervaerReportInput.ChildKey, "Approved synthetic child", JourneyScenarioInputKind.Choice, false,
                    "From the approved test data, by alias; the identifier stays in the backend. Used when the child identity is \"Approved synthetic child\".",
                    [.. children.Select(child => new JourneyScenarioInputOption(child.Alias, string.IsNullOrWhiteSpace(child.Label) ? child.Alias : child.Label))],
                    testData.DefaultChild(knownChild)?.Alias),
            ],
            new(SkoleNaervaerAltinnForm.ChildIdentifier, "Barnets fødselsnummer (synthetic)", JourneyScenarioInputKind.SyntheticPersonIdentifier, false,
                "11 digits, month + 80 (Test-Norge synthetic). Leave empty for an approved child or when BirkNext generates one. Never stored; history keeps a short hash."),
            new(SkoleNaervaerAltinnForm.PeriodFrom, "Dato fra", JourneyScenarioInputKind.Date, true),
            new(SkoleNaervaerAltinnForm.PeriodTo, "Dato til", JourneyScenarioInputKind.Date, true),
            new(SkoleNaervaerAltinnForm.PlannedHours, "Antall timer etter planen for perioden", JourneyScenarioInputKind.Number, true,
                "Whole hours when the app's schema says integer (the Skolenærvær form does)."),
            new(SkoleNaervaerAltinnForm.ActualHours, "Antall timer faktisk nærvær for perioden", JourneyScenarioInputKind.Number, true,
                "Whole hours. Attendance above plan is allowed (it is flagged by Skolenærværsrapport, not rejected)."),
            .. extra,
        ];
    }

    public JourneyInputPreparation PrepareInputs(IntegrationJourneyDefinition journey, JourneyScenarioDescriptor scenario, IReadOnlyDictionary<string, string> inputs) =>
        scenario.ScenarioId == InstanceEvidenceScenario ? SkoleNaervaerInstanceReference.Prepare(scenario, inputs)
        : scenario.ScenarioId == MessageVerificationScenario ? SkoleNaervaerMessageInput.Prepare(inputs)
        : scenario.Inputs is { Count: > 0 } ? SkoleNaervaerReportInput.Prepare(scenario, inputs,
            alias => new SkoleNaervaerTestData(altinnPolicy.Options, testDataCatalog).ChildIdentifier(alias, knownToPerson: scenario.ScenarioId != "person-not-found"))
            : inputs.Count == 0 ? JourneyInputPreparation.None : JourneyInputPreparation.Invalid("This scenario takes no inputs.");

    /// <summary>
    /// Dry run of a Rapportmottak submission: everything the executor checks and builds before its first call — reporter, unattended
    /// configuration (secret presence only), endpoint allowlist, Production safety, app policy for the reporter's role, contract, the typed
    /// form and its XML, and the process plan — without any network call, token, instance or history. The preview is redacted.
    /// </summary>
    public async Task<JourneyPackValidation> ValidateRunAsync(IntegrationJourneyDefinition journey, JourneyScenarioDescriptor scenario, IReadOnlyDictionary<string, string> values,
        JourneyPackContext context, CancellationToken ct)
    {
        if (journey.JourneyId != RapportmottakId || !SubmissionScenarios.Contains(scenario.ScenarioId, StringComparer.Ordinal)) return JourneyPackValidation.None;
        var flow = await messageFlows.GetAsync(context.EnvironmentId, ct);
        var config = flow.AltinnConfiguration;
        var contract = altinnContracts.Evaluate(context.EnvironmentId, config.AppIdentifier, flow.AppContract);
        var checks = new List<JourneyValidationCheck>();
        var preview = new List<JourneyEvidenceFact>();

        var reporter = values.GetValueOrDefault(SkoleNaervaerReportInput.ReporterKey) ?? "";
        var role = "PRIV";
        if (reporter == AltinnTestingPolicy.InteractiveReporter)
            checks.Add(new("reporter", "Reporter", true, "Interactive fallback: the tester's own TestID sign-in is used (checked when the run starts, not here)."));
        else
        {
            var resolved = altinnPolicy.Reporter(reporter, out var reporterReason);
            role = resolved?.AltinnRole ?? role;
            checks.Add(new("reporter", "Reporter", resolved is not null, resolved is not null ? $"Synthetic TEST reporter alias {reporter} resolved (identifier not shown); role {role}." : reporterReason));
            var sameAsChild = altinnPolicy.IsReporterIdentity(reporter, values.GetValueOrDefault(SkoleNaervaerAltinnForm.ChildIdentifier));
            checks.Add(new("reporter-child", "Reporter ≠ child", !sameAsChild, sameAsChild ? SkoleNaervaerAltinnExecutor.ReporterIsChild
                : "The child is a different synthetic person from the reporter (compared in the backend; neither identifier is shown)."));
            var problems = altinnPolicy.UnattendedProblems(config);
            checks.Add(new("unattended-configuration", "Unattended ID-porten TEST configuration", problems.Count == 0,
                problems.Count == 0 ? "Client id, openid scope, registered redirect URI and client secret are present (secret value not read out)." : string.Join(" ", problems)));
        }

        var environment = AltinnTestingPolicy.AltinnEnvironmentBlock(config);
        checks.Add(new("production-safety", "Production safety", environment is null, environment ?? "Altinn TT02 only; Production hosts are refused by name on every request."));
        var app = altinnPolicy.ApprovedApp(config.AppIdentifier, out _);
        var endpoints = new List<Uri>
        {
            new(AltinnTestingPolicy.IdPortenTestDiscovery), new(AltinnTestingPolicy.IdPortenTestAuthorization), new(AltinnTestingPolicy.IdPortenTestToken),
        };
        string? addressProblem = null;
        if (app is { } a)
        {
            // Every App API address the run would use, built from the contract's operation paths (sample ids only), must pass the same guard.
            try
            {
                endpoints.Add(altinnPolicy.Endpoints.TokenExchange);
                var addresses = AltinnAppEndpoints.Create(altinnPolicy.Endpoints.AppBase(a.Org, a.App), $"{a.Org}/{a.App}", contract.Operations, contract.OperationsSource);
                var sample = new Guid("00000000-0000-4000-8000-000000000001");
                endpoints.AddRange([addresses.CreateInstance(1), addresses.Instance(1, sample), addresses.CreateData(1, sample, contract.FormDataTypeId ?? "form"),
                    addresses.UpdateData(1, sample, sample, contract.FormDataTypeId ?? "form"), addresses.Validate(1, sample), addresses.ProcessNext(1, sample)]);
            }
            catch (AltinnContractException ex) { addressProblem = ex.Message; }
        }
        var blocked = endpoints.Select(uri => (uri, reason: altinnPolicy.RequestBlock(uri))).Where(item => item.reason is not null).ToList();
        checks.Add(new("endpoints", "Endpoint allowlist", blocked.Count == 0 && app is not null && addressProblem is null,
            app is null ? "No approved app: the app endpoint cannot be built." : addressProblem is not null ? addressProblem : blocked.Count == 0
                ? $"{endpoints.Count} endpoints (ID-porten TEST, Altinn TT02 exchange, and the approved app's operations · {contract.OperationsSource}) all pass the request guard."
                : string.Join(" ", blocked.Select(item => item.reason))));

        var confirmation = altinnPolicy.AppPolicy(config.AppIdentifier, flow.AppContract?.ObservedPolicy, out var policyDetail);
        var policyProblem = confirmation is null ? null : AltinnTestingPolicy.PolicyProblem(confirmation, role);
        checks.Add(new("app-policy", "App authorization policy for the reporter", confirmation is not null && policyProblem is null, policyProblem ?? policyDetail));
        checks.Add(new("contract", "App contract", contract.Complete, contract.Complete ? "Complete." : $"Incomplete — {string.Join(" ", contract.Missing)}"));

        if (contract.Fields.Count > 0 && contract.FormDataTypeId is { } dataType)
        {
            var input = SkoleNaervaerReportInput.Read(values);
            var (model, problem) = SkoleNaervaerAltinnExecutor.BuildModel(input, contract.Fields);
            var xml = model is not null && contract.SubmissionContentType == "application/xml" && contract.XmlRootElement is { } rootElement && contract.XmlNamespace is { } ns
                ? SkoleNaervaerAltinnExecutor.ToXml(model, contract.Fields, rootElement, ns, contract.XmlElementsQualified) : null;
            checks.Add(new("form", "Typed form data", model is not null, problem ?? (xml is not null
                ? $"Form built at the verified schema paths and serialized as XML ({System.Text.Encoding.UTF8.GetByteCount(xml)} bytes, not shown)."
                : "Form built at the verified schema paths.")));
            preview.Add(new("Data type", dataType));
            preview.Add(new("Content type", contract.SubmissionContentType ?? "(unknown)"));
            if (contract.XmlRootElement is { } root) preview.Add(new("XML root", root));
            if (contract.XmlNamespace is { } xmlNamespace) preview.Add(new("XML namespace", xmlNamespace));
            foreach (var binding in contract.Bindings.Where(item => item.Present))
                preview.Add(new(binding.ExpectedPath, binding.Key switch
                {
                    SkoleNaervaerAltinnForm.ChildIdentifier => $"synthetic identifier {SyntheticPersonIdentifier.Reference(input.ChildIdentifier)} (masked)",
                    SkoleNaervaerAltinnForm.PeriodFrom => input.From.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    SkoleNaervaerAltinnForm.PeriodTo => input.To.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    SkoleNaervaerAltinnForm.PlannedHours => input.PlannedHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => input.ActualHours.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }));
        }
        else checks.Add(new("form", "Typed form data", false, "The app's form schema is not known, so the form cannot be built."));

        // Without the app's form schema nothing is known about required fields: that is not evaluated, never "none missing".
        checks.Add(new("unfilled-required", "Required schema fields BirkNext does not fill", contract.Fields.Count > 0 && contract.RequiredUnboundFields.Count == 0,
            contract.Fields.Count == 0 ? SchemaUnknownDetail
            : contract.RequiredUnboundFields.Count == 0 ? "None."
                : $"{contract.RequiredUnboundFields.Count} required field(s) are not filled by BirkNext ({string.Join(", ", contract.RequiredUnboundFields.Take(10).Select(field => field.Path))}). They must come from the app itself — not verified; Altinn's validation step of the first live run will show it.",
            Blocking: false));
        if (SkoleNaervaerContractProfile.Analyze(flow.AppContract) is { } impact && ProducerBlock(impact) is { } producerBlock)
            checks.Add(new("contract-impact", "Contract change: BirkNext producer", false, producerBlock));
        var process = contract.Process;
        checks.Add(new("process-plan", "Process contract", process.Confirmed, process.Confirmed
            ? $"{ProcessStatusLabel(process.Status)}: process/next × {process.Transitions} ({string.Join(" → ", process.PlannedTasks.Select(task => $"{task.TaskId} ({task.TaskType})"))}), {process.Operation.ToLowerInvariant()}; maximum {process.MaxTransitions} process transition(s). {process.ExpectedPostCondition} A data task's process/next is authorized by \"write\"."
            : $"{ProcessStatusLabel(process.Status)} — {string.Join(" ", process.Reasons.Count > 0 ? process.Reasons : [process.Summary])} process/next is never called."));
        preview.AddRange(ProcessFacts(process));
        return new JourneyPackValidation(checks, preview);
    }

    /// <summary>
    /// Payload &amp; Mapping Flow for a Rapportmottak submission: the dry run's generated payload, or a recorded run's stored evidence. MU, MM and
    /// Skolenærværsrapport stay Expected / Not observed unless a run step recorded runtime evidence for them. No network call is made.
    /// </summary>
    public async Task<PayloadFlowView?> PayloadFlowAsync(IntegrationJourneyDefinition journey, JourneyScenarioDescriptor scenario, PayloadFlowRequest request,
        JourneyPackContext context, CancellationToken ct)
    {
        if (journey.JourneyId != RapportmottakId || !SubmissionScenarios.Contains(scenario.ScenarioId, StringComparer.Ordinal)) return null;
        var flow = await messageFlows.GetAsync(context.EnvironmentId, ct);
        if (request.Run is { } run) return SkoleNaervaerPayloadFlow.ForRun(run, journey, flow.AltinnConfiguration, context);
        if (request.DryRunValues is not { Count: > 0 } values) return null;
        var contract = altinnContracts.Evaluate(context.EnvironmentId, flow.AltinnConfiguration.AppIdentifier, flow.AppContract);
        return SkoleNaervaerPayloadFlow.ForDryRun(SkoleNaervaerReportInput.Read(values), values.GetValueOrDefault(SkoleNaervaerReportInput.ReporterKey) ?? "",
            values.GetValueOrDefault(SkoleNaervaerReportInput.IdentitySourceKey) ?? "", contract, journey, flow.AltinnConfiguration, context);
    }

    /// <summary>The Altinn TT02 prerequisites that are configuration the operator must supply (never invented by BirkNext).</summary>
    internal static readonly string[] AltinnConfigurationKeys =
        ["unattended-provider", "idporten-client", "redirect-uri", "client-secret", "synthetic-reporter", "reporter-profile", "synthetic-data", "integration:report-intake"];

    /// <summary>
    /// The evidence limits a tester must see next to the scenarios. The Altinn and SkoleAdapter items follow current readiness; the others are
    /// what a source review of Skoletjenesten and MM established (not runtime verified) and change only when new evidence exists.
    /// </summary>
    public IReadOnlyList<JourneyEvidenceLimitation> EvidenceLimitations(IReadOnlyList<IntegrationJourneyView> journeys)
    {
        var report = journeys.FirstOrDefault(view => view.Journey.JourneyId == RapportmottakId);
        var missing = report?.Prerequisites.Where(item => AltinnConfigurationKeys.Contains(item.Key, StringComparer.Ordinal) && item.State != JourneyReadinessState.Ready)
            .Select(item => item.Label).ToArray() ?? [];
        var adapter = journeys.FirstOrDefault(view => view.Journey.JourneyId == UtdanningsdataId)?.Prerequisites.FirstOrDefault(item => item.Key == "source:SkoleAdapter");
        return
        [
            missing.Length > 0
                ? new("altinn-tt02", "Altinn TT02 submission", "Blocked: ID-porten TEST configuration incomplete",
                    $"Missing or unconfirmed: {string.Join(", ", missing)}. BirkNext never invents these values; no TT02 submission is possible until they are supplied.", TeamOperator)
                : new("altinn-tt02", "Altinn TT02 submission", "Configured, not verified",
                    "Every configuration prerequisite is present. Configured is not verified against the ID-porten client registration; only a supervised run shows it.", TeamOperator),
            new("mm-skole-contract", "MM → Skoletjenesten contract", "Not runtime verified",
                "A source review found the report contract compatible except drift D1 (timestamp text MM's XSD accepts can be refused by Skoletjenesten). No live MM → Skoletjenesten call has been observed by BirkNext.",
                $"{TeamMm}; {TeamSkole}"),
            adapter is { State: JourneyReadinessState.Ready }
                ? new("skoleadapter-source", "SkoleAdapter source", "Source available", $"{adapter.Detail} Its CDC contract still needs review before update, delete and late linkage can be assessed.", "SkoleAdapter team")
                : new("skoleadapter-source", "SkoleAdapter source", "Not available",
                    "Only the SkoleAdapter specification has been supplied; its source is not in Source Analysis. The education CDC contract is not verified, and update, delete and late linkage stay Not assessed.",
                    "SkoleAdapter team"),
            new("period-lock", "Period lock", "Operator required", $"{SkoleLockLifecycle} BirkNext never locks a period.", "Skole operations"),
            new("bufdata-delivery", "Bufdata delivery", "Not verifiable beyond prepared rows", SkoleDeliveryState, $"{TeamSkole}; Bufdata team"),
            new("graphql-coverage", "Coverage (GraphQL)", "Needs an approved client and role",
                "dekningsstatistikk needs Skole:SeDekningsstatistikk and the follow-up lists need Skole:SeOppfolgingsliste (with graded access for Kode 6/7). BirkNext has no approved client, so coverage is not verified.",
                TeamSkole),
            new("target-system-tests", "Skoletjenesten and MM automated tests", "Discovered, not executed",
                "Their unit and integration suites are known from a source review only; BirkNext holds no execution result for them (they need the .NET 10 SDK, private package feeds and emulators). Discovered is not passed.",
                $"Platform team; {TeamMm}; {TeamSkole}"),
        ];
    }

    /// <summary>Message verification evidence sources: shown, never gating (a verification records what it could and could not read).</summary>
    private static IEnumerable<JourneyPrerequisite> VerificationPrerequisites(JourneyPackContext context)
    {
        string[] only = [MessageVerificationScenario];
        foreach (var (key, label, tokens, component) in new[]
        {
            ("evidence:mm-telemetry", "MM outcome events (telemetry)", SkoleNaervaerMessageEvidence.MmTokens, "MM"),
            ("evidence:skole-telemetry", "Skoletjenesten intake log (telemetry)", SkoleNaervaerMessageEvidence.SkoleTokens, "Skoletjenesten"),
        })
        {
            var (platform, state, detail) = SkoleNaervaerMessageEvidence.Scope(context.Catalog, context.TrustedEnvironment, tokens, component);
            yield return new(key, label, JourneyPrerequisiteCategory.Observation, platform is not null ? JourneyReadinessState.Ready
                    : state == MessageEvidenceProviderState.NotConfigured ? JourneyReadinessState.NotConfigured : JourneyReadinessState.NotReady,
                platform is not null ? $"Configured: platform {platform.Id} names a telemetry source. Configured is not authorized: the backend identity's access is checked when it is read." : detail,
                Mandatory: false, Scenarios: only) { Source = TargetEnvironmentSource };
        }
        yield return new("evidence:skole-graphql", "Skoletjenesten GraphQL (coverage)", JourneyPrerequisiteCategory.Observation, JourneyReadinessState.NotAvailable,
            "No approved GraphQL client and role (Skole:SeDekningsstatistikk / Skole:SeOppfolgingsliste) exists for BirkNext.", Mandatory: false, Scenarios: only) { Source = BirkNextSource };
    }

    /// <summary>A message verification's run record: its correlations and assertions, computed from the evidence its observer collected.</summary>
    public IntegrationJourneyRun CompleteRun(IntegrationJourneyDefinition journey, JourneyScenarioDescriptor scenario, IntegrationJourneyRun run) =>
        scenario.ScenarioId != MessageVerificationScenario ? run
            : run with { MessageVerification = SkoleNaervaerMessageVerification.Record(messageEvidence?.Take(run.RunId),
                run.InputSummary.FirstOrDefault()?.Replace("Expected outcome: ", "", StringComparison.Ordinal) ?? "", run.CompletedAt ?? run.StartedAt) };

    /// <summary>Why required-field coverage (including Meldingshode) is unknown: no form schema has been read for the app.</summary>
    internal const string SchemaUnknownDetail = "Not evaluated: the app's form schema has not been read, so whether BirkNext fills every required field (including Meldingshode) is unknown. Read the public TT02 metadata.";

    // Provenance labels for readiness rows: where a value was resolved from, never the value itself.
    internal const string IqrSource = "Integration Quality Review";
    internal const string LocalSource = "Local execution config";
    internal const string MetadataSource = "Live Altinn metadata";
    internal const string SourceAnalysisSource = "Source Analysis";
    internal const string BirkNextSource = "BirkNext";
    internal const string TargetEnvironmentSource = "Target Environment → Integrations";
    private static string ClientIdSource(string clientSource) => clientSource.StartsWith("Local", StringComparison.Ordinal) ? LocalSource : IqrSource;

    /// <summary>
    /// The Altinn form's contract impact as one readiness row: unchanged, or changed with the BirkNext producer's compatibility, the first
    /// incompatible boundary and the downstream boundaries that are not evaluated. Blocking (mandatory) only when the form changed since the
    /// confirmed contract and BirkNext's payload may no longer satisfy it; compatible downstream is never claimed without evidence.
    /// </summary>
    internal static JourneyPrerequisite ContractImpactRow(ContractImpactResult? impact)
    {
        const string Key = "contract-impact", Label = "Contract change impact";
        if (impact is null)
            return new(Key, Label, JourneyPrerequisiteCategory.Contract, JourneyReadinessState.NotConfigured,
                "Not evaluated: no live form schema has been read (Read public TT02 metadata).", Mandatory: false, Scenarios: SubmissionScenarios) { Source = MetadataSource };
        var downstream = string.Join("; ", impact.Boundaries.Where(item => item.BoundaryId != "producer")
            .Select(item => $"{item.Label}: {CompatibilityLabel(item.Overall)}{(item.Potential ? " (potential)" : "")}"));
        if (!impact.BaselineAvailable)
            return new(Key, Label, JourneyPrerequisiteCategory.Contract, JourneyReadinessState.Partial,
                $"Not evaluated: no confirmed baseline of the form contract yet (fingerprint {Short(impact.CurrentFingerprint)}).", Mandatory: false, Scenarios: SubmissionScenarios) { Source = MetadataSource };
        if (!impact.Changed)
            return new(Key, Label, JourneyPrerequisiteCategory.Contract, JourneyReadinessState.Ready,
                $"Unchanged since the confirmed contract (fingerprint {Short(impact.CurrentFingerprint)}). Compatible is a contract statement, not runtime observed.",
                Mandatory: false, Scenarios: SubmissionScenarios) { Source = MetadataSource };
        var block = ProducerBlock(impact);
        var first = impact.FirstIncompatibleBoundary is { } boundary ? $" First incompatible boundary: {boundary.Label} — {boundary.Reasons.FirstOrDefault()}" : "";
        return new(Key, Label, JourneyPrerequisiteCategory.Contract, block is null ? JourneyReadinessState.Partial : JourneyReadinessState.NotReady,
            $"Contract changed ({impact.Changes.Count} change(s), fingerprint {Short(impact.PreviousFingerprint ?? "")} → {Short(impact.CurrentFingerprint)}). " +
            $"{block ?? "BirkNext producer: compatible."}{first} Downstream: {downstream}. Overall: {CompatibilityLabel(impact.Overall)}.",
            Mandatory: block is not null, Scenarios: SubmissionScenarios) { Source = MetadataSource };
    }

    /// <summary>Why BirkNext's payload may no longer satisfy a changed form contract (structurally or by business meaning), or null.</summary>
    internal static string? ProducerBlock(ContractImpactResult impact) =>
        impact.Changed && impact.Boundaries.FirstOrDefault(item => item.BoundaryId == "producer") is { Overall: not ContractCompatibility.Compatible } producer
            ? $"BirkNext producer: {CompatibilityLabel(producer.Overall)} — {string.Join(" ", producer.Reasons.Take(3))} Update the payload mapping or review the change, then accept the reviewed contract; nothing is generated for unknown business fields."
            : null;

    internal static string CompatibilityLabel(ContractCompatibility compatibility) => compatibility switch
    {
        ContractCompatibility.Compatible => "Compatible",
        ContractCompatibility.Breaking => "Breaking change",
        ContractCompatibility.NeedsReview => "Needs review",
        _ => "Not evaluated",
    };

    internal static string ProcessStatusLabel(AltinnProcessContractStatus status) => status switch
    {
        AltinnProcessContractStatus.ConfirmedForCurrentOperation => "Confirmed for current operation",
        AltinnProcessContractStatus.Partial => "Partial",
        AltinnProcessContractStatus.Conflict => "Conflict",
        _ => "Not confirmed",
    };

    /// <summary>The process contract as a tester reads it: what is known for the one intended operation, and what is not (the full topology).</summary>
    internal static IReadOnlyList<JourneyEvidenceFact> ProcessFacts(AltinnProcessAssessment process)
    {
        var facts = new List<JourneyEvidenceFact>
        {
            new("Process contract", ProcessStatusLabel(process.Status)),
            new("Process source", process.Sources.Count > 0 ? string.Join(" + ", process.Sources) : "None"),
        };
        if (process.DeclaredTaskCount is { } count) facts.Add(new("Declared process tasks", count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (process.PlannedTasks.Count > 0) facts.Add(new("Task", string.Join(" → ", process.PlannedTasks.Select(AltinnProcessContractEvaluator.Describe))));
        facts.Add(new("Process action", process.Operation));
        facts.Add(new("Execution bound", $"Maximum {process.MaxTransitions} process transition(s)"));
        facts.Add(new("Expected verification", "Returned AppProcessState must indicate process completion; otherwise stop."));
        facts.Add(new("BPMN topology", process.TopologyKnown ? "Known from process.bpmn" : "Not known (not needed for this operation)"));
        facts.Add(new("process.bpmn", process.StaticDefinition));
        return facts;
    }

    public bool Matches(JourneyIntegrationRequirement requirement, IntegrationDefinition integration) => requirement.Key switch
    {
        EducationCdc => integration.Kind == IntegrationKind.EventHub && (integration.SourceResource?.Trim() ?? "").Split('.') is { Length: 3 } parts
            && SkoleNaervaerFixtures.ForTable(parts[2]) is not null,
        ReportIntake => integration.Kind == IntegrationKind.HttpApi
            && ArchitectureRuleEvaluator.MatchesAny(["Altinn"], integration.Producer, integration.SourceSystem, integration.SystemName, integration.DisplayName)
            && ArchitectureRuleEvaluator.MatchesAny(["MU", "Meldingsutveksleren"], integration.Consumer.DisplayName, integration.Consumer.LogicalName, integration.DestinationSystem),
        PeriodClosed => integration.Kind == IntegrationKind.ServiceBus
            && (ArchitectureRuleEvaluator.MatchesAny([PeriodClosedEvent], integration.EndpointOrTopic, integration.DisplayName)
                || (ArchitectureRuleEvaluator.MatchesAny(["Skolenærværsrapport"], integration.Producer, integration.SourceSystem)
                    && ArchitectureRuleEvaluator.MatchesAny(["Bufdata"], integration.Consumer.DisplayName, integration.Consumer.LogicalName, integration.DestinationSystem))),
        _ => false,
    };

    public async Task<IReadOnlyList<JourneyPrerequisite>> PrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct)
    {
        // MU, MM, Skoletjenesten and SkoleAdapter are separate repositories, usually uploaded as separate archives, so every snapshot bound in
        // Integration Quality Review (the canonical evidence) counts. A component found only in an unbound stored snapshot is Partial — named
        // so it can be bound, never source verified.
        var bound = context.Bindings.Select(binding => binding.SnapshotId).ToHashSet();
        JourneyPrerequisite Source(string component, string[] tokens, string? role)
        {
            // The component belongs to a logical source: only that source's binding is authoritative for it. A system that is not one of the
            // pack's sources (an external consumer) is looked for in every bound snapshot.
            var sourceRole = TestPackSources.Role(this, role);
            var authoritative = sourceRole is null ? [] : TestPackSources.Authoritative(this, context, sourceRole).Select(snapshot => snapshot.Id).ToHashSet();
            var trusted = authoritative.Count > 0 ? authoritative : bound;
            var found = context.SourceSnapshots.OrderBy(snapshot => trusted.Contains(snapshot.Id) ? 0 : 1)
                .SelectMany(snapshot => (snapshot.Architecture?.Components ?? []).Select(item => (Snapshot: snapshot, Component: item)))
                .FirstOrDefault(pair => ArchitectureRuleEvaluator.MatchesAny(tokens, pair.Component.Name, pair.Component.LogicalName, pair.Component.SourceProject));
            var status = sourceRole is null ? null : TestPackSources.Status(this, context, sourceRole);
            var label = sourceRole?.Label ?? component;
            return new JourneyPrerequisite($"source:{component}", $"{component} source", JourneyPrerequisiteCategory.SourceEvidence,
                found.Component is null ? JourneyReadinessState.NotReady : trusted.Contains(found.Snapshot.Id) ? JourneyReadinessState.Ready : JourneyReadinessState.Partial,
                found.Component is not null && trusted.Contains(found.Snapshot.Id)
                    ? $"Source verified: component {found.Component.Name} in snapshot {found.Snapshot.Archive.FileName} (bound in Integration Quality Review)."
                    : found.Component is not null
                        ? $"Needs {label} source evidence: component {found.Component.Name} is in {found.Snapshot.Archive.FileName}, which is available but not bound in Integration Quality Review → Evidence."
                        : status is null ? $"Not source verified: {component} source is not present in Source Analysis."
                        : status.State == TestPackSourceState.Bound
                            ? $"Not found in the bound {label} source ({string.Join(", ", status.BoundArchives)}): no {component} component."
                            : $"Needs {label} source evidence: {(status.State == TestPackSourceState.Missing ? $"no {label} source is imported" : status.Detail.TrimEnd('.'))}.")
                { Source = SourceAnalysisSource };
        }

        switch (journey.JourneyId)
        {
            case UtdanningsdataId:
                return [Source("SkoleAdapter", ["SkoleAdapter"], M2lbSource), Source("Utdanning", ["Utdanning"], M2lbSource)];

            case RapportmottakId:
            {
                var flow = await messageFlows.GetAsync(context.EnvironmentId, ct);
                var config = flow.AltinnConfiguration;
                // The report schema's snapshot is the canonical IQR evidence binding of the report-intake integration; the Altinn configuration
                // only names which XML Schema in it. A legacy snapshot id stored in the configuration is used only while no binding exists.
                var reportBinding = ReportIntakeBinding(journey, context);
                var schemaSnapshot = reportBinding?.SnapshotId ?? config.SourceSnapshotId;
                var legacyMismatch = reportBinding is not null && config.SourceSnapshotId is { } legacy && legacy != reportBinding.SnapshotId;
                var xsdBound = schemaSnapshot is not null && !string.IsNullOrWhiteSpace(config.SourceContractId) && !legacyMismatch;
                var xsdInSource = context.SourceSnapshots.Any(snapshot => snapshot.EvidenceDomains?.Contracts.Contracts.Any(contract => contract.Type == SourceContractType.XmlSchema) == true);
                var contract = altinnContracts.Evaluate(context.EnvironmentId, config.AppIdentifier, flow.AppContract);
                var signIn = altinnPolicy.SignInProblems(config);
                var unattended = altinnPolicy.UnattendedProblems(config);
                var altinnEnvironment = AltinnTestingPolicy.AltinnEnvironmentBlock(config);
                var options = altinnPolicy.Options.UnattendedIdPortenTest;
                var clientId = altinnPolicy.ClientId(config, out var clientSource, out _);
                var clientConfigured = config.AuthenticationType == ExternalAuthenticationType.IDPorten && clientId is not null
                    && config.ConfiguredScopes.Contains("openid", StringComparer.Ordinal);
                // Configured ≠ confirmed: a reporter counts as configured before its Altinn profile is confirmed (shown in its own row).
                var reporters = options.Reporters.Count(item => AltinnTestingPolicy.ReporterConfigurationProblem(item) is null);
                var reporterProblem = options.Reporters.Select(AltinnTestingPolicy.ReporterConfigurationProblem).FirstOrDefault(item => item is not null);
                var publicMetadata = contract.Sources.FirstOrDefault(item => item.Source == AltinnContractSourceKind.PublicTt02Metadata);
                var confirmation = altinnPolicy.AppPolicy(config.AppIdentifier, flow.AppContract?.ObservedPolicy, out var policyDetail);
                // A confirmed policy must also allow the roles the configured reporters act in (unattended aliases, or PRIV for the fallback).
                var roles = options.Reporters.Select(item => item.AltinnRole).Append("PRIV").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var policyProblem = confirmation is null ? null : roles.Select(role => AltinnTestingPolicy.PolicyProblem(confirmation, role)).FirstOrDefault(item => item is not null);
                var policyConfirmed = confirmation is not null && policyProblem is null;
                var awaitingProfile = altinnPolicy.ReportersAwaitingProfile;
                var testData = new SkoleNaervaerTestData(altinnPolicy.Options, testDataCatalog);
                var knownChildren = testData.Children(knownToPerson: true);
                var defaultChild = testData.DefaultChild(knownToPerson: true);
                var declaredSet = config.ApprovedSyntheticData && !string.IsNullOrWhiteSpace(config.SyntheticIdentitySetReference);
                return
                [
                    new JourneyPrerequisite("altinn-environment", "Altinn TT02 environment", JourneyPrerequisiteCategory.TrustedEnvironment,
                        altinnEnvironment is null ? JourneyReadinessState.Ready : JourneyReadinessState.NotReady,
                        altinnEnvironment is null ? "Altinn environment Test (TT02) with Production blocked (Integration Quality Review → Altinn test configuration)."
                            : altinnEnvironment + KnownNotStored(context, "altinn-environment", config.Environment == ExternalTestEnvironment.Unknown)) { Source = IqrSource },
                    new JourneyPrerequisite("altinn-app", "Approved Altinn app", JourneyPrerequisiteCategory.Integration,
                        contract.AppApproved ? JourneyReadinessState.Ready : string.IsNullOrWhiteSpace(config.AppIdentifier) ? JourneyReadinessState.NotConfigured : JourneyReadinessState.NotReady,
                        contract.AppDetail + KnownNotStored(context, "altinn-app-route", string.IsNullOrWhiteSpace(config.AppIdentifier)), Scenarios: AltinnCallingScenarios) { Source = IqrSource },
                    // Metadata only: the built-in reference supplies the app's declared facts for configuration and planning. It never makes the
                    // app approved, deployed, reachable or runnable — those rows stay their own.
                    AppReferenceRow(context),
                    // Reporter authentication: unattended (default) or the interactive fallback; either mode can satisfy it. Each unattended part is
                    // reported on its own below, so a missing secret or reporter is visible as exactly that.
                    new JourneyPrerequisite("reporter-auth", "Reporter authentication", JourneyPrerequisiteCategory.Authentication,
                        unattended.Count == 0 || signIn.Count == 0 ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        unattended.Count == 0 ? "Unattended: an approved synthetic TestID reporter is authenticated by the backend when the run starts. No sign-in step."
                            : signIn.Count == 0 ? "Only the interactive fallback is configured: the tester must sign in with TestID before running. Unattended authentication is not ready."
                            : $"Neither unattended nor interactive ID-porten TEST authentication is configured. Unattended: {string.Join(" ", unattended)}",
                        Scenarios: AltinnCallingScenarios) { Source = LocalSource },
                    new JourneyPrerequisite("unattended-provider", "Unattended ID-porten TEST provider", JourneyPrerequisiteCategory.Authentication,
                        options.Enabled ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        options.Enabled ? "Available: ID-porten TEST headless login for synthetic TestID users (TEST only), then the Altinn TT02 token exchange."
                            : $"Disabled (backend {AltinnTestingOptions.Section}:UnattendedIdPortenTest:Enabled).", Mandatory: false) { Source = LocalSource },
                    new JourneyPrerequisite("idporten-client", "ID-porten TEST client", JourneyPrerequisiteCategory.Authentication,
                        clientConfigured ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        clientConfigured ? "Configured: ID-porten authentication, client id and the openid scope (Integration Quality Review → Altinn test configuration)."
                            : "Missing: ID-porten authentication type, client id or the openid scope (Integration Quality Review → Altinn test configuration).", Mandatory: false) { Source = clientConfigured ? ClientIdSource(clientSource) : IqrSource },
                    new JourneyPrerequisite("redirect-uri", "Registered redirect URI", JourneyPrerequisiteCategory.Authentication,
                        altinnPolicy.UnattendedRedirect() is not null ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        altinnPolicy.UnattendedRedirect() is not null
                            ? "Configured in the backend; ID-porten's redirect must match it exactly (value not shown here)."
                            : $"Missing: the redirect URI registered for the ID-porten TEST client (backend {AltinnTestingOptions.Section}:UnattendedIdPortenTest:RedirectUri): https, or http on loopback.", Mandatory: false) { Source = LocalSource },
                    new JourneyPrerequisite("client-secret", "Client secret", JourneyPrerequisiteCategory.Authentication,
                        altinnPolicy.UnattendedSecretAvailable ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        altinnPolicy.UnattendedSecretAvailable ? "Available to the backend (value never shown)." : "Missing: the ID-porten TEST client secret is not available to the backend (environment variable or secret store).", Mandatory: false) { Source = LocalSource },
                    new JourneyPrerequisite("synthetic-reporter", "Synthetic reporter", JourneyPrerequisiteCategory.SyntheticData,
                        reporters > 0 ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        reporters > 0 ? testData.DefaultReporter is { } onlyReporter
                                ? $"Resolved: {onlyReporter} — the only usable approved synthetic TestID reporter (identifier stays in the backend). Configured is not authenticated."
                                : $"{reporters} approved synthetic TestID reporter alias(es) configured (identifiers stay in the backend). Configured is not authenticated."
                            : reporterProblem is not null ? $"Not usable: {reporterProblem}"
                            : $"Missing: no synthetic TestID reporter alias is configured (backend {AltinnTestingOptions.Section}:UnattendedIdPortenTest:Reporters).", Mandatory: false) { Source = SkoleNaervaerTestData.Source },
                    new JourneyPrerequisite("reporter-role", "Reporter role", JourneyPrerequisiteCategory.Authorization,
                        reporters > 0 ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        reporters > 0 ? "PRIV: the reporter submits for their own party (the only supported role; acting for an organisation is not part of this run)."
                            : "Not applicable until a synthetic reporter alias is configured; only PRIV is supported.", Mandatory: false) { Source = SkoleNaervaerTestData.Source },
                    new JourneyPrerequisite("altinn-auth", "Interactive TestID sign-in (fallback)", JourneyPrerequisiteCategory.Authentication,
                        signIn.Count == 0 ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        signIn.Count == 0 ? "Optional fallback configured: a tester can sign in with TestID in the browser instead." : $"Optional fallback not configured: {string.Join(" ", signIn)}", Mandatory: false) { Source = IqrSource },
                    new JourneyPrerequisite("reporter-profile", "Reporter Altinn profile", JourneyPrerequisiteCategory.SyntheticData,
                        reporters > 0 && awaitingProfile == 0 ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        reporters == 0 ? "Not applicable until a synthetic reporter alias is configured."
                            : awaitingProfile == 0 ? "Confirmed: every configured synthetic reporter has an Altinn TT02 profile (Altinn's token exchange requires one)."
                            : $"Not confirmed for {awaitingProfile} reporter alias(es): Altinn's token exchange fails for a person without an Altinn TT02 profile (AltinnProfileConfirmed).", Mandatory: false) { Source = SkoleNaervaerTestData.Source },
                    new JourneyPrerequisite("app-policy", "App authorization policy", JourneyPrerequisiteCategory.Authorization,
                        policyConfirmed ? JourneyReadinessState.Ready : JourneyReadinessState.NotReady, policyProblem is null ? policyDetail : $"{policyDetail} Not compatible: {policyProblem}",
                        Scenarios: SubmissionScenarios) { Source = LocalSource },
                    new JourneyPrerequisite("submission-contract", "Altinn app contract", JourneyPrerequisiteCategory.Contract,
                        contract.Complete ? JourneyReadinessState.Ready : JourneyReadinessState.NotReady,
                        contract.Complete ? $"Complete: form data type {contract.FormDataTypeId}, {contract.Bindings.Count} verified fields, process confirmed for {contract.Process.Transitions} bounded transition(s)."
                            : $"Incomplete — {string.Join(" ", contract.Missing)}", Scenarios: SubmissionScenarios) { Source = MetadataSource },
                    new JourneyPrerequisite("public-metadata", "Public TT02 process metadata", JourneyPrerequisiteCategory.Contract,
                        publicMetadata is not null ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        publicMetadata is not null
                            ? $"Loaded {publicMetadata.CapturedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC from the public TT02 metadata of {publicMetadata.AppId} (anonymous reads; fingerprint {Short(publicMetadata.Fingerprint)})."
                            : "Missing: select \"Read public TT02 metadata\" (anonymous GETs only) after restarting the backend on the current commit.", Mandatory: false) { Source = MetadataSource },
                    // Shown on its own so the tester sees what is known for the one intended operation; the contract row above already gates it.
                    new JourneyPrerequisite("process-contract", "Process contract (current operation)", JourneyPrerequisiteCategory.Contract,
                        contract.Process.Status switch
                        {
                            AltinnProcessContractStatus.ConfirmedForCurrentOperation => JourneyReadinessState.Ready,
                            AltinnProcessContractStatus.Partial => JourneyReadinessState.Partial,
                            _ => JourneyReadinessState.NotReady,
                        },
                        contract.Process.Confirmed ? contract.Process.Summary : $"{ProcessStatusLabel(contract.Process.Status)} — {string.Join(" ", contract.Process.Reasons)}",
                        Scenarios: SubmissionScenarios, Mandatory: false) { Source = MetadataSource },
                    ContractImpactRow(SkoleNaervaerContractProfile.Analyze(flow.AppContract)),
                    new JourneyPrerequisite("report-xsd", "Report XSD (MM validation)", JourneyPrerequisiteCategory.Contract,
                        xsdBound ? JourneyReadinessState.Partial : JourneyReadinessState.NotReady,
                        legacyMismatch ? "Review required: the XML Schema contract was chosen in another snapshot than the one bound in Integration Quality Review → Evidence. Choose the contract again; nothing was reassigned automatically."
                        : xsdBound ? "An XML Schema from Source Analysis is bound to the message flow. XSD exists ≠ runtime payload validated."
                            : xsdInSource ? "An XML Schema exists in Source Analysis but is not bound to the message flow. XSD exists ≠ runtime payload validated."
                            : $"XSD {config.ContractVersion ?? "(version unknown)"} is documented only; not source verified.", Mandatory: false) { Source = SourceAnalysisSource },
                    new JourneyPrerequisite("test-reporter", "Approved test reporter", JourneyPrerequisiteCategory.SyntheticData,
                        !string.IsNullOrWhiteSpace(config.ReporterReference) && config.ReporterApprovedForTest ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        !string.IsNullOrWhiteSpace(config.ReporterReference) && config.ReporterApprovedForTest ? "An approved test reporter reference is configured." : "No approved synthetic test reporter (institution/foster home test actor) is configured.",
                        Scenarios: SubmissionScenarios) { Source = IqrSource },
                    // Resolved from approved test data first (by alias, never generated); else the declared IQR identity set, with the child typed per run.
                    new JourneyPrerequisite("synthetic-data", "Known synthetic child (test data)", JourneyPrerequisiteCategory.SyntheticData,
                        knownChildren.Count > 0 || declaredSet ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        knownChildren.Count > 0
                            ? defaultChild is not null
                                ? $"Resolved: {defaultChild.Alias} — an approved synthetic child, declared known to Person (not verified by BirkNext). The identifier stays in the backend."
                                : $"{knownChildren.Count} approved synthetic children and no single default: the tester chooses one per run (by alias)."
                        : declaredSet
                            ? $"Declared test prerequisite: approved synthetic identity set {config.SyntheticIdentitySetReference}. The child's synthetic identifier is entered per run; whether Person knows it is not verified by BirkNext."
                        : testData.FirstChildProblem is { } childProblem ? $"No approved synthetic child could be resolved. Not usable: {childProblem}"
                        : $"No approved synthetic child could be resolved: add one to the approved test data ({AltinnTestingOptions.Section}:SyntheticChildren) or declare an approved identity set in Integration Quality Review. BirkNext never generates one for this.",
                        Scenarios: SubmissionScenarios) { Source = knownChildren.Count > 0 || !declaredSet ? SkoleNaervaerTestData.Source : IqrSource },
                    new JourneyPrerequisite("meldingshode", "Meldingshode (first-run risk)", JourneyPrerequisiteCategory.Contract,
                        contract.Fields.Count == 0 ? JourneyReadinessState.NotReady
                        : contract.RequiredUnboundFields.Count == 0 ? JourneyReadinessState.Ready : JourneyReadinessState.Partial,
                        contract.Fields.Count == 0 ? SchemaUnknownDetail
                        : contract.RequiredUnboundFields.Count == 0 ? "Every required form field is filled by BirkNext."
                            : $"Risk, not a blocker: {contract.RequiredUnboundFields.Count} required field(s) BirkNext does not fill ({string.Join(", ", contract.RequiredUnboundFields.Take(6).Select(field => field.Path))}). They are expected from the app; Altinn's validation in the first live run shows it, before any process transition.",
                        Scenarios: SubmissionScenarios, Mandatory: false) { Source = MetadataSource },
                    new JourneyPrerequisite("backend-build", "Backend build", JourneyPrerequisiteCategory.Executor, JourneyReadinessState.Ready,
                        $"{BackendBuild.Describe()} Restart the backend after changing branch, commit or configuration, then read the public TT02 metadata again.", Mandatory: false) { Source = BirkNextSource },
                    // Downstream steps (MU, MM, Skolenærværsrapport) are shown but do not gate the Altinn submission: without them those steps stay
                    // Not assessed, so a run can reach Partial at most.
                    Source("MU", ["MU", "Meldingsutveksleren", "Meldingsutveksler"], MuSource) with { Mandatory = false },
                    Source("MM", ["MM", "Meldingsutvekslermottak"], M2lbSource) with { Mandatory = false },
                    Source("Skolenærværsrapport", ["Skolenærværsrapport", "Skolenaervaersrapport"], M2lbSource) with { Mandatory = false },
                    new JourneyPrerequisite("report-verifier", "Skolenærværsrapport verification", JourneyPrerequisiteCategory.DownstreamVerification, JourneyReadinessState.NotAvailable,
                        "No read contract for the report service exists, so report state, mapping and Person lookup outcome cannot be verified.", Mandatory: false) { Source = BirkNextSource },
                    .. VerificationPrerequisites(context),
                ];
            }

            case PeriodeavslutningId:
            {
                var boundIds = context.Bindings.Select(binding => binding.SnapshotId).ToHashSet();
                var (channelSnapshot, channel) = context.SourceSnapshots.OrderBy(snapshot => boundIds.Contains(snapshot.Id) ? 0 : 1)
                    .SelectMany(snapshot => (snapshot.Architecture?.MessagingChannels ?? []).Select(item => (Snapshot: snapshot, Channel: item)))
                    .FirstOrDefault(pair => ArchitectureRuleEvaluator.MatchesAny([PeriodClosedEvent], pair.Channel.Name, pair.Channel.EntityName));
                var channelBound = channelSnapshot is not null && boundIds.Contains(channelSnapshot.Id);
                return
                [
                    new("domain-trigger", "Safe period-closure trigger", JourneyPrerequisiteCategory.Trigger, JourneyReadinessState.NotReady,
                        $"Operator required. {SkoleLockLifecycle} BirkNext never locks a period and never publishes the event directly."),
                    new("event-contract", $"{PeriodClosedEvent} contract", JourneyPrerequisiteCategory.Contract,
                        channel is null ? JourneyReadinessState.NotReady : channelBound ? JourneyReadinessState.Ready : JourneyReadinessState.Partial,
                        channel is null ? "Documented in the architecture diagram only; no Service Bus channel with this name is present in Source Analysis (not source verified)."
                            : channelBound ? $"Source verified: Service Bus channel {channel.Name} ({channel.Type}) in snapshot {channelSnapshot!.Archive.FileName} (bound in Integration Quality Review)."
                            : $"Not source verified: Service Bus channel {channel.Name} ({channel.Type}) is in snapshot {channelSnapshot!.Archive.FileName}, which is not bound in Integration Quality Review → Evidence."),
                    Source("Skolenærværsrapport", ["Skolenærværsrapport", "Skolenaervaersrapport"], M2lbSource),
                    Source("Bufdata", ["Bufdata"], null),
                    new("bufdata-verifier", "Bufdata verification", JourneyPrerequisiteCategory.DownstreamVerification, JourneyReadinessState.NotAvailable,
                        "No Bufdata read/verification surface (dataset metadata, storage metadata or reporting status) is available."),
                    new("pii-check", "Dataset privacy check (no fødselsnummer/DUF)", JourneyPrerequisiteCategory.DownstreamVerification, JourneyReadinessState.NotAvailable,
                        "Not verified: without dataset evidence the absence of personal identifiers cannot be checked."),
                ];
            }

            default:
                return [];
        }
    }

    private static string Short(string fingerprint) => fingerprint.Length > 12 ? fingerprint[..12] : fingerprint;
}

/// <summary>The running backend's build (assembly informational version, which carries the git commit), so a stale backend is visible.</summary>
public static class BackendBuild
{
    public static string Version { get; } = typeof(BackendBuild).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion ?? "unknown";

    /// <summary>"Running commit 8c68d872." when the version carries a commit; otherwise the version itself.</summary>
    public static string Describe(string? version = null)
    {
        version ??= Version;
        var plus = version.IndexOf('+');
        var commit = plus >= 0 ? version[(plus + 1)..] : "";
        return System.Text.RegularExpressions.Regex.IsMatch(commit, "^[0-9a-f]{7,40}$") ? $"Running commit {commit[..8]}." : $"Running version {version} (no commit recorded).";
    }
}
