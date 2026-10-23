using BirkNext.Web.Services;
using BirkNext.Web.Services.SchemaStudio;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Registering saved Schema Studio XML as reusable Test Data: explicit (only saved XML, chosen alias and intent; approval belongs to Test Data), reversible, scoped,
/// persisted with the saved document — and Schema Studio holds no second catalog (the registration is one optional field on saved XML).
/// </summary>
public sealed class SchemaXmlTestDataRegistrationTests
{
    private static readonly SchemaXmlRegistrationFacts Valid = new(SchemaXmlRegistrationFacts.Valid, SchemaXmlRegistrationFacts.Valid);
    private static readonly SchemaXmlRegistrationFacts Invalid = new(SchemaXmlRegistrationFacts.Invalid, SchemaXmlRegistrationFacts.Invalid);

    private const string Xsd = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"urn:saved\" xmlns=\"urn:saved\"><xs:element name=\"Root\" type=\"xs:string\"/></xs:schema>";

    private static (SchemaGeneratedXmlStore Store, SavedSchemaXml Saved) Saved(string scope = "import:a")
    {
        var provenance = new SchemaProvenance("Source Analysis", "source.zip", Guid.NewGuid(), new string('a', 64), DateTimeOffset.UtcNow,
            "contracts/root.xsd", new string('b', 64));
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["contracts/root.xsd"] = Xsd }, "saved", provenance);
        var store = new SchemaGeneratedXmlStore();
        return (store, store.Save(package, "contracts/root.xsd", "Generic valid", 7, "<Root>synthetic</Root>", scope));
    }

    [Fact]
    public void SavedXmlIsNotTestDataUntilRegistered_ThenKeepsAliasIntentAndFactsAcrossRestore_NeverApproved()
    {
        var (store, saved) = Saved();
        Assert.Null(saved.TestData);
        Assert.DoesNotContain("\"testData\":{", store.SnapshotJson("import:a"));

        var registered = store.Register(saved.Id, "import:a", "missing-root", SchemaXmlTestDataRegistration.NegativeTest, facts: Invalid);
        Assert.Equal("missing-root", registered.TestData!.Alias);

        var reopened = new SchemaGeneratedXmlStore();
        reopened.RestoreJson(store.SnapshotJson("import:a"));
        var registration = Assert.Single(reopened.Documents).TestData!;
        Assert.Equal(SchemaXmlTestDataRegistration.NegativeTest, registration.Intent);
        Assert.False(registration.Approved);
        Assert.Equal(SchemaXmlRegistrationFacts.Invalid, registration.Facts!.ExpectedResult);
        Assert.Equal(saved.Xml, reopened.Documents[0].Xml);
    }

    [Fact]
    public void RegistrationRequiresSavedXml_AValidUniqueAlias_AndAKnownIntent()
    {
        var (store, saved) = Saved();
        Assert.Throws<InvalidDataException>(() => store.Register(Guid.NewGuid(), "import:a", "never-saved", SchemaXmlTestDataRegistration.Positive, facts: Valid));
        Assert.Throws<InvalidDataException>(() => store.Register(saved.Id, "import:other", "wrong-scope", SchemaXmlTestDataRegistration.Positive, facts: Valid));
        Assert.Throws<InvalidDataException>(() => store.Register(saved.Id, "import:a", "Not Valid!", SchemaXmlTestDataRegistration.Positive, facts: Valid));
        Assert.Throws<InvalidDataException>(() => store.Register(saved.Id, "import:a", "ok-alias", "maybe", facts: Valid));

        store.Register(saved.Id, "import:a", "report-a", SchemaXmlTestDataRegistration.Positive, facts: Valid);
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["contracts/root.xsd"] = Xsd }, "saved");
        var second = store.Save(package, "contracts/root.xsd", "Manual XML", null, "<Root />", "import:a");
        Assert.Throws<InvalidDataException>(() => store.Register(second.Id, "import:a", "report-a", SchemaXmlTestDataRegistration.Positive, facts: Valid));
    }

    [Fact]
    public void Unregistering_KeepsTheSavedXml_AndRestoreRejectsAForgedRegistration()
    {
        var (store, saved) = Saved();
        store.Register(saved.Id, "import:a", "report-a", SchemaXmlTestDataRegistration.Positive, facts: Valid);
        Assert.True(store.Unregister(saved.Id, "import:a"));
        Assert.Null(Assert.Single(store.Documents).TestData);
        Assert.False(store.Unregister(saved.Id, "import:a"));

        store.Register(saved.Id, "import:a", "report-a", SchemaXmlTestDataRegistration.Positive, facts: Valid);
        var forged = store.SnapshotJson("import:a").Replace("\"alias\":\"report-a\"", "\"alias\":\"../Report A\"");
        Assert.Throws<InvalidDataException>(() => new SchemaGeneratedXmlStore().RestoreJson(forged));
    }
}
