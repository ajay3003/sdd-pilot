using System.Security.Cryptography;
using System.Text;
using BirkNext.Integrations;
using BirkNext.Schemas;
using BirkNext.TestData;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using BirkNext.Web.Services.SchemaStudio;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Test Data → XML fixtures: identities come from Session test identities (defaults preselected, explicit wins), the XSD is resolved
/// deterministically from the Test Pack's evidence (one preselected, several need a choice, none is never guessed), generation and validation
/// are Schema Studio's own logic over the real Skolenærvær XSD, and only a schema-valid fixture can be saved.
/// </summary>
public sealed class XmlFixturePanelTests : BunitContext
{
    private const string Contract = "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn";
    private const string XsdFile = "xsd/" + Contract + ".xsd";
    private const string ChildPath = "Klient.Identifikator.Fodselsnummer";
    private static readonly Guid Snapshot = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static string RealSchema()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "AIAssisted", "xsd"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("AIAssisted/xsd"), "AIAssisted", "xsd", Contract + ".xsd"));
    }

    private static IntegrationTemplateView Context(params IntegrationTemplateSchemaCandidate[] xsds) =>
        new("skolenaervaer-report-intake", "Report intake", "", IntegrationKind.HttpApi, "skolenaervaer", "Skolenærvær Testing")
        {
            SchemaName = Contract,
            SchemaCandidates = xsds,
            FixtureBindings = [new("Child", "child", ChildPath, "The child the report is about."), new("Reporter", "reporter", null, "Submits the report as the Altinn actor; not part of the XML.")],
        };

    private static IntegrationTemplateSchemaCandidate Xsd(Guid? snapshot = null, string archive = "skolenaervaer.zip") => new(snapshot ?? Snapshot, archive, Contract, XsdFile);

    private static TestDataAssetSummary Identity(string alias, string capability, bool isDefault = false) => new()
    {
        Id = $"altinn-local:{capability}:{alias}", Alias = alias, Label = alias, Kind = TestDataKind.EntityReference, Capabilities = ["person", capability],
        Approval = TestDataApproval.Approved, Sensitivity = TestDataSensitivity.Sensitive, IsDefault = isDefault,
    };

    private static TestDataAssetSummary Fixture(TestDataApproval approval, TestDataSchemaStatus? status, Guid? snapshot = null) => new()
    {
        Id = "schema-studio:fixture", Alias = "fixture", Kind = TestDataKind.Structured, MediaType = "application/xml", Approval = approval,
        Schema = new TestDataSchemaReference(XsdFile, snapshot ?? Snapshot, null, "Skolenaervaer", XsdFile),
        SchemaCheck = status is { } s ? new TestDataSchemaCheck(s, XsdFile, "", []) : null,
    };

    private static readonly TestDataAssetSummary[] Defaults =
        [Identity("skolenaervaer-reporter-default", "reporter", true), Identity("skolenaervaer-child-default", "child", true), Identity("child-b", "child")];

    // ---- presentation ----

    [Fact]
    public void An_explicit_identity_wins_then_the_single_default_then_the_only_candidate_never_the_first_of_several()
    {
        var children = XmlFixturePresentation.Candidates(Defaults, "child");
        XmlFixturePresentation.Chosen(children, null)!.Alias.Should().Be("skolenaervaer-child-default");
        XmlFixturePresentation.Chosen(children, "child-b")!.Alias.Should().Be("child-b");
        XmlFixturePresentation.Chosen([Identity("a", "child"), Identity("b", "child")], null).Should().BeNull("several without a default need a choice");
        XmlFixturePresentation.Chosen([Identity("a", "child")], null)!.Alias.Should().Be("a");
    }

    [Fact]
    public void One_xsd_is_preselected_several_need_a_choice_none_is_none_and_duplicates_count_once()
    {
        XmlFixturePresentation.Preselected(Context(Xsd(), Xsd()))!.File.Should().Be(XsdFile);
        XmlFixturePresentation.Preselected(Context(Xsd(), Xsd(Guid.NewGuid(), "other.zip"))).Should().BeNull();
        XmlFixturePresentation.Preselected(Context()).Should().BeNull();
        XmlFixturePresentation.Contexts([Context(), Context() with { Key = "no-roles", FixtureBindings = [] }]).Select(c => c.Key).Should().Equal("skolenaervaer-report-intake");
    }

    [Fact]
    public void Readiness_is_ready_only_for_an_approved_fixture_validated_against_the_current_snapshot()
    {
        XmlFixturePresentation.Readiness([], Snapshot).State.Should().Be(TestDataXmlReadinessState.Missing);
        XmlFixturePresentation.Readiness([Fixture(TestDataApproval.Approved, TestDataSchemaStatus.Validated)], Snapshot).State.Should().Be(TestDataXmlReadinessState.Ready);
        XmlFixturePresentation.Readiness([Fixture(TestDataApproval.Approved, TestDataSchemaStatus.Validated, Guid.NewGuid())], Snapshot).State.Should().Be(TestDataXmlReadinessState.Outdated);
        XmlFixturePresentation.Readiness([Fixture(TestDataApproval.Approved, TestDataSchemaStatus.Invalid)], Snapshot).State.Should().Be(TestDataXmlReadinessState.Invalid);
        XmlFixturePresentation.Readiness([Fixture(TestDataApproval.Unapproved, TestDataSchemaStatus.Validated)], Snapshot).State.Should().Be(TestDataXmlReadinessState.NotApproved);
        XmlFixturePresentation.Readiness([Fixture(TestDataApproval.Approved, null)], Snapshot).State.Should().Be(TestDataXmlReadinessState.NotValidated);
    }

    [Fact]
    public void Metadata_names_roles_by_alias_and_aliases_are_suggested_from_the_name()
    {
        XmlFixturePresentation.BindingsText(Context(), new Dictionary<string, string?> { ["Child"] = "skolenaervaer-child-default", ["Reporter"] = "skolenaervaer-reporter-default" })
            .Should().Be("Child: skolenaervaer-child-default; Reporter (not in the XML): skolenaervaer-reporter-default");
        XmlFixturePresentation.SuggestedAlias("Skolenærvær valid report").Should().Be("skolenaervaer-valid-report");
        XmlFixturePresentation.SuggestedAlias("  ").Should().Be("xml-fixture");
    }

    // ---- Schema Studio pipeline over the real XSD ----

    [Fact]
    public void The_real_xsd_generates_a_minimum_message_the_child_element_is_added_in_schema_position_and_validation_tracks_edits()
    {
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { [XsdFile] = RealSchema() }, Contract + ".xsd");
        var generated = SchemaStudioService.GenerateMinimumValid(package, XsdFile);
        generated.State.Should().Be(SchemaGenerationState.SchemaValid);

        var withChild = SchemaStudioService.WithElements(package, generated.Xml!, [ChildPath]);
        withChild.Should().Contain("<Klient>").And.Contain("<Identifikator>").And.Contain("<Fodselsnummer>");
        var bound = System.Text.RegularExpressions.Regex.Replace(withChild, "<Fodselsnummer>[^<]*</Fodselsnummer>", "<Fodselsnummer>04061076692</Fodselsnummer>");
        SchemaStudioService.Validate(package, XsdFile, bound).Status.Should().Be(XmlSchemaValidationStatus.Valid);
        SchemaStudioService.Validate(package, XsdFile, bound.Replace("04061076692", "123")).Status.Should().Be(XmlSchemaValidationStatus.Invalid, "the fodselsnummer pattern is enforced");
    }

    // ---- panel ----

    private sealed class Backend
    {
        public List<TestDataXmlBindRequest> Binds { get; } = [];
        public int SchemaReads { get; set; }
    }

    private Backend Setup(params IntegrationTemplateView[] templates)
    {
        var backend = new Backend();
        var journeys = new Mock<IIntegrationJourneysApiService>();
        journeys.Setup(j => j.IntegrationTemplatesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActiveEventApiResult<IReadOnlyList<IntegrationTemplateView>>(templates, null));
        Services.AddSingleton(journeys.Object);
        var text = RealSchema();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var schemas = new Mock<ISourceSchemaApiService>();
        schemas.Setup(s => s.SourceSchemasAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, CancellationToken _) =>
        {
            backend.SchemaReads++;
            return new SourceSchemaPackage(id, "fp", "skolenaervaer.zip", DateTimeOffset.UnixEpoch, true, 0, [new SourceSchemaFile(XsdFile, text, fingerprint)]);
        });
        Services.AddSingleton(schemas.Object);
        var testData = new Mock<ITestDataApiService>();
        testData.Setup(t => t.BindXmlFixtureAsync(It.IsAny<TestDataXmlBindRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((TestDataXmlBindRequest request, CancellationToken _) =>
        {
            backend.Binds.Add(request);
            return (System.Text.RegularExpressions.Regex.Replace(request.Xml, "<Fodselsnummer>[^<]*</Fodselsnummer>", "<Fodselsnummer>04061076692</Fodselsnummer>"), []);
        });
        Services.AddSingleton(testData.Object);
        return backend;
    }

    [Fact]
    public void The_summary_shows_the_default_identities_contract_and_missing_fixture_and_the_reporter_stays_out_of_the_xml()
    {
        Setup(Context(Xsd()));
        var panel = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, Defaults));

        panel.WaitForAssertion(() => panel.Find("[data-testid=xf-contract]").TextContent.Should().Be(Contract));
        panel.Find("[data-testid=xf-role][data-role=Child]").TextContent.Should().Contain("skolenaervaer-child-default");
        panel.Find("[data-testid=xf-role][data-role=Reporter]").TextContent.Should().Contain("skolenaervaer-reporter-default").And.Contain("not part of the XML");
        panel.Find("[data-testid=xf-readiness]").GetAttribute("data-state").Should().Be(nameof(TestDataXmlReadinessState.Missing));
        panel.Markup.Should().NotContain("04061076692").And.NotContain("05859497324");
    }

    [Fact]
    public void Generate_binds_the_child_by_alias_validates_locally_and_saving_needs_a_valid_xml()
    {
        var backend = Setup(Context(Xsd()));
        var panel = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, Defaults));
        panel.WaitForElement("[data-testid=xf-create]").Click();
        panel.Find("[data-testid=xf-xsd-chosen]").TextContent.Should().Contain(XsdFile);

        panel.Find("[data-testid=xf-generate]").Click();
        panel.WaitForElement("[data-testid=xf-xml]");
        panel.Find("[data-testid=xf-alias]").GetAttribute("value").Should().Be("skolenaervaer-valid-report");
        backend.Binds.Should().ContainSingle().Which.Bindings.Should().Equal(new TestDataXmlBinding("skolenaervaer-child-default", ChildPath));
        backend.Binds[0].Xml.Should().Contain("<Fodselsnummer>", "the optional child element is materialized before binding");
        panel.Find("[data-testid=xf-save-button]").HasAttribute("disabled").Should().BeTrue("not validated yet");

        panel.Find("[data-testid=xf-validate]").Click();
        panel.Find("[data-testid=xf-validation]").GetAttribute("data-state").Should().Be("Valid");
        panel.Find("[data-testid=xf-disclaimer]").TextContent.Should().Be("Valid against XSD. Business rules and runtime behavior are not verified.");
        panel.Find("[data-testid=xf-save-button]").HasAttribute("disabled").Should().BeFalse();

        var xml = panel.Find("[data-testid=xf-xml]").GetAttribute("value")!;
        panel.Find("[data-testid=xf-xml]").Input(xml.Replace("04061076692", "123"));
        panel.Find("[data-testid=xf-validation]").GetAttribute("data-state").Should().Be("Needs validation");
        panel.Find("[data-testid=xf-save-button]").HasAttribute("disabled").Should().BeTrue();
        panel.Find("[data-testid=xf-validate]").Click();
        panel.Find("[data-testid=xf-validation]").GetAttribute("data-state").Should().Be("Invalid");
        panel.FindAll("[data-testid=xf-issues] li").Should().NotBeEmpty();
    }

    [Fact]
    public void No_xsd_names_the_gap_and_links_schema_studio_and_several_xsds_need_a_choice_before_generating()
    {
        Setup(Context());
        var none = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, Defaults));
        none.WaitForElement("[data-testid=xf-create]").Click();
        none.Find("[data-testid=xf-no-xsd]").TextContent.Should().Contain("No matching XSD found");
        none.Find("[data-testid=xf-open-studio]").GetAttribute("href").Should().Be("schema-studio");
        none.Find("[data-testid=xf-generate]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Several_xsds_need_an_explicit_choice()
    {
        Setup(Context(Xsd(), Xsd(Guid.NewGuid(), "other.zip")));
        var panel = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, Defaults));
        panel.WaitForElement("[data-testid=xf-create]").Click();
        panel.Find("[data-testid=xf-xsd-select]");
        panel.Find("[data-testid=xf-generate]").HasAttribute("disabled").Should().BeTrue("never the newest by default");
        panel.Find("[data-testid=xf-xsd-select]").Change("1");
        panel.Find("[data-testid=xf-generate]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void A_missing_child_blocks_generation_without_reading_any_schema()
    {
        var backend = Setup(Context(Xsd()));
        var panel = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, [Identity("skolenaervaer-reporter-default", "reporter", true)]));
        panel.WaitForElement("[data-testid=xf-create]").Click();
        panel.Find("[data-testid=xf-generate]").Click();
        panel.Find("[data-testid=xf-error]").TextContent.Should().Contain("Choose a Child in Session test identities");
        backend.SchemaReads.Should().Be(0);
        backend.Binds.Should().BeEmpty();
    }
}
