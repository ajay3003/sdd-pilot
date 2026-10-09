using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveCdcTests;

public sealed class ActiveCdcRequestException(string message) : Exception(message);

public interface IActiveCdcTestService
{
    Task<ActiveCdcReadiness> ReadinessAsync(string environmentId, string integrationId, string? environmentType, string? targetUrl, Guid? snapshotId, CancellationToken ct = default) =>
        ReadinessAsync(environmentId, integrationId, environmentType, targetUrl, snapshotId, null, ct);
    Task<ActiveCdcReadiness> ReadinessAsync(string environmentId, string integrationId, string? environmentType, string? targetUrl, Guid? snapshotId, string? scenarioId, CancellationToken ct = default);
    Task<IReadOnlyList<ActiveEventScenarioDescriptor>> ScenariosAsync(string environmentId, string integrationId, CancellationToken ct = default);
    Task<ActiveCdcRun> StartAsync(ActiveCdcRunRequest request, string? environmentType, string? targetUrl, CancellationToken ct = default);
    Task<ActiveCdcRun?> GetAsync(Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<ActiveCdcRunSummary>> HistoryAsync(string environmentId, string? integrationId, CancellationToken ct = default);
    Task<bool> CancelAsync(Guid runId, CancellationToken ct = default);
}

/// <summary>
/// Active CDC tests: readiness (nothing contacted), start (every gate re-evaluated in the backend, durable intent written before the send,
/// one run per integration), cancellation, and immutable history. The send itself happens in <see cref="ActiveCdcRunner"/>.
/// </summary>
public sealed class ActiveCdcTestService(IIntegrationCatalogService catalog, IqrSourceStore sources, ActiveCdcPolicy policy, IActiveEventScenarioRegistry scenarios, IEventHubTestSender sender,
    ICheckpointEvidenceSource checkpoints, ActiveCdcRunStore store, ActiveCdcRunCoordinator coordinator, ActiveCdcRunner runner, TimeProvider clock) : IActiveCdcTestService
{
    private sealed record Preparation(ActiveCdcScenario Scenario, IntegrationDefinition? Integration, IntegrationPlatform? Platform, ActiveCdcDestination Destination,
        ApprovedCdcDestination? Approved, ActiveCdcContractManifest Manifest, List<ActiveCdcReadinessCheck> Checks, bool CanRun, Guid? RunningRunId,
        ActiveCdcOptions.TrustedTargetBinding? TrustedTarget);

    public async Task<ActiveCdcReadiness> ReadinessAsync(string environmentId, string integrationId, string? environmentType, string? targetUrl, Guid? snapshotId, string? scenarioId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scenarioId))
        {
            var available = scenarios.Scenarios(await ScenarioContextAsync(environmentId, integrationId, ct));
            scenarioId = available.FirstOrDefault()?.ScenarioId
                ?? throw new ActiveCdcRequestException("No active event scenarios are registered for the selected integration.");
        }
        var scenario = await ResolveScenarioAsync(environmentId, integrationId, scenarioId, ct);
        var p = await PrepareAsync(scenario, environmentId, integrationId, environmentType, targetUrl, snapshotId, ct);
        return new ActiveCdcReadiness
        {
            EnvironmentId = environmentId, IntegrationId = integrationId, Scenario = p.Scenario, Manifest = p.Manifest, Destination = p.Destination,
            Checks = p.Checks, CanRun = p.CanRun, RunningRunId = p.RunningRunId,
            ObservationSeconds = policy.Options.ObservationSeconds, SendTimeoutSeconds = policy.Options.SendTimeoutSeconds,
        };
    }

    public async Task<IReadOnlyList<ActiveEventScenarioDescriptor>> ScenariosAsync(string environmentId, string integrationId, CancellationToken ct = default)
    {
        var context = await ScenarioContextAsync(environmentId, integrationId, ct);
        return scenarios.Scenarios(context);
    }

    public async Task<ActiveCdcRun> StartAsync(ActiveCdcRunRequest request, string? environmentType, string? targetUrl, CancellationToken ct = default)
    {
        var scenario = await ResolveScenarioAsync(request.EnvironmentId, request.IntegrationId, request.ScenarioId, ct);
        if (!request.ConfirmedSend) throw new ActiveCdcRequestException("Confirm that one synthetic event is sent to the named non-production Event Hub.");
        if (string.IsNullOrWhiteSpace(request.EnvironmentId) || string.IsNullOrWhiteSpace(request.IntegrationId)) throw new ActiveCdcRequestException("Environment and integration are required.");
        var p = await PrepareAsync(scenario, request.EnvironmentId, request.IntegrationId, environmentType, targetUrl, request.SourceSnapshotId, ct);
        var run = new ActiveCdcRun
        {
            RunId = Guid.NewGuid(), EnvironmentId = request.EnvironmentId, EnvironmentName = p.TrustedTarget?.DisplayName ?? "Untrusted environment", EnvironmentType = p.TrustedTarget?.EnvironmentType.Trim() ?? "Unknown",
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
            var context = await ScenarioContextAsync(request.EnvironmentId, request.IntegrationId, ct);
            var scenarioProvider = scenarios.FindScenario(scenario.Id, context)
                ?? throw new ActiveCdcRequestException("The selected scenario provider is no longer applicable to this integration.");
            var descriptor = scenarios.Scenarios(context).Single(item => item.ScenarioId == scenario.Id && item.ExtensionId == scenarioProvider.ExtensionId);
            var providerPreparation = await scenarioProvider.PrepareAsync(scenario.Id, context, request.SourceSnapshotId, ct);
            if (!providerPreparation.Compatible)
                return await BlockedAsync(run, p, [providerPreparation.Detail.Length == 0 ? "The scenario provider prerequisites are not satisfied." : providerPreparation.Detail], ct);

            var trustedTarget = new ActiveEventTrustedTarget
            {
                TargetEnvironmentId = request.EnvironmentId,
                EnvironmentType = p.TrustedTarget?.EnvironmentType ?? "Unknown",
                IntegrationId = request.IntegrationId,
                IntegrationType = p.Integration?.Kind.ToString() ?? "",
                TransportType = descriptor.RequiredTransportType,
                Endpoint = p.Destination.NamespaceFqdn,
                Resource = p.Destination.EventHub,
                Consumer = p.Destination.ConsumerGroup,
            };
            IReadOnlyList<GeneratedActiveEvent> events;
            try
            {
                events = (await scenarioProvider.GenerateAsync(scenario.Id, run.RunId, trustedTarget, providerPreparation.Contract, ct)).Events;
                if (events.Count != descriptor.ExpectedEventCount)
                    throw new InvalidOperationException("The scenario provider generated an event count different from its descriptor.");
                run = ProjectGeneratedEvents(run, events);
            }
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
                        ? new[] { Step(ActiveCdcStepKind.IdentitiesAllocated, ActiveCdcEvidenceState.Observed, $"The scenario provider allocated {run.Messages.Select(message => message.SyntheticPersonPk).Where(value => value is not null).Distinct().Count()} distinct synthetic source identity value(s).", $"Scenario provider {scenarioProvider.ExtensionId}", now) }
                        : [],
                    .. scenario.ReplayKind == ActiveEventReplayKind.ControlAfterInvalid
                        ? new[] { Step(ActiveCdcStepKind.InvalidFixtureReviewed, ActiveCdcEvidenceState.Observed, $"{scenario.InvalidFixture}: {scenario.InvalidCondition} {p.Manifest.InvalidFixtureDetail}", "Reviewed fixture bound to the source archive", now) }
                        : [],
                    Step(ActiveCdcStepKind.FixtureGenerated, ActiveCdcEvidenceState.Observed, string.Join(" ", run.Messages.Select(message => $"{message.Label}: {message.PayloadBytes} bytes, SHA-256 {message.PayloadSha256[..16]}…")), $"Scenario provider {scenarioProvider.ExtensionId}", now),
                    .. scenario.ReplayKind == ActiveEventReplayKind.ExactReplay
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
            coordinator.Launch(intent.RunId, key, token => runner.ExecuteAsync(intent, p.Platform, p.TrustedTarget?.EnvironmentType, p.TrustedTarget?.TargetUrl, events, token));
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

    private static ActiveCdcStep ReplayEquivalence(IReadOnlyList<GeneratedActiveEvent> events, DateTimeOffset now) =>
        events[0].Body.AsSpan().SequenceEqual(events[1].Body)
            ? Step(ActiveCdcStepKind.ReplayEquivalence, ActiveCdcEvidenceState.Observed, "The scenario provider generated a byte-identical exact replay.", "Active Event scenario provider", now)
            : Step(ActiveCdcStepKind.ReplayEquivalence, ActiveCdcEvidenceState.Error, "The scenario provider's replay event is not byte-identical to its source event.", "Active Event scenario provider", now);

    private static ActiveCdcRun ProjectGeneratedEvents(ActiveCdcRun run, IReadOnlyList<GeneratedActiveEvent> events)
    {
        static ActiveCdcMessageEvidence Message(GeneratedActiveEvent item)
        {
            var metadata = item.SafeDisplayMetadata;
            var label = metadata.GetValueOrDefault("sequenceLabel", "");
            var pkText = metadata.GetValueOrDefault("syntheticPersonPk", "");
            var hasPk = int.TryParse(pkText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pk);
            var expectedId = Guid.TryParse(metadata.GetValueOrDefault("expectedPersonId"), out var parsedId) ? parsedId : Guid.Empty;
            return new ActiveCdcMessageEvidence
            {
                Label = label,
                Role = metadata.GetValueOrDefault("role", "Generated event"),
                SyntheticPersonPk = hasPk ? pk : null,
                ExpectedPersonId = expectedId,
                Marker = metadata.GetValueOrDefault("marker", ""),
                PayloadSha256 = item.BodySha256,
                PayloadBytes = item.BodyBytes,
                InvalidCondition = metadata.GetValueOrDefault("invalidCondition", ""),
            };
        }
        var orderedEvents = events.OrderBy(item => item.SequenceIndex).ToArray();
        var messages = orderedEvents.Length > 1 ? orderedEvents.Select(Message).ToList() : [];
        var summaryEvent = orderedEvents.FirstOrDefault(item => item.SafeDisplayMetadata.GetValueOrDefault("syntheticPersonPk") is { Length: > 0 });
        var fixture = summaryEvent is null ? null : new ActiveCdcFixtureSummary
        {
            SyntheticPersonPk = int.TryParse(summaryEvent.SafeDisplayMetadata.GetValueOrDefault("syntheticPersonPk"), out var parsedPk) ? parsedPk : 0,
            ExpectedPersonId = Guid.TryParse(summaryEvent.SafeDisplayMetadata.GetValueOrDefault("expectedPersonId"), out var expectedId) ? expectedId : Guid.Empty,
            Marker = summaryEvent.SafeDisplayMetadata.GetValueOrDefault("marker", ""),
            PayloadSha256 = summaryEvent.BodySha256,
            PayloadBytes = summaryEvent.BodyBytes,
            Fields = summaryEvent.SafeDisplayMetadata.GetValueOrDefault("fields", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
            SyntheticBirthDate = DateOnly.TryParse(summaryEvent.SafeDisplayMetadata.GetValueOrDefault("syntheticBirthDate"), out var birthDate) ? birthDate : default,
            Notes = ["Generated by the registered scenario provider; payload body is not persisted."],
        };
        return run with { Fixture = fixture, Messages = messages };
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
        var trustedTarget = policy.ResolveTrustedTarget(environmentId);
        var envBlock = trustedTarget is null ? "This environment ID has no trusted server-side ActiveCdcTests:TrustedTargets binding." : ActiveCdcPolicy.EnvironmentBlock(trustedTarget.EnvironmentType);
        Add("environment", "Environment (DEV/QA only)", envBlock is null ? ActiveCdcReadinessState.Ready : ActiveCdcReadinessState.Blocked,
            envBlock ?? $"{trustedTarget!.EnvironmentType.Trim()} - server-bound non-production.");

        // Caller-supplied environment type and URL are ignored; only backend-owned bindings can authorize active execution.
        var configured = await catalog.GetAsync(environmentId, trustedTarget?.EnvironmentType, trustedTarget?.TargetUrl, ct);
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

        var (approved, reason) = integrationProblem is null ? policy.Approve(trustedTarget?.EnvironmentType, destination.NamespaceFqdn, destination.EventHub, trustedTarget?.TargetUrl) : (null, "Resolve the integration first.");
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
        if (scenario.ReplayKind == ActiveEventReplayKind.ControlAfterInvalid)
            Add("invalid-fixture", "Invalid fixture", manifest.InvalidFixtureStatus == "Reviewed" ? ActiveCdcReadinessState.Ready : ActiveCdcReadinessState.Blocked,
                manifest.InvalidFixtureStatus == "Reviewed" ? $"Reviewed — {scenario.InvalidFixture}: {scenario.InvalidCondition}"
                    : $"Needs review — {(manifest.InvalidFixtureDetail.Length > 0 ? manifest.InvalidFixtureDetail : "no source snapshot is bound, so the invalid behavior cannot be checked.")}");

        var providerContext = new ActiveEventProjectEvidenceContext(environmentId, integration, platform);
        if (scenarios.FindScenario(scenario.Id, providerContext) is { } provider)
        {
            var providerPreparation = await provider.PrepareAsync(scenario.Id, providerContext, snapshotId, ct);
            foreach (var providerCheck in providerPreparation.Checks)
                if (checks.All(existing => existing.Key != providerCheck.Key)) checks.Add(new(providerCheck.Key, providerCheck.Label,
                    providerCheck.State switch
                    {
                        ActiveEventReadinessState.Ready => ActiveCdcReadinessState.Ready,
                        ActiveEventReadinessState.Blocked => ActiveCdcReadinessState.Blocked,
                        ActiveEventReadinessState.Unknown => ActiveCdcReadinessState.Unknown,
                        _ => ActiveCdcReadinessState.Optional,
                    }, providerCheck.Detail));
        }

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
        return new(scenario, integration, platform, destination, approved, manifest, checks, canRun && approved is not null, running, trustedTarget);
    }

    private async Task<ActiveCdcScenario> ResolveScenarioAsync(string environmentId, string integrationId, string scenarioId, CancellationToken ct)
    {
        var context = await ScenarioContextAsync(environmentId, integrationId, ct);
        var provider = scenarios.FindScenario(scenarioId, context);
        // A known scenario with an inapplicable target may still be represented as a blocked readiness/run record.
        // It cannot reach generation or sending without a registered provider.
        if (provider is null)
            return ActiveCdcScenarioCatalog.Find(scenarioId)
                ?? throw new ActiveCdcRequestException("The requested scenario is not a registered built-in Active Event scenario for this integration.");
        var descriptor = scenarios.Scenarios(context).Single(item => item.ScenarioId == scenarioId && item.ExtensionId == provider.ExtensionId);
        var legacyRunProjection = ActiveCdcScenarioCatalog.Find(descriptor.ScenarioId);
        return legacyRunProjection ?? throw new ActiveCdcRequestException("The registered scenario has no compatible run-history projection.");
    }

    private async Task<ActiveEventProjectEvidenceContext> ScenarioContextAsync(string environmentId, string integrationId, CancellationToken ct)
    {
        var trustedTarget = policy.ResolveTrustedTarget(environmentId);
        var configured = await catalog.GetAsync(environmentId, trustedTarget?.EnvironmentType, trustedTarget?.TargetUrl, ct);
        var integration = configured.Integrations.FirstOrDefault(item => item.Id == integrationId);
        var platform = integration is null ? null : configured.Platforms.FirstOrDefault(item => item.Id == integration.PlatformId);
        return new(environmentId, integration, platform);
    }
}
