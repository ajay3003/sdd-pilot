using BirkNext.BrowserAuthentication;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>System Settings → Authentication with Microsoft Entra ID: application URL, test identity combo box (one known entry), password
/// write-only (Securely configured / Replace password), Edit keeps the identity, separate step results, Connected / Failed / Requires interaction.</summary>
public sealed class BrowserAuthenticationEntraTests : BunitContext
{
    private const string TestUser = "testbruker01@bufdir.no";
    private const string Password = "fake-entra-ui-pw";
    private readonly FakeApi _api = new();

    public BrowserAuthenticationEntraTests()
    {
        Services.AddSingleton<IBrowserAuthenticationApiService>(_api);
        var targets = new Mock<IFrontendAnalysisSettingsService>();
        targets.SetupGet(t => t.ActiveProfile).Returns((FrontendAnalysisProfile?)null);
        Services.AddSingleton(targets.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static readonly string[] StepKeys = ["application", "entra-redirect", "login-form", "credential-submission", "authentication", "identity", "protected-resource"];

    private static List<UnattendedLoginStep> Steps(int passed, UnattendedLoginStepStatus failure) =>
        [.. StepKeys.Select((k, i) => new UnattendedLoginStep(k, k, i < passed ? UnattendedLoginStepStatus.Passed : i == passed ? failure : UnattendedLoginStepStatus.NotReached, i < passed ? "ok" : "stopped"))];

    private sealed class FakeApi : IBrowserAuthenticationApiService
    {
        public UnattendedLoginView View { get; set; } = new() { Configured = false, State = UnattendedLoginState.NotConfigured, StateLabel = "Not configured" };
        public List<SaveUnattendedLoginRequest> Saved { get; } = [];
        public UnattendedLoginOutcome NextOutcome { get; set; } = UnattendedLoginOutcome.Succeeded;

        public Task<(UnattendedLoginView? View, string? Error)> GetAsync(CancellationToken ct = default) => Task.FromResult<(UnattendedLoginView?, string?)>((View, null));
        public Task<AutomatedBrowserExecutionReadiness?> AutomatedExecutionAsync(CancellationToken ct = default) =>
            Task.FromResult<AutomatedBrowserExecutionReadiness?>(new(View.State == UnattendedLoginState.Ready, "x", "x", View.State, []));
        public Task<(UnattendedLoginView? View, string? Error)> SaveAsync(SaveUnattendedLoginRequest request, CancellationToken ct = default)
        {
            Saved.Add(request);
            var credential = View.CredentialConfigured || !string.IsNullOrEmpty(request.Password);
            View = View with
            {
                Configured = true, Enabled = request.Enabled, Provider = request.Provider, TestIdentity = request.LoginName ?? View.TestIdentity, IdentityAlias = request.LoginName ?? View.IdentityAlias,
                CredentialConfigured = credential, LoginUrl = request.LoginUrl, ProtectedResourceUrl = request.ProtectedResourceUrl, State = UnattendedLoginState.NotVerified, StateLabel = "Not verified",
                ConnectionStatus = UnattendedConnectionStatus.Configured, ConnectionStatusLabel = "Configured",
            };
            return Task.FromResult<(UnattendedLoginView?, string?)>((View, null));
        }
        public Task<(UnattendedLoginView? View, string? Error)> SetEnabledAsync(bool enabled, CancellationToken ct = default) => Task.FromResult<(UnattendedLoginView?, string?)>((View, null));
        public Task<(UnattendedLoginValidationResult? Result, string? Error)> ValidateAsync(CancellationToken ct = default)
        {
            var (status, steps) = NextOutcome switch
            {
                UnattendedLoginOutcome.Succeeded => (UnattendedConnectionStatus.Connected, Steps(7, UnattendedLoginStepStatus.Passed)),
                UnattendedLoginOutcome.InteractiveChallengeDetected => (UnattendedConnectionStatus.RequiresInteraction, Steps(4, UnattendedLoginStepStatus.RequiresInteraction)),
                _ => (UnattendedConnectionStatus.Failed, Steps(5, UnattendedLoginStepStatus.Failed)),
            };
            View = View with
            {
                State = status == UnattendedConnectionStatus.Connected ? UnattendedLoginState.Ready : UnattendedLoginState.ValidationFailed,
                ConnectionStatus = status, ConnectionStatusLabel = BrowserAuthenticationText.Connection(status),
                LastValidation = new(NextOutcome, "m", DateTimeOffset.UtcNow, true, 100) { Steps = steps },
            };
            return Task.FromResult<(UnattendedLoginValidationResult?, string?)>((new(NextOutcome, "m", BrowserAuthenticationText.Recommendation(NextOutcome), 100, View) { Steps = steps }, null));
        }
        public Task<string?> ClearAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private IRenderedComponent<BrowserAuthenticationSettings> ConfigureEntra()
    {
        var page = Render<BrowserAuthenticationSettings>();
        page.Find("[data-testid=ba-configure]").Click();
        page.Find("[data-testid=ba-form-password]").Change(Password);
        page.Find("[data-testid=ba-editor]").Submit();
        return page;
    }

    [Fact]
    public void NewConfiguration_DefaultsToEntra_WithTheKnownTestIdentityAndApplicationUrl()
    {
        var page = Render<BrowserAuthenticationSettings>();
        page.Find("[data-testid=ba-configure]").Click();

        page.Find("[data-testid=ba-form-provider]").GetAttribute("value").Should().Be(nameof(UnattendedLoginProvider.MicrosoftEntraId));
        page.Find("[data-testid=ba-form-url]").GetAttribute("value").Should().Be("https://m2lbdev.bufetat.no");
        var login = page.Find("[data-testid=ba-form-login]");
        login.GetAttribute("role").Should().Be("combobox");
        login.GetAttribute("value").Should().Be(TestUser);
        page.FindAll("#ba-test-identities option").Select(o => o.GetAttribute("value")).Should().Equal(TestUser);
        page.Find("[data-testid=ba-form-password]").GetAttribute("type").Should().Be("password");
        page.Find("[data-testid=ba-editor]").TextContent.Should().Contain("Authenticated application session").And.Contain("A URL alone never counts");
        page.FindAll("[data-testid=ba-form-success-url]").Should().BeEmpty("Entra ID success is verified, not URL matched");
    }

    [Fact]
    public void Save_ShowsConfigured_Edit_KeepsTheIdentity_ShowsSecurelyConfigured_AndCanReplaceThePassword()
    {
        var page = ConfigureEntra();
        page.WaitForAssertion(() => page.Find("[data-testid=ba-unattended-status]").TextContent.Should().Be("Configured"));
        _api.Saved.Single().Should().Match<SaveUnattendedLoginRequest>(r => r.Provider == UnattendedLoginProvider.MicrosoftEntraId && r.LoginName == TestUser && r.Password == Password);
        page.Find("[data-testid=ba-identity]").TextContent.Should().Be(TestUser);
        page.Find("[data-testid=ba-credential]").TextContent.Should().Be("Securely configured");
        page.Markup.Should().NotContain(Password);

        page.Find("[data-testid=ba-edit]").Click();
        page.Find("[data-testid=ba-form-login]").GetAttribute("value").Should().Be(TestUser, "Edit keeps the selected test identity");
        page.Find("[data-testid=ba-form-password-state]").TextContent.Should().Be("Securely configured");
        page.FindAll("[data-testid=ba-form-password]").Should().BeEmpty("the stored password is never put in the form");
        page.Find("[data-testid=ba-form-protected]").Change("/admin");
        page.Find("[data-testid=ba-editor]").Submit();
        _api.Saved[^1].Password.Should().BeNull("an unrelated change keeps the stored password");
        _api.Saved[^1].LoginName.Should().Be(TestUser);

        page.Find("[data-testid=ba-edit]").Click();
        page.Find("[data-testid=ba-form-replace-password]").Click();
        page.Find("[data-testid=ba-form-password]").Change("replaced-fake-pw");
        page.Find("[data-testid=ba-editor]").Submit();
        _api.Saved[^1].Password.Should().Be("replaced-fake-pw");
        page.Markup.Should().NotContain("replaced-fake-pw");
    }

    [Fact]
    public void Validate_ShowsEverySeparateStep_AndConnected()
    {
        var page = ConfigureEntra();
        page.Find("[data-testid=ba-validate]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=ba-unattended-status]").TextContent.Should().Be("Connected"));
        page.FindAll("[data-testid=ba-step]").Select(s => s.GetAttribute("data-step")).Should().Equal(StepKeys);
        page.FindAll("[data-testid=ba-step]").Should().OnlyContain(s => s.GetAttribute("data-status") == "Passed");
    }

    [Fact]
    public void Mfa_ShowsRequiresInteraction_AndWhereItStopped()
    {
        _api.NextOutcome = UnattendedLoginOutcome.InteractiveChallengeDetected;
        var page = ConfigureEntra();
        page.Find("[data-testid=ba-validate]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=ba-unattended-status]").TextContent.Should().Be("Requires interaction"));
        page.Find("[data-testid=ba-result-box]").TextContent.Should().Contain("Requires interaction").And.Contain("Use attended login");
        page.Find("[data-testid=ba-step][data-step=authentication]").GetAttribute("data-status").Should().Be("RequiresInteraction");
        page.Find("[data-testid=ba-step][data-step=identity]").TextContent.Should().Contain("Not reached");
    }

    [Fact]
    public void FailedIdentity_IsFailed_NeverConnected()
    {
        _api.NextOutcome = UnattendedLoginOutcome.IdentityMismatch;
        var page = ConfigureEntra();
        page.Find("[data-testid=ba-validate]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=ba-unattended-status]").TextContent.Should().Be("Failed"));
        page.Find("[data-testid=ba-step][data-step=identity]").GetAttribute("data-status").Should().Be("Failed");
        page.Find("[data-testid=ba-result-box]").TextContent.Should().Contain("Different account signed in");
    }
}
