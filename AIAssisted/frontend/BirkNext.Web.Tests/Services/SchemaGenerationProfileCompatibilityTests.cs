using BirkNext.Web.Services.SchemaStudio;

namespace BirkNext.Web.Tests.Services;

/// <summary>Profiles saved before the rename ("Test Data Profiles") reopen unchanged: the stored JSON never carried C# type names.</summary>
public sealed class SchemaGenerationProfileCompatibilityTests
{
    private const string Xsd = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="Root"><xs:complexType><xs:sequence><xs:element name="Count" type="xs:int"/></xs:sequence></xs:complexType></xs:element></xs:schema>""";

    [Fact]
    public void ProfilesSavedByThePreviousNamingReopen_WithRulesAndKind()
    {
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["compat.xsd"] = Xsd }, "compat.xsd");
        // Exactly the shape the checkpoint (TestDataProfilesService.SnapshotJson) wrote: camelCase properties, enums as numbers.
        var saved = $$"""
            [{"id":"3f2b8c1e-0d6a-4c1f-9a3e-2b7d5c9e1a00","name":"Saved before rename","schemaFingerprint":"{{package.Fingerprint}}","projectScope":"import:a",
              "kind":1,"seed":42,"rules":[{"elementPath":"/Root/Count","mode":2,"minimum":1,"maximum":5,"minimumLength":null,"maximumLength":null,
              "dateFrom":null,"dateTo":null,"occurrenceCount":null}],"createdAt":"2026-10-14T08:00:00+00:00","updatedAt":"2026-10-14T08:00:00+00:00","isBuiltIn":false}]
            """;
        var catalog = new SchemaGenerationProfilesService();
        catalog.RestoreJson(saved);

        var profile = Assert.Single(catalog.Profiles);
        Assert.Equal("Saved before rename", profile.Name);
        Assert.Equal(SchemaGenerationProfileKind.Boundary, profile.Kind);
        Assert.Equal(SchemaGenerationValueMode.Boundary, Assert.Single(profile.Rules).Mode);
        Assert.True(catalog.IsCompatible(profile, package, "import:a"));
        Assert.Contains("\"kind\":1", catalog.SnapshotJson("import:a"), StringComparison.Ordinal);
    }
}
