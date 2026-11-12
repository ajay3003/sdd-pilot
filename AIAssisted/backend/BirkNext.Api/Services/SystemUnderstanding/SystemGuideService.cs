using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Integrations;
using BirkNext.SystemUnderstanding;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.SystemUnderstanding;

/// <summary>A guide the catalog offers: its key, the curated seed (an embedded resource) and, optionally, the test pack whose source roles
/// recognise its imported sources. Generic: a guide for another system is another entry with its own seed — no code is system-specific.</summary>
public sealed record SystemGuideCatalogEntry(string GuideKey, string SeedResource, string? PackId = null);

public sealed class SystemGuideException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// System Understanding guides: curated, versioned explanations of a system under test. Revisions are stored in
/// <c>system_guide_revisions</c>; a seed is inserted once as an unreviewed Draft (never Approved), a published revision is never rewritten,
/// and newly imported source snapshots for the guide's roles are reported as "may need review" — the content is never rewritten automatically.
/// </summary>
public sealed class SystemGuideService(AppDbContext db, IEnumerable<SystemGuideCatalogEntry> catalog, IEnumerable<IIntegrationJourneyPack> packs, TimeProvider? clock = null)
{
    public const string SeedAuthor = "BirkNext curated seed (not reviewed)";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    // ── Catalog and seeds ────────────────────────────────────────────────────────────────────────────────────

    public SystemGuideCatalogEntry Entry(string guideKey) =>
        catalog.FirstOrDefault(e => string.Equals(e.GuideKey, guideKey, StringComparison.Ordinal)) ?? throw new SystemGuideException(404, $"Unknown guide '{guideKey}'.");

    /// <summary>The embedded curated seed of a guide (validated: every reference inside the guide resolves).</summary>
    public static SystemGuideContent LoadSeed(string resource, Assembly? assembly = null)
    {
        assembly ??= typeof(SystemGuideService).Assembly;
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Guide seed '{resource}' is not embedded.");
        var content = JsonSerializer.Deserialize<SystemGuideContent>(stream, Json) ?? throw new InvalidOperationException($"Guide seed '{resource}' is empty.");
        var problems = SystemGuideValidation.Problems(content);
        if (problems.Count > 0) throw new InvalidOperationException($"Guide seed '{resource}' is inconsistent: {string.Join("; ", problems)}");
        return content;
    }

