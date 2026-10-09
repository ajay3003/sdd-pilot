using System.Collections.Concurrent;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.IntegrationJourneys;

public sealed class IntegrationJourneyRequestException(string message) : Exception(message);

public interface IIntegrationJourneyService
{
    Task<IReadOnlyList<IntegrationJourneyPackView>> PacksAsync(string environmentId, CancellationToken ct);
    Task<ArchitectureRuleReport> RulesAsync(string environmentId, string packId, Guid? snapshotId, CancellationToken ct);
    IReadOnlyList<IntegrationJourneyCatalogEntry> Catalog();
    Task<IntegrationJourneyRun> StartAsync(IntegrationJourneyRunRequest request, CancellationToken ct);
    Task<IntegrationJourneyRun?> GetAsync(Guid runId, CancellationToken ct);
    Task<IReadOnlyList<IntegrationJourneyRunSummary>> HistoryAsync(IntegrationJourneyHistoryQuery query, CancellationToken ct);
}

/// <summary>One in-flight run per environment and journey.</summary>
public sealed class IntegrationJourneyRunGate
{
    private readonly ConcurrentDictionary<string, Guid> _leases = new(StringComparer.Ordinal);
    public bool TryAcquire(string key, Guid runId) => _leases.TryAdd(key, runId);
    public void Release(string key, Guid runId) => _leases.TryRemove(new KeyValuePair<string, Guid>(key, runId));
}

