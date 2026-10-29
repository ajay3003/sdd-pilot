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

    private Backend Setup(params IntegrationTemplateView[] templates) => Setup(null, templates);

    /// <summary><paramref name="schemaFor"/>: a different XSD text per snapshot (e.g. a contract version without the child's field).</summary>
    private Backend Setup(Func<Guid, string>? schemaFor, params IntegrationTemplateView[] templates)
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
            var own = schemaFor?.Invoke(id) ?? text;
            var ownFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(own))).ToLowerInvariant();
            return new SourceSchemaPackage(id, "fp", "skolenaervaer.zip", DateTimeOffset.UnixEpoch, true, 0, [new SourceSchemaFile(XsdFile, own, ownFingerprint)]);
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
        panel.Find("[data-testid=xf-disclaimer]").TextContent.Should().Be("Valid against XSD. Business rules not verified. Runtime behavior not verified.");
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
        panel.Find("[data-testid=xf-snapshot-state]").GetAttribute("data-state").Should().Be("NeedsDecision", "two snapshots declare the contract and nothing decides");
        panel.Find("[data-testid=xf-snapshot-decision]").TextContent.Should().Contain("2 compatible source snapshots found — review required").And.Contain("Nothing is chosen by date");
        panel.Find("[data-testid=xf-generate]").HasAttribute("disabled").Should().BeTrue("never the newest by default");
        panel.Find("[data-testid=xf-snapshot-select]").Change(Snapshot.ToString());
        panel.WaitForAssertion(() => panel.Find("[data-testid=xf-snapshot-state]").GetAttribute("data-state").Should().Be("Explicit"));
        panel.Find("[data-testid=xf-xsd-chosen]").TextContent.Should().Contain("skolenaervaer.zip");
        panel.Find("[data-testid=xf-generate]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void A_missing_child_blocks_generation_and_nothing_is_bound()
    {
        var backend = Setup(Context(Xsd()));
        var panel = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, [Identity("skolenaervaer-reporter-default", "reporter", true)]));
        panel.WaitForElement("[data-testid=xf-create]").Click();
        panel.WaitForElement("[data-testid=xf-mapping]");
        panel.Find("[data-testid=xf-map-no-assets]").TextContent.Should().Be("No compatible Test Data assets found.", "the reporter is never offered for the child's field");
        panel.Find("[data-testid=xf-generate]").Click();
        panel.Find("[data-testid=xf-error]").TextContent.Should().Contain("Choose a Test Data asset for Klient → Identifikator → Fodselsnummer");
        backend.SchemaReads.Should().BeLessThanOrEqualTo(1, "only the XSD's fields were loaded; nothing was generated");
        backend.Binds.Should().BeEmpty();
    }

    // ---- XML value mappings ----

    private static readonly TestDataAssetSummary XmlFixtureAsset = new()
    {
        Id = "schema-studio:report", Alias = "skolenaervaer-valid-report", Label = "Valid report", Kind = TestDataKind.Structured, MediaType = "application/xml",
        Approval = TestDataApproval.Approved, Capabilities = ["child"],
    };
    private static readonly TestDataAssetSummary JsonPayload = new()
    {
        Id = "configured:json", Alias = "report-json", Label = "Report JSON", Kind = TestDataKind.Message, MediaType = "application/json",
        Approval = TestDataApproval.Approved, Capabilities = ["child"],
    };

    private static TestDataAssetSummary[] ManyIdentities =>
    [
        Identity("reporter-01", "reporter", true), Identity("reporter-02", "reporter"), Identity("reporter-03", "reporter"),
        Identity("child-01", "child", true), Identity("child-02", "child"), Identity("child-03", "child"), XmlFixtureAsset, JsonPayload,
    ];

    [Fact]
    public void The_child_field_is_a_suggested_mapping_with_its_full_path_and_only_compatible_scalar_child_assets()
    {
        Setup(Context(Xsd()));
        var panel = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, ManyIdentities));
        panel.WaitForElement("[data-testid=xf-create]").Click();
        var row = panel.WaitForElement("[data-testid=xf-mapping]");
        row.GetAttribute("data-path").Should().Be(ChildPath);
        row.GetAttribute("data-suggested").Should().Be("true");
        panel.Find("[data-testid=xf-map-suggested]").TextContent.Should().Contain("Skolenærvær Testing test context (Child)");
        panel.Find("[data-testid=xf-map-technical]").TextContent.Should().Be("/Skolenaervaer/Klient/Identifikator/Fodselsnummer");
        panel.Find("[data-testid=xf-map-parameter] option[selected]").TextContent.Should().Be("Klient → Identifikator → Fodselsnummer (FodselsnummerType)", "full path plus the XSD data type");
        var values = panel.FindAll("[data-testid=xf-map-asset] option").Select(o => o.GetAttribute("value")).Where(v => v is { Length: > 0 }).ToList();
        values.Should().Equal(["child-01", "child-02", "child-03"], "only approved child-role scalar assets: no reporter, XML fixture or JSON payload");
        panel.Find("[data-testid=xf-map-asset] option[selected]").TextContent.Should().Be("child-01 — child-01 · Approved · Shared · default");

        // Reporter: a submission identity, listed with id/status/scope, never mapped into the XML.
        var reporters = panel.FindAll("[data-testid=xf-identity][data-role=Reporter] option").Select(o => o.TextContent).ToList();
        reporters.Should().Equal("reporter-01 — reporter-01 · Approved · Shared · default", "reporter-02 — reporter-02 · Approved · Shared", "reporter-03 — reporter-03 · Approved · Shared");
        panel.Find("[data-testid=xf-identity-row][data-role=Reporter]").GetAttribute("data-in-xml").Should().Be("false");
        panel.Find("[data-testid=xf-identity-row][data-role=Reporter] [data-testid=xf-identity-help]").TextContent.Should().Contain("Not mapped into the XML payload");
    }

    [Fact]
    public void Mappings_are_applied_before_binding_explicit_values_win_and_the_reporter_is_never_inserted()
    {
        var backend = Setup(Context(Xsd()));
        var panel = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, ManyIdentities));
        panel.WaitForElement("[data-testid=xf-create]").Click();
        panel.WaitForElement("[data-testid=xf-mapping]");
        panel.Find("[data-testid=xf-map-asset]").Change("child-02");
        panel.Find("[data-testid=xf-map-add]").Click();
        var added = panel.FindAll("[data-testid=xf-mapping]")[1];
        added.QuerySelector("[data-testid=xf-map-parameter]")!.Change("Meldingshode.Id");
        panel.FindAll("[data-testid=xf-mapping]")[1].QuerySelector("[data-testid=xf-map-source]")!.Change(nameof(XmlValueSourceKind.Generated));
        panel.FindAll("[data-testid=xf-mapping]")[1].QuerySelector("[data-testid=xf-map-technical]")!.TextContent.Should().Be("/Skolenaervaer/Meldingshode/Id");

        panel.Find("[data-testid=xf-generate]").Click();
        panel.WaitForElement("[data-testid=xf-xml]");
        backend.Binds.Should().ContainSingle().Which.Bindings.Should().Equal(new TestDataXmlBinding("child-02", ChildPath));
        backend.Binds[0].Xml.Should().MatchRegex("<Meldingshode>.*<Id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}</Id>", "the generated UUID replaces the synthetic id before binding");
        backend.Binds[0].Bindings.Should().NotContain(b => b.Alias.StartsWith("reporter", StringComparison.Ordinal), "the reporter is submission context, never in the XML");
        panel.Find("[data-testid=xf-generated-from]").TextContent.Should().Contain("Klient/Identifikator/Fodselsnummer ← Test Data child-02").And.Contain("Meldingshode/Id ← Generated UUID");
        panel.Find("[data-testid=xf-validate]").Click();
        panel.Find("[data-testid=xf-validation]").GetAttribute("data-state").Should().Be("Valid");
    }

    [Fact]
    public void Changing_the_mapped_child_never_mutates_the_approved_fixture_and_offers_a_new_draft_while_the_reporter_does_not()
    {
        Setup(Context(Xsd()));
        var approved = Fixture(TestDataApproval.Approved, TestDataSchemaStatus.Validated) with
        {
            Alias = "skolenaervaer-valid-report",
            Provenance = [new("Test Data roles", "Child: child-01; Reporter (not in the XML): reporter-01"), new("Value mappings", "Klient/Identifikator/Fodselsnummer ← Test Data child-01")],
        };
        var panel = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, [.. ManyIdentities, approved]));
        panel.WaitForElement("[data-testid=xf-create]").Click();
        panel.WaitForElement("[data-testid=xf-mapping]");
        panel.FindAll("[data-testid=xf-regenerate-notice]").Should().BeEmpty();

        panel.Find("[data-testid=xf-identity][data-role=Reporter]").Change("reporter-02");
        panel.FindAll("[data-testid=xf-regenerate-notice]").Should().BeEmpty("the reporter is not part of the payload");
        panel.Find("[data-testid=xf-identity][data-role=Child]").Change("child-03");
        panel.Find("[data-testid=xf-regenerate-notice]").TextContent.Should().Contain("skolenaervaer-valid-report was generated with child child-01; child-03 is now selected")
            .And.Contain("stays unchanged");
        panel.Find("[data-testid=xf-generate]").Click();
        panel.WaitForElement("[data-testid=xf-xml]");
        panel.Find("[data-testid=xf-alias]").GetAttribute("value").Should().Be("skolenaervaer-valid-report-2", "a regenerated fixture is a new draft, never the approved one");
    }

    [Fact]
    public void Mapping_presentation_filters_payloads_reads_full_paths_and_records_references_only()
    {
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { [XsdFile] = RealSchema() }, Contract + ".xsd");
        var parameters = XmlValueMappings.Parameters(package);
        parameters.Should().Contain(p => p.Path == ChildPath && p.Display == "Klient → Identifikator → Fodselsnummer");
        parameters.Select(p => p.Technical).Should().OnlyHaveUniqueItems("repeated leaf names (Navn, Fodselsnummer) stay distinguishable by their full path");
        parameters.Count(p => p.Path.EndsWith(".Navn", StringComparison.Ordinal) || p.Path == "Navn").Should().BeGreaterThan(1);

        var bindings = Context().FixtureBindings;
        XmlValueMappings.CompatibleAssets(ManyIdentities, ChildPath, bindings).Select(a => a.Alias).Should().Equal("child-01", "child-02", "child-03");
        XmlValueMappings.CompatibleAssets(ManyIdentities, "Meldingshode.Avsender", bindings).Should().NotContain(a => a.Alias == "skolenaervaer-valid-report" || a.Alias == "report-json",
            "payload-level assets never appear for a single element");
        XmlValueMappings.IsPayload(XmlFixtureAsset).Should().BeTrue();
        XmlValueMappings.IsPayload(JsonPayload).Should().BeTrue();

        var text = XmlValueMappings.ProvenanceText([new(ChildPath, XmlValueSourceKind.TestDataAsset, "child-01"), new("Periode.StartDato", XmlValueSourceKind.Fixed, FixedValue: "2026-01-01"),
            new("Meldingshode.Id", XmlValueSourceKind.Generated, Generated: XmlGeneratedValue.Uuid)]);
        text.Should().Be("Klient/Identifikator/Fodselsnummer ← Test Data child-01; Periode/StartDato ← Fixed value \"2026-01-01\"; Meldingshode/Id ← Generated UUID");
        XmlValueMappings.FreeAlias("a", [Identity("a", "child"), Identity("a-2", "child")]).Should().Be("a-3");
    }

    // ---- source snapshot / contract selection ----

    private static readonly Guid Other = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private IRenderedComponent<XmlFixturePanel> OpenWithSnapshotChosen(TestDataAssetSummary[] assets, Func<Guid, string>? schemaFor = null)
    {
        Setup(schemaFor, Context(Xsd(), Xsd(Other, "other.zip")));
        var panel = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, assets));
        panel.WaitForElement("[data-testid=xf-create]").Click();
        panel.Find("[data-testid=xf-snapshot-select]").Change(Snapshot.ToString());
        panel.WaitForElement("[data-testid=xf-mapping]");
        panel.WaitForAssertion(() => panel.Find("[data-testid=xf-map-technical]").TextContent.Should().StartWith("/"));
        return panel;
    }

    [Fact]
    public void Changing_the_snapshot_never_keeps_the_previous_xsd_silently_a_same_named_xsd_is_reviewed_first()
    {
        var panel = OpenWithSnapshotChosen(ManyIdentities);
        panel.Find("[data-testid=xf-change-snapshot]").Click();
        panel.Find("[data-testid=xf-snapshot-select]").Change(Other.ToString());

        panel.Find("[data-testid=xf-snapshot-notice]").TextContent.Should().Contain("Matching XSD found in the selected snapshot — review before continuing");
        panel.Find("[data-testid=xf-xsd-review]").TextContent.Should().Contain("Review before continuing");
        panel.Find("[data-testid=xf-generate]").HasAttribute("disabled").Should().BeTrue("the new snapshot's XSD is not used until reviewed");
        panel.Find("[data-testid=xf-xsd-confirm]").Click();
        panel.WaitForAssertion(() => panel.Find("[data-testid=xf-xsd-chosen]").TextContent.Should().Contain("other.zip"));
        panel.Find("[data-testid=xf-xsd-state]").GetAttribute("data-state").Should().Be("Explicit");
        panel.Find("[data-testid=xf-generate]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void A_contract_without_the_mapped_field_marks_the_mapping_needs_review_and_blocks_generation()
    {
        // The other snapshot's XSD version has no Fodselsnummer element: the child mapping is never remapped by leaf name.
        var panel = OpenWithSnapshotChosen(ManyIdentities, id => id == Other ? RealSchema().Replace("name=\"Fodselsnummer\"", "name=\"Personnummer\"") : RealSchema());
        panel.Find("[data-testid=xf-change-snapshot]").Click();
        panel.Find("[data-testid=xf-snapshot-select]").Change(Other.ToString());
        panel.Find("[data-testid=xf-xsd-confirm]").Click();

        panel.WaitForAssertion(() => panel.Find("[data-testid=xf-map-status]").GetAttribute("data-status").Should().Be("Needs review"));
        panel.Find("[data-testid=xf-map-review]").TextContent.Should().Be("Klient/Identifikator/Fodselsnummer does not exist in the selected XSD.");
        panel.Find("[data-testid=xf-generate]").HasAttribute("disabled").Should().BeTrue();
        panel.Find("[data-testid=xf-generate-blocked]").TextContent.Should().Contain("Needs review");
    }

    [Fact]
    public void Generated_summary_names_xsd_snapshot_mappings_and_roles_and_a_manual_edit_is_recorded()
    {
        var panel = OpenWithSnapshotChosen(ManyIdentities);
        panel.Find("[data-testid=xf-generate]").Click();
        panel.WaitForElement("[data-testid=xf-generated-summary]");
        panel.Find("[data-testid=xf-sum-xsd]").TextContent.Should().Be(XsdFile);
        panel.Find("[data-testid=xf-sum-snapshot]").TextContent.Should().Be("skolenaervaer.zip · snapshot 22222222");
        panel.Find("[data-testid=xf-sum-mappings]").TextContent.Should().Be("1 applied");
        panel.Find("[data-testid=xf-sum-role][data-role=Reporter]").TextContent.Should().Be("reporter-01 · submission context, not in XML");
        panel.Find("[data-testid=xf-sum-role][data-role=Child]").TextContent.Should().Be("child-01");
        panel.FindAll("[data-testid=xf-manual-edit]").Should().BeEmpty();

        panel.Find("[data-testid=xf-xml]").Input(panel.Find("[data-testid=xf-xml]").GetAttribute("value")!.Replace("<Skolenaervaer", "<Skolenaervaer "));
        panel.Find("[data-testid=xf-manual-edit]").TextContent.Should().Be("Manual XML changes present");
        panel.Find("[data-testid=xf-validation]").GetAttribute("data-state").Should().Be("Needs validation", "a manual edit is validated again");
    }

    [Fact]
    public void Existing_approved_fixtures_are_a_payload_level_choice_never_a_field_value()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Setup(Context(Xsd()));
        var approved = Fixture(TestDataApproval.Approved, TestDataSchemaStatus.Validated) with { Alias = "skolenaervaer-valid-report", Label = "Valid report" };
        var panel = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, [.. ManyIdentities, approved]));
        panel.WaitForElement("[data-testid=xf-use-existing]").Click();
        panel.FindAll("[data-testid=xf-existing-select] option").Select(o => o.GetAttribute("value")).Should().Contain("skolenaervaer-valid-report");
        panel.Find("[data-testid=xf-existing-select]").Change("skolenaervaer-valid-report");
        panel.Find("[data-testid=xf-message]").TextContent.Should().StartWith("Payload-level choice: skolenaervaer-valid-report");

        panel.Find("[data-testid=xf-create]").Click();
        panel.WaitForElement("[data-testid=xf-map-asset]");
        panel.FindAll("[data-testid=xf-map-asset] option").Select(o => o.GetAttribute("value")).Should().NotContain("skolenaervaer-valid-report");
    }

    [Fact]
    public void Explicit_snapshot_and_mappings_persist_as_references_and_win_on_reopen()
    {
        var repository = new WorkspaceArtifactRepository();
        Services.AddSingleton<IWorkspaceArtifactRepository>(repository);
        var panel = OpenWithSnapshotChosen(ManyIdentities);
        panel.Find("[data-testid=xf-map-asset]").Change("child-03");
        panel.Find("[data-testid=xf-map-add]").Click();
        panel.FindAll("[data-testid=xf-mapping]")[1].QuerySelector("[data-testid=xf-map-parameter]")!.Change("Meldingshode.Id");
        panel.FindAll("[data-testid=xf-mapping]")[1].QuerySelector("[data-testid=xf-map-source]")!.Change(nameof(XmlValueSourceKind.Generated));

        var setup = repository.SddLifecycle.XmlFixtureSetups.Should().ContainSingle().Subject;
        setup.SnapshotId.Should().Be(Snapshot);
        setup.Mappings.Select(m => (m.Path, m.Kind, m.AssetAlias)).Should().BeEquivalentTo([(ChildPath, "TestDataAsset", "child-03"), ("Meldingshode.Id", "Generated", (string?)null)]);
        System.Text.Json.JsonSerializer.Serialize(setup).Should().NotContain("04061076692", "only asset references are stored");

        var reopened = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, ManyIdentities));
        reopened.WaitForElement("[data-testid=xf-create]").Click();
        reopened.WaitForAssertion(() => reopened.FindAll("[data-testid=xf-mapping]").Count.Should().Be(2));
        reopened.Find("[data-testid=xf-snapshot-state]").GetAttribute("data-state").Should().Be("Explicit", "an explicit choice wins on reload");
        reopened.Find("[data-testid=xf-map-asset] option[selected]").GetAttribute("value").Should().Be("child-03");
    }

    [Fact]
    public void A_fixed_value_overrides_the_generated_default_and_a_generated_uuid_replaces_the_synthetic_id_over_the_real_xsd()
    {
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { [XsdFile] = RealSchema() }, Contract + ".xsd");
        var parameters = XmlValueMappings.Parameters(package);
        var hours = parameters.First(p => p.Path.EndsWith("SkolenaervaerRegistrering.AntallTimerEtterPlanen", StringComparison.Ordinal));
        hours.Technical.Should().StartWith("/Skolenaervaer/").And.EndWith("/SkolenaervaerRegistrering/AntallTimerEtterPlanen");
        var generated = SchemaStudioService.GenerateMinimumValid(package, XsdFile).Xml!;
        var withMapped = SchemaStudioService.WithElements(package, generated, [hours.Path, "Meldingshode.Id"]);

        var (xml, problems) = XmlValueMappings.Apply(withMapped,
            [new(hours.Path, XmlValueSourceKind.Fixed, FixedValue: "17"), new("Meldingshode.Id", XmlValueSourceKind.Generated, Generated: XmlGeneratedValue.Uuid)], DateTimeOffset.UtcNow);

        problems.Should().BeEmpty();
        xml.Should().Contain("<AntallTimerEtterPlanen>17</AntallTimerEtterPlanen>", "the explicit mapping wins over the generator's default");
        xml.Should().MatchRegex("<Meldingshode>.*?<Id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}</Id>");
        var bound = System.Text.RegularExpressions.Regex.Replace(xml, "<Fodselsnummer>[^<]*</Fodselsnummer>", "<Fodselsnummer>04061076692</Fodselsnummer>");
        SchemaStudioService.Validate(package, XsdFile, bound).Status.Should().Be(XmlSchemaValidationStatus.Valid);
    }
}
