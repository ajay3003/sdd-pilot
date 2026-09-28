using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Integrations.ApplicationMessaging;

public interface IApplicationMessagingStore
{
    Task<ApplicationMessagingEvidenceSet?> GetAsync(string environmentId, CancellationToken ct = default);
    Task<(ApplicationMessagingEvidenceSet? Set, string? Error)> AnalyzeAsync(string environmentId, IReadOnlyList<(string FileName, byte[] Bytes)> archives, CancellationToken ct = default);
    Task<ApplicationMessagingEvidenceSet?> BindAsync(string environmentId, string applicationId, string? consumer, CancellationToken ct = default);
    Task<bool> DeleteAsync(string environmentId, CancellationToken ct = default);
}

/// <summary>
/// The application-messaging evidence set of an environment: facts extracted from uploaded source archives (file:line provenance and the
/// archive hash). Source text is never stored. Re-analysis replaces the set; explicit consumer bindings survive for the same application.
/// </summary>
public sealed class ApplicationMessagingStore(AppDbContext db, ILogger<ApplicationMessagingStore> logger) : IApplicationMessagingStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ApplicationMessagingEvidenceSet?> GetAsync(string environmentId, CancellationToken ct = default)
    {
        var record = await db.ApplicationMessagingEvidence.AsNoTracking().FirstOrDefaultAsync(r => r.EnvironmentId == environmentId, ct);
        return record is null ? null : JsonSerializer.Deserialize<ApplicationMessagingEvidenceSet>(record.EvidenceJson, Json);
    }

    public async Task<(ApplicationMessagingEvidenceSet? Set, string? Error)> AnalyzeAsync(string environmentId, IReadOnlyList<(string FileName, byte[] Bytes)> archives, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return (null, "An analysis must belong to a Target Environment.");
        if (archives.Count == 0) return (null, "Upload at least one source archive (.zip).");
        var metadata = new List<SourceArchive>();
        var files = new List<SourceFile>();
        foreach (var (name, bytes) in archives)
        {
            var (archive, read, error) = SourceArchiveReader.Read(name, bytes);
            if (error is not null) return (null, error);
            metadata.Add(archive!);
            files.AddRange(read);
        }
        var set = WolverineSourceAnalyzer.Analyze(environmentId, metadata, files, DateTimeOffset.UtcNow);
        var previous = await GetAsync(environmentId, ct);
        set = set with
        {
            Applications = set.Applications.Select(a => a with { BoundConsumer = previous?.Applications.FirstOrDefault(p => p.ApplicationId == a.ApplicationId)?.BoundConsumer }).ToList(),
        };
        await SaveAsync(set, ct);
        foreach (var app in set.Applications)
            logger.LogInformation(
                "Application messaging analysis for {EnvironmentId}: {Application} Wolverine {Detection}, {Handlers} handler(s), {Routes} route(s), retry policy {Retry}, outbox {Outbox}.",
                environmentId, app.ApplicationId, app.Detection, app.Handlers.Count, app.Routes.Count, app.RetryPolicy, app.Outbox);
        logger.LogInformation("Application messaging analysis for {EnvironmentId}: {Archives} archive(s) ({Hashes}), {Files} file(s), {Applications} application(s).",
            environmentId, metadata.Count, string.Join(",", metadata.Select(m => m.Sha256[..12])), files.Count, set.Applications.Count);
        return (set, null);
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
