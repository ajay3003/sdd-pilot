using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
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
public sealed class TestCoverageReviewService(AppDbContext db, IReviewSourceEvidenceProvider sources, ICriticalE2EStore? e2e = null) : ITestCoverageReviewService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedValues = new(StringComparer.Ordinal) { "Confirmed", "Rejected", "DeveloperAutomated", "QaAutomated", "Unknown", "Accepted" };

    public async Task<ReviewSourceOptions> SourcesAsync(CancellationToken ct = default)
    {
        if (!sources.SourceAnalysisEnabled) return new ReviewSourceOptions { SourceAnalysisEnabled = false };
        var list = await sources.ListAsync("", ct);
        return new ReviewSourceOptions
        {
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
        if (request.BaselineSnapshotId is { } baselineId)
        {
            baseline = await sources.ResolveAsync("", baselineId, ct);
            if (baseline is null) return TestCoverageOutcome.Fail(404, "The selected baseline snapshot is unavailable; nothing is substituted.");
            if (ReviewSourceEvidenceProvider.Identity(baseline).Key != ReviewSourceEvidenceProvider.Identity(current).Key)
                return TestCoverageOutcome.Fail(400, "The baseline belongs to another repository; a change-aware review compares two snapshots of the same repository.");
        }
        var repositoryKey = ReviewSourceEvidenceProvider.Identity(current).Key;
        var decisions = await DecisionsAsync(repositoryKey, ct);
        var flows = Flows(request.EnvironmentId);
        var result = TestCoverageReviewEngine.Review(current, baseline, request, decisions, flows, DateTimeOffset.UtcNow, ct);
        var label = $"{result.Current.Repository} · {result.Inventory.TestsDiscovered} test(s) · {result.Journeys.Count} journey(s)";
        db.TestCoverageReviewRuns.Add(new TestCoverageReviewRunRecord
        {
            Id = result.RunId, CompletedAt = result.CompletedAt, CurrentSnapshotId = current.Id, BaselineSnapshotId = baseline?.Id,
            Label = label.Length > 300 ? label[..300] : label, ResultJson = JsonSerializer.Serialize(result, Json),
        });
        await db.SaveChangesAsync(ct);
        return new(result, 200, null);
    }

    private List<E2EFlowEvidence> Flows(string? environmentId)
    {
        if (e2e is null || string.IsNullOrWhiteSpace(environmentId)) return [];
        var history = e2e.History(environmentId, 200);
        return e2e.Flows(environmentId).Where(f => f.Enabled).Select(f =>
        {
            var latest = history.Where(r => r.FlowId == f.Id).OrderByDescending(r => r.StartedAt).FirstOrDefault();
            return new E2EFlowEvidence(f.Id, f.Name, f.Module, f.Description, latest?.Status, latest?.StartedAt);
        }).ToList();
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
