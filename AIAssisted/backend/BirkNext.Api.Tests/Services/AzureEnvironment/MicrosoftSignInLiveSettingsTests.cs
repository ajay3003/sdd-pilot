using System.Text.Json.Nodes;
using BirkNext.Api.Data;
using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using BirkNext.Api.Services.AzureEnvironment;
using BirkNext.AzureEnvironment;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BirkNext.Api.Tests.Services.AzureEnvironment;

/// <summary>
/// Microsoft sign-in setup applies while BirkNext runs: the values saved in System Settings reload the configuration and the services reading
/// IOptions&lt;AzureEnvironmentOptions&gt; (registered as <see cref="LiveAzureEnvironmentOptions"/>, as in Program.cs) follow them, so no restart is
/// needed. A sign-in made with a previous client id or tenant no longer counts. Temporary content root only; no Microsoft call.
/// </summary>
public sealed class MicrosoftSignInLiveSettingsTests : IDisposable
{
    private const string ClientId = "11111111-2222-3333-4444-555555555555";
    private readonly string _root = Directory.CreateTempSubdirectory("birknext-live-signin-").FullName;
    private string LocalJson => Path.Combine(_root, "appsettings.Local.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>The Program.cs wiring: the reloadable local file, Configure, and the live IOptions registration.</summary>
    private (AdminService Admin, ServiceProvider Provider) Create()
    {
        var config = new ConfigurationBuilder().AddJsonFile(LocalJson, optional: true, reloadOnChange: false)
            .AddJsonFile(Path.Combine(_root, "App_Data", "microsoft-sign-in.json"), optional: true, reloadOnChange: false).Build();
        var services = new ServiceCollection();
        services.Configure<AzureEnvironmentOptions>(config.GetSection(AzureEnvironmentOptions.SectionName));
        services.AddSingleton<IOptions<AzureEnvironmentOptions>, LiveAzureEnvironmentOptions>();
        var provider = services.BuildServiceProvider();
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(_root);
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return (new AdminService(config, env.Object, db, NullLogger<AdminService>.Instance, signInRuntime: provider.GetRequiredService<IOptions<AzureEnvironmentOptions>>()), provider);
    }

    [Fact]
    public async Task Saved_sign_in_settings_are_applied_without_a_restart()
    {
        File.WriteAllText(LocalJson, """{ "FeatureVisibility": { "X": true } }""");
        var (admin, provider) = Create();
        var live = provider.GetRequiredService<IOptions<AzureEnvironmentOptions>>();
        live.Value.Configured.Should().BeFalse("the screenshot state: sign-in off, no client id");
        var signIn = new AzureSignInService(live, Mock.Of<IAzureTokenBroker>(), Mock.Of<IAzureSignInBrowser>(b => b.Available == true), NullLogger<AzureSignInService>.Instance);
        signIn.Status().State.Should().Be(AzureConnectionState.NotConfigured);

        var (saved, message) = await admin.SaveSettingsAsync(new SaveSettingsRequest
            { MicrosoftSignIn = new() { Enabled = true, ClientId = ClientId, TenantId = "bufdir.no", AllowDeviceCode = true } });

        saved.Should().BeTrue(message);
        message.Should().Contain("applied").And.Contain("No restart is needed");
        admin.BuildMicrosoftSignInInfo().RestartRequired.Should().BeFalse();
        live.Value.Should().Match<AzureEnvironmentOptions>(o => o.Configured && o.ClientId == ClientId && o.Tenant == "bufdir.no");
        signIn.Status().State.Should().Be(AzureConnectionState.SignedOut, "the running service now sees a configured public client, ready to sign in");
        File.ReadAllText(LocalJson).Should().Be("""{ "FeatureVisibility": { "X": true } }""", "appsettings.Local.json is never written for sign-in settings");

        // Turning it off again applies as well.
        (await admin.SaveSettingsAsync(new SaveSettingsRequest { MicrosoftSignIn = new() { Enabled = false, ClientId = ClientId, TenantId = "bufdir.no", AllowDeviceCode = true } })).Success.Should().BeTrue();
        signIn.Status().State.Should().Be(AzureConnectionState.NotConfigured);
    }

    private sealed class Mutable(AzureEnvironmentOptions value) : IOptions<AzureEnvironmentOptions>
    {
        public AzureEnvironmentOptions Value { get; set; } = value;
    }

    [Fact]
    public async Task A_sign_in_made_with_a_previous_client_id_no_longer_counts_after_the_setup_changes()
    {
        var options = new Mutable(new AzureEnvironmentOptions { Enabled = true, ClientId = ClientId });
        var token = new AzureToken("token-value", DateTimeOffset.UtcNow.AddHours(1), "person@contoso.example", "tenant");
        var broker = new Mock<IAzureTokenBroker>();
        broker.Setup(b => b.DeviceCodeAsync(It.IsAny<Func<AzureDeviceCodePrompt, Task>>(), It.IsAny<CancellationToken>())).ReturnsAsync(token);
        var service = new AzureSignInService(options, broker.Object, Mock.Of<IAzureSignInBrowser>(b => b.Available == false), NullLogger<AzureSignInService>.Instance);

        service.StartDeviceCode();
        for (var i = 0; i < 200 && service.Status().State != AzureConnectionState.SignedIn; i++) await Task.Delay(10);
        service.Status().State.Should().Be(AzureConnectionState.SignedIn);
        (await service.AccessTokenAsync(false, default)).Should().Be("token-value");

        options.Value = new AzureEnvironmentOptions { Enabled = true, ClientId = "99999999-2222-3333-4444-555555555555" };
        var status = service.Status();
        status.State.Should().Be(AzureConnectionState.SignedOut);
        status.Message.Should().Contain("changed since you signed in");
        status.Account.Should().BeNull();
        (await service.AccessTokenAsync(false, default)).Should().BeNull("a token from the previous app registration is never used");
    }
}
