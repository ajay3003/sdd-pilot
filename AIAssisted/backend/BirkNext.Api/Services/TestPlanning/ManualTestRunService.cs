using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Manual test runs (Manual Testing → Execute &amp; document). A run executes one saved test case of one project, read through
/// <see cref="TestPlanSuggestionService"/> (the authoritative plan store; no copy of the plan is kept). The case's steps, expected results,
/// requirement links and Test Data names are pinned in the run at start together with a content fingerprint, so editing the case later never
/// changes a recorded result. Outcomes are only what a tester records; the overall outcome is rolled up by <see cref="ManualRunRules.Rollup"/>
/// when the tester completes the run. Evidence is stored as references (links or notes), never as uploaded files; defects are local
/// observations that may name an existing external work item. Nothing is sent anywhere.
/// </summary>
public sealed class ManualTestRunService(AppDbContext db, TestPlanSuggestionService plans, TimeProvider? clock = null)
{
    public const int MaxEvidence = 50;
    public const int MaxDefects = 50;
    public const int MaxText = 4000;
    /// <summary>The largest page of the run list (and the first page the unpaged list returns).</summary>
    public const int MaxPage = 500;

    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    /// <summary>Starts a run of the case as it is saved now, or returns the case's run that is still in progress (resume, never two at once).</summary>
    public async Task<ManualTestRun> StartAsync(StartManualRunRequest request, string actor, CancellationToken ct)
    {
        var key = TestScenarioDiscoveryService.Key(request.ProjectKey);
        var plan = await plans.DraftAsync(request.DraftId, ct);
        if (plan.ProjectKey != key) throw new TestPlanException(404, "draft-not-found", "No such test plan in this project.");
        var c = plan.Cases.FirstOrDefault(x => x.Source.ScenarioId == request.CaseId) ?? throw new TestPlanException(404, "case-not-found", "No such test case in this plan.");
        if (c.Review is TestCaseReviewState.Excluded or TestCaseReviewState.Superseded)
            throw new TestPlanException(409, "case-not-runnable", "This test case is excluded or superseded, so it cannot be run. Open it in Plan & prepare to change that.");
        var steps = c.EffectiveSteps;
        if (steps.Count == 0) throw new TestPlanException(409, "case-has-no-steps", "Add at least one step to the test case before running it.");

        var open = await db.ManualTestRuns.AsNoTracking().Where(r => r.ProjectKey == key && r.DraftId == plan.Id && r.CaseId == c.Source.ScenarioId && r.State == nameof(ManualRunState.InProgress))
            .OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
        if (open is not null) return Read(open);

        var expected = c.EffectiveStepExpected;
        // A retest: the case's latest completed run failed or was blocked (the new run records which one it retests).
        var previous = (await db.ManualTestRuns.AsNoTracking().Where(r => r.ProjectKey == key && r.DraftId == plan.Id && r.CaseId == c.Source.ScenarioId && r.State == nameof(ManualRunState.Completed))
            .OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.StartedAt).Take(1).ToListAsync(ct)).Select(Read).FirstOrDefault();
        var retestOf = previous is { Outcome: ManualRunOutcome.Failed or ManualRunOutcome.Blocked } ? previous.Id : (Guid?)null;
        var run = new ManualTestRun
        {
            Id = Guid.NewGuid(), ProjectKey = key, DraftId = plan.Id, CaseId = c.Source.ScenarioId, CaseKey = $"{plan.Id:N}/{c.Source.ScenarioId}", PlanName = plan.PlanName,
            PlanRevision = plan.Revision, CaseFingerprint = Fingerprint(c), Title = c.EffectiveTitle, Preconditions = c.EffectivePreconditions, ExpectedResult = c.EffectiveExpected,
            RequirementIds = Requirements(c), TestData = [.. c.TestData.Select(d => string.IsNullOrWhiteSpace(d.Alias) ? d.AssetId : $"{d.Alias} ({d.AssetId})")],
            Environment = Text(request.Environment, 200), Tester = actor, StartedAt = Now, State = ManualRunState.InProgress, Outcome = ManualRunOutcome.NotDecided,
            Steps = [.. steps.Select((s, i) => new ManualRunStep { Number = i + 1, Action = s, Expected = i < expected.Count ? expected[i] : null })],
            Revision = 1, UpdatedBy = actor, UpdatedAt = Now,
            Origin = Origin(c), RetestOf = retestOf,
        };
        db.ManualTestRuns.Add(new ManualTestRunRecord
        {
            Id = run.Id, ProjectKey = key, DraftId = run.DraftId, CaseId = run.CaseId, State = run.State.ToString(), Outcome = run.Outcome.ToString(), Revision = 1,
            ContentJson = JsonSerializer.Serialize(run, E.Json), Tester = Trim(actor, 200), StartedAt = run.StartedAt, UpdatedAt = run.UpdatedAt,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Another request started this case at the same moment: the database keeps one run in progress per case
            // (ux_manual_test_runs_one_in_progress), so resume that one instead of failing.
            db.ChangeTracker.Clear();
            var winner = await db.ManualTestRuns.AsNoTracking().Where(r => r.ProjectKey == key && r.DraftId == plan.Id && r.CaseId == c.Source.ScenarioId && r.State == nameof(ManualRunState.InProgress))
                .FirstOrDefaultAsync(ct) ?? throw new TestPlanException(409, "run-start-conflict", "The test run could not be started; try again.");
            return Read(winner);
        }
        return run;
    }

