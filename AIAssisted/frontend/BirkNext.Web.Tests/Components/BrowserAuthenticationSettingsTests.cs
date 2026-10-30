using BirkNext.BrowserAuthentication;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// System Settings → Authentication: attended and unattended cards side by side (no mode dropdown), the attended card read-only,
/// the unattended card configurable, the secret masked and never rendered, and no validation on page load.
/// </summary>
public sealed class BrowserAuthenticationSettingsTests : BunitContext
{
    private const string Secret = "S3cret!-ui-pw";
    private readonly FakeApi _api = new();

    public BrowserAuthenticationSettingsTests()
    {
        Services.AddSingleton<IBrowserAuthenticationApiService>(_api);
        var targets = new Mock<IFrontendAnalysisSettingsService>();
        targets.SetupGet(t => t.ActiveProfile).Returns(new FrontendAnalysisProfile { Id = "dev", Name = "Development", TargetUrl = "https://app.example.test/" });
        Services.AddSingleton(targets.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>In-memory backend with the real state rules: save → Not verified, validate → Ready, changes invalidate, clear → Not configured.</summary>
    private sealed class FakeApi : IBrowserAuthenticationApiService
    {
        public UnattendedLoginView View { get; set; } = new() { Configured = false, State = UnattendedLoginState.NotConfigured, StateLabel = "Not configured" };
        public int ValidateCalls { get; private set; }
        public List<SaveUnattendedLoginRequest> Saved { get; } = [];
        public UnattendedLoginOutcome NextOutcome { get; set; } = UnattendedLoginOutcome.Succeeded;

        public Task<(UnattendedLoginView? View, string? Error)> GetAsync(CancellationToken ct = default) => Task.FromResult<(UnattendedLoginView?, string?)>((View, null));
        public Task<AutomatedBrowserExecutionReadiness?> AutomatedExecutionAsync(CancellationToken ct = default) =>
            Task.FromResult<AutomatedBrowserExecutionReadiness?>(View.State == UnattendedLoginState.Ready
                ? new(true, "Available", "Unattended browser authentication is ready.", View.State, [])
                : new(false, "Automated browser execution unavailable", View.Configured ? "Unattended browser authentication has not been validated with the current configuration" : "Unattended browser authentication not configured", View.State, []));
        public Task<(UnattendedLoginView? View, string? Error)> SaveAsync(SaveUnattendedLoginRequest request, CancellationToken ct = default)
        {
            Saved.Add(request);
            var hasCredential = View.CredentialConfigured || !string.IsNullOrEmpty(request.Password);
            if (string.IsNullOrWhiteSpace(request.IdentityAlias)) return Task.FromResult<(UnattendedLoginView?, string?)>((null, "Enter the automation identity name."));
            View = View with
            {
                Configured = true, Enabled = request.Enabled, IdentityAlias = request.IdentityAlias, CredentialConfigured = hasCredential, LoginNameHint = "a•••@e•••.test",
                LoginUrl = request.LoginUrl, SuccessUrlContains = request.SuccessUrlContains, SuccessSelector = request.SuccessSelector,
                State = request.Enabled ? UnattendedLoginState.NotVerified : UnattendedLoginState.Disabled,
                StateLabel = request.Enabled ? "Not verified" : "Configured · Disabled", LastValidation = View.LastValidation is { } v ? v with { Current = false } : null,
            };
            return Task.FromResult<(UnattendedLoginView?, string?)>((View, null));
        }
        public Task<(UnattendedLoginView? View, string? Error)> SetEnabledAsync(bool enabled, CancellationToken ct = default)
        {
            View = View with { Enabled = enabled, State = enabled ? UnattendedLoginState.NotVerified : UnattendedLoginState.Disabled, StateLabel = enabled ? "Not verified" : "Configured · Disabled" };
            return Task.FromResult<(UnattendedLoginView?, string?)>((View, null));
        }
        public Task<(UnattendedLoginValidationResult? Result, string? Error)> ValidateAsync(CancellationToken ct = default)
        {
            ValidateCalls++;
            var ok = NextOutcome == UnattendedLoginOutcome.Succeeded;
            View = View with
            {
                State = ok ? UnattendedLoginState.Ready : UnattendedLoginState.ValidationFailed, StateLabel = ok ? "Ready" : "Validation failed",
                LastValidation = new(NextOutcome, ok ? "Signed in" : "Unattended login blocked", new DateTimeOffset(2026, 10, 30, 10, 15, 0, TimeSpan.Zero), true, 900),
            };
            return Task.FromResult<(UnattendedLoginValidationResult?, string?)>((new(NextOutcome, View.LastValidation.Message, BrowserAuthenticationText.Recommendation(NextOutcome), 900, View), null));
        }
        public Task<string?> ClearAsync(CancellationToken ct = default)
        {
            View = new() { Configured = false, State = UnattendedLoginState.NotConfigured, StateLabel = "Not configured" };
            return Task.FromResult<string?>(null);
        }
    }

    private IRenderedComponent<BrowserAuthenticationSettings> Configure(IRenderedComponent<BrowserAuthenticationSettings> page, string identity = "fake-automation-identity", string? password = Secret)
    {
        page.Find("[data-testid=ba-configure], [data-testid=ba-edit]").Click();
        page.Find("[data-testid=ba-form-provider]").Change(nameof(UnattendedLoginProvider.Generic));
        page.Find("[data-testid=ba-form-identity]").Change(identity);
        if (password is not null)
        {
            page.Find("[data-testid=ba-form-login]").Change("automation@example.test");
            page.Find("[data-testid=ba-form-password]").Change(password);
        }
        page.Find("[data-testid=ba-form-url]").Change("https://login.example.test/");
        page.Find("[data-testid=ba-form-success-url]").Change("/app");
        page.Find("[data-testid=ba-editor]").Submit();
        return page;
    }

    [Fact]
    public void BothModesAreVisibleSideBySide_AttendedIsInformational_AndThereIsNoModeDropdown()
    {
        var page = Render<BrowserAuthenticationSettings>();

        page.Find(".ba-title").TextContent.Should().Be("Authentication");
        page.Find("[data-testid=ba-attended]").TextContent.Should().Contain("Attended login").And.Contain("Used when a person must complete login or MFA.").And.Contain("Supported");
        page.Find("[data-testid=ba-attended-status]").TextContent.Should().Be("Available");
        page.Find("[data-testid=ba-attended-view]").GetAttribute("href").Should().Be("admin/system-settings?section=target-environments&tab=auth");
        page.Find("[data-testid=ba-attended]").QuerySelectorAll("button, input, select").Should().BeEmpty("the attended card cannot change attended settings");
        page.Find("[data-testid=ba-unattended-status]").TextContent.Should().Be("Not configured");
        page.Find("[data-testid=ba-unattended-empty]").TextContent.Should().Contain("dedicated automation test identity");
        page.FindAll("select").Should().BeEmpty("modes are not a mutually exclusive choice");
        page.Markup.Should().NotContainEquivalentOf("bypass");
        page.Find("[data-testid=ba-automated-reason]").TextContent.Should().Be("Unattended browser authentication not configured");
        _api.ValidateCalls.Should().Be(0, "opening the page never attempts a login");
    }

    [Fact]
    public void Configure_PersistsMasked_NotVerified_ThenValidate_IsReadyWithTimestamp()
    {
        var page = Configure(Render<BrowserAuthenticationSettings>());

        page.WaitForAssertion(() => page.Find("[data-testid=ba-unattended-status]").TextContent.Should().Be("Not verified"));
        page.Find("[data-testid=ba-credential]").TextContent.Should().StartWith("Configured securely");
        page.Markup.Should().NotContain(Secret, "the password is never rendered after saving");
        _api.Saved.Single().Password.Should().Be(Secret);

        // Reload: a fresh render reads the persisted state.
        var reloaded = Render<BrowserAuthenticationSettings>();
        reloaded.Find("[data-testid=ba-unattended-status]").TextContent.Should().Be("Not verified");
        reloaded.Find("[data-testid=ba-identity]").TextContent.Should().Be("fake-automation-identity");
        reloaded.Markup.Should().NotContain(Secret);

        reloaded.Find("[data-testid=ba-validate]").Click();
        reloaded.WaitForAssertion(() => reloaded.Find("[data-testid=ba-unattended-status]").TextContent.Should().Be("Ready"));
        reloaded.Find("[data-testid=ba-validation]").TextContent.Should().StartWith("Ready · validated 30 Oct 2026");
        reloaded.Find("[data-testid=ba-result]").GetAttribute("aria-live").Should().Be("polite");
        reloaded.Find("[data-testid=ba-automated-status]").TextContent.Should().Be("Available");
    }

    [Fact]
    public void EditingIdentity_ReturnsToNotVerified_WithoutReenteringTheSecret()
    {
        var page = Configure(Render<BrowserAuthenticationSettings>());
        page.Find("[data-testid=ba-validate]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=ba-unattended-status]").TextContent.Should().Be("Ready"));

        Configure(page, identity: "another-identity", password: null);

        page.WaitForAssertion(() => page.Find("[data-testid=ba-unattended-status]").TextContent.Should().Be("Not verified"));
        _api.Saved.Last().Password.Should().BeNull("an empty password keeps the stored credential");
    }

    [Fact]
    public void MfaValidation_ShowsFailureAndRecommendsAttendedLogin()
    {
        _api.NextOutcome = UnattendedLoginOutcome.InteractiveChallengeDetected;
        var page = Configure(Render<BrowserAuthenticationSettings>());

        page.Find("[data-testid=ba-validate]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=ba-result-box]").TextContent.Should().Contain("Requires interaction").And.Contain("MFA or interactive challenge detected").And.Contain("Use attended login"));
        page.Find("[data-testid=ba-unattended-status]").TextContent.Should().Be("Validation failed");
    }

