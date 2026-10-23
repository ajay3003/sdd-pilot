using BirkNext.TestData;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Test Data page: actionable empty states (no sources / configured but empty / unavailable), filters only with assets, sources as
/// status/action cards with provider detail behind "View details", approval ≠ validation ≠ intent per asset, and provenance per asset.
/// </summary>
public sealed class TestDataSourcesAndStatusTests : BunitContext
{
    private TestDataCatalogView _catalog = new([], [], []);

    public TestDataSourcesAndStatusTests() => Services.AddSingleton<ITestDataApiService>(new Api(() => _catalog));

    private sealed class Api(Func<TestDataCatalogView> catalog) : ITestDataApiService
    {
        public Task<(TestDataCatalogView? Catalog, string? Error)> CatalogAsync(CancellationToken ct = default) =>
            Task.FromResult<(TestDataCatalogView?, string?)>((catalog(), null));
        public Task<(TestDataAssetDetail? Detail, string? Error)> AssetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<(TestDataAssetDetail?, string?)>((new TestDataAssetDetail(catalog().Assets.Single(a => a.Id == id), null, "Sensitive: the value stays in the backend and is never shown."), null));
    }

    private static readonly TestDataSourceSummary Configured = new("configured", "Configured catalog", "Typed assets supplied by TestData configuration.", 0, 0,
        TestDataSourceStatus.NotConfigured, "Read from the TestData configuration section.") { StatusLabel = "Not configured" };
    private static readonly TestDataSourceSummary Local = new("altinn-local", "Local approved configuration", "Approved local test references supplied by environment or test configuration.", 0, 0,
        TestDataSourceStatus.NotConfigured, "Currently includes approved synthetic TestID reporters and synthetic persons.") { StatusLabel = "No approved assets" };
    private static readonly TestDataSourceSummary Studio = new("schema-studio", "Schema Studio", "Reusable schema-derived XML explicitly registered as Test Data.", 0, 0,
        TestDataSourceStatus.NotConfigured, "Only saved XML that was registered for reuse appears here.") { StatusLabel = "No registered fixtures" };

    private static TestDataAssetSummary Xml(string alias, TestDataApproval approval, TestDataSchemaStatus? schema, bool negative = false, string source = "Schema Studio", string sourceId = "schema-studio") => new()
    {
        Id = $"{sourceId}:{alias}", Alias = alias, Kind = TestDataKind.Structured, MediaType = "application/xml", Approval = approval,
        ApprovalDetail = approval == TestDataApproval.Approved ? null : "Registered as a draft in Schema Studio; approve it there before it is used.",
        Validation = schema == TestDataSchemaStatus.Validated ? TestDataValidation.Validated : schema == TestDataSchemaStatus.Invalid ? TestDataValidation.Invalid : TestDataValidation.NeedsValidation,
        Sensitivity = TestDataSensitivity.Sensitive, Source = source, SourceId = sourceId, ValueAvailable = true,
        Capabilities = negative ? ["negative-test", "schema-derived"] : ["schema-derived"],
        Schema = new TestDataSchemaReference("xsd/report.xsd", Guid.Parse("11111111-2222-3333-4444-555555555555"), "abcdef0123456789", null, "report.xsd"),
        SchemaCheck = schema is { } status ? new TestDataSchemaCheck(status, "report.xsd", status == TestDataSchemaStatus.Invalid ? "Invalid against the schema." : "Checked.",
            status == TestDataSchemaStatus.Invalid ? ["Element 'Periode': missing child element 'StartDato'."] : [], Guid.Parse("11111111-2222-3333-4444-555555555555"), "abcdef0123456789abcdef") : null,
        Provenance = [new("Saved in", "Schema Studio (saved XML)"), new("Schema file", "report.xsd"), new("Source snapshot", "11111111-2222-3333-4444-555555555555"), new("Schema fingerprint", "abcdef012345")],
    };

