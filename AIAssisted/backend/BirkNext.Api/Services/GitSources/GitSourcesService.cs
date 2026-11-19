using System.Globalization;
using System.Text.Json;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.GitSources;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.GitSources;

/// <summary>
/// Safe Git Sources settings (linked repositories, their branches, imports and checks) in one JSON file of this BirkNext instance. It never holds a
/// password, PAT or token: Git and its credential helper own authentication.
/// </summary>
public sealed class GitSourceSettingsStore(string? directory = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext"), "git-sources.json");

    private sealed record Stored(GitSourceSettings Settings, GitTestResult? LastTest);

    public (GitSourceSettings Settings, GitTestResult? LastTest) Read()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return (new(), null);
                var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path), Json);
                return (stored?.Settings ?? new(), stored?.LastTest);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return (new(), null); }
        }
    }

    public void Write(GitSourceSettings settings, GitTestResult? lastTest)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(settings, lastTest), Json));
            File.Move(temp, _path, overwrite: true);
        }
    }

    public string Folder => Path.GetDirectoryName(_path)!;

    /// <summary>Read-modify-write of the settings under one lock (links change concurrently with fetches and imports).</summary>
    public GitSourceSettings Update(Func<GitSourceSettings, GitSourceSettings> change)
    {
        lock (_gate)
        {
            var (settings, lastTest) = Read();
            var updated = change(settings);
            Write(updated, lastTest);
            return updated;
        }
    }
}

