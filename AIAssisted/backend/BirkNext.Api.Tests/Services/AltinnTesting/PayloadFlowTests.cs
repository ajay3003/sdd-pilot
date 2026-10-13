using System.Net;
using System.Text.Json;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.AltinnTesting;

/// <summary>
/// Payload &amp; Mapping Flow for Skolenærvær Rapportmottak: Altinn → MU → MM → Skolenærværsrapport. Only the Altinn stage can be generated,
/// submitted or confirmed (BirkNext builds and sends that payload); downstream stages are expected only and stay Not observed without runtime
/// evidence. Generated ≠ submitted, expected ≠ observed, not observed ≠ failed, and no identifier or secret is ever in the flow.
/// </summary>
public sealed class PayloadFlowTests
{
    private const string Env = ActiveEventTestHarness.Env;

    [Fact]
    public async Task TheDryRun_ShowsTheGeneratedPayload_NotSubmitted_AndEveryDownstreamStageExpectedOnly()
    {
        var kit = new AltinnTestKit();
        // Like the real TT02 app: the form data type allows XML only, so the payload is the app's own XML.
        kit.Http.MetadataJson = AltinnTestKit.ApplicationMetadata(allowed: "\"application/xml\"");
        await using var harness = await ReadyAsync(kit);
        var child = SyntheticPersonIdentifier.Generate();
        var validation = await Service(harness, kit).ValidateAsync(Run(Report(child: child)), default);
        var flow = validation.PayloadFlow!;

        flow.Mode.Should().Be(PayloadFlowMode.DryRun);
        flow.Stages.Select(stage => stage.StageId).Should().Equal(SkoleNaervaerPayloadFlow.AltinnStage, SkoleNaervaerPayloadFlow.MuStage, SkoleNaervaerPayloadFlow.MmStage, SkoleNaervaerPayloadFlow.ReportStage);
        var altinn = flow.Stages[0];
        altinn.Level.Should().Be(FlowStageEvidenceLevel.Generated, "a dry run generates but never submits");
        altinn.PayloadState.Should().Contain("not submitted");
        altinn.TechnicalPayload!.SentAsGenerated.Should().BeFalse();
        altinn.TechnicalPayload.Format.Should().Be("xml");
        altinn.TechnicalPayload.MaskedContent.Should().Contain("<Skolenaervaer").And.Contain(SkoleNaervaerPayloadFlow.Mask).And.NotContain(child);
        altinn.Correlation.Single(fact => fact.Label == "Altinn instance").Value.Should().Be("Not created (dry run)");
        flow.Stages.Skip(1).Should().OnlyContain(stage => stage.Level == FlowStageEvidenceLevel.Expected && stage.Runtime.StartsWith("Not observed"));
        flow.Stages.Skip(1).Should().OnlyContain(stage => stage.Payload.Count == 0 && stage.ObservedPayload.Count == 0 && stage.TechnicalPayload == null);
        flow.Timeline.Should().OnlyContain(entry => entry.At == null, "a dry run has no observed times; none is synthesized");
        JsonSerializer.Serialize(validation).Should().NotContain(child).And.NotContain(AltinnTestKit.ReporterIdentifier).And.NotContain(AltinnTestKit.ClientSecret);
    }

    [Fact]
    public async Task TheDryRunContract_IsTheLiveDiscoveredOne_WithTheUnsuppliedRequiredFieldsShown_AndXsdValidationNotEvaluated()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        var contract = Contract(harness, kit);
        var altinn = (await Service(harness, kit).ValidateAsync(Run(Report()), default)).PayloadFlow!.Stages[0];

