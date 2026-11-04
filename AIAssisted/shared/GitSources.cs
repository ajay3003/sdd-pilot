using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BirkNext.GitSources;

// ── Git Sources (Azure DevOps over Microsoft Entra ID, delegated) ──────────────────────────────────────────────────────────
// Git-native source acquisition that converges on the existing Project Import pipeline: a branch is resolved to a commit SHA, the commit is
// downloaded as an archive and previewed / committed exactly like a chosen ZIP, with the revision as provenance. Read-only against the remote.
// No password, PAT or token is ever stored here: tokens stay in the backend's memory (MSAL), these records carry none.

/// <summary>The exact revision an import came from. The commit SHA is the identity; the branch is only how it was chosen.</summary>
public sealed record GitSourceRevision(
    string Provider, string Organization, string Project, string RepositoryId, string Repository, string Branch, string CommitSha, DateTimeOffset? CommittedAt,
    DateTimeOffset RetrievedAt, string? RepositoryUrl = null)
{
    public string ShortSha => CommitSha.Length > 8 ? CommitSha[..8] : CommitSha;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitConnectionState { NotConfigured, NotConnected, SigningIn, AwaitingDeviceCode, Connected, ReconnectRequired, AccessDenied, Failed }

/// <summary>One configuration or connection prerequisite, with what an administrator does about it (never a secret).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitCheckState { Ok, Missing, Invalid, Failed, NotChecked, Info }

public sealed record GitConfigurationCheck(string Key, string Label, GitCheckState State, string Detail, string? AdminAction = null);

public sealed record GitDeviceCode(string UserCode, string VerificationUrl, DateTimeOffset ExpiresOn);

public sealed record GitConnectionStatus
{
    public GitConnectionState State { get; init; }
    public string? Account { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<string> Requirements { get; init; } = [];
    public string? OrganizationUrl { get; init; }
    public GitTestResult? LastTest { get; init; }
    public bool SignInAvailable { get; init; }
    public IReadOnlyList<GitTrackedBranch> TrackedBranches { get; init; } = [];
    /// <summary>Prerequisites in order (app registration, client id, tenant, redirect, sign-in method, delegated permission, Azure DevOps access).</summary>
    public IReadOnlyList<GitConfigurationCheck> Diagnostics { get; init; } = [];
    public bool DeviceCodeAllowed { get; init; }
    public GitDeviceCode? DeviceCode { get; init; }
    public IReadOnlyList<string> Organizations { get; init; } = [];
}

public sealed record GitTrackedBranch(string RepositoryId, string Repository, string Project, string Branch);

/// <summary>Safe settings only (no secret): the organization and the branches the person tracks.</summary>
public sealed record GitSourceSettings
{
    /// <summary>The manually entered organization (advanced; discovery is the normal path).</summary>
    public string? OrganizationUrl { get; init; }
    public List<GitTrackedBranch> TrackedBranches { get; init; } = [];
    /// <summary>Organizations the person selected (canonical URLs), discovered or entered.</summary>
    public List<string> Organizations { get; init; } = [];
    /// <summary>Repositories linked to BirkNext projects (stable Azure DevOps ids; never a name as identity).</summary>
    public List<GitSourceLink> Links { get; init; } = [];
}

public sealed record GitOrganization(string Id, string Name, string Url, bool Discovered);

public sealed record GitProject(string Id, string Name, string? State);

public sealed record GitProjectPage(IReadOnlyList<GitProject> Items, string? Next);

public sealed record GitCommitInfo(string CommitSha, DateTimeOffset? CommittedAt, string? Author, string? Message);

/// <summary>One import of a linked repository: the exact revision and the snapshot it produced (immutable history).</summary>
public sealed record GitLinkImport(string CommitSha, string Branch, Guid? SnapshotId, string? ImportId, DateTimeOffset ImportedAt);

/// <summary>The last explicit update check: the remote revision of the linked branch at that time (never imported by the check).</summary>
public sealed record GitUpdateCheck(string? RemoteCommit, DateTimeOffset? RemoteCommittedAt, DateTimeOffset CheckedAt, bool UpdateAvailable, string? Problem = null);

/// <summary>
/// A repository linked to a BirkNext project. Identity = organization + Azure DevOps repository id (a rename keeps the link). Roles are the
/// person's explicit decision (several allowed, e.g. Infrastructure + Contracts); the branch stays as chosen even when the remote default branch
/// changes; a pinned commit is imported instead of the branch head.
/// </summary>
public sealed record GitSourceLink
{
    public string Id { get; init; } = "";
    public string OrganizationUrl { get; init; } = "";
    public string Organization { get; init; } = "";
    public string? ProjectId { get; init; }
    public string Project { get; init; } = "";
    public string RepositoryId { get; init; } = "";
    /// <summary>Display name as last seen (not identity).</summary>
    public string Repository { get; init; } = "";
    public string? WebUrl { get; init; }
    public string Branch { get; init; } = "";
    public string? PinnedCommit { get; init; }
    public List<string> Roles { get; init; } = [];
    /// <summary>The BirkNext project: the Target Environment profile id it is evidence for, and the project's name as shown.</summary>
    public string EnvironmentId { get; init; } = "";
    public string? BirkNextProject { get; init; }
    public DateTimeOffset LinkedAt { get; init; }
    public List<GitLinkImport> Imports { get; init; } = [];
    public GitUpdateCheck? LastCheck { get; init; }
    /// <summary>Earlier (ZIP) snapshots a person confirmed as this repository's evidence; their provenance stays ZIP.</summary>
    public List<Guid> AssociatedSnapshots { get; init; } = [];
    /// <summary>Candidate snapshots a person decided to keep separate.</summary>
    public List<Guid> KeptSeparate { get; init; } = [];

    public static string IdentityOf(string organizationUrl, string repositoryId) => $"{GitSourcePolicy.OrganizationName(organizationUrl).ToLowerInvariant()}|{repositoryId.ToLowerInvariant()}";
    public GitLinkImport? LastImport => Imports.Count == 0 ? null : Imports[^1];
}

/// <summary>How strongly an earlier imported snapshot appears to be this repository. Names alone are never evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitSourceMatchStrength { SameContent, SameCommit, PossibleSameStructure }

public sealed record GitSourceMatch(Guid SnapshotId, string ArchiveFileName, DateTimeOffset AnalyzedAt, GitSourceMatchStrength Strength, IReadOnlyList<string> Evidence,
    string? Decision);

public sealed record GitLinkRequest(string OrganizationUrl, string RepositoryId, string Branch, string? PinnedCommit, List<string> Roles, string EnvironmentId, string? BirkNextProject);

public sealed record GitAssociationRequest(Guid SnapshotId, bool Associate);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitTestStepState { Verified, Failed, Skipped }

public sealed record GitTestStep(string Name, GitTestStepState State, string Message, long DurationMs);

/// <summary>An explicit, user-triggered connectivity test. It never imports, never creates a snapshot and never writes to the remote.</summary>
public sealed record GitTestResult(bool Ready, string Summary, IReadOnlyList<GitTestStep> Steps, DateTimeOffset TestedAt, string? Organization, int? RepositoryCount);

public sealed record GitRepository(string Id, string Name, string Project, string? DefaultBranch, string? WebUrl, bool IsDisabled)
{
    public string? ProjectId { get; init; }
}

public sealed record GitBranch(string Name, string CommitSha, DateTimeOffset? CommittedAt, string? Author, string? Message, bool IsDefault);

public sealed record GitImportRequest(string RepositoryId, string Branch)
{
    /// <summary>The organization (canonical URL); null = the manually configured one.</summary>
    public string? OrganizationUrl { get; init; }
    /// <summary>A specific revision instead of the branch head (full SHA, validated against the repository).</summary>
    public string? Commit { get; init; }
}

/// <summary>The archive of one commit, staged through the same Project Import preview as a chosen ZIP (commit it with the usual import commit).</summary>
public sealed record GitImportResult(GitSourceRevision Revision, BirkNext.ProjectImport.ProjectImportPreview? Preview, string? Failure);

/// <summary>A diagnostic provider failure: never collapsed into "connection failed".</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitFailureKind { SignInRequired, AccessDenied, OrganizationNotFound, RepositoryNotFound, BranchNotFound, Unreachable, TooLarge, Unexpected, RevisionNotFound, Throttled }

public static partial class GitSourcePolicy
{
    public const string Provider = "AzureDevOps";
    /// <summary>The Azure DevOps resource (delegated): read access as the signed-in person, never more.</summary>
    public const string AzureDevOpsScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";
    public const long MaxArchiveBytes = 512L * 1024 * 1024;

    /// <summary>The organization URL in canonical form (https://dev.azure.com/{org} or https://{org}.visualstudio.com), or null when it is not one.</summary>
    public static string? NormalizeOrganizationUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.Query)) return null;
        var segments = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (u.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
            return segments.Length == 1 && OrgRe().IsMatch(segments[0]) ? $"https://dev.azure.com/{segments[0]}" : null;
        var m = LegacyHostRe().Match(u.Host);
        return m.Success && segments.Length == 0 ? $"https://{m.Groups[1].Value.ToLowerInvariant()}.visualstudio.com" : null;
    }

    public static string OrganizationName(string organizationUrl) =>
        organizationUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? organizationUrl.TrimEnd('/').Split('/')[^1] : new Uri(organizationUrl).Host.Split('.')[0];

    /// <summary>"refs/heads/feature/x" → "feature/x"; any other text unchanged.</summary>
    public static string BranchName(string reference) => reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;

    /// <summary>Latest commit first; branches without a commit date last, then by name.</summary>
    public static IReadOnlyList<GitBranch> LatestFirst(IEnumerable<GitBranch> branches) =>
        [.. branches.OrderBy(b => b.CommittedAt is null).ThenByDescending(b => b.CommittedAt).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>The archive name of a revision (readable; the identity is the SHA in the provenance, never this name).</summary>
    public static string ArchiveFileName(GitSourceRevision r) =>
        $"{Safe(r.Repository)}@{Safe(r.Branch)}-{r.ShortSha}.zip";

    public static bool IsCommitSha(string? sha) => sha is not null && ShaRe().IsMatch(sha);

    public static string Describe(GitFailureKind kind) => kind switch
    {
        GitFailureKind.SignInRequired => "Sign-in required (or the sign-in expired): sign in to Microsoft again.",
        GitFailureKind.AccessDenied => "Microsoft Entra sign-in succeeded, but Azure DevOps denied access for this account.",
        GitFailureKind.OrganizationNotFound => "The Azure DevOps organization was not found.",
        GitFailureKind.RepositoryNotFound => "The repository was not found or is not visible to this account.",
        GitFailureKind.BranchNotFound => "The branch was not found (it may have been deleted or renamed).",
        GitFailureKind.Unreachable => "Azure DevOps could not be reached from this BirkNext instance.",
        GitFailureKind.TooLarge => "The repository archive is larger than BirkNext imports.",
        GitFailureKind.RevisionNotFound => "The chosen commit does not exist in this repository (a full 40-character SHA is required).",
        GitFailureKind.Throttled => "Azure DevOps is rate-limiting requests; wait a moment and try again.",
        _ => "Azure DevOps returned an unexpected response.",
    };

    private static string Safe(string text) => InvalidRe().Replace(text, "-").Trim('-');

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,49}$")]
    private static partial Regex OrgRe();

    [GeneratedRegex(@"^([A-Za-z0-9][A-Za-z0-9-]{0,49})\.visualstudio\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyHostRe();

    [GeneratedRegex(@"^[0-9a-f]{40}$")]
    private static partial Regex ShaRe();

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex InvalidRe();
}
