using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using BirkNext.Integrations;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.SchemaStudio;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Validate &amp; Test in the browser: the minimum example is shown as Schema-valid only after self-validation (with provenance and the
/// schema-only notice), a blocked generation says why, cases are grouped with exact totals, the representative run executes a stratified
/// sample and updates coverage, registration never approves and refuses invalid XML as a positive fixture, and an exact-artifact deep link
/// opens that artifact or fails safely with the caller's context — never another schema.
/// </summary>
public sealed class SchemaStudioValidateAndTestUiTests : BunitContext
{
    private const string Ns = "http://www.w3.org/2001/XMLSchema";
    private const string Main = $"""
        <xs:schema xmlns:xs="{Ns}" xmlns:c="urn:common" targetNamespace="urn:main" xmlns="urn:main" elementFormDefault="qualified">
          <xs:import namespace="urn:common" schemaLocation="common.xsd"/>
          <xs:element name="Report"><xs:complexType><xs:sequence>
            <xs:element name="Header"><xs:complexType><xs:sequence><xs:element name="Code"><xs:simpleType><xs:restriction base="xs:string"><xs:minLength value="4"/></xs:restriction></xs:simpleType></xs:element><xs:element name="Sent" type="xs:date"/></xs:sequence></xs:complexType></xs:element>
            <xs:element name="Party" type="c:PartyType"/>
            <xs:element name="Note" type="xs:string" minOccurs="0"/>
          </xs:sequence></xs:complexType></xs:element>
        </xs:schema>
        """;
    private const string Common = $"""
        <xs:schema xmlns:xs="{Ns}" targetNamespace="urn:common" xmlns="urn:common" elementFormDefault="qualified">
          <xs:complexType name="PartyType"><xs:sequence><xs:element name="Name" type="xs:string"/><xs:element name="Kind"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="P"/><xs:enumeration value="O"/></xs:restriction></xs:simpleType></xs:element></xs:sequence></xs:complexType>
        </xs:schema>
        """;
    private const string Henvisning = $"""<xs:schema xmlns:xs="{Ns}" targetNamespace="urn:henvisning" xmlns="urn:henvisning"><xs:element name="Henvisning" type="xs:string"/></xs:schema>""";

    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly SchemaGeneratedXmlStore _store = new();

    public SchemaStudioValidateAndTestUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_store);
        Services.AddSingleton(_repository);
        Services.AddSingleton<IWorkspaceArtifactRepository>(_repository);
        Services.AddSingleton(Mock.Of<IWorkspaceAutoSaveService>(service => service.SaveNowAsync() == Task.FromResult(true)));
        Services.AddSingleton(new SchemaGenerationProfilesService());
    }

    private SchemaPackage Studio(params (string Name, string Text)[] files)
    {
        var service = new SchemaStudioService();
        var package = service.Import(files.ToDictionary(f => f.Name, f => f.Text), files[0].Name);
        Services.AddSingleton(service);
        return package;
    }

    private static void OpenView(IRenderedComponent<SchemaStudio> page, string view)
    {
        page.FindAll("[data-testid=ss-group]").Single(b => b.TextContent == SchemaStudioNavigation.GroupOf(view).Name).Click();
        page.FindAll("[data-testid=ss-view]").Single(b => b.GetAttribute("data-view") == view).Click();
    }

    private static string RealSchema(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "AIAssisted", "xsd"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "AIAssisted", "xsd", name));
    }

    [Fact]
    public void Minimum_example_is_shown_as_schema_valid_only_after_self_validation_with_provenance_and_notice()
    {
        Studio(("main.xsd", Main), ("common.xsd", Common));
        var page = Render<SchemaStudio>();
        OpenView(page, "Generated Tests");

        page.Find("[data-testid=ss-xml-generate]").TextContent.Should().Be("Generate minimum schema-valid example");
        page.Find("[data-testid=ss-xml-generate]").Click();

        var generation = page.Find("[data-testid=ss-generation]");
        generation.GetAttribute("data-state").Should().Be("SchemaValid");
        page.Find("[data-testid=ss-generation-heading]").TextContent.Should().Be("Schema-valid");
        generation.TextContent.Should().Contain("Not business-approved");
        page.Find("[data-testid=ss-schema-only-notice]").TextContent.Should().StartWith("Schema validity does not prove business validity or runtime acceptance.");
        var xml = page.Find("[data-testid=ss-xml-editor]").GetAttribute("value")!;
        XDocument.Parse(xml).Root!.Elements().Select(e => e.Name.LocalName).Should().Equal("Header", "Party");
        page.Find("[data-testid=ss-generation-provenance]").TextContent.Should().Contain("main.xsd").And.Contain("(entry schema)").And.Contain(SchemaTestDesign.GeneratorVersion);
    }

    [Fact]
    public void A_blocked_generation_says_why_and_shows_no_valid_example()
    {
        Studio(("main.xsd", Main));
        var page = Render<SchemaStudio>();
        OpenView(page, "Generated Tests");
        page.Find("[data-testid=ss-xml-generate]").Click();

        page.Find("[data-testid=ss-generation]").GetAttribute("data-state").Should().Be("Blocked");
        page.Find("[data-testid=ss-generation-heading]").TextContent.Should().Be("Generation blocked");
        page.Find("[data-testid=ss-generation-reasons]").TextContent.Should().Contain("required schema dependency unresolved");
        page.Markup.Should().NotContain("Minimum schema-valid example");
    }

    [Fact]
    public void Cases_are_grouped_with_exact_totals_and_filters_never_change_the_count()
    {
        var package = Studio(("skolenaervaer.xsd", RealSchema("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd")));
        var page = Render<SchemaStudio>();
        OpenView(page, "Generated Tests");

        int Total() => page.FindAll("[data-testid=ss-case-group]").Sum(g => int.Parse(g.GetAttribute("data-count")!));
        Total().Should().Be(package.GeneratedTests.Count);
        page.Find("[data-testid=ss-case-group][data-family=LengthBoundaries] summary").TextContent.Should().MatchRegex(@"Length boundaries · \d+ Valid \d+ · Invalid \d+");

        page.Find("[data-testid=ss-case-expected]").Change("invalid");
        Total().Should().Be(package.GeneratedTests.Count, "filters narrow the listed rows, never the canonical count");
        page.FindAll("[data-testid=ss-case]").Should().OnlyContain(row => row.Children[1].TextContent == "Invalid");
        page.Find("[data-testid=ss-case-search]").Input("Periode/StartDato");
        page.FindAll("[data-testid=ss-case]").Should().NotBeEmpty().And.OnlyContain(row => row.Children[0].TextContent.Contains("StartDato"));
        page.Find("[data-testid=ss-candidates-total]").TextContent.Should().StartWith($"{package.GeneratedTests.Count} total candidates");
    }

    [Fact]
    public void Representative_run_executes_a_stratified_sample_and_updates_coverage()
    {
        var package = Studio(("skolenaervaer.xsd", RealSchema("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd")));
        var page = Render<SchemaStudio>();
        OpenView(page, "Generated Tests");
        page.Find("[data-testid=ss-cov-executed]").TextContent.Should().Be("0");
        page.Find("[data-testid=ss-cov-discovered]").TextContent.Should().NotBe("0");

        page.Find("[data-testid=ss-run-tests]").Click();

        page.Find("[data-testid=ss-cov-executed]").TextContent.Should().Be("100");
        page.Find("[data-testid=ss-cov-failed]").TextContent.Should().Be("0");
        page.Find("[data-testid=ss-generator-defects]").TextContent.Should().Be("0");
        page.Find("[data-testid=ss-validator-mismatches]").TextContent.Should().Be("Not assessed");
        page.FindAll("[data-testid=ss-coverage-row]").Select(r => r.GetAttribute("data-family")).Should().Contain(["Namespace", "RequiredOptional", "LengthBoundaries"]);
        page.FindAll("[data-testid=ss-test-results] tbody tr").Select(r => r.Children[1].TextContent).Distinct().Count().Should().BeGreaterThan(5, "the sample spans rule categories");

        page.Find("[data-testid=ss-run-mode]").Change(nameof(SchemaRunMode.Families));
        page.Find("[data-testid=ss-run-tests]").HasAttribute("disabled").Should().BeTrue("no category selected yet");
        page.FindAll("[data-testid=ss-run-families] input")[0].Change(true);
        page.Find("[data-testid=ss-run-tests]").TextContent.Should().EndWith("in selected categories");
        package.GeneratedTests.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Registration_never_approves_and_refuses_invalid_xml_as_a_positive_fixture()
    {
        Studio(("main.xsd", Main), ("common.xsd", Common));
        var page = Render<SchemaStudio>();
        OpenView(page, "Generated Tests");
        page.Find("[data-testid=ss-xml-generate]").Click();
        await page.Find("[data-testid=ss-xml-save]").ClickAsync();
        _store.Documents.Single().TestData.Should().BeNull("save to workspace is not registration");
        _store.Documents.Single().Generation!.ExpectedResult.Should().Be("Valid");

        await page.Find("[data-testid=ss-testdata-start]").ClickAsync();
        page.FindAll("[data-testid=ss-testdata-approved]").Should().BeEmpty("Schema Studio cannot approve Test Data");
        page.Find("[data-testid=ss-testdata-approval-note]").TextContent.Should().Contain("Approval happens in Test Data");
        page.Find("[data-testid=ss-testdata-alias]").Change("report-minimum");
        await page.Find("[data-testid=ss-testdata-register]").ClickAsync();

        var registration = _store.Documents.Single().TestData!;
        registration.Approved.Should().BeFalse();
        registration.Facts!.ValidationStatus.Should().Be("Valid");
        registration.Facts.GeneratorVersion.Should().Be(SchemaTestDesign.GeneratorVersion);
        page.Find("[data-testid=ss-testdata-registered]").TextContent.Should().Contain("Positive XML fixture").And.Contain("Needs review — approve it in Test Data");

        // Hand-written XML that fails validation: never a positive fixture, but it can be registered as a negative one.
        page.Find("[data-testid=ss-xml-editor]").Change("<Report xmlns=\"urn:main\"><Header><Code>a</Code><Sent>2000-01-01</Sent></Header></Report>");
        await page.Find("[data-testid=ss-xml-save]").ClickAsync();
        var broken = _store.Documents.OrderByDescending(d => d.SavedAt).First();
        broken.Generation.Should().BeNull("edited XML carries no generation provenance");
        await page.FindAll("[data-testid=ss-testdata-start]").First().ClickAsync();
        page.Find("[data-testid=ss-testdata-alias]").Change("report-broken");
        await page.Find("[data-testid=ss-testdata-register]").ClickAsync();
        page.Find(".ss-error").TextContent.Should().Contain("Only XML that passed local validation can be registered as a positive fixture");
        _store.Documents.Single(d => d.Id == broken.Id).TestData.Should().BeNull();

        page.Find("[data-testid=ss-testdata-intent]").Change(SchemaXmlTestDataRegistration.NegativeTest);
        await page.Find("[data-testid=ss-testdata-register]").ClickAsync();
        var negative = _store.Documents.Single(d => d.Id == broken.Id).TestData!;
        negative.Intent.Should().Be(SchemaXmlTestDataRegistration.NegativeTest);
        negative.Facts!.ExpectedResult.Should().Be("Invalid");
        negative.Approved.Should().BeFalse();
    }

    [Fact]
    public void Validate_xml_shows_status_exact_schema_and_structured_issues()
    {
        Studio(("main.xsd", Main), ("common.xsd", Common));
        var page = Render<SchemaStudio>();
        OpenView(page, "Generated Tests");
        page.Find("[data-testid=ss-xml-paste]").Click();
        page.Find("[data-testid=ss-xml-editor]").Change("<Report xmlns=\"urn:main\"><Header><Code>a</Code></Header></Report>");
        page.Find("[data-testid=ss-xml-validate]").Click();

        var result = page.Find("[data-testid=ss-validation-result]");
        result.GetAttribute("data-status").Should().Be("Invalid");
        result.QuerySelector("strong")!.TextContent.Should().Be("Invalid");
        page.FindAll("[data-testid=ss-validation-issues] li").Should().NotBeEmpty();
        page.FindAll("[data-testid=ss-generation]").Should().BeEmpty("validating pasted XML is not generation");
    }

    // ── Exact-artifact deep link ────────────────────────────────────────────────────────────────────────────────────────────

    private (Guid Snapshot, SchemaStudioService Service) SourceSnapshot()
    {
        var snapshotId = Guid.NewGuid();
        var fingerprint = new string('c', 64);
        var files = new[]
        {
            new SourceSchemaFile("schemas/main.xsd", Main, Fingerprint(Main)),
            new SourceSchemaFile("schemas/common.xsd", Common, Fingerprint(Common)),
            new SourceSchemaFile("schemas/henvisning.xsd", Henvisning, Fingerprint(Henvisning)),
        };
        var api = new Mock<ISourceSchemaApiService>();
        api.Setup(client => client.ListSchemaSnapshotIndexAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceSchemaSnapshotIndex([new(snapshotId, "contracts.zip", fingerprint, DateTimeOffset.UtcNow, true, true, 0)], false));
        api.Setup(client => client.SourceSchemasAsync(snapshotId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SourceSchemaPackage(snapshotId, fingerprint, "contracts.zip", DateTimeOffset.UtcNow, true, 0, files));
        var service = new SchemaStudioService();
        Services.AddSingleton(service);
        Services.AddSingleton(api.Object);
        return (snapshotId, service);
    }

    [Fact]
    public void Deep_link_opens_exactly_the_requested_artifact_with_the_caller_context()
    {
        var (snapshot, service) = SourceSnapshot();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/schema-studio?snapshot={snapshot}&contract=schemas%2Fmain.xsd&integration=skole-intake&from=iqr");
        var page = Render<SchemaStudio>();

        page.WaitForAssertion(() => service.Packages.Should().ContainSingle());
        var package = service.Packages.Single();
        package.Provenance!.SelectedContractPath.Should().Be("schemas/main.xsd");
        package.Files.Should().NotContain(f => f.Name.Contains("henvisning"), "only the selected contract and its dependencies");
        page.Find("[data-testid=ss-caller-context]").TextContent.Should().Contain("Opened from Integration Quality Review");
        page.Find("[data-testid=ss-caller-integration]").TextContent.Should().Be("skole-intake");
        page.Find("[data-testid=ss-caller-contract]").TextContent.Should().Contain("schemas/main.xsd");
        page.Find("[data-testid=ss-caller-return]").GetAttribute("href").Should().Be("integration-quality-review?integration=skole-intake");
        page.FindAll("[data-testid=ss-handoff-not-found]").Should().BeEmpty();
        page.Find("[data-testid=ss-caller-dismiss]").Click();
        page.FindAll("[data-testid=ss-caller-context]").Should().BeEmpty();
    }

    [Fact]
    public void Deep_link_to_a_missing_artifact_fails_safely_and_never_opens_another_schema()
    {
        var (snapshot, service) = SourceSnapshot();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/schema-studio?snapshot={snapshot}&contract=schemas%2Fskolenaervaer.xsd&integration=skole-intake&from=iqr");
        var page = Render<SchemaStudio>();

        page.WaitForAssertion(() => page.Find("[data-testid=ss-handoff-not-found]"));
        page.Find("[data-testid=ss-handoff-not-found] strong").TextContent.Should().Be("Requested contract not found in this snapshot.");
        page.Find("[data-testid=ss-handoff-not-found]").TextContent.Should().Contain("No other schema was opened");
        service.Packages.Should().BeEmpty("neither the first nor any other schema is opened instead");
        page.Find("[data-testid=ss-handoff-return]").GetAttribute("href").Should().Be("integration-quality-review?integration=skole-intake");
        page.Find("[data-testid=ss-handoff-choose]").Click();
        page.FindAll("[data-testid=ss-handoff-not-found]").Should().BeEmpty();
    }

    private static string Fingerprint(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