    public static string Fingerprint(SystemGuideContent content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, Json)))).ToLowerInvariant();

    /// <summary>
    /// Inserts the curated seed as revision 1, Draft, once: never when any revision exists, never as approved. A seed that nobody has edited,
    /// submitted or published (the only revision, still a Draft by the seed author) is replaced by a newer shipped seed; anything a person touched
    /// is never changed.
    /// </summary>
    private async Task EnsureSeededAsync(SystemGuideCatalogEntry entry, CancellationToken ct)
    {
        var rows = await db.SystemGuideRevisions.Where(r => r.GuideKey == entry.GuideKey).OrderBy(r => r.Revision).Take(2).ToListAsync(ct);
        if (rows.Count == 1 && rows[0] is { Revision: 1, State: nameof(GuideRevisionState.Draft), CreatedBy: SeedAuthor } untouched)
        {
            var shipped = LoadSeed(entry.SeedResource);
            var fingerprint = Fingerprint(shipped);
            if (untouched.ContentFingerprint == fingerprint) return;
            untouched.ContentJson = JsonSerializer.Serialize(shipped, Json);
            untouched.ContentFingerprint = fingerprint;
            untouched.SourceBasisJson = JsonSerializer.Serialize(SeedBasis(entry, shipped), Json);
            untouched.ChangeSummary = SeedSummary + " Replaced an older, unedited curated draft.";
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }
            return;
        }
        if (rows.Count > 0) return;
        var content = LoadSeed(entry.SeedResource);
        db.SystemGuideRevisions.Add(new SystemGuideRevisionRecord
        {
            Id = Guid.NewGuid(), GuideKey = entry.GuideKey, Revision = 1, State = nameof(GuideRevisionState.Draft), ContentJson = JsonSerializer.Serialize(content, Json),
            ContentFingerprint = Fingerprint(content), SourceBasisJson = JsonSerializer.Serialize(SeedBasis(entry, content), Json),
            CreatedBy = SeedAuthor, CreatedAt = Now, ChangeSummary = SeedSummary,
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); } // another request seeded it first (unique guide+revision)
    }

    private const string SeedSummary = "Curated draft shipped with BirkNext, written from reviewed source packages. Not reviewed or approved here.";

    /// <summary>
    /// A curated seed is written outside this installation, so it is based on no imported snapshot here: every recognised snapshot therefore reads
    /// as not yet reviewed against the guide. Recording the snapshots present at seeding time would claim a review that never happened.
    /// </summary>
    private List<GuideSourceBasis> SeedBasis(SystemGuideCatalogEntry entry, SystemGuideContent content) =>
        [.. Roles(entry, content).Select(role => new GuideSourceBasis(role.Key, null, null, $"{role.Label}: curated outside this installation (no imported snapshot)"))];

    // ── Reading ──────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SystemGuideSummary>> ListAsync(CancellationToken ct)
    {
        var result = new List<SystemGuideSummary>();
        foreach (var entry in catalog)
        {
            await EnsureSeededAsync(entry, ct);
            var rows = await db.SystemGuideRevisions.AsNoTracking().Where(r => r.GuideKey == entry.GuideKey).ToListAsync(ct);
            var published = rows.FirstOrDefault(r => r.State == nameof(GuideRevisionState.Published));
            var open = rows.Where(r => r.State is nameof(GuideRevisionState.Draft) or nameof(GuideRevisionState.InReview) or nameof(GuideRevisionState.Approved))
                .OrderByDescending(r => r.Revision).FirstOrDefault();
            var shown = Content(published ?? open ?? rows.OrderByDescending(r => r.Revision).First());
            var current = await CurrentSourcesAsync(entry, shown, ct);
            result.Add(new SystemGuideSummary(entry.GuideKey, shown.Title, shown.Purpose, ReviewState(published, open), published?.Revision, open?.Revision,
                current.Where(s => s.SnapshotId is not null).Select(s => s.Role).Distinct().Count(), Roles(entry, shown).Count));
        }
        return result;
    }

    /// <summary>The revision readers see (published, else the open one, else the latest) and the review state, without reading any source
    /// snapshot: for consumers that only need the guide's content (Test Plan Suggestions change detection and summaries).</summary>
    public async Task<(SystemGuideRevision Shown, string ReviewState)> ShownAsync(string guideKey, CancellationToken ct)
    {
        var entry = Entry(guideKey);
        await EnsureSeededAsync(entry, ct);
        var rows = await db.SystemGuideRevisions.AsNoTracking().Where(r => r.GuideKey == guideKey).OrderBy(r => r.Revision).ToListAsync(ct);
        var published = rows.FirstOrDefault(r => r.State == nameof(GuideRevisionState.Published));
        var open = rows.Where(r => r.State is nameof(GuideRevisionState.Draft) or nameof(GuideRevisionState.InReview) or nameof(GuideRevisionState.Approved))
            .OrderByDescending(r => r.Revision).FirstOrDefault();
        return (ToRevision(published ?? open ?? rows.Last()), ReviewState(published, open));
    }

    /// <summary>
    /// The guides whose source roles recognise at least one of <paramref name="snapshots"/> (by components/projects, as for the guide's current
    /// sources), each with the revision readers see. Used by the canonical interpretation (MT-7): a guide is read only for the system the interpreted
    /// sources belong to, so an interpretation of another system never carries its scenarios.
    /// </summary>
    public async Task<IReadOnlyList<SystemGuideRevision>> RecognisedGuidesAsync(IReadOnlyList<IqrSourceSnapshot> snapshots, CancellationToken ct)
    {
        var result = new List<SystemGuideRevision>();
        if (snapshots.Count == 0) return result;
        foreach (var entry in catalog)
        {
            var (shown, _) = await ShownAsync(entry.GuideKey, ct);
            if (Roles(entry, shown.Content).Any(role => snapshots.Any(s => TestPackSources.Recognizes(role, s)))) result.Add(shown);
        }
        return result;
    }

    /// <summary>The catalog's guide keys (no database or source reads).</summary>
    public IReadOnlyList<string> GuideKeys => [.. catalog.Select(e => e.GuideKey)];

    /// <param name="snapshots">The stored Source Analysis snapshots when the caller already loaded them in this request (one deserialization of
    /// every snapshot's evidence instead of several); null reads them here, once.</param>
    public async Task<SystemGuideView> GetAsync(string guideKey, bool canEdit, CancellationToken ct, IReadOnlyList<IqrSourceSnapshot>? snapshots = null)
    {
        var entry = Entry(guideKey);
        await EnsureSeededAsync(entry, ct);
        var rows = await db.SystemGuideRevisions.AsNoTracking().Where(r => r.GuideKey == guideKey).OrderBy(r => r.Revision).ToListAsync(ct);
        var published = rows.FirstOrDefault(r => r.State == nameof(GuideRevisionState.Published));
        var open = rows.Where(r => r.State is nameof(GuideRevisionState.Draft) or nameof(GuideRevisionState.InReview) or nameof(GuideRevisionState.Approved))
            .OrderByDescending(r => r.Revision).FirstOrDefault();
        var shownRow = published ?? open ?? rows.Last();
        var shown = ToRevision(shownRow);
        snapshots ??= await new Integrations.SourceEvidence.IqrSourceStore(db).ListSourceAnalysisIdentitiesAsync(500, ct);
        var current = await CurrentSourcesAsync(entry, shown.Content, ct, snapshots);
        return new SystemGuideView
        {
            GuideKey = guideKey, Title = shown.Content.Title, Purpose = shown.Content.Purpose, Shown = shown, Draft = open is null ? null : ToRevision(open),
            Revisions = [.. rows.OrderByDescending(r => r.Revision).Select(r => new SystemGuideRevisionSummary(r.Revision, Parse(r.State), r.ContentFingerprint, r.CreatedBy, r.CreatedAt,
                r.ApprovedBy, r.ApprovedAt, r.PublishedAt, r.ChangeSummary))],
            ReviewState = ReviewState(published, open),
            SourceChanges = SourceChanges(shown, current, snapshots.Select(s => s.Id).ToHashSet()),
            CurrentSources = current, CanEdit = canEdit, CanApprove = canEdit,
        };
    }

    public async Task<SystemGuideRevision> RevisionAsync(string guideKey, int revision, CancellationToken ct)
    {
        Entry(guideKey);
        var row = await db.SystemGuideRevisions.AsNoTracking().SingleOrDefaultAsync(r => r.GuideKey == guideKey && r.Revision == revision, ct)
            ?? throw new SystemGuideException(404, $"Guide '{guideKey}' has no revision {revision}.");
        return ToRevision(row);
    }

    private static string ReviewState(SystemGuideRevisionRecord? published, SystemGuideRevisionRecord? open) => (published, open) switch
    {
        ({ } p, null) => $"Published (revision {p.Revision})",
        ({ } p, { } o) => $"Published (revision {p.Revision}); revision {o.Revision} is {Label(o.State)}",
        (null, { } o) when o.CreatedBy == SeedAuthor && o.State == nameof(GuideRevisionState.Draft) => "Draft only — not reviewed",
        (null, { } o) => $"Revision {o.Revision} is {Label(o.State)} — nothing published yet",
        _ => "None",
    };

    private static string Label(string state) => state switch { "InReview" => "in review", "Approved" => "approved, not yet published", _ => "a draft" };

    // ── Change awareness ─────────────────────────────────────────────────────────────────────────────────────

    private IReadOnlyList<TestPackSourceRole> Roles(SystemGuideCatalogEntry entry, SystemGuideContent content)
    {
        var pack = entry.PackId is null ? null : packs.FirstOrDefault(p => string.Equals(p.PackId, entry.PackId, StringComparison.Ordinal));
        return [.. content.SourceRoles.Select(role =>
            pack?.SourceRoles.FirstOrDefault(r => string.Equals(r.Key, role.Role, StringComparison.Ordinal)) is { } packRole
                ? packRole with { Label = role.Label }
                : new TestPackSourceRole(role.Role, role.Label, role.Label) { ComponentTokens = role.MatchTokens })];
    }

    /// <summary>
    /// The imported snapshots recognised as each of the guide's source roles (by components/projects, never by archive name) — identity only.
    /// Several snapshots for one role stay separate entries: nothing picks the newest one as "the" source.
    /// </summary>
    public async Task<List<GuideSourceBasis>> CurrentSourcesAsync(SystemGuideCatalogEntry entry, SystemGuideContent content, CancellationToken ct,
        IReadOnlyList<IqrSourceSnapshot>? loaded = null)
    {
        var roles = Roles(entry, content);
        if (roles.Count == 0) return [];
        // The workspace's Source Analysis snapshots, as Source Analysis lists them (one shared reader; no second inventory).
        // Only the snapshot each logical source is analysed with (its analysis baseline, else its only content): alternative branch snapshots of the
        // same source are never all cited as the guide's basis, and none is cited while BASELINE_NOT_SELECTED. Switching the baseline changes the
        // basis, so affected claims show as source changes.
        var all = loaded ?? await new Integrations.SourceEvidence.IqrSourceStore(db).ListSourceAnalysisIdentitiesAsync(500, ct);
        var snapshots = SourceAnalysis.SourceBaselineService.Resolve(all, await SourceAnalysis.SourceBaselineService.CurrentBaselinesAsync(db, ct)).OrderBy(s => s.AnalyzedAt).ToList();
        var resolvedKeys = snapshots.Select(SourceAnalysis.SourceBaselineService.KeyOf).ToHashSet(StringComparer.Ordinal);
        var unresolved = all.Where(s => !resolvedKeys.Contains(SourceAnalysis.SourceBaselineService.KeyOf(s))).ToList();
        var result = new List<GuideSourceBasis>();
        foreach (var role in roles)
        {
            var matched = snapshots.Where(s => TestPackSources.Recognizes(role, s)).ToList();
            if (matched.Count == 0 && unresolved.Where(s => TestPackSources.Recognizes(role, s)).Select(SourceAnalysis.SourceBaselineService.KeyOf).Distinct(StringComparer.Ordinal).ToList() is { Count: > 0 } waiting)
                result.Add(new GuideSourceBasis(role.Key, null, null,
                    $"{role.Label}: BASELINE_NOT_SELECTED — imported in alternative versions ({string.Join(", ", waiting)}); select the analysis baseline in Source versions"));
            else if (matched.Count == 0) result.Add(new GuideSourceBasis(role.Key, null, null, $"{role.Label}: not imported"));
            result.AddRange(matched.Select(s => new GuideSourceBasis(role.Key, s.Id, s.Archive.Sha256, $"{role.Label}: {s.Archive.FileName} (analysed {s.AnalyzedAt:yyyy-MM-dd})")));
        }
        return result;
    }

    /// <summary>Snapshots imported or removed since the shown revision's basis, with how many of its claims cite that role. A basis snapshot that is
    /// still imported but no longer the analysis baseline (<paramref name="stillImported"/>) is said to be so — never "no longer imported".</summary>
    public static List<GuideSourceChange> SourceChanges(SystemGuideRevision shown, IReadOnlyList<GuideSourceBasis> current, IReadOnlySet<Guid>? stillImported = null)
    {
        int Claims(string role) => AllEvidence(shown.Content).Count(e => string.Equals(e.SourceRole, role, StringComparison.Ordinal));
        var basis = shown.SourceBasis.Where(b => b.SnapshotId is not null).Select(b => b.SnapshotId!.Value).ToHashSet();
        var now = current.Where(c => c.SnapshotId is not null).Select(c => c.SnapshotId!.Value).ToHashSet();
        var changes = new List<GuideSourceChange>();
        foreach (var added in current.Where(c => c.SnapshotId is { } id && !basis.Contains(id)))
            changes.Add(new GuideSourceChange(added.Role, added.Label, "Not among the sources this revision was written from — the guide may need review", Claims(added.Role)));
        foreach (var removed in shown.SourceBasis.Where(b => b.SnapshotId is { } id && !now.Contains(id)))
            changes.Add(new GuideSourceChange(removed.Role, removed.Label, stillImported?.Contains(removed.SnapshotId!.Value) == true
                ? "No longer the analysis baseline (still imported; its claims can still be opened) — review the guide against the current baseline"
                : "No longer imported — the claims citing it can no longer be opened", Claims(removed.Role)));
        return changes;
    }

    public static IEnumerable<GuideEvidence> AllEvidence(SystemGuideContent c) =>
        c.Components.SelectMany(x => x.Evidence).Concat(c.Edges.SelectMany(x => x.Evidence)).Concat(c.Steps.SelectMany(x => x.Evidence))
            .Concat(c.Rules.SelectMany(x => x.Evidence)).Concat(c.Scenarios.SelectMany(x => x.Evidence));

    // ── Lifecycle ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves the open Draft (optimistic: the caller's base fingerprint must match), or opens a new Draft revision from the published one.
    /// Identical content creates nothing. A revision in review must be returned to draft first.
    /// </summary>
    public async Task<SystemGuideRevision> SaveDraftAsync(string guideKey, SaveGuideDraftRequest request, string actor, CancellationToken ct)
    {
        var entry = Entry(guideKey);
        await EnsureSeededAsync(entry, ct);
        var problems = SystemGuideValidation.Problems(request.Content);
        if (problems.Count > 0) throw new SystemGuideException(422, "The guide is inconsistent: " + string.Join("; ", problems.Take(10)));
        var fingerprint = Fingerprint(request.Content);
        var rows = await db.SystemGuideRevisions.Where(r => r.GuideKey == guideKey).ToListAsync(ct);
        var open = rows.FirstOrDefault(r => r.State is nameof(GuideRevisionState.Draft) or nameof(GuideRevisionState.InReview));
        if (open is { State: nameof(GuideRevisionState.InReview) })
            throw new SystemGuideException(409, $"Revision {open.Revision} is in review. Return it to draft before changing it.");
        if (rows.Any(r => r.State == nameof(GuideRevisionState.Approved)))
            throw new SystemGuideException(409, "An approved revision is waiting to be published. Publish it first, or it stays as approved.");
        if (open is not null)
        {
            if (request.BaseFingerprint is not null && request.BaseFingerprint != open.ContentFingerprint)
                throw new SystemGuideException(409, "The draft was changed by someone else since you opened it. Reload and apply your change again.");
            if (open.ContentFingerprint == fingerprint) return ToRevision(open);
            open.ContentJson = JsonSerializer.Serialize(request.Content, Json);
            open.ContentFingerprint = fingerprint;
            open.SourceBasisJson = JsonSerializer.Serialize(await CurrentSourcesAsync(entry, request.Content, ct), Json);
            open.ChangeSummary = Trim(request.ChangeSummary) ?? open.ChangeSummary;
            open.CreatedBy = open.CreatedBy == SeedAuthor ? $"{SeedAuthor}; edited by {actor}" : open.CreatedBy;
            await db.SaveChangesAsync(ct);
            return ToRevision(open);
        }
        var latest = rows.OrderByDescending(r => r.Revision).First();
        if (latest.ContentFingerprint == fingerprint) throw new SystemGuideException(409, "The content is identical to revision " + latest.Revision + ": no new revision is created.");
        var row = new SystemGuideRevisionRecord
        {
            Id = Guid.NewGuid(), GuideKey = guideKey, Revision = rows.Max(r => r.Revision) + 1, State = nameof(GuideRevisionState.Draft),
            ContentJson = JsonSerializer.Serialize(request.Content, Json), ContentFingerprint = fingerprint,
            SourceBasisJson = JsonSerializer.Serialize(await CurrentSourcesAsync(entry, request.Content, ct), Json), CreatedBy = actor, CreatedAt = Now, ChangeSummary = Trim(request.ChangeSummary),
        };
        db.SystemGuideRevisions.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new SystemGuideException(409, "Another draft was opened at the same time. Reload the guide."); }
        return ToRevision(row);
    }

    public Task<SystemGuideRevision> SubmitAsync(string guideKey, int revision, string actor, CancellationToken ct) =>
        TransitionAsync(guideKey, revision, GuideRevisionState.Draft, GuideRevisionState.InReview, row => { row.SubmittedBy = actor; row.SubmittedAt = Now; }, ct);

    public Task<SystemGuideRevision> ReturnToDraftAsync(string guideKey, int revision, CancellationToken ct) =>
        TransitionAsync(guideKey, revision, GuideRevisionState.InReview, GuideRevisionState.Draft, row => { row.SubmittedBy = null; row.SubmittedAt = null; }, ct);

    /// <summary>Approval records the reviewer and time; content and source basis are unchanged (immutable beyond Draft).</summary>
    public async Task<SystemGuideRevision> ApproveAsync(string guideKey, int revision, string actor, CancellationToken ct)
    {
        Entry(guideKey);
        var row = await RowAsync(guideKey, revision, ct);
        if (row.State != nameof(GuideRevisionState.InReview))
            throw new SystemGuideException(409, $"Only a revision in review can be approved (revision {revision} is {row.State}).");
        // The source basis is part of the immutable record beyond Draft: the approval covers exactly the basis the draft was written against.
        row.State = nameof(GuideRevisionState.Approved);
        row.ApprovedBy = actor;
        row.ApprovedAt = Now;
        await db.SaveChangesAsync(ct);
        return ToRevision(row);
    }

    /// <summary>Publishes an approved revision; the previously published one becomes Superseded (kept, never rewritten). One transaction.</summary>
    public async Task<SystemGuideRevision> PublishAsync(string guideKey, int revision, CancellationToken ct)
    {
        Entry(guideKey);
        await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var row = await RowAsync(guideKey, revision, ct);
        if (row.State != nameof(GuideRevisionState.Approved))
            throw new SystemGuideException(409, $"Only an approved revision can be published (revision {revision} is {row.State}).");
        var previous = await db.SystemGuideRevisions.Where(r => r.GuideKey == guideKey && r.State == nameof(GuideRevisionState.Published)).ToListAsync(ct);
        foreach (var p in previous) { p.State = nameof(GuideRevisionState.Superseded); p.SupersededByRevision = revision; }
        await db.SaveChangesAsync(ct);
        row.State = nameof(GuideRevisionState.Published);
        row.PublishedAt = Now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { throw new SystemGuideException(409, "Another revision was published at the same time. Reload the guide."); }
        if (tx is not null) await tx.CommitAsync(ct);
        return ToRevision(row);
    }

    private async Task<SystemGuideRevision> TransitionAsync(string guideKey, int revision, GuideRevisionState from, GuideRevisionState to, Action<SystemGuideRevisionRecord> apply, CancellationToken ct)
    {
        Entry(guideKey);
        var row = await RowAsync(guideKey, revision, ct);
        if (row.State != from.ToString()) throw new SystemGuideException(409, $"Revision {revision} is {row.State}, not {from}.");
        row.State = to.ToString();
        apply(row);
        await db.SaveChangesAsync(ct);
        return ToRevision(row);
    }

    private async Task<SystemGuideRevisionRecord> RowAsync(string guideKey, int revision, CancellationToken ct) =>
        await db.SystemGuideRevisions.SingleOrDefaultAsync(r => r.GuideKey == guideKey && r.Revision == revision, ct)
        ?? throw new SystemGuideException(404, $"Guide '{guideKey}' has no revision {revision}.");

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().Length > 1000 ? text.Trim()[..1000] : text.Trim();

    private static GuideRevisionState Parse(string state) => Enum.TryParse<GuideRevisionState>(state, out var s) ? s : GuideRevisionState.Draft;

    private static SystemGuideContent Content(SystemGuideRevisionRecord row) => JsonSerializer.Deserialize<SystemGuideContent>(row.ContentJson, Json) ?? new();

    private static SystemGuideRevision ToRevision(SystemGuideRevisionRecord row) => new()
    {
        GuideKey = row.GuideKey, Revision = row.Revision, State = Parse(row.State), ContentFingerprint = row.ContentFingerprint, Content = Content(row),
        SourceBasis = JsonSerializer.Deserialize<List<GuideSourceBasis>>(row.SourceBasisJson, Json) ?? [], CreatedBy = row.CreatedBy, CreatedAt = row.CreatedAt,
        ChangeSummary = row.ChangeSummary, SubmittedBy = row.SubmittedBy, SubmittedAt = row.SubmittedAt, ApprovedBy = row.ApprovedBy, ApprovedAt = row.ApprovedAt,
        PublishedAt = row.PublishedAt, SupersededByRevision = row.SupersededByRevision,
    };
}