/// <summary>
/// Generic journey lifecycle: readiness (trusted environment, IQR integrations, step coverage, pack prerequisites; delegated journeys reuse the
/// Active Event readiness of their provider), explicit confirmation, one run per journey, ordered execution through the shared runner, and
/// durable payload-free history. Safety is enforced here and cannot be relaxed by a pack: Production and unknown environments, untrusted
/// environments, unenrolled integrations, uncovered steps and unsupported scenarios never run.
/// </summary>
public sealed class IntegrationJourneyService(
    IIntegrationJourneyPackRegistry packs,
    IIntegrationCatalogService catalog,
    ITrustedExecutionEnvironmentRegistry environments,
    IReviewSourceEvidenceProvider sources,
    IActiveEventLifecycleService activeEvents,
    IntegrationJourneyRunner runner,
    IntegrationJourneyRunStore history,
    IntegrationJourneyRunGate gate,
    TimeProvider clock,
    ILogger<IntegrationJourneyService> logger) : IIntegrationJourneyService
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);
    /// <summary>Prerequisite keys of the journey's declared IQR integration requirements.</summary>
    public const string IntegrationKeyPrefix = "integration:";

    public async Task<IReadOnlyList<IntegrationJourneyPackView>> PacksAsync(string environmentId, CancellationToken ct)
    {
        var context = await ContextAsync(environmentId, ct);
        var views = new List<IntegrationJourneyPackView>();
        foreach (var pack in packs.Packs)
        {
            var journeys = new List<IntegrationJourneyView>();
            foreach (var journey in pack.Journeys.OrderBy(item => item.Order))
                journeys.Add(await ViewAsync(pack, journey, context, ct));
            views.Add(new IntegrationJourneyPackView
            {
                PackId = pack.PackId, PackVersion = pack.PackVersion, DisplayName = pack.DisplayName, Description = pack.Description,
                EnvironmentId = environmentId, EnvironmentTrusted = TrustProblem(context) is null, EnvironmentDetail = TrustProblem(context) ?? context.TrustDetail,
                Journeys = journeys, Limitations = pack.Limitations,
            });
        }
        return views;
    }

    public async Task<ArchitectureRuleReport> RulesAsync(string environmentId, string packId, Guid? snapshotId, CancellationToken ct)
    {
        var pack = packs.Find(packId) ?? throw new IntegrationJourneyRequestException("Unknown journey pack.");
        if (!sources.SourceAnalysisEnabled)
            return ArchitectureRuleEvaluator.Evaluate(pack.PackId, pack.Rules, null) with { Limitations = [ReviewSourceEvidenceProvider.Disabled] };
        IqrSourceSnapshot? snapshot;
        if (snapshotId is { } id)
            snapshot = await sources.ResolveAsync(environmentId, id, ct) ?? throw new IntegrationJourneyRequestException("The selected Source Analysis snapshot was not found.");
        else
            snapshot = (await sources.ListAsync(environmentId, ct)).FirstOrDefault(item => item.Architecture is not null);
        return ArchitectureRuleEvaluator.Evaluate(pack.PackId, pack.Rules, snapshot);
    }

    public IReadOnlyList<IntegrationJourneyCatalogEntry> Catalog() => packs.Packs.SelectMany(pack => pack.Journeys.OrderBy(journey => journey.Order).Select(journey =>
        new IntegrationJourneyCatalogEntry(pack.PackId, journey.JourneyId, journey.DisplayName, journey.Steps
            .Select(step => new JourneyEdge(step.StepId, step.Label, step.Kind, step.FromComponent ?? step.Owner, step.ToComponent ?? step.Owner, step.Owner)).ToArray()))).ToArray();

    public async Task<IntegrationJourneyRun> StartAsync(IntegrationJourneyRunRequest request, CancellationToken ct)
    {
        var pack = packs.Find(request.PackId) ?? throw new IntegrationJourneyRequestException("Unknown journey pack.");
        var journey = pack.Journeys.FirstOrDefault(item => item.JourneyId == request.JourneyId) ?? throw new IntegrationJourneyRequestException("Unknown journey.");
        var scenario = journey.Scenarios.FirstOrDefault(item => item.ScenarioId == request.ScenarioId) ?? throw new IntegrationJourneyRequestException("Unknown scenario.");
        if (journey.ExecutionMode == JourneyExecutionMode.DelegatedToActiveEvent)
            throw new IntegrationJourneyRequestException("This journey is executed by Active Event Testing; start it from that panel.");
        if (!request.Confirmed) throw new IntegrationJourneyRequestException("Confirm the journey run before execution.");

        var context = await ContextAsync(request.EnvironmentId, ct);
        var view = await ViewAsync(pack, journey, context, ct);
        var started = clock.GetUtcNow();
        var run = new IntegrationJourneyRun
        {
            RunId = Guid.NewGuid(), PackId = pack.PackId, JourneyId = journey.JourneyId, JourneyName = journey.DisplayName,
            ScenarioId = scenario.ScenarioId, ScenarioName = scenario.DisplayName, EnvironmentId = request.EnvironmentId,
            EnvironmentName = context.TrustedEnvironment?.DisplayName ?? request.EnvironmentId, StartedAt = started,
            Steps = journey.Steps.Select(step => new JourneyStepResult { StepId = step.StepId, Label = step.Label, State = JourneyStepState.NotAssessed, Reason = "Not started." }).ToArray(),
            Limitations = journey.Limitations,
        };

        var blockers = new List<string>();
        if (scenario.Support != JourneyScenarioSupport.Supported) blockers.Add(scenario.NotAssessedBecause ?? "The scenario is not supported.");
        if (!view.CanRun)
            blockers.AddRange(view.Prerequisites.Where(item => item.Mandatory && item.State != JourneyReadinessState.Ready).Select(item => $"{item.Label}: {item.Detail}"));
        if (TrustProblem(context) is { } trust) blockers.Add(trust);
        if (blockers.Count > 0) return await BlockedAsync(run, blockers.Distinct().ToArray(), ct);

        var key = $"{request.EnvironmentId}|{pack.PackId}|{journey.JourneyId}";
        if (!gate.TryAcquire(key, run.RunId)) return await BlockedAsync(run, ["Another run of this journey is in flight for this environment."], ct);
        try
        {
            if (!await history.InsertAsync(run, ct))
                return run with { OverallState = JourneyRunState.Blocked, CompletedAt = clock.GetUtcNow(), StateReason = "The run could not be recorded before execution; nothing was executed." };
            var integrations = Matches(pack, journey, context.Catalog).Where(item => item.Integration is { Enabled: true })
                .GroupBy(item => item.Requirement.Key).ToDictionary(group => group.Key, group => group.First().Integration!, StringComparer.Ordinal);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(RunTimeout);
            IntegrationJourneyRun completed;
            try
            {
                completed = await runner.ExecuteAsync(run, context.TrustedEnvironment!, journey, scenario, integrations, bounded.Token,
                    async (progress, token) => await history.UpdateAsync(progress, token));
            }
            catch (OperationCanceledException)
            {
                completed = run with { OverallState = JourneyRunState.Cancelled, CompletedAt = clock.GetUtcNow(), StateReason = "The run was cancelled or timed out; remaining steps were not executed and nothing was retried." };
            }
            catch (Exception ex)
            {
                logger.LogError("Integration journey run {RunId} stopped unexpectedly ({ExceptionType}).", run.RunId, ex.GetType().Name);
                completed = run with { OverallState = JourneyRunState.NotVerified, CompletedAt = clock.GetUtcNow(), StateReason = "Execution stopped unexpectedly; no step result is asserted beyond what was recorded." };
            }
            await history.UpdateAsync(completed, CancellationToken.None);
            return completed;
        }
        finally { gate.Release(key, run.RunId); }
    }

    public Task<IntegrationJourneyRun?> GetAsync(Guid runId, CancellationToken ct) => history.GetAsync(runId, ct);

    public async Task<IReadOnlyList<IntegrationJourneyRunSummary>> HistoryAsync(IntegrationJourneyHistoryQuery query, CancellationToken ct) =>
        (await history.ListAsync(query, 100, ct)).Select(Summary).ToArray();

    public static IntegrationJourneyRunSummary Summary(IntegrationJourneyRun run) => new(run.RunId, run.PackId, run.JourneyId, run.ScenarioId, run.EnvironmentId,
        run.OverallState, run.StartedAt, run.CompletedAt, run.Steps.Count(step => step.State == JourneyStepState.Verified), run.Steps.Count, run.Limitations.Count);

    // ── Readiness ────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<IntegrationJourneyView> ViewAsync(IIntegrationJourneyPack pack, IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct)
    {
        var prerequisites = new List<JourneyPrerequisite>();
        var trustProblem = TrustProblem(context);
        prerequisites.Add(new("trusted-environment", "Trusted non-production environment", JourneyPrerequisiteCategory.TrustedEnvironment,
            trustProblem is null ? JourneyReadinessState.Ready : JourneyReadinessState.NotReady, trustProblem ?? context.TrustDetail));

        var matches = Matches(pack, journey, context.Catalog);
        var matched = new List<JourneyMatchedIntegration>();
        foreach (var requirement in journey.Integrations)
        {
            var found = matches.Where(item => item.Requirement.Key == requirement.Key).Select(item => item.Integration!).ToArray();
            var enabled = found.Where(item => item.Enabled).ToArray();
            var enrolled = context.TrustedEnvironment is { } trusted ? enabled.Where(item => trusted.IntegrationIds.Contains(item.Id, StringComparer.Ordinal)).ToArray() : [];
            matched.AddRange(found.Length == 0
                ? [new JourneyMatchedIntegration(requirement.Key, null, null, requirement.Kind, false, $"No configured {requirement.Kind} integration was found for {requirement.Label}.")]
                : found.Select(item => new JourneyMatchedIntegration(requirement.Key, item.Id, item.DisplayName, item.Kind, item.Enabled,
                    !item.Enabled ? "Configured in Integration Quality Review but disabled."
                    : context.TrustedEnvironment?.IntegrationIds.Contains(item.Id, StringComparer.Ordinal) == true ? "Configured and enrolled in the trusted environment."
                    : "Configured in Integration Quality Review; not enrolled in the trusted environment.")));
            var (state, detail) = found.Length == 0
                ? (JourneyReadinessState.NotConfigured, $"No configured {requirement.Kind} integration was found for {requirement.Label} ({requirement.Producer} → {requirement.Consumer}). Configure it in Integration Quality Review.")
                : enabled.Length == 0 ? (JourneyReadinessState.NotReady, $"{requirement.Label}: the configured integration is disabled.")
                : enrolled.Length == 0 ? (JourneyReadinessState.NotReady, $"{requirement.Label}: configured, but not enrolled in the backend trusted environment (TargetEnvironments:Trusted IntegrationIds).")
                : (JourneyReadinessState.Ready, $"{requirement.Label}: {string.Join(", ", enrolled.Select(item => item.DisplayName))}.");
            prerequisites.Add(new(IntegrationKeyPrefix + requirement.Key, requirement.Label, JourneyPrerequisiteCategory.Integration, state, detail));
        }

        var stepStatus = new List<JourneyStepStatus>();
        if (journey.ExecutionMode == JourneyExecutionMode.DelegatedToActiveEvent)
        {
            prerequisites.AddRange(await DelegatedPrerequisitesAsync(journey, context, matches, ct));
            foreach (var step in journey.Steps)
            {
                var configured = step.IntegrationKey is { } key && matches.Any(item => item.Requirement.Key == key && item.Integration is { Enabled: true });
                stepStatus.Add(new(step.StepId, configured, false, false, configured ? JourneyStepState.Configured : JourneyStepState.NotAssessed,
                    "Evidence for this step comes from Active Event Testing runs (transport, consumer activity, continuity, downstream verifier)."));
            }
        }
        else
        {
            var uncovered = new List<string>();
            var firstExecutable = false;
            foreach (var (step, index) in journey.Steps.Select((step, index) => (step, index)))
            {
                var integration = step.IntegrationKey is { } key ? matches.FirstOrDefault(item => item.Requirement.Key == key && item.Integration is { Enabled: true }).Integration : null;
                var probe = new JourneyStepContext(Guid.Empty, context.EnvironmentId, null, journey, null, step, integration, []);
                var coverage = runner.Coverage(probe);
                if (index == 0) firstExecutable = coverage.Executable;
                if (step.Mandatory && !coverage.Executable && !coverage.Observable) uncovered.Add(step.Label);
                var configured = integration is not null;
                stepStatus.Add(new(step.StepId, configured, coverage.Observable || coverage.Executable, coverage.Observable,
                    configured ? JourneyStepState.Configured : JourneyStepState.NotAssessed, coverage.Reason));
            }
            prerequisites.Add(new("executor", "Journey trigger", JourneyPrerequisiteCategory.Executor,
                firstExecutable ? JourneyReadinessState.Ready : JourneyReadinessState.NotAvailable,
                firstExecutable ? "A registered executor can start the journey." : $"No registered executor can perform the first step (\"{journey.Steps.FirstOrDefault()?.Label}\"); the journey cannot be started."));
            prerequisites.Add(new("observation", "Step evidence coverage", JourneyPrerequisiteCategory.Observation,
                uncovered.Count == 0 ? JourneyReadinessState.Ready : JourneyReadinessState.NotAvailable,
                uncovered.Count == 0 ? "Every mandatory step has a registered executor or observer."
                    : $"{uncovered.Count} of {journey.Steps.Count(step => step.Mandatory)} mandatory steps have no executor or observer: {string.Join("; ", uncovered)}."));
        }

        prerequisites.AddRange(await pack.PrerequisitesAsync(journey, context, ct));
        var readiness = Readiness(prerequisites);
        var lastRun = (await history.ListAsync(new IntegrationJourneyHistoryQuery(context.EnvironmentId, pack.PackId, journey.JourneyId), 1, ct)).FirstOrDefault();
        var canRun = readiness == JourneyReadinessState.Ready && journey.ExecutionMode == JourneyExecutionMode.JourneyRunner
            && journey.Scenarios.Any(scenario => scenario.Support == JourneyScenarioSupport.Supported);
        var sourceItems = prerequisites.Where(item => item.Category is JourneyPrerequisiteCategory.SourceEvidence or JourneyPrerequisiteCategory.Contract).ToArray();
        var configuredAll = journey.Integrations.Count > 0 && prerequisites.Where(item => item.Key.StartsWith(IntegrationKeyPrefix, StringComparison.Ordinal)).All(item => item.State == JourneyReadinessState.Ready);
        var sourceVerified = sourceItems.Length > 0 && sourceItems.All(item => item.State == JourneyReadinessState.Ready);
        var runtimeVerified = lastRun?.OverallState == JourneyRunState.Completed;
        return new IntegrationJourneyView
        {
            Journey = journey, Readiness = readiness, Prerequisites = prerequisites, MatchedIntegrations = matched, StepStatus = stepStatus, CanRun = canRun,
            LastRun = lastRun is null ? null : Summary(lastRun),
            ReadinessSummary = readiness switch
            {
                JourneyReadinessState.Ready => "Ready to run after confirmation.",
                JourneyReadinessState.NotConfigured => "Not configured: a required integration is missing from Integration Quality Review.",
                _ => $"{prerequisites.Count(item => item.Mandatory && item.State == JourneyReadinessState.Ready)} of {prerequisites.Count(item => item.Mandatory)} mandatory prerequisites are ready.",
            },
            Maturity = new JourneyMaturity(configuredAll, sourceVerified, canRun, runtimeVerified,
                $"Configured: {(configuredAll ? "yes" : "no")} · Source verified: {(sourceVerified ? "yes" : "no")} · Executable: {(canRun ? "yes" : "no")} · Runtime verified: {(runtimeVerified ? "yes" : "no")}."),
        };
    }

    /// <summary>Delegated journeys reuse the Active Event readiness of their provider for each matched integration — no second readiness model.</summary>
    private async Task<IReadOnlyList<JourneyPrerequisite>> DelegatedPrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context,
        IReadOnlyList<(JourneyIntegrationRequirement Requirement, IntegrationDefinition? Integration)> matches, CancellationToken ct)
    {
        var result = new List<JourneyPrerequisite>();
        var provider = journey.DelegatedProviderId ?? "";
        var integrations = matches.Where(item => item.Integration is { Enabled: true }).Select(item => item.Integration!).DistinctBy(item => item.Id).ToArray();
        if (integrations.Length == 0)
        {
            result.Add(new("delegated", "Active Event readiness", JourneyPrerequisiteCategory.Executor, JourneyReadinessState.NotConfigured,
                $"Executed by Active Event Testing (provider {provider}); it has no applicable integration until one is configured."));
            return result;
        }
        foreach (var integration in integrations)
        {
            try
            {
                var scenario = (await activeEvents.ScenariosAsync(context.EnvironmentId, integration.Id, ct))
                    .FirstOrDefault(item => item.ExtensionId == provider && item.Support == ActiveEventScenarioSupport.Supported);
                if (scenario is null)
                {
                    result.Add(new($"delegated:{integration.Id}", $"Active Event provider for {integration.DisplayName}", JourneyPrerequisiteCategory.Executor,
                        JourneyReadinessState.NotAvailable, $"Provider {provider} offers no supported scenario for this integration."));
                    continue;
                }
                var readiness = await activeEvents.ReadinessAsync(context.EnvironmentId, integration.Id, provider, scenario.ScenarioId, null, ct);
                result.AddRange(readiness.Checks.Select(check => new JourneyPrerequisite($"active-event:{integration.Id}:{check.Key}", $"{check.Label} ({integration.DisplayName})",
                    Category(check.Category), State(check.State), check.Detail, check.State != ActiveEventReadinessState.Optional)));
            }
            catch (ActiveEventRequestException ex)
            {
                result.Add(new($"delegated:{integration.Id}", $"Active Event readiness for {integration.DisplayName}", JourneyPrerequisiteCategory.Executor, JourneyReadinessState.NotAvailable, ex.Message));
            }
        }
        return result;
    }

    private static JourneyReadinessState Readiness(IReadOnlyList<JourneyPrerequisite> prerequisites)
    {
        var mandatory = prerequisites.Where(item => item.Mandatory).ToArray();
        if (mandatory.All(item => item.State == JourneyReadinessState.Ready)) return JourneyReadinessState.Ready;
        if (mandatory.Any(item => item.Key.StartsWith(IntegrationKeyPrefix, StringComparison.Ordinal) && item.State == JourneyReadinessState.NotConfigured)) return JourneyReadinessState.NotConfigured;
        return mandatory.Any(item => item.State is JourneyReadinessState.Ready or JourneyReadinessState.Partial) ? JourneyReadinessState.Partial : JourneyReadinessState.NotReady;
    }

    private static JourneyReadinessState State(ActiveEventReadinessState state) => state switch
    {
        ActiveEventReadinessState.Ready => JourneyReadinessState.Ready,
        ActiveEventReadinessState.NotConfigured => JourneyReadinessState.NotConfigured,
        ActiveEventReadinessState.NotAvailable => JourneyReadinessState.NotAvailable,
        ActiveEventReadinessState.Partial or ActiveEventReadinessState.Optional or ActiveEventReadinessState.Unknown => JourneyReadinessState.Partial,
        _ => JourneyReadinessState.NotReady,
    };

    private static JourneyPrerequisiteCategory Category(ActiveEventReadinessCategory category) => category switch
    {
        ActiveEventReadinessCategory.TrustedEnvironment => JourneyPrerequisiteCategory.TrustedEnvironment,
        ActiveEventReadinessCategory.Integration or ActiveEventReadinessCategory.Destination => JourneyPrerequisiteCategory.Integration,
        ActiveEventReadinessCategory.SourceContract or ActiveEventReadinessCategory.CdcCapture => JourneyPrerequisiteCategory.SourceEvidence,
        ActiveEventReadinessCategory.SyntheticData => JourneyPrerequisiteCategory.SyntheticData,
        ActiveEventReadinessCategory.Authentication => JourneyPrerequisiteCategory.Authentication,
        ActiveEventReadinessCategory.Authorization => JourneyPrerequisiteCategory.Authorization,
        ActiveEventReadinessCategory.DownstreamVerification => JourneyPrerequisiteCategory.DownstreamVerification,
        _ => JourneyPrerequisiteCategory.Executor,
    };

    private static IReadOnlyList<(JourneyIntegrationRequirement Requirement, IntegrationDefinition? Integration)> Matches(IIntegrationJourneyPack pack,
        IntegrationJourneyDefinition journey, IntegrationCatalog configured) =>
        journey.Integrations.SelectMany(requirement => configured.Integrations
            .Where(integration => integration.Kind == requirement.Kind && pack.Matches(requirement, integration))
            .Select(integration => (requirement, (IntegrationDefinition?)integration))).ToArray();

    /// <summary>Backend-owned trust only: a trusted record, execution allowed, Development/QA type and no production marker on the target host.</summary>
    private static string? TrustProblem(JourneyPackContext context)
    {
        if (context.TrustedEnvironment is not { } trusted) return context.TrustDetail;
        if (!trusted.ExecutionAllowed) return "Trusted, but active execution is not allowed for this environment.";
        if (ActiveEventPolicy.EnvironmentBlock(trusted.EnvironmentType) is { } blocked) return blocked;
        if (Uri.TryCreate(trusted.TargetUrl, UriKind.Absolute, out var url) && ActiveEventPolicy.LooksLikeProduction(url.Host))
            return "The trusted environment's target host carries a production marker.";
        return null;
    }

    private async Task<JourneyPackContext> ContextAsync(string environmentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) throw new IntegrationJourneyRequestException("environmentId is required.");
        var trusted = environments.Resolve(environmentId, out var reason);
        var configured = await catalog.GetAsync(environmentId, trusted?.EnvironmentType, trusted?.TargetUrl, ct);
        var snapshots = sources.SourceAnalysisEnabled ? await sources.ListAsync(environmentId, ct) : [];
        return new JourneyPackContext(environmentId, trusted, reason, configured, snapshots);
    }

    private async Task<IntegrationJourneyRun> BlockedAsync(IntegrationJourneyRun run, IReadOnlyList<string> reasons, CancellationToken ct)
    {
        var blocked = run with
        {
            OverallState = JourneyRunState.Blocked, CompletedAt = clock.GetUtcNow(), StateReason = "Blocked before execution: " + string.Join(" ", reasons),
            Steps = run.Steps.Select(step => step with { Reason = "Not executed: the run was blocked before execution." }).ToArray(),
            Limitations = [.. run.Limitations, "Nothing was sent, submitted or published by this run."],
        };
        if (!await history.InsertAsync(blocked, ct)) blocked = blocked with { Limitations = [.. blocked.Limitations, "This blocked attempt could not be saved to history."] };
        return blocked;
    }
}
