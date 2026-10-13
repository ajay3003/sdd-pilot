using BirkNext.Api.Services.ActiveEventTesting.Providers.SkoleNaervaer;
using BirkNext.Api.Services.AltinnTesting;
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
public sealed class SkoleNaervaerJourneyPack(IIntegrationMessageFlowStore messageFlows, AltinnTestingPolicy altinnPolicy, AltinnAppContractService altinnContracts) : IIntegrationJourneyPack
{
    public const string Id = "skolenaervaer";
    public const string UtdanningsdataId = "utdanningsdata";
    public const string RapportmottakId = "rapportmottak";
    public const string PeriodeavslutningId = "periodeavslutning";
    public const string PeriodClosedEvent = "SkolenaervaersperiodeLukket";
    /// <summary>Read-only observation of an existing Altinn instance (Altinn Storage GET); sends and changes nothing.</summary>
    public const string InstanceEvidenceScenario = "instance-evidence";
    /// <summary>The scenarios that create and submit a report; only these need the submission contract and the test-data references.</summary>
    public static readonly IReadOnlyList<string> SubmissionScenarios = ["valid-report", "person-not-found", "overlapping-report"];

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
        "Rapportmottak's Altinn steps run through the Altinn TT02 App API as an approved synthetic TestID reporter, authenticated unattended with ID-porten TEST's documented headless login (an interactive TestID sign-in remains an optional fallback). An Altinn receipt covers the Altinn steps only: MU, MM, Skolenærværsrapport and Person steps have no observer and stay Not assessed.",
        "The documented message-flow pilot in Integration Quality Review names the intake component \"Skolenærværsmottaket\" and the report service \"Skoletjenesten\"; this pack uses the architecture diagram's names MM and Skolenærværsrapport. The correspondence is a documentation alias, not source verified.",
        "No MU, MM, Skolenærværsrapport or Bufdata source, contract or read endpoint is available to BirkNext; their steps cannot be observed or verified yet.",
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
                    JourneyScenarioSupport.Supported, Inputs: ReportInputs(Reporters(altinnPolicy), [new(SkoleNaervaerReportInput.Registered, "Registered synthetic test person (known child)")])),
                new("person-not-found", "E2E-02 · Child not found in Person", "A typed report for a synthetic child that Person does not know (declared by the tester or generated). Whether the report is held is not observable yet.",
                    JourneyScenarioSupport.Supported, Inputs: ReportInputs(Reporters(altinnPolicy),
                    [
                        new(SkoleNaervaerReportInput.Supplied, "Synthetic identifier I supply (declared not registered)"),
                        new(SkoleNaervaerReportInput.Generated, "Generated synthetic identifier (unregistered by assumption)"),
                    ])),
                new("overlapping-report", "E2E-08 · Overlapping report", "A second typed report for the same registered synthetic child with a period that overlaps an earlier report the tester declares. How overlap is marked is not observable yet.",
                    JourneyScenarioSupport.Supported, Inputs: ReportInputs(Reporters(altinnPolicy), [new(SkoleNaervaerReportInput.Registered, "Registered synthetic test person (known child)")],
                        new JourneyScenarioInput(SkoleNaervaerReportInput.PriorReportKey, "Earlier overlapping report", JourneyScenarioInputKind.Choice, true,
                            "Required: a report for this child with an overlapping period was submitted earlier (for example by a previous run).",
                            [new("declared", "Yes — an earlier overlapping report exists for this child")]))),
                new(InstanceEvidenceScenario, "Altinn instance evidence (read-only)",
                    "Reads one existing Altinn instance from Altinn Storage (TT02) and records its process state and data-element metadata. Read-only: nothing is created, submitted or changed. An observed instance is not a verified submission, and Altinn's storage path is not an MU blob.",
                    JourneyScenarioSupport.Supported, StateChanging: false, Inputs:
                    [
                        Reporters(altinnPolicy),
                        new(SkoleNaervaerInstanceReference.PartyKey, "Instance owner party id", JourneyScenarioInputKind.Reference, true,
                            "The numeric Altinn party id from the instance id (partyId/instanceGuid). Used for this read only; never stored."),
                        new(SkoleNaervaerInstanceReference.InstanceKey, "Instance GUID", JourneyScenarioInputKind.Reference, true,
                            "The instance GUID from the instance id. History keeps only a short reference."),
                    ]),
                new("correction", "E2E-07 · Correction", "A corrected report replaces an earlier one and the earlier version is kept as history.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: how a correction is expressed in the Altinn form (a new instance, or OppfolgingAvMelding/Oppdatering) is not in any verified app contract."),
                new("multiple-reporters", "E2E-09 · Several reporters", "Two reporters report for the same child and period; both reports are kept.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: each run authenticates one approved synthetic reporter alias; two separately approved reporters and a way to show that both reports are kept downstream are not available yet."),
                new("invalid-xsd", "Invalid XSD", "A reviewed schema-invalid report rejected by MM's XSD validation.", JourneyScenarioSupport.NotAssessed,
                    "Not assessed: no reviewed invalid payload fixture and no submission contract exist; arbitrary XML is never corrupted."),
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
            Limitations = ["Authenticated ≠ submitted ≠ received by MU ≠ processed by MM ≠ stored by Skolenærværsrapport: each step is evidenced on its own, and an Altinn receipt never completes this journey.", "Correlation can use only a stable test reference returned by the real Altinn/MU contract; none is known, so at best aggregate activity could be reported."],
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

    /// <summary>
    /// The reporter choice: each approved synthetic TEST reporter by alias (unattended, the default) and, last, the tester's own interactive
    /// TestID sign-in as an explicit fallback. Values are aliases, never identifiers.
    /// </summary>
    private static JourneyScenarioInput Reporters(AltinnTestingPolicy policy) =>
        new(SkoleNaervaerReportInput.ReporterKey, "Reporter (Altinn TEST actor)", JourneyScenarioInputKind.Choice, true,
            "An approved synthetic TestID reporter, authenticated unattended by the backend. The reporter is the Altinn actor, separate from the child the report is about.",
            [
                .. policy.Options.UnattendedIdPortenTest.Reporters.Where(item => AltinnTestingPolicy.ReporterProblem(item) is null)
                    .Select(item => new JourneyScenarioInputOption(item.Alias, string.IsNullOrWhiteSpace(item.Label) ? item.Alias : item.Label)),
                new(AltinnTestingPolicy.InteractiveReporter, "Interactive TestID sign-in (fallback; sign in first)"),
            ]);

    /// <summary>Typed inputs of a report scenario: reporter, identity source (limited per scenario), the child's synthetic identifier, period and hours.</summary>
    private static IReadOnlyList<JourneyScenarioInput> ReportInputs(JourneyScenarioInput reporter, IReadOnlyList<JourneyScenarioInputOption> identitySources, params JourneyScenarioInput[] extra) =>
    [
        reporter,
        new(SkoleNaervaerReportInput.IdentitySourceKey, "Child identity", JourneyScenarioInputKind.Choice, true,
            "Synthetic test identities only. Known/unknown is your declaration; BirkNext cannot check Person.", identitySources),
        new(SkoleNaervaerAltinnForm.ChildIdentifier, "Barnets fødselsnummer (synthetic)", JourneyScenarioInputKind.SyntheticPersonIdentifier, false,
            "11 digits, month + 80 (Test-Norge synthetic). Leave empty when BirkNext generates one. Never stored; history keeps a short hash."),
        new(SkoleNaervaerAltinnForm.PeriodFrom, "Dato fra", JourneyScenarioInputKind.Date, true),
        new(SkoleNaervaerAltinnForm.PeriodTo, "Dato til", JourneyScenarioInputKind.Date, true),
        new(SkoleNaervaerAltinnForm.PlannedHours, "Antall timer etter planen for perioden", JourneyScenarioInputKind.Number, true,
            "Whole hours when the app's schema says integer (the Skolenærvær form does)."),
        new(SkoleNaervaerAltinnForm.ActualHours, "Antall timer faktisk nærvær for perioden", JourneyScenarioInputKind.Number, true,
            "Whole hours. Attendance above plan is allowed (it is flagged by Skolenærværsrapport, not rejected)."),
        .. extra,
    ];

    public JourneyInputPreparation PrepareInputs(IntegrationJourneyDefinition journey, JourneyScenarioDescriptor scenario, IReadOnlyDictionary<string, string> inputs) =>
        scenario.ScenarioId == InstanceEvidenceScenario ? SkoleNaervaerInstanceReference.Prepare(scenario, inputs)
        : scenario.Inputs is { Count: > 0 } ? SkoleNaervaerReportInput.Prepare(scenario, inputs)
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
            var problems = altinnPolicy.UnattendedProblems(config);
            checks.Add(new("unattended-configuration", "Unattended ID-porten TEST configuration", problems.Count == 0,
                problems.Count == 0 ? "Client id, openid scope, registered redirect URI and client secret are present (secret value not read out)." : string.Join(" ", problems)));
        }

        var environment = AltinnTestingPolicy.AltinnEnvironmentBlock(config);
        checks.Add(new("production-safety", "Production safety", environment is null, environment ?? "Altinn TT02 only; Production hosts are refused by name on every request."));
        var app = altinnPolicy.ApprovedApp(config.AppIdentifier, out _);
        var endpoints = new List<Uri>
        {
            new(AltinnTestingPolicy.IdPortenTestDiscovery), new("https://login.test.idporten.no/authorize"), new("https://test.idporten.no/token"), new(AltinnTestingPolicy.AltinnTokenExchange),
        };
        if (app is { } a) endpoints.Add(new Uri(AltinnTestingPolicy.AppBase(a.Org, a.App), "instances"));
        var blocked = endpoints.Select(uri => (uri, reason: AltinnTestingPolicy.RequestBlock(uri))).Where(item => item.reason is not null).ToList();
        checks.Add(new("endpoints", "Endpoint allowlist", blocked.Count == 0 && app is not null,
            app is null ? "No approved app: the app endpoint cannot be built." : blocked.Count == 0
                ? $"{endpoints.Count} endpoints (ID-porten TEST, Altinn TT02 exchange, the approved app) all pass the request guard."
                : string.Join(" ", blocked.Select(item => item.reason))));

        var confirmation = altinnPolicy.AppPolicy(config.AppIdentifier, out var policyDetail);
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

        checks.Add(new("unfilled-required", "Required schema fields BirkNext does not fill", contract.RequiredUnboundFields.Count == 0,
            contract.RequiredUnboundFields.Count == 0 ? "None."
                : $"{contract.RequiredUnboundFields.Count} required field(s) are not filled by BirkNext ({string.Join(", ", contract.RequiredUnboundFields.Take(10).Select(field => field.Path))}). They must come from the app itself — not verified; Altinn's validation step of the first live run will show it.",
            Blocking: false));
        checks.Add(new("process-plan", "Process plan", contract.ProcessTasks.Count > 0, contract.ProcessTasks.Count > 0
            ? $"process/next × {contract.ProcessTasks.Count} ({string.Join(" → ", contract.ProcessTasks.Select(task => $"{task.TaskId} ({task.TaskType})"))} → end); a data task's process/next is authorized by \"write\"."
            : "Not known: the app's process definition (process.bpmn) is not in the contract, so process/next is never called."));
        return new JourneyPackValidation(checks, preview);
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
                var xsdBound = config.SourceSnapshotId is not null && !string.IsNullOrWhiteSpace(config.SourceContractId);
                var xsdInSource = context.SourceSnapshots.Any(snapshot => snapshot.EvidenceDomains?.Contracts.Contracts.Any(contract => contract.Type == SourceContractType.XmlSchema) == true);
                var contract = altinnContracts.Evaluate(context.EnvironmentId, config.AppIdentifier, flow.AppContract);
                var signIn = altinnPolicy.SignInProblems(config);
                var unattended = altinnPolicy.UnattendedProblems(config);
                var altinnEnvironment = AltinnTestingPolicy.AltinnEnvironmentBlock(config);
                var options = altinnPolicy.Options.UnattendedIdPortenTest;
                var clientReady = config.AuthenticationType == ExternalAuthenticationType.IDPorten && !string.IsNullOrWhiteSpace(config.ClientReference)
                    && config.ConfiguredScopes.Contains("openid", StringComparer.Ordinal) && altinnPolicy.UnattendedRedirect() is not null;
                var reporters = options.Reporters.Count(item => AltinnTestingPolicy.ReporterProblem(item) is null);
                var confirmation = altinnPolicy.AppPolicy(config.AppIdentifier, out var policyDetail);
                // A confirmed policy must also allow the roles the configured reporters act in (unattended aliases, or PRIV for the fallback).
                var roles = options.Reporters.Select(item => item.AltinnRole).Append("PRIV").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var policyProblem = confirmation is null ? null : roles.Select(role => AltinnTestingPolicy.PolicyProblem(confirmation, role)).FirstOrDefault(item => item is not null);
                var policyConfirmed = confirmation is not null && policyProblem is null;
                var awaitingProfile = altinnPolicy.ReportersAwaitingProfile;
                return
                [
                    new("altinn-environment", "Altinn TT02 environment", JourneyPrerequisiteCategory.TrustedEnvironment,
                        altinnEnvironment is null ? JourneyReadinessState.Ready : JourneyReadinessState.NotReady,
                        altinnEnvironment ?? "Altinn environment Test (TT02) with Production blocked (Integration Quality Review → Altinn test configuration)."),
                    new("altinn-app", "Approved Altinn app", JourneyPrerequisiteCategory.Integration,
                        contract.AppApproved ? JourneyReadinessState.Ready : string.IsNullOrWhiteSpace(config.AppIdentifier) ? JourneyReadinessState.NotConfigured : JourneyReadinessState.NotReady,
                        contract.AppDetail),
                    // Reporter authentication: unattended (default) or the interactive fallback; either mode can satisfy it. Each unattended part is
                    // reported on its own below, so a missing secret or reporter is visible as exactly that.
                    new("reporter-auth", "Reporter authentication", JourneyPrerequisiteCategory.Authentication,
                        unattended.Count == 0 || signIn.Count == 0 ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        unattended.Count == 0 ? "Unattended: an approved synthetic TestID reporter is authenticated by the backend when the run starts. No sign-in step."
                            : signIn.Count == 0 ? "Only the interactive fallback is configured: the tester must sign in with TestID before running. Unattended authentication is not ready."
                            : $"Neither unattended nor interactive ID-porten TEST authentication is configured. Unattended: {string.Join(" ", unattended)}"),
                    new("unattended-provider", "Unattended ID-porten TEST provider", JourneyPrerequisiteCategory.Authentication,
                        options.Enabled ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        options.Enabled ? "Available: ID-porten TEST headless login for synthetic TestID users (TEST only), then the Altinn TT02 token exchange."
                            : $"Disabled (backend {AltinnTestingOptions.Section}:UnattendedIdPortenTest:Enabled).", Mandatory: false),
                    new("idporten-client", "ID-porten TEST client", JourneyPrerequisiteCategory.Authentication,
                        clientReady ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        clientReady ? "Configured: client id and scopes (Integration Quality Review) and a registered redirect URI (backend)."
                            : "Missing: ID-porten authentication type, client id, openid scope or a registered redirect URI.", Mandatory: false),
                    new("client-secret", "Client secret", JourneyPrerequisiteCategory.Authentication,
                        altinnPolicy.UnattendedSecretAvailable ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        altinnPolicy.UnattendedSecretAvailable ? "Available to the backend (value never shown)." : "Missing: the ID-porten TEST client secret is not available to the backend (environment variable or secret store).", Mandatory: false),
                    new("synthetic-reporter", "Synthetic reporter", JourneyPrerequisiteCategory.SyntheticData,
                        reporters > 0 ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        reporters > 0 ? $"{reporters} approved synthetic TestID reporter alias(es) configured (identifiers stay in the backend)." : "Missing: no approved synthetic TestID reporter alias is configured.", Mandatory: false),
                    new("altinn-auth", "Interactive TestID sign-in (fallback)", JourneyPrerequisiteCategory.Authentication,
                        signIn.Count == 0 ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        signIn.Count == 0 ? "Optional fallback configured: a tester can sign in with TestID in the browser instead." : $"Optional fallback not configured: {string.Join(" ", signIn)}", Mandatory: false),
                    new("reporter-profile", "Reporter Altinn profile", JourneyPrerequisiteCategory.SyntheticData,
                        options.Reporters.Count > 0 && awaitingProfile == 0 ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        options.Reporters.Count == 0 ? "Not applicable until a synthetic reporter alias is configured."
                            : awaitingProfile == 0 ? "Confirmed: every configured synthetic reporter has an Altinn TT02 profile (Altinn's token exchange requires one)."
                            : $"Not confirmed for {awaitingProfile} reporter alias(es): Altinn's token exchange fails for a person without an Altinn TT02 profile (AltinnProfileConfirmed).", Mandatory: false),
                    new("app-policy", "App authorization policy", JourneyPrerequisiteCategory.Authorization,
                        policyConfirmed ? JourneyReadinessState.Ready : JourneyReadinessState.NotReady, policyProblem is null ? policyDetail : $"{policyDetail} Not compatible: {policyProblem}",
                        Scenarios: SubmissionScenarios),
                    new("submission-contract", "Altinn app contract", JourneyPrerequisiteCategory.Contract,
                        contract.Complete ? JourneyReadinessState.Ready : JourneyReadinessState.NotReady,
                        contract.Complete ? $"Complete: form data type {contract.FormDataTypeId}, {contract.Bindings.Count} verified fields, {contract.ProcessTasks.Count} data task(s)."
                            : $"Incomplete — {string.Join(" ", contract.Missing)}", Scenarios: SubmissionScenarios),
                    new("report-xsd", "Report XSD (MM validation)", JourneyPrerequisiteCategory.Contract,
                        xsdBound ? JourneyReadinessState.Partial : JourneyReadinessState.NotReady,
                        xsdBound ? "An XML Schema from Source Analysis is bound to the message flow. XSD exists ≠ runtime payload validated."
                            : xsdInSource ? "An XML Schema exists in Source Analysis but is not bound to the message flow. XSD exists ≠ runtime payload validated."
                            : $"XSD {config.ContractVersion ?? "(version unknown)"} is documented only; not source verified.", Mandatory: false),
                    new("test-reporter", "Approved test reporter", JourneyPrerequisiteCategory.SyntheticData,
                        !string.IsNullOrWhiteSpace(config.ReporterReference) && config.ReporterApprovedForTest ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        !string.IsNullOrWhiteSpace(config.ReporterReference) && config.ReporterApprovedForTest ? "An approved test reporter reference is configured." : "No approved synthetic test reporter (institution/foster home test actor) is configured.",
                        Scenarios: SubmissionScenarios),
                    new("synthetic-data", "Synthetic test identity set", JourneyPrerequisiteCategory.SyntheticData,
                        config.ApprovedSyntheticData && !string.IsNullOrWhiteSpace(config.SyntheticIdentitySetReference) ? JourneyReadinessState.Ready : JourneyReadinessState.NotConfigured,
                        config.ApprovedSyntheticData && !string.IsNullOrWhiteSpace(config.SyntheticIdentitySetReference)
                            ? "An approved synthetic identity set reference is configured." : "No approved synthetic child identity set is configured; real national ids, names and institution data are never used.",
                        Scenarios: SubmissionScenarios),
                    // Downstream steps (MU, MM, Skolenærværsrapport) are shown but do not gate the Altinn submission: without them those steps stay
                    // Not assessed, so a run can reach Partial at most.
                    Source("MU", ["MU", "Meldingsutveksleren"]) with { Mandatory = false },
                    Source("MM", ["MM"]) with { Mandatory = false },
                    Source("Skolenærværsrapport", ["Skolenærværsrapport", "Skolenaervaersrapport"]) with { Mandatory = false },
                    new("report-verifier", "Skolenærværsrapport verification", JourneyPrerequisiteCategory.DownstreamVerification, JourneyReadinessState.NotAvailable,
                        "No read contract for the report service exists, so report state, mapping and Person lookup outcome cannot be verified.", Mandatory: false),
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
