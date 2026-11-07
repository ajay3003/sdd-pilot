using BirkNext.GitSources;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// System Settings → Git Sources, redesigned: a compact stepper with one focused panel; statuses from the backend state; one action row per step;
/// administrator instructions on demand; no configuration keys in the normal workflow; every disabled action says why. Microsoft and Azure DevOps
/// are doubles here — the component calls the same client services as in the product.
/// </summary>
public sealed class GitSourcesSetupUiTests : BunitContext
{
    private const string Client = "11111111-2222-3333-4444-555555555555";
    private const string Org = "https://dev.azure.com/synthetic-org";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly GitTestResult Passed = new(true, "Ready", [new("Microsoft Entra authentication", GitTestStepState.Verified, "Verified", 3)],
        new(2026, 11, 7, 8, 0, 0, TimeSpan.Zero), "synthetic-org", 2);

    private static GitConfigurationCheck Check(string key, GitCheckState state = GitCheckState.Ok) => new(key, key, state, $"{key} detail");
    private static readonly IReadOnlyList<GitConfigurationCheck> Configured = [Check("enabled"), Check("client-id"), Check("tenant")];

    private static GitConnectionStatus Unconfigured() => new()
    {
        State = GitConnectionState.NotConfigured, OrganizationUrl = Org, Message = "Microsoft sign-in is not configured.",
        Diagnostics = [new("enabled", "Microsoft sign-in enabled", GitCheckState.Missing, "AzureEnvironment:Enabled is false."),
            new("client-id", "App registration (client id)", GitCheckState.Missing, "No Entra app registration is configured (AzureEnvironment:ClientId)."), Check("tenant")],
    };

