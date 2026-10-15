using System.Text.Json;
using BirkNext.Web.Services;
using BirkNext.Web.Services.SchemaStudio;

namespace BirkNext.Web.Tests.Services;

public sealed class SchemaGeneratedXmlStoreTests
{
    private const string Xsd = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" targetNamespace=\"urn:saved\" xmlns=\"urn:saved\"><xs:element name=\"Root\" type=\"xs:string\"/></xs:schema>";

    [Fact]
    public void ExplicitlySavedXmlRestoresWithSchemaSnapshotAndProjectProvenance()
    {
        var provenance = new SchemaProvenance("Source Analysis", "source.zip", Guid.NewGuid(),
            new string('a', 64), DateTimeOffset.UtcNow, "contracts/root.xsd", new string('b', 64),
            ProjectImportId: "import-42", SourceProjectName: "Example project");
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["contracts/root.xsd"] = Xsd }, "saved", provenance);
        var store = new SchemaGeneratedXmlStore();
        var profileId = Guid.NewGuid();
        var saved = store.Save(package, "contracts/root.xsd", "Generic valid", 4101,
            "<Root>synthetic-7f1c</Root>", "import:42", profileId);

        Assert.Contains("synthetic-7f1c", store.SnapshotJson("import:42"));
        Assert.DoesNotContain("synthetic-7f1c", store.SnapshotJson("import:other"));
        var reopened = new SchemaGeneratedXmlStore();
        reopened.RestoreJson(store.SnapshotJson("import:42"));
        var artifact = Assert.Single(reopened.Documents);
        Assert.Equal(saved.Id, artifact.Id);
        Assert.Equal(package.Fingerprint, artifact.PackageFingerprint);
        Assert.Equal(provenance.SourceSnapshotId, artifact.Provenance?.SourceSnapshotId);
        Assert.Equal(provenance.SourceSnapshotFingerprint, artifact.Provenance?.SourceSnapshotFingerprint);
        Assert.Equal(profileId, artifact.ProfileId);
        Assert.Equal("import:42", artifact.ProjectScope);
        Assert.Equal("<Root>synthetic-7f1c</Root>", artifact.Xml);
    }

    [Fact]
    public void XmlStoreRejectsUnsafePathsAndEnforcesWorkspaceScopeOnDeletion()
    {
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["root.xsd"] = Xsd }, "saved");
        var store = new SchemaGeneratedXmlStore();
        var saved = store.Save(package, "root.xsd", "Manual XML", null, "<Root />", "import:a");

        Assert.False(store.Remove(saved.Id, "import:b"));
        Assert.True(store.Remove(saved.Id, "import:a"));
        var unsafeFile = saved with { FileName = "../outside.xsd" };
        var json = JsonSerializer.Serialize(new { version = 1, items = new[] { unsafeFile } }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Throws<InvalidDataException>(() => new SchemaGeneratedXmlStore().RestoreJson(json));
    }

    [Fact]
    public void WorkspacePersistenceStoresXmlArtifactWithoutTurningItIntoAnSddSourceRevision()
    {
        var repository = new WorkspaceArtifactRepository();

        repository.Set(WorkspaceArtifactType.SchemaStudioGeneratedXml, "{\"version\":1,\"items\":[]}",
            "schema-studio-generated-xml.json", "Schema Studio saved XML artifacts");

        Assert.NotNull(repository.Get(WorkspaceArtifactType.SchemaStudioGeneratedXml));
        Assert.Empty(repository.GetArtifactRevisions(WorkspaceArtifactType.SchemaStudioGeneratedXml));
    }
}
