using System.Text.Json;
using BirkNext.Api.Data.IntegrationJourneys;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>
/// Requirements linked to a project's declared journeys. A link names a requirement of the project's own specification catalog (sent with the
/// request, as for manual test cases — the same trust level, labelled on the link), keeps the journey revision it was made against and an
/// append-only history of who confirmed, rejected or removed it. Nothing is deleted; another project's journey or catalog is refused.
/// </summary>
public sealed class JourneyRequirementLinkStore(IntegrationJourneysDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public sealed record Write(JourneyRequirementLink? Link, int Status, string? Error);

    /// <summary>Every link of the project (all journeys), or of one journey; with history.</summary>
    public async Task<IReadOnlyList<JourneyRequirementLink>> ListAsync(string projectKey, string? journeySlug, CancellationToken ct)
    {
        var key = projectKey.Trim();
        var rows = db.RequirementLinks.AsNoTracking().Where(l => l.ProjectKey == key);
        if (journeySlug is { Length: > 0 } slug)
        {
            var journey = await db.Journeys.AsNoTracking().FirstOrDefaultAsync(j => j.ProjectKey == key && j.Slug == slug.Trim(), ct);
            if (journey is null) return [];
            rows = rows.Where(l => l.JourneyId == journey.Id);
        }
        return [.. (await rows.ToListAsync(ct)).Select(Read).OrderBy(l => l.JourneyId, StringComparer.Ordinal).ThenBy(l => l.RequirementId, StringComparer.Ordinal)];
    }

    public async Task<Write> LinkAsync(string projectKey, string journeySlug, JourneyRequirementLinkRequest request, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var key = projectKey.Trim();
        var journey = await db.Journeys.FirstOrDefaultAsync(j => j.ProjectKey == key && j.Slug == journeySlug.Trim(), ct);
        if (journey is null || journey.RetiredAt is not null) return new(null, 404, "This project declares no current journey with that id.");
        if (request.Catalog is null || !string.Equals(request.Catalog.ProjectKey, key, StringComparison.Ordinal))
            return new(null, 400, "The requirement must come from this project's specification.");
        var (id, path) = (request.RequirementId?.Trim() ?? "", request.SpecificationPath?.Trim() ?? "");
        var option = request.Catalog.Requirements.FirstOrDefault(r => string.Equals(r.RequirementId, id, StringComparison.Ordinal) && string.Equals(r.SpecificationPath, path, StringComparison.Ordinal));
        if (option is null) return new(null, 400, $"{id} is not a requirement of this project's specification.");
        var by = string.IsNullOrWhiteSpace(request.By) ? actor : request.By.Trim();
        var status = request.Proposed ? JourneyRequirementStatus.Proposed : JourneyRequirementStatus.Confirmed;

        var existing = await db.RequirementLinks.FirstOrDefaultAsync(l => l.ProjectKey == key && l.JourneyId == journey.Id && l.RequirementId == id && l.SpecificationPath == path, ct);
        if (existing is not null)
        {
            var link = Read(existing);
            if (link.Status is JourneyRequirementStatus.Confirmed or JourneyRequirementStatus.Proposed)
                return new(null, 409, $"{id} is already linked to this journey.");
            return await MoveAsync(existing, link, JourneyRequirementStatus.Confirmed, "Linked again", by, now, ct);
        }
        var created = new JourneyRequirementLink
        {
            Id = Guid.NewGuid(), ProjectKey = key, JourneyId = journey.Slug, JourneyRevision = journey.CurrentRevision, RequirementId = id, SpecificationPath = path,
            Title = option.Title, Status = status, LinkedBy = by, LinkedAt = now, Version = 1, History = [new(now, by, status, null)],
        };
        db.RequirementLinks.Add(new JourneyRequirementLinkRecord
        {
            Id = created.Id, ProjectKey = key, JourneyId = journey.Id, RequirementId = id, SpecificationPath = path, Status = status.ToString(),
            ContentJson = JsonSerializer.Serialize(created, Json), Version = 1, UpdatedAt = now,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return new(null, 409, $"{id} was linked to this journey at the same time; reload."); }
        return new(created, 200, null);
    }

    public async Task<Write> SetStatusAsync(string projectKey, string journeySlug, Guid linkId, JourneyRequirementStatusRequest request, string actor, DateTimeOffset now, CancellationToken ct)
    {
        var key = projectKey.Trim();
        var row = await db.RequirementLinks.FirstOrDefaultAsync(l => l.Id == linkId && l.ProjectKey == key, ct);
        if (row is null) return new(null, 404, "No such requirement link in this project.");
        var link = Read(row);
        if (!string.Equals(link.JourneyId, journeySlug.Trim(), StringComparison.Ordinal)) return new(null, 404, "No such requirement link on this journey.");
        if (row.Version != request.Version) return new(null, 409, "The link was changed by someone else. Reload; nothing was saved.");
        if (!JourneyRequirementRules.CanMove(link.Status, request.Status)) return new(null, 400, $"A {link.Status.ToString().ToLowerInvariant()} link cannot become {request.Status.ToString().ToLowerInvariant()}.");
        return await MoveAsync(row, link, request.Status, request.Note, string.IsNullOrWhiteSpace(request.By) ? actor : request.By.Trim(), now, ct);
    }

    private async Task<Write> MoveAsync(JourneyRequirementLinkRecord row, JourneyRequirementLink link, JourneyRequirementStatus to, string? note, string by, DateTimeOffset now, CancellationToken ct)
    {
        var updated = link with { Status = to, Version = row.Version + 1, History = [.. link.History, new(now, by, to, string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 1000)])] };
        row.Status = to.ToString(); row.ContentJson = JsonSerializer.Serialize(updated, Json); row.Version = updated.Version; row.UpdatedAt = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return new(null, 409, "The link was changed by someone else. Reload; nothing was saved."); }
        return new(updated, 200, null);
    }

    private static JourneyRequirementLink Read(JourneyRequirementLinkRecord r) =>
        JsonSerializer.Deserialize<JourneyRequirementLink>(r.ContentJson, Json)! with { Version = r.Version };
}
