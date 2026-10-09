using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Messaging.EventHubs;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using H = BirkNext.Api.Tests.Services.ActiveCdcTests.ActiveCdcTestHarness;

namespace BirkNext.Api.Tests.Services.ActiveCdcTests;

/// <summary>Active CDC tests Phase 1: fixture, backend guards, contract binding, the one send path, run lifecycle, history and leak safety. No Azure.</summary>
public sealed class ActiveCdcTests
{
    private static readonly ActiveCdcDestination Destination = new() { SourceDatabase = "BirkM2LB", SourceSchema = "dbo", SourceTable = "Person" };

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Fixture_IsTheDebeziumEnvelopeThePersonAdapterReads_WithSyntheticValuesOnly()
    {
        var runId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var now = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
        var (evt, summary) = PersonCdcFixtureBuilder.Build(runId, 900_000_001, Destination, now, 8192);
        using var doc = JsonDocument.Parse(evt.Body);
        var payload = doc.RootElement.GetProperty("payload");
        // CdcProcessorWorker.Deserialize: payload.op, payload.source.table, payload.after (non-delete).
        payload.GetProperty("op").GetString().Should().Be("c");
        payload.GetProperty("before").ValueKind.Should().Be(JsonValueKind.Null);
        var source = payload.GetProperty("source");
        (source.GetProperty("db").GetString(), source.GetProperty("schema").GetString(), source.GetProperty("table").GetString()).Should().Be(("BirkM2LB", "dbo", "Person"));
        var after = payload.GetProperty("after");
        after.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(ActiveCdcScenarioCatalog.NormalPerson.Fields, "the field set is closed");
        foreach (var forbidden in new[] { "Fødselsnummer", "Personnummer", "Dufnummer", "Navn", "Sikkerhetsnivå", "BarnPK" })
            after.TryGetProperty(forbidden, out _).Should().BeFalse($"{forbidden} is never sent");
        after.GetProperty("PersonPK").GetInt32().Should().Be(900_000_001);
        after.GetProperty("Fornavn").GetString().Should().Be("BIRKNEXT-TEST-0F8FAD5BD9CB").And.Be(summary.Marker);
        after.GetProperty("Etternavn").GetString().Should().Be("Synthetic");
        after.GetProperty("UsikkerFødselsnummer").GetBoolean().Should().BeTrue("the adapter's derived date component must land in the uncertain field");
        after.GetProperty("UsikkerFødselsdato").GetBoolean().Should().BeFalse("the age filter needs a confirmed birth date");
        after.GetProperty("KjønnTypeFK").GetInt32().Should().Be(3);
        after.GetProperty("EndretDato").GetInt64().Should().Be(now.ToUnixTimeMilliseconds());
        var birth = DateOnly.FromDateTime(DateTime.UnixEpoch.AddDays(after.GetProperty("Født").GetInt32()));
        birth.Should().Be(summary.SyntheticBirthDate);
        summary.AgeYears.Should().BeInRange(1, 25, "AgeFilter discards create events above 25 years");
        Encoding.UTF8.GetString(evt.Body.Span).Should().Contain("\"Født\"", "payload field names keep BiRK's real spelling");
        summary.PayloadSha256.Should().Be(Convert.ToHexString(SHA256.HashData(evt.Body.Span)).ToLowerInvariant());
        summary.PayloadBytes.Should().Be(evt.Body.Length);
        Regex.IsMatch(Encoding.UTF8.GetString(evt.Body.Span), @"\b\d{11}\b").Should().BeFalse("no national-id-like value");
    }

