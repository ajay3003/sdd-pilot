using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveCdcTests;

public sealed class ActiveCdcRequestException(string message) : Exception(message);

public interface IActiveCdcTestService
{
    Task<ActiveCdcReadiness> ReadinessAsync(string environmentId, string integrationId, string? environmentType, string? targetUrl, Guid? snapshotId, CancellationToken ct = default) =>
        ReadinessAsync(environmentId, integrationId, environmentType, targetUrl, snapshotId, ActiveCdcScenarioCatalog.NormalPersonId, ct);
    Task<ActiveCdcReadiness> ReadinessAsync(string environmentId, string integrationId, string? environmentType, string? targetUrl, Guid? snapshotId, string? scenarioId, CancellationToken ct = default);
    Task<ActiveCdcRun> StartAsync(ActiveCdcRunRequest request, string? environmentType, string? targetUrl, CancellationToken ct = default);
    Task<ActiveCdcRun?> GetAsync(Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<ActiveCdcRunSummary>> HistoryAsync(string environmentId, string? integrationId, CancellationToken ct = default);
    Task<bool> CancelAsync(Guid runId, CancellationToken ct = default);
}

/// <summary>
/// Active CDC tests: readiness (nothing contacted), start (every gate re-evaluated in the backend, durable intent written before the send,
/// one run per integration), cancellation, and immutable history. The send itself happens in <see cref="ActiveCdcRunner"/>.
/// </summary>
public sealed class ActiveCdcTestService(IIntegrationCatalogService catalog, IqrSourceStore sources, ActiveCdcPolicy policy, IEventHubTestSender sender,
    ICheckpointEvidenceSource checkpoints, ActiveCdcRunStore store, ActiveCdcRunCoordinator coordinator, ActiveCdcRunner runner, TimeProvider clock) : IActiveCdcTestService
{
    private sealed record Preparation(ActiveCdcScenario Scenario, IntegrationDefinition? Integration, IntegrationPlatform? Platform, ActiveCdcDestination Destination,
        ApprovedCdcDestination? Approved, ActiveCdcContractManifest Manifest, List<ActiveCdcReadinessCheck> Checks, bool CanRun, Guid? RunningRunId);

    public async Task<ActiveCdcReadiness> ReadinessAsync(string environmentId, string integrationId, string? environmentType, string? targetUrl, Guid? snapshotId, string? scenarioId, CancellationToken ct = default)
    {
        var scenario = ActiveCdcScenarioCatalog.Find(scenarioId ?? ActiveCdcScenarioCatalog.NormalPersonId) ?? throw new ActiveCdcRequestException("Unknown scenario. Only built-in reviewed scenarios can run.");
        var p = await PrepareAsync(scenario, environmentId, integrationId, environmentType, targetUrl, snapshotId, ct);
        return new ActiveCdcReadiness
        {
            EnvironmentId = environmentId, IntegrationId = integrationId, Scenario = p.Scenario, Manifest = p.Manifest, Destination = p.Destination,
            Checks = p.Checks, CanRun = p.CanRun, RunningRunId = p.RunningRunId,
            ObservationSeconds = policy.Options.ObservationSeconds, SendTimeoutSeconds = policy.Options.SendTimeoutSeconds,
        };
    }

    public async Task<ActiveCdcRun> StartAsync(ActiveCdcRunRequest request, string? environmentType, string? targetUrl, CancellationToken ct = default)
    {
        var scenario = ActiveCdcScenarioCatalog.Find(request.ScenarioId) ?? throw new ActiveCdcRequestException("Unknown scenario. Only built-in reviewed scenarios can run.");
        if (!request.ConfirmedSend) throw new ActiveCdcRequestException("Confirm that one synthetic event is sent to the named non-production Event Hub.");
        if (string.IsNullOrWhiteSpace(request.EnvironmentId) || string.IsNullOrWhiteSpace(request.IntegrationId)) throw new ActiveCdcRequestException("Environment and integration are required.");
        var p = await PrepareAsync(scenario, request.EnvironmentId, request.IntegrationId, environmentType, targetUrl, request.SourceSnapshotId, ct);
        var run = new ActiveCdcRun
        {
            RunId = Guid.NewGuid(), EnvironmentId = request.EnvironmentId, EnvironmentName = request.EnvironmentName, EnvironmentType = environmentType?.Trim() ?? "Unknown",
            IntegrationId = request.IntegrationId, IntegrationName = p.Integration?.DisplayName ?? request.IntegrationId, Scenario = scenario, Manifest = p.Manifest,
            Destination = p.Destination, StartedAt = clock.GetUtcNow(),
        };
        var blocked = p.Checks.Where(c => c.State == ActiveCdcReadinessState.Blocked).Select(c => $"{c.Label}: {c.Detail}").ToList();
        if (!string.Equals(request.ConfirmedEventHub?.Trim(), p.Destination.EventHub, StringComparison.Ordinal))
            blocked.Add("Destination: the confirmed Event Hub differs from the one the backend resolved — confirm again.");
        if (blocked.Count > 0 || p.Approved is null || p.Platform is null) return await BlockedAsync(run, p, blocked.Count > 0 ? blocked : ["Destination is not approved."], ct);

        var key = ActiveCdcRunCoordinator.Key(request.EnvironmentId, request.IntegrationId);
        if (!coordinator.TryAcquire(key, run.RunId))
            return await BlockedAsync(run, p, ["Another active CDC run for this integration is in flight. One run per integration at a time."], ct);
        var launched = false;
        try
        {
            if (policy.PersonPkRange(out var rangeReason) is not { } range) return await BlockedAsync(run, p, [rangeReason], ct);
            var next = (await store.MaxPersonPkAsync(request.EnvironmentId, range.Min, range.Max, ct) is { } used ? used + 1 : range.Min);
            if (next < range.Min || (long)next + KeysNeeded(scenario) - 1 > range.Max)
                return await BlockedAsync(run, p, [$"The reserved synthetic PersonPK range {range.Min}–{range.Max} has fewer than {KeysNeeded(scenario)} unused key(s) left."], ct);

            List<SyntheticCdcEvent> events;
            try { (events, run) = BuildFixtures(scenario, run, next, p.Destination); }
            catch (InvalidOperationException ex) { return await BlockedAsync(run, p, [ex.Message], ct); }

            var now = clock.GetUtcNow();
            run = run with
            {
                Steps =
                [
                    Step(ActiveCdcStepKind.EnvironmentGuard, ActiveCdcEvidenceState.Observed, $"{run.EnvironmentType} is allowed for active CDC tests.", "Backend policy", now),
                    Step(ActiveCdcStepKind.Destination, ActiveCdcEvidenceState.Observed, $"{p.Destination.EventHub} on {p.Destination.NamespaceFqdn}. {p.Destination.Detail}", "Integration catalog + backend enrollment", now),
                    Step(ActiveCdcStepKind.SourceContract, ActiveCdcEvidenceState.Observed, $"{p.Manifest.Detail} Manifest {p.Manifest.Fingerprint}.", "Bound source snapshot", now),
                    .. scenario.MessageCount > 1
                        ? new[] { Step(ActiveCdcStepKind.IdentitiesAllocated, ActiveCdcEvidenceState.Observed, scenario.Id == ActiveCdcScenarioCatalog.InvalidThenValidId
                            ? $"Valid control V = PersonPK {run.Messages[^1].SyntheticPersonPk}, inside the reserved range {range.Min}–{range.Max} and unused. The invalid event I carries no PersonPK, so no key is allocated for it."
                            : $"X = {run.Messages[0].SyntheticPersonPk} (A and A2), Y = {run.Messages[^1].SyntheticPersonPk} (B); both inside the reserved range {range.Min}–{range.Max} and unused.", "Reserved synthetic PersonPK range", now) }
                        : [],
                    .. scenario.Id == ActiveCdcScenarioCatalog.InvalidThenValidId
                        ? new[] { Step(ActiveCdcStepKind.InvalidFixtureReviewed, ActiveCdcEvidenceState.Observed, $"{scenario.InvalidFixture}: {scenario.InvalidCondition} {p.Manifest.InvalidFixtureDetail}", "Reviewed fixture bound to the source archive", now) }
                        : [],
                    Step(ActiveCdcStepKind.FixtureGenerated, ActiveCdcEvidenceState.Observed, scenario.MessageCount > 1
                        ? string.Join(" ", run.Messages.Select(m => $"{m.Label}: {(m.SyntheticPersonPk is { } pk ? $"PersonPK {pk}" : "no PersonPK (by design)")}, marker {m.Marker}, {m.PayloadBytes} bytes, SHA-256 {m.PayloadSha256[..16]}…."))
                        : $"Synthetic PersonPK {run.Fixture!.SyntheticPersonPk}, marker {run.Fixture.Marker}, {run.Fixture.PayloadBytes} bytes, SHA-256 {run.Fixture.PayloadSha256[..16]}….", "BirkNext fixture builder", now),
                    .. scenario.Id == ActiveCdcScenarioCatalog.SamePersonPkReplayId
                        ? new[] { ReplayEquivalence(events, now) }
                        : [],
                ],
            };
            if (run.Step(ActiveCdcStepKind.ReplayEquivalence) is { State: not ActiveCdcEvidenceState.Observed })
                return await BlockedAsync(run, p, ["A2 is not an exact replay of A, so nothing was sent."], ct);
            // Durable intent BEFORE any send: if this cannot be written, nothing is sent.
            var intent = run with { Steps = [.. run.Steps, Step(ActiveCdcStepKind.IntentRecorded, ActiveCdcEvidenceState.Observed, "Run recorded as Running before the send.", "BirkNext run history", now)] };
            if (!await store.InsertAsync(intent, ct))
                return Finish(run, ActiveCdcRunStatus.Blocked, "The run could not be recorded before sending, so nothing was sent.", stored: false);
            coordinator.Launch(intent.RunId, key, token => runner.ExecuteAsync(intent, p.Platform, environmentType, targetUrl, events, token));
            launched = true;
            return intent;
        }
        finally
        {
            if (!launched) coordinator.Release(key, run.RunId);
        }
    }

    public async Task<ActiveCdcRun?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await store.GetAsync(runId, ct);
        return run is { Status: ActiveCdcRunStatus.Running } && !coordinator.IsRunning(runId) ? await RecoverAsync(run, ct) : run;
    }

