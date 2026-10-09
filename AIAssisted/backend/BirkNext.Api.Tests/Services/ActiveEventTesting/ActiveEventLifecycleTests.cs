using System.Text;
using System.Text.Json;
using Azure.Messaging.EventHubs;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.ActiveEventTesting.Providers.M2lbPerson;
using BirkNext.Api.Services.ActiveEventTesting.Providers.SkoleNaervaer;
using BirkNext.Integrations;
using FluentAssertions;
using H = BirkNext.Api.Tests.Services.ActiveEventTesting.ActiveEventTestHarness;

namespace BirkNext.Api.Tests.Services.ActiveEventTesting;

/// <summary>
/// The production lifecycle (trusted environment → IQR integration → destination → safety → provider → generic runner → FAKE Event Hub
/// producer → observation → history) for the Person provider, an unrelated second provider and the Skolenærvær provider. No test sends to
/// Azure: the producer factory is a recording fake.
/// </summary>
public sealed class ActiveEventLifecycleTests
{
    [Fact]
    public async Task Person_NormalCreate_RunsTheFullGenericLifecycle_AndIsLimitedEvidenceWithoutADownstreamVerifier()
    {
        await using var h = new H();
        var snapshot = await h.AddSnapshotAsync();
        var lifecycle = h.Lifecycle();

        var readiness = await lifecycle.ReadinessAsync(H.Env, H.IntegrationId, M2lbPersonScenarioProvider.Id, M2lbPersonScenarios.NormalPersonId, null, default);
        readiness.CanRun.Should().BeTrue(string.Join(" | ", readiness.Checks.Where(c => ActiveEventReadinessRules.Blocks(c.State)).Select(c => c.Detail)));
        readiness.Target!.EnvironmentType.Should().Be("Development");
        readiness.Target.TargetUrl.Should().Be("https://m2lb-dev.example.test");
        readiness.Checks.Should().Contain(c => c.Category == ActiveEventReadinessCategory.DownstreamVerification && c.State == ActiveEventReadinessState.Partial);

        var run = await h.CompletedAsync(lifecycle, await lifecycle.StartAsync(H.Request(snapshot: snapshot.Id) with { ReviewedContractFingerprint = readiness.SourceContract.ContractFingerprint }, default));

        run.Status.Should().Be(ActiveEventRunStatus.CompletedWithLimitedEvidence, "transport acceptance is never Completed without a verified downstream result");
        run.Evidence.Select(e => e.Stage).Should().ContainInOrder(ActiveEventEvidenceStage.Generated, ActiveEventEvidenceStage.SendAttempted, ActiveEventEvidenceStage.TransportAccepted,
            ActiveEventEvidenceStage.ConsumerContinuityObserved, ActiveEventEvidenceStage.ConsumerActivityObserved, ActiveEventEvidenceStage.DownstreamVerified);
        run.Evidence.Single(e => e.Stage == ActiveEventEvidenceStage.ConsumerContinuityObserved).Status.Should().Be(ActiveEventEvidenceStatus.Observed);
        run.Evidence.Single(e => e.Stage == ActiveEventEvidenceStage.DownstreamVerified).Status.Should().Be(ActiveEventEvidenceStatus.NotVerified);
        h.Producers.Sent.Should().ContainSingle();
        var sent = h.Producers.Sent.Single();
        sent.Properties["BirkNextRunId"].Should().Be(run.RunId.ToString("N"));
        sent.Properties["BirkNextSynthetic"].Should().Be(true);
        using var body = JsonDocument.Parse(sent.EventBody.ToArray());
        body.RootElement.GetProperty("payload").GetProperty("op").GetString().Should().Be("c");
        body.RootElement.GetProperty("payload").GetProperty("after").GetProperty("PersonPK").GetInt64().Should().Be(900_000_000);
        run.Events.Single().Correlation.ExpectedResourceIdentity.Should().Be(PersonCdcFixtureBuilder.ExpectedPersonId(900_000_000).ToString("D"));
        h.Catalog.Reads.Should().OnlyContain(read => read.Type == "Development" && read.Url == "https://m2lb-dev.example.test", "the catalog is read with the backend-owned type and URL");

        using var db = h.Db();
        db.ActiveEventRuns.Select(r => r.ResultJson).Should().OnlyContain(json => !json.Contains("\"payload\"") && !json.Contains("Fornavn\":"), "the body is never stored");
        db.ActiveEventSyntheticIdentities.Should().ContainSingle(r => r.Scope == M2lbPersonScenarioProvider.PersonPkScope && r.Value == 900_000_000 && r.RunId == run.RunId);
    }

