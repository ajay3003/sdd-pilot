using System.Text.Json.Nodes;
using BirkNext.Api.Data;
using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using BirkNext.Api.Services.AzureEnvironment;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// System Settings → Git Sources → Microsoft sign-in setup: the AzureEnvironment sign-in values are saved to the installation's appsettings.Local.json
/// (a temporary content root here, never the developer's file), validated like the Git Sources diagnostics, merged with the keys already in the
/// section, and reported as needing a restart while the running sign-in client still uses the values it started with.
/// </summary>
public sealed class AdminServiceMicrosoftSignInTests : IDisposable
{
    private const string ClientId = "11111111-2222-3333-4444-555555555555";
    private readonly string _root = Directory.CreateTempSubdirectory("birknext-signin-").FullName;
    private string LocalJson => Path.Combine(_root, "appsettings.Local.json");
    private string SignInJson => Path.Combine(_root, "App_Data", "microsoft-sign-in.json");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private (AdminService Service, IConfigurationRoot Config) Create(AzureEnvironmentOptions? running = null)
    {
        // Program.cs order: appsettings.Local.json, then BirkNext's own sign-in settings file.
        var config = new ConfigurationBuilder().AddJsonFile(LocalJson, optional: true, reloadOnChange: false).AddJsonFile(SignInJson, optional: true, reloadOnChange: false).Build();
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(_root);
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        return (new AdminService(config, env.Object, db, NullLogger<AdminService>.Instance, signInRuntime: Options.Create(running ?? new AzureEnvironmentOptions())), config);
    }

    [Fact]
    public async Task Saving_writes_only_the_sign_in_values_to_App_Data_never_appsettings_Local_and_asks_for_a_restart()
    {
        File.WriteAllText(LocalJson, """{ "AzureEnvironment": { "MaxResources": 123 }, "FeatureVisibility": { "X": true } }""");
        var localBefore = File.ReadAllText(LocalJson);
        var (service, config) = Create();

        var (saved, message) = await service.SaveSettingsAsync(new SaveSettingsRequest
        {
            MicrosoftSignIn = new() { Enabled = true, ClientId = $" {ClientId} ", TenantId = "bufdir.no", AllowDeviceCode = false }
        });

        saved.Should().BeTrue(message);
        message.Should().Contain("Restart BirkNext");
        File.ReadAllText(LocalJson).Should().Be(localBefore, "the installation's appsettings.Local.json is never written for sign-in settings");
        var root = JsonNode.Parse(File.ReadAllText(SignInJson))!.AsObject();
        var section = root["AzureEnvironment"]!.AsObject();
        ((bool)section["Enabled"]!).Should().BeTrue();
        ((string)section["ClientId"]!).Should().Be(ClientId);
        ((string)section["TenantId"]!).Should().Be("bufdir.no");
        ((bool)section["AllowDeviceCode"]!).Should().BeFalse();
        section.ContainsKey("MaxResources").Should().BeFalse("only the sign-in values are in BirkNext's file");
        config["AzureEnvironment:MaxResources"].Should().Be("123", "appsettings.Local.json keeps supplying the keys the form does not edit");
        File.ReadAllText(SignInJson).Should().NotContainAny("secret", "password", "token");

        var info = service.BuildMicrosoftSignInInfo();
        info.Should().BeEquivalentTo(new MicrosoftSignInInfo { Enabled = true, ClientId = ClientId, TenantId = "bufdir.no", AllowDeviceCode = false, RestartRequired = true });
    }

    [Fact]
    public void Saved_values_matching_the_running_sign_in_need_no_restart()
    {
        File.WriteAllText(LocalJson, $$"""{ "AzureEnvironment": { "Enabled": true, "ClientId": "{{ClientId}}" } }""");
        var (service, _) = Create(new AzureEnvironmentOptions { Enabled = true, ClientId = ClientId });

        service.BuildMicrosoftSignInInfo().RestartRequired.Should().BeFalse();
        service.BuildMicrosoftSignInInfo().TenantId.Should().BeEmpty("an empty tenant means the account's home tenant");
    }

    [Theory]
    [InlineData(true, "not-a-guid", null, "Application (client) ID")]
    [InlineData(true, "", null, "before it can be turned on")]
    [InlineData(false, ClientId, "not a tenant!", "Directory (tenant) ID")]
    [InlineData(false, ClientId, "common", "organizations")]
    [InlineData(false, ClientId, "consumers", "organizations")]
    public async Task Invalid_values_are_refused_and_nothing_is_written(bool enabled, string clientId, string? tenant, string reason)
    {
        var (service, _) = Create();
        var request = new SaveSettingsRequest { MicrosoftSignIn = new() { Enabled = enabled, ClientId = clientId, TenantId = tenant } };

        var (valid, error) = service.ValidateSettingsUpdate(request);
        var (saved, message) = await service.SaveSettingsAsync(request);

        valid.Should().BeFalse();
        error.Should().Contain(reason);
        saved.Should().BeFalse();
        message.Should().Be(error);
        File.Exists(LocalJson).Should().BeFalse();
        File.Exists(SignInJson).Should().BeFalse();
    }

    [Fact]
    public async Task An_unwritable_App_Data_is_reported_and_nothing_is_claimed_saved()
    {
        File.WriteAllText(Path.Combine(_root, "App_Data"), "a file where the folder should be");
        var (service, _) = Create();
        var (saved, message) = await service.SaveSettingsAsync(new SaveSettingsRequest { MicrosoftSignIn = new() { Enabled = true, ClientId = ClientId } });
        saved.Should().BeFalse();
        message.Should().Contain("not writable");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("organizations", true)]
    [InlineData("bufdir.no", true)]
    [InlineData("72f988bf-86f1-41af-91ab-2d7cd011db47", true)]
    [InlineData("https://login.microsoftonline.com/x", false)]
    [InlineData("bufdir", false)]
    public void Tenant_validation_matches_the_Git_Sources_diagnostics(string? tenant, bool valid) =>
        AzureEnvironmentOptions.IsValidTenant(tenant).Should().Be(valid);
}
