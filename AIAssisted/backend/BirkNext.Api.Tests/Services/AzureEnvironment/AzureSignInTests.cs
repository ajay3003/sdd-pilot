using System.Text.Json;
using BirkNext.Api.Services.AuthenticatedReview;
using BirkNext.Api.Services.AzureEnvironment;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.AzureEnvironment;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Tests.Services.AzureEnvironment;

/// <summary>
/// Sign-in: the person's own interactive sign-in (dedicated Edge profile, device code as fallback), tokens in memory only, refresh after a
/// PIM activation they made themselves, explicit Not configured requirements, and a profile that is never the normal or another BirkNext profile.
/// </summary>
public sealed class AzureSignInTests
{
    private sealed class FakeBroker : IAzureTokenBroker
    {
        public TaskCompletionSource<AzureToken> Interactive { get; } = new();
        public List<bool> Silent { get; } = [];
        public AzureToken? SilentResult { get; set; } = new("refreshed-token-value", DateTimeOffset.UtcNow.AddHours(1), "person@contoso.example", AzureEnvironmentFixtures.Tenant);
        public bool SignedOut { get; private set; }
        public Uri? Opened { get; private set; }

        public async Task<AzureToken> InteractiveAsync(Func<Uri, Task> openBrowser, CancellationToken ct)
        {
            Opened = new Uri("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?client_id=x&code_challenge=y");
            await openBrowser(Opened);
            return await Interactive.Task.WaitAsync(ct);
        }

        public async Task<AzureToken> DeviceCodeAsync(Func<AzureDeviceCodePrompt, Task> prompt, CancellationToken ct)
        {
            await prompt(new("ABCD-1234", "https://microsoft.com/devicelogin", DateTimeOffset.UtcNow.AddMinutes(15)));
            return await Interactive.Task.WaitAsync(ct);
        }

        public Task<AzureToken?> SilentAsync(bool forceRefresh, CancellationToken ct) { Silent.Add(forceRefresh); return Task.FromResult(SilentResult); }
        public Task SignOutAsync() { SignedOut = true; return Task.CompletedTask; }
    }

    private sealed class FakeBrowser(bool available = true) : IAzureSignInBrowser
    {
        public List<Uri> Opened { get; } = [];
        public bool Available => available;
        public string? UnavailableReason => available ? null : "No local browser.";
        public Task OpenAsync(Uri signInUri) { Opened.Add(signInUri); return Task.CompletedTask; }
    }

    private static AzureSignInService Service(FakeBroker broker, FakeBrowser browser, AzureEnvironmentOptions? options = null) =>
        new(Options.Create(options ?? new AzureEnvironmentOptions { Enabled = true, ClientId = "00000000-0000-0000-0000-00000000c11e" }), broker, browser, NullLogger<AzureSignInService>.Instance);

    private static async Task<AzureConnectionStatus> Until(AzureSignInService service, Func<AzureConnectionStatus, bool> done)
    {
        for (var i = 0; i < 200; i++) { var s = service.Status(); if (done(s)) return s; await Task.Delay(10); }
        return service.Status();
    }

    [Fact]
    public void Not_configured_lists_the_app_registration_requirements_and_needs_no_secret()
    {
        var status = Service(new FakeBroker(), new FakeBrowser(), new AzureEnvironmentOptions()).Status();
        status.State.Should().Be(AzureConnectionState.NotConfigured);
        status.Requirements.Should().Contain(r => r.Contains("public client")).And.Contain(r => r.Contains("user_impersonation")).And.Contain(r => r.Contains("No client secret"));
        Service(new FakeBroker(), new FakeBrowser(), new AzureEnvironmentOptions { Enabled = true }).Status().Message.Should().Contain("ClientId");
    }

    [Fact]
    public async Task Interactive_sign_in_opens_the_dedicated_profile_and_keeps_the_token_out_of_the_status()
    {
        var broker = new FakeBroker();
        var browser = new FakeBrowser();
        var service = Service(broker, browser);
        service.StartInteractive().State.Should().Be(AzureConnectionState.SigningIn);
        await Until(service, _ => browser.Opened.Count > 0);
        browser.Opened.Should().ContainSingle().Which.Host.Should().Be("login.microsoftonline.com");

        broker.Interactive.SetResult(new("interactive-token-value", DateTimeOffset.UtcNow.AddHours(1), "person@contoso.example", AzureEnvironmentFixtures.Tenant));
        var status = await Until(service, s => s.State == AzureConnectionState.SignedIn);
        status.Should().Match<AzureConnectionStatus>(s => s.State == AzureConnectionState.SignedIn && s.Method == AzureSignInMethod.DedicatedEdgeProfile && s.Account == "person@contoso.example");
        JsonSerializer.Serialize(status).Should().NotContain("token-value");
        (await service.AccessTokenAsync(false, default)).Should().Be("interactive-token-value");
    }