    public async Task<IReadOnlyList<ActiveCdcRunSummary>> HistoryAsync(string environmentId, string? integrationId, CancellationToken ct = default)
    {
        var runs = new List<ActiveCdcRun>();
        foreach (var run in await store.ListAsync(environmentId, integrationId, 20, ct))
            runs.Add(run is { Status: ActiveCdcRunStatus.Running } && !coordinator.IsRunning(run.RunId) ? await RecoverAsync(run, ct) : run);
        return runs.Select(r => new ActiveCdcRunSummary(r.RunId, r.IntegrationId, r.Scenario.Id, r.Status, r.StartedAt, r.CompletedAt, r.SendAttempted, r.Manifest.Fingerprint)).ToList();
    }

    public Task<bool> CancelAsync(Guid runId, CancellationToken ct = default) => Task.FromResult(coordinator.Cancel(runId));

    /// <summary>A Running row whose process is gone: completed as Inconclusive — whether the event was sent is exactly what is unknown.</summary>
    private async Task<ActiveCdcRun> RecoverAsync(ActiveCdcRun run, CancellationToken ct)
    {
        var recovered = run with
        {
            Status = ActiveCdcRunStatus.Inconclusive, CompletedAt = clock.GetUtcNow(),
            StatusReason = run.SendAttempted ? "Interrupted after the send was requested (BirkNext stopped). The event may have been sent; it was not re-sent."
                : "Interrupted before the send (BirkNext stopped). Nothing was sent by this run.",
            WhatWasNotAssessed = [.. run.WhatWasNotAssessed, "Run interrupted — no evidence was captured after the interruption."],
        };
        return await store.UpdateAsync(recovered, ct) ? recovered : await store.GetAsync(run.RunId, ct) ?? recovered;
    }

