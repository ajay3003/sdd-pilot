using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting;

public interface IActiveEventLifecycleService
{
    Task<IReadOnlyList<ActiveEventScenarioDescriptor>> ScenariosAsync(string environmentId, string integrationId, CancellationToken ct);
    Task<ActiveEventReadiness> ReadinessAsync(string environmentId, string integrationId, string extensionId, string scenarioId, Guid? snapshotId, CancellationToken ct);
    Task<ActiveEventRunResult> StartAsync(ActiveEventRunRequest request, CancellationToken ct);
    Task<ActiveEventRunResult?> GetAsync(Guid runId, CancellationToken ct);
    Task<IReadOnlyList<ActiveEventRunResult>> HistoryAsync(string environmentId, string? integrationId, CancellationToken ct);
    bool Cancel(Guid runId);
}

/// <summary>Generic lifecycle boundary: trusted target, provider preparation, full generic runner, and body-free generic history.</summary>
public sealed class ActiveEventLifecycleService(
    IIntegrationCatalogService catalog,
    ActiveCdcPolicy policy,
    IActiveEventScenarioRegistry scenarios,
    IActiveEventTransportRegistry transports,
    ActiveEventExecutionRunner runner,
    ActiveEventRunStore history,
    ActiveCdcRunCoordinator coordinator,
    TimeProvider clock,
    ILogger<ActiveEventLifecycleService> logger) : IActiveEventLifecycleService
{
    public async Task<IReadOnlyList<ActiveEventScenarioDescriptor>> ScenariosAsync(string environmentId, string integrationId, CancellationToken ct)
    {
        var context = await ContextAsync(environmentId, integrationId, ct);
        return scenarios.Scenarios(context);
    }

    public async Task<ActiveEventReadiness> ReadinessAsync(string environmentId, string integrationId, string extensionId, string scenarioId, Guid? snapshotId, CancellationToken ct)
    {
        var setup = await PrepareAsync(environmentId, integrationId, extensionId, scenarioId, snapshotId, ct);
        return new()
        {
            TargetEnvironmentId = environmentId,
            IntegrationId = integrationId,
            Scenario = setup.Descriptor,
            Target = setup.Target,
            SourceContract = setup.Preparation.Contract,
            Checks = setup.Checks,
            CanRun = setup.Checks.All(check => check.State is ActiveEventReadinessState.Ready or ActiveEventReadinessState.Optional or ActiveEventReadinessState.Unknown),
            SendTimeoutSeconds = policy.Options.SendTimeoutSeconds,
            ObservationTimeoutSeconds = policy.Options.ObservationSeconds,
        };
    }

    public async Task<ActiveEventRunResult> StartAsync(ActiveEventRunRequest request, CancellationToken ct)
    {
        if (!request.Confirmed) throw new ActiveCdcRequestException("Confirm the synthetic event run before execution.");
        var setup = await PrepareAsync(request.TargetEnvironmentId, request.IntegrationId, request.ExtensionId, request.ScenarioId, request.SourceSnapshotId, ct);
        if (setup.Checks.Any(check => check.State == ActiveEventReadinessState.Blocked))
            return await SaveBlockedAsync(request, setup, string.Join(" ", setup.Checks.Where(check => check.State == ActiveEventReadinessState.Blocked).Select(check => check.Detail)), ct);
        if (!string.Equals(request.ConfirmedDestination?.Trim(), setup.Target.Resource, StringComparison.Ordinal))
            return await SaveBlockedAsync(request, setup, "The confirmed destination no longer matches the trusted integration target.", ct);

        var runId = Guid.NewGuid();
        var key = ActiveCdcRunCoordinator.Key(request.TargetEnvironmentId, request.IntegrationId);
        if (!coordinator.TryAcquire(key, runId))
            return await SaveBlockedAsync(request, setup, "Another active event run is in flight for this integration.", ct);

        var initial = new ActiveEventRunResult
        {
            RunId = runId,
            Scenario = setup.Descriptor,
            Target = setup.Target,
            SourceContract = setup.Preparation.Contract,
            Status = ActiveEventRunStatus.Running,
            StartedAt = clock.GetUtcNow(),
        };
        var launched = false;
        try
        {
            if (!await history.InsertRunningAsync(initial, ct))
                return initial with { Status = ActiveEventRunStatus.Failed, CompletedAt = clock.GetUtcNow(), Limitations = ["The run could not be durably recorded before execution; no event was sent."] };

            coordinator.Launch(runId, key, async token =>
            {
                try
                {
                    var result = await runner.ExecuteAsync(setup.Provider, setup.Descriptor, setup.Preparation, setup.Target,
                        setup.Preparation.Contract, runId, TimeSpan.FromSeconds(policy.Options.SendTimeoutSeconds),
                        TimeSpan.FromSeconds(policy.Options.ObservationSeconds), token,
                        async (snapshot, progressToken) =>
                        {
                            if (!await history.UpdateRunningAsync(snapshot, progressToken))
                                throw new InvalidOperationException("Active Event progress could not be persisted; execution stopped before proceeding to the next stage.");
                        });
                    await history.CompleteAsync(result.ToHistoryResult(setup.Preparation.Contract), CancellationToken.None);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    await CompleteInterruptedAsync(initial, ActiveEventRunStatus.Cancelled, "Run cancellation interrupted execution. If a send had started, transport acceptance may be unknown; the event was not resent.");
                }
                catch (Exception ex)
                {
                    logger.LogError("Active Event run {RunId} failed in lifecycle execution ({ExceptionType}).", runId, ex.GetType().Name);
                    await CompleteInterruptedAsync(initial, ActiveEventRunStatus.Inconclusive, "Execution stopped unexpectedly. Any in-flight send outcome may be unknown; the event was not resent.");
                }
            });
            launched = true;
            return initial;
        }
        finally
        {
            if (!launched) coordinator.Release(key, runId);
        }
    }

    public Task<ActiveEventRunResult?> GetAsync(Guid runId, CancellationToken ct) => history.GetAsync(runId, ct);

    public Task<IReadOnlyList<ActiveEventRunResult>> HistoryAsync(string environmentId, string? integrationId, CancellationToken ct) =>
        history.ListAsync(environmentId, integrationId, 50, ct);

    public bool Cancel(Guid runId) => coordinator.Cancel(runId);

    private async Task CompleteInterruptedAsync(ActiveEventRunResult initial, ActiveEventRunStatus status, string reason)
    {
        var current = await history.GetAsync(initial.RunId, CancellationToken.None) ?? initial;
        var evidence = current.Evidence.ToList();
        var sendWasAttempted = evidence.Any(item => item.Stage == ActiveEventEvidenceStage.SendAttempted);
        var acceptanceWasResolved = evidence.Any(item => item.Stage == ActiveEventEvidenceStage.TransportAccepted);
        if (sendWasAttempted && !acceptanceWasResolved)
            evidence.Add(new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.Ambiguous,
                Detail = reason, EvidenceSource = "Active Event lifecycle", CapturedAt = clock.GetUtcNow() });
        var completed = current with
        {
            Status = status,
            CompletedAt = clock.GetUtcNow(),
            Evidence = evidence,
            Limitations = current.Limitations.Append(reason).Distinct(StringComparer.Ordinal).ToArray(),
        };
        await history.CompleteAsync(completed, CancellationToken.None);
    }

    private async Task<ActiveEventRunResult> SaveBlockedAsync(ActiveEventRunRequest request, Prepared setup, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var result = new ActiveEventRunResult
        {
            RunId = Guid.NewGuid(), Scenario = setup.Descriptor, Target = setup.Target,
            SourceContract = setup.Preparation.Contract, Status = ActiveEventRunStatus.SafetyBlocked,
            StartedAt = now, CompletedAt = now,
            Evidence = [new() { Stage = ActiveEventEvidenceStage.Generated, Status = ActiveEventEvidenceStatus.SafetyBlocked,
                Detail = reason, EvidenceSource = "Active Event safety policy", CapturedAt = now }],
            Limitations = [reason],
        };
        await history.InsertRunningAsync(result with { Status = ActiveEventRunStatus.Running, CompletedAt = null }, ct);
        await history.CompleteAsync(result, ct);
        return result;
    }

    private async Task<Prepared> PrepareAsync(string environmentId, string integrationId, string extensionId, string scenarioId, Guid? snapshotId, CancellationToken ct)
    {
        var context = await ContextAsync(environmentId, integrationId, ct);
        var provider = scenarios.FindScenario(scenarioId, context);
        if (provider is null || !string.Equals(provider.ExtensionId, extensionId, StringComparison.Ordinal))
            throw new ActiveCdcRequestException("No applicable registered provider owns the requested scenario.");
        var descriptor = scenarios.Scenarios(context).Single(item => item.ScenarioId == scenarioId && item.ExtensionId == extensionId);
        var targetBinding = policy.ResolveTrustedTarget(environmentId);
        var checks = new List<ActiveEventReadinessCheck>();
        void Add(string key, string label, ActiveEventReadinessState state, string detail) => checks.Add(new(key, label, state, detail));

        Add("enabled", "Active event execution", policy.Options.Enabled ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
            policy.Options.Enabled ? "Enabled by backend policy." : "Disabled by backend policy.");
        var environmentProblem = targetBinding is null ? "No server-side target binding exists." : ActiveCdcPolicy.EnvironmentBlock(targetBinding.EnvironmentType);
        Add("environment", "Trusted non-production environment", environmentProblem is null ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
            environmentProblem ?? $"Server-bound environment type: {targetBinding!.EnvironmentType}.");

        var integration = context.Integration;
        var platform = context.Platform;
        var integrationProblem = integration is null || !integration.Enabled ? "The selected integration is missing or disabled."
            : !string.Equals(integration.Kind.ToString(), descriptor.RequiredIntegrationType, StringComparison.OrdinalIgnoreCase) ? "The integration type does not satisfy the scenario."
            : platform is null ? "The integration has no configured platform."
            : null;
        var resource = integration?.EndpointOrTopic?.Trim();
        var endpoint = platform?.NamespaceFqdn?.Trim();
        if (integrationProblem is null && (string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(endpoint))) integrationProblem = "The trusted integration destination is incomplete.";
        var (approved, destinationReason) = integrationProblem is null
            ? policy.Approve(targetBinding?.EnvironmentType, endpoint, resource, targetBinding?.TargetUrl)
            : (null, integrationProblem);
        var target = new ActiveEventTrustedTarget
        {
            TargetEnvironmentId = environmentId,
            EnvironmentType = targetBinding?.EnvironmentType ?? "Unknown",
            IntegrationId = integrationId,
            IntegrationType = integration?.Kind.ToString() ?? "",
            TransportType = descriptor.RequiredTransportType,
            Endpoint = endpoint,
            Resource = resource,
            Consumer = integration is null ? null : IntegrationConfigurationRules.EffectiveConsumerGroup(integration, platform).Group,
            SafeMetadata = new Dictionary<string, string>
            {
                ["destinationApproval"] = approved is null ? "blocked" : "approved",
                ["displayName"] = targetBinding?.DisplayName ?? environmentId,
            },
        };
        Add("destination", "Enrolled destination", approved is null ? ActiveEventReadinessState.Blocked : ActiveEventReadinessState.Ready,
            approved is null ? destinationReason : "Destination resolved from the enabled integration and backend enrollment policy.");

        var transport = transports.Resolve(descriptor.RequiredTransportType);
        var transportReason = "";
        var transportReady = transport is not null && transport.CanSend(target, out transportReason);
        Add("transport", "Transport provider", transport is null ? ActiveEventReadinessState.Blocked : transportReady ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
            transport is null ? $"No provider is registered for {descriptor.RequiredTransportType}." : transportReady ? "Registered provider is ready." : transportReason);

        var preparation = await provider.PrepareAsync(scenarioId, context, snapshotId, ct);
        foreach (var check in preparation.Checks)
            checks.Add(check);
        if (!preparation.Compatible && !preparation.Checks.Any(check => check.State == ActiveEventReadinessState.Blocked))
            Add("provider", "Scenario prerequisites", ActiveEventReadinessState.Blocked, preparation.Detail);

        return new(provider, descriptor, target, preparation, checks);
    }

    private async Task<ActiveEventProjectEvidenceContext> ContextAsync(string environmentId, string integrationId, CancellationToken ct)
    {
        var trusted = policy.ResolveTrustedTarget(environmentId);
        var configured = await catalog.GetAsync(environmentId, trusted?.EnvironmentType, trusted?.TargetUrl, ct);
        var integration = configured.Integrations.FirstOrDefault(item => item.Id == integrationId);
        var platform = integration is null ? null : configured.Platforms.FirstOrDefault(item => item.Id == integration.PlatformId);
        return new(environmentId, integration, platform);
    }

    private sealed record Prepared(IActiveEventScenarioProvider Provider, ActiveEventScenarioDescriptor Descriptor,
        ActiveEventTrustedTarget Target, ActiveEventScenarioPreparation Preparation, List<ActiveEventReadinessCheck> Checks);
}
