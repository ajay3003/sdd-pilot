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
/// Read-only Altinn instance evidence: one GET on Altinn Storage (TT02) — never the app's GET instance, which marks the instance read for
/// person callers — observed (not executed) by the shared journey runner. Owner data never reaches evidence or history; an observed instance
/// is not a verified submission, process ended is not MU received, and an Altinn storage path is not an MU blob. All traffic is in-process.
/// </summary>
public sealed class AltinnInstanceEvidenceTests
{
    private const string Env = ActiveEventTestHarness.Env;
    private const string Tester = "11111111-1111-1111-1111-111111111111";
    private static readonly string StorageUrl = $"{AltinnTestingPolicy.Tt02StorageInstances}{AltinnTestKit.PartyId}/{AltinnTestKit.InstanceGuid:D}";

    // ── Policy and input ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheStorageRead_IsBuiltOnlyFromTheFixedTt02Base_APartyIdAndAGuid()
    {
        AltinnTestingPolicy.StorageInstance(AltinnTestKit.PartyId, AltinnTestKit.InstanceGuid).ToString().Should().Be(StorageUrl);
        AltinnTestingPolicy.RequestBlock(new Uri(StorageUrl)).Should().BeNull("platform.tt02.altinn.no is the approved TT02 Storage host");
        FluentActions.Invoking(() => AltinnTestingPolicy.StorageInstance(0, Guid.NewGuid())).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("http://local.altinn.cloud/storage/api/v1/instances/1/6a1b2c3d-0000-4000-8000-000000000001")]
    [InlineData("https://local.altinn.cloud/storage/api/v1/instances/1/6a1b2c3d-0000-4000-8000-000000000001")]
    [InlineData("https://platform.altinn.no/storage/api/v1/instances/1/6a1b2c3d-0000-4000-8000-000000000001")]
    [InlineData("https://platform.tt02.altinn.no.example.com/storage/api/v1/instances/1/x")]
    [InlineData("https://example.com/storage/api/v1/instances/1/x")]
    public void LocalSwaggerHosts_ProductionAndArbitraryHosts_AreRefused(string url) =>
        AltinnTestingPolicy.RequestBlock(new Uri(url)).Should().NotBeNull();

    [Theory]
    [InlineData("50012345", "6a1b2c3d-0000-4000-8000-000000000001", true)]
    [InlineData("0", "6a1b2c3d-0000-4000-8000-000000000001", false)]
    [InlineData("-5", "6a1b2c3d-0000-4000-8000-000000000001", false)]
    [InlineData("12a", "6a1b2c3d-0000-4000-8000-000000000001", false)]
    [InlineData("1/../2", "6a1b2c3d-0000-4000-8000-000000000001", false)]
    [InlineData("1?x=1", "6a1b2c3d-0000-4000-8000-000000000001", false)]
    [InlineData("9999999999", "6a1b2c3d-0000-4000-8000-000000000001", false)]
    [InlineData("50012345", "not-a-guid", false)]
    [InlineData("50012345", "6a1b2c3d-0000-4000-8000-000000000001/../x", false)]
    [InlineData("50012345", "{6a1b2c3d-0000-4000-8000-000000000001}", false)]
    [InlineData("50012345", "6a1b2c3d-0000-4000-8000-000000000001?language=nb", false)]
    public void InstanceReferences_AreValidatedStrictly(string party, string guid, bool valid)
    {
        var scenario = Pack(new IntegrationJourneyHarness(), new AltinnTestKit()).Journeys.Single(item => item.JourneyId == "rapportmottak").Scenarios.Single(item => item.ScenarioId == "instance-evidence");
        var prepared = SkoleNaervaerInstanceReference.Prepare(scenario, new Dictionary<string, string>
        {
            [SkoleNaervaerReportInput.ReporterKey] = AltinnTestingPolicy.InteractiveReporter, [SkoleNaervaerInstanceReference.PartyKey] = party, [SkoleNaervaerInstanceReference.InstanceKey] = guid,
        });
        prepared.Valid.Should().Be(valid);
        if (valid) string.Join(" ", prepared.Summary).Should().Contain("inst-6a1b2c3d").And.NotContain(party, "the party id is not kept in history");
    }

