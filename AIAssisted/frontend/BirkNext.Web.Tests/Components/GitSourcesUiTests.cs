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

    [Fact]
    public void Settings_card_reads_the_sign_in_state_only_and_tests_on_request()
    {
        var api = Api(new() { State = GitConnectionState.Connected, Account = "tester@bufdir.no", OrganizationUrl = "https://dev.azure.com/bufdir", Message = "Signed in." });
        api.Setup(a => a.TestAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Passed, (string?)null));
        var cut = Render<GitSourcesSettingsCard>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-state]").GetAttribute("data-state").Should().Be("Connected"));
        cut.Find("[data-testid=gss-last-test]").TextContent.Should().StartWith("Not verified yet — a Microsoft sign-in alone does not prove repository access",
            "signed in to Microsoft is not Azure DevOps access");
        cut.Find("[data-testid=gss-org]").GetAttribute("value").Should().Be("https://dev.azure.com/bufdir");
        api.Verify(a => a.RepositoriesAsync(It.IsAny<CancellationToken>()), Times.Never(), "opening the section never contacts Azure DevOps");
        api.Verify(a => a.TestAsync(It.IsAny<CancellationToken>()), Times.Never());

        cut.Find("[data-testid=gss-test]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-test-result]").GetAttribute("data-ready").Should().Be("true"));
        cut.FindAll("[data-testid=gss-test-step]").Select(s => s.TextContent).Should().Contain(t => t.Contains("Microsoft Entra authentication") && t.Contains("✓ Verified"));
        cut.Markup.Should().NotContain("Bearer");
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
    public void State1_disabled_without_client_id_is_setup_required_with_exact_administrator_instructions()
    {
        Api(NotSetUp());
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetVoidResult();
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-steps]"));
        Steps(cut).Should().Be("provider:Current application:Waiting organization:Done authentication:Waiting test:Waiting import:Waiting");
        cut.Find("[data-testid=gss-summary]").TextContent.Should().Be("Git Sources: Setup required · Entra: Client ID missing · Azure DevOps: Not configured");
        Step(cut, "organization").Should().Contain("saved, not yet proof of access");
        cut.Find("[data-testid=gss-provider]").TextContent.Should().Be("Azure DevOps");
        cut.Find("[data-testid=gss-auth-method]").TextContent.Should().Be("Microsoft Entra ID — delegated interactive authentication");
        cut.Find("[data-testid=gss-redirect]").TextContent.Should().Be("http://localhost");
        cut.Find("[data-testid=gss-permission]").TextContent.Should().Contain("499b84ac-1321-427f-aa17-267ca6975798").And.Contain("user_impersonation").And.Contain("/.default");
        var request = cut.Find("[data-testid=gss-registration-text]").TextContent;
        request.Should().Contain("Mobile and desktop applications").And.Contain("Redirect URI: http://localhost").And.Contain("user_impersonation")
            .And.Contain("No client secret").And.Contain("Grant admin consent").And.Contain("read permission on the repositories");
        cut.Find("[data-testid=gss-registration-copy]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-registration-copied]"));
        JSInterop.VerifyInvoke("navigator.clipboard.writeText").Arguments.Single().Should().Be(GitSourcesPresentation.RegistrationRequest);
        cut.Find("[data-testid=gss-test]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=gss-connection-state]").TextContent.Should().Be("Not configured");
        cut.FindAll("[data-testid=gss-sign-in]").Should().BeEmpty();
        cut.Find("[data-testid=gss-import-reason]").TextContent.Should().Contain("after a passed connection test");
        cut.FindAll("[data-testid=gss-requirements]").Should().BeEmpty("the administrator checklist of configuration keys is gone");
    }

    [Fact]
    public void State2_enabled_without_client_id_is_configuration_incomplete()
    {
        Api(new() { State = GitConnectionState.NotConfigured, Diagnostics = [Ok("enabled"),
            new("client-id", "App registration (client id)", GitCheckState.Missing, "No Entra app registration is configured (AzureEnvironment:ClientId)."), Ok("tenant")] });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-steps]"));
        Steps(cut).Should().StartWith("provider:Done application:Current");
        Step(cut, "application").Should().Contain("Configuration incomplete");
    }

    [Fact]
    public void State3_valid_client_without_organization_asks_for_the_organization_and_validates_it()
    {
        var api = Api(new() { State = GitConnectionState.NotConnected, SignInAvailable = true, Diagnostics = Configured });
        api.Setup(a => a.SaveSettingsAsync(It.IsAny<GitSourceSettings>(), It.IsAny<CancellationToken>())).ReturnsAsync((new GitConnectionStatus { State = GitConnectionState.NotConnected }, (string?)null));
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-steps]"));
        Steps(cut).Should().StartWith("provider:Done application:Done organization:Current authentication:Waiting");
        foreach (var bad in new[] { "http://dev.azure.com/x", "https://dev.azure.com/x/project", "https://user@dev.azure.com/x", "https://example.com/x" })
        {
            cut.Find("[data-testid=gss-org]").Input(bad);
            cut.Find("[data-testid=gss-org-hint]").TextContent.Should().NotBeEmpty(bad);
            cut.Find("[data-testid=gss-save]").HasAttribute("disabled").Should().BeTrue(bad);
        }
        cut.Find("[data-testid=gss-org]").Input(" https://dev.azure.com/Synthetic-Org/ ");
        cut.FindAll("[data-testid=gss-org-hint]").Should().BeEmpty();
        cut.Find("[data-testid=gss-save]").Click();
        api.Verify(a => a.SaveSettingsAsync(It.Is<GitSourceSettings>(s => s.OrganizationUrl == "https://dev.azure.com/Synthetic-Org"), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void State4_configured_not_signed_in_offers_sign_in_and_no_test()
    {
        var api = Api(new() { State = GitConnectionState.NotConnected, SignInAvailable = true, OrganizationUrl = Org, Diagnostics = Configured });
        api.Setup(a => a.SignInAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new GitConnectionStatus { State = GitConnectionState.SigningIn, Message = "Complete sign-in." }, (string?)null));
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-steps]"));
        Steps(cut).Should().Be("provider:Done application:Done organization:Done authentication:Current test:Waiting import:Waiting");
        Step(cut, "application").Should().Contain("Client ID entered").And.Contain("Not verified yet");
        cut.Find("[data-testid=gss-signin-state]").TextContent.Should().Be("Ready to sign in");
        cut.Find("[data-testid=gss-connection-state]").TextContent.Should().Be("Sign-in required");
        cut.Find("[data-testid=gss-test]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=gss-test-reason]").TextContent.Should().Contain("Sign in first");
        cut.Find("[data-testid=gss-consent]").TextContent.Should().Contain("not observed yet");
        cut.Find("[data-testid=gss-sign-in]").Click();
        api.Verify(a => a.SignInAsync(It.IsAny<CancellationToken>()), Times.Once(), "sign-in is user initiated: never on load or save");
    }

    [Fact]
    public void State5_missing_consent_blocks_authentication_and_names_the_administrator_action()
    {
        Api(new() { State = GitConnectionState.Failed, SignInAvailable = true, OrganizationUrl = Org, Diagnostics = [.. Configured,
            new("consent", "Delegated Azure DevOps permission", GitCheckState.Failed, "Consent is required for the delegated Azure DevOps permission (AADSTS65001/90094).",
                "An administrator grants consent for user_impersonation on Azure DevOps in the app registration, or allows user consent.")] });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-steps]"));
        cut.Find("[data-testid=gss-step][data-step=authentication]").GetAttribute("data-state").Should().Be("Blocked");
        cut.Find("[data-testid=gss-signin-state]").TextContent.Should().Be("Consent required");
        cut.Find("[data-testid=gss-consent]").TextContent.Should().Contain("required").And.Contain("AADSTS65001");
        cut.Find("[data-testid=gss-sign-in]").TextContent.Should().Be("Retry sign-in");
    }

    [Fact]
    public void States6_7_9_10_signed_in_test_then_discover_projects_repositories_and_the_actual_default_branch()
    {
        var connected = new GitConnectionStatus { State = GitConnectionState.Connected, Account = "tester@synthetic.example", OrganizationUrl = Org, Diagnostics = Configured };
        var api = Api(connected);
        api.Setup(a => a.TestAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Passed, (string?)null))
            .Callback(() => api.Setup(a => a.StatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((connected with { LastTest = Passed }, (string?)null)));
        api.Setup(a => a.ProjectsAsync(Org, null, It.IsAny<CancellationToken>())).ReturnsAsync((new GitProjectPage([new GitProject("p1", "Synthetic", "wellFormed"), new GitProject("p2", "Other", null)], null), (string?)null));
        api.Setup(a => a.ProjectRepositoriesAsync(Org, "p1", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitRepository>)[
            new GitRepository("r1", "Synthetic.Service", "Synthetic", "develop", null, false), new GitRepository("r2", "Synthetic.Archive", "Synthetic", "main", null, true)], (string?)null));
        api.Setup(a => a.BranchesInAsync(Org, "r1", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitBranch>)[
            new GitBranch("main", Sha, null, null, null, false), new GitBranch("develop", new string('b', 40), null, null, null, true)], (string?)null));
        api.Setup(a => a.SaveSettingsAsync(It.IsAny<GitSourceSettings>(), It.IsAny<CancellationToken>())).ReturnsAsync((connected, (string?)null));
        var cut = Render<GitSourcesSettingsCard>();

        cut.WaitForAssertion(() => Steps(cut).Should().Be("provider:Done application:Done organization:Done authentication:Done test:Current import:Waiting"));
        Step(cut, "application").Should().Contain("Confirmed by sign-in");
        cut.Find("[data-testid=gss-consent]").TextContent.Should().Contain("observed");
        cut.Find("[data-testid=gss-connection-state]").TextContent.Should().Be("Not tested");
        cut.FindAll("[data-testid=gss-load-projects]").Should().BeEmpty("no discovery before a passed test");
        cut.Find("[data-testid=gss-test]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-connection-state]").TextContent.Should().Be("Connected"));
        cut.Find("[data-testid=gss-last-success]").TextContent.Should().Contain("last verified");
        Steps(cut).Should().EndWith("test:Done import:Current");

        cut.Find("[data-testid=gss-load-projects]").Click();
        cut.WaitForElement("[data-testid=gss-project-filter]").Input("synth");
        cut.FindAll("[data-testid=gss-project] option").Select(o => o.TextContent).Should().Equal("Choose…", "Synthetic");
        cut.Find("[data-testid=gss-project]").Change("p1");
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-repo] option[value=r2]").HasAttribute("disabled").Should().BeTrue());
        cut.Find("[data-testid=gss-repo]").Change("r1");
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-branch]").GetAttribute("value").Should().Be("develop", "the repository's actual default branch, never an assumed main"));
        cut.Find("[data-testid=gss-import]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=gss-track]").Click();
        api.Verify(a => a.SaveSettingsAsync(It.Is<GitSourceSettings>(s => s.TrackedBranches.Single().Branch == "develop"), It.IsAny<CancellationToken>()), Times.Once());
        cut.Find("[data-testid=gss-import]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-error]").TextContent.Should().Contain("Import Project is not available"));
        api.Verify(a => a.ImportAsync(It.IsAny<GitImportRequest>(), It.IsAny<CancellationToken>()), Times.Never(), "import runs only through Import Project's session");
    }

    [Fact]
    public void State8_denied_access_shows_permission_denied_and_no_repositories()
    {
        Api(new() { State = GitConnectionState.AccessDenied, Account = "tester@synthetic.example", OrganizationUrl = Org, Diagnostics = Configured });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-step][data-step=test]").GetAttribute("data-state").Should().Be("Blocked"));
        cut.Find("[data-testid=gss-connection-state]").TextContent.Should().Be("Permission denied");
        Step(cut, "test").Should().Contain("Azure DevOps organization administrator");
        cut.FindAll("[data-testid=gss-load-projects]").Should().BeEmpty();
        cut.FindAll("[data-testid=gss-repo]").Should().BeEmpty();
    }

    [Fact]
    public void State12_a_test_from_other_settings_is_stale_and_never_counts_as_connected()
    {
        Api(new() { State = GitConnectionState.Connected, OrganizationUrl = Org, Diagnostics = Configured, LastTest = Passed, LastTestStale = true,
            TrackedBranches = [new("r1", "Synthetic.Service", "Synthetic", "develop")] });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-connection-state]").TextContent.Should().Be("Previous result stale"));
        cut.Find("[data-testid=gss-test-stale]").TextContent.Should().Contain("no longer proves access");
        cut.Find("[data-testid=gss-test-result]").GetAttribute("data-ready").Should().Be("false");
        cut.Find("[data-testid=gss-step][data-step=test]").GetAttribute("data-state").Should().Be("Current");
        cut.FindAll("[data-testid=gss-last-success]").Should().BeEmpty();
        cut.FindAll("[data-testid=gss-load-projects]").Should().BeEmpty();
        cut.Find("[data-testid=gss-fetch-latest]").HasAttribute("disabled").Should().BeTrue("fetching needs current verified access");
    }

    [Fact]
    public void State13_an_expired_session_needs_sign_in_and_is_never_connected()
    {
        Api(new() { State = GitConnectionState.ReconnectRequired, SignInAvailable = true, OrganizationUrl = Org, Diagnostics = Configured, LastTest = Passed });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-state]").TextContent.Should().Be("Session expired"));
        cut.Find("[data-testid=gss-sign-in]").TextContent.Should().Be("Sign in again");
        cut.Find("[data-testid=gss-connection-state]").TextContent.Should().Be("Sign-in required");
        cut.Find("[data-testid=gss-test]").HasAttribute("disabled").Should().BeTrue();
    }

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

    [Fact]
    public void Settings_card_shows_configuration_health_and_the_device_code()
    {
        Api(new()
        {
            State = GitConnectionState.AwaitingDeviceCode, Message = "Enter the code.", DeviceCode = new GitDeviceCode("ABCD-1234", "https://microsoft.com/devicelogin", DateTimeOffset.UtcNow.AddMinutes(10)),
            Diagnostics = [new("client-id", "App registration (client id)", GitCheckState.Ok, "Client id x."),
                new("consent", "Delegated Azure DevOps permission", GitCheckState.Failed, "Consent is required.", "An administrator grants consent.")],
        });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-device-code]").TextContent.Should().Contain("ABCD-1234"));
        cut.Find("[data-testid=gss-diagnostics] summary").TextContent.Should().Contain("1 check failed", "a failed check is counted apart from administrator prerequisites");
        cut.Find("[data-testid=gss-check][data-key=consent]").TextContent.Should().Contain("Administrator: An administrator grants consent.");
        cut.Find("[data-testid=gss-check][data-key=client-id]").TextContent.Should().NotContain("Administrator");
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
    public void Missing_configuration_is_a_setup_step_not_a_failure_and_every_disabled_action_explains_itself()
    {
        Api(NotSetUp());
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-health-summary]"));
        cut.Find("[data-testid=gss-health-summary]").TextContent.Should().Be("Configuration health — 2 setup steps not done — see Microsoft sign-in setup");
        cut.Find("[data-testid=gss-health-summary]").GetAttribute("data-failures").Should().Be("0", "missing configuration is not a runtime failure");
        cut.Find("[data-testid=gss-last-test]").TextContent.Should().StartWith("Not available yet").And.NotContain("Not tested");
        cut.Find("[data-testid=gss-test]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=gss-test]").GetAttribute("aria-describedby").Should().Be("gss-test-reason");
        cut.Find("[data-testid=gss-test-reason]").TextContent.Should().Contain("Microsoft Entra application is set up");
        cut.Find("[data-testid=gss-check][data-key=client-id]").TextContent.Should().Contain("✕ App registration").And.Contain("Set up here:");
        cut.FindAll("[data-testid=gss-sign-in]").Should().BeEmpty("signing in cannot work before the app registration exists");
    }

    [Fact]
    public void Not_signed_in_without_a_usable_sign_in_names_the_missing_prerequisite()
    {
        Api(new() { State = GitConnectionState.NotConnected, SignInAvailable = false, OrganizationUrl = "https://dev.azure.com/bufdir",
            Diagnostics = [new("redirect", "Redirect URI", GitCheckState.Invalid, "Not registered for this address.", "An administrator registers the redirect URI.")] });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-sign-in-reason]").TextContent.Should().Contain("Redirect URI"));
        cut.Find("[data-testid=gss-sign-in]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=gss-last-test]").TextContent.Should().StartWith("Not tested — sign in");
    }

    [Fact]
    public void A_missing_organization_and_denied_access_are_said_plainly()
    {
        Api(new() { State = GitConnectionState.Connected, Account = "tester@bufdir.no" });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-test-reason]").TextContent.Should().Contain("organization URL"));
        GitSourcesPresentation.TestBlockedReason(new() { State = GitConnectionState.NotConnected }).Should().Contain("Sign in first", "signing in comes before the organization");

        var denied = new GitConnectionStatus { State = GitConnectionState.AccessDenied, OrganizationUrl = "https://dev.azure.com/bufdir", Account = "tester@bufdir.no" };
        GitSourcesPresentation.AccessText(denied).Should().StartWith("Denied");
        GitSourcesPresentation.TestBlockedReason(denied).Should().BeNull();
    }

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

    [Fact]
    public void Saving_enable_client_and_a_specific_tenant_persists_them_and_rereads_the_state()
    {
        var api = Api(NotSetUp());
        var admin = Admin(new() { Enabled = false, ClientId = "", TenantId = "", AllowDeviceCode = true });
        SaveSettingsRequest? sent = null;
        admin.Setup(a => a.SaveSettingsAsync(It.IsAny<SaveSettingsRequest>())).ReturnsAsync((true, "Microsoft sign-in settings saved and applied. No restart is needed."))
            .Callback<SaveSettingsRequest>(r =>
            {
                sent = r;
                admin.Setup(a => a.GetSystemSettingsAsync()).ReturnsAsync(new SystemSettingsDto { MicrosoftSignIn = new()
                    { Enabled = true, ClientId = "11111111-2222-3333-4444-555555555555", TenantId = "bufdir.no", AllowDeviceCode = true } });
            });
        var cut = Render<GitSourcesSettingsCard>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-save]").HasAttribute("disabled").Should().BeTrue("nothing changed yet"));
        cut.Find("[data-testid=gss-signin-enabled]").Change(true);
        cut.Find("[data-testid=gss-signin-client]").Input(" 11111111-2222-3333-4444-555555555555 ");
        cut.Find("[data-testid=gss-tenant-mode]").Change("specific");
        cut.Find("[data-testid=gss-signin-tenant]").Input("bufdir.no");
        cut.FindAll("[data-testid=gss-signin-save]")[0].Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-message]").TextContent.Should().Contain("applied"));
        sent!.MicrosoftSignIn.Should().BeEquivalentTo(new SaveMicrosoftSignInRequest { Enabled = true, ClientId = "11111111-2222-3333-4444-555555555555", TenantId = "bufdir.no", AllowDeviceCode = true });
        sent.FeatureVisibility.Should().BeNull("only the sign-in values are written");
        api.Verify(a => a.StatusAsync(It.IsAny<CancellationToken>()), Times.Exactly(2), "after a save the Git Sources state is read again, never left stale");
        api.Verify(a => a.SignInAsync(It.IsAny<CancellationToken>()), Times.Never(), "saving never starts a sign-in");
        cut.FindAll("[data-testid=gss-signin-restart]").Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, "", "organizations", "", "before enabling Git Sources")]
    [InlineData(false, "not-a-guid", "organizations", "", "Application (Client) ID from your Microsoft Entra app registration")]
    [InlineData(false, "11111111-2222-3333-4444-555555555555", "specific", "not a tenant!", "Directory (tenant) ID")]
    [InlineData(false, "11111111-2222-3333-4444-555555555555", "specific", "common", "Directory (tenant) ID")]
    public void Invalid_input_is_explained_inline_and_cannot_be_saved(bool enabled, string clientId, string mode, string tenant, string problem)
    {
        Api(NotSetUp());
        var admin = Admin(new() { AllowDeviceCode = true });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-client]"));
        cut.Find("[data-testid=gss-signin-enabled]").Change(enabled);
        cut.Find("[data-testid=gss-signin-client]").Input(clientId);
        cut.Find("[data-testid=gss-tenant-mode]").Change(mode);
        if (mode == "specific") cut.Find("[data-testid=gss-signin-tenant]").Input(tenant);

        var hints = string.Join(" ", cut.FindAll("[data-testid=gss-client-hint], [data-testid=gss-tenant-hint]").Select(h => h.TextContent));
        hints.Should().Contain(problem);
        cut.FindAll("[data-testid=gss-signin-save]").Should().OnlyContain(b => b.HasAttribute("disabled"));
        admin.Verify(a => a.SaveSettingsAsync(It.IsAny<SaveSettingsRequest>()), Times.Never());
    }

    [Fact]
    public void Revert_restores_the_saved_values_and_a_refused_save_shows_the_backend_reason()
    {
        Api(new() { State = GitConnectionState.NotConnected, SignInAvailable = true, OrganizationUrl = Org, Diagnostics = Configured });
        var admin = Admin(new() { Enabled = true, ClientId = "11111111-2222-3333-4444-555555555555", AllowDeviceCode = true });
        admin.Setup(a => a.SaveSettingsAsync(It.IsAny<SaveSettingsRequest>())).ReturnsAsync((false, "The Microsoft sign-in settings could not be saved: this installation's App_Data folder is not writable."));
        var cut = Render<GitSourcesSettingsCard>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-client]").GetAttribute("value").Should().Be("11111111-2222-3333-4444-555555555555"));
        cut.Find("[data-testid=gss-step][data-step=application] details").HasAttribute("open").Should().BeFalse("a finished step stays closed until revisited");
        cut.Find("[data-testid=gss-signin-client]").Input("22222222-2222-3333-4444-555555555555");
        cut.Find("[data-testid=gss-signin-revert]").Click();
        cut.Find("[data-testid=gss-signin-client]").GetAttribute("value").Should().Be("11111111-2222-3333-4444-555555555555");
        cut.Find("[data-testid=gss-signin-device]").Change(false);
        cut.FindAll("[data-testid=gss-signin-save]")[0].Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-error]").TextContent.Should().Contain("App_Data folder is not writable"));
    }

    [Fact]
    public void State14_saved_values_not_yet_in_use_say_restart_required()
    {
        Api(new() { State = GitConnectionState.NotConfigured, Diagnostics = [new("enabled", "Microsoft sign-in enabled", GitCheckState.Missing, "AzureEnvironment:Enabled is false."), Ok("client-id"), Ok("tenant")] });
        Admin(new() { Enabled = true, ClientId = "11111111-2222-3333-4444-555555555555", AllowDeviceCode = true, RestartRequired = true });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=gss-signin-restart]").Should().NotBeEmpty());
        cut.Find("[data-testid=gss-signin-restart]").TextContent.Should().Contain("Restart BirkNext to apply");
        Step(cut, "application").Should().Contain("Saved — restart required");
        cut.Find("[data-testid=gss-technical]").TextContent.Should().Contain("restart BirkNext");
    }

    [Fact]
    public void Without_System_Settings_access_the_form_says_why_it_cannot_save()
    {
        Api(NotSetUp());
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=gss-signin-unavailable]").Should().NotBeEmpty());
        cut.Find("[data-testid=gss-signin-enabled]").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("[data-testid=gss-signin-save]").Should().OnlyContain(b => b.HasAttribute("disabled"));
    }

    [Fact]
    public void Any_step_can_be_revisited_and_stays_as_the_person_left_it()
    {
        Api(new() { State = GitConnectionState.NotConnected, SignInAvailable = true, OrganizationUrl = Org, Diagnostics = Configured });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-steps]"));
        string Open(string id) => cut.Find($"[data-testid=gss-step][data-step={id}] > details").HasAttribute("open") ? "open" : "closed";
        (Open("provider"), Open("authentication")).Should().Be(("closed", "open"), "a finished step is closed, the current one open");
        cut.Find("[data-testid=gss-step][data-step=provider] > details > summary").Click();
        Open("provider").Should().Be("open");
        cut.Render();
        Open("provider").Should().Be("open", "a re-render never closes a step the person opened");
        cut.Find("[data-testid=gss-step][data-step=provider] > details > summary").Click();
        Open("provider").Should().Be("closed");
    }
}
