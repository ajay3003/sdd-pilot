using System.Text.Json;
using Azure.Messaging.EventHubs;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using FluentAssertions;
using H = BirkNext.Api.Tests.Services.ActiveCdcTests.ActiveCdcTestHarness;

namespace BirkNext.Api.Tests.Services.ActiveCdcTests;

/// <summary>
/// Same PersonPK replay (runtime resilience): A (X) → A2 (exact replay) → B (Y), one attempt each, per-partition checkpoint progression,
/// conservative aggregation, and the domains it can never claim (Person, database idempotency, outbox, Service Bus, natural-key duplicates).
/// </summary>
public sealed class ActiveCdcReplayTests
{
    private const string Replay = ActiveCdcScenarioCatalog.SamePersonPkReplayId;

    private static ActiveCdcRunRequest Request(IqrSourceSnapshot? snapshot = null) => H.Request(snapshot) with { ScenarioId = Replay };

    private static List<PartitionRuntime> Positions(params (string Id, long Seq)[] partitions) => partitions.Select(p => new PartitionRuntime(p.Id, p.Seq, DateTimeOffset.UtcNow, false)).ToList();
    private static List<PartitionCheckpoint> Checkpoints(params (string Id, long Seq)[] partitions) => partitions.Select(p => new PartitionCheckpoint(p.Id, p.Seq, 0, DateTimeOffset.UtcNow)).ToList();

    /// <summary>One partition; each send moves it by one: T0 10, after A 11, after A2 12, after B 13.</summary>
    private static void SinglePartition(H h, long checkpointAfterStart = 13)
    {
        h.Metadata.Script = call => Positions(("0", 10 + Math.Min(call, 3)));
        h.Checkpoints.Script = call => call == 0 ? Checkpoints(("0", 9)) : Checkpoints(("0", checkpointAfterStart));
    }

    private static async Task<ActiveCdcRun> RunAsync(H h, ActiveCdcRunRequest? request = null, string? type = "Development")
    {
        var started = await h.Service().StartAsync(request ?? Request(), type, null);
        return started.Status == ActiveCdcRunStatus.Running ? await h.CompletedAsync(started) : started;
    }

    private static string[] SentLabels(H h) => h.Producers.Sent.Select(e => e.Properties["BirkNextMessage"].ToString()!).ToArray();

    [Fact]
    public void Scenario_IsRegisteredAsRuntimeResilience_WithItsLimitationAndBoundedPassMeaning()
    {
        var s = ActiveCdcScenarioCatalog.Find(Replay)!;
        (s.Id, s.Version, s.Name, s.Category, s.MessageCount).Should().Be((Replay, "1", "Same PersonPK replay", "Runtime resilience", 3));
        s.Limitation.Should().Be("This does not verify database idempotency or duplicate Person handling.");
        s.PassMeaning.Should().Contain("observable Event Hub → Person Adapter consumer path continued");
        s.PassDoesNotMean.Should().Contain("database idempotency").And.Contain("Person row count").And.Contain("natural-key duplicate");
        s.Name.Should().NotContain("Duplicate Person");
        ActiveCdcScenarioCatalog.NormalPerson.MessageCount.Should().Be(1, "Normal Person is unchanged");
    }

    [Fact]
    public async Task Replay_SendsAThenExactReplayThenControl_OnceEach_AndPassesOnlyOnPerPartitionProgression()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        SinglePartition(h);
        var run = await RunAsync(h);

        SentLabels(h).Should().Equal(["A", "A2", "B"], "strictly sequential, exactly three attempts, no hidden retry");
        var sent = h.Producers.Sent.ToArray();
        sent[1].EventBody.ToArray().Should().Equal(sent[0].EventBody.ToArray(), "A2 is a byte-identical replay of A");
        sent[2].EventBody.ToArray().Should().NotEqual(sent[0].EventBody.ToArray());
        sent.Select(e => e.MessageId).Should().OnlyHaveUniqueItems("only transport metadata differs");
        using (var a = JsonDocument.Parse(sent[0].EventBody.ToArray()))
        using (var b = JsonDocument.Parse(sent[2].EventBody.ToArray()))
        {
            var x = a.RootElement.GetProperty("payload").GetProperty("after").GetProperty("PersonPK").GetInt32();
            var y = b.RootElement.GetProperty("payload").GetProperty("after").GetProperty("PersonPK").GetInt32();
            (x, y).Should().Be((900_000_000, 900_000_001), "X and Y are the next two unused keys of the reserved range");
            b.RootElement.GetProperty("payload").GetProperty("op").GetString().Should().Be("c", "B is a valid create");
            b.RootElement.GetProperty("payload").GetProperty("after").GetProperty("Fornavn").GetString().Should().EndWith("-B");
        }

