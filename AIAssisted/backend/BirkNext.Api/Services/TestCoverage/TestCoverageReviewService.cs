using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using BirkNext.SourceEvidence;
using BirkNext.TestCoverage;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.TestCoverage;

public interface ITestCoverageReviewService
{
    Task<ReviewSourceOptions> SourcesAsync(CancellationToken ct = default);
    Task<TestCoverageOutcome> RunAsync(TestCoverageReviewRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<TestCoverageRunSummary>> HistoryAsync(CancellationToken ct = default);
    Task<TestCoverageReviewResult?> GetAsync(Guid runId, CancellationToken ct = default);
    Task<IReadOnlyList<CoverageDecision>> DecisionsAsync(string repositoryKey, CancellationToken ct = default);
    Task<(CoverageDecision? Decision, string? Error)> DecideAsync(CoverageDecision decision, CancellationToken ct = default);
}

public sealed record TestCoverageOutcome(TestCoverageReviewResult? Result, int Status, string? Error)
{
    public static TestCoverageOutcome Fail(int status, string error) => new(null, status, error);
}

/// <summary>
/// Runs the Test Coverage &amp; Overlap Review over a Source Analysis snapshot through the shared source-evidence provider (no upload, no
/// rescan), with Critical E2E flow definitions and recorded runs as QA automated evidence (nothing is executed), and stores each run bound
/// to its exact snapshot ids. Reviewer decisions are review metadata per repository; they never overwrite source evidence.
/// </summary>
public sealed class TestCoverageReviewService(AppDbContext db, IReviewSourceEvidenceProvider sources, ICriticalE2EStore? e2e = null,
    SourceRepositoryAssociationService? associations = null, BirkNext.Api.Services.IntegrationJourneys.IIntegrationJourneyPackRegistry? journeyPacks = null,
    BirkNext.Api.Services.IntegrationJourneys.DeclaredIntegrationJourneyStore? declaredJourneys = null,
    BirkNext.Api.Services.TestData.TestDataCatalogService? testData = null) : ITestCoverageReviewService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedValues = new(StringComparer.Ordinal) { "Confirmed", "Rejected", "DeveloperAutomated", "QaAutomated", "Unknown", "Accepted" };

    public async Task<ReviewSourceOptions> SourcesAsync(CancellationToken ct = default)
    {
        if (!sources.SourceAnalysisEnabled) return new ReviewSourceOptions { SourceAnalysisEnabled = false };
        var list = await sources.ListAsync("", ct);
        return new ReviewSourceOptions
        {
            RepositoryAssociations = associations is null ? [] : [.. await associations.ActiveAsync(ct)],
            Snapshots = ReviewSourceEvidenceProvider.Describe(list, s => s.TestBehaviorEvidence is null
                ? new ConsumerSourceEvidence(false, "Analyzed before per-test facts existed: tests are listed but not analyzed until the source is analyzed again.")
                : new ConsumerSourceEvidence(true, null, $"{s.TestBehaviorEvidence.Tests.Count} test(s) analyzed in {s.TestBehaviorEvidence.TestFilesAnalyzed} file(s)")),
        };
    }

