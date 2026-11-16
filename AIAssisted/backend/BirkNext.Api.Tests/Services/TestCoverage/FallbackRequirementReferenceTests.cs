using BirkNext.Api.Services.SourceAnalysis.TestBehavior;
using BirkNext.Sdd;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestCoverage;

/// <summary>
/// NUnit/MSTest tests have no discovery provider, so their requirement references come from the fallback over method name, attributes and
/// leading comments. It uses the shared requirement grammar: a compact id in a test names the same requirement as in the specification.
/// </summary>
public sealed class FallbackRequirementReferenceTests
{
    [Fact]
    public void Compact_ids_are_normalised_like_specifications_and_xunit_discovery()
    {
        var refs = TestBehaviorSourceAnalyzer.FallbackRequirementReferences("Login_Works [Test] [Category(\"FR-1\")] // Verifies SC-12 and NFR-003").ToList();

        refs.Should().Equal("FR-001", "SC-012", "NFR-003");
        refs.Should().Equal(RequirementReferenceParser.Extract("FR-1 SC-12 NFR-003"), "the fallback and specifications share one grammar");
    }

    [Fact]
    public void Lower_case_prose_and_test_data_are_not_requirement_references()
    {
        TestBehaviorSourceAnalyzer.FallbackRequirementReferences("Parses [TestCase(\"UTF-8\")] [TestCase(\"ISO-8601\")] // see fr-2 in the old notes")
            .Should().BeEmpty("source ids count only as written in upper case, and test data is not a requirement prefix");
    }
}