        run.Status.Should().Be(ActiveCdcRunStatus.Passed, run.StatusReason);
        run.StatusReason.Should().Contain("All required expectations were met");
        (run.Scenario.PassMeaning, run.Scenario.PassDoesNotMean).Should().Be((ActiveCdcScenarioCatalog.SamePersonPkReplay.PassMeaning, ActiveCdcScenarioCatalog.SamePersonPkReplay.PassDoesNotMean), "the stored run carries the bounded meaning shown in UI and export");
        run.Messages.Select(m => (m.Label, m.SyntheticPersonPk)).Should().Equal([("A", 900_000_000), ("A2", 900_000_000), ("B", 900_000_001)]);
        run.Messages.Select(m => m.AdvancedPartitions["0"]).Should().Equal([11L, 12L, 13L]);
        run.Messages.Should().OnlyContain(m => m.SendState == ActiveCdcEvidenceState.Observed && m.CheckpointState == ActiveCdcEvidenceState.Observed);
        run.CheckpointSnapshots.Select(c => c.Label).Should().Equal(["T0 — before A", "T1 — after A", "T2 — after A2", "T3 — after B"]);
        run.Step(ActiveCdcStepKind.ReplayEquivalence)!.State.Should().Be(ActiveCdcEvidenceState.Observed);
        run.Step(ActiveCdcStepKind.FollowingEventProgression)!.State.Should().Be(ActiveCdcEvidenceState.Observed);
        AssertBoundaries(run);
        run.Step(ActiveCdcStepKind.CorrelatedReplayError)!.Detail.Should().Contain("No correlated replay error evidence was observed").And.Contain("not a statement that no error occurred");

        using var db = h.Db();
        var record = db.ActiveCdcRuns.Single(r => r.Id == run.RunId);
        (record.SyntheticPersonPk, record.SyntheticPersonPkControl).Should().Be((900_000_000, 900_000_001));
        record.ResultJson.Should().NotContain("\"after\"").And.NotContain("\"payload\"");
        (await h.Store.UpdateAsync(run with { Status = ActiveCdcRunStatus.Failed }, default)).Should().BeFalse("a completed replay run is immutable");

