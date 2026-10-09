using BirkNext.Api.Services.ActiveEventTesting.Providers.SkoleNaervaer;
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
public sealed class SkoleNaervaerJourneyPack(IIntegrationMessageFlowStore messageFlows) : IIntegrationJourneyPack
{
    public const string Id = "skolenaervaer";
    public const string UtdanningsdataId = "utdanningsdata";
    public const string RapportmottakId = "rapportmottak";
    public const string PeriodeavslutningId = "periodeavslutning";
    public const string PeriodClosedEvent = "SkolenaervaersperiodeLukket";

    private const string EducationCdc = "education-cdc";
    private const string ReportIntake = "report-intake";
    private const string PeriodClosed = "period-closed";
    private const string DiagramBasis = "Approved Skolenærvær architecture diagram and the SkoleAdapter/Skolenærvær specifications (external, documented). Not source verified.";

    public string PackId => Id;
    public string PackVersion => "1";
    public string DisplayName => "Skolenærvær Testing";
    public string Description => "Three Skolenærvær journeys: Utdanningsdata (CDC), Rapportmottak (Altinn → MU → MM → Skolenærværsrapport) and Periodeavslutning (Service Bus → Bufdata).";

    public IReadOnlyList<string> Limitations =>
    [
        "The documented message-flow pilot in Integration Quality Review names the intake component \"Skolenærværsmottaket\" and the report service \"Skoletjenesten\"; this pack uses the architecture diagram's names MM and Skolenærværsrapport. The correspondence is a documentation alias, not source verified.",
        "No MU, MM, Skolenærværsrapport, Bufdata or Altinn source, contract or read endpoint is available to BirkNext; their steps cannot be observed or verified yet.",
        "BirkNext never publishes SkolenaervaersperiodeLukket itself; period closure must be triggered through a safe domain workflow, which does not exist in the evidence.",
    ];

    public IReadOnlyList<IntegrationJourneyDefinition> Journeys { get; } =
    [
        new()
        {
            PackId = Id, JourneyId = UtdanningsdataId, DisplayName = "Utdanningsdata", Order = 1, ExecutionMode = JourneyExecutionMode.DelegatedToActiveEvent,
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
            PackId = Id, JourneyId = RapportmottakId, DisplayName = "Rapportmottak", Order = 2, ExecutionMode = JourneyExecutionMode.JourneyRunner,
            Description = "A private institution or foster home submits a school-attendance report through Altinn; MU receives and stores the encrypted message; MM retrieves, decrypts, validates (XSD) and maps it — transport/intake only, no Person lookup; Skolenærværsrapport receives the report and performs the Person lookup.",
            Integrations = [new(ReportIntake, "Skolenærvær – Altinn report intake", IntegrationKind.HttpApi, "External reporting party (Altinn)", "MU", "Report submission through the configured Altinn test environment")],
            Steps =
            [
                new("altinn-submit", "Submit report to the Altinn test environment", JourneyStepKind.ExternalSubmission, "External reporter", "Submission request sent", IntegrationKey: ReportIntake, FromComponent: "External reporter", ToComponent: "Altinn"),
                new("altinn-accept", "Altinn accepts the submission", JourneyStepKind.ExternalAcceptance, "Altinn", "Altinn returned an accepted submission reference", IntegrationKey: ReportIntake, FromComponent: "Altinn", ToComponent: "Altinn"),
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
                new("valid-report", "Valid report", "One synthetic, schema-valid report submitted by an approved test reporter; every step is evidenced separately.", JourneyScenarioSupport.Supported),
                new("invalid-xsd", "Invalid XSD", "A reviewed schema-invalid report rejected by MM's XSD validation.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: no reviewed invalid payload fixture and no submission contract exist; arbitrary XML is never corrupted."),
                new("person-not-found", "Person not found", "A report for a synthetic child Person does not know.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: the expected behavior (reject, hold, retry) is only documented for an older design, is not source verified and cannot be observed."),
                new("period-locked", "Period locked", "A report submitted after the reporting period is locked.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: period-lock behavior lives in Skolenærværsrapport and is not source verified; test code never re-implements it."),
                new("mapping-validation", "Mapping validation", "Mapped values in Skolenærværsrapport match the synthetic submission.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: no read contract for the report service exists to verify mapped values."),
                new("duplicate-report", "Duplicate report", "The same report submitted twice (exact repeat ≠ natural-key duplicate).", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: duplicate/idempotency semantics for reports are not defined."),
                new("retrieval-decrypt-failure", "Message retrieval/decrypt failure", "MM cannot retrieve or decrypt the payload.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: no safe test-environment support for retrieval or cryptographic fault injection; destructive testing is never improvised."),
            ],
            EvidenceBasis = DiagramBasis,
            Limitations = ["Altinn accepted ≠ report processed: each step is evidenced on its own.", "Correlation can use only a stable test reference returned by the real Altinn/MU contract; none is known, so at best aggregate activity could be reported."],
        },
        new()
        {
            PackId = Id, JourneyId = PeriodeavslutningId, DisplayName = "Periodeavslutning", Order = 3, ExecutionMode = JourneyExecutionMode.JourneyRunner,
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
                new("close-valid-period", "Close valid period", "Close one synthetic period through a safe domain workflow and follow the emitted event to Bufdata.", JourneyScenarioSupport.Supported),
                new("event-emitted", "Event emitted", $"{PeriodClosedEvent} is emitted once for the closed period.", JourneyScenarioSupport.NotAssessed, "Not assessed: requires the domain trigger and a broker observer."),
                new("event-metadata", "Event metadata correct", "The event carries the expected period metadata.", JourneyScenarioSupport.NotAssessed, "Not assessed: the event contract is not source verified."),
                new("bufdata-receives", "Bufdata receives dataset", "Bufdata processed the dataset for the closed period.", JourneyScenarioSupport.NotAssessed, "Not assessed: no Bufdata read/verification surface exists."),
                new("pii-absent", "PII absent", "The delivered dataset contains no fødselsnummer or DUF.", JourneyScenarioSupport.NotAssessed, "Not verified: no dataset evidence is available."),
                new("coverage-metadata", "Coverage/row count metadata correct", "Row count and coverage metadata match the synthetic period.", JourneyScenarioSupport.NotAssessed, "Not assessed: no dataset metadata evidence is available."),
            ],
            EvidenceBasis = DiagramBasis,
            Limitations = ["Service Bus accepted ≠ Bufdata processed: broker, consumer and dataset evidence are separate steps.", "BirkNext never sends SkolenaervaersperiodeLukket directly."],
        },
    ];

