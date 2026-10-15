using System.Text.RegularExpressions;
using BirkNext.Schemas;
using FluentAssertions;
using BirkNext.Web.Services.SchemaStudio;

namespace BirkNext.Web.Tests.Services;

/// <summary>Generated XML from the real Skolenærvær schema is schema-valid where intended and never contains real-looking identities.</summary>
public sealed class SchemaGenerationSafetyTests
{
    private const string Skolenaervaer = "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd";

    private static string RealSchema(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "AIAssisted", "xsd"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("AIAssisted/xsd"), "AIAssisted", "xsd", name));
    }

    [Theory]
    [InlineData(SchemaGenerationProfileKind.GenericValid)]
    [InlineData(SchemaGenerationProfileKind.Boundary)]
    [InlineData(SchemaGenerationProfileKind.Minimal)]
    [InlineData(SchemaGenerationProfileKind.MaximumComplexity)]
    public void BuiltInProfiles_GenerateValidXml_WithoutRealLookingIdentifiers(SchemaGenerationProfileKind kind)
    {
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { [Skolenaervaer] = RealSchema(Skolenaervaer) }, "skolenaervaer");
        var profile = SchemaGenerationProfilesService.BuiltIns(package.Fingerprint).Single(p => p.Kind == kind);
        var xml = SchemaStudioService.GenerateProfileXml(package, "Skolenaervaer", new SchemaGenerationProfilesService().GenerateValues(package, profile), profile);

        var validation = SchemaStudioService.Validate(package, Skolenaervaer, xml);
        validation.Status.Should().Be(XmlSchemaValidationStatus.Valid, validation.Message);
        foreach (Match digits in Regex.Matches(xml, @"(?<![\d.-])\d{9,12}(?![\d.-])"))
            digits.Value.Should().MatchRegex("^[01]0*$", "identifier-shaped values (FNR/D-number/DUF/organisation number) are synthetic zeros, never real-looking");
        if (kind == SchemaGenerationProfileKind.MaximumComplexity)
            xml.Should().Contain("<Fodselsnummer>00000000000</Fodselsnummer>").And.Contain("<DUFnummer>000000000000</DUFnummer>");
    }
}
