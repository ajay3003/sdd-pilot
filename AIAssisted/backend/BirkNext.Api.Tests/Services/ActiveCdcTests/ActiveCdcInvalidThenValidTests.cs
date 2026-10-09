using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Messaging.EventHubs;
using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using FluentAssertions;
using H = BirkNext.Api.Tests.Services.ActiveCdcTests.ActiveCdcTestHarness;

namespace BirkNext.Api.Tests.Services.ActiveCdcTests;

/// <summary>
/// Invalid Person → valid Person (fault resilience): the reviewed invalid fixture (PersonPK missing), then the Normal Person fixture as a valid
/// control; one attempt each; per-partition consumer continuity; conservative aggregation; nothing about handling or persistence is claimed.
/// </summary>
public sealed class ActiveCdcInvalidThenValidTests
{
    private const string Invalid = ActiveCdcScenarioCatalog.InvalidThenValidId;
    private const string ReviewedArchive = "c850a1b2813bbf6e2a9eba1f311d63ff0332eacf35764cd6b767489ac22dc41e";
    private static readonly ActiveCdcDestination Destination = new() { SourceDatabase = "BirkM2LB", SourceSchema = "dbo", SourceTable = "Person" };

    /// <summary>A snapshot showing PersonMapper deriving PersonId from PersonPK, from the reviewed archive unless another hash is given.</summary>
    private static IqrSourceSnapshot SourceSnapshot(string archive = ReviewedArchive, bool identityDerivation = true, List<DeveloperTestEvidence>? tests = null)
    {
        var baseline = H.Snapshot(ActiveCdcScenarioCatalog.NormalPerson.Fields);
        var mapper = new SourceLocation("PersonAdapter/src/M2LB.PersonBiRKAdapter.Infrastructure/Mapping/PersonMapper.cs", 35);
        var fields = baseline.IntegrationPath!.Fields.Select(f => f.OriginField == "PersonPK" && identityDerivation
            ? f with { Steps = [new(SourceStageKind.AdapterModel, "PersonRecord", "PersonId", FieldTransformation.Derived, "Derived by ToDeterministicGuid", mapper)] } : f).ToList();
        return baseline with { Archive = new("M2LB.zip", archive, 1210), IntegrationPath = baseline.IntegrationPath with { Fields = fields }, Tests = tests ?? [] };
    }

    private static ActiveCdcRunRequest Request(IqrSourceSnapshot? snapshot = null) => H.Request(snapshot) with { ScenarioId = Invalid };
    private static List<PartitionRuntime> Positions(params (string Id, long Seq)[] p) => p.Select(x => new PartitionRuntime(x.Id, x.Seq, DateTimeOffset.UtcNow, false)).ToList();
    private static List<PartitionCheckpoint> Checkpoints(params (string Id, long Seq)[] p) => p.Select(x => new PartitionCheckpoint(x.Id, x.Seq, 0, DateTimeOffset.UtcNow)).ToList();

    /// <summary>One partition: T0 10, after I 11, after V 12.</summary>
    private static void SinglePartition(H h, long checkpointAfterStart = 12)
    {
        h.Metadata.Script = call => Positions(("0", 10 + Math.Min(call, 2)));
        h.Checkpoints.Script = call => call == 0 ? Checkpoints(("0", 9)) : Checkpoints(("0", checkpointAfterStart));
    }

    private static async Task<ActiveCdcRun> RunAsync(H h, ActiveCdcRunRequest? request = null, string? type = "Development")
    {
        var started = await h.Service().StartAsync(request ?? Request(), type, null);
        return started.Status == ActiveCdcRunStatus.Running ? await h.CompletedAsync(started) : started;
    }

    private static async Task<H> ReadyAsync(ActiveCdcOptions? options = null)
    {
        var h = new H(options);
        await h.AddSnapshotAsync(SourceSnapshot());
        return h;
    }

    private static string[] Labels(H h) => h.Producers.Sent.Select(e => e.Properties["BirkNextMessage"].ToString()!).ToArray();

