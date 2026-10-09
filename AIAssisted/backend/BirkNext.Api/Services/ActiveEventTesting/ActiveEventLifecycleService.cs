using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting;

public sealed class ActiveEventRequestException(string message) : Exception(message);

public interface IActiveEventLifecycleService
{
    ActiveEventEnvironmentTrust EnvironmentTrust(string environmentId);
    Task<IReadOnlyList<ActiveEventProviderSummary>> ProvidersAsync(string environmentId, CancellationToken ct);
    Task<IReadOnlyList<ActiveEventScenarioDescriptor>> ScenariosAsync(string environmentId, string integrationId, CancellationToken ct);
    Task<ActiveEventReadiness> ReadinessAsync(string environmentId, string integrationId, string extensionId, string scenarioId, Guid? snapshotId, CancellationToken ct);
    Task<ActiveEventRunResult> StartAsync(ActiveEventRunRequest request, CancellationToken ct);
    Task<ActiveEventRunResult?> GetAsync(Guid runId, CancellationToken ct);
    Task<IReadOnlyList<ActiveEventRunSummary>> HistoryAsync(ActiveEventHistoryQuery query, CancellationToken ct);
    bool Cancel(Guid runId);
}

public sealed record ActiveEventHistoryQuery(string EnvironmentId, string? IntegrationId = null, string? ExtensionId = null, string? ScenarioId = null,
    ActiveEventRunStatus? Status = null, DateTimeOffset? From = null, DateTimeOffset? To = null, int Take = 50);

