using System.Text.Json;
using BirkNext.Api.Data.IntegrationJourneys;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>The outcome of a declared-journey write: the journey on success, otherwise the HTTP-like status (400, 404, 409) and why.</summary>
public sealed record DeclaredJourneyWrite(DeclaredIntegrationJourney? Journey, int Status, string? Error, string? ExistingId = null)
{
    public static DeclaredJourneyWrite Ok(DeclaredIntegrationJourney journey) => new(journey, 200, null);
    public static DeclaredJourneyWrite Fail(int status, string error, string? existingId = null) => new(null, status, error, existingId);
}

/// <summary>
/// Integration Journeys a reviewer declared for a project (name + ordered component chain), stored in the database (schema integration_journeys)
/// so they are durable and shared by every BirkNext process on that database. Project-scoped: every read names the project, ids carry it
/// ("project.{projectKey}:{slug}") and another project's journey is never returned or changed. Names only — no URL, value or credential.
/// A declared journey is design metadata: it never runs, and it never confirms anything by itself (Test Coverage only proposes candidates from it).
/// <para>Revisions are append-only: declaring writes revision 1, every edit appends the next revision and the latest is current; a revision row is
/// never updated or deleted. There is no draft/approved state. Removing a journey retires it (it is no longer listed, proposed or a current scope
/// member) and keeps its history; declaring the same name again in that project reactivates the same id with a new revision.</para>
/// </summary>
public sealed class DeclaredIntegrationJourneyStore(IntegrationJourneysDbContext db)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Every current (not retired) declared journey of every project (the Scope Registry catalog lists them with their project).</summary>
    public async Task<IReadOnlyList<DeclaredIntegrationJourney>> AllAsync(CancellationToken ct = default) =>
        [.. (await CurrentAsync(db.Journeys.AsNoTracking().Where(j => j.RetiredAt == null), ct))
            .OrderBy(j => j.ProjectKey, StringComparer.Ordinal).ThenBy(j => j.DisplayName, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The current declared journeys of one project; none for a blank key.</summary>
    public async Task<IReadOnlyList<DeclaredIntegrationJourney>> ForProjectAsync(string? projectKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectKey)) return [];
        var key = projectKey.Trim();
        return [.. (await CurrentAsync(db.Journeys.AsNoTracking().Where(j => j.ProjectKey == key && j.RetiredAt == null), ct))
            .OrderBy(j => j.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A declared journey by its public id ("project.{projectKey}:{slug}"), retired ones included (RetiredAt set); null when unknown.</summary>
    public async Task<DeclaredIntegrationJourney?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        if (!TryParseId(id, out var projectKey, out var slug)) return null;
        return (await CurrentAsync(db.Journeys.AsNoTracking().Where(j => j.ProjectKey == projectKey && j.Slug == slug), ct)).FirstOrDefault();
    }

    /// <summary>The journey with every revision, newest first (also when retired); null when this project has no such journey.</summary>
    public async Task<DeclaredIntegrationJourneyHistory?> HistoryAsync(string projectKey, string journeyId, CancellationToken ct = default)
    {
        var (key, slug) = (projectKey.Trim(), journeyId.Trim());
        var record = await db.Journeys.AsNoTracking().FirstOrDefaultAsync(j => j.ProjectKey == key && j.Slug == slug, ct);
        if (record is null) return null;
        var revisions = await db.Revisions.AsNoTracking().Where(r => r.JourneyId == record.Id).ToListAsync(ct);
        var current = revisions.FirstOrDefault(r => r.Revision == record.CurrentRevision);
        if (current is null) return null;
        return new DeclaredIntegrationJourneyHistory
        {
            Journey = ToContract(record, current),
            RetiredBy = record.RetiredBy,
            Revisions = [.. revisions.OrderByDescending(r => r.Revision).Select(ToRevision)],
        };
    }

    /// <summary>Declares a journey (revision 1). Refused when invalid, or when the project already has a current journey with the same id (the
    /// name's slug): 409 with the existing id, nothing overwritten. A retired journey with that id is reactivated with a new revision.</summary>
    public async Task<DeclaredJourneyWrite> DeclareAsync(DeclaredIntegrationJourneyRequest request, DateTimeOffset now, CancellationToken ct = default)
    {
        if (DeclaredIntegrationJourneyRules.Validate(request) is { } invalid) return DeclaredJourneyWrite.Fail(400, invalid);
        var key = request.ProjectKey.Trim();
        var slug = DeclaredIntegrationJourneyRules.Slug(request.DisplayName);
        now = now.ToUniversalTime();
        var existing = await db.Journeys.FirstOrDefaultAsync(j => j.ProjectKey == key && j.Slug == slug, ct);
        if (existing is { RetiredAt: null })
        {
            var id = $"{DeclaredIntegrationJourneyRules.PackId(key)}:{slug}";
            return DeclaredJourneyWrite.Fail(409, $"This project already declares a journey with the id {id}. Use another name, or edit that journey (a new revision).", id);
        }
        DeclaredJourneyRecord record;
        DeclaredJourneyRevisionRecord revision;
        if (existing is not null)
        {
            existing.CurrentRevision++;
            existing.DisplayName = request.DisplayName.Trim();
            existing.UpdatedAt = now;
            existing.RetiredAt = null;
            existing.RetiredBy = null;
            record = existing;
            revision = Revision(existing.Id, existing.CurrentRevision, request, now, DeclaredIntegrationJourneyRules.OriginReactivated);
        }
        else
        {
            record = new DeclaredJourneyRecord
            {
                Id = Guid.NewGuid(), ProjectKey = key, Slug = slug, DisplayName = request.DisplayName.Trim(), CurrentRevision = 1,
                CreatedAt = now, CreatedBy = request.DeclaredBy.Trim(), UpdatedAt = now,
            };
            db.Journeys.Add(record);
            revision = Revision(record.Id, 1, request, now, DeclaredIntegrationJourneyRules.OriginDeclared);
        }
        db.Revisions.Add(revision);
        return await SaveAsync(record, revision, ct);
    }

    /// <summary>Edits a current journey by appending the next revision (earlier revisions are never changed). The id never changes, also when
    /// the name does. Refused: invalid (400), unknown in this project (404), retired, or changed since <c>ExpectedRevision</c> (409), and an
    /// edit that changes nothing (400 — no empty revision is written).</summary>
    public async Task<DeclaredJourneyWrite> ReviseAsync(string projectKey, string journeyId, DeclaredIntegrationJourneyRequest request, DateTimeOffset now, CancellationToken ct = default)
    {
        var (key, slug) = (projectKey.Trim(), journeyId.Trim());
        if (!string.Equals(request.ProjectKey.Trim(), key, StringComparison.Ordinal))
            return DeclaredJourneyWrite.Fail(400, "The journey belongs to another project than the one named in the request.");
        if (DeclaredIntegrationJourneyRules.Validate(request) is { } invalid) return DeclaredJourneyWrite.Fail(400, invalid);
        var record = await db.Journeys.FirstOrDefaultAsync(j => j.ProjectKey == key && j.Slug == slug, ct);
        if (record is null) return DeclaredJourneyWrite.Fail(404, "This project declares no journey with that id.");
        if (record.RetiredAt is not null) return DeclaredJourneyWrite.Fail(409, "This journey was removed. Declare it again to reactivate it (its history is kept).");
        if (request.ExpectedRevision is { } expected && expected != record.CurrentRevision)
            return DeclaredJourneyWrite.Fail(409, $"The journey changed since you opened it (now revision {record.CurrentRevision}). Reload it and edit again.");
        var current = await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.JourneyId == record.Id && r.Revision == record.CurrentRevision, ct);
        if (current is not null && DeclaredIntegrationJourneyRules.SameContent(request.DisplayName, request.Description, request.Components,
                current.DisplayName, current.Description, Components(current)))
            return DeclaredJourneyWrite.Fail(400, "Nothing changed: no new revision was written.");
        now = now.ToUniversalTime();
        record.CurrentRevision++;
        record.DisplayName = request.DisplayName.Trim();
        record.UpdatedAt = now;
        var revision = Revision(record.Id, record.CurrentRevision, request, now, DeclaredIntegrationJourneyRules.OriginEdited);
        db.Revisions.Add(revision);
        return await SaveAsync(record, revision, ct);
    }

    /// <summary>Retires (removes) one current journey of a project; its revisions are kept and readable. Recorded association decisions and scope
    /// members that name it are kept (they then read as missing / not current). False when there is no current journey with that id.</summary>
    public async Task<bool> RetireAsync(string projectKey, string journeyId, string? retiredBy, DateTimeOffset now, CancellationToken ct = default)
    {
        var (key, slug) = (projectKey.Trim(), journeyId.Trim());
        var record = await db.Journeys.FirstOrDefaultAsync(j => j.ProjectKey == key && j.Slug == slug && j.RetiredAt == null, ct);
        if (record is null) return false;
        record.RetiredAt = now.ToUniversalTime();
        record.RetiredBy = string.IsNullOrWhiteSpace(retiredBy) ? null : retiredBy.Trim()[..Math.Min(retiredBy.Trim().Length, DeclaredIntegrationJourneyRules.MaxDeclaredBy)];
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return false; }
    }

    /// <summary>Splits "project.{projectKey}:{slug}" (the project key may itself contain ':'; the slug never does).</summary>
    public static bool TryParseId(string? id, out string projectKey, out string slug)
    {
        (projectKey, slug) = ("", "");
        if (id is null || !DeclaredIntegrationJourneyRules.IsDeclaredPack(id)) return false;
        var colon = id.LastIndexOf(':');
        if (colon <= DeclaredIntegrationJourneyRules.PackPrefix.Length || colon == id.Length - 1) return false;
        (projectKey, slug) = (id[DeclaredIntegrationJourneyRules.PackPrefix.Length..colon], id[(colon + 1)..]);
        return true;
    }

    internal static List<string> Components(DeclaredJourneyRevisionRecord r)
    {
        try { return JsonSerializer.Deserialize<List<string>>(r.ComponentsJson, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    internal static DeclaredJourneyRevisionRecord Revision(Guid journeyId, int number, DeclaredIntegrationJourneyRequest request, DateTimeOffset at, string origin) => new()
    {
        JourneyId = journeyId, Revision = number, DisplayName = request.DisplayName.Trim(),
        Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
        ComponentsJson = JsonSerializer.Serialize(DeclaredIntegrationJourneyRules.Chain(request.Components), Json),
        DeclaredBy = request.DeclaredBy.Trim(), CreatedAt = at, Origin = origin,
    };

    private async Task<DeclaredJourneyWrite> SaveAsync(DeclaredJourneyRecord record, DeclaredJourneyRevisionRecord revision, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return DeclaredJourneyWrite.Ok(ToContract(record, revision));
        }
        catch (DbUpdateException)
        {
            // A concurrent declare/edit of the same journey won (unique project+slug, revision key or the current-revision token): nothing is written.
            db.ChangeTracker.Clear();
            return DeclaredJourneyWrite.Fail(409, "The journey was changed at the same time by someone else. Reload it and try again.");
        }
    }

    private async Task<List<DeclaredIntegrationJourney>> CurrentAsync(IQueryable<DeclaredJourneyRecord> journeys, CancellationToken ct)
    {
        var rows = await (from j in journeys
                          join r in db.Revisions.AsNoTracking() on new { JourneyId = j.Id, Revision = j.CurrentRevision } equals new { r.JourneyId, r.Revision }
                          select new { j, r }).ToListAsync(ct);
        return [.. rows.Select(x => ToContract(x.j, x.r))];
    }

    internal static DeclaredIntegrationJourney ToContract(DeclaredJourneyRecord j, DeclaredJourneyRevisionRecord current) => new()
    {
        ProjectKey = j.ProjectKey, JourneyId = j.Slug, DisplayName = current.DisplayName, Description = current.Description, Components = Components(current),
        DeclaredBy = current.DeclaredBy, DeclaredAt = current.CreatedAt, Revision = current.Revision, CreatedBy = j.CreatedBy, CreatedAt = j.CreatedAt, RetiredAt = j.RetiredAt,
    };

    private static DeclaredIntegrationJourneyRevision ToRevision(DeclaredJourneyRevisionRecord r) => new()
    {
        Revision = r.Revision, DisplayName = r.DisplayName, Description = r.Description, Components = Components(r), DeclaredBy = r.DeclaredBy,
        DeclaredAt = r.CreatedAt, Origin = r.Origin,
    };
}
