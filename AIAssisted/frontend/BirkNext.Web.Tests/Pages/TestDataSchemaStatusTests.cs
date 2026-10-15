using BirkNext.TestData;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>The Test Data page shows schema validity next to approval without merging them, and never renders XML for sensitive assets.</summary>
public sealed class TestDataSchemaStatusTests : BunitContext
{
    private static readonly Guid Snapshot = Guid.Parse("7d5e2c58-2d0c-4a6c-9c76-0d3c2f1b5a11");

    private sealed class FakeApi(TestDataCatalogView catalog) : ITestDataApiService
    {
        public Task<(TestDataCatalogView? Catalog, string? Error)> CatalogAsync(CancellationToken ct = default) => Task.FromResult<(TestDataCatalogView?, string?)>((catalog, null));
        public Task<(TestDataAssetDetail? Detail, string? Error)> AssetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<(TestDataAssetDetail?, string?)>((new TestDataAssetDetail(catalog.Assets.Single(a => a.Id == id), null, "Sensitive: the value stays in the backend and is never shown."), null));
    }

    [Fact]
    public void ApprovedNegativeFixture_ShowsApprovedAndSchemaInvalid_WithIssueButNoXml()
    {
        var asset = new TestDataAssetSummary
        {
            Id = "configured:missing-required-field", Alias = "missing-required-field", Kind = TestDataKind.Structured, MediaType = "application/xml",
            Approval = TestDataApproval.Approved, Validation = TestDataValidation.Invalid, Sensitivity = TestDataSensitivity.Sensitive, Source = "Configured catalog",
            ValueAvailable = true, Capabilities = ["negative-test"],
            Schema = new("skolenaervaer.xsd", Snapshot, Label: "Skolenærvær"),
            SchemaCheck = new(TestDataSchemaStatus.Invalid, "Skolenærvær", "Invalid against the schema.",
                ["The element 'Periode' has incomplete content. List of possible elements expected: 'StartDato'."], Snapshot, new string('b', 64), "2.0.0"),
        };
        Services.AddSingleton<ITestDataApiService>(new FakeApi(new([asset], [], [new("configured", "Configured catalog", "", 1, 0)])));
        var page = Render<TestDataPage>();

        var row = page.Find("[data-testid=td-asset]");
        row.TextContent.Should().Contain("Approved");
        page.Find("[data-testid=td-schema]").TextContent.Should().Be("Skolenærvær 2.0.0");
        page.Find("[data-testid=td-schema-status]").TextContent.Should().Be("Invalid");
        row.QuerySelector("[data-testid=td-toggle]")!.Click();
        page.Find("[data-testid=td-schema-detail]").TextContent.Should().Contain("StartDato");
        page.Markup.Should().Contain("Approval is independent");
        page.FindAll("[data-testid=td-preview]").Should().BeEmpty();
    }

    [Theory]
    [InlineData(TestDataSchemaStatus.SchemaUnavailable, "Schema unavailable")]
    [InlineData(TestDataSchemaStatus.DependencyUnresolved, "Dependency unresolved")]
    [InlineData(TestDataSchemaStatus.Validated, "Valid")]
    public void SchemaStates_AreExplicit(TestDataSchemaStatus status, string label)
    {
        var asset = new TestDataAssetSummary
        {
            Id = "configured:report", Alias = "report", Kind = TestDataKind.Structured, MediaType = "application/xml", Approval = TestDataApproval.Approved,
            Validation = status == TestDataSchemaStatus.Validated ? TestDataValidation.Validated : TestDataValidation.NeedsValidation, Sensitivity = TestDataSensitivity.Internal,
            Source = "Configured catalog", ValueAvailable = true, Schema = new("report.xsd"), SchemaCheck = new(status, "report.xsd", "detail", []),
        };
        Services.AddSingleton<ITestDataApiService>(new FakeApi(new([asset], [], [])));
        Render<TestDataPage>().Find("[data-testid=td-schema-status]").TextContent.Should().Be(label);
    }
}
