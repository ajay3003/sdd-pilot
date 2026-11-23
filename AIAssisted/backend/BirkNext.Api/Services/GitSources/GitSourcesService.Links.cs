using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.GitSources;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.GitSources;

/// <summary>Records a committed Git import on its repository links (called by Project Import after the source snapshot is settled).</summary>
public interface IGitImportRecorder
{
    void Record(GitSourceRevision revision, Guid? snapshotId, string importId);
}

public sealed partial class GitSourcesService : IGitImportRecorder
{
    /// <summary>Changes the branch, pinned commit and roles of a link (the repository identity and its history stay).</summary>
    /// <param name="selectedBranches">The branches offered for this repository (null = unchanged, empty = every branch). When the current branch is
    /// not among them, the current branch becomes the first chosen one, so pages never default to a branch nobody can pick.</param>
    public GitSourceLink UpdateLink(string linkId, string environmentId, string? branch, string? pinnedCommit, IReadOnlyList<string>? roles, IReadOnlyList<string>? selectedBranches = null)
    {
        if (branch is { Length: > 0 } b && !GitUrlPolicy.IsBranchName(GitSourcePolicy.BranchName(b))) throw new GitProviderException(GitFailureKind.BranchNotFound, "not a valid branch name");
        List<string>? selected = selectedBranches is null ? null
            : [.. selectedBranches.Select(s => GitSourcePolicy.BranchName(s ?? "")).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)];
        if (selected is { Count: > 500 }) throw new GitProviderException(GitFailureKind.BranchNotFound, "at most 500 branches can be selected");
        if (selected?.FirstOrDefault(s => !GitUrlPolicy.IsBranchName(s)) is { } bad) throw new GitProviderException(GitFailureKind.BranchNotFound, $"\"{bad}\" is not a valid branch name");
        if (pinnedCommit is { Length: > 0 } pin && !GitSourcePolicy.IsCommitSha(pin.Trim().ToLowerInvariant())) throw new GitProviderException(GitFailureKind.RevisionNotFound);
        GitSourceLink? updated = null;
        store.Update(s => s with
        {
            Links = [.. s.Links.Select(l => l.Id != linkId || l.EnvironmentId != environmentId ? l : updated = l with
            {
                Branch = CurrentBranch(branch is { Length: > 0 } nb ? GitSourcePolicy.BranchName(nb) : l.Branch, selected ?? l.SelectedBranches),
                SelectedBranches = selected ?? l.SelectedBranches,
                PinnedCommit = pinnedCommit is null ? l.PinnedCommit : pinnedCommit.Length == 0 ? null : pinnedCommit.Trim().ToLowerInvariant(),
                Roles = roles is null ? l.Roles : [.. roles.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
            })],
        });
        return updated ?? throw new GitProviderException(GitFailureKind.RepositoryNotFound, "no such link");
    }

    /// <summary>The current branch, unless a selection leaves it out: then the first selected branch.</summary>
    private static string CurrentBranch(string current, IReadOnlyList<string> selected) =>
        selected.Count == 0 || selected.Contains(current, StringComparer.Ordinal) ? current : selected[0];

    /// <summary>Removes the link only: the BirkNext-owned cache may stay, imported snapshots, reports and Project Evidence bindings stay.</summary>
    public bool Unlink(string linkId, string environmentId)
    {
        var removed = false;
        store.Update(s =>
        {
            removed = s.Links.Any(l => l.Id == linkId && l.EnvironmentId == environmentId);
            return s with { Links = [.. s.Links.Where(l => !(l.Id == linkId && l.EnvironmentId == environmentId))] };
        });
        return removed;
    }

    /// <summary>A committed Git import is appended to the importing project's link of that repository (history is immutable; the same commit twice is
    /// one entry). Another project's link of the same repository is never changed. Provenance from before 2026-11-19 carries no project and is
    /// recorded on every link of the repository, as it was then.</summary>
    public void Record(GitSourceRevision revision, Guid? snapshotId, string importId)
    {
        store.Update(s => s with
        {
            Links = [.. s.Links.Select(l =>
                !string.Equals(l.Organization, revision.Organization, StringComparison.OrdinalIgnoreCase) || !string.Equals(l.RepositoryId, revision.RepositoryId, StringComparison.OrdinalIgnoreCase)
                || (revision.EnvironmentId is { } project && !string.Equals(l.EnvironmentId, project, StringComparison.Ordinal))
                || l.Imports.Any(i => i.CommitSha == revision.CommitSha && i.SnapshotId == snapshotId) ? l
                : l with
                {
                    Imports = [.. l.Imports, new GitLinkImport(revision.CommitSha, revision.Branch, snapshotId, importId, _clock.GetUtcNow())],
                    Repository = revision.Repository,
                    LastCheck = l.LastCheck is { } c ? c with { UpdateAvailable = c.RemoteCommit is { } r && r != revision.CommitSha } : null,
                })],
        });
    }