    private static void AssertBoundaries(ActiveCdcRun run)
    {
        foreach (var kind in new[] { ActiveCdcStepKind.InvalidHandledCorrectly, ActiveCdcStepKind.InvalidDiagnostic, ActiveCdcStepKind.FaultQueueOutcome, ActiveCdcStepKind.ConsumerRetry,
                     ActiveCdcStepKind.DatabaseEffects, ActiveCdcStepKind.ValidControlHandled, ActiveCdcStepKind.PersonPersisted, ActiveCdcStepKind.OutboxCreated,
                     ActiveCdcStepKind.ServiceBusDelivered, ActiveCdcStepKind.SubscriberProcessed })
            run.Step(kind)!.State.Should().Be(ActiveCdcEvidenceState.NotAssessed, $"{kind} is never assessed by this scenario");
    }

    [Fact]
    public void Scenario_IsRegisteredAsFaultResilience_WithABoundedMeaning()
    {
        var s = ActiveCdcScenarioCatalog.Find(Invalid)!;
        (s.Id, s.Version, s.Name, s.Category, s.MessageCount).Should().Be((Invalid, "1", "Invalid Person → valid Person", "Fault resilience", 2));
        s.Limitation.Should().Be("This verifies consumer continuity, not correct handling or persistence of either message.");
        s.PassMeaning.Should().Be("A controlled invalid Person CDC event was followed by a valid Person CDC event, and the observable Event Hub consumer advanced beyond the valid control event without becoming stuck.");
        s.PassDoesNotMean.Should().StartWith("This does not prove that the invalid event was rejected for the correct reason or that the valid Person was persisted.")
            .And.Contain("fault record").And.Contain("retry").And.Contain("Service Bus");
        s.InvalidFixture.Should().Be("person.missing-personpk v1");
        s.Name.Should().NotContainAny(["poison", "error handling", "recovery"]);
        ActiveCdcScenarioCatalog.All.Select(x => x.Id).Should().Equal(["person.normal.create", "person.same-personpk-replay", Invalid]);
    }

    [Fact]
    public void InvalidFixture_IsTheNormalPersonEnvelopeWithOnlyPersonPkRemoved()
    {
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var (invalid, summary) = PersonCdcFixtureBuilder.BuildInvalid(runId, Destination, now, 8192, Invalid, "I");
        var (valid, _) = PersonCdcFixtureBuilder.Build(runId, 900_000_000, Destination, now, 8192, Invalid, "V", "-V");
        using var i = JsonDocument.Parse(invalid.Body);
        using var v = JsonDocument.Parse(valid.Body);
        var payload = i.RootElement.GetProperty("payload");
        (payload.GetProperty("op").GetString(), payload.GetProperty("source").GetProperty("table").GetString()).Should().Be(("c", "Person"), "the outer Debezium envelope is valid");
        payload.GetProperty("before").ValueKind.Should().Be(JsonValueKind.Null);
        var after = payload.GetProperty("after");
        after.ValueKind.Should().Be(JsonValueKind.Object);
        after.TryGetProperty("PersonPK", out _).Should().BeFalse("the one invalid condition");
        var invalidFields = after.EnumerateObject().Select(p => p.Name).ToList();
        var validFields = v.RootElement.GetProperty("payload").GetProperty("after").EnumerateObject().Select(p => p.Name).ToList();
        validFields.Except(invalidFields).Should().Equal(["PersonPK"], "no accidental second invalidity");
        invalidFields.Except(validFields).Should().BeEmpty();
        after.GetProperty("KjønnTypeFK").GetInt32().Should().Be(3);
        after.GetProperty("Fornavn").GetString().Should().EndWith("-INVALID");
        foreach (var forbidden in new[] { "Fødselsnummer", "Personnummer", "Dufnummer", "Sikkerhetsnivå", "BarnPK" }) after.TryGetProperty(forbidden, out _).Should().BeFalse();
        Regex.IsMatch(System.Text.Encoding.UTF8.GetString(invalid.Body.Span), @"\b\d{11}\b").Should().BeFalse();
        summary.Fields.Should().NotContain("PersonPK");
        invalid.Label.Should().Be("I");
    }