    [Fact]
    public async Task Device_code_shows_the_code_and_a_cancelled_sign_in_is_explained()
    {
        var broker = new FakeBroker();
        var service = Service(broker, new FakeBrowser(available: false));
        service.StartInteractive().Should().Match<AzureConnectionStatus>(s => s.State == AzureConnectionState.Failed && s.Message == "No local browser.");

        service.StartDeviceCode();
        var waiting = await Until(service, s => s.State == AzureConnectionState.AwaitingDeviceCode);
        waiting.DeviceCode.Should().Match<AzureDeviceCodePrompt>(d => d.UserCode == "ABCD-1234" && d.VerificationUrl == "https://microsoft.com/devicelogin");
        broker.Interactive.SetException(new Microsoft.Identity.Client.MsalClientException("authentication_canceled", "cancelled"));
        (await Until(service, s => s.State == AzureConnectionState.Failed)).Message.Should().Be("Sign-in was cancelled in the browser.");

        Service(new FakeBroker(), new FakeBrowser(), new AzureEnvironmentOptions { Enabled = true, ClientId = "x", AllowDeviceCode = false }).StartDeviceCode().State.Should().Be(AzureConnectionState.Failed);
    }

    [Fact]
    public async Task Refresh_requests_a_new_token_and_sign_out_removes_it()
    {
        var broker = new FakeBroker();
        var service = Service(broker, new FakeBrowser());
        service.StartInteractive();
        broker.Interactive.SetResult(new("first-token", DateTimeOffset.UtcNow.AddHours(1), "person@contoso.example", AzureEnvironmentFixtures.Tenant));
        await Until(service, s => s.State == AzureConnectionState.SignedIn);

        var refreshed = await service.RefreshAsync(default);
        broker.Silent.Should().Equal(true);
        refreshed.Message.Should().Contain("newly activated roles");
        (await service.AccessTokenAsync(false, default)).Should().Be("refreshed-token-value");

        (await service.SignOutAsync()).State.Should().Be(AzureConnectionState.SignedOut);
        broker.SignedOut.Should().BeTrue();
        (await service.AccessTokenAsync(false, default)).Should().BeNull();
    }

    [Fact]
    public async Task A_session_that_cannot_refresh_silently_becomes_expired()
    {
        var broker = new FakeBroker { SilentResult = null };
        var service = Service(broker, new FakeBrowser());
        service.StartInteractive();
        broker.Interactive.SetResult(new("short-token", DateTimeOffset.UtcNow.AddMinutes(1), "person@contoso.example", AzureEnvironmentFixtures.Tenant));
        await Until(service, s => s.State == AzureConnectionState.SignedIn);
        (await service.AccessTokenAsync(false, default)).Should().BeNull("the token is inside the refresh margin and no silent refresh is possible");
        service.Status().State.Should().Be(AzureConnectionState.Expired);
    }

    [Fact]
    public void The_profile_is_dedicated_and_launched_without_remote_debugging()
    {
        AzureSignInProfile.Resolve(null).Should().EndWith(Path.Combine("BirkNext", AzureSignInProfile.DirectoryName));
        FluentActions.Invoking(() => AzureSignInProfile.Resolve(@"C:\Users\someone\AppData\Local\Microsoft\Edge\User Data")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => AzureSignInProfile.Resolve(ManagedEdgePreflightService.DefaultProfileDirectory())).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => AzureSignInProfile.Resolve(Path.Combine(BirkNext.Api.Services.LocalHttpsProxy.DedicatedCompanionProvisioner.ManagedRoot, "profile"))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => AzureSignInProfile.Resolve("relative/profile")).Should().Throw<ArgumentException>();

        var args = AzureSignInProfile.LaunchArguments(@"C:\p\AzureSignInEdgeProfile", new Uri("https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize?x=1"));
        args.Should().Contain(@"--user-data-dir=C:\p\AzureSignInEdgeProfile").And.NotContain(a => a.Contains("remote-debugging") || a.Contains("headless"));
        FluentActions.Invoking(() => AzureSignInProfile.LaunchArguments(@"C:\p", new Uri("https://evil.example/login"))).Should().Throw<ArgumentException>();
        AzureSignInProfile.DefaultDirectory().Should().NotContain("@", "no e-mail address is part of the profile path");
    }

    [Fact]
    public void The_Edge_browser_is_offered_only_on_a_local_workstation_with_Edge_installed()
    {
        var local = Options.Create(new AuthenticatedReviewOptions { Enabled = true, Runtime = "LocalWorkstation" });
        var remote = Options.Create(new AuthenticatedReviewOptions { Enabled = false });
        var options = Options.Create(new AzureEnvironmentOptions { Enabled = true, ClientId = "x" });
        new DedicatedEdgeSignInBrowser(new Locator(new EdgeInstallation(@"C:\Edge\msedge.exe", "1")), options, local, NullLogger<DedicatedEdgeSignInBrowser>.Instance).Available.Should().BeTrue();
        new DedicatedEdgeSignInBrowser(new Locator(new EdgeInstallation(@"C:\Edge\msedge.exe", "1")), options, remote, NullLogger<DedicatedEdgeSignInBrowser>.Instance).UnavailableReason.Should().Contain("device-code");
        new DedicatedEdgeSignInBrowser(new Locator(null), options, local, NullLogger<DedicatedEdgeSignInBrowser>.Instance).Available.Should().BeFalse();
    }

    private sealed class Locator(EdgeInstallation? edge) : IEdgeInstallationLocator { public EdgeInstallation? Locate() => edge; }
}