/// <summary>
/// Structural and safety checks of guide content: every reference resolves inside the guide, ids are unique, and links are relative BirkNext
/// routes (no scheme, no protocol-relative or script URL). Text is rendered as text by the page (never as markup).
/// </summary>
public static class SystemGuideValidation
{
    public static IReadOnlyList<string> Problems(SystemGuideContent c)
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(c.Title)) p.Add("title is required");
        var components = c.Components.Select(x => x.Id).ToList();
        Duplicates("component", components, p);
        Duplicates("edge", c.Edges.Select(x => x.Id), p);
        Duplicates("step", c.Steps.Select(x => x.Id), p);
        Duplicates("rule", c.Rules.Select(x => x.Id), p);
        Duplicates("scenario", c.Scenarios.Select(x => x.Id), p);
        Duplicates("stage", c.Stages.Select(x => x.Id), p);
        var ids = components.ToHashSet(StringComparer.Ordinal);
        foreach (var e in c.Edges) { if (!ids.Contains(e.From)) p.Add($"edge {e.Id}: unknown component {e.From}"); if (!ids.Contains(e.To)) p.Add($"edge {e.Id}: unknown component {e.To}"); }
        foreach (var f in c.Flows) foreach (var id in f.Path.Where(id => !ids.Contains(id))) p.Add($"flow {f.Id}: unknown component {id}");
        var stages = c.Stages.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var flows = c.Flows.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var s in c.Stages.Where(s => !flows.Contains(s.FlowId))) p.Add($"stage {s.Id}: unknown flow {s.FlowId}");
        var scenarios = c.Scenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var steps = c.Steps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var s in c.Steps)
        {
            if (!ids.Contains(s.ComponentId)) p.Add($"step {s.Id}: unknown component {s.ComponentId}");
            if (!stages.Contains(s.Stage)) p.Add($"step {s.Id}: unknown stage {s.Stage}");
            foreach (var t in s.TestScenarioIds.Where(t => !scenarios.Contains(t))) p.Add($"step {s.Id}: unknown scenario {t}");
        }
        foreach (var r in c.Rules)
        {
            if (r.ComponentId is { } comp && !ids.Contains(comp)) p.Add($"rule {r.Id}: unknown component {comp}");
            foreach (var t in r.TestScenarioIds.Where(t => !scenarios.Contains(t))) p.Add($"rule {r.Id}: unknown scenario {t}");
        }
        foreach (var s in c.Scenarios) foreach (var st in s.StepIds.Where(st => !steps.Contains(st))) p.Add($"scenario {s.Id}: unknown step {st}");
        var edges = c.Edges.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var t in c.Transfers.Where(t => !edges.Contains(t.EdgeId))) p.Add($"transfer: unknown edge {t.EdgeId}");
        foreach (var link in SystemGuideService.AllEvidence(c).Select(e => e.Link).Concat(c.Transfers.Select(t => t.Link)).OfType<string>())
            if (!IsSafeRoute(link)) p.Add($"unsafe link '{link}' (only relative BirkNext routes)");
        return p;
    }

    public static bool IsSafeRoute(string link) => SystemGuideLinks.IsSafeRoute(link);

    private static void Duplicates(string kind, IEnumerable<string> ids, List<string> problems)
    {
        foreach (var d in ids.GroupBy(i => i, StringComparer.Ordinal).Where(g => g.Count() > 1)) problems.Add($"duplicate {kind} id {d.Key}");
    }
}