    [Fact]
    public async Task InvalidThenValid_SendsIThenV_OnceEach_AndPassesOnConsumerContinuityOnly()
    {
        await using var h = await ReadyAsync();
        SinglePartition(h);
        var run = await RunAsync(h);

        Labels(h).Should().Equal(["I", "V"], "strictly I then V, exactly two attempts");
        using (var i = JsonDocument.Parse(h.Producers.Sent.First().EventBody.ToArray()))
            i.RootElement.GetProperty("payload").GetProperty("after").TryGetProperty("PersonPK", out _).Should().BeFalse();
        run.Status.Should().Be(ActiveCdcRunStatus.Passed, run.StatusReason);
        run.StatusReason.Should().Contain("consumer advanced past both positions");
        run.Messages.Select(m => (m.Label, m.SyntheticPersonPk)).Should().Equal([("I", (int?)null), ("V", (int?)900_000_000)]);
        run.Messages[0].InvalidCondition.Should().Contain("PersonPK missing");
        run.Step(ActiveCdcStepKind.ObserveInvalid)!.State.Should().Be(ActiveCdcEvidenceState.Observed);
        run.Step(ActiveCdcStepKind.ObserveValidControl)!.State.Should().Be(ActiveCdcEvidenceState.Observed);
        run.Step(ActiveCdcStepKind.ConsumerContinuity)!.Should().Match<ActiveCdcStep>(s => s.State == ActiveCdcEvidenceState.Observed && s.Detail.Contains("not evidence that I was rejected for the right reason"));
        run.Step(ActiveCdcStepKind.InvalidFixtureReviewed)!.Detail.Should().Contain("PersonMapper.Map returns null").And.Contain("CdcRouter.RouteAsync");
        run.CheckpointSnapshots.Select(c => c.Label).Should().Equal(["T0 — before I", "T1 — after invalid I", "T2 — after valid V"]);
        run.Manifest.InvalidFixtureStatus.Should().Be("Reviewed");
        AssertBoundaries(run);
        run.WhatWasNotAssessed.Should().Contain(t => t.StartsWith("Invalid event rejected for the correct reason — Not assessed"))
            .And.Contain(t => t.StartsWith("Fault queue outcome — Not assessed")).And.Contain(t => t.StartsWith("Person persisted — Not assessed"));
        run.Steps.Should().NotContain(s => s.Detail.Contains("No errors occurred") || s.Detail.Contains("no retry occurred"));

        using var db = h.Db();
        var record = db.ActiveCdcRuns.Single(r => r.Id == run.RunId);
        (record.SyntheticPersonPk, record.SyntheticPersonPkControl).Should().Be((900_000_000, (int?)null), "only V uses a reserved key");
        record.ResultJson.Should().NotContain("\"after\"");
        (await RunAsync(h)).Messages[1].SyntheticPersonPk.Should().Be(900_000_001);
    }

    [Fact]
    public async Task Continuity_IsJudgedPerPartition_AndAnUnlocatedInvalidEventIsNotAssessable()
    {
        await using var h = await ReadyAsync();
        h.Metadata.Script = call => call switch { 0 => Positions(("0", 10), ("1", 40)), 1 => Positions(("0", 11), ("1", 40)), _ => Positions(("0", 11), ("1", 41)) };
        h.Checkpoints.Script = call => call == 0 ? Checkpoints(("0", 9), ("1", 39)) : Checkpoints(("0", 11), ("1", 41));
        var run = await RunAsync(h);
        run.Messages.Select(m => string.Join(",", m.AdvancedPartitions.Keys)).Should().Equal(["0", "1"]);
        run.Status.Should().Be(ActiveCdcRunStatus.Passed);

        // I's partition never shows movement, so I cannot be located; V progressing on its own partition says nothing about I's.
        await using var unlocated = await ReadyAsync();
        unlocated.Metadata.Script = call => call switch { 0 => Positions(("0", 10), ("1", 40)), 1 => Positions(("0", 10), ("1", 40)), _ => Positions(("0", 10), ("1", 41)) };
        unlocated.Checkpoints.Script = call => call == 0 ? Checkpoints(("0", 9), ("1", 39)) : Checkpoints(("0", 10), ("1", 41));
        var partial = await RunAsync(unlocated);
        partial.Step(ActiveCdcStepKind.ObserveInvalid)!.State.Should().Be(ActiveCdcEvidenceState.NotAssessed);
        partial.Step(ActiveCdcStepKind.ObserveValidControl)!.State.Should().Be(ActiveCdcEvidenceState.Observed);
        (partial.Status, partial.Step(ActiveCdcStepKind.ConsumerContinuity)!.State).Should().Be((ActiveCdcRunStatus.Partial, ActiveCdcEvidenceState.NotAssessed));
        partial.Step(ActiveCdcStepKind.ConsumerContinuity)!.Detail.Should().Contain("invalid event's partition could not be located");
    }

