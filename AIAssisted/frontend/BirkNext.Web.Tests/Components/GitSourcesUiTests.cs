using BirkNext.GitSources;
using BirkNext.Web.Components;
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
        cut.Find("[data-testid=gss-last-test]").TextContent.Should().Be("Not tested", "configured / signed in is not verified");
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

    [Fact]
    public void Import_picker_loads_only_when_asked_and_hands_over_the_chosen_branch()
    {
        var api = Api(new() { State = GitConnectionState.Connected, Account = "tester@bufdir.no", Message = "Signed in." });
        api.Setup(a => a.RepositoriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(([new GitRepository("r1", "M2LB.Common", "M2LB", "main", null, false)], (string?)null));
        api.Setup(a => a.BranchesAsync("r1", It.IsAny<CancellationToken>())).ReturnsAsync((
            [new GitBranch("feature/rapport", Sha, new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero), "B", "recent", false), new GitBranch("main", new string('a', 40), null, null, null, true)], (string?)null));
        (GitImportRequest Request, string Label)? chosen = null;
        var cut = Render<GitImportPicker>(p => p.Add(x => x.OnImport, ((GitImportRequest Request, string Label) c) => chosen = c));

        api.Verify(a => a.StatusAsync(It.IsAny<CancellationToken>()), Times.Never(), "nothing is read until the section is opened");
        cut.Find("details").TriggerEvent("ontoggle", EventArgs.Empty);
        cut.WaitForElement("[data-testid=gip-load-repos]");
        api.Verify(a => a.RepositoriesAsync(It.IsAny<CancellationToken>()), Times.Never());
        cut.Find("[data-testid=gip-load-repos]").Click();
        cut.WaitForElement("[data-testid=gip-repo]").Change("r1");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=gip-branch]").Select(b => b.GetAttribute("data-branch")).Should().Equal(["feature/rapport", "main"]));
        cut.Find("[data-testid=gip-import]").HasAttribute("disabled").Should().BeTrue("a branch must be chosen");
        cut.Find("[data-testid=gip-branch][data-branch='feature/rapport']").Change(true);
        cut.Find("[data-testid=gip-import]").Click();
        chosen.Should().Be((new GitImportRequest("r1", "feature/rapport"), "M2LB.Common @ feature/rapport"));
    }

    [Fact]
    public void Import_picker_sends_people_to_settings_when_not_connected()
    {
        var api = Api(new() { State = GitConnectionState.NotConnected, Message = "Not connected." });
        var cut = Render<GitImportPicker>(p => p.Add(x => x.OnImport, ((GitImportRequest Request, string Label) _) => { }));
        cut.Find("details").TriggerEvent("ontoggle", EventArgs.Empty);
        cut.WaitForAssertion(() => cut.Find("[data-testid=gip-not-connected]").InnerHtml.Should().Contain("admin/system-settings?section=git-sources"));
        api.Verify(a => a.RepositoriesAsync(It.IsAny<CancellationToken>()), Times.Never());
    }
}
