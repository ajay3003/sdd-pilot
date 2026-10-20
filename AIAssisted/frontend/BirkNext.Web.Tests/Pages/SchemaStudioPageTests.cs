using BirkNext.Web.Pages;
using BirkNext.Integrations;
using BirkNext.Web.Services;
using BirkNext.Web.Services.SchemaStudio;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using Moq;

namespace BirkNext.Web.Tests.Pages;

public sealed class SchemaStudioPageTests : BunitContext
{
    /// <summary>Opens a view through the two-level navigation: its area first, then the view.</summary>
    private static void OpenView(IRenderedComponent<SchemaStudio> page, string view)
    {
        var area = BirkNext.Web.Services.SchemaStudio.SchemaStudioNavigation.GroupOf(view).Name;
        page.FindAll("[data-testid=ss-group]").Single(b => b.TextContent == area).Click();
        page.FindAll("[data-testid=ss-view]").Single(b => b.TextContent == view).Click();
    }

    [Fact]
    public void EmptyStateOffersGenericXsdAndPackageImport()
    {
        Services.AddSingleton(new SchemaStudioService());
        var page = Render<SchemaStudio>();

        Assert.Contains("Schema Studio", page.Markup);
        Assert.Contains("Choose one or more .xsd files", page.Markup);
        Assert.Contains("Choose an XSD ZIP package", page.Markup);
        Assert.Contains("No schemas imported", page.Markup);
        Assert.DoesNotContain("Skolenærvær", page.Markup);
    }

    [Fact]
    public void ExistingSessionPackageShowsOverviewAndDistinctSchemaViews()
    {
        var service = new SchemaStudioService();
        service.Import(new Dictionary<string, string> { ["generic.xsd"] = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"root\" type=\"xs:string\"/></xs:schema>" }, "generic.xsd");
        Services.AddSingleton(service);

        var page = Render<SchemaStudio>();

        Assert.Contains("generic", page.Markup);
        // Five areas at the first level; only the selected area's views at the second (default: Understand → Overview).
        Assert.Equal(["Understand", "Schema", "Inspect", "Design & Compare", "Validate & Test"], page.FindAll("[data-testid=ss-group]").Select(b => b.TextContent).ToArray());
        Assert.Equal(["Overview", "Forretning", "Felt og regler"], page.FindAll("[data-testid=ss-view]").Select(b => b.TextContent).ToArray());
        Assert.Equal("page", page.FindAll("[data-testid=ss-view]").Single(b => b.TextContent == "Overview").GetAttribute("aria-current"));
        // Every former view stays reachable through its area.
        foreach (var view in new[] { "Struktur", "Schema dependencies", "Effective Schema", "Generated Tests" })
        {
            OpenView(page, view);
            Assert.Equal("page", page.FindAll("[data-testid=ss-view]").Single(b => b.TextContent == view).GetAttribute("aria-current"));
        }
    }

    [Fact]
    public void BuiltInProfileCanBeOpenedAndExportedWithoutRenderFailure()
    {
        var schemas = new SchemaStudioService();
        schemas.Import(new Dictionary<string, string> { ["generic.xsd"] = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"root\" type=\"xs:string\"/></xs:schema>" }, "generic.xsd");
        Services.AddSingleton(schemas);
        Services.AddSingleton(new SchemaGenerationProfilesService());
        var page = Render<SchemaStudio>();

        OpenView(page, "Generation Profile");

        Assert.Contains("Generic valid (built-in)", page.Markup);
        Assert.Contains("Export selected profile", page.Markup);
        Assert.Contains("New profile random seed", page.Markup);
    }

    [Fact]
    public void ProfilePickerHidesProfilesScopedToOtherProjects()
    {
        var schemas = new SchemaStudioService();
        var package = schemas.Import(new Dictionary<string, string>
        {
            ["generic.xsd"] = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"root\" type=\"xs:string\"/></xs:schema>"
        }, "generic.xsd");
        var profiles = new SchemaGenerationProfilesService();
        profiles.Create("Other project profile", package.Fingerprint, "import:other", SchemaGenerationProfileKind.GenericValid, 1);
        profiles.Create("Same project profile", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 2);
        Services.AddSingleton(schemas);
        Services.AddSingleton(profiles);

        var page = Render<SchemaStudio>();
        OpenView(page, "Generation Profile");

        Assert.Contains("Same project profile", page.Markup);
        Assert.DoesNotContain("Other project profile", page.Markup);
    }

