using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// Message Verification: the read-only message-verification scenario of Rapportmottak, run through the real journey service, runner, run
/// store and pack, with fake telemetry. Evidence comes only from BirkNext's history and approved, enrolled telemetry; nothing is sent; HTTP 200
/// is never a business pass; missing evidence is never a failure; and every record is sanitized and kept per refresh.
/// </summary>
public sealed class MessageVerificationTests
{
    private const string Env = ActiveEventTestHarness.Env;
    private static readonly Guid MessageId = Guid.Parse("8c0f6a2e-1b2c-4d3e-8f40-0123456789ab");
    private static readonly Guid InstanceGuid = Guid.Parse("6a1b2c3d-0000-4000-8000-000000000001");
    private const string InstanceReference = "inst-6a1b2c3d";

    private static IntegrationDefinition AltinnIntake => new()
    {
        Id = "dev:http:altinn-skolenaervaer", EnvironmentId = Env, DisplayName = "Skolenærvær – Altinn report intake", Kind = IntegrationKind.HttpApi,
        Enabled = true, Producer = "Altinn (TT02)", Consumer = new IntegrationConsumer { DisplayName = "MU" },
    };

    // ── Altinn (BirkNext history) ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnExistingRun_IsReadFromHistoryOnly_ConfirmedInAltinn_ButDownstreamStaysUncorrelated_AndNoTelemetryIsRead()
    {
        var t = await TestBed.CreateAsync();
        var source = await t.RecordSourceRunAsync(receipt: JourneyStepState.Verified);
        var run = await t.VerifyAsync(Inputs("valid-report", sourceRun: source.RunId));
        var record = run.MessageVerification!;

        run.ScenarioId.Should().Be(SkoleNaervaerJourneyPack.MessageVerificationScenario);
        record.Observations.Single(item => item.System == "Altinn").Stage.Should().Be(MessageEvidenceStage.Confirmed);
        record.Assertions.Single(item => item.Id == "altinn-confirmed").Verdict.Should().Be(MessageVerificationVerdict.Passed);
        record.Assertions.Single(item => item.Id == "skole-accepted").Verdict.Should().Be(MessageVerificationVerdict.NotEvaluated, "Godkjent is stored only in Skoletjenesten's database");
        record.Verdict.Should().Be(MessageVerificationVerdict.NotEvaluated, "Altinn confirmation is not MM processing or Skoletjenesten acceptance");
        record.Providers.Single(item => item.ProviderId == SkoleNaervaerMessageEvidence.MmProvider).State.Should().Be(MessageEvidenceProviderState.NotEvaluated);
        record.Correlations.Should().ContainSingle(item => item.TargetType == "Altinn instance reference" && item.Certainty == MessageCorrelationCertainty.ExactIdentifierMatch);
        record.MissingEvidence.Should().Contain(item => item.What == "MM and Skoletjenesten evidence");
        run.Steps.Single(step => step.StepId == SkoleNaervaerAltinnExecutor.ReceiptStep).State.Should().Be(JourneyStepState.Observed, "recorded evidence is observed, not re-verified");
        t.Telemetry.Calls.Should().BeEmpty("without an MU message id nothing can be correlated downstream");
        t.Kit.Http.Calls.Should().BeEmpty("a verification never calls Altinn");
        (await t.Harness.Store.GetAsync(source.RunId, default))!.Should().BeEquivalentTo(source, "the source run is never changed");
    }

    [Theory]
    [InlineData(JourneyStepState.NotVerified, JourneyStepState.Verified, MessageEvidenceStage.Submitted)]
    [InlineData(JourneyStepState.NotAssessed, JourneyStepState.NotAssessed, MessageEvidenceStage.Generated)]
    public async Task ASubmittedButUnconfirmed_OrAGeneratedOnly_Run_IsShownAsSuch_NeverAsConfirmed(JourneyStepState receipt, JourneyStepState submit, MessageEvidenceStage expected)
    {
        var t = await TestBed.CreateAsync();
        var source = await t.RecordSourceRunAsync(receipt: receipt, submit: submit);
        var record = (await t.VerifyAsync(Inputs("valid-report", sourceRun: source.RunId))).MessageVerification!;

        record.Observations.Single(item => item.System == "Altinn").Stage.Should().Be(expected);
        record.Assertions.Single(item => item.Id == "altinn-confirmed").Verdict.Should().NotBe(MessageVerificationVerdict.Passed);
    }