    [Fact]
    public async Task Stall_IsFailedOnlyWhenAttributable_TheDefaultAssumptionIsInconclusive_AndMissingEvidenceIsPartial()
    {
        await using var assumed = await ReadyAsync();
        SinglePartition(assumed, checkpointAfterStart: 11); // passes I, never V
        var inconclusive = await RunAsync(assumed);
        (inconclusive.Status, inconclusive.Step(ActiveCdcStepKind.ConsumerContinuity)!.State).Should().Be((ActiveCdcRunStatus.Inconclusive, ActiveCdcEvidenceState.NotObserved),
            "$Default is a configured assumption: absence of movement cannot prove failure");

        await using var confirmed = new H();
        confirmed.Catalog.Integration = confirmed.Catalog.Integration with { ConsumerGroup = "person-adapter" };
        await confirmed.AddSnapshotAsync(SourceSnapshot());
        SinglePartition(confirmed, checkpointAfterStart: 11);
        var failed = await RunAsync(confirmed);
        failed.Status.Should().Be(ActiveCdcRunStatus.Failed);
        failed.StatusReason.Should().Contain("did not advance past the valid control position");
        AssertBoundaries(failed);

        await using var noStore = await ReadyAsync();
        SinglePartition(noStore);
        noStore.Checkpoints.Available = false;
        (await RunAsync(noStore)).Status.Should().Be(ActiveCdcRunStatus.Partial);
    }

    [Theory]
    [InlineData(0, false, ActiveCdcRunStatus.Failed, 1, "never entered the consumer path")]
    [InlineData(0, true, ActiveCdcRunStatus.Inconclusive, 1, "valid control was not sent")]
    [InlineData(1, false, ActiveCdcRunStatus.Failed, 2, "Control transport failure")]
    [InlineData(1, true, ActiveCdcRunStatus.Inconclusive, 2, "cannot be claimed")]
    public async Task SendFailures_StopTheSequence_AndAreNeverRetried(int failing, bool ambiguous, ActiveCdcRunStatus expected, int sends, string reason)
    {
        await using var h = await ReadyAsync();
        SinglePartition(h);
        var call = 0;
        h.Producers.Behaviour = (_, _) => call++ == failing
            ? throw new EventHubsException(ambiguous, H.Hub, "x", ambiguous ? EventHubsException.FailureReason.ServiceTimeout : EventHubsException.FailureReason.MessageSizeExceeded)
            : Task.CompletedTask;
        var run = await RunAsync(h);
        (run.Status, h.Producers.Sent.Count).Should().Be((expected, sends));
        run.StatusReason.Should().Contain(reason).And.NotContain("stuck");
        run.Step(ActiveCdcStepKind.ConsumerContinuity)!.State.Should().Be(ActiveCdcEvidenceState.NotAssessed);
        AssertBoundaries(run);
    }

