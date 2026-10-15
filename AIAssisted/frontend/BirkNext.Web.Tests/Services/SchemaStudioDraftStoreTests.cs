using BirkNext.Web.Services.SchemaStudio;

namespace BirkNext.Web.Tests.Services;

public sealed class SchemaStudioDraftStoreTests
{
    private static SchemaPackage Package(Guid? snapshotId = null) => new SchemaStudioService().Import(
        new Dictionary<string, string>
        {
            ["main.xsd"] = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"Root\" type=\"xs:string\"/></xs:schema>"
        }, "local", snapshotId is null ? null : new SchemaProvenance("Source Analysis", "source.zip", snapshotId,
            "snapshot-fingerprint", DateTimeOffset.UnixEpoch, "main.xsd", "contract-fingerprint"));

    [Fact]
    public void DraftSurvivesSnapshotRestoreAndRemainsBoundToPackageFileAndWorkspace()
    {
        var package = Package(Guid.NewGuid());
        var store = new SchemaStudioDraftStore();
        const string edited = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"Changed\" type=\"xs:string\"/></xs:schema>";

        var saved = store.Save(package, "main.xsd", edited, "import:project-a");
        var reopened = new SchemaStudioDraftStore();
        reopened.RestoreJson(store.SnapshotJson("import:project-a"));

        Assert.Equal(edited, reopened.Find(package, "main.xsd", "import:project-a")?.Text);
        Assert.Null(reopened.Find(package, "main.xsd", "import:project-b"));
        Assert.Null(reopened.Find(package, "other.xsd", "import:project-a"));
        Assert.Equal(saved.Id, reopened.Drafts.Single().Id);
        Assert.True(reopened.Remove(package, "main.xsd", "import:project-a"));
        Assert.Empty(reopened.Drafts);
    }

    [Fact]
    public void DraftIdentityDoesNotCrossSourceSnapshot()
    {
        var first = Package(Guid.NewGuid());
        var second = Package(Guid.NewGuid());
        var store = new SchemaStudioDraftStore();
        store.Save(first, "main.xsd", "<draft />");

        Assert.Null(store.Find(second, "main.xsd"));
    }

    [Fact]
    public void SaveRejectsFileOutsideTheSelectedPackage()
    {
        Assert.Throws<InvalidDataException>(() => new SchemaStudioDraftStore().Save(Package(), "other.xsd", "<draft />"));
    }

    [Theory]
    [InlineData("../escape.xsd")]
    [InlineData("C:/outside.xsd")]
    [InlineData("folder/../main.xsd")]
    [InlineData("main.xml")]
    public void RejectsUnsafeOrNonXsdDraftPaths(string path)
    {
        Assert.Throws<InvalidDataException>(() => new SchemaStudioDraftStore().Save(Package(), path, "<draft />"));
    }

    [Fact]
    public void RestoreRejectsForgedDraftIdentity()
    {
        var store = new SchemaStudioDraftStore();
        var package = Package();
        store.Save(package, "main.xsd", "<draft />");
        var json = store.SnapshotJson().Replace("main.xsd", "other.xsd", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => new SchemaStudioDraftStore().RestoreJson(json));
    }
}