    private async Task<ActiveCdcRun> BlockedAsync(ActiveCdcRun run, Preparation p, List<string> reasons, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var blocked = Finish(run with
        {
            Steps = p.Checks.Where(c => c.State == ActiveCdcReadinessState.Blocked).Select(c => Step(KindOf(c.Key), ActiveCdcEvidenceState.NotObserved, $"{c.Label}: {c.Detail}", "Backend readiness", now)).ToList(),
        }, ActiveCdcRunStatus.Blocked, string.Join(" ", reasons), stored: true);
        return await store.InsertAsync(blocked, ct) ? blocked : blocked with { Limitations = [.. blocked.Limitations, "This blocked attempt could not be saved to history."] };
    }

    private ActiveCdcRun Finish(ActiveCdcRun run, ActiveCdcRunStatus status, string reason, bool stored) => run with
    {
        Status = status, StatusReason = reason, CompletedAt = clock.GetUtcNow(),
        WhatWasTested = ["Backend readiness gates (environment, destination enrollment, source contract, synthetic key range, identity)"],
        WhatWasNotAssessed = ["Nothing was sent: no Event Hub, checkpoint, telemetry, Person, outbox or Service Bus evidence was collected."],
        Limitations = stored ? [] : ["This run could not be saved to history."],
    };

