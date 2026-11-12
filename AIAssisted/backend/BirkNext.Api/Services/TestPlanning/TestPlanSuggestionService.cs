using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Api.Services.TestData;
using BirkNext.Integrations;
using BirkNext.SystemUnderstanding;
using BirkNext.TestData;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Services.TestPlanning;

public sealed class TestPlanException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// Test Plan Suggestions over existing owners: scenarios from the System Understanding guide (<see cref="SystemGuideService"/>), the source basis
/// from the guide's own baseline-resolved sources plus <see cref="SourceBaselineService"/>, Test Data from <see cref="TestDataCatalogService"/>
/// (by immutable asset id, never values), executable definitions from the journey packs and the Critical E2E store. Persists only drafts and
/// immutable snapshots of the proposal itself.
/// </summary>
public sealed class TestPlanSuggestionService(AppDbContext db, SystemGuideService guides, SourceBaselineService baselines, TestDataCatalogService testData,
    IEnumerable<IIntegrationJourneyPack> packs, ICriticalE2EStore e2e, TimeProvider? clock = null)
{
    public const int MaxCases = 300;
    public const int MaxContentCharacters = 4_000_000;

    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    // ── Reading ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The guides as options (title and review state from the shown revision; no source reads).</summary>
    private async Task<List<TestPlanGuideOption>> GuideOptionsAsync(CancellationToken ct)
    {
        var options = new List<TestPlanGuideOption>();
        foreach (var key in guides.GuideKeys)
        {
            var (shown, review) = await guides.ShownAsync(key, ct);
            options.Add(new TestPlanGuideOption(key, shown.Content.Title, review));
        }
        return options;
    }

    /// <summary>Persisted facts only (drafts, snapshots, candidate count) for the Manual Testing focus panel on Dashboard and Recommended
    /// Workflow: no source, baseline or Test Data reads.</summary>
    public async Task<TestPlanWorkspace> SummaryAsync(string? guideKey, CancellationToken ct)
    {
        var options = await GuideOptionsAsync(ct);
        var key = options.FirstOrDefault(g => g.GuideKey == guideKey)?.GuideKey ?? options.FirstOrDefault()?.GuideKey;
        if (key is null) return new TestPlanWorkspace { Guides = options, RuleVersion = E.RuleVersion };
        var (shown, _) = await guides.ShownAsync(key, ct);
        var drafts = await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey == key).OrderByDescending(d => d.UpdatedAt).ToListAsync(ct);
        return new TestPlanWorkspace
        {
            Guides = options, GuideKey = key, RuleVersion = E.RuleVersion, Drafts = [.. drafts.Select(d => Summary(Read(d)))], Snapshots = await SnapshotsAsync(key, ct),
            Candidates = [.. shown.Content.Scenarios.Select(s => new ScenarioCandidate
            {
                Source = new ScenarioReference { Kind = ScenarioSourceKind.SystemGuide, Owner = key, ScenarioId = s.Id, Revision = shown.Revision }, Title = s.Title, GuideState = s.State.ToString(),
            })],
        };
    }

    public async Task<TestPlanWorkspace> WorkspaceAsync(string? guideKey, bool canWrite, CancellationToken ct)
    {
        var options = await GuideOptionsAsync(ct);
        var key = options.FirstOrDefault(g => g.GuideKey == guideKey)?.GuideKey ?? options.FirstOrDefault()?.GuideKey;
        if (key is null) return new TestPlanWorkspace { Guides = options, CanWrite = canWrite, RuleVersion = E.RuleVersion };
        var view = await guides.GetAsync(key, false, ct);
        var shown = view.Shown!;
        var executables = Executables();
        var candidates = shown.Content.Scenarios.Select(s =>
        {
            var stage = E.StageOf(shown.Content, s);
            return new ScenarioCandidate
            {
                Source = new ScenarioReference { Kind = ScenarioSourceKind.SystemGuide, Owner = key, ScenarioId = s.Id, Revision = shown.Revision, Fingerprint = E.ScenarioFingerprint(shown.Content, s) },
                Title = s.Title, Objective = s.Objective, GuideState = s.State.ToString(), StageId = stage, StageTitle = shown.Content.Stages.FirstOrDefault(x => x.Id == stage)?.Title,
                Requirements = E.Requirements(shown.Content, s.Id), HasExpected = !string.IsNullOrWhiteSpace(s.ExpectedOutcome), HasTestData = !string.IsNullOrWhiteSpace(s.TestData),
                HasObservation = !string.IsNullOrWhiteSpace(s.ObservableEvidence), Correlations = E.Correlate(s.ExistingTests, executables),
            };
        }).ToList();
        var drafts = await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey == key).OrderByDescending(d => d.UpdatedAt).ToListAsync(ct);
        return new TestPlanWorkspace
        {
            Guides = options, GuideKey = key, Basis = await BasisAsync(view, ct), Candidates = candidates,
            Stages = [.. shown.Content.Stages.Select(s => new TestPlanStage(s.Id, s.Title))],
            Drafts = [.. drafts.Select(d => Summary(Read(d)))], Snapshots = await SnapshotsAsync(key, ct), Executables = [.. executables],
            TestData = await TestDataOptionsAsync(ct), CanWrite = canWrite, RuleVersion = E.RuleVersion,
            SourceChanges = [.. view.SourceChanges.Select(c => $"{c.Label}: {c.Change} ({c.AffectedClaims} claims)")],
        };
    }

    public async Task<TestPlanDraft> DraftAsync(Guid id, CancellationToken ct)
    {
        var plan = Read(await Row(id, ct));
        return WithSourceFlags(plan, await CurrentFingerprintsAsync(plan.GuideKey, ct));
    }

    /// <summary>Marks each case whose scenario differs in (or left) the guide readers see now. Read-time only for a draft; a snapshot records it.</summary>
    private static TestPlanDraft WithSourceFlags(TestPlanDraft plan, IReadOnlyDictionary<string, string> current) => plan with
    {
        Cases = [.. plan.Cases.Select(c => c with { SourceChanged = c.Review != TestCaseReviewState.Superseded
            && (!current.TryGetValue(c.Source.ScenarioId, out var now) || now != c.Source.Fingerprint) })],
    };

    public async Task<List<TestPlanSnapshotSummary>> SnapshotsAsync(string guideKey, CancellationToken ct) =>
        [.. (await db.TestPlanSnapshots.AsNoTracking().Where(s => s.GuideKey == guideKey).OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Sequence)
            .Select(s => new { s.Id, s.DraftId, s.GuideKey, s.Sequence, s.PlanName, s.DeliveryLabel, s.DraftRevision, s.ContentFingerprint, s.RuleVersion, s.GuideRevision, s.CaseCount, s.CreatedBy, s.CreatedAt, s.Note })
            .ToListAsync(ct)).Select(s => new TestPlanSnapshotSummary(s.Id, s.DraftId, s.GuideKey, s.Sequence, s.PlanName, s.DeliveryLabel, s.DraftRevision, s.ContentFingerprint, s.RuleVersion,
                s.GuideRevision, s.CaseCount, s.CreatedBy, s.CreatedAt, s.Note))];

    public async Task<TestPlanSnapshot> SnapshotAsync(Guid id, CancellationToken ct)
    {
        var row = await db.TestPlanSnapshots.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw new TestPlanException(404, "snapshot-not-found", "No such Test Plan snapshot.");
        var plan = JsonSerializer.Deserialize<TestPlanDraft>(row.ContentJson, E.Json)!;
        return new TestPlanSnapshot { Summary = SnapshotSummary(row), Plan = plan };
    }

    public async Task<DeliveryComparison> CompareAsync(Guid from, Guid to, CancellationToken ct) => E.Compare(await SnapshotAsync(from, ct), await SnapshotAsync(to, ct));

    public async Task<TestPlanExport> ExportAsync(Guid snapshotId, CancellationToken ct) => E.Export(await SnapshotAsync(snapshotId, ct));

    // ── Writing ──────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<TestPlanDraft> CreateAsync(CreateTestPlanDraftRequest request, string actor, CancellationToken ct)
    {
        var name = Name(request.PlanName);
        var view = await guides.GetAsync(request.GuideKey, false, ct);
        var shown = view.Shown ?? throw new TestPlanException(404, "guide-empty", "The system guide has no revision.");
        var ids = Selected(shown.Content, request.ScenarioIds);
        if (ids.Count == 0) throw new TestPlanException(400, "no-scenarios", "Select at least one scenario.");
        var cases = await BuildCasesAsync(request.GuideKey, shown, ids, new Dictionary<string, TestCaseDraft>(StringComparer.Ordinal), ct);
        var plan = new TestPlanDraft
        {
            Id = Guid.NewGuid(), GuideKey = request.GuideKey, PlanName = name, DeliveryLabel = Label(request.DeliveryLabel), Revision = 1, Basis = await BasisAsync(view, ct),
            Suites = E.ProposeSuites(shown.Content, cases.Select(c => c.Generated)), Cases = cases, RuleVersion = E.RuleVersion,
            CreatedBy = actor, CreatedAt = Now, UpdatedBy = actor, UpdatedAt = Now,
        };
        Bound(plan);
        db.TestPlanDrafts.Add(new TestPlanDraftRecord
        {
            Id = plan.Id, GuideKey = plan.GuideKey, PlanName = plan.PlanName, DeliveryLabel = plan.DeliveryLabel, Revision = 1, ContentJson = JsonSerializer.Serialize(plan, E.Json),
            CreatedBy = actor, CreatedAt = plan.CreatedAt, UpdatedBy = actor, UpdatedAt = plan.UpdatedAt,
        });
        await db.SaveChangesAsync(ct);
        return plan;
    }

    /// <summary>Saves the human-owned parts. Generated content is server state: kept as stored for existing cases (a client cannot rewrite it) and
    /// generated for added scenarios from the draft's own guide revision, only while that revision is unchanged. A scenario that changed in the
    /// guide since is flagged (SourceChanged), never silently rewritten. A save from an older draft revision is refused with 409.</summary>
    public async Task<TestPlanDraft> SaveAsync(Guid id, SaveTestPlanDraftRequest request, string actor, CancellationToken ct)
    {
        var row = await Row(id, ct);
        if (row.Revision != request.BaseRevision) throw Stale(row);
        var current = Read(row);
        var previous = current.Cases.ToDictionary(c => c.Source.ScenarioId, StringComparer.Ordinal);
        var revision = await guides.RevisionAsync(current.GuideKey, current.Basis.GuideRevision, ct);
        var sameRevision = string.Equals(revision.ContentFingerprint, current.Basis.GuideFingerprint, StringComparison.Ordinal);
        var requested = (request.ScenarioIds ?? []).Distinct(StringComparer.Ordinal).ToList();
        var added = requested.Where(i => !previous.ContainsKey(i)).ToList();
        if (added.Count > 0 && !sameRevision)
            throw new TestPlanException(409, "guide-changed", "The guide changed since this proposal was generated. Update the proposal from the current guide before adding scenarios.");
        var known = revision.Content.Scenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var ids = requested.Where(i => previous.ContainsKey(i) || known.Contains(i)).ToList();
        if (ids.Count == 0) throw new TestPlanException(400, "no-scenarios", "A proposal keeps at least one scenario.");
        if (ids.Count > MaxCases) throw new TestPlanException(400, "too-many-cases", $"At most {MaxCases} cases per proposal.");
        var human = request.Cases.GroupBy(c => c.ScenarioId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var suites = Suites(request.Suites, revision.Content, [.. ids.Where(known.Contains)]);
        var cases = await BuildCasesAsync(current.GuideKey, revision, ids, previous, ct, human, suites, currentFingerprints: await CurrentFingerprintsAsync(current.GuideKey, ct));
        var plan = current with
        {
            PlanName = Name(request.PlanName), DeliveryLabel = Label(request.DeliveryLabel), Revision = row.Revision + 1, Suites = suites, Cases = cases, UpdatedBy = actor, UpdatedAt = Now,
        };
        return await WriteAsync(row, plan, ct);
    }

    /// <summary>Records one review decision (state, reviewer, rationale) as an event; the earlier events stay.</summary>
    public async Task<TestPlanDraft> ReviewAsync(Guid id, ReviewTestCaseRequest request, string actor, CancellationToken ct)
    {
        var row = await Row(id, ct);
        if (row.Revision != request.BaseRevision) throw Stale(row);
        if (request.State is TestCaseReviewState.Generated or TestCaseReviewState.Superseded)
            throw new TestPlanException(400, "invalid-review-state", "Generated and Superseded are set by BirkNext, not by a review.");
        var rationale = request.Rationale?.Trim();
        if (request.State is TestCaseReviewState.Excluded or TestCaseReviewState.NeedsClarification && string.IsNullOrWhiteSpace(rationale))
            throw new TestPlanException(400, "rationale-required", "Say why (a rationale is required to exclude a case or ask for clarification).");
        var plan = WithSourceFlags(Read(row), await CurrentFingerprintsAsync(row.GuideKey, ct));
        var target = plan.Cases.FirstOrDefault(c => c.Source.ScenarioId == request.ScenarioId) ?? throw new TestPlanException(404, "case-not-found", "No such case in this proposal.");
        if (request.State == TestCaseReviewState.Accepted && string.IsNullOrWhiteSpace(target.EffectiveExpected))
            throw new TestPlanException(409, "expected-result-missing", "A case without an expected result cannot be accepted. Clarify the expected result first.");
        if (request.State == TestCaseReviewState.Accepted && target.SourceChanged)
            throw new TestPlanException(409, "source-changed", "The scenario changed in the source since this draft was generated. Update the proposal from the current guide first.");
        var updated = target with
        {
            Review = request.State,
            ReviewHistory = [.. target.ReviewHistory, new TestCaseReviewEvent(request.State, actor, string.IsNullOrWhiteSpace(rationale) ? null : rationale[..Math.Min(rationale.Length, 1000)], Now)],
        };
        plan = plan with { Cases = [.. plan.Cases.Select(c => c.Source.ScenarioId == request.ScenarioId ? updated : c)], Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now };
        return await WriteAsync(row, plan, ct);
    }

    /// <summary>Creates a new proposal revision against the guide's current revision and sources: generated content is rebuilt, human edits,
    /// links and review history are kept, and a case whose scenario changed goes back to review (flagged), never silently accepted.</summary>
    public async Task<TestPlanDraft> RefreshAsync(Guid id, int baseRevision, string actor, CancellationToken ct)
    {
        var row = await Row(id, ct);
        if (row.Revision != baseRevision) throw Stale(row);
        var current = Read(row);
        var view = await guides.GetAsync(current.GuideKey, false, ct);
        var shown = view.Shown!;
        var known = shown.Content.Scenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var ids = current.Cases.Select(c => c.Source.ScenarioId).Where(known.Contains).ToList();
        var previous = current.Cases.ToDictionary(c => c.Source.ScenarioId, StringComparer.Ordinal);
        var cases = await BuildCasesAsync(current.GuideKey, shown, ids, previous, ct, null, current.Suites, refresh: true);
        var stageSuites = E.ProposeSuites(shown.Content, cases.Select(c => c.Generated));
        var suites = current.Suites.Concat(stageSuites.Where(s => current.Suites.All(x => x.Id != s.Id))).ToList();
        // A scenario no longer in the guide stays visible as Superseded: removed from the source, never silently dropped.
        var gone = current.Cases.Where(c => !known.Contains(c.Source.ScenarioId)).Select(c => c with
        {
            Review = TestCaseReviewState.Superseded,
            ReviewHistory = [.. c.ReviewHistory, new TestCaseReviewEvent(TestCaseReviewState.Superseded, "BirkNext", "The scenario is no longer in the guide.", Now)],
        });
        var plan = current with
        {
            Basis = await BasisAsync(view, ct), Cases = [.. cases, .. gone], Suites = suites, RuleVersion = E.RuleVersion, Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now,
        };
        return await WriteAsync(row, plan, ct);
    }

    /// <summary>Freezes the draft as an immutable snapshot. Test Data links are re-read first so the snapshot records the revisions in force.
    /// Identical content returns the existing snapshot (nothing new is written); a concurrent identical save resolves to the same one.</summary>
    public async Task<SaveTestPlanSnapshotResult> SaveSnapshotAsync(Guid id, SaveTestPlanSnapshotRequest request, string actor, CancellationToken ct)
    {
        var row = await Row(id, ct);
        if (row.Revision != request.ExpectedRevision) throw Stale(row);
        var plan = WithSourceFlags(Read(row), await CurrentFingerprintsAsync(row.GuideKey, ct));
        var assets = await AssetsAsync(ct);
        plan = plan with
        {
            DeliveryLabel = Label(request.DeliveryLabel) ?? plan.DeliveryLabel,
            Cases = [.. plan.Cases.Select(c => E.Assess(c with { TestData = [.. c.TestData.Select(d => Link(d.AssetId, assets) ?? d with { Resolved = false })] }))],
        };
        var fingerprint = E.ContentFingerprint(plan);
        var existing = await db.TestPlanSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.DraftId == id && s.ContentFingerprint == fingerprint, ct);
        if (existing is not null) return new SaveTestPlanSnapshotResult(SnapshotSummary(existing), true, $"Unchanged: identical to snapshot {existing.Sequence}. No new snapshot was written.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var sequence = (await db.TestPlanSnapshots.Where(s => s.DraftId == id).MaxAsync(s => (int?)s.Sequence, ct) ?? 0) + 1;
            var record = new TestPlanSnapshotRecord
            {
                Id = Guid.NewGuid(), DraftId = id, GuideKey = plan.GuideKey, Sequence = sequence, PlanName = plan.PlanName, DeliveryLabel = plan.DeliveryLabel, DraftRevision = row.Revision,
                GuideRevision = plan.Basis.GuideRevision, RuleVersion = plan.RuleVersion, CaseCount = plan.Cases.Count, ContentFingerprint = fingerprint,
                ContentJson = JsonSerializer.Serialize(plan, E.Json), CreatedBy = actor, CreatedAt = Now, Note = Note(request.Note),
            };
            db.TestPlanSnapshots.Add(record);
            try
            {
                await db.SaveChangesAsync(ct);
                return new SaveTestPlanSnapshotResult(SnapshotSummary(record), false, $"Snapshot {sequence} saved. It cannot be changed; later edits create a new snapshot.");
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                var same = await db.TestPlanSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.DraftId == id && s.ContentFingerprint == fingerprint, ct);
                if (same is not null) return new SaveTestPlanSnapshotResult(SnapshotSummary(same), true, $"Unchanged: identical to snapshot {same.Sequence} (saved concurrently).");
            }
        }
        throw new TestPlanException(409, "snapshot-conflict", "Another snapshot was saved at the same time. Try again.");
    }

    // ── Building ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The fingerprint of every scenario in the guide revision readers see now (for change detection only).</summary>
    private async Task<Dictionary<string, string>> CurrentFingerprintsAsync(string guideKey, CancellationToken ct)
    {
        var (shown, _) = await guides.ShownAsync(guideKey, ct);
        return shown.Content.Scenarios.ToDictionary(s => s.Id, s => E.ScenarioFingerprint(shown.Content, s), StringComparer.Ordinal);
    }

    private async Task<List<TestCaseDraft>> BuildCasesAsync(string guideKey, SystemGuideRevision revision, IReadOnlyList<string> ids, IReadOnlyDictionary<string, TestCaseDraft> previous,
        CancellationToken ct, IReadOnlyDictionary<string, TestCaseHumanPart>? human = null, IReadOnlyList<TestSuiteDraft>? suites = null, bool refresh = false,
        IReadOnlyDictionary<string, string>? currentFingerprints = null)
    {
        var reviewed = revision.State == GuideRevisionState.Published;
        var executables = Executables();
        var byKey = executables.ToDictionary(e => e.Reference.Key, StringComparer.Ordinal);
        var assets = await AssetsAsync(ct);
        var result = new List<TestCaseDraft>();
        foreach (var id in ids)
        {
            previous.TryGetValue(id, out var before);
            // Existing cases keep their stored generated content and source reference unless the proposal is explicitly updated (refresh).
            var keep = before is not null && !refresh;
            var scenario = revision.Content.Scenarios.FirstOrDefault(s => s.Id == id);
            if (!keep && scenario is null) continue;
            var generated = keep ? before!.Generated : E.Generate(revision.Content, scenario!, reviewed);
            var fingerprint = keep ? before!.Source.Fingerprint : E.ScenarioFingerprint(revision.Content, scenario!);
            var namedTests = keep ? before!.Generated.NamedExistingTests : scenario!.ExistingTests;
            TestCaseHumanPart? part = null;
            human?.TryGetValue(id, out part);
            var suite = part?.SuiteId ?? before?.SuiteId ?? E.SuiteFor(generated);
            if (suites is not null && suites.All(s => s.Id != suite)) suite = suites.Any(s => s.Id == E.SuiteFor(generated)) ? E.SuiteFor(generated) : suites.FirstOrDefault()?.Id ?? E.SuiteFor(generated);
            var candidates = E.Correlate(namedTests, executables);
            var decisions = part?.Correlations ?? before?.Correlations.Where(c => c.State != CorrelationState.Candidate).ToDictionary(c => c.Target.Key, c => c.State, StringComparer.Ordinal)
                ?? new Dictionary<string, CorrelationState>(StringComparer.Ordinal);
            var manual = part?.ManualLinks ?? before?.Correlations.Where(c => c.Basis.StartsWith("Linked by a reviewer", StringComparison.Ordinal)).Select(c => c.Target.Key).ToList() ?? [];
            var correlations = candidates.Select(c => decisions.TryGetValue(c.Target.Key, out var state) && state != CorrelationState.Candidate ? c with { State = state } : c).ToList();
            foreach (var key in manual.Where(k => byKey.ContainsKey(k) && correlations.All(c => c.Target.Key != k)))
                correlations.Add(new ScenarioCorrelation { Target = byKey[key].Reference, Label = byKey[key].Label, State = CorrelationState.Confirmed, Capability = byKey[key].Detail, Basis = "Linked by a reviewer." });
            var dataIds = part?.TestDataAssetIds ?? before?.TestData.Select(d => d.AssetId).ToList() ?? [];
            var links = new List<TestCaseDataLink>();
            foreach (var assetId in dataIds.Distinct(StringComparer.Ordinal).Take(20))
            {
                // A known asset is linked by its immutable id; an id no longer in the catalog stays only if it was linked before (shown unresolved).
                if (Link(assetId, assets) is { } link) links.Add(link);
                else if (before?.TestData.FirstOrDefault(d => d.AssetId == assetId) is { } old) links.Add(old with { Resolved = false });
            }
            var changed = refresh && before is not null && before.Source.Fingerprint != fingerprint;
            var review = before?.Review ?? TestCaseReviewState.Generated;
            var history = before?.ReviewHistory ?? [];
            if (refresh && changed && review is TestCaseReviewState.Accepted)
            {
                review = TestCaseReviewState.UnderReview;
                history = [.. history, new TestCaseReviewEvent(TestCaseReviewState.UnderReview, "BirkNext", "The scenario changed in the guide; review it again.", Now)];
            }
            var draft = new TestCaseDraft
            {
                Source = keep ? before!.Source : new ScenarioReference { Kind = ScenarioSourceKind.SystemGuide, Owner = guideKey, ScenarioId = id, Revision = revision.Revision, Fingerprint = fingerprint },
                SuiteId = suite, Generated = generated, Edits = Clean(part?.Edits ?? before?.Edits ?? new()), Review = review, ReviewHistory = history,
                Risk = part?.Risk ?? before?.Risk ?? TestCaseRisk.NotAssessed, TestData = links, Correlations = correlations,
                // Flagged when the guide readers see now has a different version of this scenario (or no longer has it); cleared by an update.
                SourceChanged = !refresh && currentFingerprints is not null && (!currentFingerprints.TryGetValue(id, out var now) || now != fingerprint),
                VerifiedAzureDevOpsTestCaseId = before?.VerifiedAzureDevOpsTestCaseId,
            };
            result.Add(E.Assess(draft));
        }
        return result;
    }

    private static TestCaseEdits Clean(TestCaseEdits e)
    {
        static string? T(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : v.Trim()[..Math.Min(v.Trim().Length, max)];
        var steps = e.Steps?.Where(s => !string.IsNullOrWhiteSpace(s)).Take(50).Select(s => T(s, 2000)!).ToList();
        return new TestCaseEdits
        {
            Title = T(e.Title, 300), Preconditions = T(e.Preconditions, 4000), Steps = steps is { Count: > 0 } ? steps : null, ExpectedResult = T(e.ExpectedResult, 4000),
            ObservationMethod = T(e.ObservationMethod, 2000), EnvironmentPrerequisites = T(e.EnvironmentPrerequisites, 2000),
        };
    }

    private static List<TestSuiteDraft> Suites(IReadOnlyList<TestSuiteDraft> requested, SystemGuideContent guide, IReadOnlyList<string> ids)
    {
        var suites = requested.Where(s => !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Name)).GroupBy(s => s.Id, StringComparer.Ordinal).Select(g => g.Last())
            .Take(50).Select(s => s with { Name = s.Name.Trim()[..Math.Min(s.Name.Trim().Length, 200)], Origin = s.Origin == "stage" ? "stage" : "reviewer" }).ToList();
        if (suites.Count == 0)
            suites = E.ProposeSuites(guide, ids.Select(id => E.Generate(guide, guide.Scenarios.First(s => s.Id == id), false)));
        return suites;
    }

    private static List<string> Selected(SystemGuideContent guide, IEnumerable<string>? ids)
    {
        var known = guide.Scenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var selected = (ids ?? []).Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (selected.Count > MaxCases) throw new TestPlanException(400, "too-many-cases", $"At most {MaxCases} cases per proposal.");
        return selected;
    }

    /// <summary>The guide revision and, per source role, the snapshot analysis uses (the guide's own baseline-resolved reading) with its state.</summary>
    private async Task<TestPlanSourceBasis> BasisAsync(SystemGuideView view, CancellationToken ct)
    {
        var context = await baselines.ContextAsync(ct);
        var stateOf = context.Sources.SelectMany(s => s.Snapshots.Select(x => (x.SnapshotId, s.StateCode))).GroupBy(x => x.SnapshotId).ToDictionary(g => g.Key, g => g.First().StateCode);
        var roles = view.CurrentSources.Select(s => new TestPlanSourceRole(s.Role, s.Label, s.SnapshotId, s.Fingerprint,
            s.SnapshotId is { } id ? stateOf.GetValueOrDefault(id, "USED") : s.Label.Contains("BASELINE_NOT_SELECTED", StringComparison.Ordinal) ? "BASELINE_NOT_SELECTED" : "NOT_IMPORTED")).ToList();
        return new TestPlanSourceBasis
        {
            GuideKey = view.GuideKey, GuideRevision = view.Shown!.Revision, GuideFingerprint = view.Shown.ContentFingerprint, GuideReviewState = view.ReviewState, Roles = roles,
            SourceContextFingerprint = context.ContextFingerprint,
        };
    }

    private List<ExecutableScenarioOption> Executables()
    {
        var list = new List<ExecutableScenarioOption>();
        foreach (var pack in packs)
            foreach (var journey in pack.Journeys)
                foreach (var scenario in journey.Scenarios)
                    list.Add(new ExecutableScenarioOption(new ScenarioReference { Kind = ScenarioSourceKind.IntegrationJourney, Owner = $"{pack.PackId}/{journey.JourneyId}", ScenarioId = scenario.ScenarioId },
                        $"{journey.DisplayName} · {scenario.DisplayName}",
                        scenario.Support == JourneyScenarioSupport.Supported ? "Runnable journey scenario (as the pack declares; not a result)" : $"Not assessed in the pack: {scenario.NotAssessedBecause ?? scenario.Description}"));
        foreach (var flow in e2e.Flows(""))
            list.Add(new ExecutableScenarioOption(new ScenarioReference { Kind = ScenarioSourceKind.CriticalE2EFlow, Owner = flow.EnvironmentId, ScenarioId = flow.Id, Revision = flow.Version },
                $"Critical E2E · {flow.Name}", $"Automated flow v{flow.Version} ({flow.Module}); a definition, not a result"));
        return list;
    }

    private async Task<Dictionary<string, TestDataAssetSummary>> AssetsAsync(CancellationToken ct)
    {
        var view = await testData.ViewAsync(ct);
        return view.Assets.Where(a => !string.IsNullOrWhiteSpace(a.AssetId)).GroupBy(a => a.AssetId!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    private async Task<List<TestDataOption>> TestDataOptionsAsync(CancellationToken ct) =>
        [.. (await AssetsAsync(ct)).Values.OrderBy(a => a.Alias, StringComparer.OrdinalIgnoreCase).Select(a => new TestDataOption(a.AssetId!, a.Alias, a.Label, a.Kind.ToString(), a.Approval.ToString(),
            a.Validation.ToString(), a.Fingerprint, a.Schema?.Label ?? a.Schema?.SchemaPath, a.SchemaCheck?.Status.ToString(), a.Environment))];

    private static TestCaseDataLink? Link(string assetId, IReadOnlyDictionary<string, TestDataAssetSummary> assets) => assets.TryGetValue(assetId, out var a)
        ? new TestCaseDataLink { AssetId = assetId, Alias = a.Alias, Fingerprint = a.Fingerprint, Approval = a.Approval.ToString(), Validation = a.Validation.ToString(), SchemaCheck = a.SchemaCheck?.Status.ToString() }
        : null;

    // ── Persistence helpers ──────────────────────────────────────────────────────────────────────────────────

    private async Task<TestPlanDraftRecord> Row(Guid id, CancellationToken ct) =>
        await db.TestPlanDrafts.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new TestPlanException(404, "draft-not-found", "No such Test Plan draft.");

    private static TestPlanDraft Read(TestPlanDraftRecord row) => JsonSerializer.Deserialize<TestPlanDraft>(row.ContentJson, E.Json)! with { Revision = row.Revision };

    private async Task<TestPlanDraft> WriteAsync(TestPlanDraftRecord row, TestPlanDraft plan, CancellationToken ct)
    {
        Bound(plan);
        row.Revision = plan.Revision;
        row.PlanName = plan.PlanName;
        row.DeliveryLabel = plan.DeliveryLabel;
        row.ContentJson = JsonSerializer.Serialize(plan, E.Json);
        row.UpdatedBy = plan.UpdatedBy;
        row.UpdatedAt = plan.UpdatedAt;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw new TestPlanException(409, "draft-changed", "The proposal was changed by someone else. Reload it; your change was not saved.");
        }
        return plan;
    }

    private static void Bound(TestPlanDraft plan)
    {
        if (plan.Cases.Count > MaxCases) throw new TestPlanException(400, "too-many-cases", $"At most {MaxCases} cases per proposal.");
        if (JsonSerializer.Serialize(plan, E.Json).Length > MaxContentCharacters) throw new TestPlanException(413, "proposal-too-large", "The proposal is too large to store.");
    }

    private static TestPlanException Stale(TestPlanDraftRecord row) =>
        new(409, "draft-changed", $"The proposal is at revision {row.Revision}; reload it. Your change was not saved.");

    private static string Name(string? name) => string.IsNullOrWhiteSpace(name) ? throw new TestPlanException(400, "name-required", "Give the Test Plan a name.") : name.Trim()[..Math.Min(name.Trim().Length, 300)];
    private static string? Label(string? label) => string.IsNullOrWhiteSpace(label) ? null : label.Trim()[..Math.Min(label.Trim().Length, 200)];
    private static string? Note(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 1000)];

    private static TestPlanDraftSummary Summary(TestPlanDraft p) => new(p.Id, p.GuideKey, p.PlanName, p.DeliveryLabel, p.Revision, p.Cases.Count,
        p.Cases.Count(c => c.Review == TestCaseReviewState.Accepted), p.Cases.Count(c => c.Review == TestCaseReviewState.NeedsClarification),
        p.Cases.Count(c => c.Review != TestCaseReviewState.Excluded && c.Missing.Any(m => m.StartsWith("Expected result: NEEDS", StringComparison.Ordinal))),
        p.Cases.Count(c => c.Review != TestCaseReviewState.Excluded && c.TestData.Count == 0),
        p.Cases.Count(c => c.Review != TestCaseReviewState.Excluded && !c.Testability.Ready), p.UpdatedBy, p.UpdatedAt);

    private static TestPlanSnapshotSummary SnapshotSummary(TestPlanSnapshotRecord s) => new(s.Id, s.DraftId, s.GuideKey, s.Sequence, s.PlanName, s.DeliveryLabel, s.DraftRevision,
        s.ContentFingerprint, s.RuleVersion, s.GuideRevision, s.CaseCount, s.CreatedBy, s.CreatedAt, s.Note);
}
