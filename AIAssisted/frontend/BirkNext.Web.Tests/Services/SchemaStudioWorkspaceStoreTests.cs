using BirkNext.Web.Services;
using BirkNext.Web.Services.SchemaStudio;

namespace BirkNext.Web.Tests.Services;

public sealed class SchemaStudioWorkspaceStoreTests
{
    private const string Xsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:saved" xmlns="urn:saved">
          <xs:element name="Record" type="xs:string"/>
        </xs:schema>
        """;

    private static SchemaPackage Import(SchemaProvenance? provenance = null) => new SchemaStudioService().Import(
        new Dictionary<string, string> { ["saved.xsd"] = Xsd }, "saved.xsd", provenance);

    [Fact]
    public void LocalXsdPackageCanBeSavedAndReopenedFromANewWorkspaceStore()
    {
        var package = Import();
        var store = new SchemaStudioWorkspaceStore();
        var saved = store.Save(package, "import:project-a");
        var json = store.SnapshotJson();

        var reopenedStore = new SchemaStudioWorkspaceStore();
        reopenedStore.RestoreJson(json);
        var reopened = Assert.Single(reopenedStore.Packages);
        Assert.Equal("import:project-a", reopened.ProjectScope);
        Assert.Equal(package.Fingerprint, reopened.Fingerprint);
        Assert.Equal(Xsd, Assert.Single(reopened.Files!).Value);
        var rebuilt = new SchemaStudioService().Import(reopened.Files!, reopened.Name, reopened.Provenance);
        Assert.Equal(package.Fingerprint, rebuilt.Fingerprint);
        var tamperTarget = new SchemaStudioService();
        Assert.Throws<InvalidDataException>(() => reopenedStore.ReopenLocal(reopened with { Fingerprint = "tampered" }, tamperTarget));
        Assert.Empty(tamperTarget.Packages);
        Assert.True(store.Remove(saved.Id, "import:project-a"));
        Assert.Empty(store.Packages);
        Assert.False(store.Remove(saved.Id, "import:project-b"));
    }

    [Fact]
    public void SourceAnalysisPackagePersistsExactSnapshotReferenceWithoutCopyingXsdText()
    {
        var snapshotId = Guid.NewGuid();
        var provenance = new SchemaProvenance("Source Analysis", "source.zip", snapshotId,
            "source-snapshot-fingerprint", DateTimeOffset.UtcNow, "saved.xsd", "contract-fingerprint");
        var store = new SchemaStudioWorkspaceStore();
        store.Save(Import(provenance), null);

        var json = store.SnapshotJson();
        Assert.DoesNotContain(Xsd, json, StringComparison.Ordinal);
        var reopenedStore = new SchemaStudioWorkspaceStore();
        reopenedStore.RestoreJson(json);
        var saved = Assert.Single(reopenedStore.Packages);
        Assert.Null(saved.Files);
        Assert.Equal(snapshotId, saved.Provenance!.SourceSnapshotId);
        Assert.Equal("source-snapshot-fingerprint", saved.Provenance.SourceSnapshotFingerprint);
        Assert.Equal("saved.xsd", saved.Provenance.SelectedContractPath);
    }

    [Fact]
    public void SnapshotJsonIncludesOnlyPackagesFromTheRequestedWorkspace()
    {
        var store = new SchemaStudioWorkspaceStore();
        store.Save(Import(), "import:project-a");
        store.Save(Import() with { Id = "other-id", Name = "other", Fingerprint = "other-fingerprint" }, "import:project-b");

        var json = store.SnapshotJson("import:project-a");
        var reopened = new SchemaStudioWorkspaceStore();
        reopened.RestoreJson(json);

        Assert.Collection(reopened.Packages, package => Assert.Equal("import:project-a", package.ProjectScope));
    }

    [Fact]
    public void WorkspaceStoreRejectsUnknownVersionsAndUnsafeOrOversizedPackages()
    {
        var store = new SchemaStudioWorkspaceStore();
        Assert.Throws<InvalidDataException>(() => store.RestoreJson("""{"version":99,"packages":[]}"""));

        var unsafeJson = """{"version":1,"packages":[{"id":"bad","name":"bad","fingerprint":"abc","savedAt":"2026-01-01T00:00:00Z","projectScope":null,"provenance":null,"files":{"../escape.xsd":"x"}}]}""";
        Assert.Throws<InvalidDataException>(() => store.RestoreJson(unsafeJson));
        Assert.Throws<InvalidDataException>(() => store.RestoreJson("""{"version":1,"packages":[null]}"""));
        var missingLocalContent = """{"version":1,"packages":[{"id":"local","name":"local","fingerprint":"abc","savedAt":"2026-01-01T00:00:00Z","projectScope":null,"provenance":null,"files":null}]}""";
        Assert.Throws<InvalidDataException>(() => store.RestoreJson(missingLocalContent));
        Assert.Throws<InvalidDataException>(() => store.RestoreJson(new string('x', SchemaStudioWorkspaceStore.MaxDocumentBytes + 1)));
    }

    [Fact]
    public void SchemaStudioWorkspaceArtifactsAreNotDuplicatedIntoSddRevisionHistory()
    {
        var repository = new WorkspaceArtifactRepository();
        repository.Set(WorkspaceArtifactType.SchemaStudio, "profiles-json", "profiles.json");
        repository.Set(WorkspaceArtifactType.SchemaStudioPackages, "package-json", "packages.json");

        Assert.Empty(repository.GetArtifactRevisions(WorkspaceArtifactType.SchemaStudio));
        Assert.Empty(repository.GetArtifactRevisions(WorkspaceArtifactType.SchemaStudioPackages));
        Assert.Equal(2, repository.GetAllArtifacts().Count());
    }
}
