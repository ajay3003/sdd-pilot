using BirkNext.GitSources;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

/// <summary>
/// The one state model behind System Settings → Git Sources: the backend's Git Sources status and saved Microsoft sign-in values, the form
/// drafts, the selected step and every action. Step states come from <see cref="GitSourcesPresentation.SetupSteps"/> (authoritative backend
/// state), never from UI booleans; the step panels render this model and call its actions. Discovery results are kept while switching steps,
/// so moving between steps never repeats a backend call.
/// </summary>
public sealed class GitSourcesSetupState(IGitSourcesApiService git, AdminApiService? admin, IJSRuntime js, NavigationManager navigation,
    Func<BirkNext.Web.Services.ProjectImport.ProjectImportSession?> importSession, IFrontendAnalysisContextFactory? contexts = null)
{
    public static readonly IReadOnlyList<(string Id, string Label)> StepLabels =
        [("provider", "Setup"), ("application", "Microsoft Entra"), ("organization", "Organization"), ("authentication", "Sign in"), ("test", "Connection"), ("import", "Import")];

    public event Action? Changed;

    public GitConnectionStatus? Status { get; private set; }
    public GitTestResult? Test { get; private set; }
    public MicrosoftSignInInfoDto? SignIn { get; private set; }
    public IReadOnlyList<GitSourceLink> Links { get; private set; } = [];
    public List<GitTrackedBranch> Tracked { get; private set; } = [];
    public IReadOnlyList<GitProject>? Projects { get; private set; }
    public IReadOnlyList<GitRepository>? Repositories { get; private set; }
    public IReadOnlyList<GitBranch>? Branches { get; private set; }
    public string? ProjectId { get; private set; }
    public string? RepositoryId { get; private set; }
    public string? Branch { get => _branch; set => Set(ref _branch, value); }
    public string? ProjectFilter { get => _projectFilter; set => Set(ref _projectFilter, value); }
    public string? RepositoryFilter { get => _repositoryFilter; set => Set(ref _repositoryFilter, value); }
    public string? BranchFilter { get => _branchFilter; set => Set(ref _branchFilter, value); }

    public bool Busy { get; private set; }
    public bool Testing { get; private set; }
    public string? Error { get; private set; }
    public string? SignInProblem { get; private set; }
    public string? Notice { get; private set; }
    public string? Copied { get; private set; }
    public bool HelpOpen { get => _helpOpen; set => Set(ref _helpOpen, value); }
    public bool InstructionsOpen { get => _instructionsOpen; set => Set(ref _instructionsOpen, value); }

    // Drafts of the Microsoft sign-in settings (steps 1-2) and the organization (step 3). Every change notifies the page, so dependent
    // controls in other components (Cancel changes, disabled reasons) update at once.
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool AllowDeviceCode { get => _allowDeviceCode; set => Set(ref _allowDeviceCode, value); }
    public string? ClientId { get => _clientId; set => Set(ref _clientId, value); }
    public string? Tenant { get => _tenant; set => Set(ref _tenant, value); }
    public string TenantMode { get => _tenantMode; set => Set(ref _tenantMode, value); }
    public string? Organization { get => _organization; set => Set(ref _organization, value); }

    private string? _active, _branch, _projectFilter, _repositoryFilter, _branchFilter, _clientId, _tenant, _organization;
    private string _tenantMode = "organizations";
    private bool _helpOpen, _instructionsOpen, _enabled, _allowDeviceCode = true;

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed?.Invoke();
    }

    public IReadOnlyList<GuidedStep> Steps => Status is null ? [] : GitSourcesPresentation.SetupSteps(Status, SignIn?.RestartRequired == true, Imported, Testing);
    public bool Imported => Links.Any(l => l.LastImport is not null);
    public bool CanSave => SignIn is not null;
    public bool SignedIn => Status?.State is GitConnectionState.Connected or GitConnectionState.AccessDenied;
    public bool Discoverable => Status is { State: GitConnectionState.Connected, LastTest.Ready: true, LastTestStale: false };
    public string? ClientProblem => GitSourcesPresentation.ClientIdProblem(Enabled, ClientId);
    public string? TenantProblem => GitSourcesPresentation.TenantProblem(TenantMode == "specific", Tenant);
    public string? OrganizationProblem => GitSourcesPresentation.OrganizationProblem(Organization);
    public string EffectiveTenant => TenantMode == "specific" ? Tenant?.Trim() ?? "" : "organizations";
    public string OrganizationUrl => Status?.OrganizationUrl ?? Status?.Organizations.FirstOrDefault() ?? "";
    public bool SignInDirty => SignIn is not null && (SignIn.Enabled != Enabled || SignIn.AllowDeviceCode != AllowDeviceCode
        || !string.Equals(SignIn.ClientId, ClientId?.Trim() ?? "", StringComparison.OrdinalIgnoreCase)
        || !string.Equals(NormalTenant(SignIn.TenantId), EffectiveTenant, StringComparison.OrdinalIgnoreCase));

    /// <summary>The selected step: the person's choice, otherwise the first step that needs action (current or blocked), otherwise import.</summary>
    public string Active => _active ?? Steps.FirstOrDefault(s => s.State is GuidedStepState.Current or GuidedStepState.Blocked)?.Id ?? "import";
    public GuidedStep? Step(string id) => Steps.FirstOrDefault(s => s.Id == id);

    public void Select(string id) { _active = id; Notice = null; SignInProblem = null; Changed?.Invoke(); }
    private void Next(string id)
    {
        var index = StepLabels.ToList().FindIndex(s => s.Id == id);
        _active = index >= 0 && index < StepLabels.Count - 1 ? StepLabels[index + 1].Id : id;
    }

    public async Task InitializeAsync()
    {
        await RefreshAsync();
        await LoadSignInAsync();
        if (contexts is not null)
            try { if ((await contexts.GetActiveContextAsync()).ActiveProfileOrNull?.Id is { } env) Links = (await git.LinksAsync(env)).Items; } catch (InvalidOperationException) { }
    }

    public async Task RefreshAsync()
    {
        (Status, Error) = await git.StatusAsync();
        Organization ??= Status?.OrganizationUrl;
        if (Status is not null) Tracked = [.. Status.TrackedBranches];
    }

    private async Task LoadSignInAsync()
    {
        if (admin is null || (await admin.GetSystemSettingsAsync())?.MicrosoftSignIn is not { } signIn) return;
        SignIn = signIn;
        Revert();
    }

    /// <summary>Cancel changes: the saved values again.</summary>
    public void Revert()
    {
        if (SignIn is not { } s) return;
        (_enabled, _clientId, _allowDeviceCode, SignInProblem) = (s.Enabled, s.ClientId, s.AllowDeviceCode, null);
        _tenantMode = NormalTenant(s.TenantId) == "organizations" ? "organizations" : "specific";
        _tenant = _tenantMode == "specific" ? s.TenantId : null;
        Changed?.Invoke();
    }

    private static string NormalTenant(string? tenant) => string.IsNullOrWhiteSpace(tenant) ? "organizations" : tenant.Trim();

    private async Task Run(Func<Task> action)
    {
        // The step the person acted in stays open until they move on, so its result (signed in, connection verified) is seen.
        _active ??= Active;
        (Busy, Error) = (true, null);
        Changed?.Invoke();
        try { await action(); }
        finally { Busy = false; Changed?.Invoke(); }
    }

    /// <summary>Step 1. Enabling needs the application's client id, so without one the choice is kept as a draft and step 2 completes it.</summary>
    public Task SaveProviderAsync() => Run(async () =>
    {
        if (Enabled && string.IsNullOrWhiteSpace(ClientId))
        {
            Next("provider");
            Notice = "Enter the Application (Client) ID to finish enabling Git Sources.";
            return;
        }
        if (await SaveSignInAsync()) Next("provider");
    });

    /// <summary>Step 2. Saved values are applied at once by the backend; nothing here signs in or claims Microsoft confirmed the application.</summary>
    public Task SaveApplicationAsync() => Run(async () => { if (await SaveSignInAsync()) Next("application"); });

    private async Task<bool> SaveSignInAsync()
    {
        SignInProblem = ClientProblem ?? TenantProblem;
        if (SignInProblem is not null) return false;
        if (admin is null) { SignInProblem = "Saving needs access to System Settings on this installation."; return false; }
        if (!SignInDirty) return true;
        var (saved, message) = await admin.SaveSettingsAsync(new() { MicrosoftSignIn = new()
            { Enabled = Enabled, ClientId = ClientId?.Trim() ?? "", TenantId = EffectiveTenant, AllowDeviceCode = AllowDeviceCode } });
        if (!saved) { SignInProblem = message; return false; }
        await LoadSignInAsync();
        await RefreshAsync();
        Notice = message;
        return true;
    }

    public Task SaveOrganizationAsync() => Run(async () =>
    {
        if (OrganizationProblem is not null) return;
        var (status, error) = await git.SaveSettingsAsync(new() { OrganizationUrl = GitSourcePolicy.NormalizeOrganizationUrl(Organization), TrackedBranches = Tracked });
        Error = error;
        if (status is null) return;
        Test = null;
        await RefreshAsync();
        Next("organization");
        Notice = "Organization saved. Access has not been verified.";
    });

    public Task SignInAsync() => Run(async () => { var (s, e) = await git.SignInAsync(); (Status, Error) = (s ?? Status, e); });
    public Task DeviceCodeAsync() => Run(async () => { var (s, e) = await git.DeviceCodeAsync(); (Status, Error) = (s ?? Status, e); });
    public Task CheckSignInAsync() => Run(RefreshAsync);

    public Task SignOutAsync() => Run(async () =>
    {
        var (s, e) = await git.DisconnectAsync();
        (Status, Error, Projects, Repositories, Branches, ProjectId, RepositoryId) = (s ?? Status, e, null, null, null, null, null);
    });

    /// <summary>Step 5: the backend's read-only test; one at a time.</summary>
    public async Task TestAsync()
    {
        if (Testing) return;
        Testing = true;
        try { await Run(async () => { (Test, Error) = await git.TestAsync(); await RefreshAsync(); }); }
        finally { Testing = false; Changed?.Invoke(); }
    }

    public Task LoadProjectsAsync() => Run(async () =>
    {
        var all = new List<GitProject>();
        string? continuation = null;
        do
        {
            var (page, error) = await git.ProjectsAsync(OrganizationUrl, continuation);
            if (page is null) { Error = error; return; }
            all.AddRange(page.Items);
            continuation = page.Next;
        } while (continuation is not null && all.Count < 2000);
        (Projects, Repositories, Branches, ProjectId, RepositoryId, Branch) = (all, null, null, null, null, null);
    });

    public Task SelectProjectAsync(string? projectId) => Run(async () =>
    {
        (ProjectId, Repositories, Branches, RepositoryId, Branch) = (string.IsNullOrEmpty(projectId) ? null : projectId, null, null, null, null);
        if (ProjectId is not null) (Repositories, Error) = await git.ProjectRepositoriesAsync(OrganizationUrl, ProjectId);
    });

    public Task SelectRepositoryAsync(string? repositoryId) => Run(async () =>
    {
        (RepositoryId, Branches, Branch) = (string.IsNullOrEmpty(repositoryId) ? null : repositoryId, null, null);
        if (RepositoryId is null) return;
        (Branches, Error) = await git.BranchesInAsync(OrganizationUrl, RepositoryId);
        // The repository's actual default branch, never an assumed main/master.
        Branch = Branches.FirstOrDefault(b => b.IsDefault)?.Name ?? Branches.FirstOrDefault()?.Name;
    });

    public bool IsTracked(string? repositoryId, string? branch) => Tracked.Any(t => t.RepositoryId == repositoryId && t.Branch == branch);

    public Task ToggleTrackAsync() => Run(async () =>
    {
        var repo = Repositories?.FirstOrDefault(r => r.Id == RepositoryId);
        if (repo is null || string.IsNullOrEmpty(Branch)) return;
        Tracked = IsTracked(repo.Id, Branch) ? [.. Tracked.Where(t => !(t.RepositoryId == repo.Id && t.Branch == Branch))] : [.. Tracked, new(repo.Id, repo.Name, repo.Project, Branch)];
        var (_, error) = await git.SaveSettingsAsync(new() { OrganizationUrl = Status?.OrganizationUrl, TrackedBranches = Tracked });
        Error = error;
    });

    /// <summary>Review import: the branch's current commit is staged through Import Project's session and reviewed there (read-only).</summary>
    public void ReviewImport()
    {
        var repo = Repositories?.FirstOrDefault(r => r.Id == RepositoryId);
        if (repo is null || string.IsNullOrEmpty(Branch)) return;
        StartImport(new GitImportRequest(repo.Id, Branch) { OrganizationUrl = OrganizationUrl }, $"{repo.Name} @ {Branch}");
    }

    /// <summary>Fetch latest: the tracked branch's newest commit through the same review (identical content reuses the existing snapshot).</summary>
    public void FetchLatest(GitTrackedBranch tracked) =>
        StartImport(new GitImportRequest(tracked.RepositoryId, tracked.Branch) { OrganizationUrl = OrganizationUrl }, $"{tracked.Repository} @ {tracked.Branch}");

    private void StartImport(GitImportRequest request, string label)
    {
        if (importSession() is not { } session) { Error = "Import Project is not available in this BirkNext instance."; Changed?.Invoke(); return; }
        _ = session.StartGitPreviewAsync(label, ct => git.ImportAsync(request, ct), null);
        navigation.NavigateTo("project-import");
    }

    /// <summary>The import of a tracked branch, from this Target Environment's links (null when it was never imported).</summary>
    public GitSourceLink? LinkFor(GitTrackedBranch tracked) =>
        Links.FirstOrDefault(l => string.Equals(l.RepositoryId, tracked.RepositoryId, StringComparison.OrdinalIgnoreCase) && l.Branch == tracked.Branch);

    public async Task CopyAsync(string text, string what)
    {
        try { await js.InvokeVoidAsync("navigator.clipboard.writeText", text); Copied = what; }
        catch (Exception e) when (e is JSException or InvalidOperationException) { Copied = null; }
        Changed?.Invoke();
    }
}
