using Path = System.IO.Path;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using BirkNext.Applicability;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>Resolves the provider for a definition by stable id.</summary>
public sealed class PerformanceTestProviderRegistry(IEnumerable<IPerformanceTestProvider> providers)
{
    private readonly Dictionary<string, IPerformanceTestProvider> _byId = providers.ToDictionary(p => p.ProviderId, StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, PerformanceProviderStatus Status)> _status = new();
    private readonly ConcurrentDictionary<string, PerformanceReachability> _reachability = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last container-network check for a provider + target origin.</summary>
    public PerformanceReachability? LastReachability(string providerId, string origin) => _reachability.GetValueOrDefault($"{providerId}|{origin}");

    /// <summary>Local data reset: forgets per-target reachability checks. Provider/runtime status (host capability) is kept.</summary>
    public void ClearReachability() => _reachability.Clear();

    public async Task<PerformanceReachability> CheckReachabilityAsync(PerformanceTestDefinition d, CancellationToken ct = default)
    {
        if (Find(d.ProviderId) is not { } provider)
            return new PerformanceReachability { TargetOrigin = d.TargetOrigin, State = "Unknown", Detail = $"No provider '{d.ProviderId}' is registered.", CheckedAt = DateTimeOffset.UtcNow };
        var result = await provider.CheckReachabilityAsync(d, ct);
        _reachability[$"{d.ProviderId}|{d.TargetOrigin}"] = result;
        return result;
    }

    public void InvalidateStatus(string providerId) => _status.TryRemove(providerId, out _);

    public IReadOnlyCollection<IPerformanceTestProvider> All => _byId.Values;
    public IPerformanceTestProvider? Find(string providerId) => _byId.GetValueOrDefault(providerId);

