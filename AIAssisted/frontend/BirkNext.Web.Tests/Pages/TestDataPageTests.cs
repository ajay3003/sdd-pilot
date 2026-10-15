using BirkNext.TestData;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>The general Test Data page: typed assets of every kind as safe summaries, no sensitive value in the DOM, empty/loading/error states.</summary>
public sealed class TestDataPageTests : BunitContext
{
    private const string RawIdentifier = "01821512345";
    private readonly FakeApi _api = new();

    public TestDataPageTests() => Services.AddSingleton<ITestDataApiService>(_api);

    private sealed class FakeApi : ITestDataApiService
    {
        public TaskCompletionSource<(TestDataCatalogView?, string?)> Pending { get; set; } = new();
        public TestDataCatalogView Catalog { get; set; } = Mixed();
        public bool Hold { get; set; }
        public List<string> DetailRequests { get; } = [];
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
        var page = Render<TestDataPage>();
        var rows = page.FindAll("[data-testid=td-asset]").ToDictionary(row => row.GetAttribute("data-alias")!);
        rows.Keys.Should().BeEquivalentTo("period-lock-day", "normal-report", "expected-record", "reporter-a");
        rows["period-lock-day"].QuerySelector("[data-testid=td-value]")!.TextContent.Should().Be("10");
        rows["period-lock-day"].QuerySelector("[data-testid=td-type]")!.TextContent.Should().Be("Integer");
        rows["normal-report"].QuerySelector("[data-testid=td-type]")!.TextContent.Should().Be("application/xml");
        rows["expected-record"].QuerySelector("[data-testid=td-kind]")!.TextContent.Should().Be("Expected result");
        rows["reporter-a"].QuerySelector("[data-testid=td-kind]")!.TextContent.Should().Be("Entity reference");
        rows["reporter-a"].QuerySelector("[data-testid=td-value]")!.TextContent.Should().Be("Available securely");
        page.Markup.Should().NotContain(RawIdentifier);
        page.FindAll("[data-testid=td-set]").Should().ContainSingle().Which.TextContent.Should().Contain("Reporter").And.Contain("reporter-a");
        page.FindAll("[data-testid=td-source]").Should().HaveCount(2);
    }

    [Fact]
    public void Details_ShowAPreviewForNonSensitiveData_AndNoneForSensitiveData()
    {
        var page = Render<TestDataPage>();
        page.FindAll("[data-testid=td-asset]").Single(row => row.GetAttribute("data-alias") == "normal-report").QuerySelector("[data-testid=td-toggle]")!.Click();
        page.Find("[data-testid=td-preview]").TextContent.Should().Contain("<report>");
        page.Find("[data-testid=td-toggle][aria-expanded=true]").Should().NotBeNull();

        page.FindAll("[data-testid=td-asset]").Single(row => row.GetAttribute("data-alias") == "reporter-a").QuerySelector("[data-testid=td-toggle]")!.Click();
        page.FindAll("[data-testid=td-preview]").Should().BeEmpty();
        page.Find("[data-testid=td-preview-note]").TextContent.Should().Contain("never shown");
        page.Find("[data-testid=td-sensitivity]").TextContent.Should().Be("Sensitive");
    }

    [Fact]
    public void Filters_NarrowByKind()
    {
        var page = Render<TestDataPage>();
        page.Find("[data-testid=td-filter-kind]").Change(nameof(TestDataKind.EntityReference));
        page.FindAll("[data-testid=td-asset]").Select(row => row.GetAttribute("data-alias")).Should().Equal("reporter-a");
    }

    [Fact]
    public void EmptyLoadingAndErrorStates_AreExplicit()
    {
        _api.Catalog = new([], [], [new("configured", "Configured catalog", "Declared in configuration.", 0, 0)]);
        Render<TestDataPage>().Find("[data-testid=td-empty]").TextContent.Should().Contain("never generates");

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
}
