using BirkNext.Integrations;
using BirkNext.TestData;
using BirkNext.Web.Components;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The tester view of Test Data: readiness first (Reporter, Child, XML fixture, contract validation, overall — the shared rule also used by Setup
/// Library preflight and consumers), then identities, XML fixtures, requested data, assets, candidates, sources and technical documentation,
/// with provenance, schema internals, source detail and documentation collapsed but still available. Generic fixtures.
/// </summary>
public sealed class TestDataTesterViewTests : BunitContext
{
    private const string Contract = "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn";
    private const string XsdFile = "xsd/" + Contract + ".xsd";
    private static readonly Guid Snapshot = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string RawChild = "04061076692";

    private TestDataCatalogView _catalog = new([], [], []);
    private TestDataConsumerView _consumerView = new([], [], new TestDataOverview(3, 0, 0, 0, 0, 0, 0, 0, 0));

    public TestDataTesterViewTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var api = new Mock<ITestDataApiService>();
        api.Setup(a => a.CatalogAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => (_catalog, (string?)null));
        api.Setup(a => a.ConsumersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => (_consumerView, (string?)null));
        Services.AddSingleton(api.Object);
        var journeys = new Mock<IIntegrationJourneysApiService>();
        journeys.Setup(j => j.IntegrationTemplatesAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ActiveEventApiResult<IReadOnlyList<IntegrationTemplateView>>([Context()], null));
        Services.AddSingleton(journeys.Object);
    }

    private static IntegrationTemplateView Context() =>
        new("skolenaervaer-report-intake", "Skolenærvær – Altinn report intake", "", IntegrationKind.HttpApi, "skolenaervaer", "Skolenærvær Testing")
        {
            SchemaName = Contract,
            SchemaCandidates = [new(Snapshot, "skolenaervaer.zip", Contract, XsdFile)],
            FixtureBindings = [new("Child", "child", "Klient.Identifikator.Fodselsnummer", "The child the report is about."), new("Reporter", "reporter", null, "Altinn actor.")],
        };

    private static TestDataAssetSummary Identity(string alias, string capability, TestDataApproval approval = TestDataApproval.Approved) => new()
    {
        Id = $"altinn-local:{capability}:{alias}", Alias = alias, Label = $"Skolenærvær default {capability}", Kind = TestDataKind.EntityReference, Capabilities = ["person", capability],
        Approval = approval, Sensitivity = TestDataSensitivity.Sensitive, IsDefault = true, Source = "Local approved configuration",
    };

    private static TestDataAssetSummary Fixture(TestDataApproval approval = TestDataApproval.Approved, TestDataSchemaStatus? status = TestDataSchemaStatus.Validated, Guid? snapshot = null) => new()
    {
        Id = "schema-studio:skolenaervaer-valid-report", Alias = "skolenaervaer-valid-report", Label = "Skolenærvær valid report", Kind = TestDataKind.Structured,
        MediaType = "application/xml", Approval = approval, Source = "Schema Studio",
        Schema = new TestDataSchemaReference(XsdFile, snapshot ?? Snapshot, null, "Skolenaervaer", XsdFile),
        SchemaCheck = status is { } s ? new TestDataSchemaCheck(s, XsdFile, s == TestDataSchemaStatus.Validated ? "Valid against the XSD." : "Pattern constraint failed.", [], snapshot ?? Snapshot, "abcdef0123456789abcdef") : null,
        Provenance = [new("Generator", "Test Data XML fixture v1"), new("Test Data roles", "Child: skolenaervaer-child-default; Reporter (not in the XML): skolenaervaer-reporter-default")],
    };

    private static List<TestDataAssetSummary> Identities() => [Identity("skolenaervaer-reporter-default", "reporter"), Identity("skolenaervaer-child-default", "child")];

    // ── the shared rule ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_shared_rule_counts_approved_fixtures_only_and_never_reads_ready_when_outdated_or_invalid()
    {
        TestDataXmlFixtureFacts F(bool approved, TestDataSchemaStatus? status, Guid? snapshot = null) => new("f", approved, status, snapshot ?? Snapshot);
        TestDataXmlReadiness.Evaluate([]).State.Should().Be(TestDataXmlReadinessState.Missing);
        TestDataXmlReadiness.Evaluate([F(false, TestDataSchemaStatus.Invalid)]).State.Should().Be(TestDataXmlReadinessState.NotApproved, "Setup Library preflight and Test Data agree");
        TestDataXmlReadiness.Evaluate([F(true, TestDataSchemaStatus.Validated)], Snapshot).State.Should().Be(TestDataXmlReadinessState.Ready);
        TestDataXmlReadiness.Evaluate([F(true, TestDataSchemaStatus.Validated, Guid.NewGuid())], Snapshot).State.Should().Be(TestDataXmlReadinessState.Outdated);
        TestDataXmlReadiness.Evaluate([F(true, TestDataSchemaStatus.Invalid)], Snapshot).State.Should().Be(TestDataXmlReadinessState.Invalid);
        TestDataXmlReadiness.Evaluate([F(true, TestDataSchemaStatus.SchemaUnavailable)]).State.Should().Be(TestDataXmlReadinessState.SchemaUnavailable);
        TestDataXmlReadiness.Evaluate([F(true, null)]).State.Should().Be(TestDataXmlReadinessState.NotValidated);
        TestDataXmlReadiness.IsFixtureFor("application/xml", new(XsdFile), ["negative-test"], Contract).Should().BeFalse("negative fixtures never count");
    }

    [Theory]
    [InlineData("ready", "Ready", null, "Valid")]
    [InlineData("xml-missing", "Partial", "XML fixture missing", "Missing")]
    [InlineData("xml-invalid", "Needs attention", "XML fixture invalid", "Invalid")]
    [InlineData("xml-outdated", "Partial", "XML fixture validation outdated", "Needs validation")]
    [InlineData("child-missing", "Needs attention", "Child missing", "Valid")]
    public void The_tester_aggregate_names_one_reason(string scenario, string overall, string? reason, string contract)
    {
        var assets = scenario switch
        {
            "ready" => [.. Identities(), Fixture()],
            "xml-missing" => Identities(),
            "xml-invalid" => [.. Identities(), Fixture(status: TestDataSchemaStatus.Invalid)],
            "xml-outdated" => [.. Identities(), Fixture(snapshot: Guid.NewGuid())],
            _ => new List<TestDataAssetSummary> { Identity("skolenaervaer-reporter-default", "reporter"), Fixture() },
        };
        var status = XmlFixturePresentation.Tester(assets, Context());
        (status.Overall, status.Reason, status.ContractValidation).Should().Be((overall, reason, contract));
        status.Roles.Single(r => r.Role == "Reporter").Note.Should().Be("Used to submit the report; not part of the XML payload.");
    }

    [Fact]
    public void The_readiness_component_shows_roles_xml_contract_and_overall_without_any_identifier_and_links_its_owner_for_consumers()
    {
        _catalog = new([.. Identities(), Fixture()], [], []);
        var cut = Render<TestDataTesterReadiness>(p => p.Add(x => x.ShowOwnerLink, true));
        cut.WaitForAssertion(() => cut.Find("[data-testid=tdr-overall]").GetAttribute("data-state").Should().Be("Ready"));
        cut.Find("[data-testid=tdr-role][data-role=Reporter]").TextContent.Should().Contain("Ready").And.Contain("Skolenærvær default reporter");
        cut.Find("[data-testid=tdr-role][data-role=Child]").TextContent.Should().Contain("Ready").And.Contain("Skolenærvær default child");
        cut.Find("[data-testid=tdr-xml]").TextContent.Should().Contain("Ready for test use").And.Contain("Skolenærvær valid report");
        cut.Find("[data-testid=tdr-contract]").TextContent.Should().Contain("Valid");
        cut.Find("[data-testid=tdr-open-test-data]").GetAttribute("href").Should().StartWith("test-data");
        cut.Markup.Should().NotContain(RawChild);
    }

    // ── XML fixtures: state-dependent action, compact rows, details collapsed ──────────────────────────────────────────────

    [Fact]
    public void A_ready_fixture_is_opened_not_created_again_and_its_technical_details_are_collapsed_until_opened()
    {
        var cut = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, [.. Identities(), Fixture()]));
        cut.WaitForAssertion(() => cut.Find("[data-testid=xf-readiness]").TextContent.Should().Be("Ready for test use"));
        cut.Find("[data-testid=xf-open-ready]").ClassList.Should().Contain("btn-primary");
        cut.Find("[data-testid=xf-create]").TextContent.Should().Be("Create another");
        cut.Find("[data-testid=xf-create]").ClassList.Should().Contain("btn-secondary");

        var row = cut.Find("[data-testid=xf-fixture]");
        row.QuerySelector("[data-testid=xf-fixture-approval]")!.TextContent.Should().Be("Approved");
        row.QuerySelector("[data-testid=xf-fixture-validation]")!.TextContent.Should().Be("Valid", "approval and schema validation are separate facts");
        row.QuerySelector("[data-testid=xf-fixture-ready]")!.TextContent.Should().Be("Ready for test use");
        cut.Find("[data-testid=xf-fixture-detail]").HasAttribute("hidden").Should().BeTrue();

        cut.Find("[data-testid=xf-fixture-toggle]").Click();
        var detail = cut.Find("[data-testid=xf-fixture-detail]");
        detail.HasAttribute("hidden").Should().BeFalse();
        detail.QuerySelector("[data-testid=xf-detail-schema]")!.TextContent.Should().Be(XsdFile);
        detail.QuerySelector("[data-testid=xf-detail-snapshot]")!.TextContent.Should().Be(Snapshot.ToString());
        detail.QuerySelectorAll("[data-testid=xf-detail-provenance]").Select(e => e.TextContent).Should().Contain("Test Data XML fixture v1");
        cut.Find("[data-testid=xf-fixture-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        JSInterop.Invocations.Where(i => i.Identifier == "birknextFocusElement").Last().Arguments.Single().Should().Be("xf-detail-skolenaervaer-valid-report-heading");

        cut.Find("[data-testid=xf-fixture-toggle]").Click();
        cut.Find("[data-testid=xf-fixture-detail]").HasAttribute("hidden").Should().BeTrue();
        JSInterop.Invocations.Where(i => i.Identifier == "birknextFocusElement").Last().Arguments.Single().Should().Be("xf-detail-skolenaervaer-valid-report-toggle");
        cut.Find("#xf-detail-skolenaervaer-valid-report-toggle");
    }

    [Fact]
    public void Missing_makes_create_primary_and_shows_no_schema_detail_until_creation_starts()
    {
        var cut = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, Identities()));
        cut.WaitForAssertion(() => cut.Find("[data-testid=xf-readiness]").TextContent.Should().Be("Missing"));
        cut.Find("[data-testid=xf-create]").TextContent.Should().Be("Create XML fixture");
        cut.Find("[data-testid=xf-create]").ClassList.Should().Contain("btn-primary");
        cut.FindAll("[data-testid=xf-xsd], [data-testid=xf-fixture-detail], [data-testid=xf-open-ready]").Should().BeEmpty();
    }

    [Theory]
    [InlineData("invalid", "Invalid", "Create a valid XML fixture")]
    [InlineData("outdated", "Validation outdated", "Revalidate against current XSD")]
    public void Invalid_or_outdated_never_read_ready_and_offer_the_fix(string scenario, string readiness, string action)
    {
        var fixture = scenario == "invalid" ? Fixture(status: TestDataSchemaStatus.Invalid) : Fixture(snapshot: Guid.NewGuid());
        var cut = Render<XmlFixturePanel>(p => p.Add(x => x.Assets, [.. Identities(), fixture]));
        cut.WaitForAssertion(() => cut.Find("[data-testid=xf-readiness]").TextContent.Should().Be(readiness));
        cut.Find("[data-testid=xf-create]").TextContent.Should().Be(action);
        cut.FindAll("[data-testid=xf-fixture-ready]").Should().BeEmpty();
    }

    // ── persisted consumer usage and requested Test Data ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Requirements_a_consumer_persisted_show_resolved_needs_and_an_optional_unprovided_payload_is_not_an_open_request()
    {
        var consumer = new TestDataConsumer { ConsumerType = "Test Pack integration", ConsumerId = "skolenaervaer-report-intake", DisplayName = "Skolenærvær – Altinn report intake", Parent = "Skolenærvær Testing" };
        _catalog = new([.. Identities().Select(i => i with { Consumers = 1 })], [], []);
        _consumerView = new(
        [
            new() { Id = "declared:skolenaervaer-report-intake:child", Consumer = consumer, Usage = "Child · XML child field", Required = true, Status = TestDataRequirementStatus.Resolved, BoundAssetId = "altinn-local:child:skolenaervaer-child-default" },
            new() { Id = "declared:skolenaervaer-report-intake:reporter", Consumer = consumer, Usage = "Reporter · submitting Altinn actor (not in the XML)", Required = true, Status = TestDataRequirementStatus.Resolved },
            new() { Id = "declared:skolenaervaer-report-intake:report-payload", Consumer = consumer, Usage = "Valid report payload · XML", Required = false, RequestedDataType = TestDataDataType.XmlPayload, Status = TestDataRequirementStatus.Missing },
        ], [], new TestDataOverview(2, 0, 0, 0, 1, 2, 2, 0, 0));

        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("test-data?section=requests");
        var page = Render<TestDataPage>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=td-requirement]").Should().HaveCount(3));

        page.FindAll("[data-testid=td-requested-none]").Should().BeEmpty();
        var statuses = page.FindAll("[data-testid=td-requirement-status]").Select(s => (s.TextContent.Trim(), s.GetAttribute("data-optional"))).ToList();
        statuses.Should().Equal(("Resolved", null), ("Resolved", null), ("Optional · not provided", "true"));
        nav.NavigateTo("test-data?section=assets");
        page.FindAll("[data-testid=td-used-count]").Select(c => c.TextContent).Should().OnlyContain(t => t != "Unused", "the defaults are used by a consumer that persisted them");
    }

    // ── page order and collapsed sections ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_page_follows_the_tester_workflow_and_keeps_technical_material_collapsed_but_available()
    {
        _catalog = new([.. Identities(), Fixture()], [],
        [
            new("altinn-local", "Approved local test data", "Local Altinn testing configuration.", 2, 0, TestDataSourceStatus.Available),
            new("schema-studio", "Schema Studio", "Saved XML.", 1, 0, TestDataSourceStatus.Available),
            new("configured", "Configured catalog", "Declared in configuration.", 0, 0, TestDataSourceStatus.NotConfigured),
            new("registered", "Registered assets", "Registered from candidates.", 0, 0, TestDataSourceStatus.Empty),
        ]);
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var page = Render<TestDataPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=td-overview]"));

        // Four sections; the Overview (readiness, next step, session identities) is the default — no long page of every section.
        page.FindAll("[data-testid=td-sections] a").Select(a => a.TextContent.Trim()).Should().HaveCount(4).And.StartWith("Overview");
        page.Find("[data-testid=td-section-overview]").GetAttribute("aria-current").Should().Be("page");
        page.FindAll("[data-testid=td-session-identities]").Should().ContainSingle();
        page.FindAll("[data-testid=td-assets], [data-testid=td-xml-fixtures], [data-testid=td-requested], [data-testid=td-docs]").Should().BeEmpty();
        page.Find("[data-testid=td-overview-coverage]").TextContent.Should().Be("None registered");
        page.Find("[data-testid=sti-help]").TextContent.Should().Be("Approved Test Data identities used by applicable test journeys. Explicit session selections override defaults.");

        nav.NavigateTo("test-data?section=assets");
        page.WaitForAssertion(() => page.Find("#td-assets-heading").TextContent.Should().StartWith("Test Data assets"));
        page.FindAll(".td-table thead th").Select(th => th.TextContent).Should().Equal("Name", "Type", "Role", "Applies to", "Status", "Scope", "Consumers", "Actions");
        page.FindAll("[data-testid=td-consumers]").Select(c => c.TextContent).Should().AllBe("Unused");
        page.FindAll("[data-testid=td-detail-row]").Should().OnlyContain(r => r.HasAttribute("hidden"), "asset details are collapsed");

        nav.NavigateTo("test-data?section=requests");
        page.WaitForAssertion(() => page.Find("[data-testid=td-docs]"));
        var order = new[] { "td-requested", "td-candidates-section", "td-sources", "td-docs" };
        var positions = order.Select(id => page.Markup.IndexOf($"data-testid=\"{id}\"", StringComparison.Ordinal)).ToList();
        positions.Should().OnlyContain(p => p >= 0).And.BeInAscendingOrder();
        page.Find("[data-testid=td-requested-none]").TextContent.Should().Be("No open Test Data requirements.");
        page.FindAll("[data-testid=td-source-summary-item]").Select(i => i.TextContent.Trim()).Should().Equal(
            "Approved local test data2", "Schema Studio fixtures1", "Configured catalogNot configured (optional)", "Registered source assets0");
        page.Find("[data-testid=td-sources-disclosure-body]").HasAttribute("hidden").Should().BeTrue("Sources & configuration is collapsed");
        page.Find("[data-testid=td-source][data-source=configured] [data-testid=td-source-action]").TextContent.Should().Be("Configure catalog");
        page.Find("[data-testid=td-candidates-body]").HasAttribute("hidden").Should().BeTrue();
        page.Find("[data-testid=td-documentation-body]").HasAttribute("hidden").Should().BeTrue("technical documentation is not in the default view");
        page.Find("[data-testid=td-documentation-toggle]").TextContent.Should().Contain("View technical documentation");
        page.Find("[data-testid=td-documentation-text]").TextContent.Should().Contain("skolenaervaer-valid-report", "the documentation is still available");
        page.Markup.Should().NotContain(RawChild);
    }

    // ── NEXT54: the Overview names one next step from the shared readiness ──────────────────────────────────────────────────

    [Theory]
    [InlineData(false, "Generate missing XML fixture", "section=fixtures&create=xml")]
    [InlineData(true, "Open XML fixture", "section=fixtures")]
    public void The_overview_next_step_follows_the_shared_readiness(bool fixtureReady, string label, string href)
    {
        _catalog = new([.. Identities(), .. fixtureReady ? new[] { Fixture() } : []], [], []);
        var page = Render<TestDataPage>();

        page.WaitForAssertion(() => page.Find("[data-testid=td-next-action]").TextContent.Should().Be(label));
        page.Find("[data-testid=td-next-action]").GetAttribute("href").Should().EndWith(href);
        page.FindAll("[data-testid=td-next-action]").Should().ContainSingle("one primary step, never several equal buttons");
    }

    [Fact]
    public void A_missing_approved_identity_comes_before_the_xml_fixture()
    {
        _catalog = new([Identity("skolenaervaer-reporter-default", "reporter")], [], []);
        var page = Render<TestDataPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=td-next-action]").TextContent.Should().Be("Choose an approved child"));
    }

    [Fact]
    public void A_load_failure_is_an_error_with_retry_never_an_empty_catalog()
    {
        var api = new Mock<ITestDataApiService>();
        var fail = true;
        api.Setup(a => a.CatalogAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => fail ? ((TestDataCatalogView?)null, "Test Data: the backend could not be reached.") : (_catalog, (string?)null));
        api.Setup(a => a.ConsumersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => (_consumerView, (string?)null));
        Services.AddSingleton(api.Object);
        _catalog = new([.. Identities()], [], []);
        var page = Render<TestDataPage>();

        page.WaitForAssertion(() => page.Find("[data-testid=td-error]").TextContent.Should().Contain("could not be reached"));
        page.FindAll("[data-testid=td-empty]").Should().BeEmpty();
        fail = false;
        page.Find("[data-testid=td-error-retry]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=td-overview]"));
    }

    // ── NEXT54 follow-up: the eight readiness parts ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_overview_shows_eight_readiness_parts_and_never_calls_runtime_ready()
    {
        _catalog = new([.. Identities()], [], []);
        var page = Render<TestDataPage>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=tdr-dimension]").Should().HaveCount(8));
        string State(string key) => page.Find($"[data-testid=tdr-dimension][data-key={key}]").GetAttribute("data-state")!;
        page.FindAll("[data-testid=tdr-dimension]").Select(d => d.GetAttribute("data-key")).Should()
            .Equal("identities", "contract", "mapping", "fixture", "xsd", "consumers", "environment", "runtime");
        State("identities").Should().Be("Ready");
        State("contract").Should().Be("Ready", "one XSD candidate");
        State("fixture").Should().Be("Blocked", "a preview is not a fixture: nothing is stored");
        State("xsd").Should().Be("NotAssessed", "waiting for a saved fixture");
        State("runtime").Should().Be("NotAssessed");
        page.Find("[data-testid=tdr-dimension][data-key=runtime]").TextContent.Should().Contain("XSD-valid is not runtime-ready");
    }

    [Fact]
    public void A_saved_approved_valid_fixture_is_ready_but_unbound_consumers_and_runtime_still_need_their_own_evidence()
    {
        _catalog = new([.. Identities(), Fixture()], [], []);
        var page = Render<TestDataPage>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=tdr-dimension]").Should().HaveCount(8));
        string State(string key) => page.Find($"[data-testid=tdr-dimension][data-key={key}]").GetAttribute("data-state")!;
        State("fixture").Should().Be("Ready");
        State("xsd").Should().Be("Ready");
        State("consumers").Should().Be("NeedsAttention", "no consumer binds the fixture yet");
        State("runtime").Should().Be("NotAssessed");
    }

    // ── Test Data Overview: dimension detail and next step are separate elements ─────────────────────────────────────────────

    [Fact]
    public void Readiness_values_and_dimension_next_steps_render_as_separate_elements()
    {
        _catalog = new([.. Identities()], [], []);
        var page = Render<TestDataPage>();
        page.WaitForAssertion(() => page.FindAll("[data-testid=tdr-dimension]").Should().HaveCount(8));
        page.Find("[data-testid=tdr-overall] dd").ClassList.Should().Contain("tdr-fields");
        page.Find("[data-testid=tdr-role][data-role=Reporter] dd").Children.Length.Should().BeGreaterThan(1, "status and identity name are separate");
        var fixture = page.Find("[data-testid=tdr-dimension][data-key=fixture]");
        fixture.QuerySelector("[data-testid=tdr-dimension-detail]").Should().NotBeNull();
        var action = fixture.QuerySelector("[data-testid=tdr-dimension-action]");
        if (action is not null)
        {
            action.TextContent.Should().StartWith("Next step: ");
            fixture.QuerySelector("[data-testid=tdr-dimension-detail]")!.TextContent.Should().NotContain(action.TextContent.Replace("Next step: ", ""), "the next step is not glued onto the detail");
        }
    }
}