    [Fact]
    public async Task EveryGate_BlocksBeforeAllocationOrSend_IncludingAnUnreviewedInvalidFixture()
    {
        async Task Blocked(Action<H> arrange, string? type = "Development", IqrSourceSnapshot? snapshot = null, string because = "")
        {
            await using var h = new H();
            h.Options = h.Options with { TrustedTargets = type is null ? [] : [new(H.Env, type, "https://m2lb-dev.example.test", "Test target")] };
            await h.AddSnapshotAsync(snapshot ?? SourceSnapshot());
            arrange(h);
            var run = await RunAsync(h, type: type);
            run.Status.Should().Be(ActiveCdcRunStatus.Blocked, because);
            h.Producers.Created.Should().BeEmpty(because);
            run.Messages.Should().BeEmpty(because);
        }
        await Blocked(_ => { }, "Production", because: "Production");
        await Blocked(_ => { }, null, because: "unknown environment");
        await Blocked(h => h.Options = h.Options with { SyntheticPersonPkMin = null }, because: "no approved range");
        await Blocked(h => h.Options = h.Options with { AllowedDestinations = [] }, because: "not allowlisted");
        await Blocked(h => h.Azure.Enabled = false, because: "no Azure identity");
        await Blocked(_ => { }, snapshot: SourceSnapshot(archive: new string('b', 64)), because: "invalid fixture not reviewed for this archive");
        await Blocked(_ => { }, snapshot: SourceSnapshot(identityDerivation: false), because: "source does not show PersonPK deriving the identity");

        await using var unreviewed = new H();
        await unreviewed.AddSnapshotAsync(SourceSnapshot(archive: new string('b', 64)));
        var readiness = await unreviewed.Service().ReadinessAsync(H.Env, H.IntegrationId, "Development", null, null, Invalid);
        readiness.Checks.Single(c => c.Key == "invalid-fixture").Should().Match<ActiveCdcReadinessCheck>(c => c.State == ActiveCdcReadinessState.Blocked && c.Detail.StartsWith("Needs review"));
        readiness.Manifest.Status.Should().Be(ActiveCdcContractStatus.Compatible, "the Person contract itself is fine; only the invalid fixture needs review");

        await using var rereviewed = new H(H.Enabled() with { InvalidFixtureReviewedArchives = [new string('b', 64)] });
        await rereviewed.AddSnapshotAsync(SourceSnapshot(archive: new string('b', 64)));
        (await rereviewed.Service().ReadinessAsync(H.Env, H.IntegrationId, "Development", null, null, Invalid)).Checks.Single(c => c.Key == "invalid-fixture").State
            .Should().Be(ActiveCdcReadinessState.Ready, "an operator can add a re-reviewed archive hash");

        await using var stale = new H();
        var old = await stale.AddSnapshotAsync(SourceSnapshot(), DateTimeOffset.UtcNow.AddHours(-1));
        await stale.AddSnapshotAsync(SourceSnapshot());
        var outdated = await RunAsync(stale, Request(old));
        (outdated.Status, outdated.Manifest.Status).Should().Be((ActiveCdcRunStatus.Blocked, ActiveCdcContractStatus.Outdated));

        await using var oneKey = await ReadyAsync(H.Enabled() with { SyntheticPersonPkMin = 900_000_000, SyntheticPersonPkMax = 900_000_000 });
        (await oneKey.Service().ReadinessAsync(H.Env, H.IntegrationId, "Development", null, null, Invalid)).Checks.Single(c => c.Key == "synthetic-key").State
            .Should().Be(ActiveCdcReadinessState.Ready, "only the valid control needs a key");
    }

    [Fact]
    public async Task CancelAfterI_StopsBeforeV_AndSaysItCannotBeUnsent()
    {
        await using var h = await ReadyAsync(H.Enabled() with { ObservationSeconds = 60, PollSeconds = 1 });
        SinglePartition(h, checkpointAfterStart: 9);
        var service = h.Service();
        var started = await service.StartAsync(Request(), "Development", null);
        for (var i = 0; i < 100 && h.Producers.Sent.IsEmpty; i++) await Task.Delay(50);
        await Task.Delay(300);
        (await service.CancelAsync(started.RunId)).Should().BeTrue();
        var run = await h.CompletedAsync(started);
        run.Status.Should().Be(ActiveCdcRunStatus.Cancelled);
        Labels(h).Should().Equal(["I"]);
        run.StatusReason.Should().Contain("I had already been sent and cannot be unsent");
    }

    [Fact]
    public async Task SecretSentinel_NeverReachesTheStoredRunOrTheLogs()
    {
        const string sentinel = "SECRET_SENTINEL_INVALID_123";
        await using var h = await ReadyAsync();
        SinglePartition(h);
        h.Producers.Behaviour = (_, _) => throw new EventHubsException(false, H.Hub, $"SharedAccessKey={sentinel}", EventHubsException.FailureReason.GeneralError);
        var run = await RunAsync(h);
        using var db = h.Db();
        string.Join("\n", db.ActiveCdcRuns.Select(r => r.ResultJson)).Should().NotContain(sentinel);
        JsonSerializer.Serialize(run).Should().NotContain(sentinel);
        string.Join("\n", h.Logs.Lines).Should().NotContain(sentinel);
    }

