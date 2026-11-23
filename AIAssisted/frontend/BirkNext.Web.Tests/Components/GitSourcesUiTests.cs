using BirkNext.GitSources;
using BirkNext.Web.Components;
using BirkNext.Web.Components.GitSources;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Git Sources, Git-only (2026-11-19): System Settings shows the local Git and its credential helper (never a client id, tenant or BirkNext sign-in),
/// connected repositories of every project with import only for the active one, and the add form (local folder or Git URL) refuses URLs with a
/// credential or an option. Project Import → Git queues imports that carry the project and the exact commit. No real Git runs here.
/// </summary>
public sealed class GitSourcesUiTests : BunitContext
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private readonly Mock<IGitSourcesApiService> _git = new();

    private static GitConnectionStatus Ready => new()
    {
        State = GitConnectionState.Connected, Message = "Git 2.47.1",
        Installation = new GitInstallationView(true, @"C:\Program Files\Git\cmd\git.exe", "2.47.1.windows.1", ["manager"], null),
    };

    private static GitSourceLink Link(string id, string environment, string project, string repository = "Contracts") => new()
    {
        Id = id, EnvironmentId = environment, BirkNextProject = project, Kind = "Local", LocalPath = @"C:\source\" + repository, OrganizationUrl = "local:c:/source/" + repository,
        Organization = "local", RepositoryId = "git-" + id, Repository = repository, Branch = "main", DefaultBranch = "main", LastFetchedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        SelectedBranches = ["main"],
    };

    public GitSourcesUiTests()
    {
        var repository = new WorkspaceArtifactRepository();
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "LendoPlus" } });
        _git.Setup(g => g.StatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Ready, (string?)null));
        _git.Setup(g => g.LinksAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[], (string?)null));
        _git.Setup(g => g.BranchesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<GitBranch>)[new GitBranch("main", Sha, DateTimeOffset.UtcNow, "A", "m", true), new GitBranch("release/1.0", Sha.Replace('0', 'f'), null, null, null, false)], (string?)null));
        Services.AddSingleton(_git.Object);
        Services.AddSingleton(context.Object);
        Services.AddSingleton(repository);
        Services.AddSingleton(new ProjectImportActivation(repository));
        Services.AddSingleton(Mock.Of<IProjectImportApiService>());
        Services.AddSingleton(Mock.Of<IWorkspaceAutoSaveService>());
        Services.AddSingleton(WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<BirkNext.Web.Services.Operations.BackgroundOperationService>(_ => new BirkNext.Web.Services.Operations.BackgroundOperationService());
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddScoped<ProjectApplicabilityState>();
        Services.AddScoped<ProjectImportSession>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Fetch_latest_is_disabled_with_its_reason_until_branches_are_chosen_and_the_picker_saves_the_choice()
    {
        // 2026-11-23: Fetch latest downloads only the selected branches; a repository added earlier has none, so it says why it cannot fetch.
        var link = Link("a", "dev", "LendoPlus") with { SelectedBranches = [] };
        _git.Setup(g => g.LinksAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[link], (string?)null));
        _git.Setup(g => g.AvailableBranchesAsync("a", "dev", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitBranch>)[
            new GitBranch("main", Sha, null, null, null, true), new GitBranch("release/1.0", Sha, null, null, null, false), new GitBranch("feature/x", Sha, null, null, null, false)], (string?)null));
        IReadOnlyList<string>? saved = null;
        _git.Setup(g => g.SelectBranchesAsync("a", "dev", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>((_, _, b, _) => saved = b)
            .ReturnsAsync((string _, string _, IReadOnlyList<string> b, CancellationToken _) => (link with { SelectedBranches = [.. b] }, (string?)null));
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForElement("[data-testid=gss-open]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-fetch-latest]").HasAttribute("disabled").Should().BeTrue());
        cut.Find("[data-testid=gss-fetch-disabled-reason]").TextContent.Should().Contain("no branch is selected").And.Contain("Branches to fetch");
        cut.Find("[data-testid=gss-fetch-latest]").GetAttribute("aria-describedby").Should().Be("gss-fetch-why-a");

        cut.Find("[data-testid=gbp-toggle]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=gbp-branch]").Should().HaveCount(3));
        cut.Find("[data-testid=gbp-search]").Input("rel");
        cut.FindAll("[data-testid=gbp-branch]").Should().ContainSingle().Which.GetAttribute("data-branch").Should().Be("release/1.0");
        cut.Find("[data-testid=gbp-select-shown]").Click();
        cut.Find("[data-testid=gbp-search]").Input("");
        cut.Find("[data-testid=gbp-branch][data-branch=main]").Change(true);
        cut.Find("[data-testid=gbp-save]").Click();

        cut.WaitForAssertion(() => saved.Should().Equal(["main", "release/1.0"], "the default branch first"));
        _git.Verify(g => g.FetchAsync("a", "dev", It.IsAny<CancellationToken>()), Times.Once(), "saving fetches the selection at once");
        cut.Find("[data-testid=gss-notice]").TextContent.Should().Contain("selected and fetched");
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-fetch-latest]").HasAttribute("disabled").Should().BeFalse());
        cut.FindAll("[data-testid=gss-fetch-disabled-reason]").Should().BeEmpty();
        cut.Find("[data-testid=gbp-toggle]").TextContent.Should().Contain("2 branches");
    }

    [Theory]
    [InlineData("zeta,develop,Alpha,master,feature/x,main,dev,development", "main,master,dev,develop,development,Alpha,feature/x,zeta")]
    [InlineData("release/1.0,develop,beta", "develop,beta,release/1.0")]
    [InlineData("release/1.0,Beta,alpha", "alpha,Beta,release/1.0")]
    [InlineData("feature/main-menu,MASTER,development", "MASTER,development,feature/main-menu")]
    public void Branches_list_main_or_master_first_then_the_development_line_then_the_rest_alphabetically(string input, string expected)
    {
        // 2026-11-23: main/master at the top, then dev/develop/development, then every other branch alphabetically (exact names only).
        GitBranchSelection.Ordered(input.Split(',')).Should().Equal(expected.Split(','));
        GitBranchSelection.Ordered(input.Split(',').Select(n => new GitBranch(n, Sha, null, null, null, false))).Select(b => b.Name).Should().Equal(expected.Split(','));
    }

    [Fact]
    public void Import_waits_until_the_current_branch_has_been_fetched_and_says_why()
    {
        // The current branch is selected but not in BirkNext's copy yet (repository added before 2026-11-23, or a failed fetch).
        var link = Link("a", "dev", "LendoPlus") with { Branch = "release/2.0", SelectedBranches = ["release/2.0"] };
        _git.Setup(g => g.LinksAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[link], (string?)null));
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForElement("[data-testid=gss-open]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-import]").HasAttribute("disabled").Should().BeTrue());
        cut.Find("[data-testid=gss-import-disabled-reason]").TextContent.Should().Contain("release/2.0 has not been fetched yet").And.Contain("Fetch latest");
        cut.Find("[data-testid=gss-import]").GetAttribute("aria-describedby").Should().Be("gss-import-why-a");
        cut.Find("[data-testid=gss-fetch-latest]").HasAttribute("disabled").Should().BeFalse("fetching is the way forward");
    }

    [Fact]
    public void The_branch_list_on_other_choices_shows_only_the_selected_branches_and_the_current_one()
    {
        var link = Link("a", "dev", "LendoPlus") with { SelectedBranches = ["main"] };
        GitBranchSelection.Visible(link, [new GitBranch("main", Sha, null, null, null, true), new GitBranch("release/1.0", Sha, null, null, null, false)])
            .Select(b => b.Name).Should().Equal(["main"]);
        GitBranchSelection.Visible(link with { SelectedBranches = [] }, [new GitBranch("main", Sha, null, null, null, true), new GitBranch("release/1.0", Sha, null, null, null, false)])
            .Should().HaveCount(2, "no selection = every branch");
        GitBranchSelection.Visible(link with { SelectedBranches = ["release/1.0"], Branch = "main" }, [new GitBranch("main", Sha, null, null, null, true), new GitBranch("release/1.0", Sha, null, null, null, false)])
            .Select(b => b.Name).Should().BeEquivalentTo(["main", "release/1.0"], "the current branch is never hidden");
    }

    [Fact]
    public void Settings_show_the_local_Git_and_never_ask_for_a_client_id_tenant_or_sign_in()
    {
        var cut = Render<GitSourcesSettingsCard>();

        cut.WaitForElement("[data-testid=gss-installation]");
        cut.Find("[data-testid=gss-overall]").TextContent.Trim().Should().Be("Git ready");
        cut.Find("[data-testid=gss-version]").TextContent.Should().Be("2.47.1.windows.1");
        cut.Find("[data-testid=gss-helper]").TextContent.Should().Be("manager");
        cut.Find("[data-testid=gss-credentials]").TextContent.Should().Contain("Git Credential Manager").And.Contain("never sees the password or token");
        foreach (var retired in new[] { "Client ID", "client id", "Tenant", "Entra", "Redirect URI", "Sign in with Microsoft", "app registration" })
            cut.Markup.Should().NotContain(retired, "Git-only source management has no BirkNext sign-in setup");
        cut.Find("[data-testid=gss-empty]");
        cut.FindAll("[data-testid=gar]").Should().ContainSingle("a repository can be added to the active project");
    }

    [Fact]
    public void Git_missing_is_a_prerequisite_with_instructions_not_a_configured_connection()
    {
        _git.Setup(g => g.StatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new GitConnectionStatus
        {
            State = GitConnectionState.NotConfigured, Installation = new GitInstallationView(false, null, null, [], GitSourcePolicy.Describe(GitFailureKind.GitMissing)),
        }, (string?)null));

        var cut = Render<GitSourcesSettingsCard>();

        cut.WaitForElement("[data-testid=gss-git-missing]").TextContent.Should().Contain("Install Git for Windows");
        cut.Find("[data-testid=gss-overall]").TextContent.Trim().Should().Be("Git not found");
        cut.FindAll("[data-testid=gar]").Should().BeEmpty();
        cut.Find("[data-testid=gss-recheck]");
    }

    [Fact]
    public void Repositories_of_every_project_are_listed_and_only_the_active_project_imports()
    {
        _git.Setup(g => g.LinksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IReadOnlyList<GitSourceLink>)[Link("a", "dev", "LendoPlus"), Link("b", "qa", "Other project", "Billing")], (string?)null));
        var cut = Render<GitSourcesSettingsCard>();

        cut.WaitForElements("[data-testid=gss-row]").Should().HaveCount(2);
        cut.Find("[data-testid=gss-count]").TextContent.Should().Be("2");

        cut.Find("[data-testid=gss-row][data-link=b] [data-testid=gss-open]").Click();
        cut.WaitForElement("[data-testid=gss-details][data-link=b]");
        cut.FindAll("[data-testid=gss-import]").Should().BeEmpty("another project's repository is never imported into the active workspace");
        cut.Find("[data-testid=gss-other-project]").TextContent.Should().Contain("Other project");

        cut.Find("[data-testid=gss-row][data-link=a] [data-testid=gss-open]").Click();
        cut.WaitForElement("[data-testid=gss-details][data-link=a]");
        cut.Find("[data-testid=gss-import]").TextContent.Should().Contain("Import main");
        cut.FindAll("[data-testid=gss-branch] option").Select(o => o.GetAttribute("value")).Should().Equal(["main"], "only the selected branches are offered (release/1.0 is not selected)");

        cut.Find("[data-testid=gss-search]").Input("billing");
        cut.FindAll("[data-testid=gss-row]").Should().ContainSingle().Which.GetAttribute("data-link").Should().Be("b");
    }

    [Fact]
    public void Fetch_latest_updates_the_repository_and_reports_the_branches()
    {
        var link = Link("a", "dev", "LendoPlus");
        _git.Setup(g => g.LinksAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[link with { LastFetchedAt = null }], (string?)null));
        _git.Setup(g => g.FetchAsync("a", "dev", It.IsAny<CancellationToken>())).ReturnsAsync((link, (string?)null));
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForElement("[data-testid=gss-open]").Click();
        cut.WaitForElement("[data-testid=gss-import]").HasAttribute("disabled").Should().BeTrue("nothing has been fetched yet");

        cut.Find("[data-testid=gss-fetch-latest]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-notice]").TextContent.Should().Be("Fetched: 2 branches."));
        cut.Find("[data-testid=gss-import]").HasAttribute("disabled").Should().BeFalse();
        _git.Verify(g => g.FetchAsync("a", "dev", It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void A_failed_fetch_explains_what_to_do_in_Git()
    {
        var failed = Link("a", "dev", "LendoPlus") with { LastFetchFailure = GitFailureKind.SignInRequired, LastFetchProblem = GitSourcePolicy.Describe(GitFailureKind.SignInRequired) };
        _git.Setup(g => g.LinksAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[failed], (string?)null));
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForElement("[data-testid=gss-open]").Click();

        cut.WaitForElement("[data-testid=gss-hint]").TextContent.Should().Contain("Git Credential Manager sign-in");
        cut.Find("[data-testid=gss-fetch]").TextContent.Should().StartWith("Fetch failed");
    }

    [Theory]
    [InlineData("https://user:secret@dev.azure.com/org/p/_git/r", "password or token")]
    [InlineData("file:///C:/source/repo", "local repositories")]
    [InlineData("ext::sh -c touch% /tmp/pwned", "characters")]
    [InlineData("-oProxyCommand=calc@host:repo", "characters")]
    [InlineData("git@-oProxyCommand=calc:repo", "Not a Git URL")]
    [InlineData("http://example.test/repo.git", "not supported")]
    public void The_add_form_refuses_unsafe_Git_URLs(string url, string reason)
    {
        var cut = Render<GitAddRepositoryForm>(p => p.Add(x => x.EnvironmentId, "dev"));
        cut.Find("[data-testid=gar-mode-url]").Click();

        cut.Find("[data-testid=gar-url]").Input(url);

        cut.Find("[data-testid=gar-url-problem]").TextContent.Should().Contain(reason);
        cut.Find("[data-testid=gar-add]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Adding_a_Git_URL_sends_the_canonical_URL_for_the_project()
    {
        GitAddRepositoryRequest? sent = null;
        _git.Setup(g => g.AddAsync(It.IsAny<GitAddRepositoryRequest>(), It.IsAny<CancellationToken>()))
            .Callback((GitAddRepositoryRequest r, CancellationToken _) => sent = r)
            .ReturnsAsync((Link("a", "dev", "LendoPlus"), (string?)null));
        GitSourceLink? added = null;
        var cut = Render<GitAddRepositoryForm>(p => p.Add(x => x.EnvironmentId, "dev").Add(x => x.ProjectName, "LendoPlus").Add(x => x.OnAdded, (GitSourceLink l) => added = l));
        cut.Find("[data-testid=gar-mode-url]").Click();
        cut.Find("[data-testid=gar-url]").Input("https://dev.azure.com/org/project/_git/repo");
        cut.FindAll("[data-testid=gar-url-problem]").Should().BeEmpty();

        cut.Find("[data-testid=gar-add]").Click();

        cut.WaitForAssertion(() => added.Should().NotBeNull());
        sent!.RemoteUrl.Should().Be("https://dev.azure.com/org/project/_git/repo");
        (sent.LocalPath, sent.EnvironmentId, sent.BirkNextProject).Should().Be(((string?)null, "dev", "LendoPlus"));
    }

    [Fact]
    public void A_local_folder_is_checked_before_it_can_be_added()
    {
        _git.Setup(g => g.InspectAsync(@"C:\source\Contracts", It.IsAny<CancellationToken>()))
            .ReturnsAsync((new GitLocalRepository("Contracts", @"C:\source\Contracts", "main", false, Sha, null, true, ["origin https://dev.azure.com/org/p/_git/Contracts"], false), (string?)null));
        var cut = Render<GitAddRepositoryForm>(p => p.Add(x => x.EnvironmentId, "dev"));
        cut.Find("[data-testid=gar-mode-local]").Click();
        cut.Find("[data-testid=gar-add]").HasAttribute("disabled").Should().BeTrue();

        cut.Find("[data-testid=gar-path]").Input(@"C:\source\Contracts");
        cut.Find("[data-testid=gar-inspect]").Click();

        cut.WaitForElement("[data-testid=gar-local]").TextContent.Should().Contain("uncommitted changes (not imported)").And.Contain("on main");
        cut.Find("[data-testid=gar-add]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Project_Import_queues_imports_with_the_project_and_the_exact_commit()
    {
        var link = Link("a", "dev", "LendoPlus") with { Roles = [nameof(PackageEvidenceRole.Contracts)] };
        _git.Setup(g => g.LinksAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[link], (string?)null));
        IReadOnlyList<string>? savedRoles = null;
        _git.Setup(g => g.UpdateLinkAsync("a", "dev", It.IsAny<string?>(), null, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string? _, string? _, IReadOnlyList<string>? r, CancellationToken _) => savedRoles = r)
            .ReturnsAsync((link, (string?)null));
        IReadOnlyList<(GitImportRequest Request, string Label)>? queued = null;
        var cut = Render<GitImportWizard>(p => p.Add(x => x.OnImport, (IReadOnlyList<(GitImportRequest, string)> q) => queued = q));

        // One connected repository: it is chosen already.
        cut.WaitForElement("[data-testid=giw-commit]").Change("NOT-A-SHA");
        cut.Find("[data-testid=giw-commit-problem]");
        cut.Find("[data-testid=giw-import]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=giw-commit]").Change(Sha.ToUpperInvariant());
        cut.Find("[data-testid=giw-role][data-role=Contracts]").HasAttribute("checked").Should().BeTrue("the repository's roles from its last import are ticked again");
        cut.Find("[data-testid=giw-import]").Click();

        cut.WaitForAssertion(() => queued.Should().NotBeNull());
        var request = queued!.Single().Request;
        (request.RepositoryId, request.Branch, request.Commit, request.EnvironmentId).Should().Be(("git-a", "main", Sha, "dev"));
        request.Roles.Should().Equal(nameof(PackageEvidenceRole.Contracts));
        savedRoles.Should().Equal(nameof(PackageEvidenceRole.Contracts));
        _git.Verify(g => g.FetchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never(), "an exact commit needs no new fetch");
    }

    [Fact]
    public void Adding_a_repository_starts_with_Clone_from_Git_URL()
    {
        var cut = Render<GitAddRepositoryForm>(p => p.Add(x => x.EnvironmentId, "dev"));

        cut.Find("[data-testid=gar-mode-url]").GetAttribute("aria-selected").Should().Be("true");
        cut.Find("[role=tablist] button").GetAttribute("data-testid").Should().Be("gar-mode-url", "the URL tab comes first");
        cut.Find("[data-testid=gar-url]");
        cut.FindAll("[data-testid=gar-path]").Should().BeEmpty();
    }

    [Fact]
    public void Project_Import_lists_the_connected_repositories_and_gets_the_latest_code_before_importing()
    {
        var a = Link("a", "dev", "LendoPlus");
        var b = Link("b", "dev", "LendoPlus", "Billing") with { Kind = "Remote", LocalPath = null, RemoteUrl = "https://dev.azure.com/org/p/_git/Billing", LastFetchedAt = null };
        _git.Setup(g => g.LinksAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[a, b], (string?)null));
        _git.Setup(g => g.FetchAsync("b", "dev", It.IsAny<CancellationToken>())).ReturnsAsync((b with { LastFetchedAt = DateTimeOffset.UtcNow }, (string?)null));
        _git.Setup(g => g.UpdateLinkAsync("b", "dev", It.IsAny<string?>(), null, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((b, (string?)null));
        IReadOnlyList<(GitImportRequest Request, string Label)>? queued = null;
        var cut = Render<GitImportWizard>(p => p.Add(x => x.OnImport, (IReadOnlyList<(GitImportRequest, string)> q) => queued = q));

        cut.WaitForElements("[data-testid=giw-repo-option]").Select(o => o.TextContent).Should().Equal(
            "Billing · main · https://dev.azure.com/org/p/_git/Billing", "Contracts · main · local folder");
        cut.FindAll("[data-testid=giw-chosen]").Should().BeEmpty("two repositories: the tester chooses");
        cut.FindAll("[data-testid=gar]").Should().BeEmpty("repositories are added in System Settings → Git Sources only");
        cut.Find("[data-testid=giw-manage]").GetAttribute("href").Should().Be("admin/system-settings?section=git-sources");

        cut.Find("[data-testid=giw-repo]").Change("b");
        cut.WaitForElement("[data-testid=giw-latest]").HasAttribute("checked").Should().BeTrue("the latest code is imported by default");
        cut.Find("[data-testid=giw-never-imported]");
        cut.Find("[data-testid=giw-roles] legend").TextContent.Should().Contain("What does this repository contain?");
        cut.FindAll("[data-testid=giw-import]").Should().BeEmpty("locked until the content question is answered, as Choose ZIP file");
        cut.FindAll("[data-testid=giw-roles-hint]").Should().BeEmpty("the reminder appears only after a click");
        cut.Find("[data-testid=giw-import-locked]").Click();
        cut.Find("[data-testid=giw-roles-hint]").TextContent.Should().Contain("Not sure — let BirkNext detect it");
        cut.Find("[data-testid=giw-detect]").Change(true);
        cut.FindAll("[data-testid=giw-roles-hint]").Should().BeEmpty();
        cut.Find("[data-testid=giw-import]").Click();

        cut.WaitForAssertion(() => queued.Should().NotBeNull());
        _git.Verify(g => g.FetchAsync("b", "dev", It.IsAny<CancellationToken>()), Times.Once(), "fetched before importing: a new snapshot of the current code");
        var request = queued!.Single().Request;
        (request.RepositoryId, request.Branch, request.Commit).Should().Be(("git-b", "main", (string?)null));
        request.Roles.Should().BeEmpty("Not sure: detected from the content after the import");
    }

    [Fact]
    public void Project_Import_without_a_connected_repository_points_to_Git_Sources_and_has_no_add_form()
    {
        var cut = Render<GitImportWizard>(p => p.Add(x => x.OnImport, (IReadOnlyList<(GitImportRequest, string)> _) => { }));

        cut.WaitForElement("[data-testid=giw-empty]").TextContent.Should().Contain("System Settings → Git Sources");
        cut.Find("[data-testid=giw-empty] a").GetAttribute("href").Should().Be("admin/system-settings?section=git-sources");
        cut.FindAll("[data-testid=gar]").Should().BeEmpty();
        cut.FindAll("[data-testid=giw-repo]").Should().BeEmpty();
    }

    [Fact]
    public void Project_Import_without_Git_points_to_System_Settings()
    {
        _git.Setup(g => g.StatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new GitConnectionStatus
        {
            State = GitConnectionState.NotConfigured, Message = "Git was not found.", Installation = new GitInstallationView(false, null, null, [], "Git was not found."),
        }, (string?)null));

        var cut = Render<GitImportWizard>(p => p.Add(x => x.OnImport, (IReadOnlyList<(GitImportRequest, string)> _) => { }));

        cut.WaitForElement("[data-testid=giw-not-connected]").InnerHtml.Should().Contain("admin/system-settings?section=git-sources");
    }
}