    /// <summary>Provider status, cached for 30 s (detection starts a process).</summary>
    public async Task<PerformanceProviderStatus> StatusAsync(string providerId, CancellationToken ct = default)
    {
        if (Find(providerId) is not { } provider)
            return new PerformanceProviderStatus { ProviderId = providerId, DisplayName = providerId, Availability = ProviderAvailability.Unavailable, Detail = $"No performance test provider '{providerId}' is registered." };
        if (_status.TryGetValue(providerId, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(30)) return cached.Status;
        var status = await provider.StatusAsync(ct);
        _status[providerId] = (DateTimeOffset.UtcNow, status);
        return status;
    }
}

/// <summary>
/// Readiness of a definition: every prerequisite with an exact, tester-friendly reason. Never "Fail" — nothing has run. Thresholds, baseline
/// and observability are optional: without thresholds a run is measured, not assessed.
/// </summary>
public sealed class PerformanceTestReadinessService(PerformanceTestOptions options, PerformanceTestProviderRegistry providers, Resources.ResourceObservationRegistry? resources = null)
{
    /// <summary>Resource Stability readiness: provider and target status, policy, and whether the planned duration can give a steady-state trend at all.
    /// Never blocking unless the definition explicitly requires resource evidence — telemetry does not gate the load test.</summary>
    private async Task<List<PerformanceReadinessItem>> ResourceItemsAsync(PerformanceTestDefinition d, CancellationToken ct)
    {
        if (d.ResourceObservation is not { Enabled: true } r)
            return [new("resources", "Resource observation", PerformanceReadinessState.Optional, "Not configured — enable it to collect Resource Stability evidence (preferably with a soak workload).", false)];
        var items = new List<PerformanceReadinessItem>();
        if (resources is null) return [new("resources", "Resource observation", PerformanceReadinessState.Optional, "Resource observation providers are not available in this host.", false)];
        var statuses = await resources.TargetStatusAsync(d.EnvironmentId, d.EnvironmentType, ct);
        var selected = r.TargetComponentIds.Select(id => statuses.FirstOrDefault(s => s.Target.Id == id)).OfType<ResourceTargetStatus>().ToList();
        var allReady = selected.Count > 0 && selected.All(s => s.Availability == "Available");
        var anyUsable = selected.Any(s => s.Availability is "Available" or "Partial");
        var detail = selected.Count == 0 ? "No application component selected." : string.Join(" ", selected.Select(s => $"{s.Target.DisplayName}: {s.Availability} — {s.Detail}".Trim()));
        if (r.ObserveLoadGenerator) detail += " k6 load generator: observed separately as generator health.";
        items.Add(r.RequireResourceEvidence && !anyUsable
            ? new("resources", "Resource observation", PerformanceReadinessState.ProviderUnavailable, detail + " The definition requires resource evidence.", true)
            : new("resources", "Resource observation", allReady ? PerformanceReadinessState.Ready : PerformanceReadinessState.Optional,
                (allReady ? "" : "Resource evidence will be partial. ") + detail, false));
        var warmup = r.WarmupExclusionSeconds ?? d.Workload.WarmupSeconds + d.Workload.RampUpSeconds;
        var steady = Math.Max(0, d.Workload.TotalSeconds - warmup);
        var expected = r.SampleIntervalSeconds <= 0 ? 0 : steady / r.SampleIntervalSeconds;
        items.Add(expected < Resources.ResourceStabilityAnalyzer.MinimumSteadySamples
            ? new("resource-evidence", "Resource evidence duration", PerformanceReadinessState.Optional,
                $"About {expected} steady-state sample(s) after the {warmup} s warm-up exclusion: too few for a trend, so the run will report Insufficient evidence. Longer controlled runs (soak) give stronger resource-stability evidence.", false)
            : new("resource-evidence", "Resource evidence duration", PerformanceReadinessState.Ready,
                $"About {expected} steady-state samples over {(steady / 60.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} min every {r.SampleIntervalSeconds} s (warm-up {warmup} s excluded{(r.CooldownSeconds > 0 ? $", cooldown {r.CooldownSeconds} s observed" : "")}). {(d.Workload.Purpose == WorkloadPurpose.Soak ? "" : "A soak workload gives stronger evidence. ")}Short runs never prove that resources are stable or leaking.", false));
        items.Add(r.Policies.Count == 0
            ? new("resource-policy", "Resource policy", PerformanceReadinessState.Optional, "No resource policy: trends are shown descriptively (never passed or failed).", false)
            : new("resource-policy", "Resource policy", PerformanceReadinessState.Ready, $"{r.Policies.Count} stability polic{(r.Policies.Count == 1 ? "y" : "ies")}, {r.DriftPolicies.Count} drift polic{(r.DriftPolicies.Count == 1 ? "y" : "ies")}.", false));
        return items;
    }

