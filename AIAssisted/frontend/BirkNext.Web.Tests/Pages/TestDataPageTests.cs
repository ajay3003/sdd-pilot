using BirkNext.TestData;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>The general Test Data page: typed assets of every kind as safe summaries, no sensitive value in the DOM, empty/loading/error states.</summary>
public sealed class TestDataPageTests : BunitContext
{
    /// <summary>Opens one Test Data section (the page shows Overview, assets, XML fixtures or requests & configuration — one at a time).</summary>
    private void Show(string section) => Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"test-data?section={section}");
    private const string RawIdentifier = "01821512345";
    private readonly FakeApi _api = new();

    public TestDataPageTests() => Services.AddSingleton<ITestDataApiService>(_api);

    private sealed class FakeApi : ITestDataApiService
    {
        public TaskCompletionSource<(TestDataCatalogView?, string?)> Pending { get; set; } = new();
        public TestDataCatalogView Catalog { get; set; } = Mixed();
        public bool Hold { get; set; }
        public List<string> DetailRequests { get; } = [];
        public List<TestDataReviewFlag> Flags { get; } = [];
        public Task<IReadOnlyList<TestDataReviewFlag>> ReviewFlagsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TestDataReviewFlag>>(Flags);
        public Task<(TestDataCatalogView? Catalog, string? Error)> CatalogAsync(CancellationToken ct = default) =>
            Hold ? Pending.Task : Task.FromResult<(TestDataCatalogView?, string?)>((Catalog, null));
        public Task<(TestDataAssetDetail? Detail, string? Error)> AssetAsync(string id, CancellationToken ct = default)
        {
            DetailRequests.Add(id);
            var asset = Catalog.Assets.Single(item => item.Id == id);
            return Task.FromResult<(TestDataAssetDetail?, string?)>((asset.Sensitivity == TestDataSensitivity.Sensitive
                ? new TestDataAssetDetail(asset, null, "Sensitive: the value stays in the backend and is never shown.")
                : new TestDataAssetDetail(asset, "<report><period>2026-09</period></report>", "Full value (not sensitive)."), null));
        }
    }

    private static TestDataCatalogView Mixed() => new(
    [
        new() { Id = "configured:period-lock-day", Alias = "period-lock-day", Kind = TestDataKind.Scalar, ValueType = TestDataValueType.Integer, Approval = TestDataApproval.Approved,
            Validation = TestDataValidation.Validated, Sensitivity = TestDataSensitivity.Internal, Source = "Configured catalog", ValueAvailable = true, DisplayValue = "10" },
        new() { Id = "configured:normal-report", Alias = "normal-report", Kind = TestDataKind.Structured, MediaType = "application/xml", Approval = TestDataApproval.Approved,
            Validation = TestDataValidation.Validated, Sensitivity = TestDataSensitivity.Internal, Source = "Configured catalog", ValueAvailable = true, Capabilities = ["report", "valid-report"],
            Fingerprint = new string('a', 64), Environment = "TT02" },
        new() { Id = "configured:expected-record", Alias = "expected-record", Kind = TestDataKind.ExpectedResult, MediaType = "application/json", Approval = TestDataApproval.Approved,
            Validation = TestDataValidation.Validated, Sensitivity = TestDataSensitivity.Internal, Source = "Configured catalog", ValueAvailable = true },
        new() { Id = "altinn-local:reporter:reporter-a", Alias = "reporter-a", Kind = TestDataKind.EntityReference, EntityType = "person", Approval = TestDataApproval.Approved,
            Validation = TestDataValidation.Validated, Sensitivity = TestDataSensitivity.Sensitive, Source = "Local approved configuration", ValueAvailable = true, Environment = "TT02",
            Capabilities = ["person", "reporter", "synthetic"] },
    ],
    [new() { Id = "configured:set:normal", Alias = "normal-case", Approval = TestDataApproval.Approved, Bindings = [new("Reporter", "reporter-a"), new("Payload", "normal-report")], Source = "Configured catalog" }],
    [new("configured", "Configured catalog", "Declared in configuration.", 3, 1), new("altinn-local", "Local approved configuration", "Local Altinn testing configuration.", 1, 0)]);

    [Fact]
    public void MixedAssets_ShowKindTypeAndStatus_AndASafeScalarValue_ButNeverASensitiveValue()
    {
        Show("assets");
        var page = Render<TestDataPage>();
        var rows = page.FindAll("[data-testid=td-asset]").ToDictionary(row => row.GetAttribute("data-alias")!);
        rows.Keys.Should().BeEquivalentTo("period-lock-day", "normal-report", "expected-record", "reporter-a");
        // Value and type are detail facts: each asset's detail row follows its summary row.
        var details = rows.ToDictionary(row => row.Key, row => row.Value.NextElementSibling!);
        details["period-lock-day"].QuerySelector("[data-testid=td-value]")!.TextContent.Should().Be("10");
        details["period-lock-day"].QuerySelector("[data-testid=td-type]")!.TextContent.Should().Be("Simple values / parameters · Integer");
        details["normal-report"].QuerySelector("[data-testid=td-type]")!.TextContent.Should().Be("XML payload · application/xml");
        rows["expected-record"].QuerySelector("[data-testid=td-data-type]")!.TextContent.Should().Be("Expected result / reference data");
        rows["reporter-a"].QuerySelector("[data-testid=td-data-type]")!.TextContent.Should().Be("Simple values / parameters");
        rows["reporter-a"].QuerySelector("[data-testid=td-status]")!.TextContent.Should().Be("Approved");
        rows["normal-report"].QuerySelector("[data-testid=td-scope]")!.TextContent.Should().Be("TT02");
        rows["expected-record"].QuerySelector("[data-testid=td-scope]")!.TextContent.Should().Be("Shared");
        details["reporter-a"].QuerySelector("[data-testid=td-value]")!.TextContent.Should().Be("Available securely");
        page.Markup.Should().NotContain(RawIdentifier);
        Show("requests");
        page.FindAll("[data-testid=td-set]").Should().ContainSingle().Which.TextContent.Should().Contain("Reporter").And.Contain("reporter-a");
        page.FindAll("[data-testid=td-source]").Should().HaveCount(2);
    }

    [Fact]
    public void Details_ShowAPreviewForNonSensitiveData_AndNoneForSensitiveData()
    {
        Show("assets");
        var page = Render<TestDataPage>();
        page.FindAll("[data-testid=td-asset]").Single(row => row.GetAttribute("data-alias") == "normal-report").QuerySelector("[data-testid=td-toggle]")!.Click();
        page.Find("[data-testid=td-preview]").TextContent.Should().Contain("<report>");
        page.Find("[data-testid=td-toggle][aria-expanded=true]").Should().NotBeNull();

        page.FindAll("[data-testid=td-asset]").Single(row => row.GetAttribute("data-alias") == "reporter-a").QuerySelector("[data-testid=td-toggle]")!.Click();
        page.FindAll("[data-testid=td-preview]").Should().BeEmpty();
        page.Find("[data-testid=td-preview-note]").TextContent.Should().Contain("never shown");
        page.FindAll("[data-testid=td-asset]").Single(row => row.GetAttribute("data-alias") == "reporter-a").NextElementSibling!
            .QuerySelector("[data-testid=td-sensitivity]")!.TextContent.Should().Be("Sensitive");
    }

    [Fact]
    public void Filters_NarrowByDataType()
    {
        Show("assets");
        var page = Render<TestDataPage>();
        page.Find("[data-testid=td-filter-type]").Change(nameof(TestDataDataType.ExpectedResult));
        page.FindAll("[data-testid=td-asset]").Select(row => row.GetAttribute("data-alias")).Should().Equal("expected-record");
    }

    [Fact]
    public void EmptyLoadingAndErrorStates_AreExplicit()
    {
        _api.Catalog = new([], [], [new("configured", "Configured catalog", "Declared in configuration.", 0, 0)]);
        Show("assets");
        var empty = Render<TestDataPage>();
        empty.Find("[data-testid=td-empty]").TextContent.Should().Contain("No reusable Test Data assets yet");
        empty.Find("[data-testid=td-safety]").TextContent.Should().Contain("never generates");

        _api.Hold = true;
        var loading = Render<TestDataPage>();
        loading.Find("[data-testid=td-loading]").Should().NotBeNull();
        loading.Find("[data-testid=td-page]").GetAttribute("aria-busy").Should().Be("true");
        _api.Pending.SetResult((null, "Test Data: the backend could not be reached."));
        loading.WaitForAssertion(() => loading.Find("[data-testid=td-error]").TextContent.Should().Contain("could not be reached"));
    }

    [Fact]
    public void TestData_HasOneNavigationEntry_AndDocumentation()
    {
        NavigationCatalog.Sections.SelectMany(section => section.Items).Count(item => item.Route == "test-data").Should().Be(1);
        DocumentationCatalog.Features.Should().Contain(feature => feature.Route == "/test-data");
    }

    [Fact]
    public void An_incomplete_fixture_after_a_successful_import_stays_visible_with_retry_that_reloads_the_catalog()
    {
        var repository = new WorkspaceArtifactRepository();
        Services.AddSingleton(repository);
        Services.AddSingleton(new BirkNext.Web.Services.ProjectImport.ProjectImportActivation(repository));
        Services.AddSingleton(Moq.Mock.Of<BirkNext.Web.Services.ProjectImport.IProjectImportApiService>());
        Services.AddSingleton(Moq.Mock.Of<IWorkspaceAutoSaveService>());
        Services.AddSingleton(Moq.Mock.Of<IFrontendAnalysisSettingsService>());
        var contexts = new Moq.Mock<IFrontendAnalysisContextFactory>();
        contexts.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new BirkNext.Web.Models.FrontendAnalysisContext());
        Services.AddSingleton(contexts.Object);
        Services.AddSingleton(Moq.Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(Moq.Mock.Of<ICurrentWorkspaceProjection>());
        Services.AddScoped<ProjectApplicabilityState>();
        Services.AddScoped(_ => new BirkNext.Web.Services.Operations.BackgroundOperationService());
        Services.AddScoped<BirkNext.Web.Services.ProjectImport.ProjectImportSession>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var session = Services.GetRequiredService<BirkNext.Web.Services.ProjectImport.ProjectImportSession>();
        session.AutoDraftSnapshot = Guid.NewGuid();
        session.AutoDrafts = [XmlFixtureAutoDraft.Failure("The Test Data catalog could not be read (502)."),
            new XmlFixtureAutoDraftOutcome("Report intake", "report", false, null, "Already generated.") { Kind = XmlFixtureAutoDraftKind.AlreadyCurrent }];
        Show("fixtures");
        var page = Render<TestDataPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=td-auto-draft]"));
        page.FindAll("[data-testid=td-auto-draft-reason]").Should().ContainSingle("an already generated fixture is not a problem")
            .Which.TextContent.Should().Contain("Failed").And.Contain("502");
        page.Find("[data-testid=td-auto-draft-retry]").Click();
        // No test pack client in this host: the retry yields no outcome, so the notice goes.
        page.WaitForAssertion(() => page.FindAll("[data-testid=td-auto-draft]").Should().BeEmpty());
        session.AutoDrafts.Should().BeEmpty();
    }

    // MT-6: the ReviewRequired / PotentiallyStale state set by Source Change Impact was rendered nowhere.
    [Fact]
    public void Assets_a_source_change_marked_for_review_are_listed_on_the_overview_without_changing_their_approval()
    {
        _api.Flags.Add(new TestDataReviewFlag("normal-report", "ReviewRequired", "schema skolenaervaer.xsd changed", Guid.NewGuid(), DateTimeOffset.Parse("2026-11-12T10:00:00Z"), "Skolenærvær"));
        _api.Flags.Add(new TestDataReviewFlag("expected-record", "PotentiallyStale", "entity Report changed", Guid.NewGuid(), DateTimeOffset.Parse("2026-11-12T10:00:00Z"), null));
        Show("overview");
        var page = Render<TestDataPage>();
        var flags = page.Find("[data-testid=td-review-flags]");
        flags.TextContent.Should().Contain("Review required after a source change (2)").And.Contain("approval stays as it is");
        page.FindAll("[data-testid=td-review-flag]").Select(f => f.GetAttribute("data-alias")).Should().Equal("normal-report", "expected-record");
        flags.TextContent.Should().Contain("Review required — schema skolenaervaer.xsd changed").And.Contain("Potentially stale — entity Report changed");
        page.Find("[data-testid=td-review-flag] a").GetAttribute("href").Should().Be("test-data?asset=normal-report");
    }

    [Fact]
    public void Without_review_flags_the_overview_shows_no_review_card()
    {
        Show("overview");
        Render<TestDataPage>().FindAll("[data-testid=td-review-flags]").Should().BeEmpty();
    }
}
