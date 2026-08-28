using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.FrontendQualityEngines;

/// <summary>Diagnostic test for legacy configuration detection.</summary>
public sealed class LegacyDiagnosticTest
{
    [Fact(DisplayName = "Legacy: GetSection().Exists() detects leaf-only keys")]
    public void LegacyDetection_WorksWithLeafOnlyKeys()
    {
        using var factory = TestHostConfiguration.CreateHostWithEngineEnabled("FrontendBrowserRuntime:Enabled");
        using var scope = factory.Services.CreateScope();

        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        // Check if GetSection recognizes the section exists
        var section = config.GetSection("FrontendBrowserRuntime");
        section.Exists().Should().BeTrue(because: "section with leaf values should be detected");

        // Check if the leaf value can be read
        var enabledValue = config.GetValue<bool>("FrontendBrowserRuntime:Enabled");
        enabledValue.Should().BeTrue(because: "leaf value should be readable");
    }
}
