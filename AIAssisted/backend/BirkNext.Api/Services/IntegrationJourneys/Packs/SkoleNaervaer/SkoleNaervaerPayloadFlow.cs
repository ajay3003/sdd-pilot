using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

/// <summary>
/// The Skolenærvær Rapportmottak Payload &amp; Mapping Flow: Altinn → Meldingsutveksleren (MU) → Meldingsutvekslermottaket (MM) →
/// Skolenærværsrapport. Only the Altinn stage can carry generated, submitted or confirmed evidence — BirkNext builds and sends that payload
/// itself. BirkNext has no MU, MM or Skolenærværsrapport reader, so those stages show what is expected (configured references, source-derived
/// contracts, the documented journey design) and stay Not observed unless a run step actually recorded runtime evidence for them. Values
/// come from the live contract, the Integration Quality Review configuration and the run's own record; nothing downstream is inferred.
/// </summary>
public static class SkoleNaervaerPayloadFlow
{
    public const string AltinnStage = "altinn";
    public const string MuStage = "mu";
    public const string MmStage = "mm";
    public const string ReportStage = "report";
    /// <summary>The child is shown by this alias, never by its identifier.</summary>
    public const string ChildAlias = "synthetic-child-1";
    public const string Mask = "•••••••••••";

    private const string Design = "Journey design (documented architecture; not source or runtime verified)";
    private const string Iqr = "Integration Quality Review → Altinn test configuration (configured)";
    private const string NotConfigured = "Not configured";
    private static readonly Regex ElevenDigits = new(@"(?<!\d)\d{11}(?!\d)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private static readonly (string StageId, string Name, string[] Steps)[] Downstream =
    [
        (MuStage, "Meldingsutveksleren (MU)", ["mu-receive", "mu-queue"]),
        (MmStage, "Meldingsutvekslermottaket (MM)", ["mm-retrieve", "mm-decrypt", "mm-xsd", "mm-map"]),
        (ReportStage, "Skolenærværsrapport (Skoletjenesten)", ["report-receive", "report-person", "report-state"]),
    ];

    // ── Capture: a redacted payload evidence snapshot, redacted before it is kept ───────────────────────────────────

    /// <summary>
    /// The version of the redaction rules <see cref="Capture"/> applies: (1) the child's field is replaced by <see cref="Mask"/> before
    /// serialization and shown as <see cref="ChildAlias"/>; (2) the child's raw value is masked wherever else it appears; (3) any remaining
    /// 11-digit run is masked. Change it whenever these rules change; it is fixed, never derived from time or input.
    /// </summary>
    public const string RedactionVersion = "v1";

    /// <summary>
    /// A redacted payload evidence snapshot of a form payload built from <paramref name="model"/> — the same model the executor serializes
    /// and sends. Every value is redacted here, so the snapshot is safe to persist; the unredacted payload is never part of it, and no hash
    /// of it is taken (with the rest of the snapshot visible, a hash would let the child's identifier be enumerated).
    /// </summary>
    public static JourneyPayloadCapture Capture(SkoleNaervaerReportInput input, JsonObject model, IReadOnlyList<AltinnSchemaField> fields, string contentType,
        string? rootElement, string? xmlNamespace, bool qualified, string dataType, int byteCount, bool sent,
        DateTimeOffset? capturedAt = null, AltinnContractSnapshot? contract = null)
    {
        string Redact(string text) => MaskDigits(string.IsNullOrEmpty(input.ChildIdentifier) ? text : text.Replace(input.ChildIdentifier, Mask, StringComparison.Ordinal));
        var form = new SkoleNaervaerAltinnForm();
        var masked = (JsonObject)model.DeepClone();
        var childPath = form.Fields.First(item => item.Key == SkoleNaervaerAltinnForm.ChildIdentifier).Path;
        Set(masked, childPath, JsonValue.Create(Mask)!);
        var xml = contentType == "application/xml" && rootElement is not null && xmlNamespace is not null;
        var content = xml
            ? Pretty(SkoleNaervaerAltinnExecutor.ToXml(masked, fields, rootElement!, xmlNamespace!, qualified))
            : masked.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        var values = new List<PayloadFieldValue>();
        var mappings = new List<PayloadMappingEntry>();
        foreach (var expected in form.Fields)
        {
            var field = fields.FirstOrDefault(item => item.Path == expected.Path);
            var target = Leaf(model, expected.Path);
            var child = expected.Key == SkoleNaervaerAltinnForm.ChildIdentifier;
            var shown = child ? $"{ChildAlias} (masked)" : target;
            values.Add(new(expected.Path, expected.Label, Redact(shown), child ? "Test input (synthetic, masked)" : "Test input"));
            var (source, status, transformation) = expected.Key switch
            {
                SkoleNaervaerAltinnForm.ChildIdentifier => ($"{ChildAlias} (masked)", PayloadMappingStatus.Matched, (string?)null),
                SkoleNaervaerAltinnForm.PeriodFrom or SkoleNaervaerAltinnForm.PeriodTo =>
                    ((expected.Key == SkoleNaervaerAltinnForm.PeriodFrom ? input.From : input.To).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                     field?.Format == "date-time" ? PayloadMappingStatus.Transformed : PayloadMappingStatus.Matched,
                     field?.Format == "date-time" ? "date → date-time (time 00:00:00, as the app's schema declares date-time)" : null),
                _ => ((expected.Key == SkoleNaervaerAltinnForm.PlannedHours ? input.PlannedHours : input.ActualHours).ToString("R", CultureInfo.InvariantCulture),
                      field?.Type == "integer" ? PayloadMappingStatus.Transformed : PayloadMappingStatus.Matched,
                      field?.Type == "integer" ? "number → integer (the app's schema declares an integer)" : null),
            };
            mappings.Add(new("Test input", $"{expected.Key} ({expected.Label})", Redact(source), "Altinn FormData", expected.Path, Redact(shown), status, PayloadProvenance.Configured, transformation,
                "BirkNext form binding; the target path and type were checked against the app's form schema before the payload was built."));
        }
        return new JourneyPayloadCapture
        {
            Format = xml ? "xml" : "json", ContentType = contentType, DataType = dataType, Namespace = xml ? xmlNamespace : null, ByteCount = byteCount,
            MaskedContent = Redact(content), Fields = values, Mappings = mappings, SentAsGenerated = sent,
            RedactionVersion = RedactionVersion, CapturedAt = capturedAt, ContractFingerprint = contract is null ? null : Grouped(contract.Fingerprint),
            ContractSource = contract is null ? null : Label(contract.Source),
            Note = sent
                ? "Redacted evidence snapshot of the body BirkNext built and sent in this run (one string, unchanged between generation and the request). The child's identifier is masked; the unredacted body is not kept."
                : "Redacted evidence snapshot of the payload generated in memory for the dry run (not sent). The child's identifier is masked; the unredacted payload is not kept.",
        };
    }

    /// <summary>The contract snapshot whose form schema is the one in effect — the schema a payload is built from — or null when none is known.</summary>
    public static AltinnContractSnapshot? SchemaSource(AltinnAppContractView contract) =>
        contract.Fields.Count == 0 ? null : contract.Sources.FirstOrDefault(source => source.Fields is { } fields && fields.SequenceEqual(contract.Fields));

    // ── Dry run ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public static PayloadFlowView ForDryRun(SkoleNaervaerReportInput input, string reporter, string identitySource, AltinnAppContractView contract,
        IntegrationJourneyDefinition journey, AltinnTestConfiguration config, JourneyPackContext context)
    {
        var altinn = AltinnBase(contract, config);
        if (contract.Fields.Count == 0 || contract.FormDataTypeId is not { } dataType || contract.SubmissionContentType is not { } contentType)
            altinn = altinn with
            {
                Level = FlowStageEvidenceLevel.NotEvaluated, Summary = "Not generated: the app's form contract is not known",
                Runtime = "Nothing sent (dry run)", Diagnostic = "Read the public TT02 metadata (or bind the app source) so the form schema, data type and submission format are known.",
            };
        else
        {
            var (model, problem) = SkoleNaervaerAltinnExecutor.BuildModel(input, contract.Fields);
            if (model is null)
                altinn = altinn with { Level = FlowStageEvidenceLevel.Failed, Summary = "Payload could not be generated", Runtime = "Nothing sent (dry run)", Diagnostic = problem };
            else
            {
                var xml = contentType == "application/xml" && contract.XmlRootElement is not null && contract.XmlNamespace is not null;
                var body = xml ? SkoleNaervaerAltinnExecutor.ToXml(model, contract.Fields, contract.XmlRootElement!, contract.XmlNamespace!, contract.XmlElementsQualified) : model.ToJsonString();
                var capture = Capture(input, model, contract.Fields, contentType, contract.XmlRootElement, contract.XmlNamespace, contract.XmlElementsQualified, dataType,
                    Encoding.UTF8.GetByteCount(body), sent: false, contract: SchemaSource(contract));
                altinn = altinn with
                {
                    Level = FlowStageEvidenceLevel.Generated, Summary = "Generated · not submitted", Runtime = "Nothing sent (dry run)",
                    PayloadState = "Generated payload (not submitted)", Payload = [.. capture.Fields, .. Unsupplied(contract)], TechnicalPayload = capture,
                    Mappings = capture.Mappings, MappingNote = "Test input → Altinn FormData, from BirkNext's form binding (configured).",
                    Validation = [BindingCheck(null), .. FieldChecks(contract, capture), .. UnsuppliedChecks(contract), XsdNotEvaluated(),
                        new("Altinn app validation", "0 errors", "Not evaluated — dry run: nothing was sent to Altinn", "Altinn TT02 App API")],
                };
            }
        }
        altinn = altinn with
        {
            Correlation =
            [
                new("Reporter", reporter == AltinnTestingPolicy.InteractiveReporter ? "Interactive ID-porten TEST sign-in (fallback)" : $"{reporter} (alias; identifier not shown)", "Run input"),
                new("Child", $"{ChildAlias} · {ChildState(identitySource)}", "Run input"),
                new("Altinn instance", "Not created (dry run)", "—"),
                new("Data element", "Not created (dry run)", "—"),
            ],
        };
        return new PayloadFlowView
        {
            Mode = PayloadFlowMode.DryRun,
            Stages = [altinn, .. DownstreamStages(journey, config, context, run: null)],
            Timeline =
            [
                new("Generated", null, altinn.Level == FlowStageEvidenceLevel.Generated ? "Generated in memory (dry run; not timestamped)" : "Not generated"),
                new("Submitted to Altinn", null, "Not submitted (dry run)"),
                new("Altinn accepted", null, "Not submitted (dry run)"),
                new("MU observed", null, "Not observed"),
                new("MM observed", null, "Not observed"),
                new("Skolenærværsrapport observed", null, "Not observed"),
            ],
            Notes = ["Dry run: nothing was authenticated, created, sent or recorded. Downstream stages show expectations only — Expected is not Observed."],
        };
    }

