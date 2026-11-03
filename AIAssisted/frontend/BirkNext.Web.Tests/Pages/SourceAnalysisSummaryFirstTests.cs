using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Source Analysis lists summaries and fetches the selected snapshot by id: the list never carries evidence, another snapshot is fetched only
/// when it is opened, and a snapshot whose evidence is loading or could not be fetched is never shown as "no snapshot" or "no analysis".
/// </summary>
public sealed class SourceAnalysisSummaryFirstTests : BunitContext
{
    private readonly Mock<IIntegrationCatalogApiService> _api = new();
    private readonly List<IqrSourceSnapshot> _snapshots;

    public SourceAnalysisSummaryFirstTests()
    {
        _snapshots =
        [
            Snapshot("shop-api.zip", "2026-10-02T10:00:00Z"),
            Snapshot("shop-api.zip", "2026-09-01T10:00:00Z"),
            Snapshot("infra.zip", "2026-08-01T10:00:00Z"),
        ];
        _api.SetupSourceSnapshots(() => _snapshots);
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev" } });
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(context.Object);
        Services.AddSingleton(Mock.Of<ITechnologyCoverageApiService>());
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(CurrentWorkspaceSnapshot.None()).Object);
        Services.AddSingleton(Mock.Of<IFrontendAnalysisSettingsService>());
        Services.AddScoped<ProjectApplicabilityState>();
        Services.AddScoped<SourceAnalysisSnapshotState>();
        Services.AddSingleton<IReportExportService, ReportExportService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static IqrSourceSnapshot Snapshot(string file, string at)
    {
        var id = Guid.NewGuid();
        return new IqrSourceSnapshot
        {
            Id = id, IntegrationId = "source-analysis", Archive = new SourceArchive(file, new string('a', 64), 12), AnalyzedAt = DateTimeOffset.Parse(at), Status = SourceAnalysisStatus.Ready,
            Architecture = new ArchitectureSnapshot { SourceSnapshotId = id, Status = ArchitectureStatus.Complete, SourceFingerprint = "fp", Components = [new ArchitectureComponent { Id = "c1", Name = "Shop" }] },
        };
    }

    private int Fetches(Guid id) => _api.Invocations.Count(i => i.Method.Name == nameof(IIntegrationCatalogApiService.GetSourceSnapshotAsync) && (Guid)i.Arguments[0] == id);

