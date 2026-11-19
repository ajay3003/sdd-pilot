using BirkNext.GitSources;

namespace BirkNext.Web.Services;

/// <summary>
/// Git Sources (Git-only) in words: whether Git is installed, which credential helper signs in, where a repository comes from and what its last
/// fetch said. A missing Git is a prerequisite (setup), never a failure; a failed fetch belongs to its repository, not to the installation.
/// </summary>
public static class GitSourcesPresentation
{
    /// <summary>Setup still needed on this computer (Git not found): shown as "not set up", never as an issue.</summary>
    public static IReadOnlyList<string> Prerequisites(GitConnectionStatus status) =>
        status.Installation is { Found: true } ? [] : [status.Installation?.Problem ?? GitSourcePolicy.Describe(GitFailureKind.GitMissing)];

    /// <summary>Failures of the installation itself (Git was found but could not start). Repository fetch failures are shown per repository.</summary>
    public static IReadOnlyList<string> Failures(GitConnectionStatus status) =>
        status.Installation is { Found: false, Path: not null } i ? [i.Problem ?? "Git could not be started."] : [];

    public static string HealthSummary(GitConnectionStatus status) => status.Installation switch
    {
        { Found: true } i => $"Git {i.Version}{(i.UsesCredentialManager ? " · Git Credential Manager" : i.CredentialHelpers.Count == 0 ? " · no credential helper" : $" · {string.Join(", ", i.CredentialHelpers)}")}",
        { Path: not null } => "Git found but could not be started",
        _ => "Git not found",
    };

    /// <summary>How sign-in works with this installation (no credential ever passes through BirkNext).</summary>
    public static string CredentialText(GitInstallationView installation) =>
        installation.UsesCredentialManager
            ? "Git Credential Manager signs in to HTTPS repositories in its own window when a repository needs it. BirkNext never sees the password or token."
            : installation.CredentialHelpers.Count == 0
                ? "No credential helper is configured. Public repositories, local folders and SSH keys work; private HTTPS repositories need Git Credential Manager (included with Git for Windows)."
                : $"Git uses the credential helper {string.Join(", ", installation.CredentialHelpers)}. BirkNext never sees the password or token.";

    public static string KindLabel(GitSourceLink link) => link.EffectiveKind == "Local" ? "Local folder" : "Git URL";

    /// <summary>Where the repository comes from: the local folder, or the URL without any credential.</summary>
    public static string Location(GitSourceLink link) =>
        link.EffectiveKind == "Local" ? link.LocalPath ?? "" : GitUrlPolicy.Redact(link.EffectiveRemoteUrl ?? "");

    public static string FetchText(GitSourceLink link, DateTimeOffset now) =>
        link.LastFetchFailure is { } failure ? $"Fetch failed: {link.LastFetchProblem ?? GitSourcePolicy.Describe(failure)}"
        : link.LastFetchedAt is { } at ? $"Fetched {Ago(at, now)}"
        : "Not fetched yet";

    public static string FetchTone(GitSourceLink link) => link.LastFetchFailure is not null ? "bad" : link.LastFetchedAt is null ? "pending" : "ok";

    /// <summary>What a failed fetch asks the person to do (the fix is in Git, the network or the URL, never in BirkNext settings).</summary>
    public static string? FetchHint(GitFailureKind? failure) => failure switch
    {
        GitFailureKind.SignInRequired or GitFailureKind.AuthenticationCancelled =>
            "Fetch again and complete the Git Credential Manager sign-in, or run git fetch once in a terminal to sign in.",
        GitFailureKind.AccessDenied => "Your account cannot read this repository. Ask its owner for read access.",
        GitFailureKind.HostKeyNotTrusted => "Connect once with ssh or git in a terminal to trust the host key, then fetch again.",
        GitFailureKind.RepositoryNotFound => "Check the URL. A private repository you cannot read also looks like this.",
        GitFailureKind.Unreachable => "Check the network, VPN or proxy, then fetch again.",
        GitFailureKind.TimedOut => "The fetch took longer than 10 minutes. Try again on a faster connection.",
        GitFailureKind.GitMissing => "Install Git for Windows, then restart BirkNext.",
        GitFailureKind.NotARepository => "The folder is no longer a Git repository. Remove this repository and add it again.",
        _ => null,
    };

    /// <summary>A local repository's state as found on disk (uncommitted changes are not imported: only commits are).</summary>
    public static string LocalState(GitLocalRepository r) =>
        string.Join(" · ", new[]
        {
            r.Detached ? "detached HEAD" : r.Branch is { } b ? $"on {b}" : null,
            r.HeadCommit is { Length: >= 8 } h ? h[..8] : "no commits",
            r.Dirty ? "uncommitted changes (not imported)" : null,
            r.IsWorktree ? "worktree" : null,
        }.Where(p => p is not null));

    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var d = now - at;
        return d < TimeSpan.FromMinutes(1) ? "just now"
            : d < TimeSpan.FromHours(1) ? $"{(int)d.TotalMinutes} min ago"
            : d < TimeSpan.FromDays(1) ? $"{(int)d.TotalHours} h ago"
            : at.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}