/// <summary>
/// Git Sources, Git-only (2026-11-19). The person's own Git (Git for Windows) and its credential helper (Git Credential Manager, or SSH keys) do the
/// connecting and authenticating; BirkNext stores no credential, reads none and has no Microsoft Entra app registration. Repositories are added
/// from a local clone (a chosen folder or a bounded folder scan) or a Git URL. Every remote operation is an explicit action (add, fetch latest,
/// check for updates, import) and is read-only against the remote: Git fetches into a BirkNext-owned bare repository (hooks disabled), a commit is
/// exported with git archive from that repository and staged through the same Project Import preview as a chosen ZIP. A person's working tree is
/// never checked out, reset, cleaned, rebased, merged, pulled or reconfigured, and nothing is ever pushed.
/// </summary>
public sealed partial class GitSourcesService(IGitCli git, GitSourceSettingsStore store, IServiceScopeFactory scopes, ProjectImportStagingStore staging,
    ILogger<GitSourcesService> logger, TimeProvider? clock = null)
{
    public const string Provider = "Git";
    public const string LocalOrganization = "local";
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromMinutes(2);
    /// <summary>Network operations: long enough for a Git Credential Manager sign-in window, bounded so nothing waits forever.</summary>
    public static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(10);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // ── Installation and status ──────────────────────────────────────────────────────────────────────────────────────

    public GitInstallationView Installation()
    {
        var i = git.Detect();
        return new(i.Found, i.Path, i.Version, i.CredentialHelpers, i.Problem);
    }

    /// <summary>Git readiness and the linked repositories. Never contacts a remote.</summary>
    public Task<GitConnectionStatus> StatusAsync(CancellationToken ct)
    {
        var installation = Installation();
        var settings = store.Read().Settings;
        return Task.FromResult(new GitConnectionStatus
        {
            State = installation.Found ? GitConnectionState.Connected : GitConnectionState.NotConfigured,
            Installation = installation,
            Message = !installation.Found ? installation.Problem ?? GitSourcePolicy.Describe(GitFailureKind.GitMissing)
                : installation.UsesCredentialManager ? $"Git {installation.Version} with Git Credential Manager: HTTPS sign-in happens in Git's own window when a repository needs it."
                : installation.CredentialHelpers.Count == 0 ? $"Git {installation.Version}. No credential helper is configured: HTTPS repositories that need a sign-in will fail; SSH keys and public repositories work."
                : $"Git {installation.Version} with credential helper {string.Join(", ", installation.CredentialHelpers)}.",
            Requirements = installation.Found ? [] : [GitSourcePolicy.Describe(GitFailureKind.GitMissing)],
            TrackedBranches = settings.TrackedBranches,
        });
    }

    // ── Local repositories ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Git repositories below one folder the person chose (never the whole computer): at most <see cref="GitScanRequest.MaxDepth"/> levels
    /// (≤ 5), at most 3 000 folders and 200 repositories; symbolic links and junctions are not followed; a repository's own subfolders are not
    /// searched. Read-only: each repository is inspected without changing it.
    /// </summary>
    public async Task<GitScanResult> ScanAsync(GitScanRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Folder) || !Path.IsPathFullyQualified(request.Folder) || !Directory.Exists(request.Folder))
            return new([], 0, false, "Choose an existing folder (a full path).");
        var root = Path.GetFullPath(request.Folder);
        var depth = Math.Clamp(request.MaxDepth, 0, 5);
        var found = new List<string>();
        var visited = 0;
        var truncated = false;
        var queue = new Queue<(string Path, int Depth)>([(root, 0)]);
        while (queue.Count > 0 && !ct.IsCancellationRequested)
        {
            var (dir, level) = queue.Dequeue();
            if (++visited > 3000 || found.Count >= 200) { truncated = true; break; }
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))) { found.Add(dir); continue; }
            if (level >= depth) continue;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(dir); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { continue; }
            foreach (var child in children)
            {
                try
                {
                    var info = new DirectoryInfo(child);
                    if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // never leave the chosen folder
                    if (info.Name is "node_modules" or "bin" or "obj" or ".vs") continue;
                    queue.Enqueue((child, level + 1));
                }
                catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
            }
        }
        var repositories = new List<GitLocalRepository>();
        foreach (var path in found) repositories.Add(await InspectLocalAsync(path, ct));
        return new(repositories, visited, truncated, found.Count == 0 ? "No Git repository was found in that folder (searched " + depth + " levels deep)." : null);
    }

    /// <summary>One local repository, read without changing it (no index refresh, no fsmonitor, no hooks).</summary>
    public async Task<GitLocalRepository> InspectLocalAsync(string folder, CancellationToken ct)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        if (!Directory.Exists(folder)) return new(name, folder, null, false, null, null, false, [], false, GitSourcePolicy.Describe(GitFailureKind.NotARepository));
        var top = await git.RunAsync("rev-parse", ["--show-toplevel", "--absolute-git-dir", "--git-common-dir"], folder, GitConfigMode.User, LocalTimeout, ct);
        if (!top.Ok)
        {
            var kind = GitCli.Classify(top);
            return new(name, folder, null, false, null, null, false, [], false, GitSourcePolicy.Describe(kind == GitFailureKind.RepositoryNotFound ? GitFailureKind.NotARepository : kind));
        }
        var lines = top.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var root = lines.ElementAtOrDefault(0)?.Replace('/', Path.DirectorySeparatorChar) ?? folder;
        var isWorktree = lines.Length >= 3 && !string.Equals(Path.GetFullPath(lines[1]), Path.GetFullPath(Path.Combine(root, lines[2])), StringComparison.OrdinalIgnoreCase)
            && !lines[2].Equals(".git", StringComparison.Ordinal);
        var branch = await git.RunAsync("symbolic-ref", ["--short", "-q", "HEAD"], root, GitConfigMode.User, LocalTimeout, ct);
        var head = await git.RunAsync("log", ["-1", "--format=%H%x09%cI"], root, GitConfigMode.User, LocalTimeout, ct);
        var status = await git.RunAsync("status", ["--porcelain=v1", "--untracked-files=no"], root, GitConfigMode.User, LocalTimeout, ct);
        var remotes = await git.RunAsync("remote", ["-v"], root, GitConfigMode.User, LocalTimeout, ct);
        var headParts = head.Ok ? head.StdOut.Trim().Split('\t') : [];
        return new(Path.GetFileName(root.TrimEnd('\\', '/')), root, branch.Ok && branch.StdOut.Trim().Length > 0 ? branch.StdOut.Trim() : null,
            Detached: !branch.Ok || branch.StdOut.Trim().Length == 0,
            HeadCommit: headParts.ElementAtOrDefault(0) is { Length: 40 } sha ? sha : null,
            HeadCommittedAt: headParts.ElementAtOrDefault(1) is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
            Dirty: status.Ok && status.StdOut.Trim().Length > 0,
            Remotes: [.. remotes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t', ' ')).Where(p => p.Length >= 2)
                .Select(p => $"{p[0]} {GitUrlPolicy.Redact(p[1])}").Distinct(StringComparer.Ordinal)],
            IsWorktree: isWorktree);
    }

    // ── Adding repositories ──────────────────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<GitSourceLink> Links(string? environmentId) =>
        [.. store.Read().Settings.Links.Where(l => environmentId is null || string.Equals(l.EnvironmentId, environmentId, StringComparison.Ordinal))
            .OrderBy(l => l.Repository, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The BirkNext-owned bare repository a link fetches into (never the person's working repository).</summary>
    public string CacheOf(GitSourceLink link) => Path.Combine(store.Folder, "git-repositories", GitUrlPolicy.RepositoryId(link.Id));

    /// <summary>
    /// Adds a local repository (its root folder) or a Git URL as evidence for a BirkNext project, then fetches its branches into BirkNext-owned
    /// storage. An existing link of the same repository and project keeps its imports, checks and decisions.
    /// </summary>
    public async Task<GitSourceLink> AddRepositoryAsync(GitAddRepositoryRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentId)) throw new GitSettingsException("Choose the BirkNext project (Target Environment) the repository is evidence for.");
        if (string.IsNullOrWhiteSpace(request.LocalPath) == string.IsNullOrWhiteSpace(request.RemoteUrl))
            throw new GitSettingsException("Choose either a local repository folder or a Git URL.");
        GitSourceLink link;
        if (request.LocalPath is { Length: > 0 } path)
        {
            if (!Path.IsPathFullyQualified(path)) throw new GitSettingsException("Enter the full path of the repository folder.");
            var local = await InspectLocalAsync(Path.GetFullPath(path), ct);
            if (local.Problem is { } problem) throw new GitSettingsException(problem);
            var canonical = "local:" + local.Path.Replace('\\', '/').ToLowerInvariant();
            link = new GitSourceLink
            {
                Kind = "Local", LocalPath = local.Path, OrganizationUrl = canonical, Organization = LocalOrganization, RepositoryId = GitUrlPolicy.RepositoryId(canonical),
                Repository = local.Name, Project = "", Branch = request.Branch is { Length: > 0 } b ? b : local.Branch ?? "", DefaultBranch = local.Branch,
            };
        }
        else
        {
            var (url, problem) = GitUrlPolicy.Normalize(request.RemoteUrl);
            if (url is null) throw new GitProviderException(GitFailureKind.UnsupportedUrl, problem);
            link = new GitSourceLink
            {
                Kind = "Remote", RemoteUrl = url, OrganizationUrl = url, Organization = HostOf(url), RepositoryId = GitUrlPolicy.RepositoryId(url),
                Repository = GitUrlPolicy.NameOf(url), Project = "", Branch = request.Branch ?? "", WebUrl = url.StartsWith("https://", StringComparison.Ordinal) ? url : null,
            };
        }
        if (request.Branch is { Length: > 0 } chosen && !GitUrlPolicy.IsBranchName(chosen)) throw new GitProviderException(GitFailureKind.BranchNotFound, "not a valid branch name");
        var id = GitSourceLink.IdentityOf(link.OrganizationUrl, link.RepositoryId);
        var roles = (request.Roles ?? []).Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        GitSourceLink? stored = null;
        store.Update(s =>
        {
            var existing = s.Links.FirstOrDefault(l => l.Id == id && l.EnvironmentId == request.EnvironmentId);
            stored = (existing ?? new GitSourceLink { Id = id, EnvironmentId = request.EnvironmentId, LinkedAt = _clock.GetUtcNow() }) with
            {
                Kind = link.Kind, LocalPath = link.LocalPath, RemoteUrl = link.RemoteUrl, OrganizationUrl = link.OrganizationUrl, Organization = link.Organization,
                RepositoryId = link.RepositoryId, Repository = link.Repository, Project = link.Project, WebUrl = link.WebUrl,
                Branch = link.Branch.Length > 0 ? link.Branch : existing?.Branch ?? "", DefaultBranch = link.DefaultBranch ?? existing?.DefaultBranch,
                Roles = roles.Count > 0 ? roles : existing?.Roles ?? [], BirkNextProject = request.BirkNextProject ?? existing?.BirkNextProject,
            };
            return s with { Links = [.. s.Links.Where(l => !(l.Id == id && l.EnvironmentId == request.EnvironmentId)), stored] };
        });
        return await FetchAsync(stored!.Id, request.EnvironmentId, ct);
    }

    private static string HostOf(string url) => url.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host
        : url.Split('@', 2).Last().Split(':', 2)[0];

    // ── Fetch, branches, import ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetch latest: the repository's branches into the BirkNext-owned bare repository (created on first use). From a URL with the person's Git
    /// configuration (credential helper; a sign-in window may appear); from a local repository by its path (its working tree, index and refs are
    /// not changed). Never pull, merge, rebase or push. The outcome (or the classified failure) is recorded on the link.
    /// </summary>
    public async Task<GitSourceLink> FetchAsync(string linkId, string environmentId, CancellationToken ct)
    {
        var link = Find(linkId, environmentId);
        var cache = CacheOf(link);
        GitFailureKind? failure = null;
        string? defaultBranch = link.DefaultBranch;
        try
        {
            if (!Directory.Exists(Path.Combine(cache, "objects")))
            {
                Directory.CreateDirectory(cache);
                var init = await git.RunAsync("init", ["--bare", "--quiet", "--template=", cache], null, GitConfigMode.Isolated, LocalTimeout, ct);
                if (!init.Ok) throw new GitProviderException(GitFailureKind.Unexpected, "the local cache could not be created");
            }
            if (link.EffectiveKind == "Local")
            {
                var source = link.LocalPath ?? throw new GitProviderException(GitFailureKind.NotARepository);
                if (!Directory.Exists(source)) throw new GitProviderException(GitFailureKind.NotARepository);
                var fetch = await git.RunAsync("fetch", ["--quiet", "--prune", "--no-tags", "--end-of-options", source, "+refs/heads/*:refs/heads/*"], cache, GitConfigMode.User,
                    LocalTimeout, ct, configOverrides: ["protocol.file.allow=always"]);
                if (!fetch.Ok) throw new GitProviderException(GitCli.Classify(fetch));
                var current = await git.RunAsync("symbolic-ref", ["--short", "-q", "HEAD"], source, GitConfigMode.User, LocalTimeout, ct);
                if (current.Ok && current.StdOut.Trim().Length > 0) defaultBranch = current.StdOut.Trim();
            }
            else
            {
                var url = link.EffectiveRemoteUrl is { } raw && GitUrlPolicy.Normalize(raw).Url is { } normalized ? normalized
                    : throw new GitProviderException(GitFailureKind.UnsupportedUrl);
                var head = await git.RunAsync("ls-remote", ["--symref", "--end-of-options", url, "HEAD"], cache, GitConfigMode.User, NetworkTimeout, ct,
                    configOverrides: ["protocol.file.allow=never"]);
                if (!head.Ok) throw new GitProviderException(GitCli.Classify(head));
                var symref = head.StdOut.Split('\n').FirstOrDefault(l => l.StartsWith("ref: refs/heads/", StringComparison.Ordinal));
                if (symref is not null) defaultBranch = symref["ref: refs/heads/".Length..].Split('\t')[0].Trim();
                var fetch = await git.RunAsync("fetch", ["--quiet", "--prune", "--no-tags", "--end-of-options", url, "+refs/heads/*:refs/heads/*"], cache, GitConfigMode.User,
                    NetworkTimeout, ct, configOverrides: ["protocol.file.allow=never"]);
                if (!fetch.Ok) throw new GitProviderException(GitCli.Classify(fetch));
            }
        }
        catch (GitProviderException e) { failure = e.Kind; }
        var at = _clock.GetUtcNow();
        GitSourceLink? updated = null;
        store.Update(s => s with
        {
            Links = [.. s.Links.Select(l => l.Id == linkId && l.EnvironmentId == environmentId ? updated = l with
            {
                LastFetchedAt = failure is null ? at : l.LastFetchedAt, LastFetchFailure = failure, LastFetchProblem = failure is { } f ? GitSourcePolicy.Describe(f) : null,
                DefaultBranch = defaultBranch, Branch = l.Branch.Length > 0 ? l.Branch : defaultBranch ?? "",
            } : l)],
        });
        logger.LogInformation("Git Sources fetch for repository {Repository}: {Outcome}", link.Repository, failure?.ToString() ?? "fetched");
        return updated ?? link;
    }

    /// <summary>The branches Git fetched (from the BirkNext-owned repository; no remote call), newest commit first.</summary>
    public async Task<IReadOnlyList<GitBranch>> BranchesAsync(string linkId, string environmentId, CancellationToken ct)
    {
        var link = Find(linkId, environmentId);
        var cache = CacheOf(link);
        if (!Directory.Exists(Path.Combine(cache, "objects"))) return [];
        var r = await git.RunAsync("for-each-ref", ["--format=%(refname:short)%09%(objectname)%09%(committerdate:iso-strict)%09%(authorname)%09%(subject)", "refs/heads"],
            cache, GitConfigMode.Isolated, LocalTimeout, ct);
        if (!r.Ok) throw new GitProviderException(GitCli.Classify(r));
        return GitSourcePolicy.LatestFirst(r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')).Where(p => p.Length >= 2).Select(p =>
            new GitBranch(p[0], p[1], p.ElementAtOrDefault(2) is { } d && DateTimeOffset.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null,
                p.ElementAtOrDefault(3), p.ElementAtOrDefault(4), p[0] == link.DefaultBranch)));
    }

    /// <summary>
    /// Imports one exact commit: the chosen commit (it must exist in the fetched history) or the branch head as last fetched. The commit is exported
    /// with git archive from the BirkNext-owned repository (no global configuration: no filter driver or hook runs) and staged through the same
    /// Project Import preview as a chosen ZIP; the revision (repository, branch, SHA, time) travels with it as provenance.
    /// </summary>
    public async Task<GitImportResult> ImportAsync(GitImportRequest request, CancellationToken ct)
    {
        // The link of this repository in the requesting BirkNext project: never another project's (its branch, pin and history are its own).
        var candidates = store.Read().Settings.Links.Where(l => l.RepositoryId == request.RepositoryId
            && (request.OrganizationUrl is null || string.Equals(l.OrganizationUrl, request.OrganizationUrl, StringComparison.OrdinalIgnoreCase))
            && (request.EnvironmentId is null || string.Equals(l.EnvironmentId, request.EnvironmentId, StringComparison.Ordinal))).ToList();
        if (candidates.Count > 1) throw new GitSettingsException("This repository is linked to more than one BirkNext project. Import it from the project it belongs to.");
        var link = candidates.SingleOrDefault() ?? throw new GitProviderException(GitFailureKind.RepositoryNotFound);
        var cache = CacheOf(link);
        if (!Directory.Exists(Path.Combine(cache, "objects"))) link = await FetchAsync(link.Id, link.EnvironmentId, ct);
        if (link.LastFetchFailure is { } failed && link.LastFetchedAt is null) throw new GitProviderException(failed);
        var branchName = GitSourcePolicy.BranchName(request.Branch is { Length: > 0 } b ? b : link.Branch);
        string sha;
        if (request.Commit is { Length: > 0 } pinned)
        {
            if (!GitSourcePolicy.IsCommitSha(pinned.Trim().ToLowerInvariant())) throw new GitProviderException(GitFailureKind.RevisionNotFound);
            sha = pinned.Trim().ToLowerInvariant();
            var exists = await git.RunAsync("cat-file", ["-e", $"{sha}^{{commit}}"], cache, GitConfigMode.Isolated, LocalTimeout, ct);
            if (!exists.Ok) throw new GitProviderException(GitFailureKind.RevisionNotFound);
        }
        else
        {
            if (!GitUrlPolicy.IsBranchName(branchName)) throw new GitProviderException(GitFailureKind.BranchNotFound);
            var head = await git.RunAsync("rev-parse", ["--verify", "--quiet", "--end-of-options", $"refs/heads/{branchName}^{{commit}}"], cache, GitConfigMode.Isolated, LocalTimeout, ct);
            if (!head.Ok || !GitSourcePolicy.IsCommitSha(head.StdOut.Trim())) throw new GitProviderException(GitFailureKind.BranchNotFound);
            sha = head.StdOut.Trim();
        }
        var when = await git.RunAsync("log", ["-1", "--format=%cI", sha], cache, GitConfigMode.Isolated, LocalTimeout, ct);
        DateTimeOffset? committedAt = when.Ok && DateTimeOffset.TryParse(when.StdOut.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var c) ? c : null;
        using var archive = new MemoryStream();
        var export = await git.RunAsync("archive", ["--format=zip", $"--prefix={Safe(link.Repository)}/", sha], cache, GitConfigMode.Isolated, NetworkTimeout, ct,
            stdout: archive, maxOutputBytes: GitSourcePolicy.MaxArchiveBytes);
        if (!export.Ok) throw new GitProviderException(GitCli.Classify(export));
        var revision = new GitSourceRevision(Provider, link.Organization, link.Project, link.RepositoryId, link.Repository, branchName, sha, committedAt, _clock.GetUtcNow(),
            link.EffectiveRemoteUrl ?? link.LocalPath) { EnvironmentId = link.EnvironmentId };
        var bytes = archive.ToArray();
        using var scope = scopes.CreateScope();
        var preview = scope.ServiceProvider.GetRequiredService<ProjectImportService>().Preview(GitSourcePolicy.ArchiveFileName(revision), bytes, ct);
        if (preview.Preview is { } p) staging.SetGitRevision(p.StagingId, revision);
        logger.LogInformation("Git Sources staged an import. Repository {Repository}; commit {Commit}; archive size {ArchiveBytes}", link.Repository, revision.ShortSha, bytes.Length);
        return new(revision, preview.Preview, preview.Failure?.Message);
    }

    private static string Safe(string name) => new([.. name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')]);

    private GitSourceLink Find(string linkId, string environmentId) =>
        Links(environmentId).FirstOrDefault(l => l.Id == linkId) ?? throw new GitProviderException(GitFailureKind.RepositoryNotFound, "no such link");
}

public sealed class GitSettingsException(string message) : Exception(message);