    public async Task<TestCoverageOutcome> RunAsync(TestCoverageReviewRequest request, CancellationToken ct = default)
    {
        if (!sources.SourceAnalysisEnabled) return TestCoverageOutcome.Fail(409, ReviewSourceEvidenceProvider.Disabled);
        if (request.CurrentSnapshotId == Guid.Empty) return TestCoverageOutcome.Fail(400, TestCoverageText.SourceRequired);
        if (request.BaselineSnapshotId == request.CurrentSnapshotId) return TestCoverageOutcome.Fail(400, "The baseline must be a different snapshot than the current one.");
        var current = await sources.ResolveAsync("", request.CurrentSnapshotId, ct);
        if (current is null) return TestCoverageOutcome.Fail(404, "The selected snapshot is unavailable; nothing is substituted.");
        IqrSourceSnapshot? baseline = null;
        BaselineRelation? relation = null;
        if (request.BaselineSnapshotId is { } baselineId)
        {
            baseline = await sources.ResolveAsync("", baselineId, ct);
            if (baseline is null) return TestCoverageOutcome.Fail(404, "The selected baseline snapshot is unavailable; nothing is substituted.");
            (relation, var refused) = await BaselineRelationAsync(current, baseline, request.BaselineSameRepositoryConfirmed, request.RememberRepositoryAssociation, ct);
            if (refused is not null) return TestCoverageOutcome.Fail(400, refused);
        }
        var repositoryKey = ReviewSourceEvidenceProvider.Identity(current).Key;
        var decisions = await DecisionsAsync(repositoryKey, ct);
        var flows = await FlowsAsync(request.EnvironmentId, ct);
        var result = TestCoverageReviewEngine.Review(current, baseline, request, decisions, flows, DateTimeOffset.UtcNow, ct, IntegrationJourneyCatalog(request.ProjectKey)) with { BaselineRelation = relation };
        result = await WithTestDataReferencesAsync(result, request.ProjectKey, ct);
        var label = $"{result.Current.Repository} · {result.Inventory.TestsDiscovered} test(s) · {result.Journeys.Count} journey(s)";
        db.TestCoverageReviewRuns.Add(new TestCoverageReviewRunRecord
        {
            Id = result.RunId, CompletedAt = result.CompletedAt, CurrentSnapshotId = current.Id, BaselineSnapshotId = baseline?.Id,
            Label = label.Length > 300 ? label[..300] : label, ResultJson = JsonSerializer.Serialize(result, Json),
        });
        await db.SaveChangesAsync(ct);
        return new(result, 200, null);
    }

    /// <summary>The baseline's relation through the shared rule: same identity, a remembered declaration, or this run's explicit confirmation
    /// (remembered when the reviewer asked for it).</summary>
    private async Task<(BaselineRelation? Relation, string? Error)> BaselineRelationAsync(IqrSourceSnapshot current, IqrSourceSnapshot baseline, bool confirmed, bool remember,
        CancellationToken ct)
    {
        var (currentId, baselineId) = (ReviewSourceEvidenceProvider.Identity(current), ReviewSourceEvidenceProvider.Identity(baseline));
        var remembered = associations is null ? null : await associations.FindAsync(currentId.Key, baselineId.Key, ct);
        var result = BaselineEligibility.Validate(currentId, baselineId, confirmed, remembered);
        if (result.Relation is { Kind: BaselineRelationKind.DeclaredByReviewer } && remembered is null && remember && associations is not null)
            await associations.DeclareAsync(new(currentId.Key, currentId.DisplayName, baselineId.Key, baselineId.DisplayName), "local user", ct);
        return result;
    }

    /// <summary>Critical E2E flows with their Test Data identity from authoritative links only (the flow's own asset ids, Test Plan draft cases
    /// sourced from or confirmed against the flow). Read-only: no store is changed.</summary>
    private async Task<List<E2EFlowEvidence>> FlowsAsync(string? environmentId, CancellationToken ct)
    {
        if (e2e is null || string.IsNullOrWhiteSpace(environmentId)) return [];
        var history = e2e.History(environmentId, 200);
        var definitions = e2e.Flows(environmentId).Where(f => f.Enabled).ToList();
        if (definitions.Count == 0) return [];
        var drafts = await DraftsAsync(ct);
        return definitions.Select(f =>
        {
            var latest = history.Where(r => r.FlowId == f.Id).OrderByDescending(r => r.StartedAt).FirstOrDefault();
            return TestCoverageDataLinks.With(new E2EFlowEvidence(f.Id, f.Name, f.Module, f.Description, latest?.Status, latest?.StartedAt), f, drafts, environmentId);
        }).ToList();
    }

    /// <summary>Every stored Test Plan draft (read-only). An unreadable draft is skipped, never guessed.</summary>
    private async Task<List<BirkNext.TestPlanning.TestPlanDraft>> DraftsAsync(CancellationToken ct)
    {
        var drafts = new List<BirkNext.TestPlanning.TestPlanDraft>();
        foreach (var row in await db.TestPlanDrafts.AsNoTracking().ToListAsync(ct))
        {
            try
            {
                if (JsonSerializer.Deserialize<BirkNext.TestPlanning.TestPlanDraft>(row.ContentJson, BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine.Json) is { } plan)
                    drafts.Add(plan with { Revision = row.Revision, PlanName = string.IsNullOrWhiteSpace(plan.PlanName) ? row.PlanName : plan.PlanName });
            }
            catch (JsonException) { }
        }
        return drafts;
    }

