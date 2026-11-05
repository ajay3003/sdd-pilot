using BirkNext.Integrations;
using BirkNext.Snapshots;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The shared snapshot browser: metadata only; statuses labelled by dimension (Analysis, Storage, Immutability, Retention, Project); a Source
/// Analysis snapshot's stages are the areas it contains; KPIs and filters come from the registry (true counts); filters and the open snapshot
/// live in the URL; references and lineage on demand; pointers change only by an explicit action.
/// </summary>
public sealed class SnapshotBrowserTests : BunitContext
{
    private static readonly Guid Terraform = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private readonly Mock<ISnapshotRegistryApiService> _api = new();

    private static SnapshotMetadata Meta(SnapshotType type, string id, string label, SnapshotProcessingStatus status = SnapshotProcessingStatus.Completed,
        SnapshotRetentionClass retention = SnapshotRetentionClass.Working, string? revision = null, DateTimeOffset? effective = null, string? project = null) =>
        new(type, id, project, "source-analysis", label, DateTimeOffset.Parse("2026-11-03T09:00:00Z"), effective, revision, "analyzer 2", $"store:{id}", "sha", status, retention, true, 2);

    private readonly SnapshotMetadata _terraform = Meta(SnapshotType.SourceAnalysis, Terraform.ToString(), "Terraform-iac-meldingsutveksler.zip", SnapshotProcessingStatus.Partial);
    private readonly SnapshotMetadata _report = Meta(SnapshotType.GeneratedReport, "r-1", "Week 44 report", retention: SnapshotRetentionClass.FrozenReportEvidence, revision: "abc123",
        effective: DateTimeOffset.Parse("2026-10-31T00:00:00Z"), project: "M2LB");

    public SnapshotBrowserTests()
    {
        _api.Setup(a => a.QueryAsync(It.IsAny<SnapshotQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync((SnapshotQuery q, CancellationToken _) =>
        {
            var items = new List<SnapshotListItem> { new(_terraform, [SnapshotPointerKind.LatestCreated], 0), new(_report, [], 2) }
                .Where(i => q.Status is null || i.Metadata.Status == q.Status).Where(i => q.Search is null || i.Metadata.Label.Contains(q.Search, StringComparison.OrdinalIgnoreCase)).ToList();
            return (new SnapshotListPage([.. items.Take(q.Take)], items.Count, q.Skip, q.Take), (string?)null);
        });
        _api.Setup(a => a.LineageAsync(SnapshotType.SourceAnalysis, Terraform.ToString(), It.IsAny<CancellationToken>())).ReturnsAsync((new SnapshotLineageView(_terraform,
            [new(_terraform.Reference, SnapshotLineageKind.ImportedFrom, new(SnapshotType.SourceImport, "import-1", "Terraform-iac-meldingsutveksler.zip"), "Source Analysis")],
            [new(new(SnapshotType.Interpretation, "i-1", "Interpretation"), SnapshotLineageKind.InterpretedUsing, _terraform.Reference, "Canonical interpretation")], []), (string?)null));
        _api.Setup(a => a.SourceAnalysisReferencesAsync(Terraform, It.IsAny<CancellationToken>())).ReturnsAsync((new SnapshotReferenceReport(_terraform.Reference, true,
            [new(new(SnapshotType.Review, "run-1", "API Quality Review run"), SnapshotReferenceRole.Current, "API Quality Review", DateTimeOffset.Parse("2026-11-04T10:00:00Z"))],
            ["Browser-held Project Evidence"]), (string?)null));
        _api.Setup(a => a.SetPointerAsync(It.IsAny<SnapshotPointerRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var journeys = new Mock<IIntegrationJourneysApiService>();
        journeys.Setup(j => j.SourceSetupAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ActiveEventApiResult<SourceSetupView>(new SourceSetupView
        {
            Snapshots = [new SourceSetupSnapshot(Terraform, "Terraform-iac-meldingsutveksler.zip", "sha", DateTimeOffset.UtcNow, "Failed")
            {
                ImportId = "import-1",
                Areas = [new("Application code", "NotDetected"), new("Architecture", "Unsupported"), new("Infrastructure as Code", "Complete"), new("Configuration", "Complete"),
                    new("CI/CD", "Partial"), new("Contracts", "Partial")],
            }],
        }, null));
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(journeys.Object);
    }

    private string Location => Services.GetRequiredService<NavigationManager>().Uri;

    [Fact]
    public void Rows_label_statuses_by_dimension_and_show_honest_unknowns_projects_and_references()
    {
        var cut = Render<SnapshotBrowser>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sb-row]").Should().HaveCount(2));
        var terraform = cut.FindAll("[data-testid=sb-row]")[0];
        terraform.QuerySelector(".ssb")!.GetAttribute("data-status").Should().Be(nameof(SemanticStatus.Partial));
        terraform.TextContent.Should().Contain("Analysis: Partial", "a dimension-labelled status, never a bare Failed").And.Contain("Latest");
        terraform.QuerySelector("[data-testid=sb-project-cell]")!.TextContent.Should().Be("Unassigned", "imported, but no Project Evidence associates it — never guessed from its name");
        terraform.QuerySelector("[data-testid=sb-references]")!.TextContent.Should().Be("None recorded · not all stores checked",
            "zero recorded references is not 'nothing uses it' while payload and browser stores are unchecked");
        terraform.QuerySelector("[data-testid=sb-references]")!.GetAttribute("data-state").Should().Be("partially-assessed");
        var report = cut.FindAll("[data-testid=sb-row]")[1];
        report.QuerySelector("[data-testid=sb-project-cell]")!.TextContent.Should().Be("M2LB");
        report.QuerySelector("[data-testid=sb-references]")!.TextContent.Should().Be("2 saved references");
        cut.FindAll("[data-testid=sb-table] thead th").Select(t => t.TextContent.Trim()).Should().Equal("Snapshot / source", "Type", "Project", "Created", "Analysis", "References", "Actions");
    }

    [Fact]
    public void KPIs_are_registry_counts_and_filter_by_status_and_filters_are_chips_in_the_url()
    {
        var cut = Render<SnapshotBrowser>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sb-kpi]").Should().HaveCount(6));
        cut.Find("[data-testid=sb-kpi][data-kpi=attention]").TextContent.Should().Contain("0").And.Contain("No source snapshot has a failed area");
        cut.Find("[data-testid=sb-kpi][data-kpi=partial]").TextContent.Should().Contain("Some areas partial or not supported", "every KPI states its definition");
        cut.Find("[data-testid=sb-kpi][data-kpi=total]").TextContent.Should().Contain("2");
        cut.Find("[data-testid=sb-kpi][data-kpi=partial]").TextContent.Should().Contain("1").And.Contain("analysis partial");

        cut.Find("[data-testid=sb-kpi][data-kpi=partial]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sb-row]").Should().ContainSingle());
        cut.Find("[data-testid=sb-chip]").TextContent.Should().Contain("Analysis: Partial");
        Location.Should().Contain("status=Partial");

        cut.Find("[data-testid=sb-search]").Change("week");
        cut.WaitForAssertion(() => cut.Find("[data-testid=sb-empty]"));
        Location.Should().Contain("q=week");
        cut.Find("[data-testid=sb-clear]").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sb-row]").Should().HaveCount(2));
        cut.FindAll("[data-testid=sb-chip]").Should().BeEmpty();
        _api.Verify(a => a.QueryAsync(It.Is<SnapshotQuery>(q => q.Search == "week" && q.Status == SnapshotProcessingStatus.Partial), It.IsAny<CancellationToken>()), Times.AtLeastOnce,
            "search and status are applied by the registry before paging (true counts)");
    }