    // ── Response parsing and redaction ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnActiveInstance_IsReadWithItsCurrentTaskAndValidation_AndNoOwnerData()
    {
        var evidence = AltinnStorageInstanceReader.Parse(StorageInstances.ActiveTask(), AltinnTestKit.PartyId, AltinnTestKit.InstanceGuid)!;

        evidence.IdentityMatches.Should().BeTrue();
        evidence.InstanceReference.Should().Be("inst-6a1b2c3d");
        evidence.AppId.Should().Be("buf/skolenaervaer");
        evidence.StartEvent.Should().Be("StartEvent_1");
        evidence.ProcessEnded.Should().BeNull();
        evidence.CurrentTask.Should().BeEquivalentTo(new AltinnTaskEvidence("Task_1", "Utfylling", "data", "CompleteCurrentMoveToNext",
            DateTimeOffset.Parse("2026-10-12T09:00:01Z"), null, DateTimeOffset.Parse("2026-10-12T09:04:00Z"), true));
        var data = evidence.Data.Should().ContainSingle().Subject;
        data.Should().BeEquivalentTo(new { DataReference = "data-7b2c3d4e", DataType = "model", ContentType = "application/xml", Size = 1532L, Locked = false, ContentHashPresent = true, ReferenceCount = 0 });

        var serialized = JsonSerializer.Serialize(evidence);
        serialized.Should().NotContainAny(StorageInstances.OwnerPersonNumber, StorageInstances.OwnerOrganisationNumber, StorageInstances.OwnerUsername, StorageInstances.OwnerExternalId,
            StorageInstances.ContentHash, AltinnTestKit.PartyId.ToString(), AltinnTestKit.InstanceGuid.ToString("D"), AltinnTestKit.DataGuid.ToString("D"));
    }

    [Fact]
    public void ACompletedInstance_HasNoCurrentTask_AndSeveralDataElements()
    {
        var evidence = AltinnStorageInstanceReader.Parse(StorageInstances.Completed(), AltinnTestKit.PartyId, AltinnTestKit.InstanceGuid)!;
        evidence.CurrentTask.Should().BeNull();
        evidence.ProcessEnded.Should().Be(DateTimeOffset.Parse("2026-10-12T09:10:00Z"));
        evidence.EndEvent.Should().Be("EndEvent_1");
        evidence.Data.Select(item => (item.DataType, item.ContentType, item.Locked)).Should().Equal(("model", "application/xml", true), ("ref-data-as-pdf", "application/pdf", true));
        evidence.Data[1].ReferenceCount.Should().Be(1, "references are counted, never listed");
    }

    [Fact]
    public void TheAltinnStoragePath_IsKeptOnlyAsAMaskedShape()
    {
        var evidence = AltinnStorageInstanceReader.Parse(StorageInstances.ActiveTask(), AltinnTestKit.PartyId, AltinnTestKit.InstanceGuid)!;
        evidence.Data[0].RedactedStoragePath.Should().Be("buf/skolenaervaer/{guid}/data/{guid}");
        AltinnStorageInstanceReader.RedactStoragePath("buf/skolenaervaer/01821599999/data/x").Should().Be("buf/skolenaervaer/{masked}/data/x");
        evidence.Data[0].MetadataKeys.Should().Equal("source");
        JsonSerializer.Serialize(evidence).Should().NotContain(StorageInstances.OwnerPersonNumber, "metadata values and file names are never read");
    }

