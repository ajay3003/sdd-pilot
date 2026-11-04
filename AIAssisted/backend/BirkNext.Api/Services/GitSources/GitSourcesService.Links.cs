using System.Text.RegularExpressions;
using BirkNext.Api.Services.AzureEnvironment;
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
    // ── Configuration diagnostics ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The prerequisites in order, each with its state and what an administrator does (never a secret). Static checks read configuration only;
    /// sign-in and Azure DevOps checks reflect the last real attempt (a sign-in failure is classified from the identity platform's error code).
    /// </summary>
    public IReadOnlyList<GitConfigurationCheck> Diagnostics(GitTestResult? lastTest = null)
    {
        var o = azure.Value;
        var checks = new List<GitConfigurationCheck>
        {
            o.Enabled ? new("enabled", "Microsoft sign-in enabled", GitCheckState.Ok, "AzureEnvironment:Enabled is true.")
                : new("enabled", "Microsoft sign-in enabled", GitCheckState.Missing, "AzureEnvironment:Enabled is false, so BirkNext does not sign in to Microsoft.",
                    "Set AzureEnvironment:Enabled to true in the BirkNext.Api configuration."),
            string.IsNullOrWhiteSpace(o.ClientId)
                ? new("client-id", "App registration (client id)", GitCheckState.Missing, "No Entra app registration is configured (AzureEnvironment:ClientId).",
                    "Register a public client app in Microsoft Entra ID (platform \"Mobile and desktop applications\") and set AzureEnvironment:ClientId.")
                : Guid.TryParse(o.ClientId.Trim(), out _)
                    ? new("client-id", "App registration (client id)", GitCheckState.Ok, $"Client id {o.ClientId.Trim()}.")
                    : new("client-id", "App registration (client id)", GitCheckState.Invalid, "AzureEnvironment:ClientId is not a GUID (the Application (client) ID).",
                        "Copy the Application (client) ID from the app registration's Overview."),
            TenantRe().IsMatch(o.Tenant)
                ? new("tenant", "Tenant / authority", GitCheckState.Ok, $"Authority {AzureEnvironmentOptions.AuthorityHost}/{o.Tenant}.")
                : new("tenant", "Tenant / authority", GitCheckState.Invalid, $"\"{o.Tenant}\" is not a tenant id, a verified domain, \"organizations\" or \"common\".",
                    "Set AzureEnvironment:TenantId to the Directory (tenant) ID or a verified domain, or leave it empty for the account's home tenant."),
            Failure("redirect") ?? new("redirect", "Redirect URI", GitCheckState.Info, "The app registration must list http://localhost under \"Mobile and desktop applications\".",
                "Add http://localhost as a redirect URI of the public client platform."),
            browser.Available ? new("method", "Sign-in method", GitCheckState.Ok, "Interactive sign-in in the dedicated BirkNext Edge profile (MFA and Conditional Access apply).")
                : o.AllowDeviceCode ? new("method", "Sign-in method", GitCheckState.Ok, $"Device code (the browser cannot be opened here: {browser.UnavailableReason})")
                : new("method", "Sign-in method", GitCheckState.Missing, browser.UnavailableReason ?? "No sign-in method is available.", "Allow device code (AzureEnvironment:AllowDeviceCode)."),
            Failure("consent") ?? new("consent", "Delegated Azure DevOps permission", GitCheckState.Info,
                "The app registration needs the delegated Azure DevOps permission user_impersonation; some tenants require administrator consent.",
                "API permissions → Add → Azure DevOps → user_impersonation (delegated); grant admin consent if the tenant requires it."),
        };
        if (Failure("sign-in") is { } signIn) checks.Add(signIn);
        bool denied;
        lock (_gate) denied = _accessDenied;
        checks.Add(denied
            ? new("devops", "Azure DevOps access", GitCheckState.Failed, "Signed in, but Azure DevOps refused access for this account.",
                "The account must be a member of the organization with at least Basic or Stakeholder access and read permission on the repositories.")
            : lastTest is { } test
                ? new("devops", "Azure DevOps access", test.Ready ? GitCheckState.Ok : GitCheckState.Failed, test.Ready ? $"Verified {test.TestedAt:yyyy-MM-dd HH:mm} UTC." : test.Summary)
                : new("devops", "Azure DevOps access", GitCheckState.NotChecked, "Not tested yet: a saved organization or a sign-in is not proof of repository access."));
        return checks;
    }

    private GitConfigurationCheck? Failure(string key)
    {
        lock (_gate) return _signInFailure is { } f && f.Key == key ? f : null;
    }

    /// <summary>The identity platform's error code → which prerequisite failed (AADSTS codes; MSAL client codes for cancellation).</summary>
    internal static (bool Cancelled, string Message, GitConfigurationCheck Check) ClassifySignInFailure(Exception e)
    {
        var text = e.Message;
        var code = (e as Microsoft.Identity.Client.MsalException)?.ErrorCode ?? "";
        if (code is "authentication_canceled" or "user_canceled" || e is OperationCanceledException)
            return (true, "Sign-in was cancelled or timed out. Start it again when ready.",
                new("sign-in", "Last sign-in", GitCheckState.Info, "Sign-in was cancelled or timed out."));
        if (text.Contains("AADSTS65001", StringComparison.Ordinal) || text.Contains("AADSTS90094", StringComparison.Ordinal) || text.Contains("AADSTS90095", StringComparison.Ordinal))
            return (false, "Consent is required for the Azure DevOps permission.",
                new("consent", "Delegated Azure DevOps permission", GitCheckState.Failed, "Consent is required for the delegated Azure DevOps permission (AADSTS65001/90094).",
                    "An administrator grants consent for user_impersonation on Azure DevOps in the app registration, or allows user consent."));
        if (text.Contains("AADSTS50011", StringComparison.Ordinal) || text.Contains("AADSTS500113", StringComparison.Ordinal))
            return (false, "The redirect URI is not registered on the app registration.",
                new("redirect", "Redirect URI", GitCheckState.Failed, "The identity platform rejected the redirect URI (AADSTS50011).",
                    "Add http://localhost under \"Mobile and desktop applications\" in the app registration."));
        if (text.Contains("AADSTS700016", StringComparison.Ordinal))
            return (false, "The app registration was not found in this tenant.",
                new("sign-in", "Last sign-in", GitCheckState.Failed, "The client id is not registered in the signed-in tenant (AADSTS700016).",
                    "Check AzureEnvironment:ClientId and AzureEnvironment:TenantId."));
        if (text.Contains("AADSTS90002", StringComparison.Ordinal) || text.Contains("AADSTS50020", StringComparison.Ordinal))
            return (false, "The tenant does not match the account.",
                new("sign-in", "Last sign-in", GitCheckState.Failed, "The tenant was not found or the account is not in it (AADSTS90002/50020).",
                    "Set AzureEnvironment:TenantId to the organization's tenant, or sign in with a work account of that tenant."));
        if (text.Contains("AADSTS53003", StringComparison.Ordinal) || text.Contains("AADSTS50076", StringComparison.Ordinal))
            return (false, "Conditional Access blocked or requires more from this sign-in.",
                new("sign-in", "Last sign-in", GitCheckState.Failed, "Conditional Access blocked the sign-in or requires more (AADSTS53003/50076). BirkNext never bypasses it.",
                    "Sign in from a compliant device/network, or ask the tenant administrator about the policy."));
        return (false, "Sign-in did not complete. Try again; MFA and Conditional Access are handled by Microsoft.",
            new("sign-in", "Last sign-in", GitCheckState.Failed, $"Sign-in did not complete ({e.GetType().Name})."));
    }

    [GeneratedRegex(@"^(organizations|common|consumers|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+)$")]
    private static partial Regex TenantRe();

    /// <summary>Device-code sign-in (the fallback when the dedicated browser cannot be opened): the code is shown in Status until it completes.</summary>
    public async Task<GitConnectionStatus> StartDeviceCodeAsync(CancellationToken ct)
    {
        if (!azure.Value.Configured) return await StatusAsync(ct);
        if (!azure.Value.AllowDeviceCode)
        {
            lock (_gate) { _transient = GitConnectionState.Failed; _transientMessage = "Device-code sign-in is disabled (AzureEnvironment:AllowDeviceCode)."; }
            return await StatusAsync(ct);
        }
        lock (_gate) { _transient = GitConnectionState.SigningIn; _transientMessage = "Requesting a device code…"; _deviceCode = null; }
        _ = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            try
            {
                await broker.DeviceCodeForAsync(Scopes, prompt =>
                {
                    lock (_gate)
                    {
                        _deviceCode = new(prompt.UserCode, prompt.VerificationUrl, prompt.ExpiresOn);
                        _transient = GitConnectionState.AwaitingDeviceCode;
                        _transientMessage = "Open the verification page in any browser and enter the code.";
                    }
                    return Task.CompletedTask;
                }, timeout.Token);
                lock (_gate) { _transient = null; _transientMessage = null; _deviceCode = null; _wasConnected = true; _signInFailure = null; _accessDenied = false; }
            }
            catch (Exception e)
            {
                logger.LogInformation("Git Sources device-code sign-in did not complete ({ExceptionType}).", e.GetType().Name);
                var failure = ClassifySignInFailure(e);
                lock (_gate) { _transient = failure.Cancelled ? null : GitConnectionState.Failed; _transientMessage = failure.Message; _signInFailure = failure.Check; _deviceCode = null; }
            }
        });
        return await StatusAsync(ct);
    }

    // ── Discovery: organizations → projects → repositories ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Organizations the person can use: discovered from their Azure DevOps membership, plus those they entered (advanced). When discovery is not
    /// possible the entered ones are still listed and the reason is returned — membership is never assumed.
    /// </summary>
    public async Task<(IReadOnlyList<GitOrganization> Organizations, string? Problem)> OrganizationsAsync(CancellationToken ct)
    {
        var settings = store.Read().Settings;
        var entered = settings.Organizations.Concat(settings.OrganizationUrl is { } url ? [url] : [])
            .Select(GitSourcePolicy.NormalizeOrganizationUrl).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(u => new GitOrganization(GitSourcePolicy.OrganizationName(u), GitSourcePolicy.OrganizationName(u), u, Discovered: false)).ToList();
        var token = azure.Value.Configured ? await broker.SilentForAsync(Scopes, ct) : null;
        if (token is null) return (entered, GitSourcePolicy.Describe(GitFailureKind.SignInRequired));
        try
        {
            var discovered = await Remote(() => client.OrganizationsAsync(token.AccessToken, ct));
            var all = discovered.Concat(entered.Where(e => !discovered.Any(d => string.Equals(d.Url, e.Url, StringComparison.OrdinalIgnoreCase)))).ToList();
            return (all, discovered.Count == 0 ? "This account is not a member of any Azure DevOps organization that discovery can list. Enter the organization URL (Advanced)." : null);
        }
        catch (GitProviderException e) { return (entered, $"Organizations could not be discovered: {e.Message} Enter the organization URL (Advanced)."); }
    }

    public GitConnectionStatus SelectOrganizations(IReadOnlyList<string> organizationUrls)
    {
        var normalized = organizationUrls.Select(u => GitSourcePolicy.NormalizeOrganizationUrl(u)
            ?? throw new GitSettingsException($"\"{u}\" is not an Azure DevOps organization URL.")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        store.Update(s => s with { Organizations = normalized });
        return new() { State = GitConnectionState.NotConnected, Organizations = normalized, Message = "Organizations saved." };
    }

    public async Task<GitProjectPage> ProjectsAsync(string organizationUrl, string? continuation, CancellationToken ct)
    {
        var (org, token) = await AccessAsync(ct, organizationUrl);
        return await Remote(() => client.ProjectsAsync(org, continuation, token, ct));
    }

    public async Task<IReadOnlyList<GitRepository>> ProjectRepositoriesAsync(string organizationUrl, string projectId, CancellationToken ct)
    {
        var (org, token) = await AccessAsync(ct, organizationUrl);
        return await Remote(() => client.ProjectRepositoriesAsync(org, projectId, token, ct));
    }

    public async Task<GitCommitInfo?> CommitAsync(string organizationUrl, string repositoryId, string commit, CancellationToken ct)
    {
        var (org, token) = await AccessAsync(ct, organizationUrl);
        return await Remote(() => client.CommitAsync(org, repositoryId, commit.Trim().ToLowerInvariant(), token, ct));
    }

    // ── Links: repository ↔ BirkNext project ──────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<GitSourceLink> Links(string? environmentId) =>
        [.. store.Read().Settings.Links.Where(l => environmentId is null || string.Equals(l.EnvironmentId, environmentId, StringComparison.Ordinal))
            .OrderBy(l => l.Repository, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Links a repository (by its Azure DevOps id) to a BirkNext project, or updates the link's branch, pinned commit and roles. Repository metadata
    /// is read from Azure DevOps, never taken from the request; a pinned commit must exist. Imports, history and association decisions are kept.
    /// </summary>
    public async Task<GitSourceLink> LinkAsync(GitLinkRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentId)) throw new GitSettingsException("Choose the BirkNext project (Target Environment) the repository is evidence for.");
        var (org, token) = await AccessAsync(ct, request.OrganizationUrl);
        var repo = (await Remote(() => client.RepositoriesAsync(org, token, ct))).FirstOrDefault(r => r.Id == request.RepositoryId)
                   ?? throw new GitProviderException(GitFailureKind.RepositoryNotFound);
        if (request.PinnedCommit is { Length: > 0 } pin && await Remote(() => client.CommitAsync(org, repo.Id, pin.Trim().ToLowerInvariant(), token, ct)) is null)
            throw new GitProviderException(GitFailureKind.RevisionNotFound);
        var id = GitSourceLink.IdentityOf(org, repo.Id);
        var roles = request.Roles.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        GitSourceLink? result = null;
        store.Update(s =>
        {
            var existing = s.Links.FirstOrDefault(l => l.Id == id && l.EnvironmentId == request.EnvironmentId);
            result = (existing ?? new GitSourceLink { Id = id, EnvironmentId = request.EnvironmentId, LinkedAt = _clock.GetUtcNow() }) with
            {
                OrganizationUrl = org, Organization = GitSourcePolicy.OrganizationName(org), ProjectId = repo.ProjectId, Project = repo.Project, RepositoryId = repo.Id,
                Repository = repo.Name, WebUrl = repo.WebUrl, Branch = GitSourcePolicy.BranchName(request.Branch.Length > 0 ? request.Branch : repo.DefaultBranch ?? "main"),
                PinnedCommit = request.PinnedCommit is { Length: > 0 } p ? p.Trim().ToLowerInvariant() : null, Roles = roles,
                BirkNextProject = request.BirkNextProject ?? existing?.BirkNextProject,
            };
            return s with { Links = [.. s.Links.Where(l => !(l.Id == id && l.EnvironmentId == request.EnvironmentId)), result] };
        });
        return result!;
    }

    /// <summary>Removes the link only: imported snapshots, frozen reports and Project Evidence bindings stay.</summary>
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

    /// <summary>A committed Git import is appended to every link of that repository (history is immutable; the same commit twice is one entry).</summary>
    public void Record(GitSourceRevision revision, Guid? snapshotId, string importId)
    {
        store.Update(s => s with
        {
            Links = [.. s.Links.Select(l =>
                !string.Equals(l.Organization, revision.Organization, StringComparison.OrdinalIgnoreCase) || !string.Equals(l.RepositoryId, revision.RepositoryId, StringComparison.OrdinalIgnoreCase)
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
    /// "Check for updates": the linked branch's current head (or the pinned commit) against the last imported revision. Nothing is imported; the
    /// selected branch is never changed when the remote default branch changes.
    /// </summary>
    public async Task<GitSourceLink> CheckForUpdatesAsync(string linkId, string environmentId, CancellationToken ct)
    {
        var link = Links(environmentId).FirstOrDefault(l => l.Id == linkId) ?? throw new GitProviderException(GitFailureKind.RepositoryNotFound, "no such link");
        GitUpdateCheck check;
        try
        {
            if (link.PinnedCommit is { } pinned)
                check = new(pinned, null, _clock.GetUtcNow(), link.LastImport?.CommitSha != pinned);
            else
            {
                var branch = (await BranchesAsync(link.RepositoryId, ct, link.OrganizationUrl)).FirstOrDefault(b => b.Name == link.Branch);
                check = branch is null
                    ? new(null, null, _clock.GetUtcNow(), false, GitSourcePolicy.Describe(GitFailureKind.BranchNotFound))
                    : new(branch.CommitSha, branch.CommittedAt, _clock.GetUtcNow(), link.LastImport?.CommitSha != branch.CommitSha);
            }
        }
        catch (GitProviderException e) { check = new(null, null, _clock.GetUtcNow(), false, e.Message); }
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
        var link = Links(environmentId).FirstOrDefault(l => l.Id == linkId) ?? throw new GitProviderException(GitFailureKind.RepositoryNotFound, "no such link");
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
