using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Integrations.ApplicationMessaging;

public interface IApplicationMessagingStore
{
    Task<ApplicationMessagingEvidenceSet?> GetAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Source Analysis snapshots for application messaging and, for a chosen scope, its candidates and problems. Read-only.</summary>
    Task<ReviewSourceOptions> SourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default);
    /// <summary>Builds the environment's evidence set from exactly these Source Analysis snapshots (no upload, no substitution).</summary>
    Task<(ApplicationMessagingEvidenceSet? Set, string? Error)> UseSourceScopeAsync(string environmentId, ReviewSourceScopeRequest scope, CancellationToken ct = default);
    Task<ApplicationMessagingEvidenceSet?> BindAsync(string environmentId, string applicationId, string? consumer, CancellationToken ct = default);
    Task<bool> DeleteAsync(string environmentId, CancellationToken ct = default);
}

/// <summary>
/// The application-messaging evidence set of an environment: facts extracted from source (file:line provenance and the archive hash).
/// Source comes from Source Analysis snapshots — Source Analysis runs <see cref="ExtractSnapshotEvidence"/> on each uploaded archive and the
/// set is built from the snapshots a user chooses; this review has no upload of its own. Source text is never stored. Building the set again
/// replaces it; explicit consumer bindings survive for the same application. Wolverine source evidence stays source/build evidence: runtime
/// handler processing is separate runtime evidence.
/// </summary>
public sealed class ApplicationMessagingStore(AppDbContext db, ILogger<ApplicationMessagingStore> logger, IReviewSourceEvidenceProvider? sources = null) : IApplicationMessagingStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string NoEvidence = "Analyzed before application-messaging evidence was captured. Analyze the archive again in Source Analysis to create a new snapshot.";
    private IReviewSourceEvidenceProvider Sources => sources ?? new ReviewSourceEvidenceProvider(new SourceEvidence.IqrSourceStore(db));

    /// <summary>Source Analysis' capture step: this analyzer over one archive (never throws; a source it cannot read records why).</summary>
    public static ApplicationMessagingEvidenceSet? ExtractSnapshotEvidence(string environmentId, string name, byte[] bytes)
    {
        try
        {
            var (archive, files, error) = SourceArchiveReader.Read(name, bytes);
            return error is not null || archive is null ? new ApplicationMessagingEvidenceSet { EnvironmentId = environmentId, AnalyzedAt = DateTimeOffset.UtcNow, Limitations = [error ?? "The archive could not be read."] }
                : WolverineSourceAnalyzer.Analyze(environmentId, [archive], files, DateTimeOffset.UtcNow);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    public static ConsumerSourceEvidence Evidence(IqrSourceSnapshot s) => s.ApplicationMessagingEvidence is not { } set ? new(false, NoEvidence)
        : new(true, set.Applications.Count == 0 ? "No Wolverine application was found in this snapshot." : null,
            $"{set.Applications.Count} application(s) · {set.Applications.Sum(a => a.Handlers.Count)} handler(s) · {set.Applications.Sum(a => a.Routes.Count)} route(s)");

    private static string? Problem(IqrSourceSnapshot s) => s.ApplicationMessagingEvidence is null ? NoEvidence : null;

    /// <summary>Related sources for application messaging: exact package/project references to sources that host a Wolverine application of their own.</summary>
    public static List<RelatedSourceCandidate> Candidates(IqrSourceSnapshot primary, IReadOnlyList<IqrSourceSnapshot> all) =>
        ReviewSourceEvidenceProvider.ReferenceCandidates(primary, all).Select(c => c with
        {
            MatchingSnapshotIds = c.MatchingSnapshotIds.Where(id => all.FirstOrDefault(s => s.Id == id)?.ApplicationMessagingEvidence?.Applications.Count > 0).ToList(),
        }).Where(c => c.State == RelatedSourceState.SnapshotUnavailable || c.MatchingSnapshotIds.Count > 0).ToList();

    public async Task<ReviewSourceOptions> SourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default)
    {
        var snapshots = Sources.SourceAnalysisEnabled ? await Sources.ListAsync(environmentId, ct) : [];
        return ReviewSourceEvidenceProvider.Options(Sources.SourceAnalysisEnabled, snapshots, Evidence, scope, p => Candidates(p, snapshots), Problem);
    }

    public async Task<(ApplicationMessagingEvidenceSet? Set, string? Error)> UseSourceScopeAsync(string environmentId, ReviewSourceScopeRequest scope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return (null, "An analysis must belong to a Target Environment.");
        var snapshots = await Sources.ListAsync(environmentId, ct);
        var (selected, error) = ReviewSourceEvidenceProvider.Validate(scope, snapshots, Problem, Sources.SourceAnalysisEnabled);
        if (selected is null) return (null, error);
        // Each snapshot was analyzed on its own; the set lists every snapshot's applications with their own archive provenance.
        var parts = selected.Select(s => s.ApplicationMessagingEvidence!).ToList();
        var previous = await GetAsync(environmentId, ct);
        var set = new ApplicationMessagingEvidenceSet
        {
            EnvironmentId = environmentId, AnalyzedAt = DateTimeOffset.UtcNow, AnalyzerVersion = parts.Min(p => p.AnalyzerVersion), Archives = parts.SelectMany(p => p.Archives).ToList(),
            Applications = parts.SelectMany(p => p.Applications).Select(a => a with { BoundConsumer = previous?.Applications.FirstOrDefault(x => x.ApplicationId == a.ApplicationId)?.BoundConsumer }).ToList(),
            Limitations = [.. parts.SelectMany(p => p.Limitations).Distinct(), .. selected.Count > 1 ? new[] { "Each source snapshot is analyzed on its own: a handler or message declared in another snapshot is not resolved across snapshots." } : []],
            SourceScope = ReviewSourceEvidenceProvider.Scope(selected, Candidates(selected[0], snapshots), scope.ExcludedSuggestions),
        };
        await SaveAsync(set, ct);
        logger.LogInformation("Application messaging evidence for {EnvironmentId} built from Source Analysis snapshot(s) {Fingerprints}: {Applications} application(s).",
            environmentId, string.Join(",", selected.Select(s => s.Archive.Sha256[..Math.Min(12, s.Archive.Sha256.Length)])), set.Applications.Count);
        return (set, null);
    }

    public async Task<ApplicationMessagingEvidenceSet?> GetAsync(string environmentId, CancellationToken ct = default)
    {
        var record = await db.ApplicationMessagingEvidence.AsNoTracking().FirstOrDefaultAsync(r => r.EnvironmentId == environmentId, ct);
        return record is null ? null : JsonSerializer.Deserialize<ApplicationMessagingEvidenceSet>(record.EvidenceJson, Json);
    }

    public async Task<ApplicationMessagingEvidenceSet?> BindAsync(string environmentId, string applicationId, string? consumer, CancellationToken ct = default)
    {
        var set = await GetAsync(environmentId, ct);
        if (set is null || set.Applications.All(a => a.ApplicationId != applicationId)) return null;
        var bound = string.IsNullOrWhiteSpace(consumer) ? null : consumer.Trim();
        set = set with { Applications = set.Applications.Select(a => a.ApplicationId == applicationId ? a with { BoundConsumer = bound } : a).ToList() };
        await SaveAsync(set, ct);
        logger.LogInformation("Application messaging binding for {EnvironmentId}: {Application} bound to consumer {Consumer}.", environmentId, applicationId, bound ?? "(none)");
        return set;
    }

    public async Task<bool> DeleteAsync(string environmentId, CancellationToken ct = default)
    {
        var record = await db.ApplicationMessagingEvidence.FirstOrDefaultAsync(r => r.EnvironmentId == environmentId, ct);
        if (record is null) return false;
        db.ApplicationMessagingEvidence.Remove(record);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task SaveAsync(ApplicationMessagingEvidenceSet set, CancellationToken ct)
    {
        var record = await db.ApplicationMessagingEvidence.FirstOrDefaultAsync(r => r.EnvironmentId == set.EnvironmentId, ct);
        if (record is null) db.ApplicationMessagingEvidence.Add(record = new ApplicationMessagingEvidenceRecord { EnvironmentId = set.EnvironmentId });
        record.AnalyzedAt = set.AnalyzedAt;
        record.EvidenceJson = JsonSerializer.Serialize(set, Json);
        await db.SaveChangesAsync(ct);
    }
}