    [Fact]
    public void EmptyOrPartialDocuments_AreHandled()
    {
        var bare = AltinnStorageInstanceReader.Parse($$"""{ "id": "{{AltinnTestKit.PartyId}}/{{AltinnTestKit.InstanceGuid:D}}" }""", AltinnTestKit.PartyId, AltinnTestKit.InstanceGuid)!;
        bare.Data.Should().BeEmpty();
        bare.CurrentTask.Should().BeNull();
        bare.ProcessEnded.Should().BeNull();
        AltinnStorageInstanceReader.Parse(StorageInstances.ActiveTask(id: "1/00000000-0000-0000-0000-000000000000"), AltinnTestKit.PartyId, AltinnTestKit.InstanceGuid)!
            .IdentityMatches.Should().BeFalse();
        var longName = AltinnStorageInstanceReader.Parse($$"""{ "appId": "{{new string('a', 5000)}}" }""", AltinnTestKit.PartyId, AltinnTestKit.InstanceGuid)!;
        longName.AppId!.Length.Should().BeLessThanOrEqualTo(201, "free text from Altinn is capped before it reaches evidence");
    }

    [Fact]
    public async Task AnOversizedStorageAnswer_IsRefused_NotParsed()
    {
        var kit = new AltinnTestKit();
        kit.Http.StorageInstanceJson = $$"""{ "padding": "{{new string('x', AltinnStorageInstanceReader.MaxResponseBytes + 1)}}" }""";
        await new AltinnStorageInstanceReader(kit.Factory).Invoking(item => item.ReadAsync(AltinnTestKit.AltinnToken, AltinnTestKit.PartyId, AltinnTestKit.InstanceGuid, default))
            .Should().ThrowAsync<HttpRequestException>();
    }

    // ── The journey: observed, never executed ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InstanceEvidence_IsRunnableWithoutTheSubmissionContract_WhileSubmissionsStayBlocked()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration;
        var service = Service(harness, kit);

        var view = (await service.PacksAsync(Env, default)).Single().Journeys.Single(item => item.Journey.JourneyId == "rapportmottak");
        view.RunnableScenarios.Should().Equal(SkoleNaervaerJourneyPack.InstanceEvidenceScenario);
        view.Prerequisites.Single(item => item.Key == "submission-contract").Scenarios.Should().Equal(SkoleNaervaerJourneyPack.SubmissionScenarios);

        var submission = await service.StartAsync(Request("valid-report", new Dictionary<string, string>
        {
            ["reporter"] = AltinnTestingPolicy.InteractiveReporter, ["identitySource"] = "registered", ["childIdentifier"] = SyntheticPersonIdentifier.Generate(), ["periodFrom"] = "2026-10-01", ["periodTo"] = "2026-10-31",
            ["plannedHours"] = "100", ["actualHours"] = "90",
        }), Tester, default);
        submission.OverallState.Should().Be(JourneyRunState.Blocked);
        submission.StateReason.Should().Contain("Altinn app contract");
        kit.Http.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AnActiveInstance_IsObserved_WithOneStorageGet_AndNoSubmission()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration;
        harness.Tokens.Hold(Tester, Env, AltinnTestKit.AltinnToken);

        var run = await Service(harness, kit).StartAsync(Request(SkoleNaervaerJourneyPack.InstanceEvidenceScenario, References()), Tester, default);