    [Fact]
    public void Fixture_ExpectedPersonId_FollowsTheAdapterDerivation_AndPayloadIsBounded()
    {
        PersonCdcFixtureBuilder.ExpectedPersonId(12345).Should().Be(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("12345"))[..16]));
        PersonCdcFixtureBuilder.ExpectedPersonId(-7).Should().NotBe(PersonCdcFixtureBuilder.ExpectedPersonId(7));
        var build = () => PersonCdcFixtureBuilder.Build(Guid.NewGuid(), 1, Destination, DateTimeOffset.UtcNow, 100);
        build.Should().Throw<InvalidOperationException>().WithMessage("*100-byte bound*");
    }

    // ── Policy ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Production"), InlineData("production"), InlineData(null), InlineData(""), InlineData("Local"), InlineData("Test"), InlineData("RC"), InlineData("Staging")]
    public void Policy_RefusesEverythingButDevelopmentAndQa(string? type)
    {
        ActiveCdcPolicy.EnvironmentBlock(type).Should().NotBeNull();
        new ActiveCdcPolicy(H.Enabled()).Approve(type, H.Fqdn, H.Hub, null).Approved.Should().BeNull();
    }

    [Fact]
    public void Policy_ApprovesOnlyEnrolledNonProductionDestinationsCarryingTheEnvironmentMarker()
    {
        var policy = new ActiveCdcPolicy(H.Enabled() with { AllowedDestinations = [new(H.Fqdn, H.Hub), new("evhns-m2lb-prod-nwe-001.servicebus.windows.net", "m2lb-cdc-prod.Birk.dbo.Person"), new("evhns-m2lb-qa-nwe-001.servicebus.windows.net", "m2lb-cdc-qa.Birk.dbo.Person")] });
        policy.Approve("Development", H.Fqdn, H.Hub, "https://m2lbdev.bufetat.no/").Approved!.EventHub.Should().Be(H.Hub);
        policy.Approve("development", H.Fqdn.ToUpperInvariant(), H.Hub.ToLowerInvariant(), null).Approved.Should().NotBeNull("Azure names are case-insensitive");
        policy.Approve("QA", "evhns-m2lb-qa-nwe-001.servicebus.windows.net", "m2lb-cdc-qa.Birk.dbo.Person", null).Approved.Should().NotBeNull();
        policy.Approve("Development", "evhns-m2lb-prod-nwe-001.servicebus.windows.net", "m2lb-cdc-prod.Birk.dbo.Person", null).Reason.Should().Contain("production marker", "enrollment never overrides the name guard");
        policy.Approve("QA", H.Fqdn, H.Hub, null).Reason.Should().Contain("\"qa\"", "a QA claim against a DEV namespace is refused");
        policy.Approve("Development", H.Fqdn, "m2lb-cdc-dev.BirkM2LB.dbo.Barn", null).Reason.Should().Contain("not enrolled");
        policy.Approve("Development", "evil.example.com", H.Hub, null).Reason.Should().Contain("servicebus.windows.net");
        policy.Approve("Development", H.Fqdn, H.Hub, "https://m2lb.prod.bufetat.no/").Reason.Should().Contain("target application host");
        new ActiveCdcPolicy(H.Enabled() with { Enabled = false }).Approve("Development", H.Fqdn, H.Hub, null).Reason.Should().Contain("disabled");
    }

    [Fact]
    public void Policy_ResolvesOnlyOneExactServerConfiguredTarget_AndFailsClosedOnDuplicates()
    {
        var target = new ActiveCdcOptions.TrustedTargetBinding(H.Env, "Development", "https://m2lb-dev.example.test", "M2LB DEV");
        new ActiveCdcPolicy(H.Enabled() with { TrustedTargets = [target] }).ResolveTrustedTarget(H.Env).Should().Be(target);
        new ActiveCdcPolicy(H.Enabled() with { TrustedTargets = [target] }).ResolveTrustedTarget("other-env").Should().BeNull();
        new ActiveCdcPolicy(H.Enabled() with { TrustedTargets = [target, target with { EnvironmentType = "Production" }] }).ResolveTrustedTarget(H.Env).Should().BeNull();
    }

    [Fact]
    public void Options_HaveNoSecretSetting_AndPersonPkRangeIsRequired()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActiveCdcTests:Enabled"] = "true", ["ActiveCdcTests:ConnectionString"] = $"Endpoint=sb://x/;SharedAccessKey={H.Sentinel}",
            ["ActiveCdcTests:AllowedDestinations:0:NamespaceFqdn"] = H.Fqdn, ["ActiveCdcTests:AllowedDestinations:0:EventHub"] = H.Hub,
        }).Build();
        var options = ActiveCdcOptions.From(config);
        JsonSerializer.Serialize(options).Should().NotContain(H.Sentinel, "a connection string in configuration is never read");
        options.AllowedDestinations.Should().ContainSingle();
        new ActiveCdcPolicy(options).PersonPkRange(out var reason).Should().BeNull();
        reason.Should().Contain("No reserved synthetic PersonPK range");
        new ActiveCdcPolicy(options with { SyntheticPersonPkMin = 10, SyntheticPersonPkMax = 5 }).PersonPkRange(out _).Should().BeNull();
        typeof(ActiveCdcOptions).GetProperties().Select(p => p.Name).Should().NotContain(n => Regex.IsMatch(n, "Secret|Key$|Sas|Connection|Password|Token", RegexOptions.IgnoreCase));
    }

    // ── Source contract binding ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Manifest_BindsToTheSnapshot_AndBlocksMissingFieldsOutdatedAndMissingSnapshots()
    {
        var scenario = ActiveCdcScenarioCatalog.NormalPerson;
        var snapshot = H.Snapshot(scenario.Fields);
        var ok = ActiveCdcContractManifestService.Evaluate(scenario, snapshot, snapshot.Id);
        ok.Status.Should().Be(ActiveCdcContractStatus.Compatible);
        (ok.SourceSnapshotId, ok.ArchiveSha256, ok.ScenarioVersion, ok.FixtureSchemaVersion).Should().Be((snapshot.Id, snapshot.Archive.Sha256, "1", 1));
        ok.Fingerprint.Should().HaveLength(16).And.Be(ActiveCdcContractManifestService.Evaluate(scenario, snapshot, snapshot.Id).Fingerprint, "deterministic");
        var other = H.Snapshot(scenario.Fields);
        ActiveCdcContractManifestService.Evaluate(scenario, other, other.Id).Fingerprint.Should().NotBe(ok.Fingerprint, "a different snapshot is a different binding");

        var missing = ActiveCdcContractManifestService.Evaluate(scenario, H.Snapshot(scenario.Fields.Where(f => f != "Født")), null);
        (missing.Status, missing.MissingFields.Single()).Should().Be((ActiveCdcContractStatus.Incompatible, "Født"));
        ActiveCdcContractManifestService.Evaluate(scenario, snapshot, Guid.NewGuid()).Status.Should().Be(ActiveCdcContractStatus.Outdated);
        ActiveCdcContractManifestService.Evaluate(scenario, null, null).Status.Should().Be(ActiveCdcContractStatus.NoSourceSnapshot);
        ActiveCdcContractManifestService.Evaluate(scenario, snapshot with { IntegrationPath = null, AnalyzerVersion = 1 }, snapshot.Id).Status.Should().Be(ActiveCdcContractStatus.Incompatible);
    }

    // ── Sender ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sender_SendsExactlyOneEventWithMetadata_AndMapsServiceAnswersWithoutRetry()
    {
        await using var h = new H();
        var sender = new AzureEventHubTestSender(h.Azure, h.Producers, Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureEventHubTestSender>.Instance);
        var destination = new ActiveCdcPolicy(H.Enabled()).Approve("Development", H.Fqdn, H.Hub, null).Approved!;
        var (evt, _) = PersonCdcFixtureBuilder.Build(Guid.NewGuid(), 900_000_000, Destination, DateTimeOffset.UtcNow, 8192);
        var activeEvent = M2lbPersonActiveEventAdapter.ToGenerated(evt);

        (await sender.SendAsync(destination, activeEvent, TimeSpan.FromSeconds(5), default)).State.Should().Be(ActiveCdcEvidenceState.Observed);
        h.Producers.Created.Single().Should().Be((H.Fqdn, H.Hub, TimeSpan.FromSeconds(5)));
        var sent = h.Producers.Sent.Single();
        sent.EventBody.ToArray().Should().Equal(evt.Body.ToArray());
        sent.Properties["BirkNextRunId"].Should().Be(evt.RunId.ToString("N"));
        sent.Properties["BirkNextSynthetic"].Should().Be(true);
        sent.MessageId.Should().Be(evt.RunId.ToString("N"));

        async Task<EventHubTestSendOutcome> With(Exception ex) { h.Producers.Behaviour = (_, _) => throw ex; return await sender.SendAsync(destination, activeEvent, TimeSpan.FromSeconds(5), default); }
        (await With(new UnauthorizedAccessException(H.Sentinel))).Should().Match<EventHubTestSendOutcome>(o => o.State == ActiveCdcEvidenceState.NotAuthorized && !o.Ambiguous && o.Detail.Contains("Data Sender"));
        (await With(new EventHubsException(false, H.Hub, H.Sentinel, EventHubsException.FailureReason.ResourceNotFound))).Should().Match<EventHubTestSendOutcome>(o => o.State == ActiveCdcEvidenceState.Error && !o.Ambiguous);
        (await With(new EventHubsException(true, H.Hub, H.Sentinel, EventHubsException.FailureReason.ServiceTimeout))).Should().Match<EventHubTestSendOutcome>(o => o.State == ActiveCdcEvidenceState.TimedOut && o.Ambiguous);
        (await With(new InvalidOperationException(H.Sentinel))).Should().Match<EventHubTestSendOutcome>(o => o.State == ActiveCdcEvidenceState.Error && o.Ambiguous && !o.Detail.Contains(H.Sentinel));
        h.Producers.Behaviour = async (_, ct) => await Task.Delay(TimeSpan.FromSeconds(30), ct);
        (await sender.SendAsync(destination, activeEvent, TimeSpan.FromMilliseconds(200), default)).Should().Match<EventHubTestSendOutcome>(o => o.State == ActiveCdcEvidenceState.TimedOut && o.Ambiguous);
        h.Producers.Sent.Should().HaveCount(6, "every call is exactly one attempt — never a retry");

        h.Azure.Enabled = false;
        var disabled = await sender.SendAsync(destination, activeEvent, TimeSpan.FromSeconds(5), default);
        (disabled.State, h.Producers.Sent.Count).Should().Be((ActiveCdcEvidenceState.NotAuthorized, 6), "no identity → no producer and no fallback secret");
    }

    // ── Run lifecycle ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_AcceptedEvent_IsPartial_WithEachStageRecordedSeparately_AndStoredImmutably()
    {
        await using var h = new H();
        var snapshot = await h.AddSnapshotAsync();
        var service = h.Service();
        var readiness = await service.ReadinessAsync(H.Env, H.IntegrationId, "Development", "https://m2lbdev.bufetat.no/", null);
        readiness.CanRun.Should().BeTrue(string.Join("; ", readiness.Checks.Where(c => c.State == ActiveCdcReadinessState.Blocked).Select(c => c.Detail)));
        readiness.Checks.Single(c => c.Key == "sender-rights").State.Should().Be(ActiveCdcReadinessState.Unknown, "the Data Sender right is never probed");
        readiness.Destination.EventHub.Should().Be(H.Hub, "derived from the configured integration");
        h.Producers.Sent.Should().BeEmpty("readiness never sends");

        var started = await service.StartAsync(H.Request(snapshot), "Development", "https://m2lbdev.bufetat.no/");
        started.Status.Should().Be(ActiveCdcRunStatus.Running);
        started.Step(ActiveCdcStepKind.IntentRecorded)!.State.Should().Be(ActiveCdcEvidenceState.Observed);
        var run = await h.CompletedAsync(started);

        run.Status.Should().Be(ActiveCdcRunStatus.Partial, "Event Hub accepted it, but no Person verification exists");
        run.StatusReason.Should().Contain("Partial");
        run.SendAttempted.Should().BeTrue();
        h.Producers.Sent.Should().ContainSingle();
        run.Fixture!.SyntheticPersonPk.Should().Be(900_000_000);
        run.Manifest.SourceSnapshotId.Should().Be(snapshot.Id);
        var s = run.Steps.ToDictionary(x => x.Kind, x => x.State);
        s[ActiveCdcStepKind.EventHubSend].Should().Be(ActiveCdcEvidenceState.Observed);
        s[ActiveCdcStepKind.PartitionPosition].Should().Be(ActiveCdcEvidenceState.Observed);
        s[ActiveCdcStepKind.ConsumerCheckpoint].Should().Be(ActiveCdcEvidenceState.Observed);
        run.Step(ActiveCdcStepKind.ConsumerCheckpoint)!.Detail.Should().Contain("does not show a stored Person").And.Contain("configured assumption");
        foreach (var kind in new[] { ActiveCdcStepKind.ConsumerTelemetry, ActiveCdcStepKind.PersonPersisted, ActiveCdcStepKind.OutboxCreated, ActiveCdcStepKind.ServiceBusDelivered, ActiveCdcStepKind.SubscriberProcessed })
            s[kind].Should().Be(ActiveCdcEvidenceState.NotAssessed, $"{kind} is not observable in Phase 1");
        run.WhatWasNotAssessed.Should().Contain(l => l.StartsWith("Person persisted — Not assessed")).And.Contain(l => l.Contains("No Service Bus message was sent or received"));
        run.WhatWasTested.Should().Contain(l => l.StartsWith("Event Hub accepted the event"));
        h.Checkpoints.Groups.Should().OnlyContain(g => g == "$Default");

        // Immutable once completed.
        (await h.Store.UpdateAsync(run with { Status = ActiveCdcRunStatus.Passed }, default)).Should().BeFalse();
        (await service.GetAsync(run.RunId))!.Status.Should().Be(ActiveCdcRunStatus.Partial);
        var history = await service.HistoryAsync(H.Env, H.IntegrationId);
        history.Single().Should().Match<ActiveCdcRunSummary>(x => x.RunId == run.RunId && x.SendAttempted && x.ManifestFingerprint == run.Manifest.Fingerprint);

        // The next run allocates the next reserved key.
        var second = await h.CompletedAsync(await h.Service().StartAsync(H.Request(snapshot), "Development", null));
        second.Fixture!.SyntheticPersonPk.Should().Be(900_000_001);
    }

    [Theory]
    [InlineData("Production"), InlineData(null), InlineData("Test")]
    public async Task Run_NonDevQa_IsBlockedInTheBackend_AndNothingIsSent(string? type)
    {
        await using var h = new H();
        h.Options = h.Options with { TrustedTargets = type is null ? [] : [new(H.Env, type, "https://m2lb-dev.example.test", "Test target")] };
        await h.AddSnapshotAsync();
        var run = await h.Service().StartAsync(H.Request(), type, null);
        run.Status.Should().Be(ActiveCdcRunStatus.Blocked);
        run.SendAttempted.Should().BeFalse();
        h.Producers.Created.Should().BeEmpty();
        (await h.Store.GetAsync(run.RunId, default))!.Status.Should().Be(ActiveCdcRunStatus.Blocked, "blocked attempts are part of the history");
        run.WhatWasNotAssessed.Should().ContainSingle(l => l.StartsWith("Nothing was sent"));
    }

    [Fact]
    public async Task Run_UsesServerBoundEnvironmentInsteadOfCallerClassificationOrTargetUrl()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();

        var started = await h.Service().StartAsync(H.Request(), "Production", "https://production.example.test/");
        var completed = await h.CompletedAsync(started);

        completed.EnvironmentType.Should().Be("Development");
        completed.EnvironmentName.Should().Be("M2LB DEV");
        completed.Status.Should().Be(ActiveCdcRunStatus.Partial, "the caller cannot override the backend's trusted development binding");
    }

    [Fact]
    public async Task Run_IsBlocked_ForEachMissingPrerequisite()
    {
        async Task<ActiveCdcRun> Start(Action<H> arrange, bool snapshot = true, ActiveCdcRunRequest? request = null)
        {
            await using var h = new H();
            if (snapshot) await h.AddSnapshotAsync();
            arrange(h);
            var run = await h.Service().StartAsync(request ?? H.Request(), "Development", null);
            h.Producers.Created.Should().BeEmpty();
            return run;
        }
        (await Start(h => h.Options = h.Options with { Enabled = false })).StatusReason.Should().Contain("disabled");
        (await Start(h => h.Options = h.Options with { AllowedDestinations = [] })).StatusReason.Should().Contain("not enrolled");
        (await Start(h => h.Options = h.Options with { SyntheticPersonPkMin = null })).StatusReason.Should().Contain("PersonPK range");
        (await Start(h => h.Azure.Enabled = false)).StatusReason.Should().Contain("Azure is disabled");
        (await Start(_ => { }, snapshot: false)).StatusReason.Should().Contain("No analyzed source snapshot");
        (await Start(h => h.Catalog.Integration = h.Catalog.Integration with { SourceResource = "BirkM2LB.dbo.Barn" })).StatusReason.Should().Contain("not Person");
        (await Start(h => h.Catalog.Integration = h.Catalog.Integration with { Enabled = false })).StatusReason.Should().Contain("disabled");
        (await Start(_ => { }, request: H.Request() with { ConfirmedEventHub = "m2lb-cdc-dev.BirkM2LB.dbo.Barn" })).StatusReason.Should().Contain("confirmed Event Hub differs");
        await using var hx = new H();
        var unconfirmed = () => hx.Service().StartAsync(H.Request() with { ConfirmedSend = false }, "Development", null);
        await unconfirmed.Should().ThrowAsync<ActiveCdcRequestException>();
        var arbitrary = () => hx.Service().StartAsync(H.Request() with { ScenarioId = "raw.json" }, "Development", null);
        await arbitrary.Should().ThrowAsync<ActiveCdcRequestException>().WithMessage("*built-in*");
    }

    [Fact]
    public async Task Run_OutdatedSourceSnapshot_IsBlocked()
    {
        await using var h = new H();
        var old = await h.AddSnapshotAsync(at: DateTimeOffset.UtcNow.AddHours(-1));
        await h.AddSnapshotAsync();
        var run = await h.Service().StartAsync(H.Request(old), "Development", null);
        (run.Status, run.Manifest.Status).Should().Be((ActiveCdcRunStatus.Blocked, ActiveCdcContractStatus.Outdated));
    }

    [Fact]
    public async Task Run_SendOutcomes_MapToBlockedFailedAndInconclusive_WithoutReSend()
    {
        async Task<(ActiveCdcRun Run, int Sends)> With(Func<EventData, CancellationToken, Task> behaviour)
        {
            await using var h = new H();
            await h.AddSnapshotAsync();
            h.Producers.Behaviour = behaviour;
            var run = await h.CompletedAsync(await h.Service().StartAsync(H.Request(), "Development", null));
            return (run, h.Producers.Sent.Count);
        }
        var denied = await With((_, _) => throw new UnauthorizedAccessException());
        (denied.Run.Status, denied.Run.Step(ActiveCdcStepKind.EventHubSend)!.State, denied.Sends).Should().Be((ActiveCdcRunStatus.Blocked, ActiveCdcEvidenceState.NotAuthorized, 1));
        denied.Run.Step(ActiveCdcStepKind.PersonPersisted)!.State.Should().Be(ActiveCdcEvidenceState.NotAssessed);
        var rejected = await With((_, _) => throw new EventHubsException(false, H.Hub, "x", EventHubsException.FailureReason.MessageSizeExceeded));
        rejected.Run.Status.Should().Be(ActiveCdcRunStatus.Failed);
        var unknown = await With((_, _) => throw new EventHubsException(true, H.Hub, "x", EventHubsException.FailureReason.ServiceCommunicationProblem));
        (unknown.Run.Status, unknown.Sends).Should().Be((ActiveCdcRunStatus.Inconclusive, 1), "an ambiguous outcome is never retried");
        unknown.Run.StatusReason.Should().Contain("not re-sent");
    }

    [Fact]
    public async Task Run_UnavailableOrLateEvidence_IsNotAFailure()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        h.Metadata.Available = false;
        var run = await h.CompletedAsync(await h.Service().StartAsync(H.Request(), "Development", null));
        run.Status.Should().Be(ActiveCdcRunStatus.Partial);
        run.Step(ActiveCdcStepKind.PartitionPosition)!.State.Should().Be(ActiveCdcEvidenceState.Unavailable);
        run.Step(ActiveCdcStepKind.ConsumerCheckpoint)!.State.Should().Be(ActiveCdcEvidenceState.NotAssessed, "without the event's partition position a checkpoint proves nothing");

        await using var late = new H();
        await late.AddSnapshotAsync();
        late.Checkpoints.After = late.Checkpoints.Before;
        var lateRun = await late.CompletedAsync(await late.Service().StartAsync(H.Request(), "Development", null));
        (lateRun.Status, lateRun.Step(ActiveCdcStepKind.ConsumerCheckpoint)!.State).Should().Be((ActiveCdcRunStatus.Partial, ActiveCdcEvidenceState.TimedOut));
        lateRun.Step(ActiveCdcStepKind.ConsumerCheckpoint)!.Detail.Should().Contain("not a failure");
    }

    [Fact]
    public async Task Run_OneInFlightPerIntegration_AndCancellationBeforeSendSendsNothing()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        var release = new TaskCompletionSource();
        h.Metadata.Gate = async ct => await release.Task.WaitAsync(ct);
        var service = h.Service();
        var first = await service.StartAsync(H.Request(), "Development", null);
        (await service.ReadinessAsync(H.Env, H.IntegrationId, "Development", null, null)).Should().Match<ActiveCdcReadiness>(r => !r.CanRun && r.RunningRunId == first.RunId);
        var second = await h.Service().StartAsync(H.Request(), "Development", null);
        (second.Status, second.StatusReason).Should().Match<(ActiveCdcRunStatus S, string R)>(x => x.S == ActiveCdcRunStatus.Blocked && x.R.Contains("in flight"));

        (await service.CancelAsync(first.RunId)).Should().BeTrue();
        var cancelled = await h.CompletedAsync(first);
        (cancelled.Status, cancelled.SendAttempted, h.Producers.Sent.Count).Should().Be((ActiveCdcRunStatus.Cancelled, false, 0));
        cancelled.StatusReason.Should().Contain("Nothing was sent");
        (await service.CancelAsync(first.RunId)).Should().BeFalse("a completed run cannot be cancelled");
        (await service.ReadinessAsync(H.Env, H.IntegrationId, "Development", null, null)).RunningRunId.Should().BeNull("the lease is released");
    }

    [Fact]
    public async Task Run_CancelledWhileObserving_KeepsTheAcceptedSend_AndSaysItCannotBeUnsent()
    {
        await using var h = new H(H.Enabled() with { ObservationSeconds = 60, PollSeconds = 1 });
        await h.AddSnapshotAsync();
        h.Checkpoints.After = h.Checkpoints.Before;
        var service = h.Service();
        var started = await service.StartAsync(H.Request(), "Development", null);
        for (var i = 0; i < 100 && h.Producers.Sent.IsEmpty; i++) await Task.Delay(50);
        await Task.Delay(300);
        (await service.CancelAsync(started.RunId)).Should().BeTrue();
        var run = await h.CompletedAsync(started);
        run.Status.Should().Be(ActiveCdcRunStatus.Cancelled);
        run.Step(ActiveCdcStepKind.EventHubSend)!.State.Should().Be(ActiveCdcEvidenceState.Observed);
        run.StatusReason.Should().Contain("does not unsend");
    }

    [Fact]
    public async Task Run_LeftRunningByAStoppedProcess_IsRecoveredAsInconclusive()
    {
        await using var h = new H();
        var orphan = new ActiveCdcRun { RunId = Guid.NewGuid(), EnvironmentId = H.Env, IntegrationId = H.IntegrationId, Status = ActiveCdcRunStatus.Running, SendAttempted = true, StartedAt = DateTimeOffset.UtcNow };
        (await h.Store.InsertAsync(orphan, default)).Should().BeTrue();
        var recovered = await h.Service().GetAsync(orphan.RunId);
        (recovered!.Status, recovered.StatusReason).Should().Match<(ActiveCdcRunStatus S, string R)>(x => x.S == ActiveCdcRunStatus.Inconclusive && x.R.Contains("may have been sent"));
        (await h.Store.GetAsync(orphan.RunId, default))!.Status.Should().Be(ActiveCdcRunStatus.Inconclusive);
    }

    // ── Security ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SecretSentinel_NeverReachesTheStoredRunOrTheLogs()
    {
        await using var h = new H();
        await h.AddSnapshotAsync();
        h.Producers.Behaviour = (_, _) => throw new EventHubsException(false, H.Hub, $"Endpoint=sb://x/;SharedAccessKey={H.Sentinel}", EventHubsException.FailureReason.GeneralError);
        var run = await h.CompletedAsync(await h.Service().StartAsync(H.Request(), "Development", null));
        using var db = h.Db();
        string.Join("\n", db.ActiveCdcRuns.Select(r => r.ResultJson)).Should().NotContain(H.Sentinel).And.NotContain("SharedAccessKey");
        JsonSerializer.Serialize(run).Should().NotContain(H.Sentinel);
        string.Join("\n", h.Logs.Lines).Should().NotContain(H.Sentinel);
        db.ActiveCdcRuns.Select(r => r.ResultJson).Should().OnlyContain(j => !j.Contains("\"payload\"") && !j.Contains("\"after\""), "the payload itself is never stored");
    }

    [Fact]
    public void Architecture_TheSendPathIsSingleAndSpecialized()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "BirkNext.Api"))) root = Path.GetDirectoryName(root)!;
        var api = Path.Combine(root, "BirkNext.Api");
        string Code(string file) => string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("///")));
        var sources = Directory.GetFiles(api, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")).ToList();
        sources.Where(f => Regex.IsMatch(Code(f), @"\bEventHubProducerClient\b|\bEventHubBufferedProducerClient\b")).Select(Path.GetFileName).Should().Equal(["EventHubTestSender.cs"], "one producer in the whole API");
        sources.Where(f => Regex.IsMatch(Code(f), @"new\s+SyntheticCdcEvent\(")).Select(Path.GetFileName).Should().Equal(["PersonCdcFixture.cs"], "only the fixture builder creates a sendable event");
        sources.Where(f => Regex.IsMatch(Code(f), @"new\s+ApprovedCdcDestination\(")).Select(Path.GetFileName).Should().Equal(["ActiveCdcPolicy.cs"], "only policy approves a destination");

        var active = Directory.GetFiles(Path.Combine(api, "Services", "ActiveCdcTests"), "*.cs").Append(Path.Combine(api, "Controllers", "ActiveCdcTestsController.cs")).ToList();
        var forbidden = new Regex(@"\b(ServiceBusSender|ServiceBusReceiver|ServiceBusClient|ServiceBusProcessor|EventProcessorClient|PartitionReceiver|ReadEventsAsync|ReceiveBatchAsync|UpdateCheckpointAsync|BlobClient|BlobLeaseClient|UploadAsync|SetMetadataAsync|DeleteAsync|CreateIfNotExistsAsync|ConnectionString|SharedAccessKey|SharedAccessSignature|AzureNamedKeyCredential|AzureSasCredential|CdcReplay|SqlConnection|ExecuteSqlRaw)\b|HttpMethod\.(Put|Post|Delete|Patch)\b");
        foreach (var file in active) forbidden.Matches(Code(file)).Select(m => m.Value).Should().BeEmpty($"{Path.GetFileName(file)} may only send one Event Hub event and read evidence");
        var controller = Code(Path.Combine(api, "Controllers", "ActiveCdcTestsController.cs"));
        controller.Should().NotMatchRegex(@"\[FromBody\]\s*(string|JsonElement|JsonDocument|byte\[\])", "no endpoint accepts a raw payload");
        Code(Path.Combine(api, "Services", "ActiveCdcTests", "EventHubTestSender.cs")).Should().NotMatchRegex(@"SendAsync\(\s*string", "no generic SendEventHub(string json)");
        File.ReadAllText(Path.Combine(api, "BirkNext.Api.csproj")).Should().NotContain("CdcReplay", "BirkNext does not depend on CdcReplay");
    }
}