    [Fact]
    public async Task AMissingRun_OrOneFromAnotherEnvironment_IsNotFound_AndNothingIsReconstructed()
    {
        var t = await TestBed.CreateAsync();
        var foreign = await t.RecordSourceRunAsync(receipt: JourneyStepState.Verified, environment: "other-env");
        foreach (var id in new[] { Guid.NewGuid(), foreign.RunId })
        {
            var record = (await t.VerifyAsync(Inputs("valid-report", sourceRun: id))).MessageVerification!;
            record.Providers.Single(item => item.ProviderId == SkoleNaervaerMessageEvidence.AltinnProvider).State.Should().Be(MessageEvidenceProviderState.QueryCompletedNoEvidence);
            record.Observations.Single(item => item.System == "Altinn").Stage.Should().Be(MessageEvidenceStage.NotObserved);
            record.Verdict.Should().NotBe(MessageVerificationVerdict.Passed);
        }
    }

    [Fact]
    public async Task AnAltinnInstance_FindsItsRecordedRun_ByTheShortReference_AndTheFullIdIsNeverStored()
    {
        var t = await TestBed.CreateAsync();
        var source = await t.RecordSourceRunAsync(receipt: JourneyStepState.Verified);
        var run = await t.VerifyAsync(Inputs("valid-report", instance: InstanceGuid));

        run.MessageVerification!.SourceRunId.Should().Be(source.RunId);
        run.MessageVerification.AltinnInstanceReference.Should().Be(InstanceReference);
        var persisted = (await t.Harness.PersistedJsonAsync(run.RunId))!;
        persisted.Should().NotContain(InstanceGuid.ToString("D")).And.NotContain(InstanceGuid.ToString("N"));
    }

    // ── MM and Skoletjenesten (telemetry) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AMessageId_IsCorrelatedExactly_ThroughMmAndSkoletjenesten_ButHttp200NeverPassesTheBusinessOutcome()
    {
        var t = await TestBed.CreateAsync();
        t.Telemetry.Mm = Rows(Mm("Mottatt", kvittering: "Sendt"));
        t.Telemetry.Skole = Rows(Skole("Mottatt"));
        var run = await t.VerifyAsync(Inputs("valid-report", message: MessageId));
        var record = run.MessageVerification!;

        t.Telemetry.Calls.Should().Equal($"mm:{MessageId:D}:72", $"skole:{MessageId:D}:72");
        Assertion(record, "mm-delivered").Should().Be(MessageVerificationVerdict.Passed);
        Assertion(record, "skole-200").Should().Be(MessageVerificationVerdict.Passed);
        Assertion(record, "receipt-sent").Should().Be(MessageVerificationVerdict.Passed);
        Assertion(record, "skole-accepted").Should().Be(MessageVerificationVerdict.NotEvaluated);
        record.Verdict.Should().Be(MessageVerificationVerdict.NotEvaluated, "HTTP 200 Mottatt is the same for accepted, held and replayed reports");
        record.Correlations.Should().Contain(item => item.TargetType.StartsWith("MM queue") && item.Certainty == MessageCorrelationCertainty.ExplicitMappingObserved && item.TargetValue == "koe-1");
        record.Correlations.Should().Contain(item => item.TargetType.StartsWith("Skoletjenesten MeldingId") && item.Certainty == MessageCorrelationCertainty.ExactIdentifierMatch);
        Step(run, "mu-queue").Should().Be(JourneyStepState.Observed);
        Step(run, "report-receive").Should().Be(JourneyStepState.Observed);
        Step(run, "report-state").Should().Be(JourneyStepState.NotAssessed);
        Step(run, "mu-receive").Should().Be(JourneyStepState.NotAssessed, "MU has no evidence source");
    }

    [Fact]
    public async Task AnUnknownChild_IsReceived_ButHeldStaysNotVerified()
    {
        var t = await TestBed.CreateAsync();
        t.Telemetry.Mm = Rows(Mm("Mottatt"));
        var record = (await t.VerifyAsync(Inputs("person-not-found", message: MessageId))).MessageVerification!;

        Assertion(record, "mm-delivered").Should().Be(MessageVerificationVerdict.Passed);
        Assertion(record, "skole-held").Should().Be(MessageVerificationVerdict.NotEvaluated);
        record.Verdict.Should().Be(MessageVerificationVerdict.NotEvaluated);
    }