    private static TestDataCatalogView Populated() => new(
    [
        Xml("valid-report", TestDataApproval.Approved, TestDataSchemaStatus.Validated),
        Xml("missing-startdato", TestDataApproval.Approved, TestDataSchemaStatus.Invalid, negative: true),
        Xml("draft-report", TestDataApproval.Unapproved, TestDataSchemaStatus.Validated),
        Xml("orphan-report", TestDataApproval.Approved, TestDataSchemaStatus.SchemaUnavailable, negative: true, source: "Configured catalog", sourceId: "configured"),
        new() { Id = "configured:no-record", Alias = "no-record", Kind = TestDataKind.ExpectedResult, ExpectsAbsence = true, Approval = TestDataApproval.Approved, Validation = TestDataValidation.Unknown,
            Sensitivity = TestDataSensitivity.Internal, Source = "Configured catalog", SourceId = "configured", Environment = "QA", Provenance = [new("Configuration", "TestData:Assets")] },
    ], [],
    [Configured with { AssetCount = 2, Status = TestDataSourceStatus.Available, StatusLabel = "Configured catalog" }, Local, Studio with { AssetCount = 3, Status = TestDataSourceStatus.Available, StatusLabel = "Registered fixtures" }]);

    private IRenderedComponent<TestDataPage> Page(TestDataCatalogView catalog)
    {
        _catalog = catalog;
        var page = Render<TestDataPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=td-page]").GetAttribute("aria-busy").Should().Be("false"));
        return page;
    }

    private static Dictionary<string, AngleSharp.Dom.IElement> Rows(IRenderedComponent<TestDataPage> page) =>
        page.FindAll("[data-testid=td-asset]").ToDictionary(row => row.GetAttribute("data-alias")!);

    // ── Empty states ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NoSourcesConfigured_ExplainsAndOffersOnePrimaryAction_AndNoFilters()
    {
        var page = Page(new([], [], [Configured, Local, Studio]));
        var empty = page.Find("[data-testid=td-empty]");
        empty.GetAttribute("data-kind").Should().Be("NoSources");
        empty.QuerySelector("h2")!.TextContent.Should().Be("No reusable Test Data assets yet");
        empty.TextContent.Should().Contain("register a reviewed source candidate").And.Contain("configure the catalog").And.Contain("Schema Studio fixture");
        page.Find("[data-testid=td-safety]").TextContent.Should().Contain("never generates");
        page.Find("[data-testid=td-empty-candidates]").ClassList.Should().Contain("btn-primary", "the first route is reviewing source candidates — Schema Studio is not the only one");
        page.Find("[data-testid=td-empty-configure]").GetAttribute("href").Should().Be("user-guide#test-data");
        page.Find("[data-testid=td-empty-schema-studio]").GetAttribute("href").Should().Be("schema-studio");
        page.FindAll("[data-testid=td-filters]").Should().BeEmpty("filters are useless without assets");
        page.FindAll("[data-testid=td-assets]").Should().BeEmpty();
        page.Find("[data-testid=td-safety]").TextContent.Should().Contain("Sensitive values are never displayed");
    }

    [Fact]
    public void ConfiguredButEmpty_IsNotTheSameMessage_AsNothingConfigured()
    {
        var page = Page(new([], [], [Configured with { Status = TestDataSourceStatus.Empty, StatusLabel = "Configured catalog · 0 assets" }, Local]));
        page.Find("[data-testid=td-empty]").GetAttribute("data-kind").Should().Be("ConfiguredButEmpty");
        page.Find("[data-testid=td-empty] p").TextContent.Should().Be("A source is configured, but it currently supplies no assets.");
        page.FindAll("[data-testid=td-empty-schema-studio]").Should().BeEmpty("no Schema Studio source, so no Schema Studio action");
        page.Find("[data-testid=td-source][data-source=configured] [data-testid=td-source-status]").TextContent.Should().Contain("Configured catalog · 0 assets");
    }

    [Fact]
    public void UnavailableSource_IsScopedToItsCard_AndThePageStillRenders()
    {
        var page = Page(new([Xml("valid-report", TestDataApproval.Approved, TestDataSchemaStatus.Validated)], [],
            [Configured with { Status = TestDataSourceStatus.Unavailable, Problem = "This source could not be read (IOException). Other sources are unaffected." },
             Studio with { AssetCount = 1, Status = TestDataSourceStatus.Available, StatusLabel = "Registered fixtures" }]));
        page.Find("[data-testid=td-source][data-source=configured] [data-testid=td-source-problem]").TextContent.Should().Contain("Source unavailable").And.Contain("Other sources are unaffected");
        page.Find("[data-testid=td-source][data-source=configured]").GetAttribute("data-status").Should().Be("Unavailable");
        page.FindAll("[data-testid=td-asset]").Should().ContainSingle();
    }

    // ── Sources ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sources_AreStatusActionCards_WithDomainDetailOnlyBehindViewDetails()
    {
        var page = Page(Populated());
        var cards = page.FindAll("[data-testid=td-source]");
        cards.Select(c => c.GetAttribute("data-source")).Should().Equal("configured", "altinn-local", "schema-studio");
        cards.Select(c => c.QuerySelector("[data-testid=td-source-count]")!.TextContent).Should().Equal("2 assets", "0 assets", "3 assets");
        cards.Select(c => c.QuerySelector("[data-testid=td-source-status]")!.TextContent).Should().Equal(["Status: Configured catalog", "Status: No approved assets", "Status: Registered fixtures"],
            "each source states its own truth; Not configured is never used mechanically");
        page.FindAll("[data-testid=td-source-count]").Sum(c => int.Parse(c.TextContent.Split(' ')[0])).Should().Be(page.FindAll("[data-testid=td-asset]").Count, "source counts reconcile with the assets");
        page.Find("[data-testid=td-source][data-source=configured] [data-testid=td-source-action]").GetAttribute("href").Should().Be("user-guide#test-data");
        page.Find("[data-testid=td-source][data-source=schema-studio] [data-testid=td-source-action]").TextContent.Should().Be("Open Schema Studio");
        page.FindAll("[data-testid=td-source][data-source=altinn-local] [data-testid=td-source-action]").Should().BeEmpty("no real page to configure it from");

        // Domain-specific wording is never on a card's face; it is in the collapsed source detail.
        var local = page.Find("[data-testid=td-source][data-source=altinn-local]");
        local.QuerySelector("h3")!.TextContent.Should().Be("Local approved configuration");
        page.Find("[data-testid=td-source-details-altinn-local-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        page.Find("[data-testid=td-source-details-altinn-local-body]").TextContent.Should().Contain("TestID");
        page.Find("[data-testid=td-lead]").TextContent.Should().NotContainAny("Altinn", "TestID", "Skolenærvær", "reporter");
    }

    // ── Approval / validation / intent ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("valid-report", "Approved", "Valid", "Positive")]
    [InlineData("missing-startdato", "Approved", "Invalid", "Negative test")]
    [InlineData("draft-report", "Not approved", "Valid", "Positive")]
    [InlineData("orphan-report", "Approved", "Schema unavailable", "Negative test")]
    [InlineData("no-record", "Approved", "Not verified", "Expected result")]
    public void ApprovalValidationAndIntent_AreThreeIndependentColumns(string alias, string approval, string validation, string intent)
    {
        // Approval, validation and intent stay three separate facts, in the asset's detail; the row leads with the lifecycle status.
        var detail = Rows(Page(Populated()))[alias].NextElementSibling!;
        detail.QuerySelector("[data-testid=td-approval]")!.TextContent.Should().Be(approval);
        detail.QuerySelector("[data-testid=td-validation]")!.TextContent.Should().Be(validation);
        detail.QuerySelector("[data-testid=td-intent]")!.TextContent.Should().Be(intent);
    }

    [Fact]
    public void ApprovedInvalidNegativeFixture_IsNotStyledAsAnError_AndExplainsApprovalIndependence()
    {
        var page = Page(Populated());
        var row = Rows(page)["missing-startdato"];
        var detail = row.NextElementSibling!;
        detail.QuerySelector("[data-testid=td-validation]")!.ClassList.Should().Contain("td-pill-neutral", "invalid is what a negative fixture is for");
        detail.QuerySelector("[data-testid=td-approval]")!.ClassList.Should().Contain("td-pill-ready");
        row.QuerySelector("[data-testid=td-status]")!.TextContent.Should().Be("Approved");
        detail.QuerySelector("[data-testid=td-intent-detail]")!.TextContent.Should().Contain("deliberately invalid");
        detail.QuerySelector("[data-testid=td-schema-detail]")!.TextContent.Should().Contain("StartDato");
        detail.TextContent.Should().Contain("revalidation never changes approval");
        Rows(page)["draft-report"].NextElementSibling!.QuerySelector("[data-testid=td-approval-detail]")!.TextContent.Should().Contain("draft");
    }

    // ── Filters ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Filters_ByStatusScopeSourceTypeAndSearch_WithClearAndAnEmptyResult()
    {
        var page = Page(Populated());
        page.Find("[data-testid=td-count]").TextContent.Should().Be("(5 of 5)");
        page.Find("[data-testid=td-filter-status]").Change(nameof(TestDataAssetStatus.Draft));
        Rows(page).Keys.Should().Equal("draft-report");
        page.Find("[data-testid=td-count]").TextContent.Should().Be("(1 of 5)");
        page.Find("[data-testid=td-clear-filters]").Click();
        page.Find("[data-testid=td-search]").Input("orphan");
        Rows(page).Keys.Should().Equal("orphan-report");
        page.Find("[data-testid=td-clear-filters]").Click();
        page.Find("[data-testid=td-filter-scope]").Change("QA");
        Rows(page).Keys.Should().Equal("no-record");
        page.Find("[data-testid=td-filter-source]").Change("Schema Studio");
        page.Find("[data-testid=td-no-match]").TextContent.Should().Contain("No test data matches the selected filters");
        page.Find("[data-testid=td-clear-filters]").Click();
        page.Find("[data-testid=td-filter-source]").Change("Schema Studio");
        Rows(page).Should().HaveCount(3);
        page.Find("[data-testid=td-filter-type]").Change(nameof(TestDataDataType.XmlPayload));
        Rows(page).Should().HaveCount(3);
    }

    // ── Provenance ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryAsset_ShowsItsSourceAndProvenance_WithSnapshotLinks_AndNoValue()
    {
        var page = Page(Populated());
        foreach (var row in Rows(page).Values)
        {
            row.QuerySelector("[data-testid=td-asset-source]")!.TextContent.Should().NotBeNullOrWhiteSpace();
            row.NextElementSibling!.QuerySelector("[data-testid=td-provenance-source]")!.TextContent.Should().NotBeNullOrWhiteSpace();
        }
        var studio = Rows(page)["valid-report"].NextElementSibling!;
        studio.QuerySelectorAll("[data-testid=td-provenance-fact]").Select(f => f.TextContent).Should().Contain(["report.xsd", "11111111-2222-3333-4444-555555555555", "abcdef012345"]);
        studio.QuerySelector("[data-testid=td-open-snapshot]")!.GetAttribute("href").Should().Be("source-analysis?snapshot=11111111-2222-3333-4444-555555555555");
        studio.QuerySelector("[data-testid=td-open-schema-studio]")!.GetAttribute("href").Should().Be("schema-studio");
        Rows(page)["no-record"].NextElementSibling!.QuerySelectorAll("[data-testid=td-open-snapshot]").Should().BeEmpty("no snapshot, no link");
        page.FindAll("[data-testid=td-detail-row]").Should().OnlyContain(r => r.HasAttribute("hidden"), "details start collapsed");
    }

    [Fact]
    public void AssetDetail_ExpandsAndCollapses_WithAnAccessibleToggle()
    {
        var page = Page(Populated());
        var toggle = Rows(page)["valid-report"].QuerySelector("[data-testid=td-toggle]")!;
        toggle.GetAttribute("aria-label").Should().Be("Show details for valid-report");
        toggle.Click();
        var row = Rows(page)["valid-report"];
        row.QuerySelector("[data-testid=td-toggle]")!.GetAttribute("aria-expanded").Should().Be("true");
        page.Find($"#{row.QuerySelector("[data-testid=td-toggle]")!.GetAttribute("aria-controls")}").HasAttribute("hidden").Should().BeFalse();
        page.Find("[data-testid=td-preview-note]").TextContent.Should().Contain("never shown");
        page.FindAll("[data-testid=td-preview]").Should().BeEmpty();
    }
}