    public async Task<ManualTestRun> GetAsync(string projectKey, Guid id, CancellationToken ct) => Read(await Row(projectKey, id, ct, tracked: false));

    /// <summary>The project's first <see cref="MaxPage"/> runs, newest first (optionally of one plan or case). Never another project's. Callers
    /// that need more, or need to know how many exist, page with <see cref="ListPageAsync"/>.</summary>
    public async Task<List<ManualRunSummary>> ListAsync(string projectKey, Guid? draftId, string? caseId, CancellationToken ct) =>
        (await ListPageAsync(projectKey, draftId, caseId, 0, MaxPage, ct)).Runs;

    /// <summary>
    /// One page of the project's runs, newest first with a stable order (start time, then id, so a page boundary never repeats or skips a run),
    /// and the exact number of matching runs. Filtering, counting and paging happen in the database; only the page is read and deserialized.
    /// </summary>
    public async Task<(List<ManualRunSummary> Runs, int Total)> ListPageAsync(string projectKey, Guid? draftId, string? caseId, int skip, int take, CancellationToken ct)
    {
        if (skip < 0) throw new TestPlanException(400, "page-invalid", "skip must be zero or more.");
        if (take is < 1 or > MaxPage) throw new TestPlanException(400, "page-invalid", $"take must be between 1 and {MaxPage}.");
        var key = TestScenarioDiscoveryService.Key(projectKey);
        var query = db.ManualTestRuns.AsNoTracking().Where(r => r.ProjectKey == key);
        if (draftId is { } d) query = query.Where(r => r.DraftId == d);
        if (!string.IsNullOrWhiteSpace(caseId)) query = query.Where(r => r.CaseId == caseId);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).Skip(skip).Take(take).ToListAsync(ct);
        return ([.. rows.Select(Read).Select(ManualRunRules.Summary)], total);
    }

    /// <summary>
    /// What traceability and Test Coverage need from the project's history, bounded by its cases rather than by its history (audit 2026-11-15
    /// PERF-01): per case the latest completed run (completion time, then start time) and its run in progress, if any. Stopped runs never give
    /// an outcome, so they are not read. The selection runs on narrow columns; only the chosen runs are read in full. Unreadable rows are skipped,
    /// never guessed. <see cref="PreparedCaseTraceability"/> reaches the same answer from this as from every run.
    /// </summary>
    public static async Task<List<ManualTestRun>> ReadTraceabilityRunsAsync(AppDbContext db, string projectKey, CancellationToken ct)
    {
        const string completed = nameof(ManualRunState.Completed), inProgress = nameof(ManualRunState.InProgress);
        var heads = await db.ManualTestRuns.AsNoTracking().Where(r => r.ProjectKey == projectKey && (r.State == completed || r.State == inProgress))
            .Select(r => new { r.Id, r.DraftId, r.CaseId, r.State, r.CompletedAt, r.StartedAt }).ToListAsync(ct);
        var chosen = heads.GroupBy(h => (h.DraftId, h.CaseId)).SelectMany(g => new[]
        {
            g.Where(h => h.State == completed).OrderByDescending(h => h.CompletedAt).ThenByDescending(h => h.StartedAt).Select(h => (Guid?)h.Id).FirstOrDefault(),
            g.Where(h => h.State == inProgress).OrderByDescending(h => h.StartedAt).Select(h => (Guid?)h.Id).FirstOrDefault(),
        }).OfType<Guid>().ToList();
        var list = new List<ManualTestRun>(chosen.Count);
        foreach (var chunk in chosen.Chunk(1000))
            foreach (var row in await db.ManualTestRuns.AsNoTracking().Where(r => chunk.Contains(r.Id)).ToListAsync(ct))
                try { list.Add(Read(row)); } catch (JsonException) { }
        return list;
    }

    /// <summary>Every run of the project as recorded — the whole history, read in full. Not for per-request use (see
    /// <see cref="ReadTraceabilityRunsAsync"/>); kept as the reference the bounded read is verified against.</summary>
    public static async Task<List<ManualTestRun>> ReadProjectRunsAsync(AppDbContext db, string projectKey, CancellationToken ct)
    {
        var list = new List<ManualTestRun>();
        foreach (var row in await db.ManualTestRuns.AsNoTracking().Where(r => r.ProjectKey == projectKey).ToListAsync(ct))
            try { list.Add(Read(row)); } catch (JsonException) { }
        return list;
    }

    /// <summary>Saves progress: step decisions and observations, notes, environment, evidence references and defects.</summary>
    public async Task<ManualTestRun> SaveAsync(string projectKey, Guid id, SaveManualRunRequest request, string actor, CancellationToken ct)
    {
        var row = await Row(projectKey, id, ct, tracked: true);
        var run = Open(row, request.BaseRevision);
        var byNumber = request.Steps.GroupBy(s => s.Number).ToDictionary(g => g.Key, g => g.Last());
        var steps = run.Steps.Select(s =>
        {
            if (!byNumber.TryGetValue(s.Number, out var input)) return s;
            var actual = Text(input.Actual, MaxText);
            var changed = input.Outcome != s.Outcome || actual != s.Actual;
            return changed ? s with { Outcome = input.Outcome, Actual = actual, RecordedAt = Now } : s;
        }).ToList();
        var updated = run with
        {
            Steps = steps, Notes = Text(request.Notes, MaxText), Environment = Text(request.Environment, 200) ?? run.Environment,
            Evidence = Evidence(run, request.Evidence ?? [], actor), Defects = Defects(run, request.Defects ?? [], actor),
            Revision = run.Revision + 1, UpdatedBy = actor, UpdatedAt = Now,
        };
        return await WriteAsync(row, updated, ct);
    }

    /// <summary>Completes the run: every step must have a decision; the overall outcome is rolled up from them. Final afterwards.</summary>
    public async Task<ManualTestRun> CompleteAsync(string projectKey, Guid id, CompleteManualRunRequest request, string actor, CancellationToken ct)
    {
        var row = await Row(projectKey, id, ct, tracked: true);
        var run = Open(row, request.BaseRevision);
        var undecided = run.Steps.Count(s => s.Outcome is null);
        if (undecided > 0)
            throw new TestPlanException(409, "steps-undecided", $"{undecided} step{(undecided == 1 ? " has" : "s have")} no result yet. Record Pass, Fail, Blocked or Not run for every step, then complete the run.");
        return await WriteAsync(row, run with
        {
            State = ManualRunState.Completed, Outcome = ManualRunRules.Rollup(run.Steps), CompletedAt = Now, Revision = run.Revision + 1, UpdatedBy = actor, UpdatedAt = Now,
        }, ct);
    }

    /// <summary>Stops a run without a result (a reason is required). Its recorded steps stay as history; it never counts as an execution.</summary>
    public async Task<ManualTestRun> AbandonAsync(string projectKey, Guid id, AbandonManualRunRequest request, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new TestPlanException(400, "reason-required", "Say why the run is stopped.");
        var row = await Row(projectKey, id, ct, tracked: true);
        var run = Open(row, request.BaseRevision);
        return await WriteAsync(row, run with
        {
            State = ManualRunState.Abandoned, Outcome = ManualRunOutcome.NotDecided, AbandonReason = Text(request.Reason, 1000), CompletedAt = Now, Revision = run.Revision + 1,
            UpdatedBy = actor, UpdatedAt = Now,
        }, ct);
    }

    /// <summary>The identity of a case's executable content: title, preconditions, steps and expected results as the tester sees them.</summary>
    public static string Fingerprint(TestCaseDraft c) => ManualRunRules.CaseFingerprint(c); // shared with the web app; same bytes as before

    /// <summary>Specification → scenario → case, pinned on the run: the case's source, the scenario fingerprint, the specification file and acceptance
    /// scenario it was prepared from, and each requirement's file and basis — the same derivation Requirements Traceability uses.</summary>
    internal static ManualRunOrigin Origin(TestCaseDraft c)
    {
        var fromSpecification = c.Source.Kind is not (ScenarioSourceKind.SystemGuide or ScenarioSourceKind.Manual) && c.Generated.Capability == TestScenarioDiscoveryEngine.Specification;
        var specFile = fromSpecification ? PreparedCaseTraceability.SpecificationFile(c.Generated.Evidence) : null;
        var sources = new List<ManualRunRequirementSource>();
        if (fromSpecification)
            foreach (var id in c.Generated.Requirements.Select(r => r.RuleId).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                sources.Add(new(id, PreparedCaseTraceability.RequirementFile(c.Generated.Evidence, id) ?? specFile, "Specification evidence"));
        foreach (var l in c.RequirementLinks.Where(l => !string.IsNullOrWhiteSpace(l.RequirementId)))
            sources.Add(new(l.RequirementId.Trim(), string.IsNullOrWhiteSpace(l.SpecificationPath) ? null : l.SpecificationPath.Trim(), "Manual link", string.IsNullOrWhiteSpace(l.LinkedBy) ? null : l.LinkedBy));
        return new ManualRunOrigin
        {
            SourceKind = c.Source.Kind, ScenarioFingerprint = c.Source.Fingerprint, SpecificationFile = specFile,
            SpecificationScenarioId = fromSpecification ? PreparedCaseTraceability.ScenarioIdOf(c.Generated.Evidence) : null,
            Requirements = [.. sources.DistinctBy(s => (s.RequirementId.ToUpperInvariant(), s.SpecificationFile, s.Basis))],
        };
    }

    /// <summary>Requirement ids the case names: its own specification evidence and a tester's links (guide business rules are not requirements).</summary>
    private static List<string> Requirements(TestCaseDraft c)
    {
        var automatic = c.Source.Kind is ScenarioSourceKind.SystemGuide or ScenarioSourceKind.Manual || c.Generated.Capability != TestScenarioDiscoveryEngine.Specification
            ? [] : c.Generated.Requirements.Select(r => r.RuleId);
        return [.. automatic.Concat(c.RequirementLinks.Select(l => l.RequirementId)).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private List<ManualRunEvidence> Evidence(ManualTestRun run, List<ManualRunEvidence> wanted, string actor)
    {
        if (wanted.Count > MaxEvidence) throw new TestPlanException(400, "too-much-evidence", $"A run keeps at most {MaxEvidence} evidence references.");
        var held = run.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal);
        return [.. wanted.Select(e =>
        {
            var label = Text(e.Label, 200) ?? throw new TestPlanException(400, "evidence-label-required", "Give every evidence reference a short label.");
            var reference = Text(e.Reference, 1000) ?? throw new TestPlanException(400, "evidence-reference-required", "Say where the evidence is kept (a link or a note).");
            if (e.Kind == ManualEvidenceKind.Link && !SafeLink(reference))
                throw new TestPlanException(400, "evidence-link-invalid", "An evidence link must be a full http or https address.");
            var step = Step(run, e.Step);
            return held.TryGetValue(e.Id ?? "", out var old)
                ? old with { Label = label, Reference = reference, Kind = e.Kind, Step = step }
                : new ManualRunEvidence { Id = "ev-" + Guid.NewGuid().ToString("N")[..12], Label = label, Reference = reference, Kind = e.Kind, Step = step, AddedBy = actor, AddedAt = Now };
        })];
    }

    private List<ManualRunDefect> Defects(ManualTestRun run, List<ManualRunDefect> wanted, string actor)
    {
        if (wanted.Count > MaxDefects) throw new TestPlanException(400, "too-many-defects", $"A run keeps at most {MaxDefects} defects.");
        var held = run.Defects.ToDictionary(d => d.Id, StringComparer.Ordinal);
        return [.. wanted.Select(d =>
        {
            var title = Text(d.Title, 300) ?? throw new TestPlanException(400, "defect-title-required", "Give every defect a title.");
            var severity = Text(d.Severity, 20);
            if (severity is not null && !ManualRunRules.Severities.Contains(severity))
                throw new TestPlanException(400, "defect-severity-invalid", $"Severity must be one of {string.Join(", ", ManualRunRules.Severities)}.");
            var external = Text(d.ExternalReference, 1000);
            if (external is not null && !(external.Length <= 12 && external.All(char.IsAsciiDigit)) && !SafeLink(external))
                throw new TestPlanException(400, "defect-reference-invalid", "An external reference is a work item number or a full https address.");
            var item = new ManualRunDefect
            {
                Title = title, Description = Text(d.Description, MaxText), Reproduction = Text(d.Reproduction, MaxText), Expected = Text(d.Expected, MaxText), Actual = Text(d.Actual, MaxText),
                Severity = severity, Status = d.Status, ExternalReference = external, Step = Step(run, d.Step),
            };
            return held.TryGetValue(d.Id ?? "", out var old)
                ? item with { Id = old.Id, RecordedBy = old.RecordedBy, RecordedAt = old.RecordedAt }
                : item with { Id = "df-" + Guid.NewGuid().ToString("N")[..12], RecordedBy = actor, RecordedAt = Now };
        })];
    }

    private static int? Step(ManualTestRun run, int? step) =>
        step is null ? null : run.Steps.Any(s => s.Number == step) ? step : throw new TestPlanException(400, "step-unknown", $"The run has no step {step}.");

    /// <summary>Only absolute http(s) addresses; never javascript:, data:, file: or relative paths.</summary>
    public static bool SafeLink(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && !string.IsNullOrEmpty(uri.Host);

    private static ManualTestRun Open(ManualTestRunRecord row, int baseRevision)
    {
        var run = Read(row);
        if (run.State != ManualRunState.InProgress)
            throw new TestPlanException(409, "run-final", $"This run is {(run.State == ManualRunState.Completed ? "completed" : "stopped")} and cannot be changed. Start a new run to test again.");
        if (row.Revision != baseRevision) throw new TestPlanException(409, "run-changed", $"The run is at revision {row.Revision}; reload it. Your change was not saved.");
        return run;
    }

    private async Task<ManualTestRunRecord> Row(string projectKey, Guid id, CancellationToken ct, bool tracked)
    {
        var key = TestScenarioDiscoveryService.Key(projectKey);
        var rows = tracked ? db.ManualTestRuns : db.ManualTestRuns.AsNoTracking();
        return await rows.SingleOrDefaultAsync(r => r.Id == id && r.ProjectKey == key, ct)
            ?? throw new TestPlanException(404, "run-not-found", "No such test run in this project.");
    }

    private async Task<ManualTestRun> WriteAsync(ManualTestRunRecord row, ManualTestRun run, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(run, E.Json);
        if (json.Length > 2_000_000) throw new TestPlanException(413, "run-too-large", "The run is too large to store.");
        row.Revision = run.Revision;
        row.State = run.State.ToString();
        row.Outcome = run.Outcome.ToString();
        row.ContentJson = json;
        row.CompletedAt = run.CompletedAt;
        row.UpdatedAt = run.UpdatedAt;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            throw new TestPlanException(409, "run-changed", "The run was changed by someone else. Reload it; your change was not saved.");
        }
        return run;
    }

    private static ManualTestRun Read(ManualTestRunRecord row) => JsonSerializer.Deserialize<ManualTestRun>(row.ContentJson, E.Json)! with { Revision = row.Revision };

    private static string? Text(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];
}
