using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Tests.Utilities;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.AltinnTesting;

/// <summary>
/// Altinn host resolution: IQR stores the logical environment (Test); <c>AltinnTesting:Endpoints:Test</c> (defaults TT02) is the one source of
/// every Altinn address BirkNext builds or allows. Production has no endpoint. A configured address is not a verified one.
/// </summary>
public sealed class AltinnEndpointResolutionTests
{
    private const string MovedPlatform = "https://platform.altinn-test-moved.example/";
    private const string MovedApps = "apps.altinn-test-moved.example";

    private static AltinnTestingPolicy Bound(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(AltinnTestingOptions.Section).Get<AltinnTestingOptions>() ?? new AltinnTestingOptions());

    [Fact]
    public void Test_ResolvesToTheDefaultTt02Addresses()
    {
        var endpoints = AltinnTestKit.DefaultPolicy.EndpointsFor(ExternalTestEnvironment.Test, out var reason)!;
        endpoints.Platform.ToString().Should().Be("https://platform.tt02.altinn.no/");
        endpoints.AppBase("buf", "skolenaervaer").ToString().Should().Be("https://buf.apps.tt02.altinn.no/buf/skolenaervaer/");
        endpoints.TokenExchange.ToString().Should().Be("https://platform.tt02.altinn.no/authentication/api/v1/exchange/id-porten");
        endpoints.StorageInstance(5, AltinnTestKit.InstanceGuid).ToString().Should().Be($"https://platform.tt02.altinn.no/storage/api/v1/instances/5/{AltinnTestKit.InstanceGuid:D}");
        endpoints.PolicySource("buf", "skolenaervaer").ToString().Should().Be("https://platform.tt02.altinn.no/resourceregistry/api/v1/resource/app_buf_skolenaervaer/policy");
        reason.Should().Contain("Configured, not verified").And.NotContainAny("Verified.", "Healthy", "Reachable");
    }

