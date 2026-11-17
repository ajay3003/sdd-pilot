using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.ContractTesting;
using BirkNext.IntegrationSuites;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.IntegrationSuites;

/// <summary>
/// Revision-pinned composite execution and single-stage retests. A composite run records, at start, the exact definition it executes (suite
/// revision, plan revision, the stored test revision of every step, sources, environment identity, engine); the chain runner is required to run
/// exactly those revisions, so later edits never change what a historical run means. A stage retest is a new run of the original definition in
/// the original environment, linked to the run and stage it retests; the original run and its evidence are never changed. A stage that takes
/// values from earlier stages is retested together with them: values from the original run are never reused.
/// </summary>
public sealed partial class IntegrationSuiteService
{
    public const string CompositeRetestKind = "composite-retest";
    public static readonly string Engine = $"BirkNext contract-test chain runner {typeof(IntegrationSuiteService).Assembly.GetName().Version}";

    /// <summary>The pinned definition of a composite run about to start: plan steps with the stored revision each one must run. A step whose test
    /// is not among the suite's tests is pinned to the test's revision now (what will run), never left floating.</summary>
    private async Task<CompositeExecutionDefinition> DefinitionAsync(IntegrationSuite suite, CompositePlan plan, IReadOnlyCollection<string> stages, IReadOnlyCollection<string> stepIds,
        List<SuiteSourceBinding> sources, string? projectKey, string environmentId, string targetUrl, string? configurationFingerprint, string initiator, CancellationToken ct)
    {
        var revisions = suite.Tests.GroupBy(t => t.TestId).ToDictionary(g => g.Key, g => g.First().Revision);
        var chosen = plan.Steps.Where(s => stepIds.Contains(s.StepId)).ToList();
        var missing = chosen.Select(s => s.TestId).OfType<Guid>().Where(id => !revisions.ContainsKey(id)).Distinct().ToList();
        if (missing.Count > 0)
            foreach (var row in await db.ContractTests.AsNoTracking().Where(t => missing.Contains(t.Id)).Select(t => new { t.Id, t.Revision }).ToListAsync(ct))
                revisions[row.Id] = row.Revision;
        var definition = new CompositeExecutionDefinition
        {
            ProjectKey = string.IsNullOrWhiteSpace(projectKey) ? null : projectKey.Trim(), SuiteId = suite.Id, SuiteKey = suite.SuiteKey, SuiteRevision = suite.Revision,
            SuiteFingerprint = suite.Fingerprint, Journeys = [.. suite.PreparedForJourneys], ChainId = plan.ChainId, ChainRevision = plan.ChainRevision, Stages = [.. stages],
            Steps = [.. chosen.Select(s => new CompositeExecutionStep(s.StepId, s.StageId, s.Role, s.TestId, s.TestId is { } id && revisions.TryGetValue(id, out var r) ? r : null,
                s.ContractRef, s.Operation, s.FromStep))],
            Sources = [.. sources], EnvironmentId = environmentId, TargetUrl = targetUrl, ConfigurationFingerprint = configurationFingerprint, Engine = Engine,
            StartedAt = Now, Initiator = initiator,
        };
        return definition with { DefinitionSha256 = DefinitionSha(definition) };
    }