    // ── Recorded run ────────────────────────────────────────────────────────────────────────────────────────────────

    public static PayloadFlowView ForRun(IntegrationJourneyRun run, IntegrationJourneyDefinition journey, AltinnTestConfiguration config, JourneyPackContext context)
    {
        JourneyStepResult? Step(string id) => run.Steps.FirstOrDefault(item => item.StepId == id);
        var data = Step(SkoleNaervaerAltinnExecutor.DataStep);
        var instance = Step(SkoleNaervaerAltinnExecutor.InstanceStep);
        var validate = Step(SkoleNaervaerAltinnExecutor.ValidateStep);
        var submit = Step(SkoleNaervaerAltinnExecutor.SubmitStep);
        var receipt = Step(SkoleNaervaerAltinnExecutor.ReceiptStep);
        var capture = data?.Payload;
        static bool Reached(JourneyStepResult? step) => step is not null && step.State is not (JourneyStepState.NotAssessed or JourneyStepState.Unavailable);
        static bool Failed(JourneyStepResult? step) => step?.State is JourneyStepState.NotVerified or JourneyStepState.UnexpectedResult;

        var (level, summary, diagnostic) =
            !Reached(data) ? (FlowStageEvidenceLevel.NotEvaluated, "Not generated in this run: it stopped before the form-data step", FirstProblem(run))
            : Failed(data) ? (FlowStageEvidenceLevel.Failed, "Generated and sent · Altinn did not store it", $"{data!.Evidence} {data.Reason}".Trim())
            : Failed(validate) ? (FlowStageEvidenceLevel.Failed, "Generated and submitted · Altinn validation did not pass", $"{validate!.Evidence} {validate.Reason}".Trim())
            : Failed(submit) ? (FlowStageEvidenceLevel.Failed, "Generated and submitted · process not completed as expected", $"{submit!.Evidence} {submit.Reason}".Trim())
            : receipt?.State == JourneyStepState.Verified ? (FlowStageEvidenceLevel.Confirmed, "Generated and submitted · Altinn confirmed the instance completed", (string?)null)
            : (FlowStageEvidenceLevel.Submitted, "Generated and submitted · form data stored in Altinn", (string?)null);

        var correlation = new List<FlowStageFact> { new("Journey run", run.RunId.ToString("N")[..8], "BirkNext run record") };
        if (new[] { receipt, submit, validate, data, instance }.FirstOrDefault(step => step?.Correlation?.StartsWith("inst-", StringComparison.Ordinal) == true)?.Correlation is { } reference)
            correlation.Add(new("Altinn instance", reference, "Altinn TT02 App API response"));
        else correlation.Add(new("Altinn instance", "Not created in this run", "—"));
        correlation.Add(new("Data element", Reached(data) && !Failed(data) ? "Stored (its id is not recorded)" : "Not stored", "Altinn TT02 App API response"));
        if (run.InputSummary.FirstOrDefault(item => item.StartsWith("Reporter:", StringComparison.Ordinal)) is { } reporterLine) correlation.Add(new("Reporter", reporterLine["Reporter:".Length..].Trim(), "Run record (redacted)"));
        correlation.Add(new("Child", ChildAlias + (run.InputSummary.FirstOrDefault(item => item.StartsWith("Child:", StringComparison.Ordinal)) is { } child && child.Contains('—')
            ? " · " + child[(child.IndexOf('—') + 1)..].Trim().TrimEnd('.') : ""), "Run record (redacted)"));

        var contract = new List<FlowStageFact>();
        if (capture?.DataType is { } dataType) contract.Add(new("Data type", dataType, "Recorded during this run (from the live contract in use)"));
        if (capture is not null) contract.Add(new("Content type", capture.ContentType, "Recorded during this run"));
        if (capture?.Namespace is { } ns) contract.Add(new("Namespace", ns, "Recorded during this run"));
        if (capture?.ContractFingerprint is { } fingerprint)
            contract.Add(new($"Contract fingerprint ({capture.ContractSource ?? "source not recorded"})", Short(fingerprint.Replace("-", "", StringComparison.Ordinal)),
                "Recorded during this run — identifies the contract the payload was built from; it is not a payload hash"));
        if (submit?.Facts.FirstOrDefault(fact => fact.Label == "Live task before transition") is { } task) contract.Add(new("Task (live, before process/next)", task.Value, "Altinn TT02 App API response"));
        if (contract.Count == 0) contract.Add(new("Contract used", "Not recorded for this run", "—"));

        var validation = new List<FlowStageCheck> { XsdNotEvaluated() };
        validation.Add(new("Altinn app validation", "0 errors", Reached(validate) ? $"{validate!.Evidence}".Trim() : "Not evaluated — the run did not reach validation", "Altinn TT02 App API"));
        validation.Add(new("Process", "Ended after one process/next", Reached(submit) ? submit!.Evidence : "Not evaluated — the run did not reach process/next", "Altinn TT02 App API"));
        validation.Add(new("Receipt", "Instance completed (read back)", Reached(receipt) ? receipt!.Evidence : "Not evaluated", "Altinn TT02 App API"));

        var altinn = new FlowStageEvidence
        {
            StageId = AltinnStage, DisplayName = "Altinn", Sequence = 1, Level = level, Summary = summary, Diagnostic = diagnostic,
            Runtime = level is FlowStageEvidenceLevel.Confirmed or FlowStageEvidenceLevel.Submitted ? "Altinn API responses recorded in this run" : level == FlowStageEvidenceLevel.Failed ? "Altinn answered with a failure" : "Not reached",
            Timestamp = data?.CapturedAt, EvidenceSource = "Altinn TT02 App API responses recorded in this run",
            PayloadState = capture is null ? "No payload recorded for this run" : capture.SentAsGenerated ? "Generated and submitted (the same body)" : "Generated payload",
            Payload = capture?.Fields ?? [], TechnicalPayload = capture, Mappings = capture?.Mappings ?? [],
            MappingNote = capture is null ? "Not recorded for this run (runs recorded before payload capture existed, or the run stopped before the form-data step). Nothing is reconstructed." : "Test input → Altinn FormData, recorded from BirkNext's form binding during this run.",
            Contract = contract, Validation = validation, Correlation = correlation,
        };

        DateTimeOffset? At(JourneyStepResult? step) => step?.State == JourneyStepState.Verified ? step.CapturedAt : null;
        return new PayloadFlowView
        {
            Mode = PayloadFlowMode.Run, RunId = run.RunId,
            Stages = [altinn, .. DownstreamStages(journey, config, context, run)],
            Timeline =
            [
                new("Generated", null, capture is null ? "Not recorded" : "Generated immediately before the form-data request (not timestamped separately)"),
                new("Form data stored in Altinn", At(data), At(data) is null ? Reached(data) ? "Failed" : "Not reached" : "Altinn response"),
                new("Submitted (process ended)", At(submit), At(submit) is null ? Reached(submit) ? "Not as expected" : "Not reached" : "Altinn response"),
                new("Altinn receipt", At(receipt), At(receipt) is null ? Reached(receipt) ? "Not confirmed" : "Not reached" : "Read back from Altinn"),
                .. Downstream.Select(stage => new FlowTimelineEntry($"{stage.Name} observed", ObservedAt(run, stage.Steps), ObservedAt(run, stage.Steps) is null ? "Not observed" : "Observed")),
            ],
            Notes =
            [
                "Only evidence recorded during this run is shown as generated, submitted, observed or confirmed. Downstream expectations are the current ones, labelled as such.",
                "Submitted to Altinn is not MU received; MU received is not MM processed; MM processed is not Skolenærværsrapport accepted.",
            ],
        };
    }