    [Fact]
    public async Task InvalidXml_RejectedByMmAtXsd_Passes_AndTheLaterMmChecksAreNotReached()
    {
        var t = await TestBed.CreateAsync();
        t.Telemetry.Mm = Rows(Mm("Avvist", aarsak: "SkjemaBrudd"));
        var run = await t.VerifyAsync(Inputs("invalid-xsd", message: MessageId));

        Assertion(run.MessageVerification!, "mm-xsd-rejection").Should().Be(MessageVerificationVerdict.Passed);
        Assertion(run.MessageVerification!, "not-forwarded").Should().Be(MessageVerificationVerdict.Passed);
        run.MessageVerification!.Verdict.Should().Be(MessageVerificationVerdict.Passed, "every assertion is proven by MM's own outcome event");
        Step(run, "mm-decrypt").Should().Be(JourneyStepState.Observed);
        Step(run, "mm-xsd").Should().Be(JourneyStepState.Observed);
        run.Steps.Single(step => step.StepId == "mm-xsd").Evidence.Should().Contain("SkjemaBrudd");
        Step(run, "mm-map").Should().Be(JourneyStepState.NotAssessed);
    }

    [Fact]
    public async Task InvalidXml_ThatReachedSkoletjenesten_Fails()
    {
        var t = await TestBed.CreateAsync();
        t.Telemetry.Mm = Rows(Mm("Mottatt"));
        t.Telemetry.Skole = Rows(Skole("Mottatt"));
        var record = (await t.VerifyAsync(Inputs("invalid-xsd", message: MessageId))).MessageVerification!;

        record.Verdict.Should().Be(MessageVerificationVerdict.Failed);
        Assertion(record, "not-forwarded").Should().Be(MessageVerificationVerdict.Failed);
    }

    [Theory]
    [InlineData("skole-rejection", "UGYLDIG_TIMEVERDI", MessageVerificationVerdict.Passed)]
    [InlineData("skole-rejection", "SkjemaBrudd", MessageVerificationVerdict.Failed)]
    [InlineData("mm-rejection", "DekrypteringFeilet", MessageVerificationVerdict.Passed)]
    [InlineData("mm-rejection", "UTENFOR_GYLDIGHETSVINDU", MessageVerificationVerdict.Failed)]
    public async Task Rejections_AreAttributedByTheirReasonCode_ToMmOrSkoletjenesten(string expected, string reason, MessageVerificationVerdict verdict)
    {
        var t = await TestBed.CreateAsync();
        t.Telemetry.Mm = Rows(Mm("Avvist", aarsak: reason));
        (await t.VerifyAsync(Inputs(expected, message: MessageId))).MessageVerification!.Verdict.Should().Be(verdict);
    }

    [Fact]
    public async Task ASkoletjenestenRejection_SeenInItsOwnLog_Passes_AndTheHttpRejectionIsObserved()
    {
        var t = await TestBed.CreateAsync();
        t.Telemetry.Skole = Rows(Skole("Avvist", "IKKE_RAPPORTERINGSPERIODE"));
        var run = await t.VerifyAsync(Inputs("skole-rejection", message: MessageId));

        run.MessageVerification!.Verdict.Should().Be(MessageVerificationVerdict.Passed);
        run.MessageVerification.Observations.Should().Contain(item => item.System == "Skoletjenesten" && item.Stage == MessageEvidenceStage.SkoleHttpResponseObserved);
        Step(run, "report-receive").Should().Be(JourneyStepState.Observed);
    }

    [Fact]
    public async Task ALockedPeriodRejection_Passes_ButThePeriodStatusIsBlocked_SoTheVerdictIsBlocked()
    {
        var t = await TestBed.CreateAsync();
        t.Telemetry.Mm = Rows(Mm("Avvist", aarsak: "UTENFOR_GYLDIGHETSVINDU"));
        var record = (await t.VerifyAsync(Inputs("period-locked", message: MessageId))).MessageVerification!;

        Assertion(record, "outside-window").Should().Be(MessageVerificationVerdict.Passed);
        Assertion(record, "period-status").Should().Be(MessageVerificationVerdict.Blocked);
        record.Verdict.Should().Be(MessageVerificationVerdict.Blocked);
    }