    /// <summary>Responsibility rules from the approved architecture. Evaluated on Source Analysis dependency evidence; not assessed without source.</summary>
    public IReadOnlyList<ArchitectureResponsibilityRule> Rules { get; } =
    [
        new("mm-no-person", "MM performs transport/intake only — no Person lookup", "MM", ["MM"], "Person", ["Person"], ArchitectureRuleExpectation.MustNotDependOn,
            "MM retrieves, decrypts, validates (XSD) and maps reports; Person lookup belongs to Skolenærværsrapport."),
        new("skoleadapter-no-person", "SkoleAdapter performs no Person lookup", "SkoleAdapter", ["SkoleAdapter"], "Person", ["Person"], ArchitectureRuleExpectation.MustNotDependOn,
            "SkoleAdapter forwards education data with the external child reference; Utdanning resolves the child."),
        new("utdanning-person", "Utdanning owns the Person lookup for BiRK education data", "Utdanning", ["Utdanning"], "Person", ["Person"], ArchitectureRuleExpectation.MustDependOn,
            "Utdanning links BiRK education data to the correct child through Person."),
        new("rapport-person", "Skolenærværsrapport owns the Person lookup for submitted reports", "Skolenærværsrapport", ["Skolenærværsrapport", "Skolenaervaersrapport"], "Person", ["Person"],
            ArchitectureRuleExpectation.MustDependOn, "Skolenærværsrapport performs the Person lookup for incoming reports."),
    ];

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
        var architecture = context.SourceSnapshots.FirstOrDefault(snapshot => snapshot.Architecture is not null);
        JourneyPrerequisite Source(string component, string[] tokens)
        {
            var found = architecture?.Architecture?.Components.FirstOrDefault(item => ArchitectureRuleEvaluator.MatchesAny(tokens, item.Name, item.LogicalName, item.SourceProject));
            return new($"source:{component}", $"{component} source", JourneyPrerequisiteCategory.SourceEvidence,
                found is null ? JourneyReadinessState.NotReady : JourneyReadinessState.Ready,
                found is null ? $"Not source verified: {component} source is not present in Source Analysis." : $"Source verified: component {found.Name} in snapshot {architecture!.Archive.FileName}.");
        }

