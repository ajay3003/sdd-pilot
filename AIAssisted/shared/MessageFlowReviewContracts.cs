using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowEvidenceState { Documented, DecisionApproved, OpenQuestion, Superseded, SourceConfirmed, Configured, AzureObserved, RuntimeObserved, AssertionPassed, AssertionFailed, NotAssessed, NotAvailable }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowNodeRole { ExternalSender, ExternalPlatform, MessageExchange, QueueBroker, PayloadStore, IntakeComponent, StructuralValidator, DomainService, IdentityProvider, AuthorizationService, MasterDataProvider, ReportingConsumer, AuditService, Other }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowPayloadMode { Inline, Reference, EncryptedReference, Unknown }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowValidationLayer { Structural, DomainValidity, DataQuality, SecurityPolicy }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageReceiptKind { TransportReceipt, ApplicationReceipt, BusinessReceipt, NegativeReceipt }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowCheckpointStatus { NotConfigured, NotAssessed, Observed, NotObserved, Unavailable, NotAuthorized, TimedOut, NotApplicable, Passed, Failed }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FlowCheckpointConfigurationState { NotConfigured, Configured }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AltinnConfigurationState { Unknown, Documented, NeedsConfiguration, Configured, NeedsAccess, NeedsCredential, NeedsTestIdentity, NeedsCheckpointAccess, Verified, TestReady, Blocked }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExternalTestEnvironment { Unknown, Development, Test, QualityAssurance, Staging, Production }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExternalSubmissionMode { Unknown, ManualForm, Api, Event, BrokerTransfer, FileTransfer, Other }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExternalAuthenticationType { Unknown, IDPorten, Maskinporten, OAuth, Certificate, EnterpriseCertificate, Other }

public sealed record FlowEvidenceProvenance(FlowEvidenceState State, string SourceReference, string Note, DateTimeOffset? CapturedAt = null);
public sealed record MessageFlowNode(string Id, string Name, FlowNodeRole Role, string StateOwner, FlowEvidenceProvenance Evidence);
public sealed record MessageFlowHop(string Id, string FromNodeId, string ToNodeId, string Mechanism, string Transport, string? Channel,
    string? ContractReference, string? Authentication, FlowPayloadMode PayloadMode, string ProcessingRole,
    FlowEvidenceProvenance Evidence, string Limitations = "");
public sealed record MessageLifecycleDefinition(string State, string Owner, string Meaning, FlowEvidenceProvenance Evidence);
public sealed record MessageErrorPath(string Condition, string Owner, string ExpectedHandling, string? RetryPolicy, string? HoldPolicy,
    string? DeadLetterPolicy, string FinalState, FlowEvidenceProvenance Evidence);
public sealed record FlowValidationDefinition(FlowValidationLayer Layer, string Owner, string Outcome, FlowEvidenceProvenance Evidence);
public sealed record MessageReceiptDefinition(MessageReceiptKind Kind, string Producer, string Consumer, string Trigger, string ReturnPath, FlowEvidenceProvenance Evidence);
public sealed record MessageDataHandlingDefinition(IReadOnlyList<string> SensitiveDataEntering, string TransitBoundary, string TemporaryPersistence,
    string Encryption, IReadOnlyList<string> ForbiddenDestinations, string CleanupExpectation, FlowEvidenceProvenance Evidence);
public sealed record MessageDeadlineDefinition(string Name, string TimeZone, string Trigger, string Behavior, FlowEvidenceProvenance Evidence);
public sealed record FlowAuthorityNote(string Topic, FlowEvidenceState State, string Description, string SourceReference, string? SupersededBy = null);
public sealed record FlowCheckpointDefinition(string Id, string Name, string NodeOrHopId, string ExpectedObservation, string ProbeType,
    FlowCheckpointConfigurationState ConfigurationStatus, FlowCheckpointStatus RuntimeStatus, string? EvidenceReference = null);
