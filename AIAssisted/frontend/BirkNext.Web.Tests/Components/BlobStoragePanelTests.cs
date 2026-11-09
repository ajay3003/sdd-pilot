using BirkNext.BlobStorage;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Storage tab: a synthetic store is labelled as not Azure, the only ready store and container open automatically, folders navigate by prefix,
/// a blob shows redacted metadata and a bounded preview, an expectation check lists its levels and a confirmed correlation links to the run,
/// deleting an observation needs a second click, and nothing offers a write. Synthetic fixtures.
/// </summary>
public sealed class BlobStoragePanelTests : BunitContext
{
    private static readonly Guid RunId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly Mock<IBlobStorageApiService> _api = new();

    public BlobStoragePanelTests()
    {
        _api.Setup(a => a.StoresAsync("env-a", It.IsAny<CancellationToken>())).ReturnsAsync(([
            new BlobStoreView("synthetic", "env-a", "Synthetic store", BlobStoreKind.SyntheticLocal, "synthetic://synthetic", BlobStoreReadiness.Ready, "SYNTHETIC LOCAL STORE — not Azure.", ["out"]),
            new BlobStoreView("azure", "env-a", "Azure account", BlobStoreKind.AzureStorageAccount, "https://x.blob.core.windows.net", BlobStoreReadiness.Disabled, "No Azure identity is configured for BirkNext.", [])], null));
        _api.Setup(a => a.ContainersAsync("env-a", "synthetic", It.IsAny<CancellationToken>())).ReturnsAsync(([new BlobContainerView("out", null)], null));
        _api.Setup(a => a.ListAsync("env-a", "synthetic", "out", null, null, It.IsAny<CancellationToken>())).ReturnsAsync((Listing(null, [new() { Name = "2026/", IsPrefix = true }, new() { Name = "readme.txt", Size = 12 }]), null));
        _api.Setup(a => a.ListAsync("env-a", "synthetic", "out", "2026/", null, It.IsAny<CancellationToken>())).ReturnsAsync((Listing("2026/", [new() { Name = "2026/report.json", Size = 90, ContentType = "application/json" }]), null));
        _api.Setup(a => a.PropertiesAsync("env-a", "synthetic", "out", "2026/report.json", It.IsAny<CancellationToken>())).ReturnsAsync((new BlobProperties
        {
            Container = "out", Name = "2026/report.json", Size = 90, ContentType = "application/json", Metadata = new() { ["token"] = "[redacted]", ["owner"] = "test" },
        }, null));
        _api.Setup(a => a.PreviewAsync("env-a", "synthetic", "out", "2026/report.json", It.IsAny<CancellationToken>())).ReturnsAsync((new BlobPreview
        {
            Kind = BlobPreviewKind.Json, Text = "{\n  \"schoolId\": \"s-1\"\n}", BytesRead = 90, Complete = true, SyntaxValid = true, Columns = ["schoolId"], RedactedValues = 1,
        }, null));
        _api.Setup(a => a.ObservationsAsync("env-a", It.IsAny<CancellationToken>())).ReturnsAsync(([new BlobObservation { StoreId = "synthetic", StoreKind = BlobStoreKind.SyntheticLocal, Container = "out", BlobName = "2026/report.json",
            Sha256OfBytesRead = new string('a', 64), WholeBlobRead = true, ObservedBy = "tester", ObservedAt = DateTimeOffset.UtcNow }], null));
        Services.AddSingleton(_api.Object);
    }

    private static BlobListing Listing(string? prefix, List<BlobItemView> items) => new("out", prefix, items, null, DateTimeOffset.UtcNow, "One page of a synthetic listing");

    private IRenderedComponent<BlobStoragePanel> Render() => Render<BlobStoragePanel>(p => p.Add(x => x.EnvironmentId, "env-a"));

    [Fact]
    public void The_synthetic_store_is_labelled_not_Azure_and_the_only_ready_store_opens_its_container()
    {
        var cut = Render();
        var stores = cut.FindAll("[data-testid=bs-store]");
        stores[0].TextContent.Should().Contain("Synthetic local — not Azure");
        stores[1].HasAttribute("disabled").Should().BeTrue();
        stores[1].TextContent.Should().Contain("No Azure identity");
        cut.FindAll("[data-testid=bs-blob]").Should().HaveCount(2);
        cut.Find("[data-testid=bs-folder]").Click();
        cut.Find("[data-testid=bs-crumbs]").TextContent.Should().Contain("2026");
        cut.Find("[data-testid=bs-blob-open]").TextContent.Trim().Should().Be("report.json");
    }