    [Fact]
    public void SavedProfileCanBeOpenedAndEditedWithoutChangingItsRules()
    {
        var schemas = new SchemaStudioService();
        var package = schemas.Import(new Dictionary<string, string>
        {
            ["generic.xsd"] = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"root\" type=\"xs:string\"/></xs:schema>"
        }, "generic.xsd");
        var profiles = new SchemaGenerationProfilesService();
        var profile = profiles.Create("Original profile", package.Fingerprint, null, SchemaGenerationProfileKind.GenericValid, 17);
        var rule = new SchemaGenerationRule("/root", SchemaGenerationValueMode.Synthetic, MinimumLength: 3);
        profile = profiles.Edit(profile.Id, profile.Name, profile.Kind, profile.Seed, [rule]);
        Services.AddSingleton(schemas);
        Services.AddSingleton(profiles);

        var page = Render<SchemaStudio>();
        OpenView(page, "Generation Profile");
        page.Find("select[aria-label='Generation Profile']").Change(profile.Id.ToString("D"));
        page.FindAll("button").Single(button => button.TextContent.Contains("Load selected profile for editing", StringComparison.Ordinal)).Click();
        page.FindAll("label").Single(label => label.TextContent.Contains("Profile name", StringComparison.Ordinal)).QuerySelector("input")!.Change("Edited profile");
        page.FindAll("label").Single(label => label.TextContent.Contains("Random seed", StringComparison.Ordinal)).QuerySelector("input")!.Change("29");
        page.FindAll("button").Single(button => button.TextContent.Contains("Update selected profile", StringComparison.Ordinal)).Click();

        var edited = Assert.Single(profiles.Profiles);
        Assert.Equal("Edited profile", edited.Name);
        Assert.Equal(29, edited.Seed);
        Assert.Equal([rule], edited.Rules);
        Assert.Contains("Profile updated", page.Markup);
    }

