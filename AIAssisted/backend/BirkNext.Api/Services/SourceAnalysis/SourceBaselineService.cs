using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using BirkNext.SourceBaselines;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.SourceAnalysis;

/// <summary>
/// The one authoritative source context: which imported snapshot represents each logical source (repository identity) in analysis.
/// <list type="bullet">
/// <item>Snapshots are grouped by the existing repository identity (<see cref="ReviewSourceEvidenceProvider.Identity"/>); its basis is shown, so a
/// grouping by archive name is never presented as verified repository identity.</item>
/// <item>A person selects the analysis baseline per logical source (append-only <see cref="SourceDecisionRecord"/>; selecting the same snapshot again
/// records nothing). Importing another archive never changes it.</item>
/// <item><see cref="ResolveAsync"/> is the shared rule every module uses: exactly one snapshot per logical source — the baseline when selected, the only
/// or content-identical version otherwise, else the newest import with state NotSelected. Alternative snapshots of one source are never combined.
/// A selected baseline that no longer exists is not substituted: that source is left out and reported as SelectedUnavailable.</item>
/// </list>
/// An analysis baseline is not a deployment baseline: nothing here says what is deployed anywhere.
/// </summary>
public sealed class SourceBaselineService(AppDbContext db, IqrSourceStore store)
{
    public const string BaselineKind = "AnalysisBaseline", DeclaredBranchKind = "DeclaredBranch";
    public const int Window = 500;

    public static string KeyOf(IqrSourceSnapshot snapshot) => ReviewSourceEvidenceProvider.Identity(snapshot).Key;

    /// <summary>Content identity of a snapshot: the canonical content fingerprint, or (older snapshots) the archive hash.</summary>
    public static string ContentOf(IqrSourceSnapshot snapshot) => snapshot.Archive.ContentSha256 ?? ("archive:" + snapshot.Archive.Sha256);

    /// <summary>Current analysis baselines: repository key → snapshot id (cleared selections are absent).</summary>
    public Task<IReadOnlyDictionary<string, Guid>> CurrentBaselinesAsync(CancellationToken ct) => CurrentBaselinesAsync(db, ct);