        var next = await RunAsync(h);
        next.RunId.Should().NotBe(run.RunId);
        next.Messages[0].SyntheticPersonPk.Should().Be(900_000_002, "Y of the previous run is never reused");
    }

    private static void AssertBoundaries(ActiveCdcRun run)
    {
        foreach (var kind in new[] { ActiveCdcStepKind.PersonPersisted, ActiveCdcStepKind.DatabaseIdempotency, ActiveCdcStepKind.PersonRowCount, ActiveCdcStepKind.OverwriteBehavior,
                     ActiveCdcStepKind.OutboxCreated, ActiveCdcStepKind.OutboxDuplication, ActiveCdcStepKind.ServiceBusDelivered, ActiveCdcStepKind.SubscriberProcessed })
            run.Step(kind)!.State.Should().Be(ActiveCdcEvidenceState.NotAssessed, $"{kind} is never assessed by an Active CDC run");
        run.Step(ActiveCdcStepKind.NaturalKeyDuplicate)!.State.Should().Be(ActiveCdcEvidenceState.NotTested);
    }

    [Fact]
    public async Task Replay_AcrossPartitions_ComparesEachSendOnItsOwnPartition()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        // A → partition 0, A2 → partition 1, B → partition 0. No global ordering exists across partitions.
        h.Metadata.Script = call => call switch
        {
            0 => Positions(("0", 10), ("1", 50)), 1 => Positions(("0", 11), ("1", 50)), 2 => Positions(("0", 11), ("1", 51)), _ => Positions(("0", 12), ("1", 51)),
        };
        h.Checkpoints.Script = call => call == 0 ? Checkpoints(("0", 9), ("1", 49)) : Checkpoints(("0", 12), ("1", 51));
        var run = await RunAsync(h);
        run.Messages.Select(m => string.Join(",", m.AdvancedPartitions.Keys)).Should().Equal(["0", "1", "0"]);
        run.Status.Should().Be(ActiveCdcRunStatus.Passed);
        run.CheckpointSnapshots[^1].Partitions.Should().BeEquivalentTo([new ActiveCdcPartitionPosition("0", 12, 12), new ActiveCdcPartitionPosition("1", 51, 51)]);

        // The same positions, but partition 1 (where A2 landed) never checkpoints past 50: progression after the replay is not shown,
        // even though partition 0 moved past B.
        await using var h2 = new H();
        await h2.AddSnapshotAsync();
        h2.Metadata.Script = h.Metadata.Script;
        h2.Checkpoints.Script = call => call == 0 ? Checkpoints(("0", 9), ("1", 49)) : Checkpoints(("0", 12), ("1", 50));
        var stalled = await RunAsync(h2);
        stalled.Step(ActiveCdcStepKind.ObserveReplay)!.State.Should().Be(ActiveCdcEvidenceState.TimedOut);
        stalled.Step(ActiveCdcStepKind.ObserveControl)!.State.Should().Be(ActiveCdcEvidenceState.Observed);
        stalled.Status.Should().NotBe(ActiveCdcRunStatus.Passed);
    }

    [Fact]
    public async Task Replay_CheckpointStall_IsFailedOnlyWhenAttributable_OtherwiseInconclusive()
    {
        // A checkpoints, then nothing moves past A2 or B.
        await using var assumed = new H();
        await assumed.AddSnapshotAsync();
        SinglePartition(assumed, checkpointAfterStart: 11);
        var inconclusive = await RunAsync(assumed);
        (inconclusive.Status, inconclusive.Step(ActiveCdcStepKind.FollowingEventProgression)!.State).Should().Be((ActiveCdcRunStatus.Inconclusive, ActiveCdcEvidenceState.NotObserved),
            "the harness consumer group $Default is a configured assumption");

        await using var confirmed = new H();
        confirmed.Catalog.Integration = confirmed.Catalog.Integration with { ConsumerGroup = "person-adapter" };
        await confirmed.AddSnapshotAsync();
        SinglePartition(confirmed, checkpointAfterStart: 11);
        var failed = await RunAsync(confirmed);
        failed.Status.Should().Be(ActiveCdcRunStatus.Failed);
        failed.StatusReason.Should().Contain("following valid event did not progress");
        AssertBoundaries(failed);
    }

    [Fact]
    public async Task Replay_EarlyMissesAreEventualConsistency_NotFailure()
    {
        await using var h = new H(H.Enabled() with { ObservationSeconds = 10, PollSeconds = 1 });
        await h.AddSnapshotAsync();
        h.Metadata.Script = call => Positions(("0", 10 + Math.Min(call, 3)));
        var reads = 0;
        // Each observation sees the checkpoint lag behind for two polls before it catches up.
        h.Checkpoints.Script = call => call == 0 ? Checkpoints(("0", 9)) : Checkpoints(("0", ++reads % 3 == 0 ? 13 : 9));
        var run = await RunAsync(h);
        run.Status.Should().Be(ActiveCdcRunStatus.Passed);
        reads.Should().BeGreaterThan(3, "early misses were polled again, within the bounded window");
    }

    [Fact]
    public async Task Replay_WithoutProgressionEvidence_IsPartial_NeverPassed()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        h.Metadata.Available = false;
        var run = await RunAsync(h);
        SentLabels(h).Should().Equal(["A", "A2", "B"]);
        run.Status.Should().Be(ActiveCdcRunStatus.Partial);
        run.Step(ActiveCdcStepKind.FollowingEventProgression)!.State.Should().Be(ActiveCdcEvidenceState.Unavailable);

        await using var aggregate = new H();
        await aggregate.AddSnapshotAsync();
        aggregate.Metadata.Script = _ => Positions(("0", 10)); // positions never move: the events cannot be located
        var unlocated = await RunAsync(aggregate);
        (unlocated.Status, unlocated.Step(ActiveCdcStepKind.FollowingEventProgression)!.State).Should().Be((ActiveCdcRunStatus.Partial, ActiveCdcEvidenceState.NotAssessed));

        await using var noStore = new H();
        await noStore.AddSnapshotAsync();
        SinglePartition(noStore);
        noStore.Checkpoints.Available = false;
        (await RunAsync(noStore)).Status.Should().Be(ActiveCdcRunStatus.Partial);
    }

    [Theory]
    [InlineData(0, false, ActiveCdcRunStatus.Failed, 1, "A transport failure")]
    [InlineData(0, true, ActiveCdcRunStatus.Inconclusive, 1, "A2 and B were not sent")]
    [InlineData(1, false, ActiveCdcRunStatus.Failed, 2, "Replay transport failure")]
    [InlineData(1, true, ActiveCdcRunStatus.Inconclusive, 2, "B was not sent")]
    [InlineData(2, false, ActiveCdcRunStatus.Failed, 3, "Control transport failure")]
    [InlineData(2, true, ActiveCdcRunStatus.Inconclusive, 3, "cannot be claimed")]
    public async Task Replay_SendFailures_StopTheSequence_AndAreNeverRetried(int failing, bool ambiguous, ActiveCdcRunStatus expected, int sends, string reason)
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        SinglePartition(h);
        var call = 0;
        h.Producers.Behaviour = (_, _) => call++ == failing
            ? throw new EventHubsException(ambiguous, H.Hub, "x", ambiguous ? EventHubsException.FailureReason.ServiceTimeout : EventHubsException.FailureReason.MessageSizeExceeded)
            : Task.CompletedTask;
        var run = await RunAsync(h);
        (run.Status, h.Producers.Sent.Count).Should().Be((expected, sends));
        run.StatusReason.Should().Contain(reason);
        run.StatusReason.Should().NotContain("stuck");
        run.Step(ActiveCdcStepKind.FollowingEventProgression)!.State.Should().Be(ActiveCdcEvidenceState.NotAssessed);
        AssertBoundaries(run);
    }

    [Fact]
    public async Task Replay_NeedsTwoUnusedKeys_AndEveryPhase1GateStillBlocksBeforeAnySend()
    {
        await using var one = new H(H.Enabled() with { SyntheticPersonPkMin = 900_000_000, SyntheticPersonPkMax = 900_000_000 });
        await one.AddSnapshotAsync();
        var readiness = await one.Service().ReadinessAsync(H.Env, H.IntegrationId, "Development", null, null, Replay);
        readiness.Checks.Single(c => c.Key == "synthetic-key").Should().Match<ActiveCdcReadinessCheck>(c => c.State == ActiveCdcReadinessState.Blocked && c.Detail.Contains("needs 2"));
        (await one.Service().ReadinessAsync(H.Env, H.IntegrationId, "Development", null, null)).Checks.Single(c => c.Key == "synthetic-key").State.Should().Be(ActiveCdcReadinessState.Ready, "Normal Person needs one key");
        (await RunAsync(one)).Should().Match<ActiveCdcRun>(r => r.Status == ActiveCdcRunStatus.Blocked && !r.SendAttempted);
        one.Producers.Created.Should().BeEmpty();

        async Task Blocked(Action<H> arrange, string? type = "Development", bool snapshot = true, string because = "")
        {
            await using var h = new H();
            if (snapshot) await h.AddSnapshotAsync();
            arrange(h);
            var run = await RunAsync(h, type: type);
            run.Status.Should().Be(ActiveCdcRunStatus.Blocked, because);
            h.Producers.Created.Should().BeEmpty(because);
            run.Messages.Should().BeEmpty("no identity is allocated for a blocked run");
        }
        await Blocked(_ => { }, "Production", because: "Production");
        await Blocked(_ => { }, null, because: "unknown environment");
        await Blocked(h => h.Options = h.Options with { SyntheticPersonPkMin = null }, because: "no approved range");
        await Blocked(h => h.Options = h.Options with { AllowedDestinations = [] }, because: "not allowlisted");
        await Blocked(h => h.Options = h.Options with { Enabled = false }, because: "disabled");
        await Blocked(h => h.Azure.Enabled = false, because: "no Azure identity");
        await Blocked(_ => { }, snapshot: false, because: "no source contract");

        await using var stale = new H();
        var old = await stale.AddSnapshotAsync(at: DateTimeOffset.UtcNow.AddHours(-1));
        await stale.AddSnapshotAsync();
        var outdated = await RunAsync(stale, Request(old));
        (outdated.Status, outdated.Manifest.Status).Should().Be((ActiveCdcRunStatus.Blocked, ActiveCdcContractStatus.Outdated));
        stale.Producers.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task Replay_CancelBeforeA_SendsNothing_CancelAfterA_StopsAndSaysItCannotBeUnsent()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        var release = new TaskCompletionSource();
        h.Metadata.Gate = async ct => await release.Task.WaitAsync(ct);
        var service = h.Service();
        var started = await service.StartAsync(Request(), "Development", null);
        (await service.CancelAsync(started.RunId)).Should().BeTrue();
        var before = await h.CompletedAsync(started);
        (before.Status, before.SendAttempted, h.Producers.Sent.Count).Should().Be((ActiveCdcRunStatus.Cancelled, false, 0));

        await using var h2 = new H(H.Enabled() with { ObservationSeconds = 60, PollSeconds = 1 });
        await h2.AddSnapshotAsync();
        SinglePartition(h2, checkpointAfterStart: 9); // A never checkpoints: the run waits in "Observe A"
        var service2 = h2.Service();
        var running = await service2.StartAsync(Request(), "Development", null);
        for (var i = 0; i < 100 && h2.Producers.Sent.IsEmpty; i++) await Task.Delay(50);
        await Task.Delay(300);
        (await service2.CancelAsync(running.RunId)).Should().BeTrue();
        var after = await h2.CompletedAsync(running);
        after.Status.Should().Be(ActiveCdcRunStatus.Cancelled);
        SentLabels(h2).Should().Equal(["A"], "no further message is sent after cancellation");
        after.StatusReason.Should().Contain("A had already been sent and cannot be unsent");
        after.Step(ActiveCdcStepKind.SendReplay)!.State.Should().Be(ActiveCdcEvidenceState.NotAssessed);
    }

    [Fact]
    public async Task SecretSentinel_NeverReachesTheStoredReplayRunOrTheLogs()
    {
        const string sentinel = "SECRET_SENTINEL_REPLAY_123";
        await using var h = new H();
        await h.AddSnapshotAsync();
        SinglePartition(h);
        var call = 0;
        h.Producers.Behaviour = (_, _) => call++ == 1 ? throw new EventHubsException(false, H.Hub, $"SharedAccessKey={sentinel}", EventHubsException.FailureReason.GeneralError) : Task.CompletedTask;
        var run = await RunAsync(h);
        using var db = h.Db();
        string.Join("\n", db.ActiveCdcRuns.Select(r => r.ResultJson)).Should().NotContain(sentinel);
        JsonSerializer.Serialize(run).Should().NotContain(sentinel);
        string.Join("\n", h.Logs.Lines).Should().NotContain(sentinel);
    }

    [Fact]
    public void Boundaries_SendOrCheckpointEvidenceCanNeverPromoteUnobservableDomains()
    {
        var run = new ActiveCdcRun
        {
            Scenario = ActiveCdcScenarioCatalog.SamePersonPkReplay,
            Steps = Enum.GetValues<ActiveCdcStepKind>().Select(k => new ActiveCdcStep { Kind = k, State = ActiveCdcEvidenceState.Observed, Detail = "claimed" }).ToList(),
        };
        var enforced = ActiveCdcRunner.EnforceBoundaries(run);
        AssertBoundaries(enforced);
        enforced.Step(ActiveCdcStepKind.SendA)!.State.Should().Be(ActiveCdcEvidenceState.Observed, "observable stages keep their evidence");
        enforced.Step(ActiveCdcStepKind.ConsumerCheckpoint)!.State.Should().Be(ActiveCdcEvidenceState.Observed);

        // A Normal Person run gets no replay-only steps added.
        var normal = new ActiveCdcRun { Scenario = ActiveCdcScenarioCatalog.NormalPerson, Steps = [new() { Kind = ActiveCdcStepKind.PersonPersisted, State = ActiveCdcEvidenceState.NotAssessed }] };
        ActiveCdcRunner.EnforceBoundaries(normal).Steps.Select(s => s.Kind).Should().Equal([ActiveCdcStepKind.PersonPersisted]);
    }

    [Theory]
    [InlineData(ActiveCdcEvidenceState.Observed, ActiveCdcEvidenceState.Observed, false, ActiveCdcRunStatus.Passed)]
    [InlineData(ActiveCdcEvidenceState.Unavailable, ActiveCdcEvidenceState.Observed, false, ActiveCdcRunStatus.Partial)]
    [InlineData(ActiveCdcEvidenceState.NotAssessed, ActiveCdcEvidenceState.Observed, false, ActiveCdcRunStatus.Partial)]
    [InlineData(ActiveCdcEvidenceState.TimedOut, ActiveCdcEvidenceState.Observed, false, ActiveCdcRunStatus.Failed)]
    [InlineData(ActiveCdcEvidenceState.TimedOut, ActiveCdcEvidenceState.Observed, true, ActiveCdcRunStatus.Inconclusive)]
    [InlineData(ActiveCdcEvidenceState.Observed, ActiveCdcEvidenceState.TimedOut, false, ActiveCdcRunStatus.Failed)]
    public void Progression_Aggregation_IsConservative(ActiveCdcEvidenceState replay, ActiveCdcEvidenceState control, bool assumed, ActiveCdcRunStatus expected)
    {
        ActiveCdcMessageEvidence M(ActiveCdcEvidenceState s) => new() { CheckpointState = s, AdvancedPartitions = new() { ["0"] = 1 } };
        ActiveCdcRunner.EvaluateProgression(M(replay), M(control), assumed).Status.Should().Be(expected);
        // More than one partition moved (other traffic): a stall is not attributable.
        var shared = new ActiveCdcMessageEvidence { CheckpointState = ActiveCdcEvidenceState.TimedOut, AdvancedPartitions = new() { ["0"] = 1, ["1"] = 5 } };
        ActiveCdcRunner.EvaluateProgression(shared, M(ActiveCdcEvidenceState.Observed), false).Status.Should().Be(ActiveCdcRunStatus.Inconclusive);
    }

    [Fact]
    public async Task Manifest_ListsSamePayloadDeveloperTests_ApartFromActiveEvidence_AndNormalPersonRunsAreUnchanged()
    {
        var snapshot = H.Snapshot(ActiveCdcScenarioCatalog.NormalPerson.Fields) with
        {
            Tests = [new DeveloperTestEvidence { Id = "t1", Class = "PersonIngestionTests", Method = "InnmatingPerson_SamePayloadTwice_ProducesExactlyOnePersonRow", Layer = DeveloperTestLayer.Integration },
                new DeveloperTestEvidence { Id = "t2", Class = "DuplicateHandlingTests", Method = "InnmatingPersonAsync_SecondPersonWithSameFoedselsnummer_IsRecordedAsDuplicate", Layer = DeveloperTestLayer.Integration }],
        };
        snapshot = snapshot with { IntegrationPath = snapshot.IntegrationPath! with { Rules = [new PathRuleEvidence { Kind = "Idempotency", DeveloperTestIds = ["t1", "t2"] }] } };
        ActiveCdcContractManifestService.Evaluate(ActiveCdcScenarioCatalog.SamePersonPkReplay, snapshot, snapshot.Id).DeveloperCoverage
            .Should().Equal(["PersonIngestionTests.InnmatingPerson_SamePayloadTwice_ProducesExactlyOnePersonRow (integration)"], "natural-key tests are not same-PersonPK coverage");
        ActiveCdcContractManifestService.Evaluate(ActiveCdcScenarioCatalog.NormalPerson, snapshot, snapshot.Id).DeveloperCoverage.Should().BeEmpty();

        // A stored Phase 1 run is returned exactly as it was recorded.
        await using var h = new H();
        var phase1 = new ActiveCdcRun
        {
            RunId = Guid.NewGuid(), EnvironmentId = H.Env, IntegrationId = H.IntegrationId, Scenario = ActiveCdcScenarioCatalog.NormalPerson, Status = ActiveCdcRunStatus.Partial,
            StartedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow,
            Steps = [new() { Kind = ActiveCdcStepKind.PersonPersisted, State = ActiveCdcEvidenceState.NotAssessed, Detail = "Phase 1" }],
        };
        await h.Store.InsertAsync(phase1, default);
        var stored = JsonSerializer.Serialize(await h.Service().GetAsync(phase1.RunId));
        stored.Should().Be(JsonSerializer.Serialize(phase1));

        await h.AddSnapshotAsync();
        var normal = await RunAsync(h, H.Request());
        (normal.Status, normal.Messages.Count).Should().Be((ActiveCdcRunStatus.Partial, 0));
        normal.Steps.Select(s => s.Kind).Should().NotContain([ActiveCdcStepKind.SendA, ActiveCdcStepKind.DatabaseIdempotency, ActiveCdcStepKind.NaturalKeyDuplicate]);
        h.Producers.Sent.Single().Properties.Should().NotContainKey("BirkNextMessage", "Normal Person events keep their Phase 1 metadata");
    }
}