/// <summary>
/// The one production entry point for every active event run: backend-owned trusted environment → IQR integration → approved destination →
/// core safety checks → provider readiness (which can only add blocks) → durable intent → the single generic runner → generic history.
/// Nothing the client sends decides the environment type, target URL, namespace, hub or payload.
/// </summary>
public sealed class ActiveEventLifecycleService(
    IIntegrationCatalogService catalog,
    ActiveEventPolicy policy,
    ITrustedExecutionEnvironmentRegistry environments,
    IActiveEventScenarioRegistry scenarios,
    IActiveEventTransportRegistry transports,
    ActiveEventExecutionRunner runner,
    ActiveEventRunStore history,
    LegacyActiveCdcHistory legacyHistory,
    ActiveEventRunCoordinator coordinator,
    TimeProvider clock,
    ILogger<ActiveEventLifecycleService> logger) : IActiveEventLifecycleService
{
    public ActiveEventEnvironmentTrust EnvironmentTrust(string environmentId) => environments.Describe(environmentId);

    public async Task<IReadOnlyList<ActiveEventProviderSummary>> ProvidersAsync(string environmentId, CancellationToken ct)
    {
        var configured = await CatalogAsync(environmentId, ct);
        var contexts = configured.Integrations
            .Select(integration => new ActiveEventProjectEvidenceContext(environmentId, integration, configured.Platforms.FirstOrDefault(p => p.Id == integration.PlatformId)))
            .ToArray();
        return scenarios.Providers.Select(provider =>
        {
            var applicable = contexts.Where(provider.CanApply).Select(context => context.Integration!.Id).ToArray();
            return new ActiveEventProviderSummary
            {
                ExtensionId = provider.ExtensionId, ExtensionVersion = provider.ExtensionVersion, DisplayName = provider.DisplayName, Description = provider.Description,
                Resources = provider.Resources, Scenarios = provider.CatalogScenarios, ApplicableIntegrationIds = applicable,
                NotApplicableReason = applicable.Length > 0 ? ""
                    : $"No configured Event Hub integration was found for this CDC source ({string.Join(" / ", provider.Resources)}). Configure it in Target Environment → Integrations with its database.schema.table source resource.",
            };
        }).ToArray();
    }

    public async Task<IReadOnlyList<ActiveEventScenarioDescriptor>> ScenariosAsync(string environmentId, string integrationId, CancellationToken ct) =>
        scenarios.Scenarios(await ContextAsync(environmentId, integrationId, ct));

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
            CanRun = setup.Checks.All(check => !ActiveEventReadinessRules.Blocks(check.State)),
            SendTimeoutSeconds = policy.Options.SendTimeoutSeconds,
            ObservationTimeoutSeconds = policy.Options.ObservationSeconds,
            SyntheticSummary = setup.Preparation.SyntheticSummary,
        };
    }

    public async Task<ActiveEventRunResult> StartAsync(ActiveEventRunRequest request, CancellationToken ct)
    {
        if (!request.Confirmed) throw new ActiveEventRequestException("Confirm the synthetic event run before execution.");
        if (string.IsNullOrWhiteSpace(request.TargetEnvironmentId) || string.IsNullOrWhiteSpace(request.IntegrationId))
            throw new ActiveEventRequestException("Environment and integration are required.");
        var setup = await PrepareAsync(request.TargetEnvironmentId, request.IntegrationId, request.ExtensionId, request.ScenarioId, request.SourceSnapshotId, ct);
        if (setup.Checks.Where(check => ActiveEventReadinessRules.Blocks(check.State)).Select(check => $"{check.Label}: {check.Detail}").ToList() is { Count: > 0 } blocked)
            return await SaveBlockedAsync(setup, string.Join(" ", blocked), ct);
        if (!string.Equals(request.ConfirmedDestination?.Trim(), setup.Target.Resource, StringComparison.Ordinal))
            return await SaveBlockedAsync(setup, "The confirmed destination no longer matches the trusted integration target — review and confirm again.", ct);
        if (request.ReviewedContractFingerprint is { Length: > 0 } reviewed && !string.Equals(reviewed, setup.Preparation.Contract.ContractFingerprint, StringComparison.Ordinal))
            return await SaveBlockedAsync(setup, "The source contract changed since it was reviewed (needs review). Nothing was sent.", ct);

        var runId = Guid.NewGuid();
        var key = ActiveEventRunCoordinator.Key(request.TargetEnvironmentId, request.IntegrationId);
        if (!coordinator.TryAcquire(key, runId))
            return await SaveBlockedAsync(setup, "Another active event run is in flight for this integration.", ct);

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

            var observation = new ActiveEventObservationContext(setup.Target, setup.Context.Integration, setup.Context.Platform);
            coordinator.Launch(runId, key, async token =>
            {
                try
                {
                    var result = await runner.ExecuteAsync(setup.Provider, setup.Descriptor, setup.Preparation, observation,
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
                    var sendAttempted = (await history.GetAsync(runId, CancellationToken.None))?.SendAttempted ?? true;
                    await CompleteInterruptedAsync(initial, ActiveEventRunStatus.Inconclusive,
                        ex is InvalidOperationException && !sendAttempted
                            ? $"Execution stopped before any send: {ex.Message}"
                            : "Execution stopped unexpectedly. Any in-flight send outcome may be unknown; the event was not resent.");
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

    public async Task<ActiveEventRunResult?> GetAsync(Guid runId, CancellationToken ct)
    {
        var run = await history.GetAsync(runId, ct);
        if (run is null) return await legacyHistory.GetAsync(runId, ct);
        return run.Status == ActiveEventRunStatus.Running && !coordinator.IsRunning(runId) ? await RecoverAsync(run) : run;
    }

    public async Task<IReadOnlyList<ActiveEventRunSummary>> HistoryAsync(ActiveEventHistoryQuery query, CancellationToken ct)
    {
        var take = Math.Clamp(query.Take, 1, 200);
        var runs = new List<ActiveEventRunResult>();
        foreach (var run in await history.ListAsync(query.EnvironmentId, query.IntegrationId, 200, ct))
            runs.Add(run.Status == ActiveEventRunStatus.Running && !coordinator.IsRunning(run.RunId) ? await RecoverAsync(run) : run);
        runs.AddRange(await legacyHistory.ListAsync(query.EnvironmentId, query.IntegrationId, 50, ct));
        return runs
            .Where(run => query.ExtensionId is not { Length: > 0 } extension || run.Scenario.ExtensionId == extension)
            .Where(run => query.ScenarioId is not { Length: > 0 } scenario || run.Scenario.ScenarioId == scenario)
            .Where(run => query.Status is not { } status || run.Status == status)
            .Where(run => query.From is not { } from || run.StartedAt >= from)
            .Where(run => query.To is not { } to || run.StartedAt <= to)
            .OrderByDescending(run => run.StartedAt).Take(take)
            .Select(run => new ActiveEventRunSummary
            {
                RunId = run.RunId, ExtensionId = run.Scenario.ExtensionId, ProviderDisplayName = run.Scenario.ProviderDisplayName, ScenarioId = run.Scenario.ScenarioId,
                ScenarioName = run.Scenario.DisplayName, TargetEnvironmentId = run.Target.TargetEnvironmentId, IntegrationId = run.Target.IntegrationId,
                Status = run.Status, StartedAt = run.StartedAt, CompletedAt = run.CompletedAt, SendAttempted = run.SendAttempted, EventCount = run.Events.Count, Legacy = run.Legacy,
            }).ToArray();
    }

    public bool Cancel(Guid runId) => coordinator.Cancel(runId);

    /// <summary>A Running row whose process is gone: whether an in-flight send was accepted is exactly what is unknown.</summary>
    private async Task<ActiveEventRunResult> RecoverAsync(ActiveEventRunResult run)
    {
        await CompleteInterruptedAsync(run, ActiveEventRunStatus.Inconclusive, "Interrupted (BirkNext stopped while the run was in flight). No evidence was captured after the interruption; nothing was resent.");
        return await history.GetAsync(run.RunId, CancellationToken.None) ?? run;
    }

    private async Task CompleteInterruptedAsync(ActiveEventRunResult initial, ActiveEventRunStatus status, string reason)
    {
        var current = await history.GetAsync(initial.RunId, CancellationToken.None) ?? initial;
        var evidence = current.Evidence.ToList();
        foreach (var attempted in evidence.Where(item => item.Stage == ActiveEventEvidenceStage.SendAttempted).ToList())
            if (!evidence.Any(item => item.Stage == ActiveEventEvidenceStage.TransportAccepted && item.EventId == attempted.EventId))
                evidence.Add(new() { Stage = ActiveEventEvidenceStage.TransportAccepted, Status = ActiveEventEvidenceStatus.Ambiguous, EventId = attempted.EventId,
                    Detail = reason, EvidenceSource = "Active Event lifecycle", CapturedAt = clock.GetUtcNow() });
        await history.CompleteAsync(current with
        {
            Status = status,
            CompletedAt = clock.GetUtcNow(),
            Evidence = evidence,
            Limitations = current.Limitations.Append(reason).Distinct(StringComparer.Ordinal).ToArray(),
        }, CancellationToken.None);
    }

    private async Task<ActiveEventRunResult> SaveBlockedAsync(Prepared setup, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var result = new ActiveEventRunResult
        {
            RunId = Guid.NewGuid(), Scenario = setup.Descriptor, Target = setup.Target,
            SourceContract = setup.Preparation.Contract, Status = ActiveEventRunStatus.SafetyBlocked,
            StartedAt = now, CompletedAt = now,
            Evidence = [new() { Stage = ActiveEventEvidenceStage.Generated, Status = ActiveEventEvidenceStatus.SafetyBlocked,
                Detail = reason, EvidenceSource = "Active Event safety policy", CapturedAt = now }],
            Limitations = [reason, "Nothing was generated or sent."],
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
            throw new ActiveEventRequestException("No applicable registered provider owns the requested scenario for this integration.");
        var descriptor = scenarios.Scenarios(context).Single(item => item.ScenarioId == scenarioId && item.ExtensionId == extensionId);
        var trusted = environments.Resolve(environmentId, out var trustReason);
        var checks = new List<ActiveEventReadinessCheck>();
        void Add(string key, string label, ActiveEventReadinessState state, string detail, ActiveEventReadinessCategory category) => checks.Add(new(key, label, state, detail, category));

        Add("enabled", "Active event execution", policy.Options.Enabled ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.NotConfigured,
            policy.Options.Enabled ? "Enabled by backend policy." : "Disabled by backend policy (ActiveEventTesting:Enabled is not true).", ActiveEventReadinessCategory.Execution);

        var environmentProblem = trusted is null ? trustReason
            : !trusted.ExecutionAllowed ? "The trusted environment does not allow active execution (ExecutionAllowed is false)."
            : ActiveEventPolicy.EnvironmentBlock(trusted.EnvironmentType);
        Add("environment", "Trusted environment (DEV/QA only)", environmentProblem is null ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
            environmentProblem ?? $"{trusted!.DisplayName}: backend-owned {trusted.EnvironmentType} environment.", ActiveEventReadinessCategory.TrustedEnvironment);

        var integration = context.Integration;
        var platform = context.Platform;
        var integrationProblem = integration is null || !integration.Enabled ? "The selected integration is missing or disabled."
            : !string.Equals(integration.Kind.ToString(), descriptor.RequiredIntegrationType, StringComparison.OrdinalIgnoreCase) ? "The integration type does not satisfy the scenario."
            : platform is null ? "The integration has no configured platform."
            : trusted is not null && !trusted.IntegrationIds.Contains(integration.Id, StringComparer.Ordinal)
                ? "The integration is not enrolled for active execution in the trusted environment (TargetEnvironments:Trusted:IntegrationIds)."
            : null;
        var resource = integration?.EndpointOrTopic?.Trim();
        var endpoint = platform?.NamespaceFqdn?.Trim();
        if (integrationProblem is null && (string.IsNullOrWhiteSpace(resource) || string.IsNullOrWhiteSpace(endpoint))) integrationProblem = "The trusted integration destination is incomplete.";
        Add("integration", "Integration configuration (IQR)", integrationProblem is null ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
            integrationProblem ?? $"{integration!.DisplayName}: source {integration.SourceResource ?? "not set"} → {resource} on {endpoint}.", ActiveEventReadinessCategory.Integration);

        var (approved, destinationReason) = integrationProblem is null && environmentProblem is null
            ? policy.Approve(trusted!.EnvironmentType, endpoint, resource, trusted.TargetUrl)
            : (null, environmentProblem ?? integrationProblem!);
        var (consumer, assumed) = integration is null ? (null, false) : IntegrationConfigurationRules.EffectiveConsumerGroup(integration, platform);
        var target = new ActiveEventTrustedTarget
        {
            TargetEnvironmentId = environmentId,
            EnvironmentDisplayName = trusted?.DisplayName ?? "Untrusted environment",
            EnvironmentType = trusted?.EnvironmentType ?? "Unknown",
            TargetUrl = trusted?.TargetUrl,
            IntegrationId = integrationId,
            IntegrationDisplayName = integration?.DisplayName ?? integrationId,
            IntegrationType = integration?.Kind.ToString() ?? "",
            TransportType = descriptor.RequiredTransportType,
            Endpoint = endpoint,
            Resource = resource,
            Consumer = consumer,
            ConsumerAssumed = assumed,
            ConsumerRole = integration?.Consumer.ContainerApp,
            SourceResource = integration?.SourceResource,
            SafeMetadata = new Dictionary<string, string> { ["destinationApproval"] = approved is null ? "blocked" : "approved" },
        };
        Add("destination", "Enrolled destination", approved is null ? ActiveEventReadinessState.Blocked : ActiveEventReadinessState.Ready,
            approved is null ? destinationReason : $"{destinationReason} Resolved from the enabled integration; the client cannot choose it.", ActiveEventReadinessCategory.Destination);

        var transport = transports.Resolve(descriptor.RequiredTransportType);
        var transportReason = "";
        var transportReady = transport is not null && transport.CanSend(target, out transportReason);
        Add("transport", "Transport provider", transport is null ? ActiveEventReadinessState.NotAvailable : transportReady ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
            transport is null ? $"No provider is registered for {descriptor.RequiredTransportType}." : transportReady ? "Registered provider is ready (instance identity; no connection string)." : transportReason,
            ActiveEventReadinessCategory.Transport);

        if (descriptor.Support != ActiveEventScenarioSupport.Supported)
            Add("support", "Scenario support", ActiveEventReadinessState.NotAvailable, descriptor.SupportDetail.Length > 0 ? descriptor.SupportDetail : "This scenario is declared but not assessed.",
                ActiveEventReadinessCategory.Scenario);

        var preparation = await provider.PrepareAsync(scenarioId, context, snapshotId, ct);
        // Provider checks are appended: they can add blocks, they can never replace or relax a core check.
        foreach (var check in preparation.Checks.Where(check => checks.All(existing => existing.Key != check.Key)))
            checks.Add(check);
        if (!preparation.Compatible && !preparation.Checks.Any(check => ActiveEventReadinessRules.Blocks(check.State)))
            Add("provider", "Scenario prerequisites", ActiveEventReadinessState.Blocked, preparation.Detail.Length > 0 ? preparation.Detail : "The provider reported unmet prerequisites.",
                ActiveEventReadinessCategory.Scenario);

        var running = coordinator.LeaseHolder(ActiveEventRunCoordinator.Key(environmentId, integrationId));
        Add("concurrency", "No run in flight", running is null ? ActiveEventReadinessState.Ready : ActiveEventReadinessState.Blocked,
            running is null ? "One run per integration at a time." : $"Run {running:N} is in flight for this integration.", ActiveEventReadinessCategory.Execution);

        return new(provider, descriptor, target, preparation, checks, context);
    }

    private async Task<ActiveEventProjectEvidenceContext> ContextAsync(string environmentId, string integrationId, CancellationToken ct)
    {
        var configured = await CatalogAsync(environmentId, ct);
        var integration = configured.Integrations.FirstOrDefault(item => item.Id == integrationId);
        var platform = integration is null ? null : configured.Platforms.FirstOrDefault(item => item.Id == integration.PlatformId);
        return new(environmentId, integration, platform);
    }

    /// <summary>The IQR catalog for the environment, read with the backend-owned trusted type/URL (never the caller's).</summary>
    private Task<IntegrationCatalog> CatalogAsync(string environmentId, CancellationToken ct)
    {
        var trusted = environments.Resolve(environmentId, out _);
        return catalog.GetAsync(environmentId, trusted?.EnvironmentType, trusted?.TargetUrl, ct);
    }

    private sealed record Prepared(IActiveEventScenarioProvider Provider, ActiveEventScenarioDescriptor Descriptor,
        ActiveEventTrustedTarget Target, ActiveEventScenarioPreparation Preparation, List<ActiveEventReadinessCheck> Checks, ActiveEventProjectEvidenceContext Context);
}