    private Mock<IGitSourcesApiService> Api(GitConnectionStatus status)
    {
        var api = new Mock<IGitSourcesApiService>();
        api.Setup(a => a.StatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((status, (string?)null));
        api.Setup(a => a.LinksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitSourceLink>)[], (string?)null));
        Services.AddSingleton(api.Object);
        return api;
    }

    private Mock<AdminApiService> Admin(MicrosoftSignInInfoDto signIn)
    {
        var admin = new Mock<AdminApiService>(new HttpClient()) { CallBase = false };
        admin.Setup(a => a.GetSystemSettingsAsync()).ReturnsAsync(new SystemSettingsDto { MicrosoftSignIn = signIn });
        Services.AddSingleton(admin.Object);
        return admin;
    }

    private static string Stepper(IRenderedComponent<GitSourcesSettingsCard> cut) =>
        string.Join(" ", cut.FindAll("[data-testid=gss-stepper-item]").Select(s => $"{s.GetAttribute("data-step")}:{s.GetAttribute("data-status")}"));

    private static string Panel(IRenderedComponent<GitSourcesSettingsCard> cut) => cut.Find("[data-testid=gss-panel]").GetAttribute("data-step")!;
    private static void Go(IRenderedComponent<GitSourcesSettingsCard> cut, string step) => cut.Find($"[data-testid=gss-stepper-item][data-step={step}]").Click();

    [Fact]
    public void Unconfigured_shows_a_compact_stepper_one_focused_panel_and_no_configuration_keys()
    {
        Api(Unconfigured());
        Admin(new() { AllowDeviceCode = true });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-stepper]"));

        Stepper(cut).Should().Be("provider:Current application:Needs setup organization:Complete authentication:Needs setup test:Not tested import:Needs setup");
        cut.FindAll("[data-testid=gss-panel]").Should().ContainSingle("only one step is shown at a time");
        Panel(cut).Should().Be("provider");
        cut.Find("[data-testid=gss-stepper-item][data-step=provider]").GetAttribute("aria-current").Should().Be("step");
        cut.Find("[data-testid=gss-stepper-item][data-step=provider]").GetAttribute("aria-label").Should().Be("Step 1 of 6: Setup, Current");
        cut.Find("[data-testid=gss-overall]").TextContent.Should().Be("Setup required");
        cut.Find("[data-testid=gss-provider]").TextContent.Should().Be("Azure DevOps");
        cut.Find("[data-testid=gss-auth-method]").TextContent.Should().Contain("no PAT or client secret");
        cut.Find("[data-testid=gss-summary-application]").TextContent.Should().Be("Not configured");
        cut.Find("[data-testid=gss-summary-organization]").TextContent.Should().Be("synthetic-org · saved");
        cut.Find("[data-testid=gss-summary-connection]").TextContent.Should().Be("Not configured");
        cut.Markup.Should().NotContain("AzureEnvironment:", "configuration keys are not part of the normal workflow");
        cut.FindAll("[data-testid=gss-signin-save]").Should().ContainSingle("one save for the step, never two sets");
    }

    [Fact]
    public void Any_step_can_be_opened_and_back_returns_to_the_previous_step()
    {
        Api(Unconfigured());
        Admin(new() { AllowDeviceCode = true });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-stepper]"));
        Go(cut, "import");
        Panel(cut).Should().Be("import");
        cut.Find("[data-testid=gss-import-blocked]").TextContent.Should().Contain("Verify your Azure DevOps connection");
        cut.Find("[data-testid=gss-goto-test]").Click();
        Panel(cut).Should().Be("test");
        Go(cut, "organization");
        cut.Find("[data-testid=gss-back]").Click();
        Panel(cut).Should().Be("application");
    }

    [Fact]
    public void Enabling_without_a_client_id_continues_to_Entra_without_saving_and_saving_there_persists_and_advances()
    {
        var api = Api(Unconfigured());
        var admin = Admin(new() { Enabled = false, ClientId = "", TenantId = "", AllowDeviceCode = true });
        SaveSettingsRequest? sent = null;
        admin.Setup(a => a.SaveSettingsAsync(It.IsAny<SaveSettingsRequest>())).ReturnsAsync((true, "Microsoft sign-in settings saved and applied. No restart is needed."))
            .Callback<SaveSettingsRequest>(r => sent = r);
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-enabled]"));

        cut.Find("[data-testid=gss-signin-enabled]").Change(true);
        cut.Find("[data-testid=gss-signin-save]").Click();
        cut.WaitForAssertion(() => Panel(cut).Should().Be("application"));
        cut.Find("[data-testid=gss-notice]").TextContent.Should().Contain("finish enabling");
        admin.Verify(a => a.SaveSettingsAsync(It.IsAny<SaveSettingsRequest>()), Times.Never(), "enabling needs the client id; nothing is saved yet");

        cut.Find("[data-testid=gss-signin-client]").Input("not-a-guid");
        cut.Find("[data-testid=gss-client-hint]").TextContent.Should().Contain("Application (Client) ID");
        cut.Find("[data-testid=gss-signin-save]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=gss-signin-save-reason]").TextContent.Should().NotBeEmpty();
        cut.Find("[data-testid=gss-signin-client]").Input($" {Client} ");
        cut.Find("[data-testid=gss-tenant-mode]").Change("specific");
        cut.Find("[data-testid=gss-signin-tenant]").Input("common");
        cut.Find("[data-testid=gss-tenant-hint]").TextContent.Should().Contain("Directory (tenant) ID");
        cut.Find("[data-testid=gss-signin-tenant]").Input("bufdir.no");
        cut.Find("[data-testid=gss-signin-save]").Click();

        cut.WaitForAssertion(() => Panel(cut).Should().Be("organization"));
        sent!.MicrosoftSignIn.Should().BeEquivalentTo(new SaveMicrosoftSignInRequest { Enabled = true, ClientId = Client, TenantId = "bufdir.no", AllowDeviceCode = true });
        api.Verify(a => a.StatusAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2), "the state is read again after the save");
        api.Verify(a => a.SignInAsync(It.IsAny<CancellationToken>()), Times.Never(), "saving never signs in");
    }

    [Fact]
    public void Administrator_help_is_compact_until_asked_and_both_copies_work()
    {
        Api(Unconfigured());
        Admin(new() { AllowDeviceCode = true });
        JSInterop.SetupVoid("navigator.clipboard.writeText", _ => true).SetVoidResult();
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-stepper]"));
        Go(cut, "application");

        cut.FindAll("[data-testid=gss-registration-text]").Should().BeEmpty("instructions are on demand");
        cut.Find("[data-testid=gss-instructions-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=gss-instructions-toggle]").Click();
        var steps = cut.FindAll("[data-testid=gss-registration-text] > li").Select(li => li.QuerySelector("strong")!.TextContent).ToList();
        steps.Should().Equal("App registration", "Authentication platform", "Redirect URI", "API permissions", "Consent", "Information to send back");
        cut.Find("[data-testid=gss-registration-text]").TextContent.Should().Contain("499b84ac-1321-427f-aa17-267ca6975798").And.Contain("user_impersonation");
        cut.Find("[data-testid=gss-instructions-toggle]").Click();
        cut.FindAll("[data-testid=gss-registration-text]").Should().BeEmpty("the instructions close predictably");

        cut.Find("[data-testid=gss-registration-copy]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-registration-copy]").TextContent.Trim().Should().Be("Copied"));
        cut.Find("[data-testid=gss-redirect]").GetAttribute("value").Should().Be("http://localhost");
        cut.Find("[data-testid=gss-redirect-copy]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-redirect-copy]").TextContent.Trim().Should().Be("Copied"));
        JSInterop.Invocations.Where(i => i.Identifier == "navigator.clipboard.writeText").Select(i => i.Arguments.Single()).Should()
            .Equal(GitSourcesPresentation.RegistrationRequest, "http://localhost");
    }

    [Fact]
    public void Organization_is_validated_saved_normalized_and_never_called_access()
    {
        var api = Api(new() { State = GitConnectionState.NotConnected, SignInAvailable = true, Diagnostics = Configured });
        api.Setup(a => a.SaveSettingsAsync(It.IsAny<GitSourceSettings>(), It.IsAny<CancellationToken>())).ReturnsAsync((new GitConnectionStatus { State = GitConnectionState.NotConnected }, (string?)null));
        Admin(new() { Enabled = true, ClientId = Client, AllowDeviceCode = true });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => Panel(cut).Should().Be("organization"));

        cut.Find("[data-testid=gss-save]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=gss-save-reason]").TextContent.Should().Be("Enter the organization URL.");
        cut.Find("[data-testid=gss-org]").Input("https://user@dev.azure.com/x");
        cut.Find("[data-testid=gss-org-hint]").TextContent.Should().Contain("credentials");
        cut.Find("[data-testid=gss-org]").Input(" https://dev.azure.com/Synthetic-Org/ ");
        cut.Find("[data-testid=gss-save]").Click();

        api.Verify(a => a.SaveSettingsAsync(It.Is<GitSourceSettings>(s => s.OrganizationUrl == "https://dev.azure.com/Synthetic-Org"), It.IsAny<CancellationToken>()), Times.Once());
        cut.WaitForAssertion(() => Panel(cut).Should().Be("authentication"));
        cut.Find("[data-testid=gss-notice]").TextContent.Should().Be("Organization saved. Access has not been verified.");
    }

    [Fact]
    public void Sign_in_is_blocked_with_a_way_to_fix_it_then_ready_then_signed_in()
    {
        Api(Unconfigured());
        Admin(new() { AllowDeviceCode = true });
        var blocked = Render<GitSourcesSettingsCard>();
        blocked.WaitForAssertion(() => blocked.Find("[data-testid=gss-stepper]"));
        Go(blocked, "authentication");
        blocked.Find("[data-testid=gss-signin-blocked]").TextContent.Should().Contain("Microsoft application setup is incomplete");
        blocked.FindAll("[data-testid=gss-sign-in]").Should().BeEmpty("no unusable Sign in button");
        blocked.Find("[data-testid=gss-goto-setup]").Click();
        Panel(blocked).Should().Be("provider");
    }

    [Fact]
    public void Ready_to_sign_in_starts_the_sign_in_and_a_signed_in_user_continues_to_the_test()
    {
        var api = Api(new() { State = GitConnectionState.NotConnected, SignInAvailable = true, DeviceCodeAllowed = true, OrganizationUrl = Org, Diagnostics = Configured });
        api.Setup(a => a.SignInAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new GitConnectionStatus { State = GitConnectionState.SigningIn, Message = "Complete sign-in.", Diagnostics = Configured, OrganizationUrl = Org }, (string?)null));
        Admin(new() { Enabled = true, ClientId = Client, AllowDeviceCode = true });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => Panel(cut).Should().Be("authentication"));
        cut.Find("[data-testid=gss-signin-state]").TextContent.Should().Be("Ready to sign in");
        cut.Find("[data-testid=gss-consent]").TextContent.Should().Be("Checked by Microsoft when you sign in");
        cut.Find("[data-testid=gss-sign-in]").TextContent.Trim().Should().Be("Sign in with Microsoft");
        cut.Find("[data-testid=gss-sign-in]").Click();
        api.Verify(a => a.SignInAsync(It.IsAny<CancellationToken>()), Times.Once());
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-progress]"));
        cut.Find("[data-testid=gss-refresh-status]").TextContent.Should().Contain("check again");

        var signedIn = new GitConnectionStatus { State = GitConnectionState.Connected, Account = "tester@synthetic.example", OrganizationUrl = Org, Diagnostics = Configured };
        api.Setup(a => a.StatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((signedIn, (string?)null));
        cut.Find("[data-testid=gss-refresh-status]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-account]").TextContent.Should().Be("tester@synthetic.example"));
        cut.Find("[data-testid=gss-consent]").TextContent.Should().Be("Granted (observed at sign-in)");
        cut.Find("[data-testid=gss-continue-test]").Click();
        Panel(cut).Should().Be("test");
    }

    [Fact]
    public void A_failed_sign_in_explains_itself_offers_retry_and_keeps_details_disclosed()
    {
        Api(new() { State = GitConnectionState.Failed, SignInAvailable = true, OrganizationUrl = Org, Message = "Consent is required for the Azure DevOps permission.",
            Diagnostics = [.. Configured, new("consent", "Delegated Azure DevOps permission", GitCheckState.Failed, "AADSTS65001", "An administrator grants consent.")] });
        Admin(new() { Enabled = true, ClientId = Client, AllowDeviceCode = true });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => Panel(cut).Should().Be("authentication"));
        cut.Find("[data-testid=gss-signin-failure]").TextContent.Should().Contain("Consent is required");
        cut.Find("[data-testid=gss-signin-state]").TextContent.Should().Be("Consent required");
        cut.Find("[data-testid=gss-sign-in]").TextContent.Trim().Should().Be("Retry sign-in");
        cut.Find("[data-testid=gss-diagnostics]").HasAttribute("open").Should().BeFalse("technical details are disclosed on demand");
        cut.Find("[data-testid=gss-check][data-key=consent]").TextContent.Should().Contain("AADSTS65001");
    }

    [Fact]
    public void The_connection_test_runs_the_real_client_path_and_success_leads_to_browsing()
    {
        var connected = new GitConnectionStatus { State = GitConnectionState.Connected, Account = "tester@synthetic.example", OrganizationUrl = Org, Diagnostics = Configured };
        var api = Api(connected);
        api.Setup(a => a.TestAsync(It.IsAny<CancellationToken>())).ReturnsAsync((Passed, (string?)null))
            .Callback(() => api.Setup(a => a.StatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync((connected with { LastTest = Passed }, (string?)null)));
        Admin(new() { Enabled = true, ClientId = Client, AllowDeviceCode = true });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => Panel(cut).Should().Be("test"));

        cut.FindAll("[data-testid=gss-prerequisite]").Should().OnlyContain(p => p.GetAttribute("data-ok") == "true");
        cut.Find("[data-testid=gss-connection-state]").TextContent.Should().Be("Not tested");
        cut.FindAll("[data-testid=gss-browse]").Should().BeEmpty();
        cut.Find("[data-testid=gss-test]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-connection-state]").TextContent.Should().Be("Connected"));
        cut.Find("[data-testid=gss-last-success]").TextContent.Should().Contain("synthetic-org").And.Contain("verified");
        api.Verify(a => a.TestAsync(It.IsAny<CancellationToken>()), Times.Once());
        cut.Find("[data-testid=gss-browse]").Click();
        Panel(cut).Should().Be("import");
    }

    [Theory]
    [InlineData(GitConnectionState.AccessDenied, false, "Permission denied")]
    [InlineData(GitConnectionState.Connected, true, "Previous result stale")]
    [InlineData(GitConnectionState.ReconnectRequired, false, "Sign-in required")]
    public void A_connection_is_never_shown_as_verified_when_denied_stale_or_expired(GitConnectionState state, bool stale, string expected)
    {
        Api(new() { State = state, Account = "tester@synthetic.example", OrganizationUrl = Org, Diagnostics = Configured, LastTest = Passed, LastTestStale = stale, SignInAvailable = true });
        Admin(new() { Enabled = true, ClientId = Client, AllowDeviceCode = true });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-stepper]"));
        Go(cut, "test");
        cut.Find("[data-testid=gss-connection-state]").TextContent.Should().Be(expected);
        cut.FindAll("[data-testid=gss-last-success]").Should().BeEmpty();
        cut.FindAll("[data-testid=gss-browse]").Should().BeEmpty();
        cut.Find("[data-testid=gss-overall]").TextContent.Should().NotBe("Connected");
    }

    [Fact]
    public void Discovery_selects_the_actual_default_branch_tracks_it_and_hands_the_import_to_Import_Project()
    {
        var verified = new GitConnectionStatus { State = GitConnectionState.Connected, Account = "tester@synthetic.example", OrganizationUrl = Org, Diagnostics = Configured, LastTest = Passed,
            TrackedBranches = [new("r9", "Synthetic.Old", "Synthetic", "main")] };
        var api = Api(verified);
        api.Setup(a => a.ProjectsAsync(Org, null, It.IsAny<CancellationToken>())).ReturnsAsync((new GitProjectPage([new GitProject("p1", "Synthetic", "wellFormed"), new GitProject("p2", "Other", null)], null), (string?)null));
        api.Setup(a => a.ProjectRepositoriesAsync(Org, "p1", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitRepository>)[
            new GitRepository("r1", "Synthetic.Service", "Synthetic", "develop", null, false), new GitRepository("r2", "Synthetic.Archive", "Synthetic", "main", null, true)], (string?)null));
        api.Setup(a => a.BranchesInAsync(Org, "r1", It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<GitBranch>)[
            new GitBranch("main", Sha, null, null, null, false), new GitBranch("develop", new string('b', 40), null, null, null, true)], (string?)null));
        api.Setup(a => a.SaveSettingsAsync(It.IsAny<GitSourceSettings>(), It.IsAny<CancellationToken>())).ReturnsAsync((verified, (string?)null));
        Admin(new() { Enabled = true, ClientId = Client, AllowDeviceCode = true });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => Panel(cut).Should().Be("import"));

        cut.Find("[data-testid=gss-tracked-branch]").TextContent.Should().Contain("Synthetic.Old").And.Contain("Not imported");
        cut.Find("[data-testid=gss-load-projects]").Click();
        cut.WaitForElement("[data-testid=gss-project-filter]").Input("synth");
        cut.FindAll("[data-testid=gss-project] option").Select(o => o.TextContent).Should().Equal("Choose a project", "Synthetic");
        cut.Find("[data-testid=gss-project]").Change("p1");
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-repo] option[value=r2]").HasAttribute("disabled").Should().BeTrue());
        cut.Find("[data-testid=gss-import]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=gss-import-reason]").TextContent.Should().Contain("Choose a project, repository and branch");
        cut.Find("[data-testid=gss-repo]").Change("r1");
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-branch]").GetAttribute("value").Should().Be("develop"));
        cut.Find("[data-testid=gss-track]").Click();
        api.Verify(a => a.SaveSettingsAsync(It.Is<GitSourceSettings>(s => s.TrackedBranches.Any(t => t.RepositoryId == "r1" && t.Branch == "develop")), It.IsAny<CancellationToken>()), Times.Once());
        cut.Find("[data-testid=gss-import]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-error]").TextContent.Should().Contain("Import Project is not available"));
        api.Verify(a => a.ImportAsync(It.IsAny<GitImportRequest>(), It.IsAny<CancellationToken>()), Times.Never(), "the import runs only through Import Project's review");
    }

    [Fact]
    public void Cancel_changes_restores_the_saved_values_and_restart_required_is_explicit()
    {
        Api(new() { State = GitConnectionState.NotConfigured, Diagnostics = [new("enabled", "e", GitCheckState.Missing, "off"), Check("client-id"), Check("tenant")] });
        Admin(new() { Enabled = true, ClientId = Client, AllowDeviceCode = true, RestartRequired = true });
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-revert]"));
        cut.Find("[data-testid=gss-signin-revert]").HasAttribute("disabled").Should().BeTrue("nothing changed yet");
        cut.Find("[data-testid=gss-signin-enabled]").Change(false);
        cut.Find("[data-testid=gss-signin-revert]").Click();
        cut.Find("[data-testid=gss-signin-enabled]").HasAttribute("checked").Should().BeTrue();
        Go(cut, "application");
        cut.Find("[data-testid=gss-signin-restart]").TextContent.Should().Contain("Restart required");
        cut.Find("[data-testid=gss-step-status]").TextContent.Should().Be("Saved — restart required");
    }

    [Fact]
    public void Without_System_Settings_access_saving_is_disabled_and_says_why()
    {
        Api(Unconfigured());
        var cut = Render<GitSourcesSettingsCard>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=gss-signin-save]"));
        cut.Find("[data-testid=gss-signin-enabled]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=gss-signin-save]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=gss-signin-save-reason]").TextContent.Should().Contain("System Settings");
    }
}