    [Fact]
    public async Task ATemporaryFailure_ThenDelivery_Passes_AndADeadLetterFails()
    {
        var t = await TestBed.CreateAsync();
        var at = DateTimeOffset.Parse("2026-10-14T08:00:00Z");
        t.Telemetry.Mm = Rows(Mm("Midlertidig", aarsak: "EierUtilgjengelig", forsoek: 1, at: at), Mm("Mottatt", forsoek: 2, at: at.AddMinutes(1)));
        (await t.VerifyAsync(Inputs("temporary-failure", message: MessageId))).MessageVerification!.Verdict.Should().Be(MessageVerificationVerdict.Passed);

        t.Telemetry.Mm = Rows(Mm("Midlertidig", aarsak: "EierUtilgjengelig", forsoek: 1, at: at), Mm("Dodbrev", aarsak: "EierUtilgjengelig", forsoek: 6, at: at.AddMinutes(9)));
        var failed = (await t.VerifyAsync(Inputs("temporary-failure", message: MessageId))).MessageVerification!;
        Assertion(failed, "retried-completed").Should().Be(MessageVerificationVerdict.Failed);
        failed.Verdict.Should().Be(MessageVerificationVerdict.Failed);
    }

    [Fact]
    public async Task ADuplicate_NeedsTwoAnsweredDeliveries_AndNeverProvesASingleStoredRecord()
    {
        var t = await TestBed.CreateAsync();
        t.Telemetry.Mm = Rows(Mm("Mottatt"));
        var once = (await t.VerifyAsync(Inputs("duplicate-report", message: MessageId))).MessageVerification!;
        Assertion(once, "repeat-answered").Should().Be(MessageVerificationVerdict.NotEvaluated, "one delivery is not a repeat");

        t.Telemetry.Mm = Rows(Mm("Mottatt", koe: "koe-1"), Mm("Mottatt", koe: "koe-2"));
        var twice = (await t.VerifyAsync(Inputs("duplicate-report", message: MessageId))).MessageVerification!;
        Assertion(twice, "repeat-answered").Should().Be(MessageVerificationVerdict.Passed);
        Assertion(twice, "single-record").Should().Be(MessageVerificationVerdict.NotEvaluated);
        twice.Verdict.Should().Be(MessageVerificationVerdict.NotEvaluated);
    }

    [Fact]
    public async Task ACorrection_IsDelivered_ButTheEffectiveVersionIsNotVerified()
    {
        var t = await TestBed.CreateAsync();
        t.Telemetry.Mm = Rows(Mm("Mottatt"));
        var record = (await t.VerifyAsync(Inputs("correction", message: MessageId))).MessageVerification!;
        Assertion(record, "effective-version").Should().Be(MessageVerificationVerdict.NotEvaluated);
        record.Verdict.Should().Be(MessageVerificationVerdict.NotEvaluated);
    }

    [Fact]
    public async Task NoLogs_IsNotObserved_NotFailed()
    {
        var t = await TestBed.CreateAsync();
        t.Telemetry.Mm = new(MessageEvidenceProviderState.QueryCompletedNoEvidence, "No record.", [], DateTimeOffset.UtcNow);
        var run = await t.VerifyAsync(Inputs("valid-report", message: MessageId));

        Assertion(run.MessageVerification!, "mm-delivered").Should().Be(MessageVerificationVerdict.NotEvaluated);
        run.MessageVerification!.Verdict.Should().NotBe(MessageVerificationVerdict.Failed, "a missing log entry is not evidence of failure");
        run.MessageVerification.Observations.Should().Contain(item => item.System == "MM" && item.Stage == MessageEvidenceStage.NotObserved);
        Step(run, "mu-queue").Should().Be(JourneyStepState.NotAssessed);
    }

    [Fact]
    public async Task AccessDenied_IsBlocked_AndTheStepIsUnavailable_NeverFailed()
    {
        var t = await TestBed.CreateAsync();
        t.Telemetry.Mm = new(MessageEvidenceProviderState.AccessDenied, "The backend identity may not read this telemetry (HTTP 403).", [], DateTimeOffset.UtcNow);
        var run = await t.VerifyAsync(Inputs("valid-report", message: MessageId));

        run.MessageVerification!.Verdict.Should().Be(MessageVerificationVerdict.Blocked);
        run.MessageVerification.Observations.Should().Contain(item => item.System == "MM" && item.Stage == MessageEvidenceStage.AccessDenied);
        Step(run, "mu-queue").Should().Be(JourneyStepState.Unavailable);
        run.MessageVerification.MissingEvidence.Should().Contain(item => item.NextAction.StartsWith("Grant BirkNext's backend identity read access"));
    }