        altinn.Contract.Single(fact => fact.Label == "Data type").Should().Be(new FlowStageFact("Data type", contract.FormDataTypeId!, "Live discovered (public TT02 metadata)"));
        altinn.Contract.Should().Contain(fact => fact.Label == "Namespace" && fact.Value == contract.XmlNamespace);
        altinn.Contract.Should().Contain(fact => fact.Label == "Task" && fact.Value.Contains(contract.Process.PlannedTasks[0].TaskId));
        altinn.Contract.Should().Contain(fact => fact.Label == "Form-data operation" && fact.Value.Contains("/type/"));
        foreach (var field in contract.RequiredUnboundFields)
        {
            altinn.Payload.Should().Contain(value => value.Path == field.Path && value.Value == "Not supplied by BirkNext");
            altinn.Validation.Should().Contain(check => check.Label == field.Path && check.Actual.StartsWith("Not supplied by BirkNext"));
        }
        altinn.Validation.Single(check => check.Label == "XML schema validation (XSD)").Actual.Should().StartWith("Not evaluated");
        altinn.Validation.Single(check => check.Label == "Altinn app validation").Actual.Should().StartWith("Not evaluated");
    }

    [Fact]
    public async Task TheAltinnMapping_HasConfiguredProvenance_AndShowsTransformationsOnlyWhereTheSchemaRequiresThem()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        var contract = Contract(harness, kit);
        var altinn = (await Service(harness, kit).ValidateAsync(Run(Report()), default)).PayloadFlow!.Stages[0];

        altinn.Mappings.Should().HaveCount(5).And.OnlyContain(entry => entry.Provenance == PayloadProvenance.Configured && entry.TargetStage == "Altinn FormData");
        foreach (var entry in altinn.Mappings)
        {
            var field = contract.Fields.Single(item => item.Path == entry.TargetPath);
            var transformed = field.Format == "date-time" || field.Type == "integer";
            entry.Status.Should().Be(transformed ? PayloadMappingStatus.Transformed : PayloadMappingStatus.Matched, entry.TargetPath);
            (entry.Transformation is not null).Should().Be(transformed, entry.TargetPath);
        }
        altinn.Mappings.Single(entry => entry.TargetPath == "Klient.Identifikator.Fodselsnummer").SourceValue.Should().Be($"{SkoleNaervaerPayloadFlow.ChildAlias} (masked)");
        foreach (var stage in (await Service(harness, kit).ValidateAsync(Run(Report()), default)).PayloadFlow!.Stages.Skip(1))
        {
            stage.Mappings.Should().BeEmpty("no observed, configured, source-derived or documented field mapping exists downstream");
            stage.MappingNote.Should().StartWith("No field mapping is shown");
        }
    }

    [Fact]
    public async Task ASuccessfulRun_IsGeneratedSubmittedAndConfirmedInAltinn_ButDownstreamStaysNotObserved_NotFailed()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        var child = SyntheticPersonIdentifier.Generate();
        var run = await Service(harness, kit).StartAsync(Run(Report(child: child)), "local-session:loopback", default);
        var flow = (await Service(harness, kit).PayloadFlowAsync(run.RunId, default))!;

        var altinn = flow.Stages[0];
        altinn.Level.Should().Be(FlowStageEvidenceLevel.Confirmed);
        altinn.PayloadState.Should().Be("Generated and submitted (the same body)");
        altinn.TechnicalPayload!.SentAsGenerated.Should().BeTrue();
        altinn.Correlation.Single(fact => fact.Label == "Altinn instance").Value.Should().StartWith("inst-");
        flow.Timeline.Single(entry => entry.Label == "Form data stored in Altinn").At.Should().NotBeNull();
        foreach (var stage in flow.Stages.Skip(1))
        {
            stage.Level.Should().Be(FlowStageEvidenceLevel.NotObserved, "Submitted to Altinn ≠ MU received; not observed is not failed");
            stage.ExpectationBasis.Should().StartWith("Current expected contract");
            stage.Correlation.Single(fact => fact.Label == "Correlation").Value.Should().Be("Not observed");
        }
        flow.Timeline.Where(entry => entry.Label.EndsWith("observed")).Should().OnlyContain(entry => entry.At == null && entry.State == "Not observed");

        var stored = JsonSerializer.Serialize(await harness.Service(Pack(harness, kit)).GetAsync(run.RunId, default));
        stored.Should().NotContain(child).And.NotContain(AltinnTestKit.ReporterIdentifier).And.NotContain(AltinnTestKit.AltinnToken).And.NotContain(AltinnTestKit.IdPortenToken)
            .And.NotContain(AltinnTestKit.ClientSecret).And.NotContain("Bearer");
        var recorded = JsonSerializer.Deserialize<IntegrationJourneyRun>(stored)!.Steps.Single(step => step.StepId == SkoleNaervaerAltinnExecutor.DataStep).Payload!;
        recorded.MaskedContent.Should().Contain(SkoleNaervaerPayloadFlow.Mask, "the stored payload is the masked capture");
    }

    [Fact]
    public async Task ARefusedFormDataWrite_FailsTheAltinnStage_AndLeavesDownstreamNotObserved()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        kit.Http.Fail["data"] = HttpStatusCode.BadRequest;
        var run = await Service(harness, kit).StartAsync(Run(Report()), "local-session:loopback", default);
        var flow = (await Service(harness, kit).PayloadFlowAsync(run.RunId, default))!;

        flow.Stages[0].Level.Should().Be(FlowStageEvidenceLevel.Failed);
        flow.Stages[0].Summary.Should().Be("Generated and sent · Altinn did not store it");
        flow.Stages[0].Diagnostic.Should().Contain("HTTP 400");
        flow.Stages.Skip(1).Should().OnlyContain(stage => stage.Level == FlowStageEvidenceLevel.NotObserved);
    }

    [Fact]
    public async Task AFailedAltinnValidation_IsAFailedAltinnStage_WithThePayloadSubmitted()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        kit.Http.ValidationJson = """[{"severity":1,"code":"required"}]""";
        var run = await Service(harness, kit).StartAsync(Run(Report()), "local-session:loopback", default);
        var altinn = (await Service(harness, kit).PayloadFlowAsync(run.RunId, default))!.Stages[0];

        altinn.Level.Should().Be(FlowStageEvidenceLevel.Failed);
        altinn.Summary.Should().Be("Generated and submitted · Altinn validation did not pass");
        altinn.PayloadState.Should().Be("Generated and submitted (the same body)");
        altinn.Validation.Single(check => check.Label == "Altinn app validation").Actual.Should().Contain("1 error(s)");
    }

    [Fact]
    public async Task AnOldRunWithoutACapture_ShowsOnlyItsRecordedEvidence_AndReconstructsNothing()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        var run = await Service(harness, kit).StartAsync(Run(Report()), "local-session:loopback", default);
        // A run recorded before payload capture existed: same JSON, no "payload" on any step.
        var json = JsonSerializer.Serialize(run with { Steps = run.Steps.Select(step => step with { Payload = null }).ToArray() });
        json.Should().NotContain("\"MaskedContent\"");
        var old = JsonSerializer.Deserialize<IntegrationJourneyRun>(json)!;

        var journey = Pack(harness, kit).Journeys.Single(item => item.JourneyId == SkoleNaervaerJourneyPack.RapportmottakId);
        var flow = SkoleNaervaerPayloadFlow.ForRun(old, journey, AltinnTestKit.ReadyConfiguration, new JourneyPackContext(Env, null, "", new IntegrationCatalog(), []));
        var altinn = flow.Stages[0];
        altinn.PayloadState.Should().Be("No payload recorded for this run");
        altinn.Payload.Should().BeEmpty();
        altinn.TechnicalPayload.Should().BeNull();
        altinn.MappingNote.Should().Contain("Nothing is reconstructed");
        altinn.Contract.Should().NotContain(fact => fact.Label == "Data type" || fact.Label == "Content type", "only facts recorded in the run's steps are shown");
        altinn.Level.Should().Be(FlowStageEvidenceLevel.Confirmed, "the Altinn steps themselves were recorded");
    }

    [Fact]
    public async Task RecordedDownstreamEvidence_UpgradesOnlyThatStage_AndAPartialRunIsNotAFailedFlow()
    {
        await using var harness = new IntegrationJourneyHarness();
        var journey = Pack(harness, new AltinnTestKit()).Journeys.Single(item => item.JourneyId == SkoleNaervaerJourneyPack.RapportmottakId);
        var at = DateTimeOffset.Parse("2026-10-13T10:00:00Z");
        var run = new IntegrationJourneyRun
        {
            RunId = Guid.NewGuid(), PackId = SkoleNaervaerJourneyPack.Id, JourneyId = SkoleNaervaerJourneyPack.RapportmottakId, ScenarioId = "valid-report", OverallState = JourneyRunState.Partial,
            Steps = journey.Steps.Select(step => new JourneyStepResult
            {
                StepId = step.StepId, Label = step.Label,
                State = step.StepId == "mu-receive" ? JourneyStepState.Observed : step.StepId.StartsWith("altinn", StringComparison.Ordinal) ? JourneyStepState.Verified : JourneyStepState.NotAssessed,
                Evidence = step.StepId == "mu-receive" ? "Telemetry: message stored" : "", EvidenceSource = step.StepId == "mu-receive" ? "MU telemetry (test reader)" : "", CapturedAt = at,
                Correlation = step.StepId == "mu-receive" ? "mu-ref-1" : null,
            }).ToArray(),
        };
        var flow = SkoleNaervaerPayloadFlow.ForRun(run, journey, new AltinnTestConfiguration(), new JourneyPackContext(Env, null, "", new IntegrationCatalog(), []));

        var mu = flow.Stages.Single(stage => stage.StageId == SkoleNaervaerPayloadFlow.MuStage);
        mu.Level.Should().Be(FlowStageEvidenceLevel.Observed);
        mu.EvidenceSource.Should().Be("MU telemetry (test reader)");
        mu.Correlation.Single(fact => fact.Label == "Correlation").Value.Should().Be("mu-ref-1");
        flow.Stages.Where(stage => stage.StageId is SkoleNaervaerPayloadFlow.MmStage or SkoleNaervaerPayloadFlow.ReportStage)
            .Should().OnlyContain(stage => stage.Level == FlowStageEvidenceLevel.NotObserved, "MU received ≠ MM processed");
        flow.Timeline.Single(entry => entry.Label.StartsWith("Meldingsutveksleren")).At.Should().Be(at);
        flow.Stages.Should().NotContain(stage => stage.Level == FlowStageEvidenceLevel.Failed, "partial evidence is not failure");
    }

    [Fact]
    public void ACaptureMasksTheChild_AndAnyOtherElevenDigitValue_BeforeItIsKept()
    {
        var child = SyntheticPersonIdentifier.Generate();
        var fields = new[]
        {
            new AltinnSchemaField("Klient.Identifikator.Fodselsnummer", "string", null, true), new AltinnSchemaField("Periode.StartDato", "string", "date", true),
            new AltinnSchemaField("Periode.SluttDato", "string", "date", true), new AltinnSchemaField("AntallTimerEtterPlanen", "number", null, true),
            new AltinnSchemaField("AntallTimerFaktiskNaervaer", "number", null, true),
        };
        var input = new SkoleNaervaerReportInput("registered", child, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 100, 92);
        var (model, _) = SkoleNaervaerAltinnExecutor.BuildModel(input, fields);
        var capture = SkoleNaervaerPayloadFlow.Capture(input, model!, fields, "application/json", null, null, false, "model", 200, sent: true);

        JsonSerializer.Serialize(capture).Should().NotContain(child);
        capture.Format.Should().Be("json");
        capture.Fields.Single(item => item.Path == "Klient.Identifikator.Fodselsnummer").Value.Should().Be($"{SkoleNaervaerPayloadFlow.ChildAlias} (masked)");
        SkoleNaervaerPayloadFlow.MaskDigits($"ref {child} end").Should().Be($"ref {SkoleNaervaerPayloadFlow.Mask} end");
        capture.Mappings.Should().OnlyContain(entry => entry.Status == PayloadMappingStatus.Matched, "date-format and number fields need no transformation");
    }

    [Fact]
    public async Task ABoundXsd_IsShownAsASourceDerivedExpectation_NotAsRuntimeValidation()
    {
        var kit = new AltinnTestKit();
        await using var harness = await ReadyAsync(kit);
        var snapshot = AltinnTestKit.Snapshot(AltinnTestKit.AppFiles());
        var xsd = new BirkNext.SourceDomains.SourceContract
        {
            Id = "xsd-1", Type = BirkNext.SourceDomains.SourceContractType.XmlSchema, Name = "Skolenaervaersrapport", File = "contracts/skolenaervaer.xsd",
            XmlSchema = new BirkNext.SourceDomains.XmlSchemaEvidence("urn:example:skolenaervaer:v2", "2.0.0", "version attribute", [], [], [], []),
        };
        snapshot = snapshot with { EvidenceDomains = snapshot.EvidenceDomains! with { Contracts = snapshot.EvidenceDomains.Contracts with { Contracts = [xsd] } } };
        harness.Sources.Snapshots.Add(snapshot);
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration with { SourceSnapshotId = snapshot.Id, SourceContractId = "xsd-1" };

        var flow = (await Service(harness, kit).ValidateAsync(Run(Report()), default)).PayloadFlow!;
        var mu = flow.Stages.Single(stage => stage.StageId == SkoleNaervaerPayloadFlow.MuStage);
        mu.Contract.Single(fact => fact.Label == "Expected namespace").Should().Be(new FlowStageFact("Expected namespace", "urn:example:skolenaervaer:v2",
            "Source Analysis (bound XSD; source-derived, not runtime verified)"));
        mu.Level.Should().Be(FlowStageEvidenceLevel.Expected);
        var mm = flow.Stages.Single(stage => stage.StageId == SkoleNaervaerPayloadFlow.MmStage);
        mm.Validation.Single(check => check.Label == "XSD validation (in MM)").Actual.Should().StartWith("Not evaluated");
        flow.Stages.Single(stage => stage.StageId == SkoleNaervaerPayloadFlow.ReportStage).Contract
            .Should().Contain(fact => fact.Label == "Operation" && fact.Value.StartsWith("Not known"), "no Skolenærværsrapport API contract exists; no endpoint is invented");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<IntegrationJourneyHarness> ReadyAsync(AltinnTestKit kit)
    {
        var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(new IntegrationDefinition
        {
            Id = "dev:http:altinn-skolenaervaer", EnvironmentId = Env, DisplayName = "Skolenærvær – Altinn report intake", Kind = IntegrationKind.HttpApi,
            Enabled = true, Producer = "Altinn (TT02)", Consumer = new IntegrationConsumer { DisplayName = "MU" },
        });
        harness.Enroll("dev:http:altinn-skolenaervaer");
        harness.Executors.Add(new SkoleNaervaerAltinnExecutor(new ServiceCollection()
            .AddSingleton<BirkNext.Api.Services.Integrations.IIntegrationMessageFlowStore>(harness.MessageFlows)
            .AddTransient(_ => kit.Policy)
            .AddTransient(_ => kit.Contracts(harness.MessageFlows, harness.Sources))
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), kit.Authenticator(harness.Tokens), new AltinnAppApiClient(kit.Factory), TimeProvider.System));
        kit.ConfigureUnattended();
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration;
        await kit.Contracts(harness.MessageFlows, harness.Sources).DiscoverPublicAsync(Env, default);
        kit.Http.Calls.Clear();
        return harness;
    }

    private static AltinnAppContractView Contract(IntegrationJourneyHarness harness, AltinnTestKit kit) =>
        kit.Contracts(harness.MessageFlows, harness.Sources).Evaluate(Env, AltinnTestKit.AppId, harness.MessageFlows.Contract);

    private static Dictionary<string, string> Report(string reporter = AltinnTestKit.ReporterAlias, string? child = null) => new()
    {
        [SkoleNaervaerReportInput.ReporterKey] = reporter, [SkoleNaervaerReportInput.IdentitySourceKey] = "registered",
        [SkoleNaervaerAltinnForm.ChildIdentifier] = child ?? SyntheticPersonIdentifier.Generate(), [SkoleNaervaerAltinnForm.PeriodFrom] = "2026-09-01",
        [SkoleNaervaerAltinnForm.PeriodTo] = "2026-09-30", [SkoleNaervaerAltinnForm.PlannedHours] = "100", [SkoleNaervaerAltinnForm.ActualHours] = "92",
    };

    private static IntegrationJourneyRunRequest Run(IReadOnlyDictionary<string, string> inputs) => new()
    {
        EnvironmentId = Env, PackId = "skolenaervaer", JourneyId = "rapportmottak", ScenarioId = "valid-report", Confirmed = true, Inputs = inputs.ToDictionary(),
    };

    private static SkoleNaervaerJourneyPack Pack(IntegrationJourneyHarness harness, AltinnTestKit kit) => new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));

    private static IntegrationJourneyService Service(IntegrationJourneyHarness harness, AltinnTestKit kit) => harness.Service(Pack(harness, kit));
}
