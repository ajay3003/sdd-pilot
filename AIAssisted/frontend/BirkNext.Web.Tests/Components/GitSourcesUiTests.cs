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

    [Fact]
    public void Not_configured_names_the_requirements_and_disables_the_test()
    {
        Api(new() { State = GitConnectionState.NotConfigured, Message = "Microsoft sign-in is not configured.", Requirements = ["Set AzureEnvironment:ClientId"] });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-requirements]").TextContent.Should().Contain("AzureEnvironment:ClientId"));
        cut.Find("[data-testid=gss-test]").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("[data-testid=gss-sign-in]").Should().BeEmpty();
    }

    [Fact]
    public void Reconnect_is_offered_when_the_sign_in_expired()
    {
        var api = Api(new() { State = GitConnectionState.ReconnectRequired, Message = "Reconnect.", SignInAvailable = true });
        api.Setup(a => a.SignInAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new GitConnectionStatus { State = GitConnectionState.SigningIn, Message = "Complete sign-in." }, (string?)null));
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-sign-in]").TextContent.Should().Be("Reconnect"));
        cut.Find("[data-testid=gss-sign-in]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-state]").GetAttribute("data-state").Should().Be("SigningIn"));
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
        cut.Find("[data-testid=gss-test-reason]").TextContent.Should().Contain("Microsoft sign-in is set up");
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
        Api(new() { State = GitConnectionState.NotConnected, SignInAvailable = true });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-test-reason]").TextContent.Should().Contain("organization URL"));

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

    // ── Microsoft sign-in setup: the prerequisites are editable here, saved like the other System Settings, applied after a restart ──────

    private Mock<AdminApiService> Admin(MicrosoftSignInInfoDto signIn)
    {
        var admin = new Mock<AdminApiService>(new HttpClient()) { CallBase = false };
        admin.Setup(a => a.GetSystemSettingsAsync()).ReturnsAsync(new SystemSettingsDto { MicrosoftSignIn = signIn });
        Services.AddSingleton(admin.Object);
        return admin;
    }

    [Fact]
    public void Not_set_up_opens_the_sign_in_setup_and_saves_the_entered_values()
    {
        Api(NotSetUp());
        var admin = Admin(new() { Enabled = false, ClientId = "", TenantId = "", AllowDeviceCode = true });
        SaveSettingsRequest? sent = null;
        admin.Setup(a => a.SaveSettingsAsync(It.IsAny<SaveSettingsRequest>())).ReturnsAsync((true, "Saved."))
            .Callback<SaveSettingsRequest>(r =>
            {
                sent = r;
                admin.Setup(a => a.GetSystemSettingsAsync()).ReturnsAsync(new SystemSettingsDto { MicrosoftSignIn = new()
                    { Enabled = true, ClientId = "11111111-2222-3333-4444-555555555555", TenantId = "bufdir.no", AllowDeviceCode = true, RestartRequired = true } });
            });
        var cut = Render<GitSourcesSettingsCard>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-setup]").HasAttribute("open").Should().BeTrue("nothing works until sign-in is set up"));
        cut.Find("[data-testid=gss-check][data-key=enabled]").TextContent.Should().Contain("Set up here:");
        cut.FindAll("[data-testid=gss-signin-restart]").Should().BeEmpty();
        cut.Find("[data-testid=gss-signin-enabled]").Change(true);
        cut.Find("[data-testid=gss-signin-client]").Input(" 11111111-2222-3333-4444-555555555555 ");
        cut.Find("[data-testid=gss-signin-tenant]").Input("bufdir.no");
        cut.Find("[data-testid=gss-signin-save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-restart]").TextContent.Should().Contain("Restart BirkNext to apply"));
        sent!.MicrosoftSignIn.Should().BeEquivalentTo(new SaveMicrosoftSignInRequest { Enabled = true, ClientId = "11111111-2222-3333-4444-555555555555", TenantId = "bufdir.no", AllowDeviceCode = true });
        sent.FeatureVisibility.Should().BeNull("only the sign-in section is written");
        cut.FindAll("[data-testid=gss-signin-error]").Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, "", "", "Enter the Application (client) ID")]
    [InlineData(false, "not-a-guid", "", "is a GUID")]
    [InlineData(true, "11111111-2222-3333-4444-555555555555", "not a tenant!", "Directory (tenant) ID")]
    public void Invalid_sign_in_setup_is_refused_before_anything_is_saved(bool enabled, string clientId, string tenant, string problem)
    {
        Api(NotSetUp());
        var admin = Admin(new() { AllowDeviceCode = true });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-setup]"));
        cut.Find("[data-testid=gss-signin-enabled]").Change(enabled);
        cut.Find("[data-testid=gss-signin-client]").Input(clientId);
        cut.Find("[data-testid=gss-signin-tenant]").Input(tenant);
        cut.Find("[data-testid=gss-signin-save]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-error]").TextContent.Should().Contain(problem));
        admin.Verify(a => a.SaveSettingsAsync(It.IsAny<SaveSettingsRequest>()), Times.Never());
    }

    [Fact]
    public void A_refused_save_shows_the_backend_reason_and_a_configured_installation_keeps_the_setup_closed()
    {
        Api(new() { State = GitConnectionState.NotConnected, SignInAvailable = true, OrganizationUrl = "https://dev.azure.com/bufdir" });
        var admin = Admin(new() { Enabled = true, ClientId = "11111111-2222-3333-4444-555555555555", AllowDeviceCode = true });
        admin.Setup(a => a.SaveSettingsAsync(It.IsAny<SaveSettingsRequest>())).ReturnsAsync((false, "Settings editing is turned off on this installation."));
        var cut = Render<GitSourcesSettingsCard>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-setup]").HasAttribute("open").Should().BeFalse("a working setup stays out of the way"));
        cut.Find("[data-testid=gss-signin-client]").GetAttribute("value").Should().Be("11111111-2222-3333-4444-555555555555");
        cut.Find("[data-testid=gss-signin-save]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-error]").TextContent.Should().Be("Settings editing is turned off on this installation."));
        cut.FindAll("[data-testid=gss-signin-restart]").Should().BeEmpty();
    }

    [Fact]
    public void Without_System_Settings_access_the_card_still_renders_without_the_setup()
    {
        Api(NotSetUp());
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-health-summary]"));
        cut.FindAll("[data-testid=gss-signin-setup]").Should().BeEmpty();
    }
}