    [Fact]
    public void Disable_ShowsConfiguredDisabled_AndClear_NeedsConfirmation_LeavingAttendedUnchanged()
    {
        var page = Configure(Render<BrowserAuthenticationSettings>());
        var attendedBefore = page.Find("[data-testid=ba-attended]").InnerHtml;

        page.Find("[data-testid=ba-toggle]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=ba-unattended-status]").TextContent.Should().Be("Configured · Disabled"));
        page.Find("[data-testid=ba-identity]").TextContent.Should().Be("fake-automation-identity", "configuration is kept");

        page.Find("[data-testid=ba-remove]").Click();
        var dialog = page.Find("[data-testid=ba-remove-dialog]");
        dialog.GetAttribute("role").Should().Be("alertdialog");
        dialog.TextContent.Should().Contain("Attended login and its configuration are not affected");
        page.Find("[data-testid=ba-remove-confirm]").Click();

        page.WaitForAssertion(() => page.Find("[data-testid=ba-unattended-status]").TextContent.Should().Be("Not configured"));
        page.Find("[data-testid=ba-attended]").InnerHtml.Should().Be(attendedBefore);
    }

    [Fact]
    public void Editor_IsAnAccessibleDialog_WithLabelledMaskedSecret_AndErrorSummary()
    {
        var page = Render<BrowserAuthenticationSettings>();
        page.Find("[data-testid=ba-configure]").Click();
        page.Find("[data-testid=ba-form-provider]").Change(nameof(UnattendedLoginProvider.Generic));

        var dialog = page.Find("[data-testid=ba-editor]");
        dialog.GetAttribute("role").Should().Be("dialog");
        dialog.GetAttribute("aria-modal").Should().Be("true");
        var password = page.Find("[data-testid=ba-form-password]");
        password.GetAttribute("type").Should().Be("password");
        password.GetAttribute("autocomplete").Should().Be("new-password");
        password.Closest("label")!.TextContent.Should().Contain("Password");
        page.Find("[data-testid=ba-form-use-target]").TextContent.Should().Contain("https://app.example.test/");
        page.Find("[data-testid=ba-form-use-target]").Click();
        page.Find("[data-testid=ba-form-url]").GetAttribute("value").Should().Be("https://app.example.test/");

        dialog.Submit();
        page.Find("[data-testid=ba-form-errors]").GetAttribute("role").Should().Be("alert");
    }
}

/// <summary>Target Environment → Authentication points to the unattended capability without duplicating it (no credential fields there).</summary>
public sealed class BrowserLoginModesNoteTests : BunitContext
{
    [Fact]
    public void ShowsBothModes_UnattendedStateFromTheSharedCapability_AndNoCredentialInput()
    {
        var api = new Mock<IBrowserAuthenticationApiService>();
        api.Setup(a => a.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((new UnattendedLoginView { Configured = true, State = UnattendedLoginState.Ready, StateLabel = "Ready" }, (string?)null));
        Services.AddSingleton(api.Object);

        var note = Render<BrowserLoginModesNote>();

        note.Find("[data-testid=blm]").TextContent.Should().Contain("Attended login").And.Contain("Unattended login").And.Contain("No credentials are stored on this tab");
        note.Find("[data-testid=blm-unattended-state]").TextContent.Should().Be("Ready");
        note.Find("[data-testid=blm-link]").GetAttribute("href").Should().Be("admin/system-settings?section=authentication");
        note.FindAll("input, button, select").Should().BeEmpty();
        api.Verify(a => a.ValidateAsync(It.IsAny<CancellationToken>()), Times.Never, "reading the state never starts a login");
    }
}