        kit.Http.Calls.Select(call => $"{call.Method} {call.Uri}").Should().Equal($"GET {StorageUrl}");
        kit.Http.Calls.Should().NotContain(call => call.Uri.Host.EndsWith(".apps.tt02.altinn.no"), "the app's GET instance changes read status and is never used");
        kit.Http.Calls.Single().Authorization.Should().Be($"Bearer {AltinnTestKit.AltinnToken}", "the existing token cache supplies the token");
        run.Steps.Take(6).Select(step => (step.StepId, step.State)).Should().Equal(
            ("altinn-auth", JourneyStepState.Observed), ("altinn-instance", JourneyStepState.Observed), ("altinn-data", JourneyStepState.Observed),
            ("altinn-validate", JourneyStepState.Observed), ("altinn-submit", JourneyStepState.NotVerified), ("altinn-receipt", JourneyStepState.NotVerified));
        run.Steps.Should().NotContain(step => step.State == JourneyStepState.Verified, "an observed instance is not a verified submission");
        run.Steps.Skip(6).Should().OnlyContain(step => step.State == JourneyStepState.NotAssessed);
        run.OverallState.Should().Be(JourneyRunState.Partial, "HTTP 200 is not submitted");
        run.Steps.Single(step => step.StepId == "altinn-submit").Reason.Should().Contain("Not submitted").And.Contain("Task_1");
        var validate = run.Steps.Single(step => step.StepId == "altinn-validate");
        validate.Facts.Should().Contain(new JourneyEvidenceFact("Can complete task", "yes")).And.Contain(new JourneyEvidenceFact("Altinn task type", "data"));
        run.Steps.Single(step => step.StepId == "altinn-data").Facts.Should().Contain(new JourneyEvidenceFact("data-7b2c3d4e · Altinn storage path (not an MU blob)", "buf/skolenaervaer/{guid}/data/{guid}"));
    }

    [Fact]
    public async Task ACompletedInstance_ShowsProcessEnded_ButNotMuReceived_AndHistoryHoldsNoOwnerData()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration;
        harness.Tokens.Hold(Tester, Env, AltinnTestKit.AltinnToken);
        kit.Http.StorageInstanceJson = StorageInstances.Completed();

        var run = await Service(harness, kit).StartAsync(Request(SkoleNaervaerJourneyPack.InstanceEvidenceScenario, References()), Tester, default);

        run.Steps.Single(step => step.StepId == "altinn-submit").Should().Match<JourneyStepResult>(step => step.State == JourneyStepState.Observed && step.Reason.Contains("process ended ≠ MU received"));
        run.Steps.Single(step => step.StepId == "altinn-receipt").Reason.Should().Contain("not evidence that MU received");
        run.Steps.Single(step => step.StepId == "mu-receive").State.Should().Be(JourneyStepState.NotAssessed);
        run.OverallState.Should().NotBe(JourneyRunState.Completed);

        var stored = JsonSerializer.Serialize(await harness.Store.GetAsync(run.RunId, default));
        stored.Should().NotContainAny(StorageInstances.OwnerPersonNumber, StorageInstances.OwnerOrganisationNumber, StorageInstances.OwnerUsername, StorageInstances.OwnerExternalId,
            StorageInstances.ContentHash, AltinnTestKit.AltinnToken, AltinnTestKit.PartyId.ToString(), AltinnTestKit.InstanceGuid.ToString("D"), AltinnTestKit.DataGuid.ToString("D"),
            "buf/skolenaervaer/6a1b2c3d", "instanceOwner");
        stored.Should().Contain("inst-6a1b2c3d");
    }

    [Fact]
    public async Task WithoutSignIn_NothingIsRead()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration;

        var run = await Service(harness, kit).StartAsync(Request(SkoleNaervaerJourneyPack.InstanceEvidenceScenario, References()), Tester, default);

        run.Steps[0].State.Should().Be(JourneyStepState.Unavailable);
        run.Steps[0].Reason.Should().Contain("Not signed in").And.Contain("Nothing was read or changed");
        kit.Http.Calls.Should().BeEmpty();
        run.OverallState.Should().Be(JourneyRunState.NotVerified);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "HTTP 404")]
    [InlineData(HttpStatusCode.Forbidden, "HTTP 403")]
    [InlineData(HttpStatusCode.Unauthorized, "sign in again")]
    public async Task AFailedRead_IsThatStepsEvidence_AndNothingIsAttributed(HttpStatusCode status, string expected)
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration;
        harness.Tokens.Hold(Tester, Env, AltinnTestKit.AltinnToken);
        kit.Http.Fail["storage"] = status;

        var run = await Service(harness, kit).StartAsync(Request(SkoleNaervaerJourneyPack.InstanceEvidenceScenario, References()), Tester, default);

        run.Steps[0].State.Should().Be(JourneyStepState.NotVerified);
        (run.Steps[0].Evidence + run.Steps[0].Reason).Should().Contain(expected).And.NotContain("SECRET-LOOKING");
        run.Steps.Skip(1).Should().OnlyContain(step => step.State == JourneyStepState.NotAssessed);
        kit.Http.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task ADifferentInstanceInTheAnswer_IsAnUnexpectedResult()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        harness.MessageFlows.Configuration = AltinnTestKit.ReadyConfiguration;
        harness.Tokens.Hold(Tester, Env, AltinnTestKit.AltinnToken);
        kit.Http.StorageInstanceJson = StorageInstances.ActiveTask(id: "1/00000000-0000-0000-0000-000000000000");

        var run = await Service(harness, kit).StartAsync(Request(SkoleNaervaerJourneyPack.InstanceEvidenceScenario, References()), Tester, default);

        run.Steps[0].State.Should().Be(JourneyStepState.UnexpectedResult);
        run.OverallState.Should().Be(JourneyRunState.Failed);
    }

    [Fact]
    public async Task TheExecutor_NeverActsOnTheReadOnlyScenario_AndTheReaderHonoursCancellation()
    {
        var kit = new AltinnTestKit();
        await using var harness = Harness(kit);
        var journey = Pack(harness, kit).Journeys.Single(item => item.JourneyId == "rapportmottak");
        var scenario = journey.Scenarios.Single(item => item.ScenarioId == SkoleNaervaerJourneyPack.InstanceEvidenceScenario);
        scenario.StateChanging.Should().BeFalse();
        Executor(harness, kit).CanExecute(new JourneyStepContext(Guid.NewGuid(), Env, null, journey, scenario, journey.Steps[0], null, []), out _).Should().BeFalse();

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await new AltinnStorageInstanceReader(kit.Factory).Invoking(item => item.ReadAsync(AltinnTestKit.AltinnToken, AltinnTestKit.PartyId, AltinnTestKit.InstanceGuid, cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        kit.Http.Calls.Should().BeEmpty();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> References() => new()
    {
        [SkoleNaervaerReportInput.ReporterKey] = AltinnTestingPolicy.InteractiveReporter,
        [SkoleNaervaerInstanceReference.PartyKey] = AltinnTestKit.PartyId.ToString(), [SkoleNaervaerInstanceReference.InstanceKey] = AltinnTestKit.InstanceGuid.ToString("D"),
    };

    private static IntegrationJourneyRunRequest Request(string scenario, IReadOnlyDictionary<string, string> inputs) => new()
    {
        EnvironmentId = Env, PackId = "skolenaervaer", JourneyId = "rapportmottak", ScenarioId = scenario, Confirmed = true, Inputs = inputs,
    };

    private static SkoleNaervaerJourneyPack Pack(IntegrationJourneyHarness harness, AltinnTestKit kit) => new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));

    private static IServiceScopeFactory Scopes(IntegrationJourneyHarness harness, AltinnTestKit kit)
    {
        var services = new ServiceCollection();
        services.AddSingleton<BirkNext.Api.Services.Integrations.IIntegrationMessageFlowStore>(harness.MessageFlows);
        services.AddSingleton(kit.Policy);
        services.AddSingleton(_ => kit.Contracts(harness.MessageFlows, harness.Sources));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static SkoleNaervaerAltinnExecutor Executor(IntegrationJourneyHarness harness, AltinnTestKit kit) =>
        new(Scopes(harness, kit), kit.Authenticator(harness.Tokens), new AltinnAppApiClient(kit.Factory), TimeProvider.System);

    private static IntegrationJourneyHarness Harness(AltinnTestKit kit)
    {
        var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(new IntegrationDefinition
        {
            Id = "dev:http:altinn-skolenaervaer", EnvironmentId = Env, DisplayName = "Skolenærvær – Altinn report intake", Kind = IntegrationKind.HttpApi,
            Enabled = true, Producer = "Altinn (TT02)", Consumer = new IntegrationConsumer { DisplayName = "MU" },
        });
        harness.Enroll("dev:http:altinn-skolenaervaer");
        harness.Executors.Add(Executor(harness, kit));
        harness.Observers.Add(new SkoleNaervaerAltinnInstanceObserver(Scopes(harness, kit), kit.Authenticator(harness.Tokens), new AltinnStorageInstanceReader(kit.Factory), TimeProvider.System));
        return harness;
    }

    private static IntegrationJourneyService Service(IntegrationJourneyHarness harness, AltinnTestKit kit) => harness.Service(Pack(harness, kit));
}