    [Fact]
    public void The_list_is_summaries_and_only_the_selected_snapshot_is_fetched()
    {
        var cut = Render<SourceAnalysis>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-overview-summary]"));
        cut.FindAll("[data-testid=source-snapshot] option").Should().HaveCount(4);
        Fetches(_snapshots[0].Id).Should().Be(1);
        Fetches(_snapshots[1].Id).Should().Be(0, "an earlier snapshot is fetched only when a view compares with it");
        Fetches(_snapshots[2].Id).Should().Be(0);
        _api.Verify(a => a.ListSourceSnapshotSummariesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Opening_another_snapshot_fetches_only_that_snapshot()
    {
        var cut = Render<SourceAnalysis>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-overview-summary]"));

        cut.Find("[data-testid=source-snapshot]").Change(_snapshots[2].Id.ToString());

        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-current]").GetAttribute("data-snapshot").Should().Be(_snapshots[2].Id.ToString()));
        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-overview-summary]"));
        Fetches(_snapshots[2].Id).Should().Be(1);
        Fetches(_snapshots[1].Id).Should().Be(0);
    }

    [Fact]
    public void While_the_snapshot_loads_its_header_shows_and_the_page_says_loading_never_no_snapshot()
    {
        var pending = new TaskCompletionSource<IqrSourceSnapshot?>();
        _api.Setup(a => a.GetSourceSnapshotAsync(_snapshots[0].Id, It.IsAny<CancellationToken>())).Returns(pending.Task);

        var cut = Render<SourceAnalysis>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-detail-loading]").TextContent.Should().Contain("Loading analysis"));
        cut.Find("[data-testid=sa-current-archive]").TextContent.Should().Be("shop-api.zip", "the summary names the snapshot before its evidence arrives");
        cut.FindAll("[data-testid=sa-empty]").Should().BeEmpty();
        pending.SetResult(_snapshots[0]);
        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-overview-summary]"));
    }

    [Fact]
    public void A_failed_fetch_says_could_not_load_analysis_with_retry()
    {
        _api.Setup(a => a.GetSourceSnapshotAsync(_snapshots[0].Id, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down", null, System.Net.HttpStatusCode.BadGateway));

        var cut = Render<SourceAnalysis>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-detail-error]").TextContent.Should().Contain("Could not load analysis"));
        cut.FindAll("[data-testid=sa-empty]").Should().BeEmpty("a snapshot that could not be read is not a missing snapshot");
        _api.Setup(a => a.GetSourceSnapshotAsync(_snapshots[0].Id, It.IsAny<CancellationToken>())).ReturnsAsync(_snapshots[0]);
        cut.Find("[data-testid=sa-detail-retry]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-overview-summary]"));
    }

    [Fact]
    public void A_snapshot_no_longer_stored_is_reported_as_such()
    {
        _api.Setup(a => a.GetSourceSnapshotAsync(_snapshots[0].Id, It.IsAny<CancellationToken>())).ReturnsAsync((IqrSourceSnapshot?)null);

        var cut = Render<SourceAnalysis>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-detail-error]").TextContent.Should().Contain("no longer stored"));
    }

    [Fact]
    public void Architecture_changes_fetch_the_earlier_snapshot_only_when_compared()
    {
        var cut = Render<SourceAnalysis>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-overview-summary]"));
        cut.Find("[data-testid=area-architecture]").Click();
        Fetches(_snapshots[1].Id).Should().Be(0);

        cut.WaitForAssertion(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "Changes").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid=arch-previous]"));

        cut.WaitForAssertion(() => Fetches(_snapshots[1].Id).Should().Be(1, "the newest earlier snapshot is the default comparison"));
        Fetches(_snapshots[2].Id).Should().Be(0, "other candidates are listed by summary and fetched only when chosen");
        cut.FindAll("[data-testid=arch-previous] option").Should().HaveCount(2);
    }

    [Fact]
    public void Loading_names_the_snapshot_shows_the_real_stage_and_a_superseded_load_never_overwrites_the_newer_choice()
    {
        var first = new TaskCompletionSource<IqrSourceSnapshot?>();
        CancellationToken firstToken = default;
        _api.Setup(a => a.GetSourceSnapshotAsync(_snapshots[0].Id, It.IsAny<CancellationToken>())).Returns((Guid _, CancellationToken ct) => { firstToken = ct; return first.Task; });
        _api.Setup(a => a.GetSourceSnapshotAsync(_snapshots[2].Id, It.IsAny<CancellationToken>())).ReturnsAsync(_snapshots[2]);

        var cut = Render<SourceAnalysis>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-detail-loading]").TextContent.Should().Contain("Loading analysis of shop-api.zip"));
        cut.Find("[data-testid=sa-detail-loading]").GetAttribute("data-snapshot").Should().Be(_snapshots[0].Id.ToString());
        cut.Find("[data-testid=sa-loading-stage]").GetAttribute("data-state").Should().Be("Running");
        cut.Find("[data-testid=sa-loading-stage]").TextContent.Should().Contain("Analysis of shop-api.zip");

        cut.Find("[data-testid=source-snapshot]").Change(_snapshots[2].Id.ToString());
        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-current-archive]").TextContent.Should().Be("infra.zip"));
        firstToken.IsCancellationRequested.Should().BeTrue("choosing another snapshot cancels the earlier request");
        first.SetResult(_snapshots[0]);
        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-overview-summary]"));
        cut.Find("[data-testid=sa-current]").GetAttribute("data-snapshot").Should().Be(_snapshots[2].Id.ToString(), "a late answer for the earlier snapshot never replaces the newer choice");
        cut.FindAll("[data-testid=sa-detail-loading]").Should().BeEmpty("no stuck spinner");
    }
}
