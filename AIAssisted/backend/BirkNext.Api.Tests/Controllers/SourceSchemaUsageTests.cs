using BirkNext.Api.Controllers;
using BirkNext.SourceDomains;
using FluentAssertions;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>
/// Schema Studio's three answers per XSD (2026-11-18): declared in source · referenced by validation code · verified in a runtime test.
/// Packaging (often in bulk) and registry listing declare a schema; only naming references raise the tier; runtime is never claimed.
/// </summary>
public sealed class SourceSchemaUsageTests
{
    private static SourceContract Xsd(params SourceContractCodeReference[] refs) => new()
    {
        Type = SourceContractType.XmlSchema, File = "xsds/a.v1.xsd", CodeReferences = [.. refs],
        XmlSchema = new XmlSchemaEvidence("urn:a.v1", null, "file name", [], [], [], []),
    };

    [Fact]
    public void A_literal_validator_is_validation_code_and_runtime_is_never_claimed()
    {
        var usage = SourceSchemaUsage.Of(Xsd(new SourceContractCodeReference("Hub/Validator.cs", 12, "Validator")));
        usage.Tier.Should().Be(SourceSchemaUsage.ValidationTier);
        usage.TierBasis.Should().Be("Named by validation code: Hub/Validator.cs:12");
        usage.RuntimeVerified.Should().BeFalse();
        usage.RuntimeVerification.Should().Be(SourceSchemaUsage.NotRuntimeVerified);
        usage.TargetNamespace.Should().Be("urn:a.v1");
    }

    [Fact]
    public void A_registry_validator_is_validation_code_chosen_per_message()
    {
        var usage = SourceSchemaUsage.Of(Xsd(new SourceContractCodeReference("Hub/Register.cs", 1, SourceContractReferenceKinds.RegistryValidator), new SourceContractCodeReference("xsds/registry.json", 1, SourceContractReferenceKinds.RegistryEntry)));
        usage.Tier.Should().Be(SourceSchemaUsage.ValidationTier);
        usage.TierBasis.Should().Contain("schema registry").And.Contain("chosen per message by namespace");
    }

    [Theory]
    [InlineData("Test code reference")]
    [InlineData("Configuration reference")]
    [InlineData("Code reference")]
    public void A_naming_reference_outside_validation_code_is_referenced_in_source(string kind) =>
        SourceSchemaUsage.Of(Xsd(new SourceContractCodeReference("x/file", 3, kind))).Tier.Should().Be(SourceSchemaUsage.SourceTier);

    [Fact]
    public void Packaged_or_registry_listed_only_is_declared_not_referenced()
    {
        var packaged = SourceSchemaUsage.Of(Xsd(new SourceContractCodeReference("TestData/TestData.csproj", 10, "Embedded resource")));
        packaged.Tier.Should().Be(SourceSchemaUsage.DeclaredTier);
        packaged.TierBasis.Should().StartWith("Packaged with a project (TestData/TestData.csproj:10)");
        SourceSchemaUsage.Of(Xsd(new SourceContractCodeReference("xsds/registry.json", 1, SourceContractReferenceKinds.RegistryEntry))).Tier.Should().Be(SourceSchemaUsage.DeclaredTier);
        SourceSchemaUsage.Of(null).Tier.Should().Be(SourceSchemaUsage.DeclaredTier);
    }

    [Fact]
    public void A_snapshot_analysed_before_namespace_references_says_so()
    {
        SourceSchemaUsage.Of(Xsd(), staleAnalysis: true).TierBasis.Should().Contain("re-import the source to refresh");
        SourceSchemaUsage.Of(Xsd(new SourceContractCodeReference("Hub/Validator.cs", 1, "Validator")), staleAnalysis: true).TierBasis.Should().NotContain("re-import", "a validation reference is already conclusive");
    }
}
