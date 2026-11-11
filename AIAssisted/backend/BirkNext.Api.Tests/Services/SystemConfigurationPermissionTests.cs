using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json.Nodes;
using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Models.Admin;
using BirkNext.Api.Services;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.LocalSecurity;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Changing configuration (System Settings save) needs the SystemConfiguration.Write capability, enforced by the backend: an authenticated
/// principal holding the permission, or the explicitly enabled loopback-only local grant (Development or the TesterNative package). Visibility
/// itself is not authorization: reading settings stays open. Every save is recorded with its actor — a hash for a signed-in principal,
/// local-session:loopback for the local grant, never a fabricated name.
/// </summary>
public sealed class SystemConfigurationPermissionTests : IDisposable
{
    private const string Frontend = "http://localhost:5173";
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly string _root = Directory.CreateTempSubdirectory("bn-sc-").FullName;

    public void Dispose() { _db.Dispose(); try { Directory.Delete(_root, true); } catch { /* temp */ } }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "BirkNext.Api";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static LocalTrustedExecution Local(string environment = "Development", string? setting = "true", string? profile = null) =>
        new(new Env(environment), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SystemConfigurationPermissions.LocalSetting] = setting, ["FRONTEND_ORIGIN"] = Frontend,
            [LocalTrustedExecution.Setting] = "true", // a different capability: it never implies configuration rights
            [BirkNext.BuildInfo.RuntimeProfiles.ConfigurationKey] = profile,
        }).Build());

    private static DefaultHttpContext Request(string remote = "127.0.0.1", string host = "localhost:5000", string? origin = Frontend)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Connection.LocalIpAddress = IPAddress.Loopback;
        context.Request.Host = new HostString(host);
        if (origin is not null) context.Request.Headers.Origin = origin;
        return context;
    }

    private static async Task<bool> Handle(ClaimsPrincipal user, LocalTrustedExecution local, HttpContext context)
    {
        var handler = new SystemConfigurationWriteHandler(local, new HttpContextAccessor { HttpContext = context });
        var authorization = new AuthorizationHandlerContext([new SystemConfigurationWriteRequirement()], user, null);
        await handler.HandleAsync(authorization);
        return authorization.HasSucceeded;
    }

    private static readonly ClaimsPrincipal LocalSession = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "local-session")], "BirkNextLocalSession"));
    private static ClaimsPrincipal Bearer(string scp, string oid = "11111111-1111-1111-1111-111111111111") =>
        new(new ClaimsIdentity([new Claim("oid", oid), new Claim("iss", "https://issuer.test"), new Claim("scp", scp), new Claim("preferred_username", "admin@example.test")], "Bearer"));

    [Fact]
    public async Task The_local_grant_needs_its_own_setting_and_every_loopback_condition()
    {
        (await Handle(LocalSession, Local(), Request())).Should().BeTrue("Development, setting on, loopback, localhost, frontend origin");
        (await Handle(LocalSession, Local(setting: null), Request())).Should().BeFalse("LocalTrustedExecution alone does not grant configuration rights");
        (await Handle(LocalSession, Local(), Request(remote: "192.168.1.20"))).Should().BeFalse("not loopback");
        (await Handle(LocalSession, Local(), Request(host: "evil.example:5000"))).Should().BeFalse("DNS-rebinding host");
        (await Handle(LocalSession, Local(), Request(origin: "http://evil.example"))).Should().BeFalse("another origin");
        (await Handle(LocalSession, Local(), Request(origin: null))).Should().BeFalse("no origin");
        (await Handle(LocalSession, Local(environment: "Production"), Request())).Should().BeFalse("a production server without the tester profile");
        (await Handle(LocalSession, Local(environment: "Production", profile: "TesterNative"), Request())).Should().BeTrue("the per-user Tester Package");
    }

    [Fact]
    public async Task A_signed_in_principal_needs_the_permission_and_other_permissions_do_not_imply_it()
    {
        var remote = Request(remote: "10.0.0.5");
        (await Handle(Bearer("User.Read SystemConfiguration.Write"), Local(setting: null), remote)).Should().BeTrue();
        (await Handle(Bearer("User.Read ActiveEventExecute IntegrationConfiguration.Write WorkspaceTransfer"), Local(setting: null), remote)).Should().BeFalse();
        SystemConfigurationPermissions.Evaluate(Bearer("User.Read"), Local(setting: null), remote).Reason.Should().Contain("without the SystemConfiguration.Write permission");
    }

    [Fact]
    public void The_actor_is_derived_and_never_a_name_or_a_fabricated_identity()
    {
        var actor = SystemConfigurationPermissions.Actor(Bearer("SystemConfiguration.Write"), localGranted: true);
        actor.Should().MatchRegex("^authenticated-user:[0-9a-f]{16}$").And.NotContain("admin");
        SystemConfigurationPermissions.Actor(Bearer("SystemConfiguration.Write", oid: "22222222-2222-2222-2222-222222222222"), false).Should().NotBe(actor);
        SystemConfigurationPermissions.Actor(LocalSession, localGranted: true).Should().Be("local-session:loopback");
        SystemConfigurationPermissions.Actor(LocalSession, localGranted: false).Should().BeNull();
    }

    [Fact]
    public void The_save_endpoint_is_protected_by_the_policy_and_reading_is_not()
    {
        typeof(AdminController).GetMethod(nameof(AdminController.SaveSystemSettings))!.GetCustomAttributes<AuthorizeAttribute>()
            .Should().ContainSingle(a => a.Policy == SystemConfigurationPermissions.Policy);
        foreach (var read in new[] { nameof(AdminController.GetSystemSettings), nameof(AdminController.GetEditableSettings), nameof(AdminController.GetFeatureVisibility) })
            typeof(AdminController).GetMethod(read)!.GetCustomAttributes<AuthorizeAttribute>().Should().BeEmpty($"{read} stays readable: visibility is not authorization");
    }

    private AdminService Service()
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        env.SetupGet(e => e.ContentRootPath).Returns(_root);
        return new AdminService(new ConfigurationBuilder().AddInMemoryCollection([]).Build(), env.Object, _db, NullLogger<AdminService>.Instance);
    }

    [Fact]
    public async Task Every_save_is_recorded_with_before_after_values_and_its_actor()
    {
        if (AppDataLocation.Override is not null) return; // the audit file follows BIRKNEXT_APP_DATA_DIR; this test needs the content-root default
        var service = Service();
        (await service.SaveSettingsAsync(new SaveSettingsRequest { FeatureVisibility = new() { ["ImpactAnalysis"] = true, ["SourceAnalysis"] = true } }, "local-session:loopback")).Success.Should().BeTrue();
        (await service.SaveSettingsAsync(new SaveSettingsRequest { Logging = new() { MinimumLevel = "Warning" } })).Success.Should().BeTrue();

        var records = service.ReadConfigurationAudit();
        records.Should().HaveCount(2);
        var first = records[1]!;
        first["actor"]!.GetValue<string>().Should().Be("local-session:loopback");
        first["featureVisibility"]!.AsArray().Should().ContainSingle("SourceAnalysis did not change (already on by default)");
        first["featureVisibility"]![0]!["key"]!.GetValue<string>().Should().Be("ImpactAnalysis");
        first["featureVisibility"]![0]!["before"]!.GetValue<bool>().Should().BeFalse();
        first["featureVisibility"]![0]!["after"]!.GetValue<bool>().Should().BeTrue();
        records[0]!["actor"]!.GetValue<string>().Should().Be("not attributed", "an identity is never invented");
        records[0]!["otherSections"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("Logging");
    }

    [Theory]
    [InlineData("QaArtifactLibrary")]
    [InlineData("BlazorWasmPerformanceReview")]
    [InlineData("EnableArchitectureView")]
    public void Retired_no_effect_switches_are_not_configurable_and_cannot_be_saved(string key)
    {
        var service = Service();
        var settings = service.BuildEditableSettings().FeatureVisibility;
        settings.Core.Concat(settings.Advanced).Should().NotContain(e => e.Key == key);
        var (valid, error) = service.ValidateSettingsUpdate(new SaveSettingsRequest { FeatureVisibility = new() { [key] = true } });
        valid.Should().BeFalse();
        error.Should().Contain("retired");
        service.BuildFeatureVisibility().EnableArchitectureView.Should().BeFalse("retiring never enables a switch");
        service.BuildFeatureVisibility().EnableExtractionReview.Should().BeFalse();
    }
}