    /// <summary>Distinct reserved keys a run allocates: replay needs X and Y; Normal Person and invalid → valid need one (the invalid event has none).</summary>
    private static int KeysNeeded(ActiveCdcScenario scenario) => scenario.Id == ActiveCdcScenarioCatalog.SamePersonPkReplayId ? 2 : 1;

    private static ActiveCdcStep ReplayEquivalence(IReadOnlyList<SyntheticCdcEvent> events, DateTimeOffset now) => PersonCdcFixtureBuilder.IsExactReplay(events[0], events[1])
        ? Step(ActiveCdcStepKind.ReplayEquivalence, ActiveCdcEvidenceState.Observed, "A2 body is byte-identical to A (same PersonPK, fields, operation and timestamps); only the transport label differs.", "BirkNext fixture builder", now)
        : Step(ActiveCdcStepKind.ReplayEquivalence, ActiveCdcEvidenceState.Error, "A2 differs from A — not a replay.", "BirkNext fixture builder", now);

    /// <summary>Normal Person: one fixture. Same PersonPK replay: A (X), A2 = byte-identical replay of A, B (Y = X + 1, its own marker).</summary>
    private (List<SyntheticCdcEvent> Events, ActiveCdcRun Run) BuildFixtures(ActiveCdcScenario scenario, ActiveCdcRun run, int x, ActiveCdcDestination destination)
    {
        var now = clock.GetUtcNow();
        if (scenario.MessageCount == 1)
        {
            var single = PersonCdcFixtureBuilder.Build(run.RunId, x, destination, now, policy.Options.MaxPayloadBytes);
            return ([single.Event], run with { Fixture = single.Summary });
        }
        if (scenario.Id == ActiveCdcScenarioCatalog.InvalidThenValidId)
        {
            // I: the reviewed invalid fixture (PersonPK omitted, nothing else changed). V: the Normal Person fixture with one reserved key.
            var invalid = PersonCdcFixtureBuilder.BuildInvalid(run.RunId, destination, now, policy.Options.MaxPayloadBytes, scenario.Id, "I");
            var valid = PersonCdcFixtureBuilder.Build(run.RunId, x, destination, now, policy.Options.MaxPayloadBytes, scenario.Id, "V", "-V");
            return ([invalid.Event, valid.Event], run with
            {
                Fixture = valid.Summary,
                Messages =
                [
                    new() { Label = "I", Role = "Controlled invalid Person CDC", SyntheticPersonPk = null, Marker = invalid.Summary.Marker, PayloadSha256 = invalid.Summary.PayloadSha256,
                        PayloadBytes = invalid.Summary.PayloadBytes, InvalidCondition = scenario.InvalidCondition },
                    new() { Label = "V", Role = "Valid synthetic Person control", SyntheticPersonPk = valid.Summary.SyntheticPersonPk, ExpectedPersonId = valid.Summary.ExpectedPersonId,
                        Marker = valid.Summary.Marker, PayloadSha256 = valid.Summary.PayloadSha256, PayloadBytes = valid.Summary.PayloadBytes },
                ],
            });
        }
        var a = PersonCdcFixtureBuilder.Build(run.RunId, x, destination, now, policy.Options.MaxPayloadBytes, scenario.Id, "A", "");
        var a2 = PersonCdcFixtureBuilder.Replay(a.Event, "A2");
        var b = PersonCdcFixtureBuilder.Build(run.RunId, x + 1, destination, now, policy.Options.MaxPayloadBytes, scenario.Id, "B", "-B");
        static ActiveCdcMessageEvidence Message(string label, string role, ActiveCdcFixtureSummary f) => new()
        {
            Label = label, Role = role, SyntheticPersonPk = f.SyntheticPersonPk, ExpectedPersonId = f.ExpectedPersonId, Marker = f.Marker, PayloadSha256 = f.PayloadSha256, PayloadBytes = f.PayloadBytes,
        };
        return ([a.Event, a2, b.Event], run with
        {
            Fixture = a.Summary,
            Messages = [Message("A", "First create (PersonPK X)", a.Summary), Message("A2", "Exact replay of A (same PersonPK X)", a.Summary), Message("B", "Following valid control (different PersonPK Y)", b.Summary)],
        });
    }

