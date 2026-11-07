using BirkNext.GitSources;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>Git Sources UI: opening reads only the sign-in state (no Azure DevOps call), configured is not verified, the explicit test shows every
/// step, disconnect keeps settings, and the Import Project picker loads repositories / branches only when asked and hands the choice over.</summary>
public sealed class GitSourcesUiTests : BunitContext
{
    const string Sha = "0123456789abcdef0123456789abcdef01234567";
    static readonly GitTestResult Passed = new(true, "Ready",
        [new("Microsoft Entra authentication", GitTestStepState.Verified, "Verified (tester@bufdir.no)", 12), new("Branch discovery", GitTestStepState.Verified, "4 branches", 30)],
        new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero), "bufdir", 3);

    private Mock<IGitSourcesApiService> Api(GitConnectionStatus status)
    {
        var api = new Mock<IGitSourcesApiService>();
        api.Setup(a => a.StatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((status, (string?)null));
        api.Setup(a => a.LinksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[], (string?)null));
        Services.AddSingleton(api.Object);
        return api;
    }


    // ── Six guided steps: the specification's state matrix ─────────────────────────────────────────────────────────────────

    private static GitConfigurationCheck Ok(string key) => key switch
    {
        "enabled" => new("enabled", "Microsoft sign-in enabled", GitCheckState.Ok, "AzureEnvironment:Enabled is true."),
        "client-id" => new("client-id", "App registration (client id)", GitCheckState.Ok, "Client id 11111111-2222-3333-4444-555555555555."),
        _ => new("tenant", "Tenant / authority", GitCheckState.Ok, "Authority https://login.microsoftonline.com/organizations."),
    };

    private static readonly IReadOnlyList<GitConfigurationCheck> Configured = [Ok("enabled"), Ok("client-id"), Ok("tenant")];
    private const string Org = "https://dev.azure.com/synthetic-org";

    private static string Steps(IRenderedComponent<GitSourcesSettingsCard> cut) =>
        string.Join(" ", cut.FindAll("[data-testid=gss-step]").Select(s => $"{s.GetAttribute("data-step")}:{s.GetAttribute("data-state")}"));

    private static string Step(IRenderedComponent<GitSourcesSettingsCard> cut, string id) => cut.Find($"[data-testid=gss-step][data-step={id}]").TextContent;










    [Fact]
    public void Connection_state_vocabulary_follows_the_evidence()
    {
        var failed = new GitTestResult(false, "Not ready", [new("Azure DevOps organization", GitTestStepState.Failed, "Not found (404)", 4)], DateTimeOffset.UtcNow, "x", null);
        var other = new GitTestResult(false, "Not ready", [new("Repository discovery", GitTestStepState.Failed, "Timed out", 4)], DateTimeOffset.UtcNow, "x", null);
        GitSourcesPresentation.ConnectionState(new() { State = GitConnectionState.Connected, LastTest = failed }).Should().Be("Organization not accessible");
        GitSourcesPresentation.ConnectionState(new() { State = GitConnectionState.Connected, LastTest = other }).Should().Be("Failed");
        GitSourcesPresentation.ConnectionState(new() { State = GitConnectionState.Connected }, testing: true).Should().Be("Testing");
        GitSourcesPresentation.SignInState(new() { State = GitConnectionState.SigningIn }).Should().Be("Signing in");
        GitSourcesPresentation.SignInState(new() { State = GitConnectionState.Failed }).Should().Be("Sign-in failed");
        GitSourcesPresentation.SignInState(new() { State = GitConnectionState.NotConfigured }).Should().Be("Not configured");
        GitSourcesPresentation.OrganizationProblem("https://contoso.visualstudio.com").Should().BeNull();
        GitSourcesPresentation.TenantProblem(true, "common").Should().NotBeNull("only Organizations or one specific tenant");
        GitSourcesPresentation.TenantProblem(true, "contoso.no").Should().BeNull();
        GitSourcesPresentation.ClientIdProblem(false, " 11111111-2222-3333-4444-555555555555 ").Should().BeNull("whitespace is trimmed");
    }

    private void ActiveProject()
    {
        var contexts = new Mock<IFrontendAnalysisContextFactory>();
        contexts.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Project = new EnvironmentProject { ProjectName = "M2LB" } },
        });
        Services.AddSingleton(contexts.Object);
    }

    [Fact]
    public void Import_wizard_browses_organization_project_repositories_links_each_and_queues_the_imports()
    {
        ActiveProject();
        var api = Api(new() { State = GitConnectionState.Connected, Account = "tester@bufdir.no", Message = "Signed in." });
        const string Org = "https://dev.azure.com/bufdir";
        api.Setup(a => a.LinksAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[], (string?)null));
        api.Setup(a => a.OrganizationsAsync(It.IsAny<CancellationToken>())).ReturnsAsync((
            (IReadOnlyList<GitOrganization>)[new GitOrganization("a1", "bufdir", Org, true), new GitOrganization("a2", "other", "https://dev.azure.com/other", true)], (string?)null, (string?)null));
        api.Setup(a => a.ProjectsAsync(Org, null, It.IsAny<CancellationToken>())).ReturnsAsync((new GitProjectPage([new GitProject("p1", "M2LB", "wellFormed")], "next-1"), (string?)null));
        api.Setup(a => a.ProjectsAsync(Org, "next-1", It.IsAny<CancellationToken>())).ReturnsAsync((new GitProjectPage([new GitProject("p2", "Meldingsutveksler", null)], null), (string?)null));
        api.Setup(a => a.ProjectRepositoriesAsync(Org, "p1", It.IsAny<CancellationToken>())).ReturnsAsync((
            (IReadOnlyList<GitRepository>)[new GitRepository("r1", "M2LB.Common", "M2LB", "main", null, false), new GitRepository("r2", "M2LB.Infra", "M2LB", "main", null, false)], (string?)null));
        api.Setup(a => a.BranchesInAsync(Org, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((
            (IReadOnlyList<GitBranch>)[new GitBranch("feature/rapport", Sha, new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero), "B", "recent", false), new GitBranch("main", new string('a', 40), null, null, null, true)], (string?)null));
        var links = new List<GitLinkRequest>();
        api.Setup(a => a.LinkAsync(It.IsAny<GitLinkRequest>(), It.IsAny<CancellationToken>()))
            .Callback((GitLinkRequest r, CancellationToken _) => links.Add(r)).ReturnsAsync((new GitSourceLink { Id = "x" }, (string?)null));
        IReadOnlyList<(GitImportRequest Request, string Label)>? queued = null;
        var cut = Render<GitImportWizard>(p => p.Add(x => x.OnImport, (IReadOnlyList<(GitImportRequest Request, string Label)> q) => queued = q));

        cut.WaitForElement("[data-testid=giw-load-orgs]");
        api.Verify(a => a.OrganizationsAsync(It.IsAny<CancellationToken>()), Times.Never(), "opening reads only the sign-in state");
        cut.Find("[data-testid=giw-load-orgs]").Click();
        cut.WaitForElement("[data-testid=giw-org]").Change(Org);
        cut.WaitForElement("[data-testid=giw-more-projects]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=giw-project] option").Select(o => o.TextContent).Should().Equal(["Choose…", "M2LB", "Meldingsutveksler"]));
        cut.Find("[data-testid=giw-project]").Change("p1");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=giw-repo]").Should().HaveCount(2));

        cut.Find("[data-testid=giw-repo][data-repo=r1] [data-testid=giw-repo-check]").Change(true);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=giw-branch] option").Select(o => o.GetAttribute("value")).Should().Equal(["feature/rapport", "main"]));
        cut.Find("[data-testid=giw-branch]").Change("feature/rapport");
        cut.Find("[data-testid=giw-role][data-role=Infrastructure]").Change(true);
        cut.Find("[data-testid=giw-role][data-role=Contracts]").Change(true);
        cut.Find("[data-testid=giw-repo][data-repo=r2] [data-testid=giw-repo-check]").Change(true);
        cut.WaitForAssertion(() => cut.Find("[data-testid=giw-selected-count]").TextContent.Should().Be("2 selected"));
        cut.FindAll("[data-testid=giw-review-row]").Should().HaveCount(2);

        cut.Find("[data-testid=giw-import]").Click();
        cut.WaitForAssertion(() => queued.Should().NotBeNull());
        links.Select(l => (l.RepositoryId, l.Branch, l.EnvironmentId, l.BirkNextProject)).Should().Equal([("r1", "feature/rapport", "dev", "M2LB"), ("r2", "main", "dev", "M2LB")],
            "each repository is linked by its stable id to the active BirkNext project");
        links[0].Roles.Should().BeEquivalentTo(["Infrastructure", "Contracts"], "several roles per repository");
        queued!.Select(q => (q.Request.RepositoryId, q.Request.Branch, q.Request.OrganizationUrl, q.Label))
            .Should().Equal([("r1", "feature/rapport", Org, "M2LB.Common @ feature/rapport"), ("r2", "main", Org, "M2LB.Infra @ main")]);
    }

    [Fact]
    public void Import_wizard_rejects_a_short_commit_and_blocks_import_until_fixed()
    {
        ActiveProject();
        var api = Api(new() { State = GitConnectionState.Connected, Message = "Signed in." });
        const string Org = "https://dev.azure.com/bufdir";
        api.Setup(a => a.OrganizationsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitOrganization>)[new GitOrganization("a1", "bufdir", Org, true)], (string?)null, (string?)null));
        api.Setup(a => a.ProjectsAsync(Org, null, It.IsAny<CancellationToken>())).ReturnsAsync((new GitProjectPage([new GitProject("p1", "M2LB", null)], null), (string?)null));
        api.Setup(a => a.ProjectRepositoriesAsync(Org, "p1", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitRepository>)[new GitRepository("r1", "M2LB.Common", "M2LB", "main", null, false)], (string?)null));
        var cut = Render<GitImportWizard>(p => p.Add(x => x.OnImport, (IReadOnlyList<(GitImportRequest Request, string Label)> _) => { }));
        cut.WaitForElement("[data-testid=giw-load-orgs]").Click();
        cut.WaitForElement("[data-testid=giw-project]").Change("p1");
        cut.WaitForElement("[data-testid=giw-repo-check]").Change(true);
        cut.WaitForElement("[data-testid=giw-commit]").Change("abc123");
        cut.WaitForAssertion(() => cut.Find("[data-testid=giw-commit-problem]").TextContent.Should().Contain("40-character"));
        cut.Find("[data-testid=giw-import]").HasAttribute("disabled").Should().BeTrue();
        api.Verify(a => a.CommitAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public void Import_wizard_sends_people_to_settings_when_not_connected()
    {
        ActiveProject();
        var api = Api(new() { State = GitConnectionState.NotConnected, Message = "Not connected." });
        var cut = Render<GitImportWizard>(p => p.Add(x => x.OnImport, (IReadOnlyList<(GitImportRequest Request, string Label)> _) => { }));
        cut.WaitForAssertion(() => cut.Find("[data-testid=giw-not-connected]").InnerHtml.Should().Contain("admin/system-settings?section=git-sources"));
        api.Verify(a => a.OrganizationsAsync(It.IsAny<CancellationToken>()), Times.Never());
    }


    // ── The screenshot state: Entra not enabled, no client id, organization URL set ─────────────────────────────────────────

    private static GitConnectionStatus NotSetUp() => new()
    {
        State = GitConnectionState.NotConfigured, OrganizationUrl = "https://dev.azure.com/bufdir", Message = "Microsoft sign-in is not configured on this installation.",
        Diagnostics =
        [
            new("enabled", "Azure environment (AzureEnvironment:Enabled)", GitCheckState.Missing, "Disabled.", "An administrator enables AzureEnvironment:Enabled."),
            new("client-id", "App registration (AzureEnvironment:ClientId)", GitCheckState.Missing, "Not set.", "An administrator sets the app registration client id."),
            new("organization", "Azure DevOps organization", GitCheckState.Ok, "https://dev.azure.com/bufdir"),
        ],
    };




    [Fact]
    public void A_failed_or_timed_out_test_is_never_shown_as_access()
    {
        var failed = new GitTestResult(false, "Timed out", [new("Azure DevOps organization", GitTestStepState.Failed, "Timed out after 30 s", 30000)], DateTimeOffset.UtcNow, "bufdir", null);
        var status = new GitConnectionStatus { State = GitConnectionState.Connected, OrganizationUrl = "https://dev.azure.com/bufdir", LastTest = failed };
        GitSourcesPresentation.AccessText(status).Should().StartWith("Not verified by the read-only Git test");
        GitSourcesPresentation.HealthSummary(status).Should().Be("Configuration health — all prerequisites in place");
    }

    // ── Steps 1-2: the configuration form (saved to BirkNext's own App_Data settings, applied while BirkNext runs) ─────────────────────

    private Mock<AdminApiService> Admin(MicrosoftSignInInfoDto signIn)
    {
        var admin = new Mock<AdminApiService>(new HttpClient()) { CallBase = false };
        admin.Setup(a => a.GetSystemSettingsAsync()).ReturnsAsync(new SystemSettingsDto { MicrosoftSignIn = signIn });
        Services.AddSingleton(admin.Object);
        return admin;
    }






}