    /// <summary>SHA-256 of what was executed (not when or by whom): equal values = the same definition.</summary>
    public static string DefinitionSha(CompositeExecutionDefinition d) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(d with { StartedAt = default, Initiator = "", DefinitionSha256 = "" }, Json)))).ToLowerInvariant();

    private static Dictionary<Guid, int> Pins(CompositeExecutionDefinition d) =>
        d.Steps.Where(s => s.TestId is not null && s.TestRevision is not null).GroupBy(s => s.TestId!.Value).ToDictionary(g => g.Key, g => g.First().TestRevision!.Value);

    // ── Stage retest ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<(IntegrationSuiteRunRecord? Link, string? Problem)> RunLinkAsync(Guid suiteId, Guid runId, string? projectKey, CancellationToken ct)
    {
        var suite = await GetAsync(suiteId, ct);
        if (suite is null) return (null, "Suite not found.");
        if (!UsableBy(suite, projectKey)) return (null, "This suite was generated for another project; nothing was sent.");
        var link = await db.IntegrationSuiteRuns.AsNoTracking().FirstOrDefaultAsync(r => r.SuiteKey == suite.SuiteKey && r.RunId == runId && r.PlanJson != null, ct);
        return link is null ? (null, "No such composite run of this suite.") : (link, null);
    }

    /// <summary>What retesting <paramref name="stageId"/> of a run takes: its steps, the earlier stages it takes values from, and why it can or cannot run.</summary>
    public async Task<CompositeStageRetestPlan> RetestPlanAsync(Guid suiteId, Guid runId, string stageId, bool includePrerequisites, string? projectKey, CancellationToken ct)
    {
        var (link, problem) = await RunLinkAsync(suiteId, runId, projectKey, ct);
        if (link is null) return new(false, stageId, [], [], [problem!], problem!);
        return RetestPlan(link, stageId, includePrerequisites);
    }

    internal static CompositeStageRetestPlan RetestPlan(IntegrationSuiteRunRecord link, string stageId, bool includePrerequisites)
    {
        if (link.DefinitionJson is null || JsonSerializer.Deserialize<CompositeExecutionDefinition>(link.DefinitionJson, Json) is not { } d)
            return new(false, stageId, [], [], ["This run was recorded before BirkNext pinned what a run executed, so a comparable retest cannot be shown. Start a new run instead."],
                "Legacy run: its executed definition was not recorded.");
        var own = d.Steps.Where(s => s.StageId == stageId).ToList();
        if (own.Count == 0) return new(false, stageId, [], [], [$"Stage {stageId} was not part of this run."], "Not part of this run.");
        var byId = d.Steps.ToDictionary(s => s.StepId, StringComparer.Ordinal);
        var closure = new HashSet<string>(StringComparer.Ordinal);
        void Add(CompositeExecutionStep step)
        {
            if (!closure.Add(step.StepId)) return;
            if (step.FromStep is { } from && byId.TryGetValue(from, out var upstream)) Add(upstream);
        }
        foreach (var step in own) Add(step);
        var prerequisites = d.Steps.Where(s => closure.Contains(s.StepId) && s.StageId != stageId).Select(s => s.StageId).Distinct(StringComparer.Ordinal).ToList();
        var stepIds = d.Steps.Where(s => closure.Contains(s.StepId)).Select(s => s.StepId).ToList();
        if (prerequisites.Count > 0 && !includePrerequisites)
            return new(false, stageId, prerequisites, stepIds,
                [$"This stage takes values from {string.Join(", ", prerequisites)}. Retest it together with those stages: values captured in the original run are not reused, because they may no longer be valid."],
                $"Needs {string.Join(", ", prerequisites)} first.");
        return new(true, stageId, prerequisites, stepIds, [],
            prerequisites.Count == 0 ? $"Runs only stage {stageId} ({stepIds.Count} step(s)) with the original run's test revisions, in the original environment."
                                     : $"Runs {string.Join(", ", prerequisites)} and then {stageId} ({stepIds.Count} step(s)) with the original run's test revisions, in the original environment.");
    }

    /// <summary>Retests one stage of a composite run as a new, separately recorded run of the original pinned definition.</summary>
    public async Task<CompositeRunResult> RetestStageAsync(Guid suiteId, CompositeStageRetestRequest request, string initiator, CancellationToken ct)
    {
        CompositeRunResult Refuse(string state, string message) => new(null, state, message, new(false, CompositeRunKind.SubflowTest, [request.StageId], [], [message], []));
        var (link, problem) = await RunLinkAsync(suiteId, request.OriginalRunId, request.WorkspaceProjectKey, ct);
        if (link is null) return Refuse(problem == "Suite not found." || problem == "No such composite run of this suite." ? "NotFound" : "Refused", problem!);
        var plan = RetestPlan(link, request.StageId, request.IncludePrerequisites);
        if (!plan.CanRetest) return Refuse("Refused", string.Join(" ", plan.Problems));
        var original = JsonSerializer.Deserialize<CompositeExecutionDefinition>(link.DefinitionJson!, Json)!;
        var runPlan = JsonSerializer.Deserialize<CompositeRunPlan>(link.PlanJson!, Json)!;
        if (!request.Authorized) return Refuse("NotAuthorized", "Execution needs the tester's explicit authorisation; nothing was sent.");
        if (request.IsProduction) return Refuse("Refused", "A composite run never targets production.");
        if (!string.Equals(request.EnvironmentId, original.EnvironmentId, StringComparison.Ordinal))
            return Refuse("Refused", $"A retest runs in the original run's environment ({original.EnvironmentId}) so its result can be compared; start a new run to test another environment.");
        var mutating = original.Steps.Where(s => plan.StepIds.Contains(s.StepId) && runPlan.Plan.Steps.Any(p => p.StepId == s.StepId && p.Mutating)).Select(s => $"{s.StageId}: {s.Operation}").ToList();
        if (mutating.Count > 0 && !request.ConfirmMutatingCalls) return Refuse("Refused", $"Confirm the data-changing operation(s): {string.Join("; ", mutating)}.");
        if (original.ChainId is not { } chainId) return Refuse("Refused", "The original run has no transaction plan to retest.");

        var suite = (await GetAsync(suiteId, ct))!;
        var stages = original.Stages.Where(s => s == request.StageId || plan.PrerequisiteStages.Contains(s)).ToList();
        var result = await contractTests.RunChainAsync(chainId, new ContractChainRunRequest
        {
            Environment = new ContractTestEnvironment
            {
                EnvironmentId = request.EnvironmentId, TargetUrl = request.TargetUrl, EnvironmentType = request.EnvironmentType, IsProduction = request.IsProduction,
                ProfileId = request.ProfileId, ContextFingerprint = request.ContextFingerprint,
            },
            ConfirmMutatingCalls = request.ConfirmMutatingCalls, StopOnFirstFailure = true, StepIds = plan.StepIds, OwnerDependencies = SuiteDependencies(suite),
            PinnedRevisions = Pins(original), ExpectedChainRevision = original.ChainRevision,
            Name = $"Stage retest · {request.StageId} · {suite.Scope.DisplayName} r{original.SuiteRevision}",
        }, ct);
        if (result.Run?.RunId is not { } runId)
            return Refuse("Refused", string.Join(" ", result.NotRun.Concat(result.Run?.Blockers ?? []).DefaultIfEmpty(result.Run?.Message ?? "The retest did not start.")));
        var definition = original with
        {
            Stages = stages, Steps = [.. original.Steps.Where(s => plan.StepIds.Contains(s.StepId))], TargetUrl = request.TargetUrl,
            ConfigurationFingerprint = request.ContextFingerprint, Engine = Engine, StartedAt = Now, Initiator = initiator,
            ProjectKey = string.IsNullOrWhiteSpace(request.WorkspaceProjectKey) ? original.ProjectKey : request.WorkspaceProjectKey.Trim(),
        };
        definition = definition with { DefinitionSha256 = DefinitionSha(definition) };
        db.IntegrationSuiteRuns.Add(new IntegrationSuiteRunRecord
        {
            Id = Guid.NewGuid(), SuiteKey = link.SuiteKey, SuiteId = link.SuiteId, RunId = runId, Kind = CompositeRetestKind, StartedAt = definition.StartedAt, Initiator = initiator,
            TestIdsJson = JsonSerializer.Serialize(definition.Steps.Select(s => s.TestId).OfType<Guid>(), Json),
            PlanJson = JsonSerializer.Serialize(runPlan with { Kind = CompositeRunKind.SubflowTest, Stages = stages, StepIds = plan.StepIds }, Json),
            DefinitionJson = JsonSerializer.Serialize(definition, Json), RetestOfRunId = link.RunId, RetestStageId = request.StageId,
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Composite stage retest started. Suite {Suite}; stage {Stage}; steps {Steps}", link.SuiteKey, request.StageId, plan.StepIds.Count);
        return new(runId, result.Run.State.ToString(), result.Run.Message ?? "Started.", new(true, CompositeRunKind.SubflowTest, stages, mutating, [], []));
    }

    /// <summary>A composite run by its execution run id with its stored link and pinned definition (null definition = legacy run).</summary>
    public async Task<(IntegrationSuiteRunRecord Link, CompositeRunView View, CompositeExecutionDefinition? Definition)?> CompositeRunByRunIdAsync(Guid runId, CancellationToken ct)
    {
        var link = await db.IntegrationSuiteRuns.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId && r.PlanJson != null, ct);
        if (link is null || await ReadCompositeRunAsync(db, evidence, link, withRetests: false, ct) is not { } view) return null;
        return (link, view, view.Definition);
    }

    // ── Read model additions ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What changed in the suite's definition since a pinned run: a newer suite revision, a revised plan, revised step tests. Empty = current.</summary>
    internal static async Task<List<string>> ChangedSinceAsync(AppDbContext db, CompositeExecutionDefinition d, CancellationToken ct)
    {
        var changes = new List<string>();
        var latest = await db.IntegrationSuites.AsNoTracking().Where(s => s.SuiteKey == d.SuiteKey).MaxAsync(s => (int?)s.Revision, ct);
        if (latest is { } l && l > d.SuiteRevision) changes.Add($"The suite has a newer revision (r{l}; this run used r{d.SuiteRevision}).");
        if (d.ChainId is { } chainId && await db.ContractTestChains.AsNoTracking().Where(c => c.ChainId == chainId).MaxAsync(c => (int?)c.Revision, ct) is { } chain && chain != d.ChainRevision)
            changes.Add($"The transaction plan was revised (plan revision {d.ChainRevision} → {chain}).");
        var ids = d.Steps.Select(s => s.TestId).OfType<Guid>().Distinct().ToList();
        var current = await db.ContractTests.AsNoTracking().Where(t => ids.Contains(t.Id)).Select(t => new { t.Id, t.Name, t.Revision }).ToListAsync(ct);
        foreach (var step in d.Steps.Where(s => s.TestId is not null))
        {
            var now = current.FirstOrDefault(c => c.Id == step.TestId);
            if (now is null) changes.Add($"The test of step {step.StepId} no longer exists.");
            else if (now.Revision != step.TestRevision) changes.Add($"{now.Name} ({step.StepId}) was revised (r{step.TestRevision} → r{now.Revision}).");
        }
        return changes;
    }
}
