using System.Text.Json;
using BirkNext.Api.Data.ScopeRegistry;
using BirkNext.Scopes;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ScopeRegistry;

/// <summary>Outcome of a scope write: the updated scope, or why nothing changed (Conflict = based on an older version).</summary>
public sealed record ScopeWriteResult(ScopeDetail? Scope, string? Error, bool Conflict = false);

/// <summary>
/// The shared Scope Registry: project-scoped scopes with immutable revisions of typed member references. An approved revision never changes
/// (an edit starts a new draft revision); every write names the version it was based on, so a stale edit is refused, not merged.
/// Approval pins membership — it does not mean tests passed or that runtime execution is authorized.
/// </summary>
public sealed class ScopeRegistryService(ScopeRegistryDbContext db, TimeProvider clock, IScopeMemberResolver? resolver = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ScopeSummary>> ListAsync(string projectKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectKey)) return [];
        var scopes = await db.Scopes.AsNoTracking().Where(s => s.ProjectKey == projectKey).OrderBy(s => s.Name).ToListAsync(ct);
        var ids = scopes.Select(s => s.Id).ToList();
        var revisions = await db.Revisions.AsNoTracking().Where(r => ids.Contains(r.ScopeId)).ToListAsync(ct);
        return [.. scopes.Select(s => Summary(s, revisions.Where(r => r.ScopeId == s.Id && r.Number == s.CurrentRevision).Select(View).FirstOrDefault()))];
    }

    public async Task<ScopeDetail?> GetAsync(string projectKey, Guid scopeId, CancellationToken ct = default)
    {
        var scope = await db.Scopes.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scopeId && s.ProjectKey == projectKey, ct);
        return scope is null ? null : await DetailAsync(scope, ct);
    }

    public async Task<ScopeWriteResult> CreateAsync(CreateScopeRequest request, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectKey)) return new(null, "A project is required: scopes belong to one project.");
        if (string.IsNullOrWhiteSpace(request.Name)) return new(null, "A scope needs a name.");
        var name = request.Name.Trim()[..Math.Min(200, request.Name.Trim().Length)];
        if (await db.Scopes.AnyAsync(s => s.ProjectKey == request.ProjectKey && s.Name == name, ct)) return new(null, $"This project already has a scope named \"{name}\".");
        var (checkedMembers, memberError) = await CheckNewMembersAsync(request.Members, [], request.CatalogScope, ct);
        if (memberError is not null) return new(null, memberError);
        request = request with { Members = checkedMembers };
        var now = clock.GetUtcNow();
        var scope = new ScopeRecord
        {
            Id = Guid.NewGuid(), ProjectKey = request.ProjectKey, Name = name, Description = Trim(request.Description, 2000), Purpose = request.Purpose.ToString(),
            Owner = Trim(request.Owner, 200), State = nameof(ScopeState.Draft), CurrentRevision = 1, Version = 1, CreatedAt = now, UpdatedAt = now,
        };
        db.Scopes.Add(scope);
        db.Revisions.Add(NewRevision(scope.Id, 1, request.Members, "Created.", actor, now));
        await db.SaveChangesAsync(ct);
        return new(await DetailAsync(scope, ct), null);
    }

    /// <summary>Edits the newest revision while it is a draft; when it is approved (or superseded), a new draft revision is created instead.</summary>
    public async Task<ScopeWriteResult> SaveDraftAsync(string projectKey, Guid scopeId, SaveScopeDraftRequest request, string actor, CancellationToken ct = default)
    {
        var scope = await db.Scopes.FirstOrDefaultAsync(s => s.Id == scopeId && s.ProjectKey == projectKey, ct);
        if (scope is null) return new(null, "Scope not found in this project.");
        if (scope.State == nameof(ScopeState.Archived)) return new(null, "An archived scope is read-only.");
        if (scope.Version != request.ExpectedVersion) return new(null, "The scope was changed by someone else since you opened it. Reload it and apply your change again.", Conflict: true);
        if (string.IsNullOrWhiteSpace(request.Name)) return new(null, "A scope needs a name.");
        var name = request.Name.Trim()[..Math.Min(200, request.Name.Trim().Length)];
        if (name != scope.Name && await db.Scopes.AnyAsync(s => s.ProjectKey == projectKey && s.Name == name && s.Id != scopeId, ct))
            return new(null, $"This project already has a scope named \"{name}\".");
        var now = clock.GetUtcNow();
        var current = await db.Revisions.FirstAsync(r => r.ScopeId == scopeId && r.Number == scope.CurrentRevision, ct);
        var previous = JsonSerializer.Deserialize<List<ScopeMember>>(current.MembersJson, Json) ?? [];
        var (checkedMembers, memberError) = await CheckNewMembersAsync(request.Members, previous, request.CatalogScope, ct);
        if (memberError is not null) return new(null, memberError);
        var members = ScopeRegistryRules.Normalize(checkedMembers);
        if (current.State is nameof(ScopeRevisionState.Draft) or nameof(ScopeRevisionState.InReview))
        {
            current.MembersJson = JsonSerializer.Serialize(members, Json);
            current.ContentFingerprint = ScopeRegistryRules.Fingerprint(members);
            current.State = nameof(ScopeRevisionState.Draft);
            current.ChangeSummary = Trim(request.ChangeSummary, 2000) ?? current.ChangeSummary;
        }
        else
        {
            // The approved revision is immutable: the edit becomes the next draft revision.
            scope.CurrentRevision++;
            db.Revisions.Add(NewRevision(scope.Id, scope.CurrentRevision, members, Trim(request.ChangeSummary, 2000) ?? "Edited after approval.", actor, now));
        }
        scope.Name = name; scope.Description = Trim(request.Description, 2000); scope.Purpose = request.Purpose.ToString(); scope.Owner = Trim(request.Owner, 200);
        scope.State = scope.ApprovedRevision is null ? nameof(ScopeState.Draft) : nameof(ScopeState.Approved);
        scope.Version++; scope.UpdatedAt = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return new(null, "The scope was changed by someone else since you opened it. Reload it and apply your change again.", Conflict: true); }
        return new(await DetailAsync(scope, ct), null);
    }

    /// <summary>Approves one draft revision (the newest); the previously approved revision becomes Superseded but stays readable.</summary>
    public async Task<ScopeWriteResult> ApproveAsync(string projectKey, Guid scopeId, ScopeDecisionRequest request, string actor, CancellationToken ct = default)
    {
        var scope = await db.Scopes.FirstOrDefaultAsync(s => s.Id == scopeId && s.ProjectKey == projectKey, ct);
        if (scope is null) return new(null, "Scope not found in this project.");
        if (scope.Version != request.ExpectedVersion) return new(null, "The scope was changed since you opened it. Reload it before approving.", Conflict: true);
        if (request.Revision != scope.CurrentRevision) return new(null, "Only the newest revision can be approved.");
        var revision = await db.Revisions.FirstAsync(r => r.ScopeId == scopeId && r.Number == request.Revision, ct);
        if (revision.State == nameof(ScopeRevisionState.Approved)) return new(null, "This revision is already approved.");
        if (scope.State == nameof(ScopeState.Archived)) return new(null, "An archived scope cannot be approved.");
        if (!(JsonSerializer.Deserialize<List<ScopeMember>>(revision.MembersJson, Json) ?? []).Any(m => m.Inclusion == ScopeMemberInclusion.Confirmed))
            return new(null, "An empty scope cannot be approved: add at least one member first. (An empty draft can be saved.)");
        var now = clock.GetUtcNow();
        if (scope.ApprovedRevision is { } previous)
        {
            var old = await db.Revisions.FirstAsync(r => r.ScopeId == scopeId && r.Number == previous, ct);
            old.State = nameof(ScopeRevisionState.Superseded);
        }
        revision.State = nameof(ScopeRevisionState.Approved);
        revision.ApprovedAt = now; revision.ApprovedBy = actor; revision.ApprovalRationale = Trim(request.Rationale, 2000);
        scope.ApprovedRevision = revision.Number; scope.State = nameof(ScopeState.Approved); scope.Version++; scope.UpdatedAt = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return new(null, "The scope was changed since you opened it. Reload it before approving.", Conflict: true); }
        return new(await DetailAsync(scope, ct), null);
    }

    /// <summary>The confirmed members of one kind in each scope's newest revision: one read for the whole project (no request per scope).</summary>
    public async Task<IReadOnlyList<ScopeMembershipSummary>> MembershipAsync(string projectKey, ScopeMemberKind kind, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(projectKey)) return [];
        var scopes = await db.Scopes.AsNoTracking().Where(s => s.ProjectKey == projectKey).ToListAsync(ct);
        var ids = scopes.Select(s => s.Id).ToList();
        var revisions = await db.Revisions.AsNoTracking().Where(r => ids.Contains(r.ScopeId)).ToListAsync(ct);
        return [.. scopes.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(s =>
        {
            var current = revisions.FirstOrDefault(r => r.ScopeId == s.Id && r.Number == s.CurrentRevision);
            var members = current is null ? [] : JsonSerializer.Deserialize<List<ScopeMember>>(current.MembersJson, Json) ?? [];
            return new ScopeMembershipSummary(s.Id, s.Name, Enum.TryParse<ScopeState>(s.State, out var st) ? st : ScopeState.Draft, s.CurrentRevision,
                [.. members.Where(m => m.Kind == kind && m.Inclusion == ScopeMemberInclusion.Confirmed).Select(m => m.TargetId)]);
        })];
    }

    /// <summary>Marks the newest draft revision as ready for review (no approval; saving it again returns it to draft).</summary>
    public async Task<ScopeWriteResult> SubmitAsync(string projectKey, Guid scopeId, ScopeDecisionRequest request, CancellationToken ct = default)
    {
        var scope = await db.Scopes.FirstOrDefaultAsync(s => s.Id == scopeId && s.ProjectKey == projectKey, ct);
        if (scope is null) return new(null, "Scope not found in this project.");
        if (scope.Version != request.ExpectedVersion) return new(null, "The scope was changed since you opened it. Reload it first.", Conflict: true);
        if (scope.State == nameof(ScopeState.Archived)) return new(null, "An archived scope is read-only.");
        if (request.Revision != scope.CurrentRevision) return new(null, "Only the newest revision can be submitted for review.");
        var revision = await db.Revisions.FirstAsync(r => r.ScopeId == scopeId && r.Number == request.Revision, ct);
        if (revision.State != nameof(ScopeRevisionState.Draft)) return new(null, $"Revision {revision.Number} is {revision.State}, not a draft.");
        revision.State = nameof(ScopeRevisionState.InReview);
        if (scope.ApprovedRevision is null) scope.State = nameof(ScopeState.InReview);
        scope.Version++; scope.UpdatedAt = clock.GetUtcNow();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return new(null, "The scope was changed since you opened it. Reload it first.", Conflict: true); }
        return new(await DetailAsync(scope, ct), null);
    }

    /// <summary>The current catalog state of every member of a revision (stale members are shown for review, never replaced).</summary>
    public async Task<IReadOnlyList<ScopeMemberStatus>?> CheckAsync(string projectKey, Guid scopeId, int? revision, string? catalogScope, CancellationToken ct = default)
    {
        var detail = await GetAsync(projectKey, scopeId, ct);
        var chosen = detail?.Revisions.FirstOrDefault(r => r.Number == (revision ?? detail.Scope.CurrentRevision));
        if (chosen is null) return null;
        if (resolver is null) return [.. chosen.Members.Select(m => new ScopeMemberStatus(m.Key, ScopeMemberCheck.NotCheckable, "No catalog is available in this host."))];
        return await resolver.CheckAsync(chosen.Members, catalogScope, ct);
    }

    /// <summary>
    /// Every member that is new compared with <paramref name="previous"/> must exist in its catalog now (right kind, this project's catalog
    /// context). Members carried over from an earlier revision are kept as they are, even when their catalog no longer lists them (that is
    /// shown as stale, never silently dropped). New members get the catalog context they were checked in.
    /// </summary>
    private async Task<(List<ScopeMember> Members, string? Error)> CheckNewMembersAsync(IReadOnlyList<ScopeMember> members, IReadOnlyList<ScopeMember> previous,
        string? catalogScope, CancellationToken ct)
    {
        if (resolver is null) return ([.. members], null);
        var known = previous.ToDictionary(m => m.Key, StringComparer.Ordinal);
        var fresh = members.Select(m => m with { TargetId = (m.TargetId ?? "").Trim() })
            .Where(m => m.TargetId.Length > 0 && !known.ContainsKey(m.Key)).GroupBy(m => m.Key).Select(g => g.Last()).ToList();
        if (fresh.Count == 0) return ([.. members.Select(m => known.TryGetValue(m.Key, out var k) && m.CatalogScope is null ? m with { CatalogScope = k.CatalogScope } : m)], null);
        var statuses = (await resolver.CheckAsync(fresh, catalogScope, ct)).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var refused = fresh.Where(m => statuses.GetValueOrDefault(m.Key) is not { Check: ScopeMemberCheck.Current }).ToList();
        if (refused.Count > 0)
            return ([], "These members cannot be added because their catalog does not confirm them: "
                + string.Join("; ", refused.Take(5).Select(m => $"{(string.IsNullOrWhiteSpace(m.Label) ? m.TargetId : m.Label)} ({statuses.GetValueOrDefault(m.Key)?.Detail ?? "not checked"})"))
                + (refused.Count > 5 ? $"; and {refused.Count - 5} more" : "") + ". Nothing was saved.");
        var freshKeys = fresh.ToDictionary(m => m.Key, StringComparer.Ordinal);
        return ([.. members.Select(m => m with { TargetId = m.TargetId.Trim() }).Select(m => freshKeys.ContainsKey(m.Key)
            ? m with { CatalogScope = m.CatalogScope ?? (ScopeMemberCatalogService.IsCatalogScoped(m.Kind) ? catalogScope : null) }
            : known.TryGetValue(m.Key, out var k) && m.CatalogScope is null ? m with { CatalogScope = k.CatalogScope } : m)], null);
    }

    public async Task<ScopeWriteResult> ArchiveAsync(string projectKey, Guid scopeId, ScopeDecisionRequest request, CancellationToken ct = default)
    {
        var scope = await db.Scopes.FirstOrDefaultAsync(s => s.Id == scopeId && s.ProjectKey == projectKey, ct);
        if (scope is null) return new(null, "Scope not found in this project.");
        if (scope.Version != request.ExpectedVersion) return new(null, "The scope was changed since you opened it.", Conflict: true);
        scope.State = nameof(ScopeState.Archived); scope.Version++; scope.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new(await DetailAsync(scope, ct), null);
    }

    public async Task<ScopeComparison?> CompareAsync(string projectKey, Guid scopeId, int from, int to, CancellationToken ct = default)
    {
        var detail = await GetAsync(projectKey, scopeId, ct);
        var a = detail?.Revisions.FirstOrDefault(r => r.Number == from);
        var b = detail?.Revisions.FirstOrDefault(r => r.Number == to);
        return a is null || b is null ? null : ScopeRegistryRules.Compare(scopeId, a, b);
    }

    /// <summary>
    /// The confirmed members of several scopes of one project: the approved revision of each (or the newest when <paramref name="includeDrafts"/>).
    /// A scope of another project is never read; a scope without an approved revision is reported, not silently widened to its draft.
    /// </summary>
    public async Task<ScopeSelection> SelectAsync(string projectKey, IReadOnlyList<Guid> scopeIds, bool includeDrafts, CancellationToken ct = default)
    {
        var notices = new List<string>();
        var chosen = new List<(ScopeSummary, ScopeRevisionView)>();
        foreach (var id in scopeIds.Distinct())
        {
            var detail = await GetAsync(projectKey, id, ct);
            if (detail is null) { notices.Add($"Scope {id} is not part of this project."); continue; }
            var number = includeDrafts ? detail.Scope.CurrentRevision : detail.Scope.ApprovedRevision;
            if (number is null) { notices.Add($"\"{detail.Scope.Name}\" has no approved revision yet."); continue; }
            chosen.Add((detail.Scope, detail.Revisions.First(r => r.Number == number)));
        }
        return ScopeRegistryRules.Select(projectKey, chosen, notices);
    }

    private ScopeRevisionRecord NewRevision(Guid scopeId, int number, IEnumerable<ScopeMember> members, string? summary, string actor, DateTimeOffset now)
    {
        var normalized = ScopeRegistryRules.Normalize(members);
        return new ScopeRevisionRecord
        {
            Id = Guid.NewGuid(), ScopeId = scopeId, Number = number, State = nameof(ScopeRevisionState.Draft), MembersJson = JsonSerializer.Serialize(normalized, Json),
            ContentFingerprint = ScopeRegistryRules.Fingerprint(normalized), ChangeSummary = summary, CreatedAt = now, CreatedBy = string.IsNullOrWhiteSpace(actor) ? "not attributed" : actor,
        };
    }

    private async Task<ScopeDetail> DetailAsync(ScopeRecord scope, CancellationToken ct)
    {
        var revisions = (await db.Revisions.AsNoTracking().Where(r => r.ScopeId == scope.Id).OrderByDescending(r => r.Number).ToListAsync(ct)).Select(View).ToList();
        return new ScopeDetail { Scope = Summary(scope, revisions.FirstOrDefault(r => r.Number == scope.CurrentRevision)), Revisions = revisions };
    }

    private static ScopeRevisionView View(ScopeRevisionRecord r) => new()
    {
        Id = r.Id, Number = r.Number, State = Enum.TryParse<ScopeRevisionState>(r.State, out var s) ? s : ScopeRevisionState.Draft,
        Members = JsonSerializer.Deserialize<List<ScopeMember>>(r.MembersJson, Json) ?? [], ChangeSummary = r.ChangeSummary, CreatedAt = r.CreatedAt, CreatedBy = r.CreatedBy,
        ApprovedAt = r.ApprovedAt, ApprovedBy = r.ApprovedBy, ApprovalRationale = r.ApprovalRationale, ContentFingerprint = r.ContentFingerprint,
    };

    private static ScopeSummary Summary(ScopeRecord s, ScopeRevisionView? current) => new()
    {
        Id = s.Id, ProjectKey = s.ProjectKey, Name = s.Name, Description = s.Description,
        Purpose = Enum.TryParse<ScopePurpose>(s.Purpose, out var p) ? p : ScopePurpose.Other, Owner = s.Owner,
        State = Enum.TryParse<ScopeState>(s.State, out var st) ? st : ScopeState.Draft, CurrentRevision = s.CurrentRevision, ApprovedRevision = s.ApprovedRevision,
        MemberCount = current?.Members.Count(m => m.Inclusion == ScopeMemberInclusion.Confirmed) ?? 0,
        MembersByKind = current?.Members.Where(m => m.Inclusion == ScopeMemberInclusion.Confirmed).GroupBy(m => m.Kind).ToDictionary(g => g.Key, g => g.Count()) ?? [],
        Version = s.Version, UpdatedAt = s.UpdatedAt,
    };

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(max, value.Trim().Length)];
}