    [Theory]
    [InlineData(false, true, true, MessageEvidenceProviderState.NotConfigured)]
    [InlineData(true, false, true, MessageEvidenceProviderState.Blocked)]
    [InlineData(true, true, false, MessageEvidenceProviderState.NotConfigured)]
    public async Task TelemetryThatIsDisabledNotEnrolledOrNotConfigured_IsNeverRead(bool enabled, bool enrol, bool configured, MessageEvidenceProviderState state)
    {
        var t = await TestBed.CreateAsync(enrolTelemetry: enrol, telemetryConfigured: configured, telemetryEnabled: enabled);
        var record = (await t.VerifyAsync(Inputs("valid-report", message: MessageId))).MessageVerification!;

        t.Telemetry.Calls.Should().BeEmpty("a configured or named source is not an approved one");
        record.Providers.Single(item => item.ProviderId == SkoleNaervaerMessageEvidence.MmProvider).State.Should().Be(state);
        record.Verdict.Should().Be(MessageVerificationVerdict.Blocked);
    }

    // ── Coverage and Bufdata ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Coverage_IsBlocked_WithoutAnApprovedGraphQlClient_AndBufdataIsNeverPassed()
    {
        var t = await TestBed.CreateAsync();
        var coverage = (await t.VerifyAsync(Inputs("coverage", message: MessageId))).MessageVerification!;
        coverage.Verdict.Should().Be(MessageVerificationVerdict.Blocked);
        coverage.Providers.Single(item => item.ProviderId == SkoleNaervaerMessageEvidence.CoverageProvider).State.Should().Be(MessageEvidenceProviderState.Blocked);
        coverage.Providers.Single(item => item.ProviderId == SkoleNaervaerMessageEvidence.CoverageProvider).Detail.Should().Contain("aggregate");

        var bufdata = (await t.VerifyAsync(Inputs("bufdata-delivery", message: MessageId))).MessageVerification!;
        bufdata.Verdict.Should().Be(MessageVerificationVerdict.NotEvaluated);
        Assertion(bufdata, "bufdata-received").Should().Be(MessageVerificationVerdict.NotEvaluated);
        bufdata.Providers.Single(item => item.ProviderId == SkoleNaervaerMessageEvidence.BufdataProvider).Detail.Should().Contain("Klargjort");
    }

    // ── Persistence and safety ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EachRefresh_IsANewRecord_EarlierOnesStayUnchanged_AndTheStoredRecordIsSanitized()
    {
        var t = await TestBed.CreateAsync();
        t.Telemetry.Mm = Rows(Mm("Midlertidig", aarsak: "EierUtilgjengelig"));
        var first = await t.VerifyAsync(Inputs("temporary-failure", message: MessageId));
        var firstJson = await t.Harness.PersistedJsonAsync(first.RunId);
        t.Telemetry.Mm = Rows(Mm("Midlertidig", aarsak: "EierUtilgjengelig"), Mm("Mottatt", forsoek: 2));
        var second = await t.VerifyAsync(Inputs("temporary-failure", message: MessageId));

        (await t.Harness.PersistedJsonAsync(first.RunId)).Should().Be(firstJson, "a refresh never overwrites earlier evidence");
        var reopened = (await t.Harness.Store.GetAsync(second.RunId, default))!.MessageVerification!;
        reopened.TargetKey.Should().Be(first.MessageVerification!.TargetKey).And.Be($"message:{MessageId:D}");
        reopened.Verdict.Should().Be(MessageVerificationVerdict.Passed);
        first.MessageVerification.Verdict.Should().Be(MessageVerificationVerdict.NotEvaluated);
        reopened.RuleVersion.Should().Be("1");
        reopened.EvaluatedAt.Should().BeOnOrAfter(first.MessageVerification.EvaluatedAt);

        var json = (await t.Harness.PersistedJsonAsync(second.RunId))!;
        // The run's own id (N format, the runner's default step correlation) is random hex; it can contain 11 digits by chance and is not an identifier of a person.
        Regex.IsMatch(json.Replace(second.RunId.ToString("N"), "", StringComparison.Ordinal), @"(?<!\d)\d{11}(?!\d)").Should().BeFalse("no identifier-shaped value is stored");
        json.Should().NotContainAny("Aktoer", "Bearer", AltinnTestKit.ClientSecret, AltinnTestKit.AltinnToken, AltinnTestKit.IdPortenToken, "Authorization", "Cookie");
        (await t.Harness.Service(t.Pack).HistoryAsync(new IntegrationJourneyHistoryQuery(Env, SkoleNaervaerJourneyPack.Id, SkoleNaervaerJourneyPack.RapportmottakId,
            SkoleNaervaerJourneyPack.MessageVerificationScenario), default)).Should().HaveCount(2, "the existing run history keeps both");
    }

