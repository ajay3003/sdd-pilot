using System.Text.Json;
using BirkNext.Api.Models;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Sprint-based scenario execution and plan duplication (2026-11-26). Results are stored in the existing <c>manual_test_runs</c> table (dormant
/// since 2026-11-19; no migration): one completed row per recorded result, keyed by the plan id and «occ:{occurrence key}» so they never mix with
/// the old case-level runs. The plan itself is not changed by recording a result (no plan revision).
/// </summary>
public sealed partial class TestPlanSuggestionService
{
    private const string OccurrencePrefix = "occ:";

    /// <summary>The plan's scenario occurrences with their current status and history (read-only; writes nothing).</summary>
    public async Task<PlanExecutionView> ExecutionAsync(Guid planId, CancellationToken ct)
    {
        var row = await Row(planId, ct);
        var plan = Read(row);
        var events = await EventsAsync(planId, ct);
        // Preparation per occurrence (same engine as «Klargjør AC»): the scenario from the project's latest discovery and the testers' decisions
        // in its document. Read-only; a scenario that is not in the discovery has none.
        var readiness = await OccurrenceReadinessAsync(plan, ct);
        return new PlanExecutionView
        {
            PlanId = plan.Id, PlanName = plan.PlanName, SprintLabel = plan.DeliveryLabel, Archived = row.Lifecycle != ActiveState,
            Occurrences = [.. Occurrences(plan).Select(o =>
            {
                var history = events.GetValueOrDefault(o.Key) ?? [];
                var latest = history.FirstOrDefault();
                return o with { History = history, Latest = latest, Status = latest?.Status ?? ScenarioExecutionStatus.NotRun,
                    Preparation = o.ScenarioId is { } sid ? readiness.GetValueOrDefault(sid) : null };
            })],
        };
    }

    private async Task<Dictionary<string, PreparationStatus?>> OccurrenceReadinessAsync(TestPlanDraft plan, CancellationToken ct)
    {
        var result = new Dictionary<string, PreparationStatus?>(StringComparer.Ordinal);
        if (plan.ProjectKey is not { } key || discoveries is null) return result;
        if (discoveryStore is not null)
            try { await discoveryStore.RestoreAsync(key, null, ct); } catch (Exception e) when (e is not OperationCanceledException) { }
        if (discoveries.Latest(key) is not { } latest) return result;
        var ids = Occurrences(plan).Select(o => o.ScenarioId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0) return result;
        var (_, preparation) = await GroupingDecisionsAsync(key, ct);
        foreach (var c in latest.Candidates.Where(c => ids.Contains(c.CandidateId)))
        {
            var doc = ScenarioPreparationEngine.DocumentKey(c);
            var decisions = doc is null ? null : preparation.GetValueOrDefault(StoryGroupingService.NormalizePath(doc));
            result[c.CandidateId] = ScenarioPreparationEngine.Evaluate(ScenarioPreparationEngine.FactsOf(c, []), decisions, c.SourceIdentity, c.Fingerprint).Status;
        }
        return result;
    }

    /// <summary>Records one scenario result in this plan (a new event; history kept). Refused for an archived plan, an unknown occurrence, or when
    /// a newer result was recorded since the tester looked (409).</summary>
    public async Task<PlanExecutionView> RecordResultAsync(Guid planId, RecordScenarioResultRequest request, string actor, CancellationToken ct)
    {
        var row = await Row(planId, ct);
        if (row.Lifecycle != ActiveState) throw new TestPlanException(409, "plan-archived", "Testplanen er arkivert. Gjenopprett den for å registrere resultater.");
        var plan = Read(row);
        if (Occurrences(plan).All(o => o.Key != request.OccurrenceKey)) throw new TestPlanException(404, "occurrence-unknown", "Scenarioet finnes ikke i denne testplanen.");
        var latest = (await EventsAsync(planId, ct)).GetValueOrDefault(request.OccurrenceKey)?.FirstOrDefault();
        if (latest?.Id != request.ExpectedLatestEventId)
            throw new TestPlanException(409, "stale-result", "Et nyere resultat er registrert for scenarioet. Last inn på nytt; kommentaren din er beholdt.");
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim()[..Math.Min(request.Comment.Trim().Length, 2000)];
        var defect = string.IsNullOrWhiteSpace(request.DefectReference) ? null : request.DefectReference.Trim()[..Math.Min(request.DefectReference.Trim().Length, 300)];
        var e = new ScenarioExecutionEvent(Guid.NewGuid(), request.Status, comment, defect, actor, Now);
        db.ManualTestRuns.Add(new ManualTestRunRecord
        {
            Id = e.Id, ProjectKey = plan.ProjectKey ?? plan.GuideKey, DraftId = planId, CaseId = OccurrencePrefix + request.OccurrenceKey,
            State = nameof(ManualRunState.Completed), Outcome = e.Status.ToString(), Revision = 1, ContentJson = JsonSerializer.Serialize(e, E.Json),
            Tester = actor, StartedAt = e.At, CompletedAt = e.At, UpdatedAt = e.At,
        });
        await db.SaveChangesAsync(ct);
        return await ExecutionAsync(planId, ct);
    }

    /// <summary>Events per occurrence key, newest first.</summary>
    private async Task<Dictionary<string, List<ScenarioExecutionEvent>>> EventsAsync(Guid planId, CancellationToken ct) =>
        (await db.ManualTestRuns.AsNoTracking().Where(r => r.DraftId == planId && r.CaseId.StartsWith(OccurrencePrefix)).ToListAsync(ct))
            .Select(r => (Key: r.CaseId[OccurrencePrefix.Length..], Event: JsonSerializer.Deserialize<ScenarioExecutionEvent>(r.ContentJson, E.Json)!))
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Event).OrderByDescending(x => x.At).ThenByDescending(x => x.Id).ToList(), StringComparer.Ordinal);

    /// <summary>A plan's scenario occurrences: each scenario once (its cases grouped under it), each scenario-only item, and each hand-written case
    /// without a scenario on its own. Excluded and superseded cases are not executed.</summary>
    internal static List<ScenarioOccurrence> Occurrences(TestPlanDraft plan)
    {
        var result = new List<ScenarioOccurrence>();
        string? Path(string? suite) => suite is not null && plan.Suites.Any(s => s.Id == suite) ? PlanStructure.PathOf(plan.Suites, suite) : null;
        foreach (var g in plan.Cases.Where(c => c.Review is not (TestCaseReviewState.Superseded or TestCaseReviewState.Excluded))
                     .GroupBy(c => c.ParentScenario is { } sid ? "scenario:" + sid : "case:" + c.Source.ScenarioId, StringComparer.Ordinal))
        {
            var first = g.First();
            result.Add(new ScenarioOccurrence
            {
                Key = g.Key, ScenarioId = first.ParentScenario, Title = first.Source.Kind == ScenarioSourceKind.Manual && first.ParentScenario is null ? first.EffectiveTitle : first.Generated.Title,
                StoryId = first.Generated.Story?.StoryId, NumberInStory = first.Generated.NumberInStory, ExpectedBehaviour = first.Generated.ExpectedResult ?? first.EffectiveExpected,
                SuiteId = first.SuiteId, SuitePath = Path(first.SuiteId), CaseIds = [.. g.Select(c => c.Source.ScenarioId)],
            });
        }
        foreach (var item in plan.PlannedScenarios ?? [])
        {
            if (result.Any(o => o.Key == "scenario:" + item.ScenarioId)) continue;
            result.Add(new ScenarioOccurrence
            {
                Key = "scenario:" + item.ScenarioId, ScenarioId = item.ScenarioId, Title = item.Title, StoryId = item.StoryId, NumberInStory = item.NumberInStory,
                ExpectedBehaviour = item.ExpectedBehaviour, SuiteId = item.SuiteId, SuitePath = Path(item.SuiteId), ScenarioOnly = true,
            });
        }
        return result;
    }

    // ── «Kopier testplan» ──

    public async Task<DuplicatePlanPreview> DuplicatePreviewAsync(Guid planId, CancellationToken ct)
    {
        var plan = Read(await Row(planId, ct));
        var occurrences = Occurrences(plan);
        return new DuplicatePlanPreview(plan.Id, plan.PlanName, plan.Suites.Count, occurrences.Count, plan.Cases.Count(c => c.Review is not (TestCaseReviewState.Superseded or TestCaseReviewState.Excluded)),
            occurrences.Count(o => o.ScenarioOnly), plan.Cases.Count(c => c.Review == TestCaseReviewState.Accepted));
    }

    /// <summary>
    /// A NEW plan from an existing one (one transaction; the source plan is never changed). Copied: the suite tree (new suite ids, same names,
    /// nesting, order and level; empty suites too), the scenarios (scenario-only items and the cases as independent copies with their content,
    /// translations, test data links and source links). NOT copied: execution results (the new plan starts with every scenario «Ikke kjørt»),
    /// approvals (an approved case becomes «under arbeid» in the copy; review history is not carried over as if it were this plan's), revisions
    /// (the new plan starts at revision 1) and Azure DevOps mappings (they belong to the source plan id).
    /// </summary>
    public async Task<TestPlanDraft> DuplicateAsync(Guid planId, DuplicatePlanRequest request, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PlanName)) throw new TestPlanException(400, "name-required", "Gi den nye testplanen et navn.");
        var source = Read(await Row(planId, ct));
        if (source.ProjectKey is { } key && !string.IsNullOrWhiteSpace(request.CreationRequestId) && await CreatedFromAsync(key, request.CreationRequestId.Trim(), ct) is { } already) return already;
        var ids = source.Suites.ToDictionary(s => s.Id, _ => "suite:c:" + Guid.NewGuid().ToString("N")[..12], StringComparer.Ordinal);
        string Map(string id) => ids.TryGetValue(id, out var n) ? n : id;
        var plan = source with
        {
            Id = Guid.NewGuid(), CreationRequestId = string.IsNullOrWhiteSpace(request.CreationRequestId) ? null : request.CreationRequestId.Trim()[..Math.Min(request.CreationRequestId.Trim().Length, 64)],
            PlanName = Name(request.PlanName), DeliveryLabel = string.IsNullOrWhiteSpace(request.SprintLabel) ? source.DeliveryLabel : Label(request.SprintLabel), Revision = 1,
            Suites = [.. source.Suites.Select(s => s with { Id = Map(s.Id), ParentId = s.ParentId is { } p ? Map(p) : null })],
            Cases = [.. source.Cases.Select(c => c with
            {
                SuiteId = Map(c.SuiteId),
                Review = c.Review == TestCaseReviewState.Accepted ? TestCaseReviewState.UnderReview : c.Review,
                ReviewHistory = [],
            })],
            PlannedScenarios = source.PlannedScenarios is { } items ? [.. items.Select(p => p with { SuiteId = Map(p.SuiteId) })] : null,
            CreatedBy = actor, CreatedAt = Now, UpdatedBy = actor, UpdatedAt = Now,
        };
        if (PlanStructure.Validate(plan.Suites) is { } invalid) throw new TestPlanException(400, "invalid-suites", invalid);
        db.TestPlanDrafts.Add(new TestPlanDraftRecord
        {
            Id = plan.Id, GuideKey = plan.GuideKey, PlanName = plan.PlanName, DeliveryLabel = plan.DeliveryLabel, Revision = 1, ContentJson = JsonSerializer.Serialize(plan, E.Json),
            CreatedBy = actor, CreatedAt = plan.CreatedAt, UpdatedBy = actor, UpdatedAt = plan.UpdatedAt,
        });
        AddRevision(plan, $"Kopiert fra «{source.PlanName}» (versjon {source.Revision})");
        await db.SaveChangesAsync(ct);
        return plan;
    }
}
