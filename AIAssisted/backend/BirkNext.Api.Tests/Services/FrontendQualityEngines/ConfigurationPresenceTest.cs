using Microsoft.Extensions.Configuration;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.FrontendQualityEngines;

/// <summary>Verify ASP.NET IConfiguration presence detection semantics.</summary>
public sealed class ConfigurationPresenceTest
{
    [Fact(DisplayName = "Config: tri-state presence detection")]
    public void TriStatePresence()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "KeyWithFalse", "false" },
                { "KeyWithTrue", "true" },
                // KeyMissing not added
            })
            .Build();

        // Case A: MISSING
        var missing_indexer = config["KeyMissing"];
        var missing_getvalue_string = config.GetValue<string>("KeyMissing");
        var missing_getvalue_bool = config.GetValue<bool>("KeyMissing");
        var missing_section = config.GetSection("KeyMissing");
        var missing_section_exists = missing_section.Exists();
        var missing_section_value = missing_section.Value;

        missing_indexer.Should().BeNull(because: "missing key via indexer should be null");
        missing_getvalue_string.Should().BeNull(because: "missing key GetValue<string> should be null");
        missing_getvalue_bool.Should().BeFalse(because: "missing key GetValue<bool> defaults to false");
        missing_section_exists.Should().BeFalse(because: "missing section should not exist");
        missing_section_value.Should().BeNull(because: "missing section value should be null");

        // Case B: EXPLICIT FALSE
        var false_indexer = config["KeyWithFalse"];
        var false_getvalue_string = config.GetValue<string>("KeyWithFalse");
        var false_getvalue_bool = config.GetValue<bool>("KeyWithFalse");
        var false_section = config.GetSection("KeyWithFalse");
        var false_section_exists = false_section.Exists();
        var false_section_value = false_section.Value;

        false_indexer.Should().Be("false", because: "explicit false via indexer should be 'false' string");
        false_getvalue_string.Should().Be("false", because: "explicit false GetValue<string> should be 'false'");
        false_getvalue_bool.Should().BeFalse(because: "explicit false GetValue<bool> should parse to false");
        false_section_exists.Should().BeTrue(because: "explicit false section should exist");
        false_section_value.Should().Be("false", because: "explicit false section value should be 'false'");

        // Case C: EXPLICIT TRUE
        var true_indexer = config["KeyWithTrue"];
        var true_getvalue_string = config.GetValue<string>("KeyWithTrue");
        var true_getvalue_bool = config.GetValue<bool>("KeyWithTrue");
        var true_section = config.GetSection("KeyWithTrue");
        var true_section_exists = true_section.Exists();
        var true_section_value = true_section.Value;

        true_indexer.Should().Be("true", because: "explicit true via indexer should be 'true' string");
        true_getvalue_string.Should().Be("true", because: "explicit true GetValue<string> should be 'true'");
        true_getvalue_bool.Should().BeTrue(because: "explicit true GetValue<bool> should parse to true");
        true_section_exists.Should().BeTrue(because: "explicit true section should exist");
        true_section_value.Should().Be("true", because: "explicit true section value should be 'true'");
    }
}