    [Fact]
    public async Task AVerification_WritesNothing_SendsNothing_AndRunsWithoutReporterAuthentication()
    {
        var t = await TestBed.CreateAsync();
        t.Telemetry.Mm = Rows(Mm("Mottatt"));
        var run = await t.VerifyAsync(Inputs("valid-report", message: MessageId));

        run.OverallState.Should().NotBe(JourneyRunState.Blocked);
        t.Kit.Http.Calls.Should().BeEmpty("no Altinn call");
        t.Harness.Active.Producers.Sent.Should().BeEmpty("no Event Hub send");
        t.Harness.Tokens.Should().NotBeNull();
        run.InputSummary.Should().Contain($"MU message id {MessageId:D}").And.Contain("Expected outcome: Valid report");
    }

    [Fact]
    public async Task ARunRecordedBeforeMessageVerification_StillReadsBack()
    {
        var t = await TestBed.CreateAsync();
        var source = await t.RecordSourceRunAsync(receipt: JourneyStepState.Verified);
        var json = JsonSerializer.Serialize(source, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContain("\"messageVerification\":{");
        (await t.Harness.Store.GetAsync(source.RunId, default))!.MessageVerification.Should().BeNull();
    }

    [Theory]
    [InlineData("", "", "", "valid-report", "72", "at least one")]
    [InlineData("not-a-guid", "", "", "valid-report", "72", "must be a GUID")]
    [InlineData("", "", "8c0f6a2e-1b2c-4d3e-8f40-0123456789ab", "unknown", "72", "expected outcomes")]
    [InlineData("", "", "8c0f6a2e-1b2c-4d3e-8f40-0123456789ab", "valid-report", "9999", "observation windows")]
    public void Inputs_AreValidated_AndOnlyBoundedChoicesAreAccepted(string run, string instance, string message, string expected, string window, string error)
    {
        var prepared = SkoleNaervaerMessageInput.Prepare(new Dictionary<string, string>
        {
            [SkoleNaervaerMessageInput.SourceRunKey] = run, [SkoleNaervaerMessageInput.InstanceKey] = instance, [SkoleNaervaerMessageInput.MessageKey] = message,
            [SkoleNaervaerMessageInput.ExpectedKey] = expected, [SkoleNaervaerMessageInput.WindowKey] = window,
        });
        prepared.Errors.Should().Contain(item => item.Contains(error));
    }

    [Fact]
    public void TheTelemetryQueries_ArePredefined_Parameterized_Bounded_AndReadOnlyAllowListedFields()
    {
        foreach (var classic in new[] { false, true })
        {
            var mm = LogAnalyticsSkoleNaervaerMessageTelemetry.MmQuery(MessageId, classic);
            mm.Should().Contain($"== \"{MessageId:D}\"").And.Contain("\"MeldingUtfall\"").And.EndWith("take 50").And.NotContain("Aktoer");
            var skole = LogAnalyticsSkoleNaervaerMessageTelemetry.SkoleQuery(MessageId, classic);
            skole.Should().Contain($"== \"{MessageId:D}\"").And.Contain(LogAnalyticsSkoleNaervaerMessageTelemetry.SkoleCategory).And.EndWith("take 50");
            foreach (var query in new[] { mm, skole })
                query.Should().NotContainAny(".create", ".drop", ".set", ".append", "externaldata", "evaluate", "union *");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static MessageVerificationVerdict Assertion(MessageVerificationRecord record, string id) => record.Assertions.Single(item => item.Id == id).Verdict;
    private static JourneyStepState Step(IntegrationJourneyRun run, string id) => run.Steps.Single(step => step.StepId == id).State;

    private static Dictionary<string, string> Inputs(string expected, Guid? sourceRun = null, Guid? instance = null, Guid? message = null) => new()
    {
        [SkoleNaervaerMessageInput.ExpectedKey] = expected, [SkoleNaervaerMessageInput.WindowKey] = "72",
        [SkoleNaervaerMessageInput.SourceRunKey] = sourceRun?.ToString("D") ?? "", [SkoleNaervaerMessageInput.InstanceKey] = instance?.ToString("D") ?? "",
        [SkoleNaervaerMessageInput.MessageKey] = message?.ToString("D") ?? "",
    };

    private static MmOutcomeEvent Mm(string utfall, string? aarsak = null, int forsoek = 1, string? kvittering = null, string koe = "koe-1", DateTimeOffset? at = null) =>
        new(at ?? DateTimeOffset.Parse("2026-10-14T08:00:00Z"), utfall, aarsak, forsoek, kvittering, koe, "Skolenaervaer", "2.0.0");

    private static SkoleIntakeEvent Skole(string utfall, string? code = null) => new(DateTimeOffset.Parse("2026-10-14T08:00:01Z"), utfall, code ?? "-", "2026-09", false);

    private static MessageTelemetryRead<T> Rows<T>(params T[] rows) => new(MessageEvidenceProviderState.Available, $"{rows.Length} record(s).", rows, DateTimeOffset.UtcNow);

    private sealed class FakeTelemetry : ISkoleNaervaerMessageTelemetry
    {
        public List<string> Calls { get; } = [];
        public MessageTelemetryRead<MmOutcomeEvent> Mm { get; set; } = new(MessageEvidenceProviderState.QueryCompletedNoEvidence, "No record.", [], DateTimeOffset.UtcNow);
        public MessageTelemetryRead<SkoleIntakeEvent> Skole { get; set; } = new(MessageEvidenceProviderState.QueryCompletedNoEvidence, "No record.", [], DateTimeOffset.UtcNow);

        public Task<MessageTelemetryRead<MmOutcomeEvent>> MmOutcomesAsync(IntegrationPlatform platform, Guid messageId, int windowHours, CancellationToken ct)
        {
            Calls.Add($"mm:{messageId:D}:{windowHours}");
            return Task.FromResult(Mm);
        }

        public Task<MessageTelemetryRead<SkoleIntakeEvent>> SkoleIntakeAsync(IntegrationPlatform platform, Guid messageId, int windowHours, CancellationToken ct)
        {
            Calls.Add($"skole:{messageId:D}:{windowHours}");
            return Task.FromResult(Skole);
        }
    }

    /// <summary>A trusted DEV target with the report-intake integration, the Altinn executor (journey trigger) and MM / Skoletjenesten integrations.</summary>
    private sealed class TestBed
    {
        public required IntegrationJourneyHarness Harness { get; init; }
        public required AltinnTestKit Kit { get; init; }
        public required FakeTelemetry Telemetry { get; init; }
        public required SkoleNaervaerJourneyPack Pack { get; init; }

        public static async Task<TestBed> CreateAsync(bool enrolTelemetry = true, bool telemetryConfigured = true, bool telemetryEnabled = true)
        {
            var kit = new AltinnTestKit();
            var harness = new IntegrationJourneyHarness();
            var catalog = harness.Active.Catalog;
            catalog.Extra.Add(AltinnIntake);
            harness.Enroll(AltinnIntake.Id);
            if (telemetryConfigured)
                catalog.Platform = catalog.Platform with { RuntimeEvidence = (catalog.Platform.RuntimeEvidence ?? new()) with { TelemetryWorkspaceId = "2f9c1d7e-4b6a-4e1f-9a2b-3c4d5e6f7a8b" } };
            IntegrationDefinition[] downstream =
            [
                new() { Id = "dev:servicebus:mu-mm", EnvironmentId = Env, PlatformId = catalog.Platform.Id, DisplayName = "MU → MM", Kind = IntegrationKind.ServiceBus, Enabled = telemetryEnabled,
                    Producer = "MU", Consumer = new IntegrationConsumer { DisplayName = "Meldingsutvekslermottak" } },
                new() { Id = "dev:http:mm-skole", EnvironmentId = Env, PlatformId = catalog.Platform.Id, DisplayName = "MM → Skoletjenesten", Kind = IntegrationKind.HttpApi, Enabled = telemetryEnabled,
                    Producer = "MM", Consumer = new IntegrationConsumer { DisplayName = "Skoletjenesten" } },
            ];
            catalog.Extra.AddRange(downstream);
            if (enrolTelemetry) foreach (var item in downstream) harness.Enroll(item.Id);

            var scopes = new ServiceCollection().AddSingleton<IIntegrationCatalogService>(catalog).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            var telemetry = new FakeTelemetry();
            var evidence = new SkoleNaervaerMessageEvidence(harness.Store, scopes, telemetry, TimeProvider.System);
            harness.Observers.Add(new SkoleNaervaerMessageVerificationObserver(evidence));
            harness.Executors.Add(new SkoleNaervaerAltinnExecutor(new ServiceCollection()
                .AddSingleton<IIntegrationMessageFlowStore>(harness.MessageFlows).AddTransient(_ => kit.Policy).AddTransient(_ => kit.Contracts(harness.MessageFlows, harness.Sources))
                .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), kit.Authenticator(harness.Tokens), new AltinnAppApiClient(kit.Factory), TimeProvider.System));
            harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration;
            await Task.CompletedTask;
            return new TestBed { Harness = harness, Kit = kit, Telemetry = telemetry, Pack = new SkoleNaervaerJourneyPack(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources), evidence) };
        }

        public async Task<IntegrationJourneyRun> VerifyAsync(Dictionary<string, string> inputs)
        {
            var run = await Harness.Service(Pack).StartAsync(new IntegrationJourneyRunRequest
            {
                EnvironmentId = Env, PackId = SkoleNaervaerJourneyPack.Id, JourneyId = SkoleNaervaerJourneyPack.RapportmottakId, ScenarioId = SkoleNaervaerJourneyPack.MessageVerificationScenario,
                Confirmed = true, Inputs = inputs,
            }, "local-session:loopback", default);
            run.OverallState.Should().NotBe(JourneyRunState.Blocked, run.StateReason);
            return (await Harness.Store.GetAsync(run.RunId, default))!;
        }

        /// <summary>An earlier Rapportmottak submission as BirkNext records it (redacted), with its instance's short reference on the Altinn steps.</summary>
        public async Task<IntegrationJourneyRun> RecordSourceRunAsync(JourneyStepState receipt, JourneyStepState submit = JourneyStepState.Verified, string environment = Env)
        {
            var at = DateTimeOffset.Parse("2026-10-13T10:00:00Z");
            JourneyStepResult Altinn(string id, JourneyStepState state) => new()
            {
                StepId = id, Label = id, State = state, Evidence = state == JourneyStepState.Verified ? "HTTP 200" : "", EvidenceSource = "Altinn TT02 App API", CapturedAt = at, Correlation = InstanceReference,
            };
            var run = new IntegrationJourneyRun
            {
                RunId = Guid.NewGuid(), PackId = SkoleNaervaerJourneyPack.Id, JourneyId = SkoleNaervaerJourneyPack.RapportmottakId, ScenarioId = "valid-report", ScenarioName = "Valid report",
                EnvironmentId = environment, StartedAt = at, CompletedAt = at, OverallState = JourneyRunState.Partial,
                Steps =
                [
                    Altinn(SkoleNaervaerAltinnExecutor.AuthStep, JourneyStepState.Verified), Altinn(SkoleNaervaerAltinnExecutor.InstanceStep, JourneyStepState.Verified),
                    Altinn(SkoleNaervaerAltinnExecutor.DataStep, submit == JourneyStepState.NotAssessed ? JourneyStepState.Verified : JourneyStepState.Verified),
                    Altinn(SkoleNaervaerAltinnExecutor.ValidateStep, JourneyStepState.Verified), Altinn(SkoleNaervaerAltinnExecutor.SubmitStep, submit),
                    Altinn(SkoleNaervaerAltinnExecutor.ReceiptStep, receipt),
                ],
            };
            (await Harness.Store.InsertAsync(run, default)).Should().BeTrue();
            return run;
        }
    }
}