    // ── Stages ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static FlowStageEvidence AltinnBase(AltinnAppContractView contract, AltinnTestConfiguration config)
    {
        var facts = new List<FlowStageFact>
        {
            new("App", contract.ConfiguredAppId ?? NotConfigured, "Target configuration (Integration Quality Review; approved by the backend)"),
            new("Environment", config.Environment == ExternalTestEnvironment.Test ? "Test (TT02)" : config.Environment.ToString(), "Target configuration"),
        };
        if (contract.FormDataTypeId is { } dataType) facts.Add(new("Data type", dataType, SourceOf(contract, item => item.FormDataTypeId == dataType)));
        if (contract.Process.PlannedTasks.Count > 0)
            facts.Add(new("Task", string.Join(" → ", contract.Process.PlannedTasks.Select(AltinnProcessContractEvaluator.Describe)), string.Join(" + ", contract.Process.Sources)));
        if (contract.SubmissionContentType is { } contentType) facts.Add(new("Content type", contentType, SourceOf(contract, item => item.AllowedContentTypes.Contains(contentType))));
        if (contract.XmlRootElement is { } root) facts.Add(new("XML root element", root, SourceOf(contract, item => item.XmlRootElement == root)));
        if (contract.XmlNamespace is { } ns) facts.Add(new("Namespace", ns, SourceOf(contract, item => item.XmlNamespace == ns)));
        foreach (var source in contract.Sources)
            facts.Add(new($"Contract fingerprint ({Label(source.Source)})", $"{Short(source.Fingerprint)}{(source.DeployedProcess?.AppLibVersion is { } appLib ? $" · app-lib {appLib}" : "")} · read {source.CapturedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC", Label(source.Source)));
        if (contract.Operations.FirstOrDefault(item => item.Key == AltinnAppOperationKeys.UpdateData) is { } update)
            facts.Add(new("Form-data operation", $"{update.Method} {update.PathTemplate}", contract.OperationsSource));
        return new FlowStageEvidence { StageId = AltinnStage, DisplayName = "Altinn", Sequence = 1, EvidenceSource = "Local generated payload + live Altinn app contract", Contract = facts };
    }