    [Fact]
    public void A_blob_shows_redacted_metadata_and_a_bounded_preview()
    {
        var cut = Render();
        cut.Find("[data-testid=bs-folder]").Click();
        cut.Find("[data-testid=bs-blob-open]").Click();
        cut.Find("[data-testid=bs-metadata]").TextContent.Should().Contain("[redacted]").And.Contain("owner");
        cut.Find("[data-testid=bs-preview]").GetAttribute("data-kind").Should().Be("Json");
        cut.Find("[data-testid=bs-syntax]").TextContent.Should().Be("valid");
        cut.Find("[data-testid=bs-text]").TextContent.Should().Contain("schoolId");
        cut.Find("[data-testid=bs-check-path]").GetAttribute("value").Should().Be("2026/report.json");
    }

    [Fact]
    public void A_check_lists_each_level_and_a_confirmed_correlation_links_to_the_run()
    {
        _api.Setup(a => a.ValidateAsync(It.Is<BlobValidateRequest>(r => r.Expectation.RunId == RunId && r.Expectation.RequiredFields.SequenceEqual(new[] { "schoolId", "absences" }) && !r.Save), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new BlobValidation
            {
                StoreId = "synthetic", Container = "out", BlobName = "2026/report.json", Correlation = BlobCorrelationState.Confirmed, CorrelatedRunId = RunId, CorrelationDetail = "Metadata carries call 1's execution id.",
                Checks = [new(BlobCheckLevel.Exists, BlobCheckOutcome.Pass, "found"), new(BlobCheckLevel.Content, BlobCheckOutcome.Fail, "missing field(s): absences")],
            }, null));
        var cut = Render();
        cut.Find("[data-testid=bs-check-path]").Change("2026/report.json");
        cut.Find("[data-testid=bs-check-fields]").Change("schoolId, absences");
        cut.Find("[data-testid=bs-check-run]").Change(RunId.ToString());
        cut.Find("[data-testid=bs-check-go]").Click();
        cut.FindAll("[data-testid=bs-check-row]").Select(r => r.GetAttribute("data-outcome")).Should().Equal("Pass", "Fail");
        cut.Find("[data-testid=bs-correlation-link]").GetAttribute("href").Should().Be($"execution-results?run={RunId:D}");
    }

    [Fact]
    public void An_invalid_run_id_is_refused_before_any_call()
    {
        var cut = Render();
        cut.Find("[data-testid=bs-check-path]").Change("2026/report.json");
        cut.Find("[data-testid=bs-check-run]").Change("not-an-id");
        cut.Find("[data-testid=bs-check-go]").Click();
        cut.Find("[data-testid=bs-check-error]").TextContent.Should().Contain("not a valid id");
        _api.Verify(a => a.ValidateAsync(It.IsAny<BlobValidateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Deleting_an_observation_needs_a_confirming_click_and_no_write_action_is_offered()
    {
        _api.Setup(a => a.DeleteObservationAsync("env-a", It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var cut = Render();
        cut.Find("[data-testid=bs-observation-delete]").Click();
        _api.Verify(a => a.DeleteObservationAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        cut.Find("[data-testid=bs-observation-delete]").TextContent.Should().Contain("the blob is not touched");
        cut.Find("[data-testid=bs-observation-delete]").Click();
        _api.Verify(a => a.DeleteObservationAsync("env-a", It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        cut.Markup.Should().NotContainAny("Upload", "Overwrite", "Delete blob", "Generate SAS", "Change tier");
    }

    [Fact]
    public void Without_a_configured_store_the_panel_says_how_one_is_added()
    {
        _api.Setup(a => a.StoresAsync("env-a", It.IsAny<CancellationToken>())).ReturnsAsync(([], null));
        var cut = Render();
        cut.Find("[data-testid=bs-none]").TextContent.Should().Contain("BlobStorage:Stores");
    }
}
