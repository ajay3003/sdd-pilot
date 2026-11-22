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
        JSInterop.Mode = JSRuntimeMode.Loose;
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
        cut.FindAll("[data-testid=gss-branch] option").Select(o => o.GetAttribute("value")).Should().Equal("main", "release/1.0");

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
        var link = Link("a", "dev", "LendoPlus");
        _git.Setup(g => g.LinksAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[link], (string?)null));
        _git.Setup(g => g.UpdateLinkAsync("a", "dev", It.IsAny<string?>(), null, It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((link, (string?)null));
        IReadOnlyList<(GitImportRequest Request, string Label)>? queued = null;
        var cut = Render<GitImportWizard>(p => p.Add(x => x.OnImport, (IReadOnlyList<(GitImportRequest, string)> q) => queued = q));

        // One connected repository: it is chosen already.
        cut.WaitForElement("[data-testid=giw-commit]").Change("NOT-A-SHA");
        cut.Find("[data-testid=giw-commit-problem]");
        cut.Find("[data-testid=giw-import]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=giw-commit]").Change(Sha.ToUpperInvariant());
        cut.Find("[data-testid=giw-import]").Click();

        cut.WaitForAssertion(() => queued.Should().NotBeNull());
        var request = queued!.Single().Request;
        (request.RepositoryId, request.Branch, request.Commit, request.EnvironmentId).Should().Be(("git-a", "main", Sha, "dev"));
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
        cut.Find("[data-testid=giw-import]").Click();

        cut.WaitForAssertion(() => queued.Should().NotBeNull());
        _git.Verify(g => g.FetchAsync("b", "dev", It.IsAny<CancellationToken>()), Times.Once(), "fetched before importing: a new snapshot of the current code");
        var request = queued!.Single().Request;
        (request.RepositoryId, request.Branch, request.Commit).Should().Be(("git-b", "main", (string?)null));
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
