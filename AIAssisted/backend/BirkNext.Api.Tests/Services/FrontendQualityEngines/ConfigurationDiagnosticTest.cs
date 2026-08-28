using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.FrontendQualityEngines;

/// <summary>Diagnostic test to verify configuration setup is working correctly.</summary>
public sealed class ConfigurationDiagnosticTest
{
    [Fact(DisplayName = "Configuration: in-memory override is readable")]
    public void InMemoryOverride_IsReadable()
    {
        using var factory = TestHostConfiguration.CreateHostWithEngineEnabled("FrontendQualityCapabilities:PassiveSecurityAllowed");
        using var scope = factory.Services.CreateScope();

        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var section = config.GetSection("FrontendQualityCapabilities:PassiveSecurityAllowed");
        section.Exists().Should().BeTrue(because: "key should exist");

        var value = config["FrontendQualityCapabilities:PassiveSecurityAllowed"];
        value.Should().Be("true", because: "override should set value to 'true'");

        var getValueResult = config.GetValue<bool>("FrontendQualityCapabilities:PassiveSecurityAllowed", false);
        getValueResult.Should().BeTrue(because: "GetValue should parse 'true' to bool true");

        var sectionValue = section.Value;
        bool.TryParse(sectionValue, out var parsed).Should().BeTrue();
        parsed.Should().BeTrue(because: "section.Value should be parseable to true");
    }
}