    [Theory]
    [InlineData(ExternalTestEnvironment.Production)]
    [InlineData(ExternalTestEnvironment.Staging)]
    [InlineData(ExternalTestEnvironment.Unknown)]
    public void OnlyTestHasAnEndpoint_ProductionIsNeverResolved(ExternalTestEnvironment environment)
    {
        AltinnTestKit.DefaultPolicy.EndpointsFor(environment, out var reason).Should().BeNull();
        reason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ConfigurationOverride_WinsOverTheDefault_AndMovesEveryAltinnAddress()
    {
        var policy = Bound(new()
        {
            ["AltinnTesting:Endpoints:Test:PlatformBaseUrl"] = MovedPlatform,
            ["AltinnTesting:Endpoints:Test:AppsDomain"] = MovedApps,
        });

        policy.Endpoints.AppBase("buf", "skolenaervaer").ToString().Should().Be("https://buf.apps.altinn-test-moved.example/buf/skolenaervaer/");
        policy.Endpoints.TokenExchange.Host.Should().Be("platform.altinn-test-moved.example");
        policy.Endpoints.PolicySource("buf", "skolenaervaer").Host.Should().Be("platform.altinn-test-moved.example");
        policy.RequestBlock(new Uri("https://buf.apps.altinn-test-moved.example/buf/skolenaervaer/api/v1/applicationmetadata")).Should().BeNull();
        policy.RequestBlock(new Uri("https://buf.apps.tt02.altinn.no/buf/skolenaervaer/")).Should().NotBeNull("the old host is no longer allowed once configuration moves it");
        policy.RequestBlock(new Uri("https://test.idporten.no/token")).Should().BeNull("ID-porten TEST is its own endpoint family, not derived from the Altinn address");
    }

    [Fact]
    public void WithoutConfiguration_TheTt02DefaultsApply()
    {
        var policy = Bound([]);
        policy.Endpoints.Platform.Host.Should().Be("platform.tt02.altinn.no");
        policy.Endpoints.AppsDomain.Should().Be("apps.tt02.altinn.no");
    }

    [Theory]
    [InlineData(null, "apps.tt02.altinn.no", "PlatformBaseUrl")]
    [InlineData("http://platform.tt02.altinn.no/", "apps.tt02.altinn.no", "PlatformBaseUrl")]
    [InlineData("https://platform.tt02.altinn.no:8443/", "apps.tt02.altinn.no", "PlatformBaseUrl")]
    [InlineData("https://platform.tt02.altinn.no/?x=1", "apps.tt02.altinn.no", "PlatformBaseUrl")]
    [InlineData("https://platform.tt02.altinn.no/", null, "AppsDomain")]
    [InlineData("https://platform.tt02.altinn.no/", "https://apps.tt02.altinn.no", "AppsDomain")]
    [InlineData("https://platform.altinn.no/", "apps.tt02.altinn.no", "Production")]
    [InlineData("https://platform.tt02.altinn.no/", "apps.altinn.no", "Production")]
    public void MissingOrInvalidEndpoints_FailClearly_AndAllowNoAltinnHost(string? platform, string? apps, string expected)
    {
        var policy = new AltinnTestingPolicy(new AltinnTestingOptions
        {
            Endpoints = new AltinnEndpointOptions { Test = new AltinnEnvironmentEndpointOptions { PlatformBaseUrl = platform, AppsDomain = apps } },
        });

        FluentActions.Invoking(() => policy.Endpoints).Should().Throw<AltinnContractException>().WithMessage($"*{expected}*");
        policy.RequestBlock(new Uri("https://buf.apps.tt02.altinn.no/buf/skolenaervaer/")).Should().NotBeNull("misconfigured endpoints fail closed");
        policy.RequestBlock(new Uri("https://platform.altinn.no/x")).Should().Contain("production", "the production denylist is checked first, whatever is configured");
    }

    [Fact]
    public async Task MissingEndpoints_StopTheMetadataRead_BeforeAnyRequest()
    {
        var kit = new AltinnTestKit();
        kit.Options.Endpoints = new AltinnEndpointOptions { Test = new AltinnEnvironmentEndpointOptions() };
        var flows = new IntegrationJourneys.IntegrationJourneyHarness.FakeMessageFlows { Configuration = AltinnTestKit.ReadyConfiguration };

        await kit.Contracts(flows).Invoking(contracts => contracts.DiscoverPublicAsync(ActiveEventTesting.ActiveEventTestHarness.Env, default))
            .Should().ThrowAsync<AltinnContractException>().WithMessage("*AltinnTesting:Endpoints:Test:PlatformBaseUrl*");
        kit.Http.Calls.Should().BeEmpty();
    }

    [Fact]
    public void TestHosts_AreConfiguredAwayFromRealAltinn()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        var endpoints = factory.Services.GetRequiredService<AltinnTestingPolicy>().Endpoints;
        endpoints.Platform.ToString().Should().Be(AltinnNetworkSafety.PlatformBaseUrl);
        endpoints.AppsDomain.Should().Be(AltinnNetworkSafety.AppsDomain);
        endpoints.AppBase("buf", "skolenaervaer").Host.Should().EndWith(".invalid", "a test host can never reach Altinn TT02");
    }

    [Fact]
    public void AltinnHostnames_LiveOnlyInTheCentralEndpointDefaults_AndNeverInSkolenaervaerCode()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "BirkNext.Api"))) root = Path.GetDirectoryName(root)!;
        var api = Path.Combine(root, "BirkNext.Api");
        var sources = Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        var withTt02Host = sources.Where(path => File.ReadAllText(path).Contains("tt02.altinn.no", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).ToList();
        withTt02Host.Should().Equal(["AltinnEndpoints.cs"], "a TT02 host change is one configuration/default change");

        var skolenaervaer = sources.Where(path => path.Contains("SkoleNaervaer", StringComparison.OrdinalIgnoreCase)).ToList();
        skolenaervaer.Should().NotBeEmpty();
        skolenaervaer.Where(path => File.ReadAllText(path) is var text && (text.Contains("altinn.no", StringComparison.OrdinalIgnoreCase) || text.Contains("idporten.no", StringComparison.OrdinalIgnoreCase)))
            .Select(Path.GetFileName).Should().BeEmpty("Skolenærvær depends on Environment = Test, never on a host");
    }
}