public sealed record MessageFlowDefinition
{
    public string EnvironmentId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public List<MessageFlowNode> Nodes { get; init; } = [];
    public List<MessageFlowHop> Hops { get; init; } = [];
    public List<MessageLifecycleDefinition> Lifecycle { get; init; } = [];
    public List<MessageErrorPath> ErrorPaths { get; init; } = [];
    public List<FlowValidationDefinition> Validation { get; init; } = [];
    public List<MessageReceiptDefinition> Receipts { get; init; } = [];
    public MessageDataHandlingDefinition? DataHandling { get; init; }
    public List<MessageDeadlineDefinition> Deadlines { get; init; } = [];
    public List<FlowCheckpointDefinition> Checkpoints { get; init; } = [];
    public List<string> OpenQuestions { get; init; } = [];
    public List<FlowAuthorityNote> AuthorityNotes { get; init; } = [];
    public List<string> Limitations { get; init; } = [];
    public string? TargetEnvironmentReference { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Safe, test-specific references. Secret values and message contents are deliberately not representable.</summary>
public sealed record AltinnTestConfiguration
{
    public ExternalTestEnvironment Environment { get; init; }
    public string? TargetEnvironmentId { get; init; }
    public string? ProductOrServiceType { get; init; }
    public string? AppIdentifier { get; init; }
    public ExternalSubmissionMode SubmissionMode { get; init; }
    public string? BaseUrlReference { get; init; }
    public string? EndpointReference { get; init; }
    public ExternalAuthenticationType AuthenticationType { get; init; }
    public string? TenantReference { get; init; }
    public string? ClientReference { get; init; }
    public List<string> DocumentedScopes { get; init; } = [];
    public List<string> ConfiguredScopes { get; init; } = [];
    public List<string> VerifiedScopes { get; init; } = [];
    public string? CertificateReference { get; init; }
    public string? KeyReference { get; init; }
    public string? ReporterType { get; init; }
    public string? ReporterReference { get; init; }
    public bool ReporterApprovedForTest { get; init; }
    public string? SyntheticIdentitySetReference { get; init; }
    public string? MessageType { get; init; }
    public string? ContractReference { get; init; }
    public string? ContractVersion { get; init; }
    public Guid? SourceSnapshotId { get; init; }
    public string? SourceContractId { get; init; }
    public string? MuChannelReference { get; init; }
    public string? QueueReference { get; init; }
    public string? PayloadStoreReference { get; init; }
    public string? ReceiptMode { get; init; }
    public string? CorrelationMappingReference { get; init; }
    public string? CheckpointConfigurationReference { get; init; }
    public bool ApprovedSyntheticData { get; init; }
    public bool ProductionBlocked { get; init; } = true;
    public AltinnConfigurationState ConfigurationState { get; init; } = AltinnConfigurationState.Unknown;
    public FlowEvidenceState VerificationState { get; init; } = FlowEvidenceState.NotAssessed;
    public List<string> DocumentationReferences { get; init; } = [];
    public string? DocumentationPlatform { get; init; }
    public DateOnly? DocumentationRetrievedOn { get; init; }
}

public sealed record TestReadinessItem(string Key, string Label, bool Satisfied, string State, string Explanation);
public sealed record IntegrationTestReadiness(bool CanRunActiveTest, int Ready, int Total, IReadOnlyList<TestReadinessItem> Items, IReadOnlyList<string> Blockers);
public sealed record ManualIntegrationTestCase(string Name, string Purpose, string EvidenceBasis, IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> Action, IReadOnlyList<string> ExpectedFlow, IReadOnlyList<string> Checkpoints,
    IReadOnlyList<string> EvidenceRequired, string AutomationReadiness, string Limitations);
public sealed record IntegrationMessageFlowPackage(MessageFlowDefinition Flow, AltinnTestConfiguration AltinnConfiguration,
    IntegrationTestReadiness Readiness, IReadOnlyList<string> ManualTestPlan, IReadOnlyList<ManualIntegrationTestCase> TestCases);
public sealed record MessageFlowSourceContractOptions(Guid SnapshotId, string ArchiveName, string Fingerprint,
    IReadOnlyList<BirkNext.SourceDomains.SourceContract> Contracts);

public static class IntegrationMessageFlowReadiness
{
    public static IntegrationTestReadiness Evaluate(MessageFlowDefinition flow, AltinnTestConfiguration config)
    {
        var items = new List<TestReadinessItem>
        {
            Item("implementation", "Implementation", flow.Nodes.Any(n => n.Evidence.State == FlowEvidenceState.SourceConfirmed), "Not available / not source verified"),
            Item("environment", "Non-production environment", config.Environment is ExternalTestEnvironment.Test or ExternalTestEnvironment.QualityAssurance or ExternalTestEnvironment.Staging, config.Environment.ToString()),
            Item("product", "Altinn product / app", !string.IsNullOrWhiteSpace(config.ProductOrServiceType) && !string.IsNullOrWhiteSpace(config.AppIdentifier), "Needs verification"),
            Item("submission", "Submission mechanism", config.SubmissionMode != ExternalSubmissionMode.Unknown, "Needs verification"),
            Item("endpoint", "Approved test endpoint reference", !string.IsNullOrWhiteSpace(config.EndpointReference), "Needs verification"),
            Item("authentication", "Authentication and access", config.AuthenticationType != ExternalAuthenticationType.Unknown && !string.IsNullOrWhiteSpace(config.ClientReference), "Needs configuration / verification"),
            Item("certificate", "Certificate/key reference", config.AuthenticationType is not (ExternalAuthenticationType.Certificate or ExternalAuthenticationType.EnterpriseCertificate) ||
                (!string.IsNullOrWhiteSpace(config.CertificateReference) && !string.IsNullOrWhiteSpace(config.KeyReference)), "Required for selected certificate authentication"),
            Item("reporter", "Approved test reporter", !string.IsNullOrWhiteSpace(config.ReporterReference) && config.ReporterApprovedForTest, "Missing test actor or approval"),
            Item("synthetic", "Approved synthetic test data", config.ApprovedSyntheticData && !string.IsNullOrWhiteSpace(config.SyntheticIdentitySetReference), "Missing approved synthetic identity set"),
            Item("contract", "Contract and message type", !string.IsNullOrWhiteSpace(config.ContractReference) && !string.IsNullOrWhiteSpace(config.ContractVersion) && !string.IsNullOrWhiteSpace(config.MessageType), "Needs contract selection"),
            Item("routing", "MU channel / routing", !string.IsNullOrWhiteSpace(config.MuChannelReference), "Needs verification"),
            Item("payload", "Queue, payload store and key references", !string.IsNullOrWhiteSpace(config.QueueReference) && !string.IsNullOrWhiteSpace(config.PayloadStoreReference) && !string.IsNullOrWhiteSpace(config.KeyReference), "Needs configuration"),
            Item("checkpoints", "Checkpoint access", !string.IsNullOrWhiteSpace(config.CheckpointConfigurationReference) && flow.Checkpoints.Any(c => c.ConfigurationStatus == FlowCheckpointConfigurationState.Configured), "Not configured / verified"),
            Item("downstream", "Downstream verification", flow.Checkpoints.Any(c => c.Name.Contains("Skoletjenesten", StringComparison.OrdinalIgnoreCase) && c.ConfigurationStatus == FlowCheckpointConfigurationState.Configured), "Not configured"),
            Item("correlation", "Cross-hop correlation", !string.IsNullOrWhiteSpace(config.CorrelationMappingReference), "Not configured"),
        };
        var blockers = items.Where(i => !i.Satisfied).Select(i => i.Label + ": " + i.State).ToList();
        blockers.Add("No active external test provider is implemented.");
        if (config.Environment == ExternalTestEnvironment.Production) blockers.Add("Production is never an allowed active-test environment.");
        if (!config.ProductionBlocked) blockers.Add("Production execution guard is not enabled.");
        var ready = items.Count(i => i.Satisfied);
        return new IntegrationTestReadiness(blockers.Count == 0, ready, items.Count, items, blockers.Distinct().ToList());

        static TestReadinessItem Item(string key, string label, bool satisfied, string missing) =>
            new(key, label, satisfied, satisfied ? "Configured; verification may still be required" : missing, satisfied ? "Present as safe configuration metadata." : missing);
    }

    public static IReadOnlyList<string> ManualPlan(MessageFlowDefinition flow, AltinnTestConfiguration config)
    {
        var lines = new List<string> { $"Manual integration test plan — {flow.Name}", "Evidence maturity: documented/planned design; source and runtime behavior are not verified." };
        lines.AddRange(flow.Hops.Select((hop, i) => $"{i + 1}. {flow.Nodes.FirstOrDefault(n => n.Id == hop.FromNodeId)?.Name ?? hop.FromNodeId} → {flow.Nodes.FirstOrDefault(n => n.Id == hop.ToNodeId)?.Name ?? hop.ToNodeId}: {hop.Mechanism}. Evidence: {hop.Evidence.State}."));
        if ((config.ContractReference ?? "").Contains("XSD", StringComparison.OrdinalIgnoreCase) || (config.ContractReference ?? "").Contains("XML Schema", StringComparison.OrdinalIgnoreCase))
            lines.Add("Validate XML structure against the selected XSD locally; schema validity does not establish business validity.");
        lines.Add("Use only an approved test environment and approved synthetic identity reference. Do not send real personal data.");
        foreach (var checkpoint in flow.Checkpoints) lines.Add($"Checkpoint: {checkpoint.Name} — configuration {checkpoint.ConfigurationStatus}; runtime {checkpoint.RuntimeStatus}; probe {checkpoint.ProbeType}.");
        lines.Add("Record each checkpoint separately. A submission or transport receipt does not prove downstream processing or persistence.");
        if (!string.IsNullOrWhiteSpace(config.EndpointReference)) lines.Add("Configured endpoint reference: " + config.EndpointReference);
        lines.AddRange(flow.OpenQuestions.Select(q => "Needs clarification: " + q));
        lines.AddRange(flow.Limitations);
        return lines;
    }
}

public static class MessageFlowTestCaseDesigner
{
    public static IReadOnlyList<ManualIntegrationTestCase> Generate(MessageFlowDefinition flow)
    {
        var cases = new List<ManualIntegrationTestCase>();
        var basis = flow.Hops.Count == 0 ? "No documented flow steps are available." : string.Join("; ", flow.Hops.Select(h => $"{h.Mechanism} [{h.Evidence.State}]"));
        if (flow.Hops.Count > 0)
            cases.Add(new("Documented happy path", "Exercise the configured flow from source to destination.", basis,
                ["Non-production environment", "Approved synthetic test data", "Checkpoint sources configured"],
                ["Submit the approved test message through the configured sender."],
                flow.Hops.Select(h => $"{h.FromNodeId} → {h.ToNodeId}: {h.Mechanism} (evidence {h.Evidence.State}).").ToList(),
                flow.Checkpoints.Select(c => c.Name).ToList(), flow.Checkpoints.Select(c => c.ExpectedObservation).ToList(),
                "Manual; active provider not necessarily available.", "Documented path does not prove source or runtime implementation."));

        foreach (var validation in flow.Validation)
            cases.Add(new($"{validation.Layer} validation", $"Exercise the {validation.Layer} layer independently.", validation.Evidence.SourceReference,
                ["Approved test fixture for this validation layer"], ["Submit or validate one fixture that exercises this layer."],
                [validation.Outcome], flow.Checkpoints.Where(c => c.NodeOrHopId == validation.Owner).Select(c => c.Name).ToList(),
                ["Validation result and evidence source"], "Needs test configuration.", "Do not infer behavior beyond the documented rule."));

        foreach (var error in flow.ErrorPaths)
            cases.Add(new($"Error path: {error.Condition}", "Verify ownership and terminal/retry/hold handling for this condition.", error.Evidence.SourceReference,
                ["Approved synthetic fixture", "Safe test environment and relevant checkpoint access"], [$"Trigger: {error.Condition}"],
                [$"Owner: {error.Owner}", error.ExpectedHandling, $"Final state: {error.FinalState}"], flow.Checkpoints.Select(c => c.Name).ToList(),
                ["Owner/state, timestamp, correlation reference and redacted reason"], "Needs test configuration.", "Configured policy is not proof that retry, hold or parking occurred."));

        if (flow.DataHandling is { } data && data.SensitiveDataEntering.Count > 0)
            cases.Add(new("Sensitive data boundary", "Check documented transit, storage, forbidden destinations and cleanup.", data.Evidence.SourceReference,
                ["Approved synthetic test data", "Redacted evidence sources"], ["Trace one synthetic message through each configured checkpoint."],
                [data.TransitBoundary, data.TemporaryPersistence, data.CleanupExpectation], flow.Checkpoints.Select(c => c.Name).ToList(), data.ForbiddenDestinations,
                "Needs implementation and observation access.", "Never capture raw personal data in BirkNext evidence."));

        foreach (var deadline in flow.Deadlines)
            cases.Add(new($"Lifecycle: {deadline.Name}", "Exercise the documented deadline or period transition.", deadline.Evidence.SourceReference,
                ["Approved test period", "Clock/time-zone behavior specified"], [deadline.Trigger], [deadline.Behavior], flow.Checkpoints.Select(c => c.Name).ToList(),
                ["Lifecycle state, timestamp and resulting dataset/cleanup metadata"], "Needs test configuration.", "Exact domain rules and time boundaries must be approved."));
        return cases;
    }
}

/// <summary>Portable examples for the generic flow-review UI. Example facts are documentation evidence, never a detected project configuration.</summary>
public static class MessageFlowReviewExamples
{
    public static MessageFlowDefinition GenericFixture => Build("generic-external-message-fixture", "External portal to reporting service",
        ["ExternalPortal", "MessageExchange", "Queue", "Blob", "StatelessIntake", "DomainService", "IdentityService", "ReportingConsumer"],
        [FlowNodeRole.ExternalSender, FlowNodeRole.MessageExchange, FlowNodeRole.QueueBroker, FlowNodeRole.PayloadStore,
         FlowNodeRole.IntakeComponent, FlowNodeRole.DomainService, FlowNodeRole.IdentityProvider, FlowNodeRole.ReportingConsumer],
        ["portal accepted submission", "exchange routed message", "queue delivered payload reference", "intake fetched encrypted blob",
         "intake validated structure", "domain service resolved identity or held report", "period batch delivered after domain processing"],
        ["The queue carries a payload reference; the business payload is retrieved from object storage and decrypted by the receiver.",
         "Transport retry and dead-letter ownership are separate from a domain hold."], "example-generic-message-flow");

    public static MessageFlowDefinition SkolenærværDocumented => Build("skolenarvaer-document-pilot", "Inbound monthly school-provision report",
        ["Altinn", "Meldingsutveksleren", "Queue", "Encrypted blob", "FagKlient", "Skolenærværsmottaket", "Skoletjenesten", "Person", "Bufdata"],
        [FlowNodeRole.ExternalPlatform, FlowNodeRole.MessageExchange, FlowNodeRole.QueueBroker, FlowNodeRole.PayloadStore,
         FlowNodeRole.Other, FlowNodeRole.IntakeComponent, FlowNodeRole.DomainService, FlowNodeRole.IdentityProvider, FlowNodeRole.ReportingConsumer],
        ["foster home submits monthly report", "MU routes to channel and queue message carries encrypted blob reference", "FagKlient retrieves and decrypts payload for stateless intake",
         "intake structurally validates documented XSD v2.0.0", "intake calls Skoletjenesten over REST with Identifikator", "Skoletjenesten resolves identity through Person",
         "report is accepted using internal child identity or held encrypted until resolution/period lock", "intake acknowledges MU only after accepted/held; period lock calculates completeness and prepares one final Bufdata dataset"],
        ["FNR/DUF may transit in the intake-to-service request; persistence, logs and telemetry behavior are planned and not source verified.",
         "BarnRegistreringId is internal child identity; BirkID is the documented Bufdata delivery key.",
         "BiRK attendance history import is out of scope; Utdanning CDC is a separate education-master-data flow.",
         "Skolenærværsmottaket is the planned stateless, permanent integration component; Utdanning CDC adapter is the disposable adapter.",
         "Completeness compares expected population and received reports; detailed calculation depends on Person, Tjeneste and Utdanning evidence."], "Skolenærvær — Spec-Kit Reference") with
    {
        AuthorityNotes =
        [
            new("Receiver state", FlowEvidenceState.Superseded, "Older stateful Meldingsmottaket design is rejected; current baseline says stateless intake.", "Older architecture artifacts", "Skolenærvær — Spec-Kit Reference"),
            new("Identity resolution owner", FlowEvidenceState.Superseded, "Older receiver-side Person resolution is rejected; current baseline places lookup in Skoletjenesten.", "Older architecture artifacts", "Skolenærvær — Spec-Kit Reference"),
            new("Unresolved report handling", FlowEvidenceState.Superseded, "Older intake-owned parking is rejected; current baseline assigns encrypted domain hold to Skoletjenesten.", "Older architecture artifacts", "Skolenærvær — Spec-Kit Reference"),
            new("Bufdata identifier", FlowEvidenceState.Superseded, "BarnRegistreringId is internal; BirkID is the current documented Bufdata delivery key.", "Older architecture artifacts", "Skolenærvær — Spec-Kit Reference"),
            new("Bufdata delivery", FlowEvidenceState.Superseded, "Older daily cursor/change stream is rejected; current baseline describes one final dataset per locked period.", "Older architecture artifacts", "Skolenærvær — Spec-Kit Reference"),
            new("BiRK attendance import", FlowEvidenceState.Superseded, "Importing BiRK Skolenærvær history is out of scope; the Utdanning CDC master-data path is separate.", "Older architecture artifacts", "Skolenærvær — Spec-Kit Reference"),
        ]
    };

    public static AltinnTestConfiguration SkolenærværInitialConfiguration => new()
    {
        Environment = ExternalTestEnvironment.Unknown,
        ProductOrServiceType = null,
        AppIdentifier = null,
        SubmissionMode = ExternalSubmissionMode.Unknown,
        AuthenticationType = ExternalAuthenticationType.Unknown,
        MessageType = "Skolenærværsrapport",
        ContractVersion = "2.0.0",
        ContractReference = "XSD v2.0.0 — documented, not source verified",
        ConfigurationState = AltinnConfigurationState.NeedsConfiguration,
        VerificationState = FlowEvidenceState.NotAssessed,
        ProductionBlocked = true,
    };

    public static IReadOnlyList<ManualIntegrationTestCase> SkolenærværTestCases =>
    [
        Case("Valid report — happy path", "Exercise the documented end-to-end acceptance path.", ["Approved test environment and reporter", "Approved synthetic child identity", "XSD v2.0.0 contract", "All observation checkpoints configured"], ["Submit one valid synthetic monthly report through the verified Altinn product."], ["Altinn → MU → queue/blob → intake/decrypt → structural validation → Skoletjenesten → Person → accepted report → period lock → final Bufdata dataset"], ["Every checkpoint from submission through persistence; record correlation identifiers separately."], "Not ready for automation; implementation and test setup unavailable.", "Documented/planned only; active test remains blocked."),
        Case("Unsupported XSD version", "Verify the documented rejection of a non-v2.0.0 contract.", ["A supported test channel that accepts the test case", "Documented alternate schema fixture"], ["Submit a synthetic message using an unsupported contract version."], ["Intake rejects before calling Skoletjenesten."], ["Submission, MU intake, structural/policy result, and absence of downstream call."], "Not ready for automation.", "The rejecting component is documented as intake; not source verified."),
        Case("Malformed XML and XSD-invalid XML", "Distinguish malformed syntax from well-formed XML that violates XSD structure.", ["Test endpoint/channel accepts invalid test fixtures"], ["Submit one malformed XML fixture and a separate well-formed schema-invalid fixture."], ["Observe the rejecting layer and keep both outcomes distinct."], ["Submission, receiving hop, validation outcome, and proof Skoletjenesten was or was not called."], "Not ready for automation.", "External acceptance behavior and exact failure layer are not source verified."),
        Case("Missing Identifikator", "Verify the documented required-identifier rejection without assuming which layer enforces it.", ["Approved synthetic message fixture"], ["Submit a structurally valid message with required Identifikator omitted."], ["Message is rejected; validation owner is recorded from observed/source evidence."], ["Structural/domain validation result and downstream-call checkpoint."], "Needs validation-owner clarification.", "The required rule is documented; exact enforcement layer must be verified."),
        Case("Unknown child — domain hold", "Distinguish domain hold from broker dead-letter.", ["Approved synthetic identity known not to resolve", "Person and hold checkpoints available"], ["Submit a structurally valid report with the approved unresolved identity."], ["Skoletjenesten holds report encrypted; intake may acknowledge after hold; retry until resolution or period lock."], ["Skoletjenesten hold owner/state, encrypted storage/access, retry trigger, MU acknowledgement, resolution/expiry cleanup."], "Not ready for automation.", "Do not use dead-letter as a business hold; exact runtime behavior is not verified."),
        Case("Transient transport failure", "Verify retry before domain acceptance.", ["Safe controllable QA fault injection"], ["Cause a controlled pre-acceptance timeout/5xx/blob retrieval failure."], ["Broker retry; no premature acknowledgement; dead-letter only after retry exhaustion if configured."], ["Attempt count, retry timing, final owner/state, message identity and no duplicate persistence."], "Not ready for automation.", "Only execute with approved isolated QA simulation."),
        Case("Person unavailable", "Check planned hold/retry behavior when identity dependency is transiently unavailable.", ["Approved isolated QA fault simulation"], ["Make Person unavailable only through approved test controls."], ["No message loss; domain hold/retry until resolution or period-lock handling."], ["Person outcome, held state, retry events and final resolution."], "Not ready for automation.", "No shared infrastructure failure injection."),
        Case("Domain validity failure", "Separate business validity rejection from XSD validation.", ["Approved synthetic report containing one domain-invalid value"], ["Submit a schema-valid report with approved invalid period/hours/absence code."], ["Skoletjenesten rejects after structural validation."], ["Structural result, domain validity outcome, no accepted persistence."], "Not ready for automation.", "Exact business constraints must come from approved requirements."),
        Case("Quality deviation", "Verify quality flags do not become business rejection.", ["Approved quality rule and synthetic report"], ["Submit a schema-valid, domain-valid report with an approved quality deviation."], ["Accepted with quality flag."], ["Structural validation, domain validity, quality outcome, persistence."], "Not ready for automation.", "Exact requirements and rule values must be approved."),
        Case("Duplicate delivery and business duplicate", "Keep broker redelivery, message-id duplicate, same child/period, and correction semantics separate.", ["Approved duplicate test fixture and expected behavior"], ["Replay transport message and separately submit the same business report using documented identity references."], ["Behavior is assessed against explicit idempotency/correction rules; no assumption is made."], ["Message ids, business key references, persistence count and audit evidence."], "Needs design decision before automation.", "Expected duplicate semantics remain to be specified."),
        Case("Correction and resubmission", "Verify replacement/correction behavior separately from duplicate delivery.", ["Approved correction rules and test period"], ["Submit an initial report and a corrected report using the documented correction mechanism."], ["Replacement semantics follow the approved domain specification."], ["Both report references, version/replacement relationship, final period state and Bufdata dataset metadata."], "Needs domain clarification.", "Current return/correction details are not source verified."),
        Case("Sensitive identifier boundary", "Verify permitted transit and prohibited retention/exposure boundaries.", ["Approved synthetic test identity", "Safe access to logs/traces/events/Bufdata checks"], ["Trace one synthetic report through the approved test flow."], ["Identifier may transit to Skoletjenesten for Person lookup; it is absent from logs, traces, telemetry, events and Bufdata delivery; cleanup follows resolution."], ["Redacted evidence at each boundary; never capture identifier value in BirkNext."], "Not ready for automation.", "Planned privacy policy; not source verified."),
        Case("Reporting-period lock and completeness", "Verify month boundary, lock behavior and final completeness calculation.", ["Approved test period and known expected-population evidence"], ["Exercise reporting dates through day 10 and lock on day 11 in Europe/Oslo."], ["One final locked-period dataset is prepared; unresolved held reports are rejected and identifiers cleaned up."], ["Period state, expected/received counts and final dataset metadata."], "Not ready for automation.", "Calculation details and test dates require approved domain rules."),
        Case("Reporting window boundaries", "Verify reporting before the period, days 1–10, the day-11 lock, late updates and pre-October-2026 periods.", ["Clock control or approved period fixtures", "Europe/Oslo boundary rules"], ["Exercise each boundary as a distinct case."], ["Accept/reject/lock behavior matches the approved period policy."], ["Business period, event time with timezone, lock state and outcome."], "Needs implementation and clock controls.", "Documented first period is October 2026; late/pre-period behavior needs approved rules."),
        Case("Receipt checkpoints", "Keep queue completion, application receipt, MU forwarding, and sender visibility distinct.", ["Each receipt observation source configured"], ["Submit a synthetic report and observe each receipt stage separately."], ["Only expected configured receipt checkpoints are asserted; sender return path remains unresolved until clarified."], ["Receipt id/type, trigger, timestamp and return path observation."], "Needs clarification.", "Transport receipt does not establish business completion."),
        Case("Correlation across hops", "Measure whether one message can be followed without exposing personal data.", ["Identifier mapping configured for each participating system"], ["Submit one synthetic report and record each system's separate message/correlation/trace identifiers."], ["Correlation identifiers are linked only where an explicit mapping exists."], ["Altinn, MU, broker, blob, intake, Skoletjenesten and Person references."], "Not ready for automation.", "MessageId, CorrelationId, TraceId and child references are not presumed equivalent."),
        Case("Dedicated decryption key and peek-lock", "Review receiver key access and time budget before queue completion.", ["Dedicated key reference and queue-lock observation configured"], ["Verify receiver can access the dedicated key and that peek-lock/renewal covers fetch, decrypt, validation and service call."], ["Key material is never shown; receive-and-delete is not assumed safe."], ["Key reference verification, lock mode, duration, renewals and queue completion."], "Not ready for automation.", "Configuration/runtime evidence is not yet available."),
        Case("Confidentiality grading 6/7", "Design policy checks for strict grading values.", ["Approved policy interpretation and synthetic message"], ["Submit approved synthetic cases for grading 6 and 7."], ["Outcome follows the approved security-policy decision."], ["Policy decision, restricted handling and redacted audit evidence."], "Needs clarification.", "No expected runtime outcome is asserted before policy approval."),
        Case("Bufdata final dataset", "Verify the documented final-period delivery and identifiers.", ["Bufdata test destination and checkpoints configured"], ["After lock, inspect one final dataset for the period."], ["BirkID is the delivery key; no FNR/DUF; one final dataset, not a daily change stream."], ["Dataset metadata, identifier categories, quality state and delivery receipt."], "Not ready for automation.", "Bufdata active test is outside the initial Altinn/MU scope."),
    ];

    private static MessageFlowDefinition Build(string id, string name, string[] names, FlowNodeRole[] roles, string[] hopDescriptions, string[] limitations, string source)
    {
        var evidence = new FlowEvidenceProvenance(FlowEvidenceState.Documented, source, "Planned design evidence only; no implementation, QA configuration or runtime observation is asserted.");
        var nodes = names.Select((node, i) => new MessageFlowNode(Slug(node), node, roles[i], i switch
        {
            2 or 3 => "Broker / payload store ownership to verify",
            6 when id.StartsWith("skolenarvaer", StringComparison.Ordinal) => "Skoletjenesten (planned)",
            _ => "Owner documented/planned; source verification pending",
        }, evidence)).ToList();
        var hops = hopDescriptions.Select((description, i) => new MessageFlowHop($"hop-{i + 1}", nodes[i].Id, nodes[i + 1].Id,
            description, "Not source verified", null, i == 4 && id.StartsWith("skolenarvaer", StringComparison.Ordinal) ? "XML/XSD v2.0.0 (documented)" : null,
            null, i == 2 && id.StartsWith("skolenarvaer", StringComparison.Ordinal) ? FlowPayloadMode.EncryptedReference : FlowPayloadMode.Unknown,
            "Documented role only", evidence, "Runtime behavior is not observed." )).ToList();
        return new MessageFlowDefinition
        {
            EnvironmentId = id, Name = name, Description = id.StartsWith("skolenarvaer", StringComparison.Ordinal) ? "Document-only pilot from the user-provided consolidated Spec-Kit baseline." : "Generic fixture for external asynchronous messages.",
            Nodes = nodes, Hops = hops, Limitations = limitations.ToList(),
            Validation = id.StartsWith("skolenarvaer", StringComparison.Ordinal)
                ? new List<FlowValidationDefinition> { new(FlowValidationLayer.Structural, "Skolenærværsmottaket (planned)", "XSD structure and supported schema version", evidence),
                   new(FlowValidationLayer.DomainValidity, "Skoletjenesten (planned)", "Reporting period, absence codes and hour validity", evidence),
                   new(FlowValidationLayer.DataQuality, "Skoletjenesten (planned)", "Accept with quality flag when deviation is non-blocking", evidence),
                   new(FlowValidationLayer.SecurityPolicy, "Skoletjenesten (planned)", "Confidentiality / grading policy", evidence) } : [],
            Receipts = id.StartsWith("skolenarvaer", StringComparison.Ordinal)
                ? [new MessageReceiptDefinition(MessageReceiptKind.TransportReceipt, "Meldingsutveksleren (planned)", "Intake / sender (path unresolved)", "After downstream Accepted or Held and queue completion", "Needs clarification; Altinn sender return path unresolved", evidence),
                   new MessageReceiptDefinition(MessageReceiptKind.ApplicationReceipt, "Intake or service (planned)", "Meldingsutveksleren", "After accepted/held", "Configured return route not verified", evidence)] : [],
            DataHandling = id.StartsWith("skolenarvaer", StringComparison.Ordinal)
                ? new MessageDataHandlingDefinition(["FNR or DUF in inbound report"], "May transit in intake-to-Skoletjenesten REST request for identity resolution", "Unresolved identifier may be temporarily retained encrypted by Skoletjenesten until resolution or reporting-period lock", "Encrypted hold is documented; transport/decryption key configuration is not verified", ["logs", "traces", "telemetry", "events", "Bufdata delivery"], "Remove identifier after successful resolution and after rejection/expiry at period lock", evidence)
                : new MessageDataHandlingDefinition([], "Document data boundaries explicitly", "Not configured", "Not configured", [], "Not configured", evidence),
            Deadlines = id.StartsWith("skolenarvaer", StringComparison.Ordinal)
                ? [new MessageDeadlineDefinition("Monthly reporting period", "Europe/Oslo", "Submission/correction from day 1 through day 10 of following month; first period October 2026", "Lock on the 11th; unresolved held report rejected and identifier removed; completeness/final dataset processing follows", evidence),
                   new MessageDeadlineDefinition("Bufdata delivery", "Europe/Oslo", "After reporting period lock", "One final dataset per locked period; no daily change stream in current baseline. First planned delivery: 11 November 2026.", evidence)] : [],
            ErrorPaths = id.StartsWith("skolenarvaer", StringComparison.Ordinal)
                ? new List<MessageErrorPath> { new("Invalid XML/XSD or unsupported version", "Skolenærværsmottaket (planned)", "Structural rejection", null, null, null, "Rejected", evidence),
                   new("Unknown/unresolved child", "Skoletjenesten (planned)", "Encrypted domain hold; retry on Person event and fixed interval; reject and clean up at period lock", null, "Until resolution or reporting-period lock", null, "Accepted into hold or rejected at lock", evidence),
                   new("Transient failure before acceptance/hold", "MU / broker (planned; legacy evidence only)", "Transport retry then dead-letter after configured limit", "Broker retry", null, "Dead-letter after retry exhaustion if configured", "Not source verified", evidence),
                   new("Domain validity failure", "Skoletjenesten (planned)", "Reject", null, null, null, "Rejected", evidence),
                   new("Quality deviation", "Skoletjenesten (planned)", "Accept and flag", null, null, null, "Accepted with quality flag", evidence) } :
                new List<MessageErrorPath> { new("Transient dependency unavailable", "Broker (example ownership)", "Retry; dead-letter only after configured retry exhaustion", "Transport retry", null, "Dead-letter", "Needs project configuration", evidence),
                 new("Unknown identity", "Domain service (example ownership)", "Domain hold; independent from broker dead-letter", null, "Domain retry until deadline", null, "Held / resolved / expired", evidence) },
            Lifecycle = new[] { "Submitted", "Accepted", "Transferred", "Received", "Validated", "IdentityResolved", "Held", "Persisted", "Acknowledged", "Rejected", "RetryScheduled", "DeadLettered" }.Select((state, i) => new MessageLifecycleDefinition(state, i switch { 0 => names[0], 1 or 2 => names[Math.Min(1, names.Length - 1)], 3 or 4 => names[Math.Min(5, names.Length - 1)], _ => "Owner documented/planned" }, "State semantics and owner remain separate from end-to-end completion.", evidence)).ToList(),
            Checkpoints = nodes.Select((node, i) => new FlowCheckpointDefinition($"checkpoint-{i + 1}", node.Name, node.Id, "Observation to be configured", "Manual", FlowCheckpointConfigurationState.NotConfigured, FlowCheckpointStatus.NotAssessed)).ToList(),
            OpenQuestions = id.StartsWith("skolenarvaer", StringComparison.Ordinal) ? ["Exact Altinn product/app and submission mode", "Altinn sender receipt return path", "QA channel/routing and observation access", "Person QA synthetic test identities and downstream verification"] : [],
        };
        static string Slug(string value) => new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
    }

    private static ManualIntegrationTestCase Case(string name, string purpose, string[] preconditions, string[] action, string[] expected, string[] evidence, string readiness, string limitations) =>
        new(name, purpose, "User-provided consolidated design baseline; Documented / Planned.", preconditions, action, expected, evidence, evidence, readiness, limitations);
}