    private static IEnumerable<FlowStageEvidence> DownstreamStages(IntegrationJourneyDefinition journey, AltinnTestConfiguration config, JourneyPackContext context, IntegrationJourneyRun? run)
    {
        var xsd = BoundXsd(config, context);
        var sequence = 2;
        foreach (var (stageId, name, stepIds) in Downstream)
        {
            var steps = journey.Steps.Where(step => stepIds.Contains(step.StepId)).ToList();
            var results = run?.Steps.Where(step => stepIds.Contains(step.StepId)).ToList() ?? [];
            var observed = results.Where(step => step.State is JourneyStepState.Observed or JourneyStepState.Verified or JourneyStepState.Accepted).ToList();
            var failed = results.Where(step => step.State is JourneyStepState.UnexpectedResult or JourneyStepState.NotVerified).ToList();
            var level = run is null ? FlowStageEvidenceLevel.Expected
                : failed.Count > 0 ? FlowStageEvidenceLevel.Failed
                : observed.Count > 0 ? FlowStageEvidenceLevel.Observed
                : FlowStageEvidenceLevel.NotObserved;
            var runtime = run is null ? "Not observed — a dry run sends nothing"
                : observed.Count + failed.Count > 0 ? string.Join(" · ", observed.Concat(failed).Select(step => $"{step.Label}: {step.Evidence}"))
                : "Not observed — BirkNext has no runtime evidence source connected for this stage";

            var contract = new List<FlowStageFact>();
            var validation = new List<FlowStageCheck>();
            var notes = new List<string>();
            var correlation = new List<FlowStageFact>
            {
                new("Correlation", observed.FirstOrDefault(step => step.Correlation is not null)?.Correlation ?? "Not observed", observed.Count > 0 ? observed[0].EvidenceSource : "—"),
                new("Correlation mapping", string.IsNullOrWhiteSpace(config.CorrelationMappingReference) ? NotConfigured : config.CorrelationMappingReference!, Iqr),
            };
            foreach (var step in steps) contract.Add(new($"Expected: {step.Label}", step.ExpectedEvidence, Design));
            switch (stageId)
            {
                case MuStage:
                    contract.Add(new("Message type", Configured(config.MessageType), Iqr));
                    contract.Add(new("MU channel", Configured(config.MuChannelReference), Iqr));
                    contract.Add(new("Queue reference", Configured(config.QueueReference), Iqr));
                    contract.Add(new("Payload store reference", Configured(config.PayloadStoreReference), Iqr));
                    contract.AddRange(XsdFacts(xsd, config, "Expected namespace"));
                    notes.Add("The MU message is a different representation from the Altinn form model (documented: it nests the hours differently), so the Altinn payload is not compared with it.");
                    break;
                case MmStage:
                    contract.AddRange(XsdFacts(xsd, config, "Input contract namespace"));
                    contract.Add(new("Mapping target", "Skolenærværsrapport", Design));
                    validation.Add(new("XSD validation (in MM)", "Valid", run is null ? "Not evaluated — a dry run sends nothing" : "Not observed", xsd is null ? "No XSD bound" : "Bound XSD (source-derived)"));
                    validation.Add(new("Mapping (in MM)", "Mapped values match the synthetic input", run is null ? "Not evaluated" : "Not observed", Design));
                    break;
                default:
                    var operations = ReportOperations(context);
                    contract.AddRange(operations.Count > 0 ? operations
                        : [new FlowStageFact("Operation", "Not known — no Skolenærværsrapport API contract is configured or present in Source Analysis", "—")]);
                    validation.Add(new("Report accepted", "Report received and stored", run is null ? "Not evaluated" : "Not observed", Design));
                    validation.Add(new("Person lookup", "Outcome for the synthetic child", run is null ? "Not evaluated" : "Not observed", Design));
                    validation.Add(new("Business validity", "Accepted (schema-valid is not business-valid)", run is null ? "Not evaluated" : "Not observed", Design));
                    break;
            }
            yield return new FlowStageEvidence
            {
                StageId = stageId, DisplayName = name, Sequence = sequence++, Level = level,
                Summary = level switch
                {
                    FlowStageEvidenceLevel.Expected => "Expected · runtime not observed",
                    FlowStageEvidenceLevel.Observed => "Observed in this run",
                    FlowStageEvidenceLevel.Failed => "Observed failure in this run",
                    _ => "Expected · runtime not observed",
                },
                Runtime = runtime, Timestamp = observed.FirstOrDefault()?.CapturedAt,
                EvidenceSource = observed.Count > 0 ? string.Join(", ", observed.Select(step => step.EvidenceSource).Distinct()) : "Configuration, source-derived contracts and the documented journey design (no runtime source)",
                ExpectationBasis = run is null ? null : "Current expected contract and configuration — not recorded during this run",
                PayloadState = "Expected representation only — no payload observed",
                Contract = contract, Validation = validation, Correlation = correlation, Notes = notes,
                MappingNote = "No field mapping is shown: BirkNext has no observed, configured, source-derived or documented field mapping for this hop.",
            };
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static SourceContract? BoundXsd(AltinnTestConfiguration config, JourneyPackContext context) =>
        config.SourceSnapshotId is { } snapshotId && !string.IsNullOrWhiteSpace(config.SourceContractId)
            ? context.SourceSnapshots.FirstOrDefault(snapshot => snapshot.Id == snapshotId)?.EvidenceDomains?.Contracts.Contracts
                .FirstOrDefault(contract => contract.Id == config.SourceContractId && contract.Type == SourceContractType.XmlSchema)
            : null;

    private static IEnumerable<FlowStageFact> XsdFacts(SourceContract? xsd, AltinnTestConfiguration config, string namespaceLabel)
    {
        if (xsd?.XmlSchema is { } schema)
        {
            const string source = "Source Analysis (bound XSD; source-derived, not runtime verified)";
            yield return new(namespaceLabel, schema.TargetNamespace ?? "No target namespace declared", source);
            yield return new("Contract", $"{xsd.Name} ({xsd.File})", source);
            yield return new("Contract version", schema.ExplicitVersion ?? schema.VersionEvidence, source);
            yield return new("Required elements", $"{schema.Elements.Count(element => element.MinOccurs > 0)} of {schema.Elements.Count}", source);
            yield break;
        }
        yield return new(namespaceLabel, "Not known — no XSD is bound to the message flow", "—");
        yield return new("Contract reference", Configured(config.ContractReference), Iqr);
        yield return new("Contract version", Configured(config.ContractVersion), Iqr);
    }

    /// <summary>Operations of a Skolenærværsrapport API contract from Source Analysis, when its producer is that component. Never guessed.</summary>
    private static IReadOnlyList<FlowStageFact> ReportOperations(JourneyPackContext context)
    {
        foreach (var snapshot in context.SourceSnapshots)
            foreach (var contract in snapshot.EvidenceDomains?.Contracts.Contracts ?? [])
                if (contract.Type == SourceContractType.OpenApi && contract.Producer is { } producer
                    && ArchitectureRuleEvaluator.MatchesAny(["Skolenærværsrapport", "Skolenaervaersrapport", "Skoletjenesten"], producer))
                    return contract.Operations.Take(10).Select(operation => new FlowStageFact("Operation", operation.Name,
                        $"Source Analysis · {snapshot.Archive.FileName} · {contract.File} (source-derived; not runtime verified)")).ToArray();
        return [];
    }

    private static DateTimeOffset? ObservedAt(IntegrationJourneyRun run, string[] steps) =>
        run.Steps.FirstOrDefault(step => steps.Contains(step.StepId) && step.State is JourneyStepState.Observed or JourneyStepState.Verified or JourneyStepState.Accepted)?.CapturedAt;

    private static FlowStageCheck BindingCheck(string? problem) => new("Field paths and types", "Each bound field exists in the app's form schema with a compatible type",
        problem ?? "Passed — every bound field was found and typed", "App's form schema (live contract)");

    private static IEnumerable<FlowStageCheck> FieldChecks(AltinnAppContractView contract, JourneyPayloadCapture capture) =>
        capture.Fields.Select(value => contract.Fields.FirstOrDefault(field => field.Path == value.Path) is { } field
            ? new FlowStageCheck(value.Path, $"{field.Type}{(field.Format is { } format ? $" · {format}" : "")}{(field.Required ? " · required" : "")}", $"Present · {value.Value}", "App's form schema (live contract)")
            : new FlowStageCheck(value.Path, "In the app's form schema", "Not in the schema", "App's form schema (live contract)"));

    private static IEnumerable<PayloadFieldValue> Unsupplied(AltinnAppContractView contract) =>
        contract.RequiredUnboundFields.Select(field => new PayloadFieldValue(field.Path, field.Path, "Not supplied by BirkNext", "App's form schema (required)",
            "Expected from the app; not verified. Altinn's validation in a live run shows whether it is needed."));

    private static IEnumerable<FlowStageCheck> UnsuppliedChecks(AltinnAppContractView contract) =>
        contract.RequiredUnboundFields.Select(field => new FlowStageCheck(field.Path, "Required", "Not supplied by BirkNext (first-run risk)", "App's form schema (live contract)"));

    private static FlowStageCheck XsdNotEvaluated() => new("XML schema validation (XSD)", "Valid",
        "Not evaluated — BirkNext checks field paths and types against the app's form schema; it does not validate the XML document against an XSD", "—");

    private static string FirstProblem(IntegrationJourneyRun run) =>
        run.Steps.FirstOrDefault(step => step.State is JourneyStepState.Unavailable or JourneyStepState.NotVerified or JourneyStepState.UnexpectedResult) is { } step
            ? $"{step.Label}: {step.Reason}" : run.StateReason;

    private static string ChildState(string identitySource) => identitySource switch
    {
        SkoleNaervaerReportInput.Registered => "Declared: registered synthetic test person (not verified by BirkNext)",
        SkoleNaervaerReportInput.Supplied => "Declared: synthetic, not registered in Person (not verified by BirkNext)",
        _ => "Generated synthetic identifier (not verified by BirkNext)",
    };

    private static string SourceOf(AltinnAppContractView contract, Func<AltinnContractSnapshot, bool> has) =>
        contract.Sources.Where(has).Select(item => Label(item.Source)).FirstOrDefault() ?? "Effective contract";

    private static string Label(AltinnContractSourceKind kind) => kind switch
    {
        AltinnContractSourceKind.PublicTt02Metadata => "Live discovered (public TT02 metadata)",
        AltinnContractSourceKind.DeployedOpenApi => "Live discovered (deployed OpenAPI)",
        AltinnContractSourceKind.SourceAnalysisSnapshot => "Imported source (Source Analysis)",
        _ => "Imported OpenAPI (not live-verified)",
    };

    private static string Configured(string? value) => string.IsNullOrWhiteSpace(value) ? NotConfigured : value;
    private static string Short(string fingerprint) => fingerprint.Length > 12 ? fingerprint[..12] + "…" : fingerprint;
    internal static string MaskDigits(string text) => ElevenDigits.Replace(text, Mask);

    /// <summary>A hex fingerprint in groups of eight (<c>xxxxxxxx-xxxxxxxx-…</c>): a run record never holds anything shaped like an 11-digit
    /// identifier, not even by chance inside a fingerprint, so the history's identifier guard stays strict.</summary>
    internal static string Grouped(string fingerprint) => string.Join('-', fingerprint.Chunk(8).Select(chunk => new string(chunk)));

    private static string Leaf(JsonObject model, string path)
    {
        JsonNode? node = model;
        foreach (var segment in path.Split('.')) node = node?[segment];
        return node is JsonValue value ? (value.TryGetValue<string>(out var text) ? text : value.ToJsonString()) : "";
    }

    private static void Set(JsonObject model, string path, JsonNode value)
    {
        var segments = path.Split('.');
        var node = model;
        foreach (var segment in segments[..^1]) node = (node[segment] as JsonObject) ?? (JsonObject)(node[segment] = new JsonObject());
        node[segments[^1]] = value;
    }

    private static string Pretty(string xml)
    {
        try { return System.Xml.Linq.XDocument.Parse(xml).ToString(); }
        catch (System.Xml.XmlException) { return xml; }
    }
}
