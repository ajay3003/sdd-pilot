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
public sealed partial class TestPlanSuggestionService(AppDbContext db, SystemGuideService guides, SourceBaselineService baselines, TestDataCatalogService testData,
    IEnumerable<IIntegrationJourneyPack> packs, ICriticalE2EStore e2e, TimeProvider? clock = null, ScenarioDiscoveryCache? discoveries = null,
    ScenarioDiscoveryStore? discoveryStore = null, BirkNext.Api.Services.IntegrationJourneys.JourneyRequirementLinkStore? requirementLinks = null,
    BirkNext.Api.Services.IntegrationJourneys.DeclaredIntegrationJourneyStore? declaredJourneys = null,
    BirkNext.Api.Services.IntegrationSuites.IntegrationSuiteService? integrationSuites = null,
    BirkNext.Api.Services.Specifications.SpecificationCatalogService? specificationCatalogs = null, ScenarioSourceBasisResolver? basisResolver = null) : IScenarioReviewSource
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
        var drafts = await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey == key && d.Lifecycle == ActiveState).OrderByDescending(d => d.UpdatedAt).ToListAsync(ct);
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
        var drafts = await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey == key && d.Lifecycle == ActiveState).OrderByDescending(d => d.UpdatedAt).ToListAsync(ct);
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

    /// <summary>What still stops approval (the export's content rules), or null.</summary>
    public static string? ApproveBlocker(TestCaseDraft c)
    {
        var steps = c.EffectiveSteps;
        if (steps.Count == 0 || steps.All(string.IsNullOrWhiteSpace)) return "add at least one step with its expected result.";
        var expected = c.EffectiveStepExpected;
        var noAction = steps.Select((a, i) => (a, i)).Where(x => string.IsNullOrWhiteSpace(x.a)).Select(x => x.i + 1).ToList();
        if (noAction.Count > 0) return $"step {string.Join(", ", noAction)} need{(noAction.Count == 1 ? "s" : "")} an action.";
        var missing = steps.Select((_, i) => i).Where(i => i >= expected.Count || string.IsNullOrWhiteSpace(expected[i])).Select(i => i + 1).ToList();
        return missing.Count == 0 ? null : $"step {string.Join(", ", missing)} need{(missing.Count == 1 ? "s" : "")} an expected result.";
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
        AddRevision(plan, "Created");
        await db.SaveChangesAsync(ct);
        return plan;
    }

    /// <summary>Saves the human-owned parts. Generated content is server state: kept as stored for existing cases (a client cannot rewrite it) and
    /// generated for added scenarios from the draft's own guide revision, only while that revision is unchanged. A scenario that changed in the
    /// guide since is flagged (SourceChanged), never silently rewritten. A save from an older draft revision is refused with 409.</summary>
    public async Task<TestPlanDraft> SaveAsync(Guid id, SaveTestPlanDraftRequest request, string actor, CancellationToken ct)
    {
        var row = await Mutable(id, ct);
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
        var row = await Mutable(id, ct);
        if (row.Revision != request.BaseRevision) throw Stale(row);
        if (request.State is TestCaseReviewState.Generated or TestCaseReviewState.Superseded)
            throw new TestPlanException(400, "invalid-review-state", "Generated and Superseded are set by BirkNext, not by a review.");
        var rationale = request.Rationale?.Trim();
        if (request.State is TestCaseReviewState.Excluded or TestCaseReviewState.NeedsClarification && string.IsNullOrWhiteSpace(rationale))
            throw new TestPlanException(400, "rationale-required", "Say why (a rationale is required to exclude a case or ask for clarification).");
        var plan = await FlagsAsync(Read(row), ct);
        var target = plan.Cases.FirstOrDefault(c => c.Source.ScenarioId == request.ScenarioId) ?? throw new TestPlanException(404, "case-not-found", "No such case in this proposal.");
        // Approving means ready for the Azure DevOps file: the same content rules as the export (a step, an action and an expected result on
        // every step). The case-level expected result is not part of an Azure DevOps test case and is not required.
        if (request.State == TestCaseReviewState.Accepted && ApproveBlocker(target) is { } blocker)
            throw new TestPlanException(409, "steps-incomplete", $"This case cannot be approved yet: {blocker}");
        if (request.State == TestCaseReviewState.Accepted && target.HasUnresolvedConflict)
            throw new TestPlanException(409, "conflict-unresolved", E.ConflictReason + " Registrer avklaringen på testtilfellet først.");
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

    /// <summary>
    /// Excludes a whole scenario: each of its test cases (not yet excluded or superseded) gets the Excluded decision with the same reason, in one saved
    /// revision. Nothing is deleted: the cases, their history and the scenario's evidence stay, and each case can be reviewed again later.
    /// </summary>
    public async Task<TestPlanDraft> ExcludeScenarioAsync(Guid id, ExcludeScenarioRequest request, string actor, CancellationToken ct)
    {
        var row = await Mutable(id, ct);
        if (row.Revision != request.BaseRevision) throw Stale(row);
        var rationale = request.Rationale?.Trim();
        if (string.IsNullOrWhiteSpace(rationale)) throw new TestPlanException(400, "rationale-required", "Say why the scenario is excluded.");
        var plan = Read(row);
        var cases = plan.Cases.Where(c => (c.ParentScenario ?? "") == (request.ScenarioKey ?? "") && c.Review is not (TestCaseReviewState.Excluded or TestCaseReviewState.Superseded)).ToList();
        if (cases.Count == 0) throw new TestPlanException(404, "scenario-not-found", "That scenario has no test case to exclude.");
        var ids = cases.Select(c => c.Source.ScenarioId).ToHashSet(StringComparer.Ordinal);
        var reason = rationale[..Math.Min(rationale.Length, 1000)];
        plan = plan with
        {
            Cases = [.. plan.Cases.Select(c => ids.Contains(c.Source.ScenarioId)
                ? c with { Review = TestCaseReviewState.Excluded, ReviewHistory = [.. c.ReviewHistory, new TestCaseReviewEvent(TestCaseReviewState.Excluded, actor, $"Scenario excluded: {reason}", Now)] }
                : c)],
            Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now,
        };
        return await WriteAsync(row, plan, ct, $"Scenario excluded ({cases.Count} test case{(cases.Count == 1 ? "" : "s")})");
    }

    /// <summary>Creates a new proposal revision against the guide's current revision and sources: generated content is rebuilt, human edits,
    /// links and review history are kept, and a case whose scenario changed goes back to review (flagged), never silently accepted.</summary>
    public async Task<TestPlanDraft> RefreshAsync(Guid id, int baseRevision, string actor, CancellationToken ct)
    {
        var row = await Mutable(id, ct);
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
            foreach (var row in await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey == key && d.Lifecycle == ActiveState).OrderByDescending(d => d.UpdatedAt).ToListAsync(ct))
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
            TestKind = e.TestKind is ScenarioTestKind.Manual or ScenarioTestKind.Technical ? e.TestKind : null,
            TestLevel = e.TestLevel, ScenarioType = e.ScenarioType,
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
        foreach (var row in await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey == owner && d.Lifecycle == ActiveState).ToListAsync(ct))
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
        var view = PreparedCaseTraceability.Build(key, await ProjectDraftsAsync(key, ct), specificationPaths, requirementIds, requirementTitles);
        return await BirkNext.Api.Services.IntegrationSuites.IntegrationRequirementTrace.WithIntegrationAsync(view, projectKey.Trim(), requirementLinks, declaredJourneys, integrationSuites, ct);
    }

    /// <summary>The project's plans and snapshots (only this project's), Test Data options and the Critical E2E flows of its environment.</summary>
    public async Task<TestPlanWorkspace> ProjectWorkspaceAsync(string projectKey, string? environmentId, bool canWrite, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(projectKey);
        var owner = ProjectOwner(key);
        var rows = (await db.TestPlanDrafts.AsNoTracking().Where(d => d.GuideKey == owner).OrderByDescending(d => d.UpdatedAt).ToListAsync(ct))
            .Select(r => (Plan: Read(r), Row: r)).Where(x => x.Plan.ProjectKey == key).ToList();
        var drafts = rows.Where(x => IsActive(x.Row)).Select(x => x.Plan).ToList();
        // The application's plans from earlier imports of it (a newer import continues them) and the current basis of the application.
        if (discoveryStore is not null) await discoveryStore.RestoreAsync(key, null, ct);
        var current = discoveries?.Latest(key);
        var application = current?.Basis?.ApplicationKey ?? drafts.Select(d => d.ApplicationKey).FirstOrDefault(a => a is not null);
        if (application is not null)
        {
            if (discoveryStore is not null) await discoveryStore.RestoreApplicationAsync(application, ct);
            var others = await ApplicationDraftsAsync(application, key, ct);
            rows = [.. rows, .. others];
            drafts = [.. drafts, .. others.Where(x => IsActive(x.Row)).Select(x => x.Plan)];
            drafts = [.. drafts.OrderByDescending(d => d.UpdatedAt)];
        }
        var status = new Dictionary<Guid, PlanSourceStatus>();
        foreach (var d in drafts.Take(20)) status[d.Id] = await StatusAsync(d, ct);
        var currentBasis = (application is null ? null : discoveries?.CurrentForApplication(application))?.Basis ?? current?.Basis;
        var executables = string.IsNullOrWhiteSpace(environmentId) ? [] : e2e.Flows(environmentId).Select(flow => new ExecutableScenarioOption(
            new ScenarioReference { Kind = ScenarioSourceKind.CriticalE2EFlow, Owner = flow.EnvironmentId, ScenarioId = flow.Id, Revision = flow.Version },
            $"Critical E2E · {flow.Name}", $"Automated flow v{flow.Version} ({flow.Module}); a definition, not a result")).ToList();
        return new TestPlanWorkspace
        {
            ProjectKey = key, GuideKey = owner, Drafts = [.. drafts.Select(d => Summary(d, rows.First(x => x.Plan.Id == d.Id).Row))],
            ArchivedDrafts = [.. rows.Where(x => x.Row.Lifecycle == ArchivedState).OrderByDescending(x => x.Row.LifecycleChangedAt).Select(x => Summary(x.Plan, x.Row))], Snapshots = await SnapshotsAsync(owner, ct), Executables = executables,
            TestData = await TestDataOptionsAsync(ct), CanWrite = canWrite, RuleVersion = E.RuleVersion, CurrentBasis = currentBasis, PlanStatus = status,
        };
    }

    /// <summary>
    /// Prepares test cases from selected candidates of one discovery result of the same project (or adds them to the project's draft). Generated
    /// content is built on the server from the candidate; a candidate id the result does not hold is ignored, never trusted from the client.
    /// </summary>
    public async Task<TestPlanDraft> CreateProjectAsync(CreateProjectTestPlanRequest request, string actor, CancellationToken ct)
    {
        // Two simultaneous requests for the same «Opprett testplan» draft: one creates, the other is told (never a second plan).
        if (request.DraftId is not null || string.IsNullOrWhiteSpace(request.CreationRequestId)) return await CreateProjectCoreAsync(request, actor, ct);
        var guard = $"{TestScenarioDiscoveryService.Key(request.ProjectKey)}|{request.CreationRequestId.Trim()}";
        if (!CreatingPlans.TryAdd(guard, 0)) throw new TestPlanException(409, "create-running", "Testplanen opprettes allerede. Vent litt.");
        try { return await CreateProjectCoreAsync(request, actor, ct); }
        finally { CreatingPlans.TryRemove(guard, out _); }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> CreatingPlans = new(StringComparer.Ordinal);

    private async Task<TestPlanDraft> CreateProjectCoreAsync(CreateProjectTestPlanRequest request, string actor, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(request.ProjectKey);
        var manualTitle = string.IsNullOrWhiteSpace(request.ManualCaseTitle) ? null : request.ManualCaseTitle.Trim()[..Math.Min(request.ManualCaseTitle.Trim().Length, 300)];
        var discovery = string.IsNullOrWhiteSpace(request.DiscoveryId) ? null
            : discoveries?.Find(key, request.DiscoveryId) ?? (discoveryStore is null ? null : await discoveryStore.FindAsync(key, request.DiscoveryId, ct));
        if (discovery is { IsCurrent: false })
            throw new TestPlanException(409, "historical-discovery", "Dette er et historisk innsyn i en eldre kildeversjon. Testtilfeller forberedes fra gjeldende kildegrunnlag; oppdag testscenarier på nytt uten historisk innsyn.");
        if (discovery is null && request.CandidateIds.Count > 0)
            throw new TestPlanException(409, "discovery-missing", "The discovery result is no longer available (BirkNext restarted or the project changed). Discover test scenarios again.");
        var wanted = request.CandidateIds.ToHashSet(StringComparer.Ordinal);
        var chosen = SpecificationOrder(discovery?.Candidates.Where(c => wanted.Contains(c.CandidateId) && c.ProjectKey == key).ToList() ?? []);
        var empty = request.AllowEmpty && request.DraftId is null && chosen.Count == 0 && manualTitle is null;
        if (empty && string.IsNullOrWhiteSpace(request.PlanName)) throw new TestPlanException(400, "name-required", "Gi testplanen et navn.");
        if (chosen.Count == 0 && manualTitle is null && !empty) throw new TestPlanException(400, "no-scenarios", "Select at least one scenario, or write a test case by hand.");
        // The same «Opprett testplan» draft again (double-click, retry after a lost answer): the plan already created from it, never a second one.
        if (request.DraftId is null && !string.IsNullOrWhiteSpace(request.CreationRequestId) && await CreatedFromAsync(key, request.CreationRequestId.Trim(), ct) is { } already) return already;
        if (chosen.Count > MaxCases) throw new TestPlanException(400, "too-many-cases", $"At most {MaxCases} cases per plan.");
        // «Legg til scenario» (2026-11-26): scenario-only items instead of generated cases (no case is created).
        var cases = request.ScenarioOnly ? new List<TestCaseDraft>() : chosen.Select(c => ProjectCase(key, c, discovery?.Basis)).ToList();
        var items = request.ScenarioOnly ? chosen.Select((c, i) => Planned(c, discovery?.SnapshotKey, actor, i)).ToList() : new List<PlannedScenario>();
        if (manualTitle is not null) cases.Add(ManualCase(key, manualTitle, null, null));

        if (request.DraftId is { } draftId)
        {
            var row = await Mutable(draftId, ct);
            if (request.BaseRevision is { } baseRevision && row.Revision != baseRevision) throw Stale(row);
            var current = Read(row);
            // A plan of an earlier import of the same application continues here (a plan follows the application, never another application).
            var sameApplication = current.ApplicationKey is { } app && discovery?.Basis?.ApplicationKey == app;
            if (current.ProjectKey != key && !sameApplication) throw new TestPlanException(404, "draft-not-found", "No such Test Plan draft in this project.");
            var known = current.Cases.Select(c => c.Source.ScenarioId).ToHashSet(StringComparer.Ordinal);
            var knownSources = current.Cases.Select(c => c.Generated.SourceIdentity).OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (request.ScenarioOnly)
            {
                // A scenario the plan already holds — as an item or by a case — is never planned twice (no duplicate membership).
                var planned = current.PlannedScenarios ?? [];
                bool Holds(PlannedScenario p) => known.Contains(p.ScenarioId) || p.SourceIdentity is { } si && knownSources.Contains(si)
                    || planned.Any(x => x.ScenarioId == p.ScenarioId || p.SourceIdentity is { } s2 && x.SourceIdentity == s2);
                var newItems = items.Where(p => !Holds(p)).ToList();
                if (newItems.Count == 0) return current;
                var suite = request.SuiteId is { Length: > 0 } sid ? current.Suites.FirstOrDefault(s => s.Id == sid)?.Id ?? throw new TestPlanException(400, "suite-unknown", "Testgruppen finnes ikke i testplanen.")
                    : current.Suites.FirstOrDefault()?.Id;
                var itemSuites = current.Suites;
                if (suite is null)
                {
                    var fresh = new TestSuiteDraft("suite:p:" + Guid.NewGuid().ToString("N")[..10], "Planlagte scenarioer", "planned", null);
                    itemSuites = [.. current.Suites, fresh];
                    suite = fresh.Id;
                }
                var order = planned.Count;
                return await WriteAsync(row, current with
                {
                    PlannedScenarios = [.. planned, .. newItems.Select((p, i) => p with { SuiteId = suite, Order = order + i })], Suites = itemSuites,
                    Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now,
                }, ct, $"{newItems.Count} scenario{(newItems.Count == 1 ? "" : "er")} lagt til uten testtilfelle");
            }
            var added = cases.Where(c => !known.Contains(c.Source.ScenarioId) && (c.Generated.SourceIdentity is null || !knownSources.Contains(c.Generated.SourceIdentity))).ToList();
            var suites = current.Suites.Concat(TestScenarioDiscoveryEngine.ProjectSuites(added.Select(c => c.Generated)).Where(s => current.Suites.All(x => x.Id != s.Id))).ToList();
            return await WriteAsync(row, FlagProject(current with
            {
                Cases = [.. current.Cases, .. added], Suites = suites, Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now,
                SnapshotKey = discovery?.SnapshotKey ?? current.SnapshotKey, DiscoveryId = discovery?.DiscoveryId ?? current.DiscoveryId,
                ApplicationKey = current.ApplicationKey ?? discovery?.Basis?.ApplicationKey, DiscoveryBasis = discovery?.Basis ?? current.DiscoveryBasis,
            }), ct);
        }

        var projectName = string.IsNullOrWhiteSpace(request.ProjectName) ? discovery?.ProjectName : request.ProjectName.Trim()[..Math.Min(request.ProjectName.Trim().Length, 200)];
        var plan = new TestPlanDraft
        {
            Id = Guid.NewGuid(), CreationRequestId = string.IsNullOrWhiteSpace(request.CreationRequestId) ? null : request.CreationRequestId.Trim()[..Math.Min(request.CreationRequestId.Trim().Length, 64)],
            GuideKey = ProjectOwner(key), ProjectKey = key, ProjectName = projectName, SnapshotKey = discovery?.SnapshotKey, DiscoveryId = discovery?.DiscoveryId,
            ApplicationKey = discovery?.Basis?.ApplicationKey, DiscoveryBasis = discovery?.Basis,
            ScopeIds = [.. request.ScopeIds.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).Take(20)],
            PlanName = string.IsNullOrWhiteSpace(request.PlanName) ? $"{projectName ?? key} – test plan" : Name(request.PlanName), DeliveryLabel = Label(request.DeliveryLabel), Revision = 1,
            Basis = new TestPlanSourceBasis { GuideKey = ProjectOwner(key), GuideReviewState = "Project discovery", Roles = [] },
            Suites = TestScenarioDiscoveryEngine.ProjectSuites(cases.Select(c => c.Generated)), Cases = cases, RuleVersion = E.RuleVersion,
            CreatedBy = actor, CreatedAt = Now, UpdatedBy = actor, UpdatedAt = Now,
        };
        // «Fra mal» (2026-11-26): an empty plan starts with the template's structure — suites only, new ids; no case, approval or remote id is copied.
        if (empty && request.TemplateSuites is { Count: > 0 } templateSuites) plan = plan with { Suites = [.. templateSuites] };
        // AC-level types for the structure (2026-11-26): each acceptance scenario's own reviewed type, read only when a structure needs it.
        var typeDecisions = items.Count > 0 || PlanStructureTemplates.Known(request.Structure) && cases.Count > 0 ? await TypeDecisionsAsync(key, ct) : [];
        if (items.Count > 0)
        {
            // Scenario-only items get the same structure rules as cases (the preview's), keyed by the scenario id.
            var itemStructure = PlanStructure.Build(PlanStructureTemplates.Known(request.Structure) ? request.Structure! : PlanStructureTemplates.Default,
                [.. chosen.Select(c => PlanStructure.FactsOf(c.CandidateId, c, decision: DecisionFor(typeDecisions, c)))]);
            plan = plan with
            {
                Suites = [.. plan.Suites, .. itemStructure.Suites.Where(s => plan.Suites.All(x => x.Id != s.Id))],
                PlannedScenarios = [.. items.Select(p => p with { SuiteId = itemStructure.Assignment.GetValueOrDefault(p.ScenarioId, itemStructure.Suites.FirstOrDefault()?.Id ?? "") })],
            };
        }
        // Hierarchical plan (2026-11-26): the chosen template becomes the saved suite structure — the same rules as the browser's preview.
        if (PlanStructureTemplates.Known(request.Structure) && cases.Count > 0)
        {
            var structure = PlanStructure.Build(request.Structure!, [.. cases.Select((c, i) => StructureFacts(c, i < chosen.Count ? chosen[i] : null, typeDecisions))]);
            plan = plan with { Suites = structure.Suites, Cases = [.. cases.Select(c => structure.Assignment.TryGetValue(c.Source.ScenarioId, out var suite) ? c with { SuiteId = suite } : c)] };
        }
        Bound(plan);
        db.TestPlanDrafts.Add(new TestPlanDraftRecord
        {
            Id = plan.Id, GuideKey = plan.GuideKey, PlanName = plan.PlanName, DeliveryLabel = plan.DeliveryLabel, Revision = 1, ContentJson = JsonSerializer.Serialize(plan, E.Json),
            CreatedBy = actor, CreatedAt = plan.CreatedAt, UpdatedBy = actor, UpdatedAt = plan.UpdatedAt,
        });
        AddRevision(plan, "Created");
        await db.SaveChangesAsync(ct);
        return plan;
    }

    /// <summary>A scenario-only plan item from a discovered scenario (its text as it is now; no case, no steps, no approval).</summary>
    internal PlannedScenario Planned(DiscoveredScenario c, string? snapshotKey, string actor, int order) => new()
    {
        ScenarioId = c.CandidateId, SourceIdentity = c.SourceIdentity.Length > 0 ? c.SourceIdentity : null, Title = c.Title, ExpectedBehaviour = c.ExpectedBehaviour,
        StoryKey = c.Story?.Key, StoryId = c.Story?.StoryId, NumberInStory = c.NumberInStory, SpecificationPath = c.Story?.SpecificationPath,
        SnapshotKey = snapshotKey ?? c.SnapshotKey, Order = order, AddedBy = actor, AddedAt = Now,
    };

    /// <summary>What the plan structure reads about one new case: its scenario's facts (shared with the browser's preview), or a hand-written case.</summary>
    internal static PlanStructureFacts StructureFacts(TestCaseDraft c, DiscoveredScenario? scenario, Dictionary<string, Dictionary<string, ScenarioTypeDecision>>? decisions = null) => scenario is null
        ? new(c.Source.ScenarioId, ScenarioTestLevel.Unclassified, c.Generated.GroupTitle ?? "Skrevet for hånd", null, null, null)
        : PlanStructure.FactsOf(c.Source.ScenarioId, scenario, c.Generated.GroupTitle, decisions is null ? null : DecisionFor(decisions, scenario));

    /// <summary>The project's plan created from this browser draft id, if any (archived and active; never another project's).</summary>
    private async Task<TestPlanDraft?> CreatedFromAsync(string key, string creationRequestId, CancellationToken ct)
    {
        var owner = ProjectOwner(key);
        var rows = await db.TestPlanDrafts.AsNoTracking().Where(r => r.GuideKey == owner && r.ContentJson.Contains(creationRequestId)).ToListAsync(ct);
        return rows.Select(Read).FirstOrDefault(p => p.CreationRequestId == creationRequestId && p.ProjectKey == key);
    }

    /// <summary>Adds a hand-written case to a project plan (title and purpose only; nothing generated).</summary>
    public async Task<TestPlanDraft> AddManualCaseAsync(Guid id, AddManualTestCaseRequest request, string actor, CancellationToken ct)
    {
        var row = await Mutable(id, ct);
        if (row.Revision != request.BaseRevision) throw Stale(row);
        var current = Read(row);
        if (current.ProjectKey is not { } key) throw new TestPlanException(400, "not-a-project-plan", "Hand-written cases are added to project plans.");
        if (string.IsNullOrWhiteSpace(request.Title)) throw new TestPlanException(400, "title-required", "Give the test case a title.");
        var title = request.Title.Trim()[..Math.Min(request.Title.Trim().Length, 300)];

        // Scenario-centric: a case is written under a scenario of this plan, duplicated from a case, or started from a variant the scenario's
        // evidence supports. The scenario must be one the plan holds; nothing is assigned to a scenario by guesswork.
        var copy = request.CopyFrom is { Length: > 0 } from
            ? current.Cases.FirstOrDefault(c => c.Source.ScenarioId == from) ?? throw new TestPlanException(404, "case-not-found", "The case to duplicate is not in this plan.")
            : null;
        var scenarioKey = string.IsNullOrWhiteSpace(request.ScenarioKey) ? copy?.ParentScenario : request.ScenarioKey.Trim();
        var parent = scenarioKey is null ? null : current.Cases.FirstOrDefault(c => c.Source.Kind != ScenarioSourceKind.Manual && c.Source.ScenarioId == scenarioKey);
        if (scenarioKey is not null && parent is null && current.Cases.All(c => c.ParentScenario != scenarioKey))
            throw new TestPlanException(400, "scenario-unknown", "That scenario is not in this plan. Prepare it first, then add test cases under it.");
        var siblings = scenarioKey is null ? [] : current.Cases.Where(c => c.ParentScenario == scenarioKey).ToList();

        var manual = ManualCase(key, title, request.Purpose ?? copy?.EffectivePurpose ?? parent?.EffectivePurpose, request.Type);
        var fields = new List<string>();
        TestCaseEdits? edits = null;
        if (copy is not null)
        {
            edits = new TestCaseEdits
            {
                Title = title, Preconditions = copy.EffectivePreconditions, Steps = [.. copy.EffectiveSteps], StepExpected = [.. copy.EffectiveStepExpected],
                ExpectedResult = copy.EffectiveExpected, ObservationMethod = copy.EffectiveObservation, EnvironmentPrerequisites = copy.EffectiveEnvironment,
                Roles = copy.Edits.Roles, Dependencies = copy.Edits.Dependencies, Priority = copy.Edits.Priority, TestDataNeed = copy.EffectiveTestDataNeed,
                TestType = copy.Edits.TestType, ExecutionMethod = copy.Edits.ExecutionMethod, TestKind = copy.Edits.TestKind,
            };
            fields.Add($"Duplicated from \"{copy.EffectiveTitle}\"");
        }
        else if (request.Variant is { Length: > 0 } variantKey)
        {
            if (parent is null) throw new TestPlanException(400, "scenario-required", "A variant is added under the scenario it comes from.");
            var v = ScenarioPlanning.Variants(parent, siblings).FirstOrDefault(x => x.Key == variantKey)
                ?? throw new TestPlanException(409, "variant-unknown", "That suggestion is no longer supported by the scenario's evidence (or a case with its title exists). Suggest again.");
            var steps = v.Action is not null ? [v.Action] : parent.EffectiveSteps.ToList();
            var results = v.Action is not null ? [v.Expected] : parent.EffectiveStepExpected.Select((e, i) => i == parent.EffectiveSteps.Count - 1 ? v.Expected ?? e : e).ToList();
            edits = new TestCaseEdits
            {
                Title = title,
                Preconditions = string.Join("\n", new[] { parent.EffectivePreconditions, v.Preconditions }.Where(p => !string.IsNullOrWhiteSpace(p))) is { Length: > 0 } pre ? pre : null,
                Steps = steps, StepExpected = results, ExpectedResult = v.Expected, Purpose = v.Basis,
            };
            fields.Add($"Started from a suggested variant ({v.Dimension}): {v.Basis}");
        }
        else if (parent is not null && parent.EffectivePreconditions is { Length: > 0 } startingSituation)
        {
            edits = new TestCaseEdits { Title = title, Preconditions = startingSituation }; // the scenario's starting situation; no step or outcome is invented
            fields.Add("Started from the scenario's starting situation");
        }
        if (request.TestKind is { } chosen && chosen is not (ScenarioTestKind.Manual or ScenarioTestKind.Technical))
            throw new TestPlanException(400, "invalid-test-kind", "A test case is Manual or Technical («Begge» is a scenario decision).");
        if (request.TestKind is { } kind)
        {
            edits = (edits ?? new TestCaseEdits { Title = title }) with { TestKind = kind };
            fields.Add($"Test type: {kind}");
        }
        // Inside a chosen suite (2026-11-26): the suite must be the plan's; its level is only a proposal the tester confirmed or changed.
        var chosenSuite = string.IsNullOrWhiteSpace(request.SuiteId) ? null
            : current.Suites.FirstOrDefault(s => s.Id == request.SuiteId) ?? throw new TestPlanException(400, "suite-unknown", "Testgruppen finnes ikke i testplanen.");
        if (request.TestLevel is { } level) { edits = (edits ?? new TestCaseEdits { Title = title }) with { TestLevel = level }; fields.Add($"Test level: {level}"); }
        if (request.ScenarioType is { } type) { edits = (edits ?? new TestCaseEdits { Title = title }) with { ScenarioType = type }; fields.Add($"Scenario type: {type}"); }
        manual = manual with
        {
            ScenarioKey = scenarioKey,
            ScenarioTitle = scenarioKey is null ? null : parent?.Generated.Title ?? siblings.Select(s => s.ScenarioTitle).FirstOrDefault(t => t is not null),
            DuplicatedFrom = copy?.Source.ScenarioId,
            FromVariant = request.Variant is { Length: > 0 } fromVariant && copy is null ? fromVariant : null,
            SuiteId = chosenSuite?.Id ?? parent?.SuiteId ?? copy?.SuiteId ?? manual.SuiteId,
            Edits = edits is null ? manual.Edits : Clean(edits),
            TestData = copy?.TestData ?? [],
            Risk = copy?.Risk ?? TestCaseRisk.NotAssessed,
            EditHistory = fields.Count == 0 ? [] : [new TestCaseEditEvent(actor, Now, fields)],
        };
        manual = E.Assess(manual);
        List<TestSuiteDraft> suites = current.Suites.Any(s => s.Id == manual.SuiteId) ? current.Suites
            : [.. current.Suites, new TestSuiteDraft(manual.SuiteId, manual.Generated.GroupTitle ?? "Written by hand", "area", null)];
        return await WriteAsync(row, current with { Cases = [.. current.Cases, manual], Suites = suites, Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now }, ct,
            copy is not null ? "Test case duplicated" : "Test case added");
    }

    /// <summary>The variants the evidence of a scenario of this plan supports (proposals; nothing is added).</summary>
    public async Task<List<TestCaseVariantProposal>> VariantsAsync(Guid id, string scenarioKey, CancellationToken ct)
    {
        var plan = Read(await Row(id, ct));
        var parent = plan.Cases.FirstOrDefault(c => c.Source.Kind != ScenarioSourceKind.Manual && c.Source.ScenarioId == scenarioKey)
            ?? throw new TestPlanException(404, "scenario-not-found", "That scenario's own case is not in this plan, so its evidence cannot be read.");
        return ScenarioPlanning.Variants(parent, plan.Cases.Where(c => c.ParentScenario == scenarioKey));
    }

    /// <summary>
    /// Translates one scenario (only that scenario) into the requested language (default: the first configured target, Norwegian bokmål): every
    /// case's text a tester reads, the scenario's Given/When/Then, its user story and the wording of the requirements linked to it. Saved at once
    /// as a new plan revision. Technical identifiers are protected by placeholders and must come back unchanged, else that text keeps its previous
    /// version and is reported as failed. Text a person wrote or edited in that language is kept unless the tester asks to overwrite it. The
    /// original text and every review decision stay as they are. When no service can translate at all, one clear message says what was tried.
    /// </summary>
    public async Task<TranslateScenarioResult> TranslateScenarioAsync(Guid id, TranslateScenarioRequest request, ITestCaseTranslator translator, TranslationSettings settings,
        string actor, CancellationToken ct)
    {
        var row = await Mutable(id, ct);
        if (row.Revision != request.BaseRevision) throw Stale(row);
        var current = Read(row);
        if (string.IsNullOrWhiteSpace(request.ScenarioKey) || current.Cases.All(c => c.ParentScenario != request.ScenarioKey || c.Review == TestCaseReviewState.Superseded))
            throw new TestPlanException(404, "scenario-not-found", "That scenario has no test case in this plan.");
        return await TranslateAsync(row, current, [request.ScenarioKey], request.Target, request.Overwrite, translator, settings, actor, ct, onlyMissing: false);
    }

    /// <summary>Translate all: every scenario of the plan (with its user story and linked requirement wording), saved as one revision.</summary>
    public async Task<TranslateScenarioResult> TranslatePlanAsync(Guid id, TranslatePlanRequest request, ITestCaseTranslator translator, TranslationSettings settings,
        string actor, CancellationToken ct)
    {
        var row = await Mutable(id, ct);
        if (row.Revision != request.BaseRevision) throw Stale(row);
        var current = Read(row);
        if (current.ProjectKey is null) throw new TestPlanException(400, "not-a-project-plan", "Translation is available for project plans.");
        var scenarios = current.Cases.Where(c => c.Review != TestCaseReviewState.Superseded).Select(c => c.ParentScenario ?? "").Distinct(StringComparer.Ordinal).ToList();
        return await TranslateAsync(row, current, scenarios, request.Target, request.Overwrite && !request.OnlyMissing, translator, settings, actor, ct, request.OnlyMissing);
    }

    private async Task<TranslateScenarioResult> TranslateAsync(TestPlanDraftRecord row, TestPlanDraft current, List<string> scenarioKeys, string? requestedTarget, bool overwrite,
        ITestCaseTranslator translator, TranslationSettings settings, string actor, CancellationToken ct, bool onlyMissing)
    {
        var target = string.IsNullOrWhiteSpace(requestedTarget) ? settings.DefaultTarget : requestedTarget.Trim();
        if (!TranslationLanguages.IsCode(target) || string.Equals(target, settings.SourceLanguage, StringComparison.OrdinalIgnoreCase))
            throw new TestPlanException(400, "translation-language", "Choose a language to translate into that differs from the original's.");
        var name = TranslationLanguages.Name(target);
        var keys = scenarioKeys.ToHashSet(StringComparer.Ordinal);
        var cases = current.Cases.Where(c => keys.Contains(c.ParentScenario ?? "") && c.Review != TestCaseReviewState.Superseded).ToList();
        if (onlyMissing)
        {
            var storiesHeld = (current.StoryTranslations ?? []).Where(t => t.Language == target).Select(t => t.StoryKey).ToHashSet(StringComparer.Ordinal);
            var missing = cases.Count(c => c.Translation(target) is null)
                + cases.Select(c => c.Generated.Story).OfType<BirkNext.TestCoverage.CoverageUserStory>().DistinctBy(st => st.Key).Count(st => !storiesHeld.Contains(st.Key));
            if (missing == 0) return new TranslateScenarioResult(null, [], [], [], "Nothing is missing a translation.", target, null);
        }
        if (!translator.Availability.Available) throw NoTranslationService(translator.Availability.Reason, name);

        var translated = new List<string>(); var kept = new List<string>(); var failed = new List<string>();
        var updated = new Dictionary<string, TestCaseDraft>(StringComparer.Ordinal);
        string? provider = null;
        string? serviceDown = null;

        // One request per text group; identifiers are replaced by placeholders and must all come back.
        async Task<(List<string?>? Texts, string? Problem)> Translate(IReadOnlyList<string?> fields)
        {
            if (serviceDown is not null) return (null, serviceDown);
            var masked = new List<string?>(); var tokens = new List<List<string>>();
            foreach (var f in fields)
            {
                if (f is null) { masked.Add(null); tokens.Add([]); continue; }
                var (m, t) = ScenarioPlanning.Protect(f);
                masked.Add(m); tokens.Add(t);
            }
            TranslatedTexts answer;
            try { answer = await translator.TranslateAsync(masked, settings.SourceLanguage, target, ct); }
            catch (TestCaseTranslationException e)
            {
                // The service itself is unavailable: nothing more is sent; with nothing translated yet, one clear message instead of a list.
                if (translated.Count == 0) throw NoTranslationService(e.Message, name);
                serviceDown = e.Message;
                return (null, e.Message);
            }
            if (answer.Texts.Count != fields.Count) return (null, "the service returned a different number of texts.");
            var restored = new List<string?>();
            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i] is null) { restored.Add(null); continue; }
                if (answer.Texts[i] is not { } text || ScenarioPlanning.Restore(text, tokens[i]) is not { } back)
                    return (null, "a technical identifier was changed or lost in the translation, so it was not used.");
                if (!ScenarioPlanning.PreservesNumericValues(fields[i]!, back))
                    return (null, "a numeric value (such as an HTTP status, date, timeout or limit) changed in translation, so it was not used.");
                if (!ScenarioPlanning.PreservesNegationAndConditions(fields[i]!, back, target))
                    return (null, "a negation or conditional behaviour marker was lost in translation, so it was not used.");
                restored.Add(back);
            }
            provider = answer.Provider;
            return (restored, null);
        }

        foreach (var c in cases)
        {
            if (onlyMissing && c.Translation(target) is not null) continue; // existing text (also from an older original) stays as it is
            if (c.Translation(target) is { Origin: TranslationOrigin.Manual or TranslationOrigin.MachineEdited } && !overwrite) { kept.Add(c.EffectiveTitle); continue; }
            var (texts, problem) = await Translate(ScenarioPlanning.TranslatableFields(c));
            var translation = texts is null ? null : ScenarioPlanning.FromFields(c, texts, TranslationOrigin.Machine, provider, actor, Now, target);
            if (translation is null) { failed.Add($"{c.EffectiveTitle}: {problem ?? "the translation did not match the case's fields."}"); continue; }
            updated[c.Source.ScenarioId] = c.WithTranslation(target, translation) with
            {
                EditHistory = [.. c.EditHistory.TakeLast(99), new TestCaseEditEvent(actor, Now, [$"{name} text (machine translation, {provider})"])],
            };
            translated.Add(c.EffectiveTitle);
        }

        // The user stories of these scenarios (once each), unless a person wrote that story's text.
        var storyTranslations = (current.StoryTranslations ?? []).ToList();
        foreach (var story in cases.Select(c => c.Generated.Story).OfType<BirkNext.TestCoverage.CoverageUserStory>().DistinctBy(s => s.Key))
        {
            var held = storyTranslations.FirstOrDefault(t => t.StoryKey == story.Key && t.Language == target);
            if (onlyMissing && held is not null) continue;
            if (held is { Origin: TranslationOrigin.Manual or TranslationOrigin.MachineEdited } && !overwrite) continue;
            if (held is not null && held.SourceFingerprint == StoryFingerprint(story) && !overwrite) continue;
            var (texts, problem) = await Translate([story.Title, story.Description, story.WhyThisPriority, story.IndependentTest]);
            if (texts is null) { failed.Add($"User story {story.StoryId ?? story.Title}: {problem}"); continue; }
            storyTranslations.RemoveAll(t => t.StoryKey == story.Key && t.Language == target);
            storyTranslations.Add(new StoryTranslation
            {
                StoryKey = story.Key, Language = target, Title = texts[0], Description = texts[1], WhyThisPriority = texts[2], IndependentTest = texts[3],
                SourceFingerprint = StoryFingerprint(story), Origin = TranslationOrigin.Machine, Provider = provider, By = actor, At = Now,
            });
        }

        // The wording of the requirements linked to these cases (display only), when it is known and not yet translated as it now reads.
        var requirementTranslations = (current.RequirementTranslations ?? []).ToList();
        var serverCatalog = specificationCatalogs is null || current.ProjectKey is null ? null : await specificationCatalogs.CurrentAsync(current.ProjectKey, ct);
        var wording = new Dictionary<(string, string), (string Id, string Path, string Text)>();
        foreach (var c in cases)
        {
            foreach (var l in c.RequirementLinks.Where(l => !string.IsNullOrWhiteSpace(l.Title)))
                wording.TryAdd(LinkKey(l.RequirementId, l.SpecificationPath), (l.RequirementId, l.SpecificationPath, l.Title));
            foreach (var r in c.Generated.Requirements)
                if (serverCatalog?.Requirements.FirstOrDefault(e => string.Equals(e.RequirementId, r.RuleId, StringComparison.OrdinalIgnoreCase)) is { } entry)
                    wording.TryAdd(LinkKey(entry.RequirementId, entry.Path), (entry.RequirementId, entry.Path, entry.Title));
        }
        foreach (var l in (current.StoryRequirementLinks ?? []).Where(l => cases.Any(c => c.Generated.Story?.Key == l.StoryKey) && !string.IsNullOrWhiteSpace(l.Link.Title)))
            wording.TryAdd(LinkKey(l.Link.RequirementId, l.Link.SpecificationPath), (l.Link.RequirementId, l.Link.SpecificationPath, l.Link.Title));
        var pending = wording.Values.Where(w => !requirementTranslations.Any(t => t.Language == target && LinkKey(t.RequirementId, t.SpecificationPath) == LinkKey(w.Id, w.Path)
            && (onlyMissing || t.SourceSha256 == ScenarioPlanning.Sha(w.Text)))).Take(200).ToList();
        if (pending.Count > 0)
        {
            var (texts, problem) = await Translate([.. pending.Select(p => (string?)p.Text)]);
            if (texts is null) failed.Add($"Requirement wording: {problem}");
            else
                for (var i = 0; i < pending.Count; i++)
                {
                    requirementTranslations.RemoveAll(t => t.Language == target && LinkKey(t.RequirementId, t.SpecificationPath) == LinkKey(pending[i].Id, pending[i].Path));
                    requirementTranslations.Add(new RequirementTextTranslation(pending[i].Id, pending[i].Path, target, texts[i]!, ScenarioPlanning.Sha(pending[i].Text), provider, Now));
                }
        }

        TestPlanDraft? plan = null;
        var storiesChanged = !storyTranslations.SequenceEqual(current.StoryTranslations ?? []);
        var requirementsChanged = !requirementTranslations.SequenceEqual(current.RequirementTranslations ?? []);
        if (updated.Count > 0 || storiesChanged || requirementsChanged)
            plan = await WriteAsync(row, current with
            {
                Cases = [.. current.Cases.Select(c => updated.GetValueOrDefault(c.Source.ScenarioId) ?? c)], Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now,
                StoryTranslations = storyTranslations.Count == 0 ? null : storyTranslations,
                RequirementTranslations = requirementTranslations.Count == 0 ? null : requirementTranslations,
            }, ct, $"Translated to {name}");
        var message = updated.Count == 0 && plan is null ? "Nothing was translated."
            : $"Translated {translated.Count} test case{(translated.Count == 1 ? "" : "s")}{(scenarioKeys.Count > 1 ? $" in {cases.Select(c => c.ParentScenario).Distinct().Count()} scenarios" : "")} to {name}{(provider is null ? "" : $" ({provider})")}. Saved; translating approves nothing.";
        if (kept.Count > 0) message += $" Kept {kept.Count} {name} text{(kept.Count == 1 ? "" : "s")} written by a person.";
        if (failed.Count > 0) message += $" {failed.Count} could not be translated.";
        return new TranslateScenarioResult(plan, translated, kept, failed, message, target, provider);
    }

    public static string StoryFingerprint(BirkNext.TestCoverage.CoverageUserStory s) => ScenarioPlanning.StoryFingerprint(s);

    /// <summary>No translation choice could translate: what was tried and why, where to set it up, and that the text can be written by hand.</summary>
    private static TestPlanException NoTranslationService(string detail, string language)
    {
        var why = detail.Split(" You can write", 2)[0].Split(" Nothing was changed", 2)[0].Trim();
        return new(503, "translation-unavailable",
            $"No translation service is available right now. {why} Set up Azure AI Translator or start the local LibreTranslate server in System Settings → Translation. Nothing was changed; you can write the {language} text by hand meanwhile.");
    }

    /// <summary>Scenarios of one user story are prepared in the specification's order (their number within the story), whatever order discovery
    /// listed them in; each story keeps the slots its scenarios had, so nothing else moves. The tester's later reordering is the plan's order.</summary>
    internal static List<DiscoveredScenario> SpecificationOrder(List<DiscoveredScenario> chosen)
    {
        var result = chosen.ToList();
        foreach (var story in chosen.Select((c, i) => (c, i)).Where(x => x.c.Story is not null && x.c.NumberInStory is not null).GroupBy(x => x.c.Story!.Key, StringComparer.Ordinal))
        {
            var slots = story.Select(x => x.i).ToList();
            var ordered = story.Select(x => x.c).OrderBy(c => c.NumberInStory).ToList();
            for (var k = 0; k < slots.Count; k++) result[slots[k]] = ordered[k];
        }
        return result;
    }

    private static TestCaseDraft ProjectCase(string projectKey, DiscoveredScenario c, DiscoverySourceBasis? basis = null)
    {
        var content = TestScenarioDiscoveryEngine.Content(c) with { BasisFingerprint = basis?.Fingerprint };
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
        await RestoreCurrentAsync(current, ct);
        var previous = current.Cases.ToDictionary(c => c.Source.ScenarioId, StringComparer.Ordinal);
        var ids = (request.ScenarioIds ?? []).Distinct(StringComparer.Ordinal).Where(previous.ContainsKey).ToList();
        // An empty plan (from a template or «Helt tom testplan») saves its suites with no case (2026-11-26); a plan with cases keeps at least one.
        if (ids.Count == 0 && current.Cases.Count > 0) throw new TestPlanException(400, "no-scenarios", "A plan keeps at least one test case.");
        var human = request.Cases.GroupBy(c => c.ScenarioId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var suites = request.Suites.Where(s => !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.Name)).GroupBy(s => s.Id, StringComparer.Ordinal).Select(g => g.Last())
            .Take(200).Select(s => s with
            {
                Name = s.Name.Trim()[..Math.Min(s.Name.Trim().Length, 200)], Origin = s.Origin is "area" or "stage" or "structure" ? s.Origin : "reviewer",
                ParentId = string.IsNullOrWhiteSpace(s.ParentId) ? null : s.ParentId,
            }).ToList();
        if (suites.Count == 0 && (current.Cases.Count > 0 || current.PlannedScenarios is { Count: > 0 })) suites = current.Suites;
        // The suite hierarchy is saved business data (2026-11-26): an unknown parent, a cycle or a duplicate id is refused, never repaired silently.
        if (PlanStructure.Validate(suites) is { } invalid) throw new TestPlanException(400, "invalid-suites", invalid);
        var assets = await AssetsAsync(ct);
        var executables = Executables().GroupBy(e => e.Reference.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var catalog = RequirementCatalog(current.ProjectKey!, request.RequirementCatalog);
        // Server-authoritative when the project has a server-held catalog (captured at Project Import); the browser's list is then not trusted.
        var serverCatalog = specificationCatalogs is null ? null : await specificationCatalogs.CurrentAsync(current.ProjectKey!, ct);
        var cases = new List<TestCaseDraft>();
        foreach (var id in ids)
        {
            var before = previous[id];
            human.TryGetValue(id, out var part);
            var edits = part is null ? before.Edits : Clean(part.Edits);
            var requirementLinks = part?.RequirementLinks is { } wanted ? RequirementLinks(before, wanted, catalog, serverCatalog, actor) : before.RequirementLinks;
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
            var saved = before with
            {
                Edits = edits, EditHistory = History(before, part is null ? null : edits, actor, !SameLinks(before.RequirementLinks, requirementLinks)), TestData = links,
                Correlations = correlations, Risk = part?.Risk ?? before.Risk, SuiteId = suite, RequirementLinks = requirementLinks,
                RejectedSuggestions = part?.RejectedSuggestions is { } rejected ? Rejections(before, rejected, actor) : before.RejectedSuggestions,
            };
            foreach (var (language, wantedText) in (part?.Translations ?? []).Where(t => TranslationLanguages.IsCode(t.Key)).Take(TranslationSettingsStore.MaxTargets))
            {
                var held = before.Translation(language);
                var next = Translation(held, wantedText, saved, actor);
                if (ReferenceEquals(next, held)) continue;
                saved = saved.WithTranslation(language, next);
                saved = saved with { EditHistory = [.. saved.EditHistory.TakeLast(99), new TestCaseEditEvent(actor, Now, [$"{TranslationLanguages.Name(language)} text"])] };
            }
            cases.Add(E.Assess(saved));
        }
        var plan = current with
        {
            PlanName = string.IsNullOrWhiteSpace(request.PlanName) ? current.PlanName : Name(request.PlanName), DeliveryLabel = Label(request.DeliveryLabel),
            Revision = row.Revision + 1, Suites = suites, Cases = cases, UpdatedBy = actor, UpdatedAt = Now,
            // A scenario-only item whose suite was removed moves to the first suite (never dropped; 2026-11-26).
            PlannedScenarios = current.PlannedScenarios is { Count: > 0 } items ? [.. items.Select(p => suites.Count == 0 || suites.Any(x => x.Id == p.SuiteId) ? p : p with { SuiteId = suites[0].Id })] : current.PlannedScenarios,
            StoryRequirementLinks = request.StoryRequirementLinks is { } storyLinks ? StoryLinks(current, storyLinks, catalog, serverCatalog, actor) : current.StoryRequirementLinks,
            StoryRejectedSuggestions = request.StoryRejectedSuggestions is { } storyRejections ? StoryRejections(current, storyRejections, actor) : current.StoryRejectedSuggestions,
            StoryTranslations = request.StoryTranslations is { } storyTexts ? StoryTexts(current, storyTexts, actor) : current.StoryTranslations,
            DesignDecisions = request.DesignDecisions is { } decisions ? DesignDecisions(current, decisions, actor) : current.DesignDecisions,
        };
        return await WriteAsync(row, FlagProject(plan), ct);
    }

    public const int MaxStories = 500;

    /// <summary>User story translations after a save: unchanged text keeps what was recorded; changed text is a person's (Manual / MachineEdited).</summary>
    private List<StoryTranslation>? StoryTexts(TestPlanDraft current, List<StoryTranslation> wanted, string actor)
    {
        static string? T(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim()[..Math.Min(v.Trim().Length, MaxTranslatedField)];
        var stories = current.Cases.Select(c => c.Generated.Story).OfType<BirkNext.TestCoverage.CoverageUserStory>().GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.First());
        var result = new List<StoryTranslation>();
        foreach (var w in wanted.Where(w => stories.ContainsKey(w.StoryKey) && TranslationLanguages.IsCode(w.Language)).DistinctBy(w => (w.StoryKey, w.Language)).Take(MaxStories))
        {
            var held = (current.StoryTranslations ?? []).FirstOrDefault(t => t.StoryKey == w.StoryKey && t.Language == w.Language);
            var clean = new StoryTranslation { StoryKey = w.StoryKey, Language = w.Language, Title = T(w.Title), Description = T(w.Description), WhyThisPriority = T(w.WhyThisPriority), IndependentTest = T(w.IndependentTest) };
            if (clean.Title is null && clean.Description is null && clean.WhyThisPriority is null && clean.IndependentTest is null) continue;
            if (held is not null && held.Title == clean.Title && held.Description == clean.Description && held.WhyThisPriority == clean.WhyThisPriority && held.IndependentTest == clean.IndependentTest)
            { result.Add(held); continue; }
            result.Add(clean with
            {
                SourceFingerprint = StoryFingerprint(stories[w.StoryKey]), By = actor, At = Now, Provider = held?.Provider,
                Origin = held?.Origin is TranslationOrigin.Machine or TranslationOrigin.MachineEdited ? TranslationOrigin.MachineEdited : TranslationOrigin.Manual,
            });
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Story-level requirement links after a save, per user story: each checked exactly like a case link (kept as recorded, new ones only from the
    /// project's catalog, who and when recorded). An association with the story — never inherited by its scenarios or cases, never counted.
    /// </summary>
    private List<StoryRequirementLink>? StoryLinks(TestPlanDraft current, List<StoryRequirementLink> wanted,
        Dictionary<(string, string), ProjectRequirementOption>? catalog, BirkNext.Specifications.SpecificationCatalogView? serverCatalog, string actor)
    {
        var result = new List<StoryRequirementLink>();
        foreach (var story in wanted.Where(w => !string.IsNullOrWhiteSpace(w.StoryKey)).GroupBy(w => w.StoryKey.Trim(), StringComparer.Ordinal).Take(MaxStories))
        {
            var held = (current.StoryRequirementLinks ?? []).Where(l => l.StoryKey == story.Key).Select(l => l.Link);
            result.AddRange(RequirementLinks(held, [.. story.Select(w => w.Link)], catalog, serverCatalog, actor).Select(l => new StoryRequirementLink(story.Key, l with { Scope = null })));
        }
        return result.Count == 0 ? null : result;
    }

    public const int MaxDesignDecisions = 2000;

    /// <summary>Test-design decisions after a save: an unchanged decision keeps who and when; a new or changed one is recorded for this actor.</summary>
    private List<TestDesignDecision>? DesignDecisions(TestPlanDraft current, List<TestDesignDecision> wanted, string actor)
    {
        var held = (current.DesignDecisions ?? []).ToDictionary(d => (d.ScenarioKey, d.ProposalKey), d => d);
        var result = wanted.Where(w => !string.IsNullOrWhiteSpace(w.ScenarioKey) && !string.IsNullOrWhiteSpace(w.ProposalKey)
                && w.Decision is TestDesignDecisions.Rejected or TestDesignDecisions.Deferred)
            .DistinctBy(w => (w.ScenarioKey, w.ProposalKey)).Take(MaxDesignDecisions)
            .Select(w => held.TryGetValue((w.ScenarioKey, w.ProposalKey), out var kept) && kept.Decision == w.Decision && kept.Reason == w.Reason ? kept
                : new TestDesignDecision
                {
                    ScenarioKey = w.ScenarioKey.Trim(), ProposalKey = w.ProposalKey.Trim(), ProposalTitle = (w.ProposalTitle ?? "").Trim(), Decision = w.Decision,
                    Reason = string.IsNullOrWhiteSpace(w.Reason) ? null : w.Reason.Trim(), By = actor, At = Now,
                })
            .ToList();
        return result.Count == 0 ? null : result;
    }

    private List<StoryRequirementRejection>? StoryRejections(TestPlanDraft current, List<StoryRequirementRejection> wanted, string actor)
    {
        var held = (current.StoryRejectedSuggestions ?? []).ToDictionary(r => (r.StoryKey, ScenarioPlanning.Key(r.Rejection.RequirementId, r.Rejection.SpecificationPath)), r => r);
        var result = wanted.Where(w => !string.IsNullOrWhiteSpace(w.StoryKey) && !string.IsNullOrWhiteSpace(w.Rejection.RequirementId))
            .DistinctBy(w => (w.StoryKey, ScenarioPlanning.Key(w.Rejection.RequirementId, w.Rejection.SpecificationPath))).Take(MaxRejectedSuggestions * 4)
            .Select(w => held.TryGetValue((w.StoryKey, ScenarioPlanning.Key(w.Rejection.RequirementId, w.Rejection.SpecificationPath)), out var kept) ? kept
                : new StoryRequirementRejection(w.StoryKey.Trim(), new RejectedRequirementSuggestion
                {
                    RequirementId = w.Rejection.RequirementId.Trim(), SpecificationPath = (w.Rejection.SpecificationPath ?? "").Trim(), RejectedBy = actor, RejectedAt = Now,
                }))
            .ToList();
        return result.Count == 0 ? null : result;
    }

    /// <summary>Source-change flags: for a project plan from the project's latest discovery in this process (none known → nothing flagged), for a
    /// guide plan from the guide revision readers see now.</summary>
    private async Task<TestPlanDraft> FlagsAsync(TestPlanDraft plan, CancellationToken ct)
    {
        if (plan.ProjectKey is null) return WithSourceFlags(plan, await CurrentFingerprintsAsync(plan.GuideKey, ct));
        await RestoreCurrentAsync(plan, ct);
        return FlagProject(plan);
    }

    private static string? StoryKey(BirkNext.TestCoverage.CoverageUserStory? story) => story?.Key;

    /// <summary>
    /// Specification scenarios have positional ids (scenario-N): when a scenario is inserted or removed earlier in the file, the same text gets
    /// another id. A plan case follows its text to the new id only when that is unambiguous: exactly one specification candidate of the latest
    /// discovery has the case's fingerprint, its id is not held by a case that stays, and no two cases want it. Otherwise nothing moves.
    /// </summary>
    internal static Dictionary<string, DiscoveredScenario> Renumbered(TestPlanDraft plan, IEnumerable<DiscoveredScenario> latest)
    {
        var all = latest.ToList();
        var byId = all.ToDictionary(c => c.CandidateId, StringComparer.Ordinal);
        var byText = all.Where(c => c.CapabilityId == TestScenarioDiscoveryEngine.Specification && c.Fingerprint.Length > 0)
            .GroupBy(c => c.Fingerprint, StringComparer.Ordinal).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var proposals = new Dictionary<string, DiscoveredScenario>(StringComparer.Ordinal);
        foreach (var c in plan.Cases.Where(c => c.Source.Kind == ScenarioSourceKind.ProjectDiscovery && c.Review != TestCaseReviewState.Superseded))
        {
            var same = byId.TryGetValue(c.Source.ScenarioId, out var now) && now.Fingerprint == c.Source.Fingerprint;
            if (!same && c.Source.Fingerprint is { Length: > 0 } fp && byText.TryGetValue(fp, out var target) && target.CandidateId != c.Source.ScenarioId)
                proposals[c.Source.ScenarioId] = target;
        }
        var staying = plan.Cases.Where(c => c.Source.Kind != ScenarioSourceKind.Manual && !proposals.ContainsKey(c.Source.ScenarioId)).Select(c => c.Source.ScenarioId).ToHashSet(StringComparer.Ordinal);
        var contested = proposals.Values.GroupBy(t => t.CandidateId, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return proposals.Where(p => !staying.Contains(p.Value.CandidateId) && !contested.Contains(p.Value.CandidateId)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    /// <summary>"Moved in the specification: the same scenario text is now spec.md#scenario-5 (was spec.md#scenario-4)" for the case's history.</summary>
    private static string Renumbering(TestCaseDraft c, DiscoveredScenario moved) =>
        $"Moved in the specification: the same scenario text is now {moved.Evidence.FirstOrDefault(e => e.Kind == "specification")?.Reference ?? moved.CandidateId}"
        + (c.Generated.Evidence.FirstOrDefault(e => e.Kind == "specification")?.Reference is { } was ? $" (was {was})" : "")
        + (StoryKey(c.Generated.Story) != StoryKey(moved.Story) && moved.Story is not null ? $"; user story {moved.Story.StoryId ?? moved.Story.Key}" : "");

    /// <summary>"User story: US6 → US7" (or "identified: US6", "no longer under a user story (was US6)") for the case's history.</summary>
    private static string StoryMove(BirkNext.TestCoverage.CoverageUserStory? before, BirkNext.TestCoverage.CoverageUserStory? after) =>
        before is null ? $"User story identified: {after!.StoryId ?? after.Key} ({after.Title})"
        : after is null ? $"No longer under a user story in the latest source (was {before.StoryId ?? before.Key})"
        : $"User story: {before.StoryId ?? before.Key} → {after.StoryId ?? after.Key} ({after.Title})";

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

    /// <summary>SHA-256 over the catalog's (requirement id, specification file) keys, sorted — independent of the order the browser sent them in.</summary>
    public static string CatalogFingerprint(IReadOnlyDictionary<(string, string), ProjectRequirementOption> catalog) =>
        E.Sha(string.Join("\n", catalog.Keys.Select(k => $"{k.Item1}\u001f{k.Item2}").Order(StringComparer.Ordinal)));

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
    private List<ManualRequirementLink> RequirementLinks(TestCaseDraft before, List<ManualRequirementLink> wanted, Dictionary<(string, string), ProjectRequirementOption>? catalog,
        BirkNext.Specifications.SpecificationCatalogView? serverCatalog, string actor) => RequirementLinks(before.RequirementLinks, wanted, catalog, serverCatalog, actor);

    /// <summary>The same check for any held links (a case's, or one user story's): kept as recorded, new ones only from the project's catalog.</summary>
    private List<ManualRequirementLink> RequirementLinks(IEnumerable<ManualRequirementLink> heldLinks, List<ManualRequirementLink> wanted, Dictionary<(string, string), ProjectRequirementOption>? catalog,
        BirkNext.Specifications.SpecificationCatalogView? serverCatalog, string actor)
    {
        var held = heldLinks.GroupBy(l => LinkKey(l.RequirementId, l.SpecificationPath)).ToDictionary(g => g.Key, g => g.First());
        var result = new List<ManualRequirementLink>();
        foreach (var w in wanted.Where(w => !string.IsNullOrWhiteSpace(w.RequirementId)).DistinctBy(w => LinkKey(w.RequirementId, w.SpecificationPath)))
        {
            var key = LinkKey(w.RequirementId, w.SpecificationPath);
            if (held.TryGetValue(key, out var kept)) { result.Add(kept); continue; }
            if (serverCatalog is not null)
            {
                var resolved = BirkNext.Api.Services.Specifications.SpecificationCatalogService.Resolve(serverCatalog, w.RequirementId, w.SpecificationPath);
                if (resolved.Entry is not { } entry) throw new TestPlanException(400, "requirement-unknown", resolved.Problem!);
                result.Add(Provenance(new ManualRequirementLink
                {
                    RequirementId = entry.RequirementId, SpecificationPath = entry.Path, Title = entry.Title, LinkedBy = actor, LinkedAt = Now,
                    CheckedAgainst = ManualRequirementLinkBasis.ServerSpecification, CatalogFingerprint = serverCatalog.Fingerprint,
                    CatalogRevision = serverCatalog.Revision, RequirementTextSha256 = entry.TextSha256,
                }, w));
                continue;
            }
            if (catalog is null)
                throw new TestPlanException(400, "requirements-unavailable", "The project's requirements could not be read, so no new requirement link can be saved. Open the project's specification and try again.");
            if (!catalog.TryGetValue(key, out var requirement))
                throw new TestPlanException(400, "requirement-unknown", $"{w.RequirementId.Trim()} is not a requirement of this project's specification. Choose a requirement from the list.");
            result.Add(Provenance(new ManualRequirementLink
            {
                RequirementId = requirement.RequirementId.Trim(), SpecificationPath = requirement.SpecificationPath.Trim(),
                Title = requirement.Title.Trim()[..Math.Min(requirement.Title.Trim().Length, 500)], LinkedBy = actor, LinkedAt = Now,
                // Audit 2026-11-15: the catalog comes from the browser's reading of the specification; say so, and keep what it was.
                CheckedAgainst = ManualRequirementLinkBasis.BrowserSpecification, CatalogFingerprint = CatalogFingerprint(catalog),
            }, w));
        }
        if (result.Count > MaxRequirementLinks)
            throw new TestPlanException(400, "too-many-requirement-links", $"A test case links at most {MaxRequirementLinks} requirements.");
        return result;
    }

    /// <summary>What the tester chose with the link: scenario-level or case-level, and the suggestion (level and reason) they confirmed, if any.</summary>
    private static ManualRequirementLink Provenance(ManualRequirementLink link, ManualRequirementLink wanted) => link with
    {
        Scope = wanted.Scope == RequirementLinkScope.Scenario ? RequirementLinkScope.Scenario : null,
        SuggestionLevel = wanted.SuggestionLevel,
        SuggestionReason = string.IsNullOrWhiteSpace(wanted.SuggestionReason) ? null : wanted.SuggestionReason.Trim()[..Math.Min(wanted.SuggestionReason.Trim().Length, 500)],
    };

    public const int MaxRejectedSuggestions = 200;

    /// <summary>Rejected suggestions after a save: one already held keeps who and when; a new one is recorded for the saving tester.</summary>
    private List<RejectedRequirementSuggestion>? Rejections(TestCaseDraft before, List<RejectedRequirementSuggestion> wanted, string actor)
    {
        var held = (before.RejectedSuggestions ?? []).GroupBy(r => ScenarioPlanning.Key(r.RequirementId, r.SpecificationPath), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var result = wanted.Where(w => !string.IsNullOrWhiteSpace(w.RequirementId)).DistinctBy(w => ScenarioPlanning.Key(w.RequirementId, w.SpecificationPath))
            .Take(MaxRejectedSuggestions)
            .Select(w => held.TryGetValue(ScenarioPlanning.Key(w.RequirementId, w.SpecificationPath), out var kept) ? kept
                : new RejectedRequirementSuggestion { RequirementId = w.RequirementId.Trim(), SpecificationPath = (w.SpecificationPath ?? "").Trim(), RejectedBy = actor, RejectedAt = Now })
            .ToList();
        return result.Count == 0 ? null : result;
    }

    public const int MaxTranslatedField = 4000;

    /// <summary>
    /// The translated text (one language) after a save. Unchanged text keeps what was recorded (who, when, service, the original it was made from). Changed text is
    /// a person's: Manual, or MachineEdited when it started as a machine translation, bound to the case's original as saved now. Empty = removed.
    /// </summary>
    private TestCaseTranslation? Translation(TestCaseTranslation? before, TestCaseTranslation wanted, TestCaseDraft saved, string actor)
    {
        static string? T(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim()[..Math.Min(v.Trim().Length, MaxTranslatedField)];
        var steps = wanted.Steps.Take(saved.EffectiveSteps.Count).Select(s => T(s) ?? "").ToList();
        var results = wanted.StepExpected.Take(steps.Count).Select(T).ToList();
        while (results.Count < steps.Count) results.Add(null);
        var clean = new TestCaseTranslation
        {
            Title = T(wanted.Title), Purpose = T(wanted.Purpose), Preconditions = T(wanted.Preconditions), Steps = steps, StepExpected = results, ExpectedResult = T(wanted.ExpectedResult),
            Given = T(wanted.Given), When = T(wanted.When), Then = T(wanted.Then), ObservationMethod = T(wanted.ObservationMethod), Environment = T(wanted.Environment),
            Roles = T(wanted.Roles), Dependencies = T(wanted.Dependencies),
        };
        if (new[] { clean.Title, clean.Purpose, clean.Preconditions, clean.ExpectedResult, clean.Given, clean.When, clean.Then, clean.ObservationMethod, clean.Environment, clean.Roles, clean.Dependencies }
            .All(v => v is null) && steps.All(s => s.Length == 0)) return null;
        if (before is not null && SameText(before, clean)) return before;
        return clean with
        {
            SourceFingerprint = ScenarioPlanning.SourceFingerprint(saved), By = actor, At = Now, Provider = before?.Provider,
            Origin = before?.Origin is TranslationOrigin.Machine or TranslationOrigin.MachineEdited ? TranslationOrigin.MachineEdited : TranslationOrigin.Manual,
        };
    }

    private static bool SameText(TestCaseTranslation a, TestCaseTranslation b) =>
        a.Title == b.Title && a.Purpose == b.Purpose && a.Preconditions == b.Preconditions && a.ExpectedResult == b.ExpectedResult
        && a.Given == b.Given && a.When == b.When && a.Then == b.Then && a.ObservationMethod == b.ObservationMethod && a.Environment == b.Environment
        && a.Roles == b.Roles && a.Dependencies == b.Dependencies
        && a.Steps.SequenceEqual(b.Steps) && a.StepExpected.Select(x => x ?? "").SequenceEqual(b.StepExpected.Select(x => x ?? ""));

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
        C("Environment", a.EnvironmentPrerequisites, b.EnvironmentPrerequisites); C("Test type", a.TestType, b.TestType); C("Execution method", a.ExecutionMethod, b.ExecutionMethod); C("Manual or technical", a.TestKind, b.TestKind);
        C("Performance", a.Performance, b.Performance); C("Roles", a.Roles, b.Roles); C("Dependencies", a.Dependencies, b.Dependencies); C("Priority", a.Priority, b.Priority);
        C("Test Data need", a.TestDataNeed, b.TestDataNeed); C("Blocked by", a.BlockedBy, b.BlockedBy); C("Template values", a.TemplateValues, b.TemplateValues);
        return fields;
    }

    // ── Persistence helpers ──────────────────────────────────────────────────────────────────────────────────

    // ── Version history (append-only) ────────────────────────────────────────────────────────────────────────────────

    private void AddRevision(TestPlanDraft plan, string reason, string? json = null) =>
        db.TestPlanDraftRevisions.Add(new TestPlanDraftRevisionRecord
        {
            Id = Guid.NewGuid(), DraftId = plan.Id, Revision = plan.Revision, PlanName = plan.PlanName, ContentJson = json ?? JsonSerializer.Serialize(plan, E.Json),
            Reason = reason, SavedBy = plan.UpdatedBy, SavedAt = plan.UpdatedAt,
        });

    /// <summary>The saved revisions of a plan, newest first. Revisions saved before history was recorded are not listed (the draft itself is).</summary>
    public async Task<List<TestPlanRevisionSummary>> RevisionsAsync(Guid id, CancellationToken ct)
    {
        var draft = await db.TestPlanDrafts.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct)
            ?? throw new TestPlanException(404, "draft-not-found", "No such Test Plan draft.");
        var rows = await db.TestPlanDraftRevisions.AsNoTracking().Where(r => r.DraftId == id).OrderByDescending(r => r.Revision).ToListAsync(ct);
        return [.. rows.Select(r => new TestPlanRevisionSummary(r.Revision, r.Reason, r.SavedBy, r.SavedAt, r.PlanName, CaseCount(r.ContentJson), r.Revision == draft.Revision))];
    }

    /// <summary>One saved revision as it was (read-only).</summary>
    public async Task<TestPlanDraft> RevisionAsync(Guid id, int revision, CancellationToken ct)
    {
        var row = await db.TestPlanDraftRevisions.AsNoTracking().SingleOrDefaultAsync(r => r.DraftId == id && r.Revision == revision, ct)
            ?? throw new TestPlanException(404, "revision-not-found", $"Revision {revision} of this plan is not in its history.");
        return JsonSerializer.Deserialize<TestPlanDraft>(row.ContentJson, E.Json)! with { Revision = row.Revision };
    }

    /// <summary>Restores an earlier revision as the next revision: history is never rewritten and the replaced state stays in history. Manual runs of
    /// cases the restored revision does not hold stay in run history.</summary>
    public async Task<TestPlanDraft> RestoreRevisionAsync(Guid id, RestoreTestPlanRevisionRequest request, string actor, CancellationToken ct)
    {
        var row = await Mutable(id, ct);
        if (row.Revision != request.BaseRevision) throw new TestPlanException(409, "draft-changed", $"The plan is at revision {row.Revision}; reload it. Nothing was restored.");
        if (request.Revision == row.Revision) throw new TestPlanException(409, "revision-current", "That revision is the current plan.");
        var old = await RevisionAsync(id, request.Revision, ct);
        return await WriteAsync(row, old with { Id = id, Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now }, ct, $"Restored from revision {request.Revision}");
    }

    private static int CaseCount(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.TryGetProperty("cases", out var c) && c.ValueKind == JsonValueKind.Array ? c.GetArrayLength() : 0; }
        catch (JsonException) { return 0; }
    }

    private async Task<TestPlanDraftRecord> Row(Guid id, CancellationToken ct) =>
        await db.TestPlanDrafts.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw new TestPlanException(404, "draft-not-found", "No such Test Plan draft.");

    private static TestPlanDraft Read(TestPlanDraftRecord row) => JsonSerializer.Deserialize<TestPlanDraft>(row.ContentJson, E.Json)! with { Revision = row.Revision };

    private async Task<TestPlanDraft> WriteAsync(TestPlanDraftRecord row, TestPlanDraft plan, CancellationToken ct, string reason = "Saved")
    {
        Bound(plan);
        row.Revision = plan.Revision;
        row.PlanName = plan.PlanName;
        row.DeliveryLabel = plan.DeliveryLabel;
        row.ContentJson = JsonSerializer.Serialize(plan, E.Json);
        row.UpdatedBy = plan.UpdatedBy;
        row.UpdatedAt = plan.UpdatedAt;
        AddRevision(plan, reason, row.ContentJson); // same transaction: every saved draft state has a revision
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

    private static TestPlanDraftSummary Summary(TestPlanDraft p, TestPlanDraftRecord? row) => Summary(p) with
    {
        Lifecycle = Enum.TryParse<TestPlanLifecycle>(row?.Lifecycle, out var state) ? state : TestPlanLifecycle.Active,
        LifecycleChangedAt = row?.LifecycleChangedAt, LifecycleChangedBy = row?.LifecycleChangedBy,
    };

    private static TestPlanDraftSummary Summary(TestPlanDraft p) => new TestPlanDraftSummary(p.Id, p.GuideKey, p.PlanName, p.DeliveryLabel, p.Revision, p.Cases.Count,
        p.Cases.Count(c => c.Review == TestCaseReviewState.Accepted), p.Cases.Count(c => c.Review == TestCaseReviewState.NeedsClarification),
        p.Cases.Count(c => c.Review != TestCaseReviewState.Excluded && c.Missing.Any(m => m.StartsWith("Expected result: NEEDS", StringComparison.Ordinal))),
        p.Cases.Count(c => c.Review != TestCaseReviewState.Excluded && c.TestData.Count == 0),
        p.Cases.Count(c => c.Review != TestCaseReviewState.Excluded && !c.Testability.Ready), p.UpdatedBy, p.UpdatedAt)
    {
        Scenarios = p.Cases.Where(c => c.Review != TestCaseReviewState.Superseded).Select(c => c.ParentScenario).OfType<string>().Distinct(StringComparer.Ordinal).Count(),
        ApplicationKey = p.ApplicationKey, ProjectName = p.ProjectName,
    };

    private static TestPlanSnapshotSummary SnapshotSummary(TestPlanSnapshotRecord s) => new(s.Id, s.DraftId, s.GuideKey, s.Sequence, s.PlanName, s.DeliveryLabel, s.DraftRevision,
        s.ContentFingerprint, s.RuleVersion, s.GuideRevision, s.CaseCount, s.CreatedBy, s.CreatedAt, s.Note);
}