    /// <summary>Integration Journey definitions (global pack registry, then the journeys declared for this project) as design-level edges, for
    /// structural association candidates only. Only the declared From/To components are used (a step without them contributes nothing to the
    /// chain; the owner is not substituted). Another project's declared journeys are never read.</summary>
    private IReadOnlyList<BirkNext.Integrations.IntegrationJourneyCatalogEntry> IntegrationJourneyCatalog(string? projectKey) =>
        [.. (journeyPacks?.Packs ?? []).SelectMany(pack => pack.Journeys.OrderBy(j => j.Order).Select(j => new BirkNext.Integrations.IntegrationJourneyCatalogEntry(pack.PackId, j.JourneyId, j.DisplayName,
            [.. j.Steps.Select(step => new BirkNext.Integrations.JourneyEdge(step.StepId, step.Label, step.Kind, step.FromComponent ?? "", step.ToComponent ?? "", step.Owner))]))),
         .. (declaredJourneys?.ForProject(projectKey) ?? []).Select(DeclaredIntegrationJourneyRules.CatalogEntry)];

    /// <summary>Test Data's consumer references for this project's QA tests (usages with their current state, requirements), merged by
    /// <see cref="TestCoverageDataLinks.WithConsumerReferences"/>. Read-only here; when Test Data cannot be read, the QA tests keep their other
    /// sources and a limitation says so (never "no data needed").</summary>
    private async Task<TestCoverageReviewResult> WithTestDataReferencesAsync(TestCoverageReviewResult result, string? projectKey, CancellationToken ct)
    {
        if (testData is null || result.QaTests.Count == 0) return result;
        try
        {
            var consumers = await testData.ConsumersAsync(ct);
            var assets = (await testData.ViewAsync(ct)).Assets;
            return result with { QaTests = TestCoverageDataLinks.WithConsumerReferences(result.QaTests, projectKey, consumers.Usages, consumers.Requirements, assets) };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            return result with { Limitations = [.. result.Limitations, "Test Data consumer references could not be read; QA test data needs come from the Critical E2E flows and Test Plan cases only."] };
        }
    }

    public async Task<IReadOnlyList<TestCoverageRunSummary>> HistoryAsync(CancellationToken ct = default) =>
        (await db.TestCoverageReviewRuns.AsNoTracking().OrderByDescending(r => r.CompletedAt).Take(20).ToListAsync(ct))
            .Select(r => new TestCoverageRunSummary(r.Id, r.CompletedAt, r.CurrentSnapshotId, r.BaselineSnapshotId, r.Label)).ToList();

    public async Task<TestCoverageReviewResult?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        var json = await db.TestCoverageReviewRuns.AsNoTracking().Where(r => r.Id == runId).Select(r => r.ResultJson).FirstOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<TestCoverageReviewResult>(json, Json);
    }

    public async Task<IReadOnlyList<CoverageDecision>> DecisionsAsync(string repositoryKey, CancellationToken ct = default) =>
        (await db.TestCoverageDecisions.AsNoTracking().Where(d => d.RepositoryKey == repositoryKey).OrderBy(d => d.DecidedAt).ToListAsync(ct))
            .Select(d => new CoverageDecision
            {
                RepositoryKey = d.RepositoryKey, Kind = Enum.TryParse<CoverageDecisionKind>(d.Kind, out var k) ? k : CoverageDecisionKind.JourneyConnection,
                SubjectKey = d.SubjectKey, Value = d.Value, Note = d.Note, DecidedAt = d.DecidedAt,
            }).ToList();

    public async Task<(CoverageDecision? Decision, string? Error)> DecideAsync(CoverageDecision decision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(decision.RepositoryKey) || string.IsNullOrWhiteSpace(decision.SubjectKey) || decision.SubjectKey.Length > 600)
            return (null, "A repository and subject are required.");
        if (!AllowedValues.Contains(decision.Value)) return (null, "Unsupported decision value.");
        var stored = decision with { DecidedAt = DateTimeOffset.UtcNow, Note = decision.Note is { Length: > 500 } n ? n[..500] : decision.Note };
        db.TestCoverageDecisions.Add(new TestCoverageDecisionRecord
        {
            Id = Guid.NewGuid(), RepositoryKey = stored.RepositoryKey, Kind = stored.Kind.ToString(), SubjectKey = stored.SubjectKey, Value = stored.Value, Note = stored.Note, DecidedAt = stored.DecidedAt,
        });
        await db.SaveChangesAsync(ct);
        return (stored, null);
    }
}
