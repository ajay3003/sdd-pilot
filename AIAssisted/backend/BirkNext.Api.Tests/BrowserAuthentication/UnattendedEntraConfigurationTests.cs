using System.Text.Json;
using BirkNext.Api.Services.BrowserAuthentication;
using BirkNext.BrowserAuthentication;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.BrowserAuthentication;

/// <summary>Microsoft Entra ID unattended login configuration (no browser): the selected test identity is kept on Edit, the password is
/// write-only and replaceable alone, Connected only follows a successful validation of the current configuration, and MFA/conditional access
/// show Requires interaction.</summary>
public sealed class UnattendedEntraConfigurationTests : IDisposable
{
    private const string TestUser = "testbruker01@bufdir.no";
    private const string Password = "fake-entra-password-1";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-entra-config-" + Guid.NewGuid().ToString("N"));
    private readonly UnattendedLoginStore _store;
    private readonly UnattendedLoginService _service;

    public UnattendedEntraConfigurationTests()
    {
        _store = new UnattendedLoginStore(_dir);
        _service = new UnattendedLoginService(_store, new UnattendedLoginRunner(new NoBrowser(), NullLogger<UnattendedLoginRunner>.Instance), TimeProvider.System,
            NullLogger<UnattendedLoginService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class NoBrowser : IUnattendedLoginBrowserLauncher
    {
        public Task<(Microsoft.Playwright.IPlaywright Playwright, Microsoft.Playwright.IBrowser Browser)> LaunchAsync(CancellationToken ct) => throw new InvalidOperationException();
    }

    private static SaveUnattendedLoginRequest Entra(string? login = TestUser, string? password = Password, string? resource = null) => new()
    {
        Provider = UnattendedLoginProvider.MicrosoftEntraId, LoginName = login, Password = password, LoginUrl = BrowserAuthenticationText.EntraDefaultApplicationUrl,
        ProtectedResourceUrl = resource, Enabled = true,
    };

    private void MarkValidated(UnattendedLoginOutcome outcome)
    {
        var settings = _store.Load()!;
        _store.Save(settings with { LastValidation = new StoredValidation(outcome.ToString(), "fake", DateTimeOffset.UtcNow, UnattendedLoginService.Fingerprint(settings), 10) });
    }

    [Fact]
    public void Save_NeedsNoSuccessSelector_ShowsTheTestIdentity_NeverThePassword_Configured()
    {
        var view = _service.Save(Entra()).View!;

        view.Provider.Should().Be(UnattendedLoginProvider.MicrosoftEntraId);
        view.TestIdentity.Should().Be(TestUser);
        view.IdentityAlias.Should().Be(TestUser, "the test identity names the automation identity");
        view.CredentialConfigured.Should().BeTrue();
        view.ConnectionStatus.Should().Be(UnattendedConnectionStatus.Configured);
        view.ConnectionStatusLabel.Should().Be("Configured");
        view.Requirements.Single(r => r.Key == "success-detection").Detail.Should().Contain("Authenticated application session");
        JsonSerializer.Serialize(view).Should().NotContain(Password);
        foreach (var file in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories)) File.ReadAllText(file).Should().NotContain(Password);
    }

    [Fact]
    public void Edit_KeepsTheSelectedIdentityAndCredential_UnrelatedChangeNeedsNoPassword()
    {
        _service.Save(Entra());
        var version = _store.Load()!.CredentialVersion;

        var edited = _service.Save(Entra(password: null, resource: "/admin"));

        edited.Errors.Should().BeEmpty();
        edited.View!.TestIdentity.Should().Be(TestUser);
        edited.View.CredentialConfigured.Should().BeTrue();
        _store.Load()!.CredentialVersion.Should().Be(version, "saving an unrelated change keeps the stored credential");
        _store.ReadCredential()!.Password.Should().Be(Password);
    }

    [Fact]
    public void ReplacePasswordOnly_KeepsTheIdentity_InvalidatesTheValidation()
    {
        _service.Save(Entra());
        MarkValidated(UnattendedLoginOutcome.Succeeded);
        _service.View().ConnectionStatus.Should().Be(UnattendedConnectionStatus.Connected);

        var replaced = _service.Save(Entra(login: null, password: "new-fake-password-2"));

        replaced.Errors.Should().BeEmpty();
        _store.ReadCredential()!.LoginName.Should().Be(TestUser);
        _store.ReadCredential()!.Password.Should().Be("new-fake-password-2");
        replaced.View!.ConnectionStatus.Should().Be(UnattendedConnectionStatus.Configured, "a new password needs a new validation before Connected");
        replaced.View.Audit.Select(a => a.Event).Should().Contain("Credential replaced");
    }

    [Fact]
    public void AnotherIdentity_NeedsItsPassword()
    {
        _service.Save(Entra());
        _service.Save(Entra(login: "testbruker02@bufdir.no", password: null)).Errors.Should().ContainSingle().Which.Should().Contain("password of the selected test identity");
        _service.Save(Entra(login: "not an account", password: "x")).Errors.Should().Contain(e => e.Contains("account name"));
    }

    [Theory]
    [InlineData(UnattendedLoginOutcome.Succeeded, UnattendedConnectionStatus.Connected)]
    [InlineData(UnattendedLoginOutcome.InteractiveChallengeDetected, UnattendedConnectionStatus.RequiresInteraction)]
    [InlineData(UnattendedLoginOutcome.ConditionalAccessChallenge, UnattendedConnectionStatus.RequiresInteraction)]
    [InlineData(UnattendedLoginOutcome.AuthenticationRejected, UnattendedConnectionStatus.Failed)]
    [InlineData(UnattendedLoginOutcome.EntraRedirectNotDetected, UnattendedConnectionStatus.Failed)]
    [InlineData(UnattendedLoginOutcome.IdentityMismatch, UnattendedConnectionStatus.Failed)]
    [InlineData(UnattendedLoginOutcome.ProtectedResourceDenied, UnattendedConnectionStatus.Failed)]
    public void ConnectionStatus_FollowsTheValidationOutcome(UnattendedLoginOutcome outcome, UnattendedConnectionStatus expected)
    {
        _service.Save(Entra());
        MarkValidated(outcome);
        _service.View().ConnectionStatus.Should().Be(expected);
    }

    [Fact]
    public void Connected_IsNotKeptWhenTheConfigurationChanges()
    {
        _service.Save(Entra());
        MarkValidated(UnattendedLoginOutcome.Succeeded);
        _service.Save(Entra(password: null, resource: "/protected"));
        _service.View().ConnectionStatus.Should().Be(UnattendedConnectionStatus.Configured);
        _service.AutomatedReadiness().Available.Should().BeFalse();
    }

    [Fact]
    public void ProtectedResource_MustBeOnTheApplication()
    {
        _service.Save(Entra(resource: "https://evil.example/steal")).Errors.Should().Contain(e => e.Contains("protected resource"));
        _service.Save(Entra(resource: "/admin")).Errors.Should().BeEmpty();
        EntraIdLoginFlow.ResolveResource("https://m2lbdev.bufetat.no", "/admin").Should().Be("https://m2lbdev.bufetat.no/admin");
        EntraIdLoginFlow.ResolveResource("https://m2lbdev.bufetat.no", null).Should().Be("https://m2lbdev.bufetat.no");
    }

    [Fact]
    public void GenericProvider_StillHidesTheLoginName()
    {
        var view = _service.Save(new SaveUnattendedLoginRequest
        {
            IdentityAlias = "generic", LoginName = TestUser, Password = Password, LoginUrl = "https://login.example.test/", SuccessSelector = "#ok",
        }).View!;
        view.TestIdentity.Should().BeNull();
        JsonSerializer.Serialize(view).Should().NotContain(TestUser);
        File.ReadAllText(Path.Combine(_dir, "unattended-login.json")).Should().NotContain(TestUser);
    }

    private sealed class Env(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "BirkNext.Api";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Theory]
    [InlineData("Development", "127.0.0.1:5298", true)]
    [InlineData("Development", "localhost", true)]
    [InlineData("Production", "127.0.0.1:5298", false)]
    [InlineData("Development", "evil.example", false)]
    public void DevelopmentEntraHosts_OnlyLoopbackInDevelopment(string environment, string hosts, bool honoured)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["BrowserAuthentication:Unattended:DevelopmentEntraHosts"] = hosts }).Build();
        var set = EntraIdLoginFlow.DevelopmentHosts(new Env(environment), configuration);
        (set is not null).Should().Be(honoured);
        if (set is not null) set.Should().Contain("login.microsoftonline.com").And.Contain(hosts);
    }
}