    [Fact]
    public void The_detail_shows_dimensions_area_stages_references_lineage_and_diagnostics_and_viewing_never_moves_a_pointer()
    {
        var cut = Render<SnapshotBrowser>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sb-open]").Should().NotBeEmpty());
        cut.FindAll("[data-testid=sb-open]")[0].Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sb-detail]"));
        cut.Find("[data-testid=sb-dimensions]").TextContent.Should().Contain("Analysis: Partial").And.Contain("Storage: available").And.Contain("Immutable")
            .And.Contain("Retention: Working").And.Contain("Project: Unassigned");
        cut.Find("[data-testid=sb-set-approved]").HasAttribute("disabled").Should().BeTrue("only a completed analysis can be approved");
        Location.Should().Contain($"open=SourceAnalysis%3A{Terraform}");

        cut.Find("[data-testid=sb-tab][data-tab=processing]").Click();
        cut.FindAll("[data-testid=sb-processing-stage]").Select(s => s.TextContent).Should().Contain(["Infrastructure as Code: completed", "Application code: not present in this package",
            "Architecture: not supported by the analyzer (a BirkNext limitation)"]);
        cut.Find("[data-testid=sb-processing-summary]").TextContent.Should().Contain("Completed: Infrastructure as Code, Configuration").And.Contain("Not complete: Architecture, CI/CD, Contracts");

        cut.Find("[data-testid=sb-tab][data-tab=references]").Click();
        cut.Find("[data-testid=sb-reference-list]").TextContent.Should().Contain("API Quality Review run").And.Contain("current");
        cut.Find("[data-testid=sb-not-checked]").TextContent.Should().Contain("Browser-held Project Evidence");

        cut.Find("[data-testid=sb-tab][data-tab=processing]").Click();
        cut.Find("[data-testid=sb-processing-next]").TextContent.Should().Contain("usable now");
        cut.Find("[data-testid=sb-tab][data-tab=lineage]").Click();
        cut.FindAll("[data-testid=sb-chain] li").Select(l => l.TextContent).Should().Equal("Imported: Terraform-iac-meldingsutveksler.zip", "This snapshot · Source Analysis",
            "Used by 1 recorded result");
        cut.Find("[data-testid=sb-upstream]").TextContent.Should().Contain("imported from");
        cut.Find("[data-testid=sb-downstream]").TextContent.Should().Contain("interpreted from");

        cut.Find("[data-testid=sb-tab][data-tab=evidence]").Click();
        cut.Find("[data-testid=sb-open-source-analysis]").GetAttribute("href").Should().Be($"source-analysis?snapshot={Terraform}");
        cut.Find("[data-testid=sb-tab][data-tab=diagnostics]").Click();
        cut.Find("[data-testid=sb-diagnostics]").TextContent.Should().Contain(Terraform.ToString()).And.Contain("Stored by its owner");

        _api.Verify(a => a.SetPointerAsync(It.IsAny<SnapshotPointerRequest>(), It.IsAny<CancellationToken>()), Times.Never, "viewing never moves a pointer");
        cut.Find("[data-testid=sb-tab][data-tab=overview]").Click();
        cut.Find("[data-testid=sb-set-current]").Click();
        cut.Find("[data-testid=sb-confirm]").TextContent.Should().Contain("the snapshot itself and earlier results are unchanged");
        _api.Verify(a => a.SetPointerAsync(It.IsAny<SnapshotPointerRequest>(), It.IsAny<CancellationToken>()), Times.Never, "the first click only explains the effect");
        cut.Find("[data-testid=sb-confirm-cancel]").Click();
        cut.FindAll("[data-testid=sb-confirm]").Should().BeEmpty();
        cut.Find("[data-testid=sb-set-current]").Click();
        cut.Find("[data-testid=sb-confirm-apply]").Click();
        cut.WaitForAssertion(() => _api.Verify(a => a.SetPointerAsync(It.Is<SnapshotPointerRequest>(r => r.Kind == SnapshotPointerKind.CurrentSelected && r.SnapshotId == Terraform.ToString()), It.IsAny<CancellationToken>()), Times.Once));
        cut.WaitForAssertion(() => cut.Find("[data-testid=sb-notice]").TextContent.Should().Contain("Make current"));
    }

    [Fact]
    public void Returning_with_the_url_restores_filters_and_the_open_snapshot_and_tab()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"snapshots?status=Partial&open=SourceAnalysis:{Terraform}&tab=processing");
        var cut = Render<SnapshotBrowser>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=sb-detail]").GetAttribute("data-id").Should().Be(Terraform.ToString()));
        cut.Find("[data-testid=sb-tabpanel]").GetAttribute("data-tab").Should().Be("processing");
        cut.Find("[data-testid=sb-chip]").TextContent.Should().Contain("Analysis: Partial");
        cut.FindAll("[data-testid=sb-row]").Should().ContainSingle();
    }

    [Fact]
    public void A_backend_error_is_shown_with_retry()
    {
        _api.Setup(a => a.QueryAsync(It.IsAny<SnapshotQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync(((SnapshotListPage?)null, "Snapshots request failed (500)."));
        var cut = Render<SnapshotBrowser>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sb-error]").TextContent.Should().Contain("500"));
        cut.FindAll("[data-testid=sb-kpis]").Should().BeEmpty("no count is shown that the registry did not answer");
    }

    [Fact]
    public void A_slower_lineage_for_an_earlier_selection_never_replaces_the_newer_one()
    {
        var slow = new TaskCompletionSource<(SnapshotLineageView?, string?)>();
        _api.Setup(a => a.LineageAsync(SnapshotType.SourceAnalysis, Terraform.ToString(), It.IsAny<CancellationToken>())).Returns(slow.Task);
        _api.Setup(a => a.LineageAsync(SnapshotType.GeneratedReport, "r-1", It.IsAny<CancellationToken>())).ReturnsAsync((new SnapshotLineageView(_report, [], [], []), (string?)null));
        var cut = Render<SnapshotBrowser>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sb-open]").Should().HaveCount(2));

        cut.FindAll("[data-testid=sb-open]")[0].Click();
        cut.Find("[data-testid=sb-detail-loading]");
        cut.FindAll("[data-testid=sb-open]")[1].Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sb-detail]").GetAttribute("data-id").Should().Be("r-1"));

        slow.SetResult((new SnapshotLineageView(_terraform, [], [], []), null));
        cut.WaitForAssertion(() => cut.Find("[data-testid=sb-detail]").GetAttribute("data-id").Should().Be("r-1", "the stale response is ignored"));
        cut.FindAll("[data-testid=sb-detail-loading]").Should().BeEmpty();
    }

    [Fact]
    public void Tabs_move_by_keyboard()
    {
        var cut = Render<SnapshotBrowser>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=sb-open]").Should().NotBeEmpty());
        cut.FindAll("[data-testid=sb-open]")[0].Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=sb-detail]"));
        cut.FindAll("[data-testid=sb-tab]").Select(t => t.TextContent).Should().Equal("Overview", "Analysis coverage", "Evidence", "References & consumers", "Lineage & revisions", "Technical diagnostics");
        cut.Find("[data-testid=sb-tab][data-tab=overview]").KeyDown("ArrowLeft");
        cut.Find("[data-testid=sb-tabpanel]").GetAttribute("data-tab").Should().Be("diagnostics");
        cut.Find("[data-testid=sb-tab][data-tab=diagnostics]").GetAttribute("tabindex").Should().Be("0");
        cut.Find("[data-testid=sb-diagnostics]").TextContent.Should().Contain("re-analysis creates a new snapshot");
    }
}
