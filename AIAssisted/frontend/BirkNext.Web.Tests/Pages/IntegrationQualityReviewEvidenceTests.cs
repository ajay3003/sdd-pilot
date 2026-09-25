using System.Reflection;
using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Runtime evidence sources pre-run and post-run, review window, per-check freshness, session hand-off and export.</summary>
public sealed class IntegrationQualityReviewEvidenceTests : BunitContext
{
    private static readonly DateTimeOffset Captured = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly List<IntegrationEvidenceAdapterStatus> Adapters =
    [
        new() { Adapter = "Event Hub metadata", Source = IntegrationEvidenceSource.AzureMetadata, State = IntegrationEvidenceState.NotAuthorized, Reason = "The BirkNext identity is not authorized to read Event Hub metadata.", CapturedAt = Captured },
        new() { Adapter = "Consumer groups", Source = IntegrationEvidenceSource.AzureResourceManager, State = IntegrationEvidenceState.NotConfigured, Reason = "No subscription id is configured for this platform.", CapturedAt = Captured },
        new() { Adapter = "Checkpoint store", Source = IntegrationEvidenceSource.CheckpointStore, State = IntegrationEvidenceState.Available, Reason = "Read 4 checkpoint blobs.", CapturedAt = Captured },
    ];

    private readonly FakeIntegrationCatalogApi _api = new() { Catalog = M2lbFixture.Catalog() };
    private readonly RuntimeReviewSessionService _session = new();

    public IntegrationQualityReviewEvidenceTests()
    {
        _api.Readiness = M2lbFixture.Readiness() with { EvidenceAdapters = Adapters };
        _api.Result = M2lbFixture.Result() with { EvidenceAdapters = Adapters, ReviewWindowHours = 24 };
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext
        {
            ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "M2LB DEV", EnvironmentType = FrontendEnvironmentType.Development, TargetUrl = "https://m2lbdev.bufetat.no/" },
        });
        Services.AddSingleton<IIntegrationCatalogApiService>(_api);
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<IWorkspaceSessionService>());
        Services.AddSingleton<IReportExportService, ReportExportService>();
        Services.AddSingleton(_session);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<IntegrationQualityReview> Run()
    {
        var cut = Render<IntegrationQualityReview>();
        cut.Find("[data-testid=iqr-run]").Click();
        cut.WaitForElement("[data-testid=iqr-result]");
        return cut;
    }

    [Fact]
    public void PreRunListsEachEvidenceSourceWithItsOwnState()
    {
        var cut = Render<IntegrationQualityReview>();
        cut.Find("[data-testid=iqr-evidence-sources] h2").TextContent.Should().Contain("1 of 3 available");
        var sources = cut.FindAll("[data-testid=iqr-evidence-source]");
        sources.Select(s => s.GetAttribute("data-state")).Should().Equal("NotAuthorized", "NotConfigured", "Available");
        sources[0].TextContent.Should().Contain("Not authorized").And.Contain("not authorized to read");
        sources[1].TextContent.Should().Contain("Not configured");
    }

    [Fact]
    public void ResultStatesTheReviewWindowAndTheSourcesItUsed()
    {
        var cut = Run();
        cut.Find("[data-testid=iqr-result-window]").TextContent.Should().StartWith("Last 24 h before");
        cut.FindAll("[data-testid=iqr-result-adapter]").Should().HaveCount(3);
        cut.Find("[data-testid=iqr-result-adapters]").TextContent.Should().Contain("1 of 3 available");
    }

    [Fact]
    public void AResultWithoutAWindowSaysSoInsteadOfInventingOne()
    {
        _api.Result = _api.Result! with { ReviewWindowHours = null };
        var cut = Run();
        cut.Find("[data-testid=iqr-result-window]").TextContent.Should().StartWith("Not recorded");
    }

    [Fact]
    public void DomainTablesHaveAFreshnessColumnThatNeverGuessesForConfiguration()
    {
        var cut = Run();
        cut.Find("[data-testid=iqr-tab-configuration]").Click();
        cut.Find("[data-testid=iqr-checks-table] thead").TextContent.Should().Contain("Freshness");
        cut.FindAll("[data-testid=iqr-check-freshness]").Should().OnlyContain(c => c.TextContent == "—");
    }

    [Fact]
    public void CheckFreshnessUsesTheSourceTimestampWhenThereIsOne()
    {
        var check = new IntegrationCheck
        {
            CheckId = "rel-checkpoint-freshness", Provenance = IntegrationEvidenceSource.CheckpointStore, Status = IntegrationCheckStatus.Observed,
            CapturedAt = Captured, SourceTimestamp = Captured.AddMinutes(-20), Freshness = IntegrationEvidenceItemFreshness.Current,
        };
        IntegrationReviewResultPresentation.Freshness(check).Should().Be("Current (last hour) · 2026-09-25 11:40 UTC");
        IntegrationReviewResultPresentation.Freshness(check with { SourceTimestamp = null, Status = IntegrationCheckStatus.NotAssessed }).Should().Be("—");
    }

    [Fact]
    public void ACompletedRunIsHandedToTheSessionForTheDashboard()
    {
        Run();
        _session.IntegrationQualityReview.Status.Should().Be(RuntimeReviewStatus.Completed);
        _session.IntegrationQualityReview.Report!.EvidenceAdapters.Should().HaveCount(3);
    }

    [Fact]
    public void TheExportCarriesWindowSourcesContractsAndFreshness()
    {
        var result = _api.Result! with
        {
            ContractSnapshot = [new IntegrationContractArtifact { IntegrationId = "dev:eventhub:birk-cdc:dbo.Person", Role = IntegrationContractRole.Producer, FileName = "person.schema.json", ContentHash = new string('a', 64), FieldCount = 4 }],
        };
        var html = new ReportExportService().ExportIntegrationReview(result, "BirkNext");
        html.Should().Contain("Review window").And.Contain("Last 24 h before");
        html.Should().Contain("Runtime evidence sources").And.Contain("Not authorized").And.Contain("Checkpoint store");
        html.Should().Contain("Contract snapshot").And.Contain("person.schema.json").And.Contain("aaaaaaaaaaaa");
        html.Should().Contain("<th>Freshness</th>");
        foreach (var forbidden in new[] { "SharedAccessSignature", "sig=", "AccountKey=", "Endpoint=sb://" })
            html.Should().NotContain(forbidden);
    }

    [Fact]
    public void TheLegacyIntegrationQualityServiceIsGone()
    {
        var web = typeof(IntegrationQualityReview).Assembly;
        web.GetTypes().Select(t => t.Name).Should().NotContain(["IntegrationQualityReviewService", "IIntegrationQualityReviewService", "IntegrationQualityReport", "ContractStatePresenter"]);
        typeof(IReportExportService).GetMethods().Select(m => m.Name).Should().NotContain("ExportIntegrationQualityReview");
        typeof(IntegrationQualityReview).GetCustomAttributes<Microsoft.AspNetCore.Components.RouteAttribute>().Select(r => r.Template).Should().Equal("/integration-quality-review");
    }
}
