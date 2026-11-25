using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Stores the user-owned grouping and user story → requirement review decisions of one project and specification (2026-11-24). Every change is a
/// list of operations applied together by <see cref="StoryGroupingEngine.Apply"/> (all or nothing), against the revision the browser read: a stale
/// change is refused with the current revision, never merged. A new link may only name a requirement of the project's catalog for this
/// specification (as for plan saves). Source facts are never stored or changed here; saved plans and cases are never touched.
/// </summary>
public sealed class StoryGroupingService(AppDbContext db, TimeProvider clock, ILogger<StoryGroupingService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>How many applied change ids a document remembers for idempotent retries.</summary>
    public const int MaxRememberedRequests = 50;

    public sealed class GroupingException(int status, string code, string message, int? currentRevision = null) : Exception(message)
    {
        public int Status { get; } = status;
        public string Code { get; } = code;
        public int? CurrentRevision { get; } = currentRevision;
    }

    public static string NormalizePath(string? path) => (path ?? "").Trim().Replace('\\', '/').TrimStart('/');

    public async Task<StoryGroupingDocument> GetAsync(string projectKey, string specificationPath, CancellationToken ct)
    {
        Validate(projectKey, specificationPath);
        var path = NormalizePath(specificationPath);
        var row = await db.StoryGroupings.AsNoTracking().FirstOrDefaultAsync(r => r.ProjectKey == projectKey && r.SpecificationPath == path, ct);
        return row is null ? new StoryGroupingDocument { ProjectKey = projectKey, SpecificationPath = path } : Read(row);
    }

    public async Task<StoryGroupingDocument> ApplyAsync(string projectKey, string specificationPath, StoryGroupingChangeRequest request, string actor, CancellationToken ct)
    {
        Validate(projectKey, specificationPath);
        if (request.Operations is not { Count: > 0 } ops) throw new GroupingException(400, "no-operations", "Ingen endringer å lagre.");
        if (ops.Count > 50) throw new GroupingException(400, "too-many-operations", "For mange endringer i én lagring (høyst 50).");
        var path = NormalizePath(specificationPath);
        if (request.RequirementCatalog is { } catalog && !string.Equals(catalog.ProjectKey?.Trim(), projectKey, StringComparison.Ordinal))
            throw new GroupingException(400, "requirement-other-project", "Kravene hører til et annet prosjekt.");
        // Only requirements of this project's specification may be linked: never a fictional id, never another service's FR-001.
        var known = (request.RequirementCatalog?.Requirements ?? [])
            .Where(r => string.Equals(NormalizePath(r.SpecificationPath), path, StringComparison.OrdinalIgnoreCase))
            .Select(r => BirkNext.Sdd.RequirementReferenceParser.Normalize(r.RequirementId)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var row = await db.StoryGroupings.FirstOrDefaultAsync(r => r.ProjectKey == projectKey && r.SpecificationPath == path, ct);
        var current = row is null ? new StoryGroupingDocument { ProjectKey = projectKey, SpecificationPath = path } : Read(row);
        // A retry of a change already stored (the answer was lost: timeout, network): answered with the stored document, never applied twice.
        if (request.RequestId is { Length: > 0 } requestId && current.AppliedRequests.Contains(requestId, StringComparer.Ordinal)) return current;
        if (request.ExpectedRevision != current.Revision)
            throw new GroupingException(409, "stale-revision", "Grupperingen er endret av noen andre siden du åpnet den. Last inn på nytt; ingenting ble lagret.", current.Revision);
        StoryGroupingDocument next;
        try { next = StoryGroupingEngine.Apply(current, ops, actor, clock.GetUtcNow(), known.Contains); }
        catch (StoryGroupingEngine.GroupingRefusal refusal) { throw new GroupingException(400, refusal.Code, refusal.Message, current.Revision); }

        if (request.RequestId is { Length: > 0 and <= 100 } applied)
            next = next with { AppliedRequests = [.. next.AppliedRequests.Append(applied).TakeLast(MaxRememberedRequests)] };
        var json = JsonSerializer.Serialize(next, Json);
        if (row is null)
        {
            row = new StoryGroupingRecord { Id = Guid.NewGuid(), ProjectKey = projectKey, SpecificationPath = path };
            db.StoryGroupings.Add(row);
        }
        row.Revision = next.Revision; row.ContentJson = json; row.UpdatedBy = actor; row.UpdatedAt = clock.GetUtcNow();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) // concurrency token or the unique (project, specification) row: someone else saved first
        {
            logger.LogInformation(e, "Story grouping save refused as stale for {Project}", projectKey);
            throw new GroupingException(409, "stale-revision", "Grupperingen ble lagret av noen andre samtidig. Last inn på nytt; ingenting ble lagret.");
        }
        return next;
    }

    private static StoryGroupingDocument Read(StoryGroupingRecord row) =>
        (JsonSerializer.Deserialize<StoryGroupingDocument>(row.ContentJson, Json) ?? new StoryGroupingDocument()) with
        { ProjectKey = row.ProjectKey, SpecificationPath = row.SpecificationPath, Revision = row.Revision };

    private static void Validate(string projectKey, string specificationPath)
    {
        if (string.IsNullOrWhiteSpace(projectKey) || projectKey.Length > 200) throw new GroupingException(400, "project", "Mangler prosjekt.");
        if (string.IsNullOrWhiteSpace(specificationPath) || specificationPath.Length > 500 || specificationPath.Contains("..", StringComparison.Ordinal))
            throw new GroupingException(400, "specification", "Mangler eller ugyldig spesifikasjon.");
    }
}