    public async Task<PerformanceTestReadiness> EvaluateAsync(PerformanceTestDefinition d, PerformanceTestDataProfile? data, bool hasActiveBaseline, CancellationToken ct = default)
    {
        var issues = PerformanceTestSafety.Check(d, data, options);
        var provider = await providers.StatusAsync(d.ProviderId, ct);
        var providerIssues = providers.Find(d.ProviderId)?.Validate(d) ?? [];
        var items = new List<PerformanceReadinessItem>();
        void Item(string key, string label, IEnumerable<PerformanceTestSafety.Issue> keyed, string okDetail, PerformanceReadinessState okState = PerformanceReadinessState.Ready)
        {
            var found = keyed.ToList();
            items.Add(found.Count == 0 ? new(key, label, okState, okDetail, false) : new(key, label, found[0].State, string.Join(" ", found.Select(f => f.Message)), true));
        }
        IEnumerable<PerformanceTestSafety.Issue> By(params string[] keys) => issues.Where(i => keys.Contains(i.Key));

        Item("target", "Target", By("target"), $"{d.TargetOrigin} (from the Target Environment)");
        Item("environment", "Environment and production guard", By("environment"), $"{d.EnvironmentName} ({d.EnvironmentType}) — a non-production environment; production is refused by the backend.");
        // Runtime, image and provider: three separate tool states. None of them is a performance result.
        var runtime = provider.Runtime;
        if (runtime is not null)
            items.Add(runtime.Availability == ProviderAvailability.Available
                ? new("runtime", "Execution runtime", PerformanceReadinessState.Ready, runtime.Detail, false)
                : new("runtime", "Execution runtime", PerformanceReadinessState.RuntimeUnavailable, $"{runtime.DisplayName}: {runtime.Detail}", true));
        if (provider.Image is not null && runtime?.Availability == ProviderAvailability.Available)
            items.Add(provider.ImagePresent
                ? new("image", "Provider image", PerformanceReadinessState.Ready, provider.ImageDigest is null ? provider.Image : $"{provider.Image} ({provider.ImageDigest[..Math.Min(19, provider.ImageDigest.Length)]}...)", false)
                : new("image", "Provider image", PerformanceReadinessState.ImageMissing, provider.Detail, true));
        var toolReady = provider.Availability == ProviderAvailability.Available;
        if (provider.Availability is not (ProviderAvailability.RuntimeUnavailable or ProviderAvailability.ImageMissing) || providerIssues.Count > 0)
            items.Add(toolReady && providerIssues.Count == 0
                ? new("provider", "Provider", PerformanceReadinessState.Ready, provider.Detail, false)
                : new("provider", "Provider", PerformanceReadinessState.ProviderUnavailable, string.Join(" ", new[] { toolReady ? null : provider.Detail }.Concat(providerIssues).Where(t => t is not null)), true));
        // Container-network reachability: the load generator's own network, not the BirkNext host's (VPN, corporate DNS, private routes).
        var reach = providers.LastReachability(d.ProviderId, d.TargetOrigin);
        var fresh = reach is not null && DateTimeOffset.UtcNow - reach.CheckedAt < TimeSpan.FromMinutes(options.Container.NetworkCheckValidMinutes);
        items.Add(!toolReady
            ? new("network", "Container network", PerformanceReadinessState.Optional, "Checked once the provider is available.", false)
            : reach is null || !fresh
                ? new("network", "Container network", PerformanceReadinessState.NeedsConfiguration, "Not checked yet: check that the k6 container can reach the target (one request). Host reachability does not prove container reachability.", true)
                : reach.Reachable
                    ? new("network", "Container network", PerformanceReadinessState.Ready, $"{reach.Detail} Checked {reach.CheckedAt.ToUniversalTime():HH:mm} UTC.", false)
                    : new("network", "Container network", PerformanceReadinessState.NetworkUnavailable, reach.Detail, true));
        items.Add(new("tls", "TLS and proxy", PerformanceReadinessState.Ready,
            (options.Container.CaBundlePath is null ? "System CA roots; TLS verification is always on." : "Configured CA bundle (read-only); TLS verification is always on.")
            + (options.Container.HttpsProxy is null && options.Container.HttpProxy is null ? " No proxy." : " Allow-listed proxy settings only."), false));
        Item("scenario", "Scenario", By("scenario", "step-name", "method", "path", "header", "status", "think", "body", "graphql"),
            $"{d.Scenario.Steps.Count} request step(s), read-only methods{(d.TargetType == PerformanceTargetType.GraphQlHttp ? ", GraphQL query only" : "")}.");
        Item("authentication", "Authentication", By("authentication"), "None — the target is tested without authentication.");
        Item("testdata", "Test data", By("testdata", "placeholder"), d.Scenario.TestDataProfileId is null ? "Not used." : $"Approved synthetic profile ({data?.Rows.Count ?? 0} rows).",
            d.Scenario.TestDataProfileId is null ? PerformanceReadinessState.Optional : PerformanceReadinessState.Ready);
        Item("workload", "Workload", By("workload"), $"{d.Workload.Purpose}, {d.Workload.Mode}.");
        Item("limits", "Safety limits", By("limit-duration", "limit-purpose", "limit-vus", "limit-rate", "limit-requests"), "Within the configured safety limits.");
        items.Add(d.Thresholds.Count == 0
            ? new("thresholds", "Thresholds", PerformanceReadinessState.Optional, "No thresholds: the run will be measured, not assessed (no quality score).", false)
            : new("thresholds", "Thresholds", PerformanceReadinessState.Ready, $"{d.Thresholds.Count(t => t.Severity == ThresholdSeverity.Required)} required, {d.Thresholds.Count(t => t.Severity == ThresholdSeverity.Advisory)} advisory.", false));
        items.Add(new("baseline", "Baseline", PerformanceReadinessState.Optional, hasActiveBaseline ? "An active baseline exists for this scope; drift will be assessed against it." : "Optional — no baseline selected.", false));
        items.Add(new("observability", "Observability", PerformanceReadinessState.Optional, "Optional — not configured. The run window is recorded so telemetry can be correlated later.", false));
        if (d.ResourceObservation is { Enabled: true }) Item("resource-config", "Resource configuration", By("resources"), "Resource observation settings are valid.");
        items.AddRange(await ResourceItemsAsync(d, ct));
        var blockers = items.Where(i => i.Blocking).Select(i => i.Detail).ToList();
        return new PerformanceTestReadiness
        {
            Ready = blockers.Count == 0, Items = items, Blockers = blockers, Limits = options.Limits(d.SafetyPolicy), EstimatedDurationSeconds = d.Workload.TotalSeconds,
            EstimatedMaxRequests = PerformanceTestSafety.EstimatedMaxRequests(d), Timeline = PerformanceTestRules.Timeline(d.Workload), Provider = provider,
        };
    }
}

public sealed record PerformanceRunStartResult(PerformanceTestRun? Run, List<string> Blockers, string? Conflict);

/// <summary>
/// Executes performance tests: re-validates readiness and safety on the backend, allows one active run per environment, runs the provider in
/// the background, honours cancellation and timeout, evaluates BirkNext thresholds and drift, and persists the immutable run.
/// </summary>
public sealed class PerformanceTestExecutionService(IServiceScopeFactory scopes, PerformanceTestOptions options, PerformanceTestProviderRegistry providers,
    ILogger<PerformanceTestExecutionService> logger, TimeProvider? clock = null, Resources.ResourceObservationRegistry? resources = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, Task> _tasks = new();

    /// <summary>Cancels every active run and waits (bounded) for each to stop its provider container and resource collectors. Used at API shutdown.</summary>
    public async Task CancelAllAsync(TimeSpan wait)
    {
        foreach (var cts in _active.Values) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
        var tasks = _tasks.Values.ToArray();
        if (tasks.Length > 0) { try { await Task.WhenAll(tasks).WaitAsync(wait); } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { } }
    }

    /// <summary>Starts resource collection for the run (null when not configured). Approved targets only; the k6 container is load-generator health.</summary>
    private Resources.ResourceObservationSession? StartResourceSession(PerformanceTestRun run, Func<ResourceStabilityAssessment, Task> persist)
    {
        if (resources is null || run.DefinitionSnapshot.ResourceObservation is not { Enabled: true } config) return null;
        var d = run.DefinitionSnapshot;
        var components = config.TargetComponentIds.Distinct(StringComparer.Ordinal)
            .Select(id => resources.Resolve(id, d.EnvironmentId, d.EnvironmentType)).OfType<Resources.ResourceTargetSpec>()
            .Select(spec => (spec, resources.Provider(spec.Target.ProviderId))).ToList();
        if (config.ObserveLoadGenerator)
        {
            var generator = Resources.ResourceObservationRegistry.LoadGenerator(run.RunId);
            components.Add((generator, resources.Provider(generator.Target.ProviderId)));
        }
        var o = resources.Options;
        var interval = TimeSpan.FromSeconds(Math.Clamp(config.SampleIntervalSeconds, o.MinSampleIntervalSeconds, o.MaxSampleIntervalSeconds));
        var session = new Resources.ResourceObservationSession(run.RunId, components, interval, o.MaxSamplesPerComponent, logger) { OnRound = persist };
        session.Start();
        return session;
    }

    /// <summary>Assessment of the collected evidence over the run's windows (warm-up excluded, cooldown separate).</summary>
    private ResourceStabilityAssessment? AssessResources(PerformanceTestRun run, Resources.ResourceObservationSession? session, DateTimeOffset workloadStart, DateTimeOffset workloadEnd)
    {
        if (session is null || run.DefinitionSnapshot.ResourceObservation is not { } config) return null;
        var w = run.DefinitionSnapshot.Workload;
        var warmup = Math.Max(0, config.WarmupExclusionSeconds ?? w.WarmupSeconds + w.RampUpSeconds);
        var windows = new Resources.ResourceWindows(workloadStart, workloadEnd, warmup, Math.Max(0, config.CooldownSeconds));
        return Resources.ResourceStabilityAnalyzer.Assess(session.Snapshot(inProgress: false) with
        {
            PolicyFingerprint = PerformanceTestStore.Fingerprint(new { config.Policies, config.DriftPolicies, warmup, config.SampleIntervalSeconds }),
        }, config, windows);
    }
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _active = new();

    /// <summary>True while a run is executing in this process (the reset refuses to run under an active writer).</summary>
    public bool HasActiveRuns => !_active.IsEmpty;
    private readonly ConcurrentDictionary<string, Guid> _activeByEnvironment = new(StringComparer.Ordinal);
    private int _orphansCleaned;

    /// <summary>Removes stale BirkNext-managed provider containers (labels only; never other containers). Once per process, and on demand.</summary>
    public async Task<IReadOnlyList<string>> CleanupOrphansAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && Interlocked.Exchange(ref _orphansCleaned, 1) == 1) return [];
        var removed = new List<string>();
        var active = _active.Keys.ToHashSet();
        foreach (var provider in providers.All)
        {
            try { removed.AddRange(await provider.CleanupOrphansAsync(active, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning("Orphan cleanup failed for {Provider}: {Type}", provider.ProviderId, ex.GetType().Name); }
        }
        return removed;
    }

    /// <summary>The background task of the last started run (tests await it).</summary>
    internal Task? LastExecution { get; private set; }

    public async Task<PerformanceRunStartResult> StartAsync(string environmentId, PerformanceRunRequest request, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PerformanceTestStore>();
        var readinessService = scope.ServiceProvider.GetRequiredService<PerformanceTestReadinessService>();
        var definition = await store.DefinitionAsync(environmentId, request.DefinitionId, ct);
        if (definition is null) return new(null, ["The performance test definition does not exist in this environment."], null);
        if (definition.Archived) return new(null, ["The definition is archived; restore or copy it to run."], null);
        var data = await store.DataProfileAsync(environmentId, definition.Scenario.TestDataProfileId, ct);
        var baseline = await store.ActiveBaselineAsync(environmentId, definition.Id, definition.ComparisonFingerprint, ct);
        var readiness = await readinessService.EvaluateAsync(definition, data, baseline is not null, ct);
        if (!readiness.Ready) return new(null, readiness.Blockers, null);
        await ReconcileAsync(store, environmentId, ct);
        await CleanupOrphansAsync(false, ct);
        if (_activeByEnvironment.ContainsKey(environmentId) || await store.HasActiveRunAsync(environmentId, ct))
            return new(null, [], "A performance test is already running for this environment. Wait for it to finish or cancel it.");

        var now = _clock.GetUtcNow();
        Guid? sourceSnapshot = null;
        try { sourceSnapshot = (await scope.ServiceProvider.GetRequiredService<Integrations.SourceEvidence.IqrSourceStore>().ListSourceAnalysisAsync(1, ct)).FirstOrDefault()?.Id; }
        catch (InvalidOperationException) { }
        var run = new PerformanceTestRun
        {
            EnvironmentId = environmentId, ProjectId = definition.ProjectId, DefinitionId = definition.Id, DefinitionVersion = definition.Version,
            DefinitionFingerprint = definition.Fingerprint, ComparisonFingerprint = definition.ComparisonFingerprint, DefinitionSnapshot = definition,
            ProviderId = definition.ProviderId, ProviderVersion = readiness.Provider?.Version, TargetOrigin = definition.TargetOrigin, EnvironmentType = definition.EnvironmentType,
            RuntimeId = readiness.Provider?.Runtime?.RuntimeId, RuntimeVersion = readiness.Provider?.Runtime?.Version, ContainerImage = readiness.Provider?.Image, ImageDigest = readiness.Provider?.ImageDigest,
            CreatedAt = now, State = PerformanceRunState.Queued, BaselineIdAtRun = baseline?.BaselineId,
            VersionLabel = string.IsNullOrWhiteSpace(request.VersionLabel) ? null : request.VersionLabel.Trim()[..Math.Min(100, request.VersionLabel.Trim().Length)],
            SourceSnapshotId = sourceSnapshot,
            Host = new PerformanceExecutionHost(RuntimeInformation.OSDescription, Environment.ProcessorCount, typeof(PerformanceTestExecutionService).Assembly.GetName().Version?.ToString() ?? "unknown"),
        };
        if (!_activeByEnvironment.TryAdd(environmentId, run.RunId)) return new(null, [], "A performance test is already running for this environment.");
        try { await store.SaveRunAsync(run, ct); }
        catch { _activeByEnvironment.TryRemove(environmentId, out _); throw; }
        var cts = new CancellationTokenSource();
        _active[run.RunId] = cts;
        LastExecution = Task.Run(() => ExecuteAsync(run, data, cts.Token));
        _tasks[run.RunId] = LastExecution;
        return new(run, [], null);
    }

    public async Task<PerformanceTestRun?> CancelAsync(Guid runId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<PerformanceTestStore>();
        var run = await store.RunAsync(runId, ct);
        if (run is null) return null;
        if (!run.IsActive) return run;
        if (_active.TryGetValue(runId, out var cts))
        {
            var cancelling = run with { State = PerformanceRunState.Cancelling, StateReason = "Cancellation requested." };
            await store.SaveRunAsync(cancelling, ct);
            cts.Cancel();
            return cancelling;
        }
        return await ReconcileRunAsync(store, run, ct);
    }

    /// <summary>A run left active by a BirkNext restart can never finish: it becomes ExecutionFailed (not a quality result).</summary>
    public async Task ReconcileAsync(PerformanceTestStore store, string environmentId, CancellationToken ct = default)
    {
        foreach (var run in (await store.RunsAsync(environmentId, null, 20, ct)).Where(r => r.IsActive && !_active.ContainsKey(r.RunId)))
            await ReconcileRunAsync(store, run, ct);
    }

    private async Task<PerformanceTestRun> ReconcileRunAsync(PerformanceTestStore store, PerformanceTestRun run, CancellationToken ct)
    {
        if (_active.ContainsKey(run.RunId)) return run;
        var failed = run with { State = PerformanceRunState.ExecutionFailed, StateReason = "BirkNext stopped while the run was active; the provider result is unknown.", FinishedAt = _clock.GetUtcNow(),
            ThresholdResults = PerformanceTestRules.EvaluateThresholds(run.DefinitionSnapshot.Thresholds, null, false) };
        return await store.SaveRunAsync(failed, ct);
    }

    private async Task ExecuteAsync(PerformanceTestRun queued, PerformanceTestDataProfile? data, CancellationToken ct)
    {
        var run = queued;
        Resources.ResourceObservationSession? session = null;
        DateTimeOffset? workloadStart = null, workloadEnd = null;
        try
        {
            using var scope = scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<PerformanceTestStore>();
            run = await store.SaveRunAsync(run with { State = PerformanceRunState.Preparing }, CancellationToken.None);
            // Safety re-checked immediately before the provider starts (the definition snapshot, not whatever the UI shows).
            var blockers = PerformanceTestSafety.Check(run.DefinitionSnapshot, data, options);
            if (blockers.Count > 0)
            {
                await store.SaveRunAsync(run with { State = PerformanceRunState.Blocked, StateReason = string.Join(" ", blockers.Select(b => b.Message)), FinishedAt = _clock.GetUtcNow(),
                    ThresholdResults = PerformanceTestRules.EvaluateThresholds(run.DefinitionSnapshot.Thresholds, null, false) }, CancellationToken.None);
                return;
            }
            var provider = providers.Find(run.ProviderId)!;
            // A fresh container-network check immediately before the load starts: an unreachable target is Blocked (a precondition), never a result.
            var reach = await providers.CheckReachabilityAsync(run.DefinitionSnapshot, ct);
            if (!reach.Reachable)
            {
                await store.SaveRunAsync(run with { State = PerformanceRunState.Blocked, StateReason = reach.Detail, FinishedAt = _clock.GetUtcNow(), Reachability = reach,
                    ThresholdResults = PerformanceTestRules.EvaluateThresholds(run.DefinitionSnapshot.Thresholds, null, false) }, CancellationToken.None);
                return;
            }
            run = await store.SaveRunAsync(run with { State = PerformanceRunState.Running, StartedAt = _clock.GetUtcNow(), Reachability = reach }, CancellationToken.None);
            // Resource observation starts with the workload; partial evidence is persisted after each round so it survives a cancel or a restart.
            var running = run;
            session = StartResourceSession(run, async snapshot =>
            {
                using var persistScope = scopes.CreateScope();
                await persistScope.ServiceProvider.GetRequiredService<PerformanceTestStore>().SaveRunAsync(running with { Resources = snapshot }, CancellationToken.None);
            });
            workloadStart = run.StartedAt;
            var timeout = TimeSpan.FromSeconds(run.DefinitionSnapshot.Workload.TotalSeconds + options.ProviderTimeoutGraceSeconds);
            var workDir = Path.Combine(Path.GetTempPath(), "birknext-performance", run.RunId.ToString("N"));
            var result = await provider.ExecuteAsync(new PerformanceProviderInput(run.RunId, run.DefinitionSnapshot, data, workDir, timeout), null, ct);
            workloadEnd = _clock.GetUtcNow();
            // Optional post-load cooldown: does the component return toward its steady state once the load stops? Cancelling here keeps the result.
            if (session is not null && result.State == PerformanceRunState.Completed && run.DefinitionSnapshot.ResourceObservation?.CooldownSeconds is > 0 and var cooldown)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(cooldown, resources!.Options.MaxCooldownSeconds)), ct); }
                catch (OperationCanceledException) { }
            }
            if (session is not null) await session.StopAsync();
            run = await FinishAsync(store, run, result, AssessResources(run, session, workloadStart.Value, workloadEnd.Value));
            logger.LogInformation("Performance test {RunId} for {EnvironmentId}: {State}, verdict {Verdict}, {Requests} request(s).", run.RunId, run.EnvironmentId, run.State, run.Verdict, run.Metrics?.RequestCount ?? 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (session is not null) await session.StopAsync();
            using var scope = scopes.CreateScope();
            await FinishAsync(scope.ServiceProvider.GetRequiredService<PerformanceTestStore>(), run,
                new PerformanceProviderResult { State = PerformanceRunState.Cancelled, Reason = "Cancelled before the provider produced a result.", MetricsSource = run.ProviderId },
                workloadStart is { } ws ? AssessResources(run, session, ws, workloadEnd ?? _clock.GetUtcNow()) : null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError("Performance test {RunId} failed to execute: {Type}", run.RunId, ex.GetType().Name);
            try
            {
                if (session is not null) await session.StopAsync();
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<PerformanceTestStore>().SaveRunAsync(run with { State = PerformanceRunState.ExecutionFailed,
                    StateReason = $"The performance test could not be executed ({ex.GetType().Name}).", FinishedAt = _clock.GetUtcNow(),
                    Resources = workloadStart is { } ws ? AssessResources(run, session, ws, workloadEnd ?? _clock.GetUtcNow()) : null,
                    ThresholdResults = PerformanceTestRules.EvaluateThresholds(run.DefinitionSnapshot.Thresholds, null, false) }, CancellationToken.None);
            }
            catch (Exception inner) when (inner is not OutOfMemoryException) { logger.LogError("Performance test {RunId} result could not be stored: {Type}", run.RunId, inner.GetType().Name); }
        }
        finally
        {
            if (session is not null) await session.DisposeAsync();
            _tasks.TryRemove(queued.RunId, out _);
            if (_active.TryRemove(queued.RunId, out var cts)) cts.Dispose();
            _activeByEnvironment.TryRemove(new KeyValuePair<string, Guid>(queued.EnvironmentId, queued.RunId));
        }
    }

    /// <summary>Threshold evaluation (only a Completed run with complete metrics is assessed), shared-scoring quality, and drift against the
    /// baseline that was active when the run was created.</summary>
    internal async Task<PerformanceTestRun> FinishAsync(PerformanceTestStore store, PerformanceTestRun run, PerformanceProviderResult result, ResourceStabilityAssessment? resourceAssessment = null)
    {
        var assessable = result.State == PerformanceRunState.Completed && result.Metrics is not null && !result.MetricsPartial;
        var thresholds = PerformanceTestRules.EvaluateThresholds(run.DefinitionSnapshot.Thresholds, result.Metrics, assessable);
        // Shared scoring: evaluable resource policies count with the thresholds; resource evidence gaps are coverage, never a failure.
        var (quality, verdict) = PerformanceTestRules.Quality(thresholds, assessable ? Resources.ResourceStabilityAnalyzer.QualityOutcomes(resourceAssessment).ToList() : []);
        var limitations = new List<string>(result.Limitations);
        if (run.DefinitionSnapshot.Thresholds.Count == 0 && assessable) limitations.Add("No thresholds were configured: metrics are measured, not assessed (no quality score).");
        limitations.Add("Load-test metrics describe this workload from this load generator; they are not single-request API latency (API Quality Review) or browser performance (Frontend Quality Review).");
        limitations.Add("Observability enrichment is not configured; the run window is recorded for later telemetry correlation. Correlation is not root cause.");
        PerformanceDriftAssessment? drift = null;
        var finished = run with
        {
            State = result.State, StateReason = result.Reason, FinishedAt = _clock.GetUtcNow(), Metrics = result.Metrics, MetricsPartial = result.MetricsPartial,
            ThresholdResults = thresholds, Quality = quality, Verdict = verdict, ProviderVersion = result.ProviderVersion ?? run.ProviderVersion, MetricsSource = result.MetricsSource,
            RuntimeId = result.RuntimeId ?? run.RuntimeId, RuntimeVersion = result.RuntimeVersion ?? run.RuntimeVersion, ContainerImage = result.ContainerImage ?? run.ContainerImage,
            ImageDigest = result.ImageDigest ?? run.ImageDigest,
            ProviderDiagnostics = result.Diagnostics, Limitations = limitations, Resources = resourceAssessment,
        };
        ResourceDriftAssessment? resourceDrift = null;
        if (assessable && run.BaselineIdAtRun is { } baselineId && await store.BaselineAsync(baselineId) is { } baseline && await store.RunAsync(baseline.RunId) is { } baselineRun)
        {
            drift = PerformanceTestRules.Drift(finished, baselineRun, baseline, run.DefinitionSnapshot.DriftPolicies);
            resourceDrift = Resources.ResourceStabilityAnalyzer.Drift(finished, baselineRun, baseline.BaselineId, run.DefinitionSnapshot.ResourceObservation?.DriftPolicies ?? []);
        }
        return await store.SaveRunAsync(finished with { Drift = drift, ResourceDrift = resourceDrift }, CancellationToken.None);
    }
}