    [Fact]
    public void NoOverclaim_SendAndCheckpointEvidenceCannotPromoteHandlingPersistenceFaultQueueOrServiceBus()
    {
        var run = new ActiveCdcRun
        {
            Scenario = ActiveCdcScenarioCatalog.InvalidThenValid,
            Steps = Enum.GetValues<ActiveCdcStepKind>().Select(k => new ActiveCdcStep { Kind = k, State = ActiveCdcEvidenceState.Observed, Detail = "claimed" }).ToList(),
        };
        var enforced = ActiveCdcRunner.EnforceBoundaries(run);
        AssertBoundaries(enforced);
        enforced.Step(ActiveCdcStepKind.InvalidHandledCorrectly)!.Detail.Should().NotBe("claimed", "a forced step carries the canonical Not assessed text");
        enforced.Step(ActiveCdcStepKind.ConsumerContinuity)!.State.Should().Be(ActiveCdcEvidenceState.Observed, "continuity is the one thing this scenario can observe");

        ActiveCdcMessageEvidence M(ActiveCdcEvidenceState s) => new() { CheckpointState = s, AdvancedPartitions = new() { ["0"] = 1 } };
        ActiveCdcRunner.EvaluateContinuity(M(ActiveCdcEvidenceState.Observed), M(ActiveCdcEvidenceState.Observed), true).Status.Should().Be(ActiveCdcRunStatus.Passed);
        ActiveCdcRunner.EvaluateContinuity(M(ActiveCdcEvidenceState.TimedOut), M(ActiveCdcEvidenceState.Observed), true).Status.Should().Be(ActiveCdcRunStatus.Inconclusive);
        ActiveCdcRunner.EvaluateContinuity(M(ActiveCdcEvidenceState.Observed), M(ActiveCdcEvidenceState.NotAssessed), false).Status.Should().Be(ActiveCdcRunStatus.Partial);
    }

    [Fact]
    public void Manifest_BindsTheReviewedFixture_AndListsNearbyDeveloperTestsApart()
    {
        var tests = new List<DeveloperTestEvidence>
        {
            new() { Id = "1", Class = "CdcRouterTests", Method = "RouteAsync_BarnNHjemmstedskommune_MapperReturnsNull_Discards", Layer = DeveloperTestLayer.Unit },
            new() { Id = "2", Class = "PersonProcessingTests", Method = "PersonDelete_IsDiscarded_NoHttpCall_CheckpointStillAdvances", Layer = DeveloperTestLayer.Integration },
            new() { Id = "3", Class = "PersonMapperTests", Method = "Map_SetsEksternIdFromPersonPK", Layer = DeveloperTestLayer.Unit },
        };
        var snapshot = SourceSnapshot(tests: tests);
        var manifest = ActiveCdcContractManifestService.Evaluate(ActiveCdcScenarioCatalog.InvalidThenValid, snapshot, snapshot.Id);
        (manifest.Status, manifest.InvalidFixture, manifest.InvalidFixtureStatus).Should().Be((ActiveCdcContractStatus.Compatible, "person.missing-personpk v1", "Reviewed"));
        manifest.DeveloperCoverage.Should().Equal(["CdcRouterTests.RouteAsync_BarnNHjemmstedskommune_MapperReturnsNull_Discards (unit, related)",
            "PersonProcessingTests.PersonDelete_IsDiscarded_NoHttpCall_CheckpointStillAdvances (integration, related)"]);
        var normal = ActiveCdcContractManifestService.Evaluate(ActiveCdcScenarioCatalog.NormalPerson, snapshot, snapshot.Id);
        (normal.InvalidFixture, normal.Fingerprint).Should().Be(("", ActiveCdcContractManifestService.Evaluate(ActiveCdcScenarioCatalog.NormalPerson, snapshot, snapshot.Id).Fingerprint));
        manifest.Fingerprint.Should().NotBe(normal.Fingerprint);
    }
}
