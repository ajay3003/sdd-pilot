using System.Collections.Concurrent;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Services.ManualReview;
using BirkNext.Api.Services.Specifications;
using BirkNext.ImplementationGaps;
using BirkNext.ManualReview;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// User story implementation status for a project (2026-11-26): the stored specification evidence (stories) and the task lines the same import
/// extracted (Implementation Gaps' persisted inputs, scope "import:{id}" = the project key) through <see cref="UserStoryImplementationEngine"/>.
/// Nothing is parsed from the archive again and nothing is written by reading. The extracted task inputs are immutable per evaluation row and
/// cached by its id. Confirmations are manual-review decisions (module <see cref="Module"/>), append-only, applying to one evidence fingerprint.
/// </summary>
public sealed class UserStoryStatusService(AppDbContext db, SpecificationCatalogService? catalogs = null, ManualReviewDecisionStore? reviews = null, TimeProvider? clock = null)
{
    /// <summary>The manual-review module of implementation confirmations (subject = canonical story key, scope = project key).</summary>
    public const string Module = ManualReviewModules.UserStoryImplementation;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<Guid, GapDocumentSet> Inputs = new();

    private sealed record StoredInputs(GapDocumentSet? Documents);

    public async Task<UserStoryStatusView> StatusAsync(string projectKey, string? specificationPath, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(projectKey);
        var stored = catalogs is null ? null : await catalogs.EvidenceAsync(null, key, ct);
        var snapshot = stored?.Catalog.SnapshotId;
        var (docs, readAt, docsSnapshot) = await TasksAsync(key, snapshot, ct);
        var confirmations = await ConfirmationsAsync(key, ct);
        var stories = new List<UserStoryImplementationStatus>();
        foreach (var spec in stored?.Evidence ?? [])
        {
            if (specificationPath is { Length: > 0 } wanted && !string.Equals(spec.Path.Replace('\\', '/').Trim('/'), wanted.Replace('\\', '/').Trim('/'), StringComparison.OrdinalIgnoreCase)) continue;
            var specStories = spec.Evidence.Stories.Where(s => string.Equals((s.SpecificationPath ?? spec.Path).Replace('\\', '/'), spec.Path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToList();
            if (specStories.Count == 0) continue;
            stories.AddRange(UserStoryImplementationEngine.Compute(spec.Path, TestPlanSuggestionService.ModuleOf(spec.Path), specStories, docs, confirmations));
        }
        return new UserStoryStatusView
        {
            ProjectKey = key, SnapshotId = snapshot, SourceReadAt = readAt, CalculatedAt = (clock ?? TimeProvider.System).GetUtcNow(), RuleVersion = UserStoryImplementationEngine.RuleVersion,
            TasksAvailable = docs is not null, SourcesDiffer = docs is not null && snapshot is not null && docsSnapshot is not null && docsSnapshot != snapshot, Stories = stories,
        };
    }

    /// <summary>The task lines of the project's import: the evaluation of the same snapshot as the specification catalog, else the newest one.</summary>
    private async Task<(GapDocumentSet? Docs, DateTimeOffset? ReadAt, Guid? Snapshot)> TasksAsync(string key, Guid? snapshot, CancellationToken ct)
    {
        var rows = await db.ImplementationGapEvaluations.AsNoTracking().Where(r => r.ScopeKey == key)
            .OrderByDescending(r => r.EvaluatedAt).Select(r => new { r.Id, r.SnapshotId, r.EvaluatedAt }).Take(20).ToListAsync(ct);
        var row = rows.FirstOrDefault(r => snapshot is not null && r.SnapshotId == snapshot) ?? rows.FirstOrDefault();
        if (row is null) return (null, null, null);
        if (!Inputs.TryGetValue(row.Id, out var docs))
        {
            var json = await db.ImplementationGapEvaluations.AsNoTracking().Where(r => r.Id == row.Id).Select(r => r.InputsJson).FirstAsync(ct);
            try { docs = JsonSerializer.Deserialize<StoredInputs>(json, Json)?.Documents; }
            catch (JsonException) { docs = null; }
            if (docs is null) return (null, row.EvaluatedAt, row.SnapshotId);
            if (Inputs.Count > 50) Inputs.Clear();
            Inputs[row.Id] = docs;
        }
        return (docs, row.EvaluatedAt, row.SnapshotId);
    }

    private async Task<Dictionary<string, UserStoryImplementationEngine.Confirmation>> ConfirmationsAsync(string key, CancellationToken ct)
    {
        if (reviews is null) return [];
        var decisions = await reviews.ListAsync(Module, key, null, ct);
        return decisions.Where(d => d.Current).ToDictionary(d => d.SubjectKey,
            d => new UserStoryImplementationEngine.Confirmation(d.Decision == ManualReviewDecisionValue.Acceptable, d.AppliesTo ?? "", d.Reviewer, d.DecidedAt), StringComparer.Ordinal);
    }

    /// <summary>The confirmation history of one story, newest first (audit; never rewritten).</summary>
    public async Task<IReadOnlyList<ManualReviewDecisionView>> HistoryAsync(string projectKey, string storyKey, CancellationToken ct) =>
        reviews is null ? [] : await reviews.ListAsync(Module, TestScenarioDiscoveryService.Key(projectKey), storyKey, ct);

    /// <summary>
    /// A person confirms (or withdraws a confirmation of) a story's implementation, for the task evidence shown now: the decision records the
    /// evidence fingerprint, so a later change to the story's tasks shows «Må bekreftes på nytt». A new row every time; history is kept.
    /// </summary>
    public async Task<UserStoryImplementationStatus> ConfirmAsync(ConfirmUserStoryRequest request, string actor, CancellationToken ct)
    {
        if (reviews is null) throw new TestPlanException(503, "review-unavailable", "Bekreftelser kan ikke lagres her.");
        if (string.IsNullOrWhiteSpace(actor)) throw new TestPlanException(403, "actor-required", "Bekreftelsen krever en innlogget bruker.");
        var view = await StatusAsync(request.ProjectKey, null, ct);
        var story = view.Stories.FirstOrDefault(s => s.StoryKey == request.StoryKey)
            ?? throw new TestPlanException(404, "story-not-found", "Brukerhistorien finnes ikke i prosjektets gjeldende spesifikasjon.");
        if (request.Confirmed && story.Eligible == 0)
            throw new TestPlanException(409, "no-task-evidence", "Brukerhistorien har ingen oppgaver i tasks.md å bekrefte mot. Bekreft når oppgavegrunnlaget finnes.");
        var (decision, error) = await reviews.RecordAsync(new ManualReviewDecisionRequest
        {
            Module = Module, ScopeKey = view.ProjectKey, SubjectKey = story.StoryKey, Reviewer = actor,
            Decision = request.Confirmed ? ManualReviewDecisionValue.Acceptable : ManualReviewDecisionValue.NeedsEvidence,
            Note = request.Note, AppliesTo = story.EvidenceFingerprint,
            EvidenceReferences = [$"{story.TasksPath}@{view.SnapshotId?.ToString() ?? "ukjent kildeversjon"}"],
        }, ct);
        if (decision is null) throw new TestPlanException(400, "invalid-confirmation", error ?? "Bekreftelsen kunne ikke lagres.");
        return (await StatusAsync(request.ProjectKey, story.SpecificationPath, ct)).Stories.First(s => s.StoryKey == story.StoryKey);
    }
}
