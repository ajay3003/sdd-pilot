using System.Diagnostics;
using Path = System.IO.Path;
using System.Text.Json;
using BirkNext.Api.Services.AzureEnvironment;
using BirkNext.Api.Services.ProjectImport;
using BirkNext.GitSources;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.GitSources;

/// <summary>
/// Safe Git Sources settings (organization URL, tracked branches, last test summary) in one JSON file of this BirkNext instance. It never holds a
/// password, PAT or token; tokens live only in MSAL's in-memory cache.
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

    /// <summary>Read-modify-write of the settings under one lock (links and organizations change concurrently with tests and imports).</summary>
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
/// Git Sources: Azure DevOps over Microsoft Entra ID (delegated, the person's own access). Sign-in reuses BirkNext's Microsoft app registration and
/// MSAL broker (dedicated Edge profile, MFA / Conditional Access as Entra decides, tokens in memory only). Every remote call happens only on an
/// explicit action (sign in, test, refresh, import) — never because a page opened. Read-only against the remote.
/// </summary>
public sealed partial class GitSourcesService(
    IOptions<AzureEnvironmentOptions> azure, IAzureTokenBroker broker, IAzureSignInBrowser browser, IAzureDevOpsGitClient client, GitSourceSettingsStore store,
    IServiceScopeFactory scopes, ProjectImportStagingStore staging, ILogger<GitSourcesService> logger, TimeProvider? clock = null)
{
    private static readonly string[] Scopes = [GitSourcePolicy.AzureDevOpsScope];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private GitConnectionState? _transient;
    private string? _transientMessage;
    private bool _wasConnected, _accessDenied;
    private GitDeviceCode? _deviceCode;
    private GitConfigurationCheck? _signInFailure;

    public static IReadOnlyList<string> Requirements() =>
    [
        "Git Sources signs in with BirkNext's Microsoft app registration: set AzureEnvironment:Enabled and AzureEnvironment:ClientId (a public client, redirect URI http://localhost).",
        "Give that app registration the delegated Azure DevOps permission (user_impersonation). BirkNext reads only what your own account can read.",
        "Enter your Azure DevOps organization URL below (https://dev.azure.com/{organization}).",
    ];

    /// <summary>Connection state from the in-memory token cache only: no remote call.</summary>
    public async Task<GitConnectionStatus> StatusAsync(CancellationToken ct)
    {
        var (settings, lastTest) = store.Read();
        var diagnostics = Diagnostics(lastTest);
        if (!azure.Value.Configured)
            return new() { State = GitConnectionState.NotConfigured, Requirements = Requirements(), OrganizationUrl = settings.OrganizationUrl, LastTest = lastTest,
                Message = diagnostics.FirstOrDefault(d => d.State is GitCheckState.Missing or GitCheckState.Invalid)?.Detail ?? "Microsoft sign-in is not configured for this BirkNext instance.",
                Diagnostics = diagnostics, Organizations = settings.Organizations };
        lock (_gate)
            if (_transient is { } t)
                return new() { State = t, Message = _transientMessage ?? "", OrganizationUrl = settings.OrganizationUrl, LastTest = lastTest, SignInAvailable = browser.Available,
                    Diagnostics = diagnostics, DeviceCodeAllowed = azure.Value.AllowDeviceCode, DeviceCode = _deviceCode, Organizations = settings.Organizations };
        var token = await broker.SilentForAsync(Scopes, ct);
        GitConnectionState state;
        lock (_gate)
        {
            state = token is not null ? (_accessDenied ? GitConnectionState.AccessDenied : GitConnectionState.Connected)
                : _wasConnected ? GitConnectionState.ReconnectRequired : GitConnectionState.NotConnected;
            if (token is not null) _wasConnected = true;
        }
        return new()
        {
            State = state, Account = token?.Account, OrganizationUrl = settings.OrganizationUrl, LastTest = lastTest, SignInAvailable = browser.Available, TrackedBranches = settings.TrackedBranches,
            Diagnostics = diagnostics, DeviceCodeAllowed = azure.Value.AllowDeviceCode, Organizations = settings.Organizations,
            Requirements = settings.OrganizationUrl is null ? [Requirements()[2]] : [],
            Message = state switch
            {
                GitConnectionState.Connected => "Signed in with Microsoft Entra ID. Azure DevOps access is your own account's access.",
                GitConnectionState.AccessDenied => "Signed in with Microsoft Entra ID, but Azure DevOps denied access for this account (organization membership or permission).",
                GitConnectionState.ReconnectRequired => "The Azure DevOps sign-in expired or needs consent again: reconnect.",
                _ => "Not connected. Sign in to read repositories and branches.",
            },
        };
    }

    /// <summary>Starts interactive sign-in in the dedicated BirkNext Edge profile (background; poll <see cref="StatusAsync"/>).</summary>
    public async Task<GitConnectionStatus> SignInAsync(CancellationToken ct)
    {
        if (!azure.Value.Configured) return await StatusAsync(ct);
        if (!browser.Available)
        {
            lock (_gate) { _transient = GitConnectionState.Failed; _transientMessage = browser.UnavailableReason; }
            return await StatusAsync(ct);
        }
        lock (_gate) { _transient = GitConnectionState.SigningIn; _transientMessage = "Complete sign-in in the Microsoft Edge window that opened (dedicated BirkNext profile)."; }
        _ = Task.Run(async () =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                await broker.InteractiveForAsync(Scopes, browser.OpenAsync, timeout.Token);
                lock (_gate) { _transient = null; _transientMessage = null; _wasConnected = true; _signInFailure = null; _accessDenied = false; }
            }
            catch (Exception e)
            {
                // The exception text can echo request details; only its type is logged.
                logger.LogInformation("Git Sources sign-in did not complete ({ExceptionType}).", e.GetType().Name);
                var failure = ClassifySignInFailure(e);
                lock (_gate) { _transient = failure.Cancelled ? null : GitConnectionState.Failed; _transientMessage = failure.Message; _signInFailure = failure.Check; }
            }
        });
        return await StatusAsync(ct);
    }

    /// <summary>
    /// Disconnect: removes the cached Microsoft account from this BirkNext instance (which also signs Azure Environment Analysis out — one Microsoft
    /// session). Never deletes settings, tracked branches, imported projects or snapshots.
    /// </summary>
    public async Task<GitConnectionStatus> DisconnectAsync(CancellationToken ct)
    {
        await broker.SignOutAsync();
        lock (_gate) { _transient = null; _transientMessage = null; _wasConnected = false; _accessDenied = false; _deviceCode = null; }
        return await StatusAsync(ct);
    }

    public GitConnectionStatus SaveSettings(GitSourceSettings settings)
    {
        var url = settings.OrganizationUrl is null ? null : GitSourcePolicy.NormalizeOrganizationUrl(settings.OrganizationUrl)
            ?? throw new GitSettingsException("Enter the organization URL as https://dev.azure.com/{organization} or https://{organization}.visualstudio.com.");
        var (current, lastTest) = store.Read();
        // A different organization makes the last test about something else: it is cleared, never shown as verified for the new one.
        store.Write(settings with { OrganizationUrl = url }, url == current.OrganizationUrl ? lastTest : null);
        return new() { State = GitConnectionState.NotConnected, OrganizationUrl = url, Message = "Saved. Test the Git connection to verify it." };
    }

    /// <summary>
    /// The explicit Git test: sign-in → token → organization → repositories → branches → commit metadata → local storage. Each step diagnostic; it
    /// never imports, creates a snapshot or writes to the remote. Only the safe summary is kept.
    /// </summary>
    public async Task<GitTestResult> TestAsync(CancellationToken ct)
    {
        var (settings, _) = store.Read();
        var steps = new List<GitTestStep>();
        int? repositoryCount = null;
        async Task<T?> Step<T>(string name, Func<Task<(T? Value, string Message)>> run) where T : class
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var (value, message) = await run();
                steps.Add(new(name, value is null ? GitTestStepState.Failed : GitTestStepState.Verified, message, sw.ElapsedMilliseconds));
                return value;
            }
            catch (GitProviderException e) { steps.Add(new(name, GitTestStepState.Failed, e.Message, sw.ElapsedMilliseconds)); return null; }
        }
        void Skip(params string[] names) { foreach (var n in names) steps.Add(new(n, GitTestStepState.Skipped, "Not tested: an earlier step failed.", 0)); }

        var org = settings.OrganizationUrl ?? settings.Organizations.FirstOrDefault();
        var token = !azure.Value.Configured ? null : await Step("Microsoft Entra authentication", async () =>
        {
            var t = await broker.SilentForAsync(Scopes, ct);
            return (t?.AccessToken, t is null ? GitSourcePolicy.Describe(GitFailureKind.SignInRequired) : $"Verified{(t.Account is null ? "" : $" ({t.Account})")}");
        });
        if (!azure.Value.Configured) steps.Add(new("Microsoft Entra authentication", GitTestStepState.Failed, "Microsoft sign-in is not configured for this BirkNext instance.", 0));
        if (org is null) steps.Add(new("Azure DevOps organization", GitTestStepState.Failed, "No organization URL is set.", 0));
        if (token is null || org is null) Skip("Repository discovery", "Branch discovery", "Commit metadata");
        else
        {
            var repos = await Step("Azure DevOps organization and repository discovery", async () =>
            {
                var list = await client.RepositoriesAsync(org, token, ct);
                repositoryCount = list.Count;
                return (list, list.Count == 0 ? "The organization is accessible, but this account sees no repositories." : $"{list.Count} repositories found");
            });
            var repo = repos?.FirstOrDefault(r => !r.IsDisabled && settings.TrackedBranches.Any(t => t.RepositoryId == r.Id)) ?? repos?.FirstOrDefault(r => !r.IsDisabled);
            if (repo is null) Skip("Branch discovery", "Commit metadata");
            else
            {
                var branches = await Step("Branch discovery", async () =>
                {
                    var list = await client.BranchesAsync(org, repo.Id, token, ct);
                    return (list, $"{list.Count} branches in {repo.Name}");
                });
                var head = branches?.FirstOrDefault();
                steps.Add(head is { CommitSha.Length: > 0 }
                    ? new("Commit metadata", GitTestStepState.Verified, $"{head.Name} @ {head.CommitSha[..Math.Min(8, head.CommitSha.Length)]}{(head.CommittedAt is { } d ? $" · {d:yyyy-MM-dd}" : "")}", 0)
                    : new("Commit metadata", branches is null ? GitTestStepState.Skipped : GitTestStepState.Failed, branches is null ? "Not tested: an earlier step failed." : "No branch head commit was returned.", 0));
            }
        }
        steps.Add(StorageStep());

        var ready = steps.All(s => s.State == GitTestStepState.Verified);
        var failed = steps.FirstOrDefault(s => s.State == GitTestStepState.Failed);
        var result = new GitTestResult(ready, ready ? "Ready" : $"Not ready: {failed?.Name} — {failed?.Message}", steps, _clock.GetUtcNow(),
            org is null ? null : GitSourcePolicy.OrganizationName(org), repositoryCount);
        store.Write(settings, result);
        return result;
    }

    private GitTestStep StorageStep()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            System.IO.Directory.CreateDirectory(store.Folder);
            var probe = Path.Combine(store.Folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new("Local storage", GitTestStepState.Verified, "Writable", sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new("Local storage", GitTestStepState.Failed, "The BirkNext settings folder is not writable.", sw.ElapsedMilliseconds);
        }
    }

    public async Task<IReadOnlyList<GitRepository>> RepositoriesAsync(CancellationToken ct, string? organizationUrl = null)
    {
        var (org, token) = await AccessAsync(ct, organizationUrl);
        return await Remote(() => client.RepositoriesAsync(org, token, ct));
    }

    public async Task<IReadOnlyList<GitBranch>> BranchesAsync(string repositoryId, CancellationToken ct, string? organizationUrl = null)
    {
        var (org, token) = await AccessAsync(ct, organizationUrl);
        return await Remote(() => client.BranchesAsync(org, repositoryId, token, ct));
    }

    /// <summary>
    /// Resolves the branch to its current commit (or validates the chosen commit), downloads exactly that commit's archive and stages it through the
    /// same Project Import preview as a chosen ZIP. The revision (repository, branch, SHA, retrieval time) travels to the commit as provenance;
    /// nothing else differs from a ZIP import.
    /// </summary>
    public async Task<GitImportResult> ImportAsync(GitImportRequest request, CancellationToken ct)
    {
        var (org, token) = await AccessAsync(ct, request.OrganizationUrl);
        var repositories = await Remote(() => client.RepositoriesAsync(org, token, ct));
        var repo = repositories.FirstOrDefault(r => r.Id == request.RepositoryId) ?? throw new GitProviderException(GitFailureKind.RepositoryNotFound);
        var branchName = GitSourcePolicy.BranchName(request.Branch);
        string sha;
        DateTimeOffset? committedAt;
        if (request.Commit is { Length: > 0 } pinned)
        {
            // A chosen revision: it must exist in this repository (never a guessed or partial SHA).
            var commit = await Remote(() => client.CommitAsync(org, repo.Id, pinned.Trim().ToLowerInvariant(), token, ct))
                         ?? throw new GitProviderException(GitFailureKind.RevisionNotFound);
            (sha, committedAt) = (commit.CommitSha, commit.CommittedAt);
        }
        else
        {
            var branch = (await Remote(() => client.BranchesAsync(org, repo.Id, token, ct))).FirstOrDefault(b => b.Name == branchName)
                         ?? throw new GitProviderException(GitFailureKind.BranchNotFound);
            (sha, committedAt) = (branch.CommitSha, branch.CommittedAt);
        }
        if (!GitSourcePolicy.IsCommitSha(sha)) throw new GitProviderException(GitFailureKind.Unexpected, "no commit SHA for the revision");
        var bytes = await Remote(() => client.CommitArchiveAsync(org, repo.Id, sha, token, GitSourcePolicy.MaxArchiveBytes, ct));
        var revision = new GitSourceRevision(GitSourcePolicy.Provider, GitSourcePolicy.OrganizationName(org), repo.Project, repo.Id, repo.Name, branchName, sha,
            committedAt, _clock.GetUtcNow(), repo.WebUrl);
        using var scope = scopes.CreateScope();
        var preview = scope.ServiceProvider.GetRequiredService<ProjectImportService>().Preview(GitSourcePolicy.ArchiveFileName(revision), bytes, ct);
        if (preview.Preview is { } p) staging.SetGitRevision(p.StagingId, revision);
        logger.LogInformation("Git Sources staged an import. Repository {Repository}; commit {Commit}; archive size {ArchiveBytes}", repo.Name, revision.ShortSha, bytes.Length);
        return new(revision, preview.Preview, preview.Failure?.Message);
    }

    /// <summary>An organization the person selected or entered (never an arbitrary URL), else the configured one.</summary>
    private async Task<(string Organization, string Token)> AccessAsync(CancellationToken ct, string? organizationUrl = null)
    {
        var settings = store.Read().Settings;
        var org = organizationUrl is { Length: > 0 }
            ? GitSourcePolicy.NormalizeOrganizationUrl(organizationUrl) ?? throw new GitSettingsException("The organization URL is not an Azure DevOps organization.")
            : settings.OrganizationUrl ?? settings.Organizations.FirstOrDefault()
              ?? throw new GitSettingsException("Choose an Azure DevOps organization first (Import Project → Azure DevOps Git, or System Settings → Git Sources).");
        var token = azure.Value.Configured ? await broker.SilentForAsync(Scopes, ct) : null;
        return (org, token?.AccessToken ?? throw new GitProviderException(GitFailureKind.SignInRequired));
    }

    /// <summary>A remote call; an access refusal after a valid sign-in is remembered as "Azure DevOps access denied" (not "not connected").</summary>
    private async Task<T> Remote<T>(Func<Task<T>> call)
    {
        try
        {
            var result = await call();
            lock (_gate) _accessDenied = false;
            return result;
        }
        catch (GitProviderException e) when (e.Kind is GitFailureKind.AccessDenied or GitFailureKind.SignInRequired)
        {
            lock (_gate) _accessDenied = e.Kind == GitFailureKind.AccessDenied;
            throw;
        }
    }
}

public sealed class GitSettingsException(string message) : Exception(message);