    [Fact]
    public void SourceAnalysisSnapshotOpensNestedIncludedSchemaWithImmutableProvenance()
    {
        const string xs = "http://www.w3.org/2001/XMLSchema";
        const string main = $"<xs:schema xmlns:xs=\"{xs}\" targetNamespace=\"urn:source\" xmlns=\"urn:source\" elementFormDefault=\"qualified\"><xs:include schemaLocation=\"types/common.xsd\"/><xs:element name=\"Root\" type=\"RootType\"/><xs:complexType name=\"RootType\"><xs:sequence><xs:element name=\"Code\" type=\"CommonCode\"/></xs:sequence></xs:complexType></xs:schema>";
        const string common = $"<xs:schema xmlns:xs=\"{xs}\" targetNamespace=\"urn:source\" xmlns=\"urn:source\"><xs:simpleType name=\"CommonCode\"><xs:restriction base=\"xs:string\"><xs:minLength value=\"2\"/></xs:restriction></xs:simpleType></xs:schema>";
        const string unrelated = $"<xs:schema xmlns:xs=\"{xs}\" targetNamespace=\"urn:source\" xmlns=\"urn:source\"><xs:simpleType name=\"CommonCode\"><xs:restriction base=\"xs:string\"><xs:minLength value=\"7\"/></xs:restriction></xs:simpleType></xs:schema>";
        var snapshotId = Guid.NewGuid();
        const string snapshotFingerprint = "snapshot-fingerprint-immutable";
        var snapshot = new IqrSourceSnapshot
        {
            Id = snapshotId,
            IntegrationId = "source-analysis",
            Archive = new SourceArchive("contracts.zip", snapshotFingerprint, 3),
            AnalyzedAt = DateTimeOffset.UtcNow,
        };
        var files = new[]
        {
            new SourceSchemaFile("schemas/main.xsd", main, Fingerprint(main)),
            new SourceSchemaFile("schemas/other/common.xsd", unrelated, Fingerprint(unrelated)),
            new SourceSchemaFile("schemas/types/common.xsd", common, Fingerprint(common)),
        };
        var api = new Mock<ISourceSchemaApiService>();
        api.Setup(client => client.ListSchemaSnapshotIndexAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceSchemaSnapshotIndex([new(snapshotId, "contracts.zip", snapshotFingerprint, snapshot.AnalyzedAt, true, true, 0)], false));
        api.Setup(client => client.SourceSchemasAsync(snapshotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceSchemaPackage(snapshotId, snapshotFingerprint, "contracts.zip", snapshot.AnalyzedAt, true, 0, files));
        var schemas = new SchemaStudioService();
        Services.AddSingleton(schemas);
        Services.AddSingleton(api.Object);

        var page = Render<SchemaStudio>();
        page.Find("select[aria-label='Source Analysis snapshot']").Change(snapshotId.ToString("D"));
        page.FindAll("button").Single(button => button.TextContent.Contains("Load XSDs from snapshot", StringComparison.Ordinal)).Click();
        page.FindAll("button").Single(button => button.TextContent.Contains("Open contract in Schema Studio", StringComparison.Ordinal)).Click();

        var package = Assert.Single(schemas.Packages);
        Assert.True(package.Validation == "Valid", string.Join("\n", package.Diagnostics));
        Assert.Equal(snapshotId, package.Provenance!.SourceSnapshotId);
        Assert.Equal(snapshotFingerprint, package.Provenance.SourceSnapshotFingerprint);
        Assert.Equal("schemas/main.xsd", package.Provenance.SelectedContractPath);
        Assert.DoesNotContain(package.Files, file => file.Name == "schemas/other/common.xsd");
        Assert.Equal("schemas/types/common.xsd", package.Files.Single(file => file.Name == "schemas/main.xsd").Links.Single().ResolvedFile);
        var code = Assert.Single(Assert.Single(package.EffectiveSchema).Children);
        Assert.Equal("CommonCode", code.Type);
        Assert.Equal("schemas/types/common.xsd", code.TypeDeclaredIn);
        Assert.Contains("schemas/main.xsd", page.Markup);
    }

    [Fact]
    public void SourceAnalysisArtifactsFromAnotherSnapshotAreRejected()
    {
        var snapshotId = Guid.NewGuid();
        var snapshot = new IqrSourceSnapshot
        {
            Id = snapshotId,
            IntegrationId = "source-analysis",
            Archive = new SourceArchive("contracts.zip", "snapshot-A", 3),
            AnalyzedAt = DateTimeOffset.UtcNow,
        };
        var api = new Mock<ISourceSchemaApiService>();
        api.Setup(client => client.ListSchemaSnapshotIndexAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceSchemaSnapshotIndex([new(snapshotId, "contracts.zip", "snapshot-A", snapshot.AnalyzedAt, true, true, 0)], false));
        api.Setup(client => client.SourceSchemasAsync(snapshotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceSchemaPackage(snapshotId, "snapshot-B", "contracts.zip", snapshot.AnalyzedAt, true, 0, []));
        var schemas = new SchemaStudioService();
        Services.AddSingleton(schemas);
        Services.AddSingleton(api.Object);

        var page = Render<SchemaStudio>();
        page.Find("select[aria-label='Source Analysis snapshot']").Change(snapshotId.ToString("D"));
        page.FindAll("button").Single(button => button.TextContent.Contains("Load XSDs from snapshot", StringComparison.Ordinal)).Click();

        Assert.Contains("do not match the selected immutable Source Analysis snapshot", page.Markup);
        Assert.Empty(schemas.Packages);
    }

    private static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public void CreateWizardAndStructuredDesignerSaveASeparateVersion()
    {
        var service = new SchemaStudioService();
        Services.AddSingleton(service);
        var page = Render<SchemaStudio>();

        page.Find("button.ss-action").Click();
        page.FindAll("button").Single(b => b.TextContent.Contains("Create schema and open Designer", StringComparison.Ordinal)).Click();
        Assert.Contains("Schema Designer", page.Markup);
        page.Find("input[aria-label='Child element name']").Change("RecordId");
        page.FindAll("button").Single(b => b.TextContent.Contains("Add child element", StringComparison.Ordinal)).Click();
        page.FindAll("button").Single(b => b.TextContent.Contains("Validate and save new version", StringComparison.Ordinal)).Click();

        Assert.Equal(2, service.Packages.Count);
        Assert.Contains(service.Packages[0].Files[0].Declarations.SelectMany(d => d.Children), e => e.Name == "RecordId");
        Assert.All(service.Packages, p => Assert.Equal("Valid", p.Validation));
    }

    [Fact]
    public void EffectiveSchemaViewShowsInheritedAndCrossFileProvenance()
    {
        const string xs = "http://www.w3.org/2001/XMLSchema";
        var service = new SchemaStudioService();
        service.Import(new Dictionary<string, string>
        {
            ["main.xsd"] = $"<xs:schema xmlns:xs=\"{xs}\" xmlns:m=\"urn:msg\" targetNamespace=\"urn:main\" xmlns=\"urn:main\"><xs:import namespace=\"urn:msg\" schemaLocation=\"message.xsd\"/><xs:element name=\"Report\" type=\"ReportType\"/><xs:complexType name=\"ReportType\"><xs:complexContent><xs:extension base=\"m:MessageType\"><xs:sequence><xs:element name=\"Period\" type=\"xs:string\"/></xs:sequence></xs:extension></xs:complexContent></xs:complexType></xs:schema>",
            ["message.xsd"] = $"<xs:schema xmlns:xs=\"{xs}\" targetNamespace=\"urn:msg\" xmlns=\"urn:msg\"><xs:complexType name=\"MessageType\"><xs:sequence><xs:element name=\"Id\" type=\"xs:string\"/></xs:sequence></xs:complexType></xs:schema>"
        }, "generic linked contract");
        Services.AddSingleton(service);
        var page = Render<SchemaStudio>();

        OpenView(page, "Effective Schema");

        Assert.Contains("Id", page.Markup);
        Assert.Contains("inherited from MessageType", page.Markup);
        Assert.Contains("message.xsd", page.Markup);
        Assert.Contains("Period", page.Markup);
    }

    [Fact]
    public void DesignerAddsChildInsideImportedBaseExtensionAndSavesValidVersion()
    {
        const string xs = "http://www.w3.org/2001/XMLSchema";
        var service = new SchemaStudioService();
        service.Import(new Dictionary<string, string>
        {
            ["main.xsd"] = $"<xs:schema xmlns:xs=\"{xs}\" xmlns:c=\"urn:common\" xmlns=\"urn:main\" targetNamespace=\"urn:main\"><xs:import namespace=\"urn:common\" schemaLocation=\"common.xsd\"/><xs:element name=\"Root\" type=\"LocalType\"/><xs:complexType name=\"LocalType\"><xs:complexContent><xs:extension base=\"c:BaseType\"><xs:sequence><xs:element name=\"Local\" type=\"xs:string\"/></xs:sequence></xs:extension></xs:complexContent></xs:complexType></xs:schema>",
            ["common.xsd"] = $"<xs:schema xmlns:xs=\"{xs}\" xmlns=\"urn:common\" targetNamespace=\"urn:common\"><xs:complexType name=\"BaseType\"><xs:sequence><xs:element name=\"Base\" type=\"xs:string\"/></xs:sequence></xs:complexType></xs:schema>"
        }, "linked");
        Services.AddSingleton(service);
        var page = Render<SchemaStudio>();

        OpenView(page, "Designer");
        page.FindAll("button").Single(b => b.TextContent.Contains("Add child element", StringComparison.Ordinal)).Click();
        page.FindAll("button").Single(b => b.TextContent.Contains("Validate and save new version", StringComparison.Ordinal)).Click();

        Assert.Equal(2, service.Packages.Count);
        Assert.Equal("Valid", service.Packages[0].Validation);
        Assert.Contains(Assert.Single(service.Packages[0].EffectiveSchema).Children, member => member.Name == "NewElement");
    }

    [Fact]
    public void DesignerAddsBoundedRepeatedEnumElementAndPreservesExistingXsdPrefix()
    {
        const string xs = "http://www.w3.org/2001/XMLSchema";
        var service = new SchemaStudioService();
        service.Import(new Dictionary<string, string>
        {
            ["prefixed.xsd"] = $"<xsd:schema xmlns:xsd=\"{xs}\" targetNamespace=\"urn:designer\" xmlns=\"urn:designer\" elementFormDefault=\"qualified\"><xsd:element name=\"Record\"><xsd:complexType><xsd:sequence /></xsd:complexType></xsd:element></xsd:schema>"
        }, "prefixed");
        Services.AddSingleton(service);
        var page = Render<SchemaStudio>();

        OpenView(page, "Designer");
        page.Find("input[aria-label='Child element name']").Change("Status");
        page.Find("select[aria-label='Minimum occurrences']").Change("1");
        page.Find("select[aria-label='Maximum occurrences']").Change("2");
        page.Find("input[aria-label='Enumeration values']").Change("Ready, Done");
        page.FindAll("button").Single(button => button.TextContent.Contains("Add child element", StringComparison.Ordinal)).Click();
        page.FindAll("button").Single(button => button.TextContent.Contains("Preview and validate draft", StringComparison.Ordinal)).Click();

        Assert.Contains("Draft compilation: Valid", page.Markup);
        page.FindAll("button").Single(button => button.TextContent.Contains("Validate and save new version", StringComparison.Ordinal)).Click();
        var saved = service.Packages.OrderByDescending(package => package.ImportedAt).First();
        var member = Assert.Single(Assert.Single(saved.EffectiveSchema).Children);
        Assert.Equal("1", member.MinOccurs);
        Assert.Equal("2", member.MaxOccurs);
        Assert.Equal(new[] { "Ready", "Done" }, member.Facets.Where(facet => facet.Kind == "enumeration").Select(facet => facet.Value));
        Assert.Equal("Valid", saved.Validation);
    }

    [Fact]
    public void DesignerWorkingDraftCanBeSavedAndReopenedFromWorkspaceArtifact()
    {
        var schemas = new SchemaStudioService();
        var package = schemas.Import(new Dictionary<string, string>
        {
            ["draft.xsd"] = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"Root\" type=\"xs:string\"/></xs:schema>"
        }, "draft");
        var repository = new WorkspaceArtifactRepository();
        var autoSave = Mock.Of<IWorkspaceAutoSaveService>(service => service.SaveNowAsync() == Task.FromResult(true));
        var drafts = new SchemaStudioDraftStore();
        Services.AddSingleton(schemas);
        Services.AddSingleton(repository);
        Services.AddSingleton<IWorkspaceArtifactRepository>(repository);
        Services.AddSingleton(autoSave);
        Services.AddSingleton(drafts);

        var firstPage = Render<SchemaStudio>();
        OpenView(firstPage, "Designer");
        var input = (AngleSharp.Html.Dom.IHtmlTextAreaElement)firstPage.Find("textarea[aria-label='Edit XSD source']");
        const string edited = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"ChangedRoot\" type=\"xs:string\"/></xs:schema>";
        input.Change(edited);
        firstPage.FindAll("button").Single(button => button.TextContent.Contains("Save working draft", StringComparison.Ordinal)).Click();
        Assert.Contains("Saved draft for draft.xsd", firstPage.Markup);
        Assert.Equal(edited, drafts.Find(package, "draft.xsd")?.Text);

        drafts.RestoreJson(null);
        var reopenedPage = Render<SchemaStudio>();
        OpenView(reopenedPage, "Designer");

        Assert.Equal(edited, drafts.Find(package, "draft.xsd")?.Text);
        Assert.Contains("draft", repository.Get(WorkspaceArtifactType.SchemaStudioDrafts)!.Text);
    }

    [Fact]
    public async Task GeneratedXmlRequiresExplicitSaveAndReopensFromWorkspaceArtifact()
    {
        var schemas = new SchemaStudioService();
        schemas.Import(new Dictionary<string, string>
        {
            ["saved.xsd"] = "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"Root\" type=\"xs:string\"/></xs:schema>"
        }, "saved");
        var repository = new WorkspaceArtifactRepository();
        var store = new SchemaGeneratedXmlStore();
        var autoSave = Mock.Of<IWorkspaceAutoSaveService>(service => service.SaveNowAsync() == Task.FromResult(true));
        Services.AddSingleton(schemas);
        Services.AddSingleton(store);
        Services.AddSingleton(repository);
        Services.AddSingleton<IWorkspaceArtifactRepository>(repository);
        Services.AddSingleton(autoSave);

        var firstPage = Render<SchemaStudio>();
        OpenView(firstPage, "Generated Tests");
        var xmlInput = firstPage.Find("textarea[aria-label='XML instance to validate']");
        xmlInput.Change("<Root>synthetic value</Root>");
        await firstPage.FindAll("button").Single(button => button.TextContent == "Save XML artifact").ClickAsync();
        Assert.Contains("XML artifact saved", firstPage.Markup);
        Assert.Equal(1, store.Documents.Count);
        Assert.Equal("<Root>synthetic value</Root>", Assert.Single(store.Documents).Xml);
        Assert.Contains("synthetic value", repository.Get(WorkspaceArtifactType.SchemaStudioGeneratedXml)!.Text);

        store.RestoreJson(null);
        var reopenedPage = Render<SchemaStudio>();
        Assert.Equal("<Root>synthetic value</Root>", Assert.Single(store.Documents).Xml);
        OpenView(reopenedPage, "Generated Tests");
        await reopenedPage.FindAll("button").Single(button => button.TextContent == "Reopen XML").ClickAsync();

        Assert.Contains("Reopened XML artifact", reopenedPage.Markup);
        await reopenedPage.FindAll("button").Single(button => button.TextContent == "Validate XML").ClickAsync();
        Assert.Contains("Valid against", reopenedPage.Markup);
        Assert.Contains("Validate it again against the active schema", reopenedPage.Markup);
    }
}
