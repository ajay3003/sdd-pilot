using BirkNext.Snapshots;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>The shared snapshot browser: metadata only, status through the shared vocabulary, unknown revision/effective date shown as such,
/// lineage on demand, and pointers changed only by an explicit action.</summary>
public sealed class SnapshotBrowserTests : BunitContext
{
    private static SnapshotMetadata Meta(SnapshotType type, string id, string label, SnapshotProcessingStatus status = SnapshotProcessingStatus.Completed,
        SnapshotRetentionClass retention = SnapshotRetentionClass.Working, string? revision = null, DateTimeOffset? effective = null) =>
        new(type, id, null, "source-analysis", label, DateTimeOffset.Parse("2026-11-03T09:00:00Z"), effective, revision, "analyzer 2", $"store:{id}", "sha", status, retention, true, 1);

    [Fact]
    public void Lists_metadata_with_shared_status_and_honest_unknowns_and_opens_lineage()
    {
        var sa = Meta(SnapshotType.SourceAnalysis, "sa-1", "M2LB.zip", SnapshotProcessingStatus.Failed);
        var report = Meta(SnapshotType.GeneratedReport, "r-1", "Week 44 report", retention: SnapshotRetentionClass.FrozenReportEvidence, revision: "abc123", effective: DateTimeOffset.Parse("2026-10-31T00:00:00Z"));
        var api = new Mock<ISnapshotRegistryApiService>();
        api.Setup(a => a.ListAsync(null, null, null, null, null, 0, 25, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new SnapshotListPage([new(sa, [SnapshotPointerKind.LatestCreated], 2), new(report, [], 0)], 2, 0, 25), (string?)null));
        api.Setup(a => a.LineageAsync(SnapshotType.SourceAnalysis, "sa-1", It.IsAny<CancellationToken>())).ReturnsAsync((new SnapshotLineageView(sa,
            [new(sa.Reference, SnapshotLineageKind.ImportedFrom, new(SnapshotType.SourceImport, "import-1", "M2LB.zip"), "Source Analysis")],
            [new(new(SnapshotType.Interpretation, "i-1", "Interpretation"), SnapshotLineageKind.InterpretedUsing, sa.Reference, "Canonical interpretation")], []), (string?)null));
        api.Setup(a => a.SetPointerAsync(It.IsAny<SnapshotPointerRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        Services.AddSingleton(api.Object);

        var cut = Render<SnapshotBrowser>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sb-row]").Should().HaveCount(2));
        var first = cut.FindAll("[data-testid=sb-row]")[0];
        first.QuerySelector(".ssb")!.GetAttribute("data-status").Should().Be(nameof(SemanticStatus.Failed));
        first.TextContent.Should().Contain("Unknown").And.Contain("Not recorded", "no revision or effective date is invented");
        first.TextContent.Should().Contain("Latest");
        cut.FindAll("[data-testid=sb-row]")[1].TextContent.Should().Contain("Frozen report evidence").And.Contain("abc123");

        first.QuerySelector("[data-testid=sb-open]")!.Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sb-upstream]").TextContent.Should().Contain("imported from"));
        cut.Find("[data-testid=sb-downstream]").TextContent.Should().Contain("interpreted from");
        api.Verify(a => a.SetPointerAsync(It.IsAny<SnapshotPointerRequest>(), It.IsAny<CancellationToken>()), Times.Never, "viewing never moves a pointer");
        cut.Find("[data-testid=sb-set-approved]").HasAttribute("disabled").Should().BeTrue("a failed snapshot cannot be approved as current");

        cut.Find("[data-testid=sb-set-current]").Click();
        api.Verify(a => a.SetPointerAsync(It.Is<SnapshotPointerRequest>(r => r.Kind == SnapshotPointerKind.CurrentSelected && r.SnapshotId == "sa-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void A_backend_error_is_shown_with_retry()
    {
        var api = new Mock<ISnapshotRegistryApiService>();
        api.Setup(a => a.ListAsync(It.IsAny<string?>(), It.IsAny<SnapshotType?>(), It.IsAny<string?>(), It.IsAny<SnapshotProcessingStatus?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((SnapshotListPage?)null, "Snapshots request failed (500)."));
        Services.AddSingleton(api.Object);
        var cut = Render<SnapshotBrowser>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sb-error]").TextContent.Should().Contain("500"));
    }
}