    private static ActiveCdcStepKind KindOf(string key) => key switch
    {
        "environment" => ActiveCdcStepKind.EnvironmentGuard,
        "contract" => ActiveCdcStepKind.SourceContract,
        "synthetic-key" => ActiveCdcStepKind.FixtureGenerated,
        _ => ActiveCdcStepKind.Destination,
    };

    internal static ActiveCdcStep Step(ActiveCdcStepKind kind, ActiveCdcEvidenceState state, string detail, string source, DateTimeOffset? at) =>
        new() { Kind = kind, State = state, Detail = detail, Source = source, CapturedAt = at };

    private async Task<Preparation> PrepareAsync(ActiveCdcScenario scenario, string environmentId, string integrationId, string? environmentType, string? targetUrl, Guid? snapshotId, CancellationToken ct)
    {
        var checks = new List<ActiveCdcReadinessCheck>();
        void Add(string key, string label, ActiveCdcReadinessState state, string detail) => checks.Add(new(key, label, state, detail));

        Add("enabled", "Active tests enabled", policy.Options.Enabled ? ActiveCdcReadinessState.Ready : ActiveCdcReadinessState.Blocked,
            policy.Options.Enabled ? "Enabled in backend configuration (ActiveCdcTests:Enabled)." : "Disabled in backend configuration (ActiveCdcTests:Enabled is not true).");
        var envBlock = ActiveCdcPolicy.EnvironmentBlock(environmentType);
        Add("environment", "Environment (DEV/QA only)", envBlock is null ? ActiveCdcReadinessState.Ready : ActiveCdcReadinessState.Blocked, envBlock ?? $"{environmentType!.Trim()} — non-production.");

        var configured = await catalog.GetAsync(environmentId, environmentType, targetUrl, ct);
        var integration = configured.Integrations.FirstOrDefault(i => i.Id == integrationId);
        var platform = integration is null ? null : configured.Platforms.FirstOrDefault(pl => pl.Id == integration.PlatformId);
        var source = (integration?.SourceResource ?? "").Split('.');
        var (group, assumed) = integration is null ? (null, false) : IntegrationConfigurationRules.EffectiveConsumerGroup(integration, platform);
        var destination = new ActiveCdcDestination
        {
            EnvironmentId = environmentId, IntegrationId = integrationId, NamespaceFqdn = platform?.NamespaceFqdn?.Trim(), EventHub = integration?.EndpointOrTopic?.Trim(),
            ConsumerGroup = group, ConsumerGroupAssumed = assumed,
            SourceDatabase = source.Length == 3 ? source[0] : null, SourceSchema = source.Length == 3 ? source[1] : null, SourceTable = source.Length == 3 ? source[2] : null,
        };
        var integrationProblem = integration is null ? "The integration is not configured for this environment."
            : !integration.Enabled ? "The integration is disabled."
            : integration.Kind != IntegrationKind.EventHub ? "The integration is not an Event Hub integration."
            : platform is null ? "The integration has no Event Hubs platform."
            : source.Length != 3 ? "The integration's source resource is not database.schema.table, so the CDC envelope source cannot be derived."
            : !string.Equals(destination.SourceTable, scenario.Table, StringComparison.OrdinalIgnoreCase) ? $"The integration carries {destination.SourceTable}, not {scenario.Table}."
            : null;
        Add("integration", "CDC integration", integrationProblem is null ? ActiveCdcReadinessState.Ready : ActiveCdcReadinessState.Blocked,
            integrationProblem ?? $"{integration!.DisplayName}: source {integration.SourceResource} → consumer {integration.Consumer.DisplayName ?? "not named"}.");

        var (approved, reason) = integrationProblem is null ? policy.Approve(environmentType, destination.NamespaceFqdn, destination.EventHub, targetUrl) : (null, "Resolve the integration first.");
        destination = destination with { Approved = approved is not null, Detail = reason };
        Add("destination", "Destination approved", approved is null ? ActiveCdcReadinessState.Blocked : ActiveCdcReadinessState.Ready,
            $"{destination.EventHub ?? "No hub"} on {destination.NamespaceFqdn ?? "no namespace"} — {reason}");

        var identity = sender.UnavailableReason;
        Add("identity", "Azure sender identity", identity is null ? ActiveCdcReadinessState.Ready : ActiveCdcReadinessState.Blocked,
            identity ?? "The instance's Azure identity (DefaultAzureCredential) is available. No connection string, SAS or key is used.");
        Add("sender-rights", "Event Hubs Data Sender right", ActiveCdcReadinessState.Unknown,
            "Not probed (probing would send). The first run shows whether the identity holds Azure Event Hubs Data Sender on this hub.");

        var snapshots = (await sources.ListAsync(environmentId, ct)).Where(s => s.IntegrationId == integrationId).OrderByDescending(s => s.AnalyzedAt).ToList();
        // An explicitly chosen snapshot is a Source Analysis snapshot (exact id); without a choice only a snapshot uploaded for this
        // integration by an earlier version is used — a standalone Source Analysis snapshot is never bound silently.
        var selected = snapshotId is not { } id ? snapshots.FirstOrDefault() : snapshots.FirstOrDefault(s => s.Id == id);
        if (snapshotId is { } chosenId && selected is null && await sources.FindSourceAnalysisAsync(chosenId, ct) is { } chosen)
            selected = chosen with { IntegrationId = integrationId };
        var manifest = snapshotId is not null && selected is null
            ? ActiveCdcContractManifestService.Evaluate(scenario, null, null) with { Detail = "The selected source snapshot does not belong to this integration." }
            : ActiveCdcContractManifestService.Evaluate(scenario, selected, snapshots.FirstOrDefault()?.Id, policy.Options.InvalidFixtureReviewedArchives);
        Add("contract", "Source contract", manifest.Status == ActiveCdcContractStatus.Compatible ? ActiveCdcReadinessState.Ready : ActiveCdcReadinessState.Blocked,
            $"{ActiveCdcLabels.Contract(manifest.Status)} — {manifest.Detail}");
        if (scenario.Id == ActiveCdcScenarioCatalog.InvalidThenValidId)
            Add("invalid-fixture", "Invalid fixture", manifest.InvalidFixtureStatus == "Reviewed" ? ActiveCdcReadinessState.Ready : ActiveCdcReadinessState.Blocked,
                manifest.InvalidFixtureStatus == "Reviewed" ? $"Reviewed — {scenario.InvalidFixture}: {scenario.InvalidCondition}"
                    : $"Needs review — {(manifest.InvalidFixtureDetail.Length > 0 ? manifest.InvalidFixtureDetail : "no source snapshot is bound, so the invalid behavior cannot be checked.")}");

        var range = policy.PersonPkRange(out var rangeReason);
        if (range is { } r)
        {
            var next = await store.MaxPersonPkAsync(environmentId, r.Min, r.Max, ct) is { } used ? used + 1 : r.Min;
            var available = Math.Max(0, (long)r.Max - next + 1);
            var needed = KeysNeeded(scenario);
            Add("synthetic-key", "Reserved synthetic PersonPK range", available >= needed ? ActiveCdcReadinessState.Ready : ActiveCdcReadinessState.Blocked,
                available >= needed ? $"{rangeReason} {available} unused key(s); this scenario needs {needed}."
                    : $"{rangeReason} Only {available} unused key(s) left; this scenario needs {needed} distinct keys.");
        }
        else Add("synthetic-key", "Reserved synthetic PersonPK range", ActiveCdcReadinessState.Blocked, rangeReason);

        var checkpoint = platform is null ? null : checkpoints.Describe(platform);
        Add("checkpoint", "Consumer checkpoint evidence (read-only)",
            checkpoint?.State == IntegrationEvidenceState.Available && group is not null ? ActiveCdcReadinessState.Ready : ActiveCdcReadinessState.Optional,
            group is null ? "No consumer group is configured or assumed; checkpoint movement will be Not assessed."
            : $"{checkpoint?.Reason ?? "No platform."} Consumer group {group}{(assumed ? " (configured assumption)" : "")}.");
        if (scenario.MessageCount > 1)
            Add("progression-evidence", "Runtime progression evidence", checkpoint?.State == IntegrationEvidenceState.Available && group is not null ? ActiveCdcReadinessState.Ready : ActiveCdcReadinessState.Optional,
                checkpoint?.State == IntegrationEvidenceState.Available && group is not null
                    ? $"Partition positions and consumer checkpoints can be read for {group}{(assumed ? " (configured assumption)" : "")}. Without them the result is Partial, never Passed."
                    : "Partition/checkpoint evidence is not available, so consumer progression cannot be assessed: the result would be Partial, never Passed.");
        else
            Add("person-verification", "Person persisted verification", ActiveCdcReadinessState.Optional,
                "No reliable read-only Person verification exists (no standalone Person read endpoint; the ingestion success log is Debug level). Person persisted stays Not assessed, so a successful send is Partial, never Passed.");

        var running = coordinator.LeaseHolder(ActiveCdcRunCoordinator.Key(environmentId, integrationId));
        Add("concurrency", "No run in flight", running is null ? ActiveCdcReadinessState.Ready : ActiveCdcReadinessState.Blocked,
            running is null ? "One run per integration at a time." : $"Run {running:N} is in flight for this integration.");

        var canRun = checks.All(c => c.State is ActiveCdcReadinessState.Ready or ActiveCdcReadinessState.Unknown or ActiveCdcReadinessState.Optional);
        return new(scenario, integration, platform, destination, approved, manifest, checks, canRun && approved is not null, running);
    }
}