    /// <summary>
    /// "Check for updates": fetch latest (explicit, read-only against the remote), then the linked branch's head (or the pinned commit) against the
    /// last imported commit. Nothing is imported; the chosen branch never follows a changed default branch.
    /// </summary>
    public async Task<GitSourceLink> CheckForUpdatesAsync(string linkId, string environmentId, CancellationToken ct)
    {
        var link = await FetchAsync(linkId, environmentId, ct);
        GitUpdateCheck check;
        if (link.LastFetchFailure is { } failure)
            check = new(null, null, _clock.GetUtcNow(), false, GitSourcePolicy.Describe(failure), failure);
        else if (link.PinnedCommit is { } pinned)
            check = new(pinned, null, _clock.GetUtcNow(), link.LastImport?.CommitSha != pinned);
        else
        {
            var branch = (await BranchesAsync(linkId, environmentId, ct)).FirstOrDefault(b => b.Name == link.Branch);
            check = branch is null
                ? new(null, null, _clock.GetUtcNow(), false, GitSourcePolicy.Describe(GitFailureKind.BranchNotFound), GitFailureKind.BranchNotFound)
                : new(branch.CommitSha, branch.CommittedAt, _clock.GetUtcNow(), link.LastImport?.CommitSha != branch.CommitSha);
        }
        GitSourceLink? updated = null;
        store.Update(s => s with
        {
            Links = [.. s.Links.Select(l => l.Id == linkId && l.EnvironmentId == environmentId ? updated = l with { LastCheck = check } : l)],
        });
        return updated ?? link with { LastCheck = check };
    }

    /// <summary>The person's decision about an earlier (ZIP) snapshot: associate it with the repository (its ZIP provenance stays) or keep it separate.</summary>
    public GitSourceLink? Decide(string linkId, string environmentId, GitAssociationRequest request)
    {
        GitSourceLink? updated = null;
        store.Update(s => s with
        {
            Links = [.. s.Links.Select(l => l.Id != linkId || l.EnvironmentId != environmentId ? l : updated = l with
            {
                AssociatedSnapshots = request.Associate ? [.. l.AssociatedSnapshots.Append(request.SnapshotId).Distinct()] : [.. l.AssociatedSnapshots.Where(x => x != request.SnapshotId)],
                KeptSeparate = request.Associate ? [.. l.KeptSeparate.Where(x => x != request.SnapshotId)] : [.. l.KeptSeparate.Append(request.SnapshotId).Distinct()],
            })],
        });
        return updated;
    }

    /// <summary>
    /// Earlier imported snapshots (without Git provenance) that appear to be this repository, strongest first: the same archive bytes, the same
    /// commit recorded in the archive, or the same project-file structure (a review candidate). Names alone are never evidence; nothing is merged.
    /// </summary>
    public async Task<IReadOnlyList<GitSourceMatch>> MatchesAsync(string linkId, string environmentId, CancellationToken ct)
    {
        var link = Find(linkId, environmentId);
        using var scope = scopes.CreateScope();
        var snapshots = await scope.ServiceProvider.GetRequiredService<IqrSourceStore>().ListSourceAnalysisAsync(200, ct);
        var own = snapshots.Where(s => link.Imports.Any(i => i.SnapshotId == s.Id)).ToList();
        return Matches(link, own, snapshots.Where(s => s.ProjectImport?.Git is null && own.All(o => o.Id != s.Id)).ToList());
    }

    internal static IReadOnlyList<GitSourceMatch> Matches(GitSourceLink link, IReadOnlyList<IqrSourceSnapshot> own, IReadOnlyList<IqrSourceSnapshot> candidates)
    {
        if (own.Count == 0) return [];
        var shas = own.Select(s => s.Archive.Sha256).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commits = link.Imports.Select(i => i.CommitSha).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var structure = Structure(own[^1]);
        var matches = new List<GitSourceMatch>();
        foreach (var c in candidates)
        {
            string? Decision() => link.AssociatedSnapshots.Contains(c.Id) ? "Associated" : link.KeptSeparate.Contains(c.Id) ? "Kept separate" : null;
            if (shas.Contains(c.Archive.Sha256))
                matches.Add(new(c.Id, c.Archive.FileName, c.AnalyzedAt, GitSourceMatchStrength.SameContent, [$"Identical archive content (SHA-256 {c.Archive.Sha256[..Math.Min(12, c.Archive.Sha256.Length)]}…)"], Decision()));
            else if (c.Commit is { Length: 40 } commit && commits.Contains(commit))
                matches.Add(new(c.Id, c.Archive.FileName, c.AnalyzedAt, GitSourceMatchStrength.SameCommit, [$"The archive records commit {commit[..8]}, which was imported from this repository"], Decision()));
            else if (structure.Count >= 2 && Structure(c) is { Count: >= 2 } theirs
                     && (double)structure.Intersect(theirs).Count() / structure.Union(theirs).Count() is var jaccard and >= 0.8)
                matches.Add(new(c.Id, c.Archive.FileName, c.AnalyzedAt, GitSourceMatchStrength.PossibleSameStructure,
                    [$"{structure.Intersect(theirs).Count()} of {structure.Union(theirs).Count()} project files at the same paths ({jaccard:P0}) — review before associating"], Decision()));
        }
        return [.. matches.OrderBy(m => m.Strength).ThenByDescending(m => m.AnalyzedAt)];
    }

    /// <summary>Project files by path below the archive's root folder (the root folder name differs between a ZIP export and a Git archive).</summary>
    private static HashSet<string> Structure(IqrSourceSnapshot s) =>
        s.Projects.Select(p => p.Path.Replace('\\', '/')).Select(p => p.Contains('/') && s.Projects.All(x => x.Path.Replace('\\', '/').Split('/')[0] == p.Split('/')[0]) ? p[(p.IndexOf('/') + 1)..] : p)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