    /// <summary>The same lookup for services that hold their own <see cref="AppDbContext"/>.</summary>
    public static async Task<IReadOnlyDictionary<string, Guid>> CurrentBaselinesAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.SourceDecisions.AsNoTracking().Where(d => d.Kind == BaselineKind).ToListAsync(ct);
        return rows.GroupBy(r => r.Subject, StringComparer.Ordinal).Select(g => g.OrderByDescending(r => r.DecidedAt).ThenByDescending(r => r.Id).First())
            .Where(r => r.SnapshotId is not null).ToDictionary(r => r.Subject, r => r.SnapshotId!.Value, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> DeclaredBranchesAsync(CancellationToken ct)
    {
        var rows = await db.SourceDecisions.AsNoTracking().Where(d => d.Kind == DeclaredBranchKind).ToListAsync(ct);
        return rows.GroupBy(r => r.Subject, StringComparer.Ordinal).Select(g => g.OrderByDescending(r => r.DecidedAt).ThenByDescending(r => r.Id).First())
            .Where(r => r.SnapshotId is not null && !string.IsNullOrWhiteSpace(r.Value)).ToDictionary(r => r.SnapshotId!.Value, r => r.Value!);
    }

    /// <summary>The shared resolution rule over already-loaded snapshots (a selected baseline outside the loaded window is fetched exactly).</summary>
    public async Task<List<IqrSourceSnapshot>> ResolveAsync(IEnumerable<IqrSourceSnapshot> loaded, CancellationToken ct)
    {
        var list = loaded.ToList();
        var baselines = await CurrentBaselinesAsync(ct);
        foreach (var (key, id) in baselines)
            if (list.Any(s => KeyOf(s) == key) && list.All(s => s.Id != id) && await store.FindSourceAnalysisAsync(id, ct) is { } missing) list.Add(missing);
        return Resolve(list, baselines);
    }

    /// <summary>One snapshot per logical source (see the class summary). Pure; unit-testable.</summary>
    public static List<IqrSourceSnapshot> Resolve(IEnumerable<IqrSourceSnapshot> snapshots, IReadOnlyDictionary<string, Guid> baselines)
    {
        var result = new List<IqrSourceSnapshot>();
        foreach (var group in snapshots.GroupBy(KeyOf, StringComparer.Ordinal))
        {
            if (baselines.TryGetValue(group.Key, out var id))
            {
                if (group.FirstOrDefault(s => s.Id == id) is { } selected) result.Add(selected);
                continue; // selected but unavailable: never substitute
            }
            result.Add(group.OrderByDescending(s => s.AnalyzedAt).ThenByDescending(s => s.Id).First());
        }
        return result;
    }

    public static SourceBranchKind BranchKindOf(IqrSourceSnapshot s, string? declared) =>
        !string.IsNullOrWhiteSpace(s.Branch) && !string.Equals(s.Commit, "Unknown", StringComparison.OrdinalIgnoreCase) ? SourceBranchKind.Verified
        : declared is { Length: > 0 } ? SourceBranchKind.Declared : SourceBranchKind.Unknown;

    /// <summary>Top-level folders that hold the snapshot's production projects (e.g. "SkoleService"): a readable component summary, not an identity.</summary>
    public static IReadOnlyList<string> ComponentsOf(IqrSourceSnapshot s) =>
        [.. s.Projects.Where(p => !p.IsTest).Select(p => p.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? p.Name)
            .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The source context: every logical source with its snapshots, the selected baseline and the snapshot modules use.</summary>
    public async Task<SourceContextView> ContextAsync(CancellationToken ct)
    {
        var snapshots = await store.ListSourceAnalysisAsync(Window, ct);
        var baselines = await CurrentBaselinesAsync(ct);
        var declared = await DeclaredBranchesAsync(ct);
        var used = (await ResolveAsync(snapshots, ct)).Select(s => s.Id).ToHashSet();
        var sources = new List<LogicalSourceView>();
        foreach (var group in snapshots.GroupBy(KeyOf, StringComparer.Ordinal))
        {
            var ordered = group.OrderByDescending(s => s.AnalyzedAt).ThenByDescending(s => s.Id).ToList();
            var identity = ReviewSourceEvidenceProvider.Identity(ordered[0]);
            var contents = ordered.Select(ContentOf).Distinct(StringComparer.Ordinal).Count();
            baselines.TryGetValue(group.Key, out var baselineId);
            var hasBaseline = baselines.ContainsKey(group.Key);
            var baselineAvailable = hasBaseline && ordered.Any(s => s.Id == baselineId);
            var state = hasBaseline ? (baselineAvailable ? SourceBaselineState.Selected : SourceBaselineState.SelectedUnavailable)
                : ordered.Count == 1 ? SourceBaselineState.SingleSnapshot : contents == 1 ? SourceBaselineState.IdenticalContent : SourceBaselineState.NotSelected;
            var usedId = ordered.FirstOrDefault(s => used.Contains(s.Id))?.Id;
            var detail = state switch
            {
                SourceBaselineState.Selected => "Analysis baseline selected; every module uses this snapshot for this source.",
                SourceBaselineState.SingleSnapshot => "One snapshot; it is used for analysis.",
                SourceBaselineState.IdenticalContent => $"{ordered.Count} imports with identical content; the newest import is used.",
                SourceBaselineState.SelectedUnavailable => "The selected baseline snapshot is no longer available; this source is left out of analysis until a baseline is selected again. Nothing is substituted.",
                _ => $"{contents} different versions and no analysis baseline: modules use the newest import until you select one. Implementation-specific results depend on this choice.",
            };
            var views = ordered.Select(s =>
            {
                declared.TryGetValue(s.Id, out var label);
                var kind = BranchKindOf(s, label);
                return new SourceSnapshotView(s.Id, s.Archive.FileName, s.Archive.Sha256, s.Archive.ContentSha256, s.AnalyzedAt, ReviewSourceEvidenceProvider.SourceStatus(s.Status),
                    kind, kind == SourceBranchKind.Verified ? s.Branch : kind == SourceBranchKind.Declared ? label : null, kind == SourceBranchKind.Verified ? s.Commit : null,
                    s.ProjectImport?.ImportId, hasBaseline && s.Id == baselineId, s.Id == ordered[0].Id, used.Contains(s.Id), s.AnalyzerVersion, s.Archive.FilesAnalyzed,
                    ComponentsOf(s), ordered.Count(o => o.Id != s.Id && ContentOf(o) == ContentOf(s)));
            }).ToList();
            sources.Add(new LogicalSourceView(group.Key, identity.DisplayName, identity.Basis, state, hasBaseline ? baselineId : null, usedId, detail, ordered.Count, contents, views));
        }
        sources = [.. sources.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)];
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", sources.Select(s => $"{s.RepositoryKey}={s.UsedSnapshotId}"))))).ToLowerInvariant()[..16];
        return new SourceContextView(sources, sources.Count, snapshots.Count, sources.Count(s => s.State is SourceBaselineState.NotSelected or SourceBaselineState.SelectedUnavailable), fingerprint);
    }

    /// <summary>Selects (or clears, <paramref name="snapshotId"/> null) the analysis baseline of one logical source. The same choice again records nothing.</summary>
    public async Task<SourceDecisionResult> SelectBaselineAsync(string repositoryKey, Guid? snapshotId, string actor, string? reason, CancellationToken ct)
    {
        IqrSourceSnapshot? snapshot = null;
        if (snapshotId is { } id)
        {
            snapshot = await store.FindSourceAnalysisAsync(id, ct);
            if (snapshot is null) return new(false, "That source snapshot does not exist.", null);
            if (KeyOf(snapshot) != repositoryKey) return new(false, $"That snapshot belongs to {ReviewSourceEvidenceProvider.Identity(snapshot).DisplayName}, not to the selected source.", null);
        }
        var current = await db.SourceDecisions.AsNoTracking().Where(d => d.Kind == BaselineKind && d.Subject == repositoryKey)
            .OrderByDescending(d => d.DecidedAt).ThenByDescending(d => d.Id).FirstOrDefaultAsync(ct);
        if (current?.SnapshotId == snapshotId || (current is null && snapshotId is null))
            return new(false, "Unchanged: that is already the analysis baseline; no new revision was recorded.", current is null ? null : View(current));
        var record = new SourceDecisionRecord
        {
            Id = Guid.NewGuid(), Kind = BaselineKind, Subject = repositoryKey, SnapshotId = snapshotId, SnapshotLabel = snapshot?.Archive.FileName,
            ContentFingerprint = snapshot is null ? null : ContentOf(snapshot)[..Math.Min(64, ContentOf(snapshot).Length)],
            DecidedAt = After(current), DecidedBy = actor, Reason = Trim(reason, 500),
        };
        db.SourceDecisions.Add(record);
        await db.SaveChangesAsync(ct);
        return new(true, snapshot is null ? "Analysis baseline cleared." : $"Analysis baseline set to {snapshot.Archive.FileName}.", View(record));
    }

    /// <summary>Records (or removes) a declared branch label for a snapshot: a person's statement, shown as "declared", never as Git evidence.</summary>
    public async Task<SourceDecisionResult> DeclareBranchAsync(Guid snapshotId, string? branch, string actor, CancellationToken ct)
    {
        var snapshot = await store.FindSourceAnalysisAsync(snapshotId, ct);
        if (snapshot is null) return new(false, "That source snapshot does not exist.", null);
        var label = Trim(branch, 200);
        if (label is not null && !System.Text.RegularExpressions.Regex.IsMatch(label, @"^[\p{L}\p{N}][\p{L}\p{N} ._/\-()]*$"))
            return new(false, "A branch label may contain letters, digits, spaces and . _ / - ( ) only.", null);
        var subject = snapshotId.ToString();
        var current = await db.SourceDecisions.AsNoTracking().Where(d => d.Kind == DeclaredBranchKind && d.Subject == subject)
            .OrderByDescending(d => d.DecidedAt).ThenByDescending(d => d.Id).FirstOrDefaultAsync(ct);
        if (string.Equals(current?.Value, label, StringComparison.Ordinal) || (current is null && label is null))
            return new(false, "Unchanged.", current is null ? null : View(current));
        var record = new SourceDecisionRecord
        {
            Id = Guid.NewGuid(), Kind = DeclaredBranchKind, Subject = subject, SnapshotId = snapshotId, Value = label, SnapshotLabel = snapshot.Archive.FileName,
            ContentFingerprint = ContentOf(snapshot)[..Math.Min(64, ContentOf(snapshot).Length)], DecidedAt = After(current), DecidedBy = actor,
        };
        db.SourceDecisions.Add(record);
        await db.SaveChangesAsync(ct);
        return new(true, label is null ? "Declared branch label removed." : $"Declared branch label \"{label}\" recorded (not Git evidence).", View(record));
    }

    /// <summary>Decision history of one logical source (its baselines and its snapshots' declared labels), newest first.</summary>
    public async Task<IReadOnlyList<SourceDecisionView>> HistoryAsync(string repositoryKey, CancellationToken ct)
    {
        var snapshotIds = (await store.ListSourceAnalysisAsync(Window, ct)).Where(s => KeyOf(s) == repositoryKey).Select(s => s.Id.ToString()).ToHashSet();
        var rows = await db.SourceDecisions.AsNoTracking().Where(d => (d.Kind == BaselineKind && d.Subject == repositoryKey) || d.Kind == DeclaredBranchKind).ToListAsync(ct);
        return [.. rows.Where(r => r.Kind == BaselineKind || snapshotIds.Contains(r.Subject)).OrderByDescending(r => r.DecidedAt).ThenByDescending(r => r.Id).Select(View)];
    }

    /// <summary>Now, but strictly after the subject's current decision, so "newest" is unambiguous even for decisions within one clock tick.</summary>
    private static DateTimeOffset After(SourceDecisionRecord? current)
    {
        var now = DateTimeOffset.UtcNow;
        return current is not null && now <= current.DecidedAt ? current.DecidedAt.AddTicks(1) : now;
    }

    private static SourceDecisionView View(SourceDecisionRecord r) => new(r.Id, r.Kind, r.Subject, r.SnapshotId, r.Value, r.SnapshotLabel, r.ContentFingerprint, r.DecidedAt, r.DecidedBy, r.Reason);

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length > max ? value.Trim()[..max] : value.Trim();
}
