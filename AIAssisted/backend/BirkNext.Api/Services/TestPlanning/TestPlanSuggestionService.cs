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
    IEnumerable<IIntegrationJourneyPack> packs, ICriticalE2EStore e2e, TimeProvider? clock = null, ScenarioDiscoveryCache? discoveries = null,
    ScenarioDiscoveryStore? discoveryStore = null) : IScenarioReviewSource
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
        // One load of the stored snapshots for the guide's sources and the baseline context (each load deserializes every snapshot's evidence).
        var snapshots = await baselines.SnapshotsAsync(ct);
        var view = await guides.GetAsync(key, false, ct, snapshots);
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
            Guides = options, GuideKey = key, Basis = await BasisAsync(view, ct, snapshots), Candidates = candidates,
            Stages = [.. shown.Content.Stages.Select(s => new TestPlanStage(s.Id, s.Title))],
            Drafts = [.. drafts.Select(d => Summary(Read(d)))], Snapshots = await SnapshotsAsync(key, ct), Executables = [.. executables],
            TestData = await TestDataOptionsAsync(ct), CanWrite = canWrite, RuleVersion = E.RuleVersion,
            SourceChanges = [.. view.SourceChanges.Select(c => $"{c.Label}: {c.Change} ({c.AffectedClaims} claims)")],
        };
    }

    public async Task<TestPlanDraft> DraftAsync(Guid id, CancellationToken ct)
    {
        var plan = Read(await Row(id, ct));
        return await FlagsAsync(plan, ct);
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
        var snapshots = await baselines.SnapshotsAsync(ct);
        var view = await guides.GetAsync(request.GuideKey, false, ct, snapshots);
        var shown = view.Shown ?? throw new TestPlanException(404, "guide-empty", "The system guide has no revision.");
        var ids = Selected(shown.Content, request.ScenarioIds);
        if (ids.Count == 0) throw new TestPlanException(400, "no-scenarios", "Select at least one scenario.");
        var cases = await BuildCasesAsync(request.GuideKey, shown, ids, new Dictionary<string, TestCaseDraft>(StringComparer.Ordinal), ct);
        var plan = new TestPlanDraft
        {
            Id = Guid.NewGuid(), GuideKey = request.GuideKey, PlanName = name, DeliveryLabel = Label(request.DeliveryLabel), Revision = 1, Basis = await BasisAsync(view, ct, snapshots),
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
        if (current.ProjectKey is not null) return await SaveProjectAsync(row, current, request, actor, ct);
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
        var cases = await BuildCasesAsync(current.GuideKey, revision, ids, previous, ct, human, suites, currentFingerprints: await CurrentFingerprintsAsync(current.GuideKey, ct), actor: actor);
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
        var plan = await FlagsAsync(Read(row), ct);
        var target = plan.Cases.FirstOrDefault(c => c.Source.ScenarioId == request.ScenarioId) ?? throw new TestPlanException(404, "case-not-found", "No such case in this proposal.");
        if (request.State == TestCaseReviewState.Accepted && string.IsNullOrWhiteSpace(target.EffectiveExpected))
            throw new TestPlanException(409, "expected-result-missing", "A case without an expected result cannot be accepted. Clarify the expected result first.");
        if (request.State == TestCaseReviewState.Accepted && target.SourceChanged)
            throw new TestPlanException(409, "source-changed", plan.ProjectKey is null ? "The scenario changed in the source since this draft was generated. Update the proposal from the current guide first."
                : "The scenario changed in the latest discovery since this case was prepared. Update the plan from the latest discovery first.");
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
        if (current.ProjectKey is not null) return await RefreshProjectAsync(row, current, actor, ct);
        var snapshots = await baselines.SnapshotsAsync(ct);
        var view = await guides.GetAsync(current.GuideKey, false, ct, snapshots);
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
            Basis = await BasisAsync(view, ct, snapshots), Cases = [.. cases, .. gone], Suites = suites, RuleVersion = E.RuleVersion, Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now,
        };
        return await WriteAsync(row, plan, ct);
    }

    /// <summary>Freezes the draft as an immutable snapshot. Test Data links are re-read first so the snapshot records the revisions in force.
    /// Identical content returns the existing snapshot (nothing new is written); a concurrent identical save resolves to the same one.</summary>
    public async Task<SaveTestPlanSnapshotResult> SaveSnapshotAsync(Guid id, SaveTestPlanSnapshotRequest request, string actor, CancellationToken ct)
    {
        var row = await Row(id, ct);
        if (row.Revision != request.ExpectedRevision) throw Stale(row);
        var plan = await FlagsAsync(Read(row), ct);
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

    /// <summary>The current fingerprint of every scenario of a guide (MT-7 coverage: is a review or an interpretation about this revision?).</summary>
    public Task<Dictionary<string, string>> ScenarioFingerprintsAsync(string guideKey, CancellationToken ct) => CurrentFingerprintsAsync(guideKey, ct);

    /// <summary>
    /// For each guide scenario, the tester review from the most recently updated draft that contains it (MT-7). <see cref="ScenarioReviewFact.Current"/>
    /// is true only when that review was made on the scenario revision readers see now. Reads drafts only; nothing is changed.
    /// </summary>
    public async Task<IReadOnlyDictionary<(string, string), ScenarioReviewFact>> ScenarioReviewsAsync(IReadOnlyCollection<string> guideKeys, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), ScenarioReviewFact>();
        foreach (var key in guideKeys.Where(k => guides.GuideKeys.Contains(k, StringComparer.Ordinal)))
        {
            var current = await CurrentFingerprintsAsync(key, ct);
            foreach (var row in await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey == key).OrderByDescending(d => d.UpdatedAt).ToListAsync(ct))
                foreach (var c in Read(row).Cases.Where(c => c.Source.Kind == ScenarioSourceKind.SystemGuide && c.Review != TestCaseReviewState.Superseded))
                {
                    if (result.ContainsKey((key, c.Source.ScenarioId))) continue; // the newest draft wins
                    var accepted = c.ReviewHistory.LastOrDefault(e => e.State == c.Review);
                    result[(key, c.Source.ScenarioId)] = new ScenarioReviewFact(key, c.Source.ScenarioId, row.Id, c.Review, c.Source.Fingerprint,
                        current.TryGetValue(c.Source.ScenarioId, out var now) && now == c.Source.Fingerprint, accepted?.Reviewer, accepted?.At);
                }
        }
        return result;
    }

    /// <summary>The fingerprint of every scenario in the guide revision readers see now (for change detection only).</summary>
    private async Task<Dictionary<string, string>> CurrentFingerprintsAsync(string guideKey, CancellationToken ct)
    {
        var (shown, _) = await guides.ShownAsync(guideKey, ct);
        return shown.Content.Scenarios.ToDictionary(s => s.Id, s => E.ScenarioFingerprint(shown.Content, s), StringComparer.Ordinal);
    }

    private async Task<List<TestCaseDraft>> BuildCasesAsync(string guideKey, SystemGuideRevision revision, IReadOnlyList<string> ids, IReadOnlyDictionary<string, TestCaseDraft> previous,
        CancellationToken ct, IReadOnlyDictionary<string, TestCaseHumanPart>? human = null, IReadOnlyList<TestSuiteDraft>? suites = null, bool refresh = false,
        IReadOnlyDictionary<string, string>? currentFingerprints = null, string? actor = null)
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
                EditHistory = History(before, part is null ? null : Clean(part.Edits), actor),
            };
            result.Add(E.Assess(draft));
        }
        return result;
    }

    private static TestCaseEdits Clean(TestCaseEdits e)
    {
        static string? T(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : v.Trim()[..Math.Min(v.Trim().Length, max)];
        // Steps and their expected results stay paired: a blank step is dropped together with its expected result.
        var pairs = e.Steps?.Select((s, i) => (Step: s, Expected: e.StepExpected is { } x && i < x.Count ? x[i] : null)).Where(p => !string.IsNullOrWhiteSpace(p.Step)).Take(50).ToList();
        var steps = pairs?.Select(p => T(p.Step, 2000)!).ToList();
        var stepExpected = pairs is { Count: > 0 } && e.StepExpected is not null ? pairs.Select(p => T(p.Expected, 2000)).ToList() : null;
        var template = e.TemplateValues?.Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value)).Take(20)
            .ToDictionary(kv => kv.Key.Trim()[..Math.Min(kv.Key.Trim().Length, 60)], kv => T(kv.Value, 1000)!, StringComparer.Ordinal);
        return new TestCaseEdits
        {
            StepExpected = steps is { Count: > 0 } ? stepExpected : null, Purpose = T(e.Purpose, 1000), Roles = T(e.Roles, 500), Dependencies = T(e.Dependencies, 1000),
            Priority = T(e.Priority, 50), TestDataNeed = e.TestDataNeed is TestDataNeed.Unknown ? null : e.TestDataNeed, BlockedBy = T(e.BlockedBy, 500),
            TemplateValues = template is { Count: > 0 } ? template : null,
            Title = T(e.Title, 300), Preconditions = T(e.Preconditions, 4000), Steps = steps is { Count: > 0 } ? steps : null, ExpectedResult = T(e.ExpectedResult, 4000),
            ObservationMethod = T(e.ObservationMethod, 2000), EnvironmentPrerequisites = T(e.EnvironmentPrerequisites, 2000),
            TestType = e.TestType is TestCaseType.NotClassified ? null : e.TestType,
            ExecutionMethod = e.ExecutionMethod is TestExecutionMethod.NotDecided ? null : e.ExecutionMethod,
            Performance = e.Performance is null ? null : CleanPerformance(e.Performance),
        };

        static PerformanceCaseDetails? CleanPerformance(PerformanceCaseDetails p)
        {
            var cleaned = new PerformanceCaseDetails
            {
                Subtype = p.Subtype, OperationUnderTest = T(p.OperationUnderTest, 500), Workload = T(p.Workload, 2000), Concurrency = T(p.Concurrency, 200),
                ArrivalRate = T(p.ArrivalRate, 200), RampUp = T(p.RampUp, 200), SteadyStateDuration = T(p.SteadyStateDuration, 200), RampDown = T(p.RampDown, 200),
                TargetEnvironment = T(p.TargetEnvironment, 500), DataVolume = T(p.DataVolume, 500), Measurements = T(p.Measurements, 2000),
                AcceptanceCriteria = T(p.AcceptanceCriteria, 2000), ThresholdSource = T(p.ThresholdSource, 500), Monitoring = T(p.Monitoring, 1000),
                ResponsibleRole = T(p.ResponsibleRole, 200), Cleanup = T(p.Cleanup, 1000), PerformanceReviewReference = T(p.PerformanceReviewReference, 300),
            };
            return cleaned.IsEmpty ? null : cleaned;
        }
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
    private async Task<TestPlanSourceBasis> BasisAsync(SystemGuideView view, CancellationToken ct, IReadOnlyList<IqrSourceSnapshot>? snapshots = null)
    {
        var context = snapshots is null ? await baselines.ContextAsync(ct) : await baselines.ContextAsync(snapshots, ct);
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

    // ── Project plans (project-scoped discovery) ─────────────────────────────────────────────────────────────

    /// <summary>The owner key stored with a project plan ("project:{key}"); hashed when the key would not fit the column. Never a name.</summary>
    public static string ProjectOwner(string projectKey)
    {
        var key = projectKey.Trim();
        var owner = "project:" + key;
        return owner.Length <= 100 ? owner : "project:#" + E.Sha(key)[..40];
    }

    /// <summary>This project's saved plans as stored (only owner "project:{key}" with the same ProjectKey). Unreadable rows are skipped, never guessed.</summary>
    public async Task<List<TestPlanDraft>> ProjectDraftsAsync(string projectKey, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(projectKey);
        var owner = ProjectOwner(key);
        var list = new List<TestPlanDraft>();
        foreach (var row in await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey == owner).ToListAsync(ct))
        {
            try { if (Read(row) is { } plan && plan.ProjectKey == key) list.Add(plan); }
            catch (JsonException) { }
        }
        return list;
    }

    /// <summary>The project's saved cases traced to a specification's requirements (<see cref="PreparedCaseTraceability"/>).</summary>
    public async Task<PreparedCaseTraceView> PreparedCaseTraceAsync(string projectKey, IReadOnlyCollection<string> specificationPaths, IReadOnlyCollection<string>? requirementIds,
        CancellationToken ct, IReadOnlyDictionary<string, string>? requirementTitles = null)
    {
        var key = TestScenarioDiscoveryService.Key(projectKey);
        return PreparedCaseTraceability.Build(key, await ProjectDraftsAsync(key, ct), specificationPaths, requirementIds, requirementTitles,
            await ManualTestRunService.ReadProjectRunsAsync(db, key, ct));
    }

    /// <summary>The project's plans and snapshots (only this project's), Test Data options and the Critical E2E flows of its environment.</summary>
    public async Task<TestPlanWorkspace> ProjectWorkspaceAsync(string projectKey, string? environmentId, bool canWrite, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(projectKey);
        var owner = ProjectOwner(key);
        var drafts = (await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey == owner).OrderByDescending(d => d.UpdatedAt).ToListAsync(ct))
            .Select(Read).Where(p => p.ProjectKey == key).ToList();
        var executables = string.IsNullOrWhiteSpace(environmentId) ? [] : e2e.Flows(environmentId).Select(flow => new ExecutableScenarioOption(
            new ScenarioReference { Kind = ScenarioSourceKind.CriticalE2EFlow, Owner = flow.EnvironmentId, ScenarioId = flow.Id, Revision = flow.Version },
            $"Critical E2E · {flow.Name}", $"Automated flow v{flow.Version} ({flow.Module}); a definition, not a result")).ToList();
        return new TestPlanWorkspace
        {
            ProjectKey = key, GuideKey = owner, Drafts = [.. drafts.Select(Summary)], Snapshots = await SnapshotsAsync(owner, ct), Executables = executables,
            TestData = await TestDataOptionsAsync(ct), CanWrite = canWrite, RuleVersion = E.RuleVersion,
        };
    }

    /// <summary>
    /// Prepares test cases from selected candidates of one discovery result of the same project (or adds them to the project's draft). Generated
    /// content is built on the server from the candidate; a candidate id the result does not hold is ignored, never trusted from the client.
    /// </summary>
    public async Task<TestPlanDraft> CreateProjectAsync(CreateProjectTestPlanRequest request, string actor, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(request.ProjectKey);
        var manualTitle = string.IsNullOrWhiteSpace(request.ManualCaseTitle) ? null : request.ManualCaseTitle.Trim()[..Math.Min(request.ManualCaseTitle.Trim().Length, 300)];
        var discovery = string.IsNullOrWhiteSpace(request.DiscoveryId) ? null
            : discoveries?.Find(key, request.DiscoveryId) ?? (discoveryStore is null ? null : await discoveryStore.FindAsync(key, request.DiscoveryId, ct));
        if (discovery is null && request.CandidateIds.Count > 0)
            throw new TestPlanException(409, "discovery-missing", "The discovery result is no longer available (BirkNext restarted or the project changed). Discover test scenarios again.");
        var wanted = request.CandidateIds.ToHashSet(StringComparer.Ordinal);
        var chosen = discovery?.Candidates.Where(c => wanted.Contains(c.CandidateId) && c.ProjectKey == key).ToList() ?? [];
        if (chosen.Count == 0 && manualTitle is null) throw new TestPlanException(400, "no-scenarios", "Select at least one scenario, or write a test case by hand.");
        if (chosen.Count > MaxCases) throw new TestPlanException(400, "too-many-cases", $"At most {MaxCases} cases per plan.");
        var cases = chosen.Select(c => ProjectCase(key, c)).ToList();
        if (manualTitle is not null) cases.Add(ManualCase(key, manualTitle, null, null));

        if (request.DraftId is { } draftId)
        {
            var row = await Row(draftId, ct);
            if (request.BaseRevision is { } baseRevision && row.Revision != baseRevision) throw Stale(row);
            var current = Read(row);
            if (current.ProjectKey != key) throw new TestPlanException(404, "draft-not-found", "No such Test Plan draft in this project.");
            var known = current.Cases.Select(c => c.Source.ScenarioId).ToHashSet(StringComparer.Ordinal);
            var added = cases.Where(c => !known.Contains(c.Source.ScenarioId)).ToList();
            var suites = current.Suites.Concat(TestScenarioDiscoveryEngine.ProjectSuites(added.Select(c => c.Generated)).Where(s => current.Suites.All(x => x.Id != s.Id))).ToList();
            return await WriteAsync(row, FlagProject(current with
            {
                Cases = [.. current.Cases, .. added], Suites = suites, Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now,
                SnapshotKey = discovery?.SnapshotKey ?? current.SnapshotKey, DiscoveryId = discovery?.DiscoveryId ?? current.DiscoveryId,
            }), ct);
        }

        var projectName = string.IsNullOrWhiteSpace(request.ProjectName) ? discovery?.ProjectName : request.ProjectName.Trim()[..Math.Min(request.ProjectName.Trim().Length, 200)];
        var plan = new TestPlanDraft
        {
            Id = Guid.NewGuid(), GuideKey = ProjectOwner(key), ProjectKey = key, ProjectName = projectName, SnapshotKey = discovery?.SnapshotKey, DiscoveryId = discovery?.DiscoveryId,
            ScopeIds = [.. request.ScopeIds.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).Take(20)],
            PlanName = string.IsNullOrWhiteSpace(request.PlanName) ? $"{projectName ?? key} – test plan" : Name(request.PlanName), DeliveryLabel = Label(request.DeliveryLabel), Revision = 1,
            Basis = new TestPlanSourceBasis { GuideKey = ProjectOwner(key), GuideReviewState = "Project discovery", Roles = [] },
            Suites = TestScenarioDiscoveryEngine.ProjectSuites(cases.Select(c => c.Generated)), Cases = cases, RuleVersion = E.RuleVersion,
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

    /// <summary>Adds a hand-written case to a project plan (title and purpose only; nothing generated).</summary>
    public async Task<TestPlanDraft> AddManualCaseAsync(Guid id, AddManualTestCaseRequest request, string actor, CancellationToken ct)
    {
        var row = await Row(id, ct);
        if (row.Revision != request.BaseRevision) throw Stale(row);
        var current = Read(row);
        if (current.ProjectKey is not { } key) throw new TestPlanException(400, "not-a-project-plan", "Hand-written cases are added to project plans.");
        if (string.IsNullOrWhiteSpace(request.Title)) throw new TestPlanException(400, "title-required", "Give the test case a title.");
        var manual = ManualCase(key, request.Title.Trim()[..Math.Min(request.Title.Trim().Length, 300)], request.Purpose, request.Type);
        List<TestSuiteDraft> suites = current.Suites.Any(s => s.Id == manual.SuiteId) ? current.Suites
            : [.. current.Suites, new TestSuiteDraft(manual.SuiteId, manual.Generated.GroupTitle ?? "Written by hand", "area", null)];
        return await WriteAsync(row, current with { Cases = [.. current.Cases, manual], Suites = suites, Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now }, ct);
    }

    private static TestCaseDraft ProjectCase(string projectKey, DiscoveredScenario c)
    {
        var content = TestScenarioDiscoveryEngine.Content(c);
        return E.Assess(new TestCaseDraft
        {
            Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = projectKey, ScenarioId = c.CandidateId, Fingerprint = c.Fingerprint },
            SuiteId = E.SuiteFor(content), Generated = content,
        });
    }

    private static TestCaseDraft ManualCase(string projectKey, string title, string? purpose, ScenarioCandidateType? type)
    {
        var content = new TestCaseContent
        {
            Title = title, Purpose = string.IsNullOrWhiteSpace(purpose) ? "" : purpose.Trim()[..Math.Min(purpose.Trim().Length, 1000)], CandidateType = type,
            StageId = TestScenarioDiscoveryEngine.AreaSuiteId("written-by-hand"), GroupTitle = "Written by hand", ExpectedBasis = ExpectedResultBasis.Missing,
        };
        return E.Assess(new TestCaseDraft
        {
            Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = projectKey, ScenarioId = "manual-" + Guid.NewGuid().ToString("N")[..12] },
            SuiteId = E.SuiteFor(content), Generated = content,
        });
    }

    /// <summary>Saves the human-owned parts of a project plan. Generated content stays as stored (a client cannot rewrite it); cases are only
    /// removed here — adding goes through <see cref="CreateProjectAsync"/> so generated content always comes from a discovery result.</summary>
    private async Task<TestPlanDraft> SaveProjectAsync(TestPlanDraftRecord row, TestPlanDraft current, SaveTestPlanDraftRequest request, string actor, CancellationToken ct)
    {
        await RestoreDiscoveryAsync(current.ProjectKey, ct);
        var previous = current.Cases.ToDictionary(c => c.Source.ScenarioId, StringComparer.Ordinal);
        var ids = (request.ScenarioIds ?? []).Distinct(StringComparer.Ordinal).Where(previous.ContainsKey).ToList();
        if (ids.Count == 0) throw new TestPlanException(400, "no-scenarios", "A plan keeps at least one test case.");
        var human = request.Cases.GroupBy(c => c.ScenarioId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var suites = request.Suites.Where(s => !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Name)).GroupBy(s => s.Id, StringComparer.Ordinal).Select(g => g.Last())
            .Take(50).Select(s => s with { Name = s.Name.Trim()[..Math.Min(s.Name.Trim().Length, 200)], Origin = s.Origin is "area" or "stage" ? s.Origin : "reviewer" }).ToList();
        if (suites.Count == 0) suites = current.Suites;
        var assets = await AssetsAsync(ct);
        var executables = Executables().GroupBy(e => e.Reference.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var catalog = RequirementCatalog(current.ProjectKey!, request.RequirementCatalog);
        var cases = new List<TestCaseDraft>();
        foreach (var id in ids)
        {
            var before = previous[id];
            human.TryGetValue(id, out var part);
            var edits = part is null ? before.Edits : Clean(part.Edits);
            var requirementLinks = part?.RequirementLinks is { } wanted ? RequirementLinks(before, wanted, catalog, actor) : before.RequirementLinks;
            var dataIds = part?.TestDataAssetIds ?? [.. before.TestData.Select(d => d.AssetId)];
            var links = new List<TestCaseDataLink>();
            foreach (var assetId in dataIds.Distinct(StringComparer.Ordinal).Take(20))
                if (Link(assetId, assets) is { } link) links.Add(link);
                else if (before.TestData.FirstOrDefault(d => d.AssetId == assetId) is { } old) links.Add(old with { Resolved = false });
            var correlations = new List<ScenarioCorrelation>();
            foreach (var c in before.Correlations)
            {
                var manual = c.Basis.StartsWith("Linked by a reviewer", StringComparison.Ordinal);
                if (part is not null && manual && !part.ManualLinks.Contains(c.Target.Key)) continue; // unlinked by the reviewer
                correlations.Add(part is not null && part.Correlations.TryGetValue(c.Target.Key, out var state) ? c with { State = state } : c);
            }
            foreach (var key in (part?.ManualLinks ?? []).Where(k => executables.ContainsKey(k) && correlations.All(c => c.Target.Key != k)))
                correlations.Add(new ScenarioCorrelation { Target = executables[key].Reference, Label = executables[key].Label, State = CorrelationState.Confirmed, Capability = executables[key].Detail, Basis = "Linked by a reviewer." });
            var suite = part?.SuiteId is { Length: > 0 } chosenSuite ? chosenSuite : before.SuiteId;
            if (suites.All(x => x.Id != suite)) suite = suites.Any(x => x.Id == E.SuiteFor(before.Generated)) ? E.SuiteFor(before.Generated) : suites[0].Id;
            cases.Add(E.Assess(before with
            {
                Edits = edits, EditHistory = History(before, part is null ? null : edits, actor, !SameLinks(before.RequirementLinks, requirementLinks)), TestData = links,
                Correlations = correlations, Risk = part?.Risk ?? before.Risk, SuiteId = suite, RequirementLinks = requirementLinks,
            }));
        }
        var plan = current with
        {
            PlanName = string.IsNullOrWhiteSpace(request.PlanName) ? current.PlanName : Name(request.PlanName), DeliveryLabel = Label(request.DeliveryLabel),
            Revision = row.Revision + 1, Suites = suites, Cases = cases, UpdatedBy = actor, UpdatedAt = Now,
        };
        return await WriteAsync(row, FlagProject(plan), ct);
    }

    /// <summary>Rebuilds generated content from the project's latest discovery. Human edits, links, Test Data and review history are kept; an accepted
    /// case whose candidate changed goes back to review; manual cases and cases not in the latest discovery are left untouched.</summary>
    private async Task<TestPlanDraft> RefreshProjectAsync(TestPlanDraftRecord row, TestPlanDraft current, string actor, CancellationToken ct)
    {
        await RestoreDiscoveryAsync(current.ProjectKey, ct);
        var latest = discoveries?.Latest(current.ProjectKey!) ?? throw new TestPlanException(409, "discovery-missing", "Discover test scenarios for this project first, then update the plan.");
        var byId = latest.Candidates.ToDictionary(c => c.CandidateId, StringComparer.Ordinal);
        var cases = current.Cases.Select(c =>
        {
            if (c.Source.Kind != ScenarioSourceKind.ProjectDiscovery || !byId.TryGetValue(c.Source.ScenarioId, out var candidate) || candidate.Fingerprint == c.Source.Fingerprint)
                return E.Assess(c with { SourceChanged = false });
            var review = c.Review;
            var history = c.ReviewHistory;
            if (review == TestCaseReviewState.Accepted)
            {
                review = TestCaseReviewState.UnderReview;
                history = [.. history, new TestCaseReviewEvent(TestCaseReviewState.UnderReview, "BirkNext", "The scenario changed in the latest discovery; review it again.", Now)];
            }
            // Generated content is replaced; the person's edits (Edits) are kept as they are and still take precedence.
            return E.Assess(c with
            {
                Generated = TestScenarioDiscoveryEngine.Content(candidate), Source = c.Source with { Fingerprint = candidate.Fingerprint }, Review = review, ReviewHistory = history, SourceChanged = false,
            });
        }).ToList();
        var suites = current.Suites.Concat(TestScenarioDiscoveryEngine.ProjectSuites(cases.Select(c => c.Generated)).Where(s => current.Suites.All(x => x.Id != s.Id))).ToList();
        var plan = current with
        {
            Cases = cases, Suites = suites, SnapshotKey = latest.SnapshotKey, DiscoveryId = latest.DiscoveryId, RuleVersion = E.RuleVersion, Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now,
        };
        return await WriteAsync(row, plan, ct);
    }

    /// <summary>Source-change flags: for a project plan from the project's latest discovery in this process (none known → nothing flagged), for a
    /// guide plan from the guide revision readers see now.</summary>
    private async Task<TestPlanDraft> FlagsAsync(TestPlanDraft plan, CancellationToken ct)
    {
        if (plan.ProjectKey is null) return WithSourceFlags(plan, await CurrentFingerprintsAsync(plan.GuideKey, ct));
        await RestoreDiscoveryAsync(plan.ProjectKey, ct);
        return FlagProject(plan);
    }

    /// <summary>After a restart the project's last discovery is read back from storage, so source-change flags and updates keep working.</summary>
    private async Task RestoreDiscoveryAsync(string? projectKey, CancellationToken ct)
    {
        if (projectKey is not null && discoveryStore is not null) await discoveryStore.RestoreAsync(projectKey, null, ct);
    }

    private TestPlanDraft FlagProject(TestPlanDraft plan)
    {
        if (plan.ProjectKey is not { } key || discoveries?.Latest(key) is not { } latest) return plan;
        var current = latest.Candidates.ToDictionary(c => c.CandidateId, c => c.Fingerprint, StringComparer.Ordinal);
        return plan with
        {
            Cases = [.. plan.Cases.Select(c => c with
            {
                SourceChanged = c.Source.Kind == ScenarioSourceKind.ProjectDiscovery && c.Review != TestCaseReviewState.Superseded
                    && current.TryGetValue(c.Source.ScenarioId, out var now) && now != c.Source.Fingerprint,
            })],
        };
    }

    /// <summary>The case's edit history plus one event naming the human-owned fields that changed in this save (none → unchanged).</summary>
    private List<TestCaseEditEvent> History(TestCaseDraft? before, TestCaseEdits? after, string? actor, bool linksChanged = false)
    {
        var history = before?.EditHistory ?? [];
        if (before is null || after is null || actor is null) return history;
        var fields = ChangedFields(before.Edits, after);
        if (linksChanged) fields.Add(LinkedRequirementsField);
        return fields.Count == 0 ? history : [.. history.TakeLast(99), new TestCaseEditEvent(actor, Now, fields)];
    }

    // ── Manual requirement links ─────────────────────────────────────────────────────────────────────────────

    public const string LinkedRequirementsField = "Linked requirements";
    public const int MaxRequirementLinks = 50;

    /// <summary>The save's requirement catalog keyed by (id, specification file); refused when it belongs to another project. Null = none sent.</summary>
    private static Dictionary<(string, string), ProjectRequirementOption>? RequirementCatalog(string projectKey, ProjectRequirementCatalog? catalog)
    {
        if (catalog is null) return null;
        if (!string.Equals(catalog.ProjectKey?.Trim(), projectKey, StringComparison.Ordinal))
            throw new TestPlanException(400, "requirement-other-project", "These requirements belong to another project. Open this plan's own project to link its requirements.");
        var map = new Dictionary<(string, string), ProjectRequirementOption>();
        foreach (var r in catalog.Requirements.Where(r => !string.IsNullOrWhiteSpace(r.RequirementId) && !string.IsNullOrWhiteSpace(r.SpecificationPath)).Take(5000))
            map.TryAdd(LinkKey(r.RequirementId, r.SpecificationPath), r);
        return map;
    }

    /// <summary>
    /// The case's manual links after a save. A link the case already holds is kept as recorded (who, when, title) even when its requirement has
    /// left the specification since — traceability shows it as such; the tester removes it explicitly. A new link must name a requirement of the
    /// project's catalog (never free text); the server records who and when. Automatic links (the case's evidence) are not touched here.
    /// </summary>
    private List<ManualRequirementLink> RequirementLinks(TestCaseDraft before, List<ManualRequirementLink> wanted, Dictionary<(string, string), ProjectRequirementOption>? catalog, string actor)
    {
        var held = before.RequirementLinks.GroupBy(l => LinkKey(l.RequirementId, l.SpecificationPath)).ToDictionary(g => g.Key, g => g.First());
        var result = new List<ManualRequirementLink>();
        foreach (var w in wanted.Where(w => !string.IsNullOrWhiteSpace(w.RequirementId)).DistinctBy(w => LinkKey(w.RequirementId, w.SpecificationPath)))
        {
            var key = LinkKey(w.RequirementId, w.SpecificationPath);
            if (held.TryGetValue(key, out var kept)) { result.Add(kept); continue; }
            if (catalog is null)
                throw new TestPlanException(400, "requirements-unavailable", "The project's requirements could not be read, so no new requirement link can be saved. Open the project's specification and try again.");
            if (!catalog.TryGetValue(key, out var requirement))
                throw new TestPlanException(400, "requirement-unknown", $"{w.RequirementId.Trim()} is not a requirement of this project's specification. Choose a requirement from the list.");
            result.Add(new ManualRequirementLink
            {
                RequirementId = requirement.RequirementId.Trim(), SpecificationPath = requirement.SpecificationPath.Trim(),
                Title = requirement.Title.Trim()[..Math.Min(requirement.Title.Trim().Length, 500)], LinkedBy = actor, LinkedAt = Now,
            });
        }
        if (result.Count > MaxRequirementLinks)
            throw new TestPlanException(400, "too-many-requirement-links", $"A test case links at most {MaxRequirementLinks} requirements.");
        return result;
    }

    private static (string, string) LinkKey(string id, string? path) => (id.Trim().ToUpperInvariant(), (path ?? "").Replace('\\', '/').Trim().TrimStart('/').ToUpperInvariant());

    private static bool SameLinks(List<ManualRequirementLink> a, List<ManualRequirementLink> b) =>
        a.Select(l => LinkKey(l.RequirementId, l.SpecificationPath)).OrderBy(k => k).SequenceEqual(b.Select(l => LinkKey(l.RequirementId, l.SpecificationPath)).OrderBy(k => k));

    /// <summary>The human-owned fields that differ between two edit sets (names only, never values).</summary>
    public static List<string> ChangedFields(TestCaseEdits a, TestCaseEdits b)
    {
        var fields = new List<string>();
        void C(string name, object? x, object? y) { if (JsonSerializer.Serialize(x, E.Json) != JsonSerializer.Serialize(y, E.Json)) fields.Add(name); }
        C("Title", a.Title, b.Title); C("Purpose", a.Purpose, b.Purpose); C("Preconditions", a.Preconditions, b.Preconditions); C("Steps", a.Steps, b.Steps);
        C("Step results", a.StepExpected, b.StepExpected); C("Expected result", a.ExpectedResult, b.ExpectedResult); C("Observation method", a.ObservationMethod, b.ObservationMethod);
        C("Environment", a.EnvironmentPrerequisites, b.EnvironmentPrerequisites); C("Test type", a.TestType, b.TestType); C("Execution method", a.ExecutionMethod, b.ExecutionMethod);
        C("Performance", a.Performance, b.Performance); C("Roles", a.Roles, b.Roles); C("Dependencies", a.Dependencies, b.Dependencies); C("Priority", a.Priority, b.Priority);
        C("Test Data need", a.TestDataNeed, b.TestDataNeed); C("Blocked by", a.BlockedBy, b.BlockedBy); C("Template values", a.TemplateValues, b.TemplateValues);
        return fields;
    }

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
