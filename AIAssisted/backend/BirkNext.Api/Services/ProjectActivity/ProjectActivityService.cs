using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.ProjectActivity;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ProjectActivity;

/// <summary>A request the store refuses (no project context, no title or idempotency key, a route that leaves the app).</summary>
public sealed class ProjectActivityRequestException(string message) : Exception(message);

/// <summary>
/// Persistent project activity: records meaningful events of one logical project/application and lists the newest. Insert-only and
/// idempotent per (project context, idempotency key), so a producer's retry — or two producers that report the same completion — is one
/// event. The server sets the time. Every text field is bounded and redacted: a value that looks like a credential, token, cookie or
/// authorization header is never stored.
/// </summary>
public sealed partial class ProjectActivityService(AppDbContext db, TimeProvider time)
{
    public const int MaxTake = 50;

    public async Task<ProjectActivityItem> RecordAsync(ProjectActivityRecordRequest request, CancellationToken ct)
    {
        var context = Bounded(request.ProjectContextId, 128);
        var key = Bounded(request.IdempotencyKey, 200);
        var title = Clean(request.Title, 300);
        if (context is null) throw new ProjectActivityRequestException("A project context id is required.");
        if (key is null) throw new ProjectActivityRequestException("An idempotency key is required.");
        if (title is null) throw new ProjectActivityRequestException("A title is required.");
        var route = Bounded(request.Route, 500);
        if (route is not null && (route.Contains("://", StringComparison.Ordinal) || route.StartsWith("//", StringComparison.Ordinal)))
            throw new ProjectActivityRequestException("The route must be an application route.");

        if (await FindAsync(context, key, ct) is { } existing) return Item(existing);
        var record = new ProjectActivityRecord
        {
            Id = Guid.NewGuid(), ProjectContextId = context, ProjectName = Clean(request.ProjectName, 200),
            ActivityType = request.Type.ToString(), Status = request.Status.ToString(), SourceModule = Clean(request.SourceModule, 100) ?? "",
            Title = title, Summary = Clean(request.Summary, 1000), Outcome = Clean(request.Outcome, 200),
            RelatedEntityType = Bounded(request.RelatedEntityType, 64), RelatedEntityId = Bounded(request.RelatedEntityId, 200),
            PackageId = Bounded(request.PackageId, 128), PackageName = Clean(request.PackageName, 300),
            EnvironmentId = Bounded(request.EnvironmentId, 200), EnvironmentName = Clean(request.EnvironmentName, 200),
            Route = route, IdempotencyKey = key, OccurredAt = time.GetUtcNow(),
        };
        db.ProjectActivity.Add(record);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // The same event was recorded concurrently (unique context + key): that one is the event.
            if (await FindAsync(context, key, ct, detach: record) is { } raced) return Item(raced);
            throw;
        }
        return Item(record);
    }

    /// <summary>The newest events of one project context (never another's), newest first, bounded.</summary>
    public async Task<IReadOnlyList<ProjectActivityItem>> ListAsync(string projectContextId, int take, CancellationToken ct)
    {
        if (Bounded(projectContextId, 128) is not { } context) return [];
        var rows = await db.ProjectActivity.AsNoTracking().Where(r => r.ProjectContextId == context)
            .OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Id).Take(Math.Clamp(take, 1, MaxTake)).ToListAsync(ct);
        return [.. rows.Select(Item)];
    }

    private async Task<ProjectActivityRecord?> FindAsync(string context, string key, CancellationToken ct, ProjectActivityRecord? detach = null)
    {
        if (detach is not null) db.Entry(detach).State = EntityState.Detached;
        return await db.ProjectActivity.AsNoTracking().FirstOrDefaultAsync(r => r.ProjectContextId == context && r.IdempotencyKey == key, ct);
    }

    private static ProjectActivityItem Item(ProjectActivityRecord r)
    {
        var type = Enum.TryParse<ProjectActivityType>(r.ActivityType, out var t) ? t : ProjectActivityType.ImportCompleted;
        return new()
        {
            Id = r.Id, ProjectContextId = r.ProjectContextId, ProjectName = r.ProjectName, Type = type, Category = ProjectActivityCategories.Of(type),
            Status = Enum.TryParse<ProjectActivityStatus>(r.Status, out var s) ? s : ProjectActivityStatus.Completed,
            SourceModule = r.SourceModule, Title = r.Title, Summary = r.Summary, Outcome = r.Outcome, RelatedEntityType = r.RelatedEntityType,
            RelatedEntityId = r.RelatedEntityId, PackageId = r.PackageId, PackageName = r.PackageName, EnvironmentId = r.EnvironmentId,
            EnvironmentName = r.EnvironmentName, Route = r.Route, OccurredAt = r.OccurredAt,
        };
    }

    private static string? Bounded(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    /// <summary>Bounded, single-line, and never a secret: a value carrying a credential, token, cookie or authorization header is dropped
    /// (the event keeps its other facts).</summary>
    internal static string? Clean(string? value, int max)
    {
        if (Bounded(value, max * 2) is not { } text) return null;
        text = Whitespace().Replace(text, " ");
        if (Secret().IsMatch(text)) return null;
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?ix)
        \b(bearer|basic)\s+[a-z0-9\-._~+/=]{8,}
        | \b(authorization|cookie|set-cookie|password|passwd|pwd|secret|client_secret|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|token|pat|sig)\s*[:=]
        | \beyJ[a-z0-9_\-]{10,}\.[a-z0-9_\-]{10,}
        | \b[a-z0-9]{52}\b")]
    private static partial Regex Secret();
}