        switch (journey.JourneyId)
        {
            case UtdanningsdataId:
                return [Source("SkoleAdapter", ["SkoleAdapter"]), Source("Utdanning", ["Utdanning"])];

            case RapportmottakId:
            {
                var flow = await messageFlows.GetAsync(context.EnvironmentId, ct);
                var config = flow.AltinnConfiguration;
                var testEnvironment = config.Environment is ExternalTestEnvironment.Test or ExternalTestEnvironment.QualityAssurance or ExternalTestEnvironment.Staging or ExternalTestEnvironment.Development;
                var xsdBound = config.SourceSnapshotId is not null && !string.IsNullOrWhiteSpace(config.SourceContractId);
                var xsdInSource = context.SourceSnapshots.Any(snapshot => snapshot.EvidenceDomains?.Contracts.Contracts.Any(contract => contract.Type == SourceContractType.XmlSchema) == true);
                return
                [
                    new("altinn-endpoint", "Altinn test endpoint", JourneyPrerequisiteCategory.Integration,
                        testEnvironment && !string.IsNullOrWhiteSpace(config.EndpointReference) ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        testEnvironment && !string.IsNullOrWhiteSpace(config.EndpointReference)
                            ? $"Endpoint reference configured for the {config.Environment} environment (Integration Quality Review → Message flow)."
                            : "No approved Altinn test environment and endpoint reference are configured in Integration Quality Review → Message flow."),
                    new("altinn-auth", "Altinn authentication", JourneyPrerequisiteCategory.Authentication, JourneyReadinessState.NotConfigured,
                        config.AuthenticationType == ExternalAuthenticationType.Unknown
                            ? "Authentication not configured: no Altinn authentication type is configured, and BirkNext has no shared machine-identity / Maskinporten-compatible token source."
                            : $"Authentication not configured: {config.AuthenticationType} is documented, but BirkNext has no shared machine-identity / Maskinporten-compatible token source to obtain a token."),
                    new("submission-contract", "Submission contract", JourneyPrerequisiteCategory.Contract, JourneyReadinessState.NotReady,
                        "No verified Altinn submission contract (app/API, request schema) exists in repository or source evidence. Spec-backed only; the report payload is never invented."),
                    new("report-xsd", "Report XSD (MM validation)", JourneyPrerequisiteCategory.Contract,
                        xsdBound ? JourneyReadinessState.Partial : JourneyReadinessState.NotReady,
                        xsdBound ? "An XML Schema from Source Analysis is bound to the message flow. XSD exists ≠ runtime payload validated."
                            : xsdInSource ? "An XML Schema exists in Source Analysis but is not bound to the message flow. XSD exists ≠ runtime payload validated."
                            : $"XSD {config.ContractVersion ?? "(version unknown)"} is documented only; not source verified."),
                    new("test-reporter", "Approved test reporter", JourneyPrerequisiteCategory.SyntheticData,
                        !string.IsNullOrWhiteSpace(config.ReporterReference) && config.ReporterApprovedForTest ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        !string.IsNullOrWhiteSpace(config.ReporterReference) && config.ReporterApprovedForTest ? "An approved test reporter reference is configured." : "No approved synthetic test reporter (institution/foster home test actor) is configured."),
                    new("synthetic-data", "Synthetic test identity set", JourneyPrerequisiteCategory.SyntheticData,
                        config.ApprovedSyntheticData && !string.IsNullOrWhiteSpace(config.SyntheticIdentitySetReference) ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        config.ApprovedSyntheticData && !string.IsNullOrWhiteSpace(config.SyntheticIdentitySetReference)
                            ? "An approved synthetic identity set reference is configured." : "No approved synthetic child identity set is configured; real national ids, names and institution data are never used."),
                    Source("MU", ["MU", "Meldingsutveksleren"]),
                    Source("MM", ["MM"]),
                    Source("Skolenærværsrapport", ["Skolenærværsrapport", "Skolenaervaersrapport"]),
                    new("report-verifier", "Skolenærværsrapport verification", JourneyPrerequisiteCategory.DownstreamVerification, JourneyReadinessState.NotAvailable,
                        "No read contract for the report service exists, so report state, mapping and Person lookup outcome cannot be verified."),
                ];
            }

            case PeriodeavslutningId:
            {
                var channel = context.SourceSnapshots.SelectMany(snapshot => snapshot.Architecture?.MessagingChannels ?? [])
                    .FirstOrDefault(item => ArchitectureRuleEvaluator.MatchesAny([PeriodClosedEvent], item.Name, item.EntityName));
                return
                [
                    new("domain-trigger", "Safe period-closure trigger", JourneyPrerequisiteCategory.Trigger, JourneyReadinessState.NotReady,
                        "No safe domain API/workflow to close a synthetic period exists in evidence. BirkNext never publishes the event directly."),
                    new("event-contract", $"{PeriodClosedEvent} contract", JourneyPrerequisiteCategory.Contract,
                        channel is null ? JourneyReadinessState.NotReady : JourneyReadinessState.Ready,
                        channel is null ? "Documented in the architecture diagram only; no Service Bus channel with this name is present in Source Analysis (not source verified)."
                            : $"Source verified: Service Bus channel {channel.Name} ({channel.Type}) in Source Analysis."),
                    Source("Skolenærværsrapport", ["Skolenærværsrapport", "Skolenaervaersrapport"]),
                    Source("Bufdata", ["Bufdata"]),
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
}