    [Fact]
    public async Task Person_Replay_SendsAThenByteIdenticalA2ThenB_AndCompletesOnContinuityOnly()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        var lifecycle = h.Lifecycle();
        var run = await h.CompletedAsync(lifecycle, await lifecycle.StartAsync(H.Request(M2lbPersonScenarios.SamePersonPkReplayId), default));

        var sent = h.Producers.Sent.ToArray();
        sent.Should().HaveCount(3);
        sent[1].EventBody.ToArray().Should().Equal(sent[0].EventBody.ToArray(), "A2 is a byte-identical replay of A");
        sent[2].EventBody.ToArray().Should().NotEqual(sent[0].EventBody.ToArray());
        run.Events[1].Correlation.ReplayKind.Should().Be(ActiveEventReplayKind.ExactReplay);
        run.Scenario.RequiresDownstreamVerification.Should().BeFalse();
        run.Scenario.ResultDoesNotMean.Should().Contain("does not mean the replay was processed successfully");
        run.Status.Should().Be(ActiveEventRunStatus.Completed, "continuity is what this resilience scenario asks for — and it says what it does not mean");
    }

    [Fact]
    public async Task Person_InvalidThenValid_NeedsTheReviewedArchive_ThenSendsIWithoutPersonPkAndAValidControl()
    {
        await using var h = new H();
        var snapshot = H.Snapshot(M2lbPersonScenarios.Fields) with
        {
            IntegrationPath = H.Snapshot(M2lbPersonScenarios.Fields).IntegrationPath! with
            {
                Fields = M2lbPersonScenarios.Fields.Select(f => new FieldTrace
                {
                    Key = $"CDC PersonRecord.{f}", OriginField = f,
                    Steps = f == "PersonPK" ? [new FieldTraceStep(SourceStageKind.AdapterModel, "PersonMapper", "PersonId", FieldTransformation.Derived, "", new("Adapter/Mapping/PersonMapper.cs", 17))] : [],
                }).ToList(),
            },
        };
        await h.AddSnapshotAsync(snapshot, H.IntegrationId);

        var blocked = await h.Lifecycle().ReadinessAsync(H.Env, H.IntegrationId, M2lbPersonScenarioProvider.Id, M2lbPersonScenarios.InvalidThenValidId, null, default);
        blocked.CanRun.Should().BeFalse();
        blocked.Checks.Single(c => c.Key == "invalid-fixture").Detail.Should().StartWith("Needs review");

        h.Settings["ActiveEventTesting:Providers:m2lb.person:InvalidFixtureReviewedArchives:0"] = new string('a', 64);
        var lifecycle = h.Lifecycle();
        var run = await h.CompletedAsync(lifecycle, await lifecycle.StartAsync(H.Request(M2lbPersonScenarios.InvalidThenValidId), default));
        var sent = h.Producers.Sent.ToArray();
        sent.Should().HaveCount(2);
        JsonDocument.Parse(sent[0].EventBody.ToArray()).RootElement.GetProperty("payload").GetProperty("after").TryGetProperty("PersonPK", out _).Should().BeFalse();
        JsonDocument.Parse(sent[1].EventBody.ToArray()).RootElement.GetProperty("payload").GetProperty("after").GetProperty("PersonPK").GetInt64().Should().Be(900_000_000);
        run.Status.Should().Be(ActiveEventRunStatus.Completed);
    }

    [Fact]
    public async Task SecondUnrelatedProvider_UsesTheSameCompleteLifecycle_IncludingObservationVerifierPersistenceAndHistory()
    {
        await using var h = new H();
        h.Catalog.Extra.Add(new IntegrationDefinition
        {
            Id = "dev:eventhub:orders", EnvironmentId = H.Env, PlatformId = "dev:eventhub:m2lb", DisplayName = "Orders", Kind = IntegrationKind.EventHub, Enabled = true,
            SourceResource = "Shop.dbo.Orders", EndpointOrTopic = H.Hub, Consumer = new IntegrationConsumer { ContainerApp = "ca-orders" },
        });
        h.Verifiers.Add(new FakeVerifier(ActiveEventDownstreamOutcome.Verified, "example.orders"));
        await h.AddSnapshotAsync();
        var orders = new ExampleOrdersProvider();
        var lifecycle = h.Lifecycle(orders);

        var run = await h.CompletedAsync(lifecycle, await lifecycle.StartAsync(H.Request("orders.create", orders.ExtensionId, "dev:eventhub:orders"), default));
        var person = await h.CompletedAsync(lifecycle, await lifecycle.StartAsync(H.Request(), default));

        run.GetType().Should().Be(typeof(ActiveEventRunResult));
        run.Status.Should().Be(ActiveEventRunStatus.Completed, "every event accepted, continuity observed and the downstream verifier verified it");
        run.Evidence.Single(e => e.Stage == ActiveEventEvidenceStage.DownstreamVerified).Downstream!.Outcome.Should().Be(ActiveEventDownstreamOutcome.Verified);
        run.Evidence.Select(e => e.Stage).Distinct().Should().BeEquivalentTo(person.Evidence.Select(e => e.Stage).Distinct(), "both providers pass the same stages");
        Encoding.UTF8.GetString(h.Producers.Sent.First().EventBody.ToArray()).Should().Contain("ORD-TEST-").And.NotContain("Person");

        var history = await lifecycle.HistoryAsync(new ActiveEventHistoryQuery(H.Env), default);
        history.Select(item => item.ExtensionId).Should().BeEquivalentTo([orders.ExtensionId, M2lbPersonScenarioProvider.Id]);
        (await lifecycle.HistoryAsync(new ActiveEventHistoryQuery(H.Env, ExtensionId: orders.ExtensionId), default)).Should().ContainSingle().Which.ScenarioId.Should().Be("orders.create");
        (await lifecycle.HistoryAsync(new ActiveEventHistoryQuery(H.Env, Status: ActiveEventRunStatus.CompletedWithLimitedEvidence), default)).Should().ContainSingle().Which.ExtensionId.Should().Be(M2lbPersonScenarioProvider.Id);
    }

    [Fact]
    public async Task Skolenaervaer_AppliesOnlyToItsCdcResource_NeverToAProjectName()
    {
        await using var h = new H();
        h.Catalog.Integration = h.Catalog.Integration with { DisplayName = "Skole project Person CDC" };
        var providers = await h.Lifecycle().ProvidersAsync(H.Env, default);
        var skole = providers.Single(p => p.ExtensionId == SkoleNaervaerScenarioProvider.Id);
        skole.ApplicableIntegrationIds.Should().BeEmpty();
        skole.NotApplicableReason.Should().StartWith("No configured Event Hub integration was found for this CDC source").And.Contain("dbo.Utdanning");
        skole.Scenarios.Should().Contain(s => s.ScenarioId == "skolenaervaer.utdanning.create").And.Contain(s => s.ScenarioId == "skolenaervaer.manglende-skoletilbud.create");
        providers.Single(p => p.ExtensionId == M2lbPersonScenarioProvider.Id).ApplicableIntegrationIds.Should().Equal(H.IntegrationId);

        AddSkoleIntegration(h);
        (await h.Lifecycle().ProvidersAsync(H.Env, default)).Single(p => p.ExtensionId == SkoleNaervaerScenarioProvider.Id).ApplicableIntegrationIds.Should().Equal(H.SkoleIntegrationId);
        (await h.Lifecycle().ScenariosAsync(H.Env, H.SkoleIntegrationId, default)).Should().OnlyContain(s => s.ScenarioId.StartsWith("skolenaervaer.utdanning.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Skolenaervaer_IsNotReadyToday_ForEveryMissingPrerequisite_AndAStartSendsNothing()
    {
        await using var h = new H();
        AddSkoleIntegration(h);
        var lifecycle = h.Lifecycle();

        var readiness = await lifecycle.ReadinessAsync(H.Env, H.SkoleIntegrationId, SkoleNaervaerScenarioProvider.Id, "skolenaervaer.utdanning.create", null, default);
        readiness.CanRun.Should().BeFalse();
        var checks = readiness.Checks.ToDictionary(c => c.Key);
        checks["source-contract"].State.Should().Be(ActiveEventReadinessState.Blocked);
        checks["source-contract"].Detail.Should().Contain("Source evidence unavailable").And.Contain("SkoleAdapter source is not present");
        checks["cdc-capture"].State.Should().Be(ActiveEventReadinessState.Blocked);
        checks["cdc-capture"].Detail.Should().Contain("CDC capture of dbo.Utdanning is not proven");
        checks["fixture-review"].Detail.Should().StartWith("Needs review");
        checks["synthetic-data"].State.Should().Be(ActiveEventReadinessState.NotConfigured);
        checks["downstream"].State.Should().Be(ActiveEventReadinessState.Partial);
        checks["downstream"].Detail.Should().Contain("No Utdanning read contract");
        readiness.SyntheticSummary["Never sent"].Should().Contain("Names").And.Contain("national ids");

        var run = await lifecycle.StartAsync(H.Request("skolenaervaer.utdanning.create", SkoleNaervaerScenarioProvider.Id, H.SkoleIntegrationId, H.SkoleHub), default);
        run.Status.Should().Be(ActiveEventRunStatus.SafetyBlocked);
        run.Limitations.Should().Contain("Nothing was generated or sent.");
        h.Producers.Sent.Should().BeEmpty();

        var unsupported = await lifecycle.ReadinessAsync(H.Env, H.SkoleIntegrationId, SkoleNaervaerScenarioProvider.Id, "skolenaervaer.utdanning.natural-key-duplicate", null, default);
        unsupported.Scenario.Support.Should().Be(ActiveEventScenarioSupport.NotAssessed);
        unsupported.Checks.Should().Contain(c => c.Key == "support" && c.State == ActiveEventReadinessState.NotAvailable && c.Detail.Contains("does not define natural-key duplicate"));
    }

    [Theory]
    [InlineData("unknown-environment", "not a trusted execution environment")]
    [InlineData("duplicate", "configured more than once")]
    [InlineData("not-allowed", "does not allow active execution")]
    [InlineData("production", "not permitted against a Production environment")]
    [InlineData("production-url", "target application host carries a production marker")]
    [InlineData("fake-qa-label", "marker \"qa\"")]
    [InlineData("not-enrolled", "not enrolled for active execution")]
    [InlineData("alternate-hub", "not enrolled for active tests in backend configuration")]
    [InlineData("alternate-namespace", "not an Azure Event Hubs namespace host")]
    public async Task TargetTrust_FailsClosed_AndNothingIsSent(string variant, string expected)
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        var environment = H.Env;
        switch (variant)
        {
            case "unknown-environment": environment = "browser-guid-0b3f"; break;
            case "duplicate": h.Settings["TargetEnvironments:Trusted:1:EnvironmentId"] = H.Env; h.Settings["TargetEnvironments:Trusted:1:DisplayName"] = "Copy"; h.Settings["TargetEnvironments:Trusted:1:EnvironmentType"] = "QA"; break;
            case "not-allowed": h.Settings["TargetEnvironments:Trusted:0:ExecutionAllowed"] = "false"; break;
            case "production": h.Settings["TargetEnvironments:Trusted:0:EnvironmentType"] = "Production"; break;
            case "production-url": h.Settings["TargetEnvironments:Trusted:0:TargetUrl"] = "https://m2lb-prod.example.test"; break;
            case "fake-qa-label": h.Settings["TargetEnvironments:Trusted:0:EnvironmentType"] = "QA"; break; // the hub carries "dev", not "qa"
            case "not-enrolled": h.Settings.Remove("TargetEnvironments:Trusted:0:IntegrationIds:0"); break;
            case "alternate-hub": h.Catalog.Integration = h.Catalog.Integration with { EndpointOrTopic = "m2lb-cdc-dev.BirkM2LB.dbo.Other" }; break;
            case "alternate-namespace": h.Catalog.Platform = h.Catalog.Platform with { NamespaceFqdn = "attacker-dev.example.net" }; break;
        }
        var lifecycle = h.Lifecycle();
        var readiness = await lifecycle.ReadinessAsync(environment, H.IntegrationId, M2lbPersonScenarioProvider.Id, M2lbPersonScenarios.NormalPersonId, null, default);
        readiness.CanRun.Should().BeFalse();
        string.Join(" ", readiness.Checks.Where(c => ActiveEventReadinessRules.Blocks(c.State)).Select(c => c.Detail)).Should().Contain(expected);

        var run = await lifecycle.StartAsync(H.Request() with { TargetEnvironmentId = environment, ConfirmedDestination = h.Catalog.Integration.EndpointOrTopic }, default);
        run.Status.Should().Be(ActiveEventRunStatus.SafetyBlocked);
        h.Producers.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ConfirmedDestinationOrReviewedContract_Mismatch_BlocksBeforeGeneration()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        var lifecycle = h.Lifecycle();
        (await lifecycle.StartAsync(H.Request() with { ConfirmedDestination = "another-hub" }, default)).Limitations[0].Should().Contain("confirmed destination no longer matches");
        (await lifecycle.StartAsync(H.Request() with { ReviewedContractFingerprint = "0000000000000000" }, default)).Limitations[0].Should().Contain("changed since it was reviewed");
        h.Producers.Sent.Should().BeEmpty();
        using var db = h.Db();
        db.ActiveEventSyntheticIdentities.Should().BeEmpty("nothing is reserved for a blocked run");
    }

    [Fact]
    public async Task SourceContract_MissingOutdatedAndIncompatible_AllBlock()
    {
        await using var h = new H();
        var lifecycle = h.Lifecycle();
        async Task<string> Contract() => (await lifecycle.ReadinessAsync(H.Env, H.IntegrationId, M2lbPersonScenarioProvider.Id, M2lbPersonScenarios.NormalPersonId, null, default))
            .Checks.Single(c => c.Key == "source-contract").Detail;

        (await Contract()).Should().StartWith("Source evidence unavailable");
        var old = await h.AddSnapshotAsync(M2lbPersonScenarios.Fields.Where(f => f != "KjønnTypeFK"), DateTimeOffset.UtcNow.AddHours(-1));
        (await Contract()).Should().Contain("does not read: KjønnTypeFK");
        await h.AddSnapshotAsync();
        var outdated = await lifecycle.ReadinessAsync(H.Env, H.IntegrationId, M2lbPersonScenarioProvider.Id, M2lbPersonScenarios.NormalPersonId, old.Id, default);
        outdated.Checks.Single(c => c.Key == "source-contract").Detail.Should().Contain("does not read");
        (await Contract()).Should().StartWith("Compatible");
    }

    [Fact]
    public async Task SyntheticIdentities_AreNeverReused_HonourLegacyKeys_AndBlockWhenExhausted()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        using (var db = h.Db())
        {
            db.ActiveCdcRuns.Add(new ActiveCdcRunRecord { Id = Guid.NewGuid(), EnvironmentId = H.Env, IntegrationId = H.IntegrationId, SyntheticPersonPk = 900_000_005, Status = "Partial", ResultJson = "{}" });
            await db.SaveChangesAsync();
        }
        var lifecycle = h.Lifecycle();
        var first = await h.CompletedAsync(lifecycle, await lifecycle.StartAsync(H.Request(), default));
        var second = await h.CompletedAsync(lifecycle, await lifecycle.StartAsync(H.Request(), default));
        first.Events.Single().Correlation.SafeSourceIdentity.Should().Be("900000006", "the retired runner's keys stay used");
        second.Events.Single().Correlation.SafeSourceIdentity.Should().Be("900000007");

        h.Settings["ActiveEventTesting:SyntheticIdentityRanges:m2lb.person.PersonPK:Max"] = "900000007";
        var exhausted = await h.Lifecycle().ReadinessAsync(H.Env, H.IntegrationId, M2lbPersonScenarioProvider.Id, M2lbPersonScenarios.NormalPersonId, null, default);
        exhausted.Checks.Single(c => c.Key == "synthetic-key").State.Should().Be(ActiveEventReadinessState.Blocked);
        h.Settings.Remove("ActiveEventTesting:SyntheticIdentityRanges:m2lb.person.PersonPK:Min");
        (await h.Lifecycle().ReadinessAsync(H.Env, H.IntegrationId, M2lbPersonScenarioProvider.Id, M2lbPersonScenarios.NormalPersonId, null, default))
            .Checks.Single(c => c.Key == "synthetic-key").Detail.Should().Contain("No reserved synthetic identity range");
    }

    [Fact]
    public async Task SendOutcomes_AreNeverResent_AndMapToFailedOrInconclusive()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        h.Producers.Behaviour = (_, _) => throw new EventHubsException(false, H.Hub, "nope", EventHubsException.FailureReason.ResourceNotFound);
        var lifecycle = h.Lifecycle();
        (await h.CompletedAsync(lifecycle, await lifecycle.StartAsync(H.Request(), default))).Status.Should().Be(ActiveEventRunStatus.Failed);
        h.Producers.Behaviour = (_, _) => throw new EventHubsException(true, H.Hub, "slow", EventHubsException.FailureReason.ServiceTimeout);
        var replay = await h.CompletedAsync(lifecycle, await lifecycle.StartAsync(H.Request(M2lbPersonScenarios.SamePersonPkReplayId), default));
        replay.Status.Should().Be(ActiveEventRunStatus.Inconclusive);
        h.Producers.Sent.Should().HaveCount(2, "one Normal Person attempt, then the replay stops after its first ambiguous send");
        replay.Evidence.Should().NotContain(e => e.Stage == ActiveEventEvidenceStage.DownstreamVerified);
    }

    [Fact]
    public async Task OneRunPerIntegration_AndARunLeftRunningByAStoppedProcessIsRecoveredAsInconclusive()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        var gate = new TaskCompletionSource();
        h.Producers.Behaviour = async (_, ct) => await gate.Task.WaitAsync(ct);
        var lifecycle = h.Lifecycle();
        var started = await lifecycle.StartAsync(H.Request(), default);
        (await lifecycle.StartAsync(H.Request(), default)).Limitations[0].Should().Contain("is in flight for this integration");
        gate.SetResult();
        await h.CompletedAsync(lifecycle, started);

        var orphan = started with { RunId = Guid.NewGuid(), Status = ActiveEventRunStatus.Running, CompletedAt = null, Evidence =
            [new() { Stage = ActiveEventEvidenceStage.SendAttempted, Status = ActiveEventEvidenceStatus.Observed, EventId = "e1" }] };
        (await h.History.InsertRunningAsync(orphan, default)).Should().BeTrue();
        var recovered = await lifecycle.GetAsync(orphan.RunId, default);
        recovered!.Status.Should().Be(ActiveEventRunStatus.Inconclusive);
        recovered.Evidence.Should().Contain(e => e.Stage == ActiveEventEvidenceStage.TransportAccepted && e.Status == ActiveEventEvidenceStatus.Ambiguous && e.EventId == "e1");
    }

    [Fact]
    public async Task Cancellation_KeepsPersistedProgress_AndMarksAnInFlightSendAmbiguous()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        var sending = new TaskCompletionSource();
        h.Producers.Behaviour = async (_, ct) => { sending.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var lifecycle = h.Lifecycle();
        var started = await lifecycle.StartAsync(H.Request(), default);
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lifecycle.Cancel(started.RunId).Should().BeTrue();
        var run = await h.CompletedAsync(lifecycle, started);
        run.Status.Should().Be(ActiveEventRunStatus.Cancelled);
        run.Evidence.Should().Contain(e => e.Stage == ActiveEventEvidenceStage.TransportAccepted && e.Status == ActiveEventEvidenceStatus.Ambiguous);
    }

    [Fact]
    public async Task LegacyCdcRuns_AppearReadOnlyInTheGenericHistory()
    {
        await using var h = new H();
        var legacy = new ActiveCdcRun
        {
            RunId = Guid.NewGuid(), EnvironmentId = H.Env, IntegrationId = H.IntegrationId, Status = ActiveCdcRunStatus.Partial, StartedAt = DateTimeOffset.UtcNow.AddDays(-1),
            CompletedAt = DateTimeOffset.UtcNow.AddDays(-1), Scenario = new ActiveCdcScenario { Id = "person.normal.create", Name = "Normal Person", MessageCount = 1 }, SendAttempted = true,
            Steps = [new() { Kind = ActiveCdcStepKind.EventHubSend, State = ActiveCdcEvidenceState.Observed, Detail = "accepted" }, new() { Kind = ActiveCdcStepKind.PersonPersisted, State = ActiveCdcEvidenceState.NotAssessed }],
        };
        using (var db = h.Db())
        {
            db.ActiveCdcRuns.Add(new ActiveCdcRunRecord { Id = legacy.RunId, EnvironmentId = H.Env, IntegrationId = H.IntegrationId, StartedAt = legacy.StartedAt, Status = "Partial",
                ResultJson = JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            await db.SaveChangesAsync();
        }
        var lifecycle = h.Lifecycle();
        var item = (await lifecycle.HistoryAsync(new ActiveEventHistoryQuery(H.Env), default)).Should().ContainSingle().Subject;
        item.Legacy.Should().BeTrue();
        var run = await lifecycle.GetAsync(legacy.RunId, default);
        run!.Status.Should().Be(ActiveEventRunStatus.CompletedWithLimitedEvidence);
        run.Evidence.Should().Contain(e => e.Stage == ActiveEventEvidenceStage.TransportAccepted && e.Status == ActiveEventEvidenceStatus.Observed)
            .And.Contain(e => e.Stage == ActiveEventEvidenceStage.DownstreamVerified && e.Status == ActiveEventEvidenceStatus.NotVerified);
    }

    [Fact]
    public async Task SecretSentinel_NeverReachesTheStoredRunOrTheLogs()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        h.Producers.Behaviour = (_, _) => throw new EventHubsException(false, H.Hub, $"Endpoint=sb://x/;SharedAccessKey={H.Sentinel}", EventHubsException.FailureReason.GeneralError);
        var lifecycle = h.Lifecycle();
        var run = await h.CompletedAsync(lifecycle, await lifecycle.StartAsync(H.Request(), default));
        using var db = h.Db();
        string.Join("\n", db.ActiveEventRuns.Select(r => r.ResultJson)).Should().NotContain(H.Sentinel).And.NotContain("SharedAccessKey");
        JsonSerializer.Serialize(run).Should().NotContain(H.Sentinel);
        string.Join("\n", h.Logs.Lines).Should().NotContain(H.Sentinel);
    }

    [Fact]
    public void Registry_RejectsDuplicateExtensionAndScenarioIds_AcrossDeclaredCatalogs()
    {
        var duplicateExtension = () => new ActiveEventScenarioRegistry([new ExampleOrdersProvider(), new ExampleOrdersProvider()]);
        duplicateExtension.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate Active Event extension ID*");
        var duplicateScenario = () => new ActiveEventScenarioRegistry([new ExampleOrdersProvider(), new ExampleOrdersProvider("example.orders.copy")]);
        duplicateScenario.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate Active Event scenario ID 'orders.create'*");
        var transports = () => new ActiveEventTransportRegistry([new NullTransport("EventHub"), new NullTransport("eventhub")]);
        transports.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate Active Event transport provider*");
    }

    internal static void AddSkoleIntegration(H h) => h.Catalog.Extra.Add(new IntegrationDefinition
    {
        Id = H.SkoleIntegrationId, EnvironmentId = H.Env, PlatformId = "dev:eventhub:m2lb", DisplayName = "BIRK Utdanning CDC", Kind = IntegrationKind.EventHub, Enabled = true,
        SourceResource = "BirkM2LB.dbo.Utdanning", EndpointOrTopic = H.SkoleHub, Consumer = new IntegrationConsumer { DisplayName = "SkoleAdapter", ContainerApp = "ca-skole-adapter" },
    });

    internal sealed class ExampleOrdersProvider(string extensionId = "example.orders") : IActiveEventScenarioProvider
    {
        public string ExtensionId => extensionId;
        public string ExtensionVersion => "1";
        public string DisplayName => "Example orders";
        public string Description => "Unrelated test provider.";
        public IReadOnlyList<string> Resources => ["dbo.Orders"];
        public IReadOnlyList<ActiveEventScenarioDescriptor> CatalogScenarios => [new()
        {
            ExtensionId = ExtensionId, ExtensionVersion = "1", ProviderDisplayName = DisplayName, ScenarioId = "orders.create", ScenarioVersion = "1", DisplayName = "Order create",
            RequiredIntegrationType = "EventHub", RequiredTransportType = "EventHub", SupportedOperations = [ActiveEventOperation.Create], RequiresDownstreamVerification = true,
        }];
        public bool CanApply(ActiveEventProjectEvidenceContext context) => context.Integration?.SourceResource?.EndsWith(".Orders", StringComparison.Ordinal) == true;
        public IReadOnlyList<ActiveEventScenarioDescriptor> GetScenarios(ActiveEventProjectEvidenceContext context) => CanApply(context) ? CatalogScenarios : [];
        public Task<ActiveEventScenarioPreparation> PrepareAsync(string scenarioId, ActiveEventProjectEvidenceContext context, Guid? sourceSnapshotId, CancellationToken ct) =>
            Task.FromResult(new ActiveEventScenarioPreparation { Compatible = true, Contract = new() { ContractFingerprint = "orders-v1", ExtensionVersion = "1" } });
        public Task<ActiveEventScenarioGeneration> GenerateAsync(string scenarioId, Guid runId, ActiveEventTrustedTarget target, ActiveEventSourceContractReference contract, CancellationToken ct)
        {
            var body = Encoding.UTF8.GetBytes($"{{\"OrderId\":\"ORD-TEST-{runId:N}\",\"Amount\":12.50}}");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body)).ToLowerInvariant();
            var id = runId.ToString("N");
            return Task.FromResult(new ActiveEventScenarioGeneration { Events = [new GeneratedActiveEvent
            {
                EventId = id, ExtensionId = ExtensionId, ScenarioId = scenarioId, Operation = ActiveEventOperation.Create, Body = body, BodySha256 = hash, BodyBytes = body.Length,
                ContentType = "application/json", Correlation = new() { RunId = runId, EventId = id, EventFingerprint = hash, ExpectedResourceIdentity = $"ORD-TEST-{runId:N}" },
            }] });
        }
    }

    internal sealed class FakeVerifier(ActiveEventDownstreamOutcome outcome, string extensionId) : IActiveEventDownstreamVerifier
    {
        public bool CanVerify(ActiveEventTrustedTarget target, ActiveEventScenarioDescriptor scenario, out string reason) { reason = ""; return scenario.ExtensionId == extensionId; }
        public Task<ActiveEventDownstreamResult> VerifyAsync(ActiveEventTrustedTarget target, ActiveEventScenarioDescriptor scenario, ActiveEventCorrelation correlation, TimeSpan window, CancellationToken ct) =>
            Task.FromResult(new ActiveEventDownstreamResult { Outcome = outcome, ExpectedIdentity = correlation.ExpectedResourceIdentity, ObservedIdentity = outcome == ActiveEventDownstreamOutcome.Verified ? correlation.ExpectedResourceIdentity : null, Reason = $"{outcome} (fake verifier)" });
    }

    private sealed class NullTransport(string type) : IActiveEventTransportProvider
    {
        public string TransportType => type;
        public bool CanSend(ActiveEventTrustedTarget target, out string reason) { reason = ""; return true; }
        public Task<ActiveEventStageEvidence> SendAsync(ActiveEventTrustedTarget target, GeneratedActiveEvent activeEvent, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
    }
}
